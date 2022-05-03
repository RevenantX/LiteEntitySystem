using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using K4os.Compression.LZ4;
using LiteNetLib;
using LiteNetLib.Utils;

namespace LiteEntitySystem
{
    public interface IInputGenerator
    {
        void GenerateInput(NetDataWriter writer);
    }

    /// <summary>
    /// Client entity manager
    /// </summary>
    public sealed partial class ClientEntityManager : EntityManager
    {
        public override byte PlayerId => (byte)(_localPeer.RemoteId + 1);
        public int StoredCommands => _inputCommands.Count;
        public int LastProcessedTick => _stateA?.ProcessedTick ?? 0;
        public int LerpBufferCount => _lerpBuffer.Count;

        private const int InterpolateBufferSize = 10;
        
        private readonly NetPeer _localPeer;
        private readonly NetDataWriter _netDataWriter = new NetDataWriter();
        private readonly SortedList<ushort, ServerStateData> _receivedStates = new SortedList<ushort, ServerStateData>();
        private readonly Queue<ServerStateData> _statesPool = new Queue<ServerStateData>(MaxSavedStateDiff);
        private readonly NetDataReader _inputReader = new NetDataReader();
        private readonly LiteRingBuffer<NetDataWriter> _inputCommands = new LiteRingBuffer<NetDataWriter>(32);
        private readonly IInputGenerator _inputGenerator;
        private readonly SortedSet<ServerStateData> _lerpBuffer = new SortedSet<ServerStateData>(new ServerStateComparer());
        private readonly byte[][] _interpolatedInitialData = new byte[MaxEntityCount][];
        private readonly byte[][] _interpolatePrevData = new byte[MaxEntityCount][];
        private readonly StateSerializer[] _predictedEntities = new StateSerializer[MaxEntityCount];
        private readonly NetDataWriter _predictWriter = new NetDataWriter(false, NetConstants.MaxPacketSize*MaxParts);
        private readonly NetDataReader _predictReader = new NetDataReader();
        
        private byte[] _compressionBuffer;
        private ServerStateData _stateA;
        private ServerStateData _stateB;
        private float _lerpTime;
        private double _timer;
        private bool _isSyncReceived;
        private ushort _lastServerTick;
        private bool _inputGenerated;
        private int _executedRpcs;
        
        internal readonly EntityFilter<EntityLogic> OwnedEntities = new EntityFilter<EntityLogic>();

        public ClientEntityManager(NetPeer localPeer, byte headerByte, int framesPerSecond, IInputGenerator inputGenerator) : base(NetworkMode.Client, framesPerSecond)
        {
            _localPeer = localPeer;
            _netDataWriter.Put(headerByte);
            _inputCommands.Fill(() =>
            {
                var writer = new NetDataWriter();
                writer.Put(headerByte);
                writer.Put(PacketClientSync);
                return writer;
            });
            _inputGenerator = inputGenerator;
            OwnedEntities.OnAdded += OnOwnedAdded;
        }

        private void OnOwnedAdded(EntityLogic entity)
        {
            ref var stateSerializer = ref _predictedEntities[entity.Id];
            var classData = ClassDataDict[entity.ClassId];
            stateSerializer ??= new StateSerializer();
            stateSerializer.Init(classData, entity);
            stateSerializer.Write(1);
            Utils.ResizeOrCreate(ref _interpolatePrevData[entity.Id], classData.InterpolatedFieldsSize);
        }

        protected override unsafe void OnLogicTick()
        {
            ServerTick++;
            if (_inputCommands.IsFull)
            {
                _inputCommands.RemoveFromStart(1);
            }
            
            var inputWriter = _inputCommands.Add();
            inputWriter.SetPosition(2);
            inputWriter.Put(_lastServerTick);
            inputWriter.Put(Tick);

            if (_stateB != null)
            {
                fixed (byte* rawData = _stateB.FinalReader.RawData)
                {
                    for (int i = _executedRpcs; i < _stateB.RemoteCallsCount; i++)
                    {
                        ref var rpcCache = ref _stateB.RemoteCallsCaches[i];
                        if (SequenceDiff(rpcCache.Tick, ServerTick) <= 0)
                        {
                            var entity = EntitiesArray[rpcCache.EntityId];
                            rpcCache.Delegate(Unsafe.AsPointer(ref entity), rawData + rpcCache.Offset);
                            _executedRpcs++;
                        }
                        else
                        {
                            break;
                        }
                    }
                }        
            }

            _inputGenerated = true;
            foreach(var controller in GetControllers<HumanControllerLogic>())
            {
                int sizeBefore = inputWriter.Length;
                _inputGenerator.GenerateInput(inputWriter);
                _inputReader.SetSource(inputWriter.Data, sizeBefore, inputWriter.Length);
                controller.ReadInput(_inputReader);
                _inputReader.Clear();
            }
            
            //local update
            foreach (var entity in OwnedEntities)
            {
                //save data for interpolation before update
                var entityLocal = entity;
                var classData = ClassDataDict[entity.ClassId];
                int offset = 0;
  
                byte* entityPtr = (byte*) Unsafe.As<EntityLogic, IntPtr>(ref entityLocal);
                fixed (byte* currentDataPtr = _interpolatedInitialData[entity.Id],
                       prevDataPtr = _interpolatePrevData[entity.Id])
                {
                    //restore previous value
                    for(int i = 0; i < classData.InterpolatedMethods.Length; i++)
                    {
                        var field = classData.Fields[i];
                        Unsafe.CopyBlock(entityPtr + field.Offset, currentDataPtr + offset, field.Size);
                        offset += field.IntSize;
                    }
                    Unsafe.CopyBlock(prevDataPtr, currentDataPtr, (uint)classData.InterpolatedFieldsSize);
                                        
                    //update
                    entity.Update();
                
                    //save current
                    offset = 0;
                    for(int i = 0; i < classData.InterpolatedMethods.Length; i++)
                    {
                        var field = classData.Fields[i];
                        Unsafe.CopyBlock(currentDataPtr + offset, entityPtr + field.Offset, field.Size);
                        offset += field.IntSize;
                    }
                }
            }
        }

        public override unsafe void Update()
        {
            CheckStart();

            if (!_isSyncReceived)
                return;

            base.Update();

            if (_inputGenerated)
            {
                _inputGenerated = false;
                foreach (var inputCommand in _inputCommands)
                {
                    _localPeer.Send(inputCommand, DeliveryMethod.Unreliable);
                }
            }
            
            //local interpolation
            float localLerpT = (float)(_accumulator/DeltaTime);
            foreach (var entity in OwnedEntities)
            {
                var entityLocal = entity;
                var classData = ClassDataDict[entity.ClassId];
                int offset = 0;
                
                byte* entityPtr = (byte*) Unsafe.As<EntityLogic, IntPtr>(ref entityLocal);
                fixed (byte* currentDataPtr = _interpolatedInitialData[entity.Id],
                       prevDataPtr = _interpolatePrevData[entity.Id])
                {
                    for(int i = 0; i < classData.InterpolatedMethods.Length; i++)
                    {
                        var field = classData.Fields[i];
                        classData.InterpolatedMethods[i](
                            prevDataPtr + offset,
                            currentDataPtr + offset,
                            entityPtr + field.Offset,
                            localLerpT);
                        offset += field.IntSize;
                    }
                }
            }
            
            if (_stateB == null)
            {
                if (_lerpBuffer.Count > 1)
                {
                    _stateB = _lerpBuffer.Min;
                    _lerpBuffer.Remove(_stateB);
                    _lerpTime = SequenceDiff(_stateB.Tick, _stateA.Tick) * DeltaTime;
                    _stateB.Preload(this);
                    
                    _executedRpcs = 0;

                    int commandsToRemove = 0;
                    //remove processed inputs
                    foreach (var inputCommand in _inputCommands)
                    {
                        ushort inputTick = BitConverter.ToUInt16(inputCommand.Data, 4);
                        if (SequenceDiff(_stateB.ProcessedTick, inputTick) >= 0)
                            commandsToRemove++;
                    }
                    _inputCommands.RemoveFromStart(commandsToRemove);
                }
            }

            //remote interpolation
            if (_stateB != null)
            {
                float fTimer = (float)(_timer/_lerpTime);
                for(int i = 0; i < _stateB.InterpolatedCount; i++)
                {
                    ref var preloadData = ref _stateB.PreloadDataArray[_stateB.InterpolatedFields[i]];
                    var entity = EntitiesArray[preloadData.EntityId];
                    var fields = ClassDataDict[entity.ClassId].Fields;
                    byte* entityPtr = (byte*)Unsafe.As<InternalEntity, IntPtr>(ref entity);
                    fixed (byte* initialDataPtr = _interpolatedInitialData[entity.Id], nextDataPtr =
                               _stateB.FinalReader.RawData)
                    {
                        for (int j = 0; j < preloadData.InterpolatedCachesCount; j++)
                        {
                            var interpolatedCache = preloadData.InterpolatedCaches[j];
                            {
                                interpolatedCache.Interpolator(
                                    initialDataPtr + interpolatedCache.InitialDataOffset,
                                    nextDataPtr + interpolatedCache.StateReaderOffset,
                                    entityPtr + fields[interpolatedCache.Field].Offset,
                                    fTimer);
                            }
                        }
                    }
                }
                _timer += CurrentDelta * (0.94f + 0.02f * _lerpBuffer.Count);
                if (_timer >= _lerpTime)
                {
                    _statesPool.Enqueue(_stateA);
                    _stateA = _stateB;
                    _stateB = null;
                    //goto state b
                    ReadEntityStates();
                    _timer -= _lerpTime;
                }
            }
        }

        private unsafe void ReadEntityStates()
        {
            ServerTick = _stateA.Tick;
            var reader = _stateA.FinalReader;

            if (_stateA.IsBaseline)
            {
                _inputCommands.FastClear();
                while (reader.AvailableBytes > 0)
                {
                    ushort entityId = reader.GetUShort();
                    ReadEntityState(reader, entityId, true);
                }
            }
            else
            {
                for(int i = 0; i < _stateA.PreloadDataCount; i++)
                {
                    ref var preloadData = ref _stateA.PreloadDataArray[i];
                    reader.SetPosition(preloadData.DataOffset);
                    ReadEntityState(reader, preloadData.EntityId, preloadData.EntityFieldsOffset == -1);
                }
            }
            
            //SetEntityIds
            for (int i = 0; i < _setEntityIdsCount; i++)
            {
                ref var setIdInfo = ref _setEntityIds[i];
                byte* entityPtr = (byte*) Unsafe.As<InternalEntity, IntPtr>(ref setIdInfo.Entity);
                Unsafe.AsRef<InternalEntity>(entityPtr + setIdInfo.FieldOffset) =
                    setIdInfo.Id == InvalidEntityId ? null : EntitiesArray[setIdInfo.Id];
            }
            _setEntityIdsCount = 0;

            //Make OnSyncCalls
            for (int i = 0; i < _syncCallsCount; i++)
            {
                ref var syncCall = ref _syncCalls[i];
                fixed (byte* readerData = reader.RawData)
                {
                    if (syncCall.IsEntity)
                    {
                        ushort prevId = *(ushort*)(readerData + syncCall.PrevDataPos);
                        var prevEntity = prevId == InvalidEntityId ? null : EntitiesArray[prevId];
                        syncCall.OnSync(
                            Unsafe.AsPointer(ref syncCall.Entity),
                            prevEntity != null ? Unsafe.AsPointer(ref prevEntity) : null);
                    }
                    else
                    {
                        syncCall.OnSync(
                            Unsafe.AsPointer(ref syncCall.Entity),
                            readerData + syncCall.PrevDataPos);
                    }
                }
            }
            _syncCallsCount = 0;
            
            //Call construct methods
            for (int i = 0; i < _entitiesToConstructCount; i++)
            {
                ConstructEntity(_entitiesToConstruct[i]);
            }
            _entitiesToConstructCount = 0;

            //reset entities
            if (_inputCommands.Count > 0)
            {
                foreach (var entity in OwnedEntities)
                {
                    var localEntity = entity;
                    _predictWriter.Reset();
                    _predictedEntities[entity.Id].MakeBaseline(1, _predictWriter);
                    _predictReader.SetSource(_predictWriter.Data, 0, _predictWriter.Length);
                    
                    var classData = ClassDataDict[entity.ClassId];
                    var fixedFields = classData.Fields;
                    byte* entityPtr = (byte*) Unsafe.As<EntityLogic, IntPtr>(ref localEntity);
                    int readerPosition = StateSerializer.HeaderSize;
                    fixed (byte* rawData = _predictReader.RawData)
                    {
                        for (int i = 0; i < classData.FieldsCount; i++)
                        {
                            ref var entityFieldInfo = ref fixedFields[i];
                            if (entityFieldInfo.IsEntity)
                            {
                                ushort id = *(ushort*) (rawData + readerPosition);
                                Unsafe.AsRef<InternalEntity>(entityPtr + entityFieldInfo.Offset) = id == InvalidEntityId ? null : EntitiesArray[id];
                            }
                            else
                            {
                                Unsafe.CopyBlock(entityPtr + entityFieldInfo.Offset, rawData + readerPosition, entityFieldInfo.Size);
                            }
                            readerPosition += entityFieldInfo.IntSize;
                        }
                    }
                }
                
                //reapply input
                foreach (var inputCommand in _inputCommands)
                {
                    //reapply input data
                    _inputReader.SetSource(inputCommand.Data, 6, inputCommand.Length);
                    foreach(var entity in GetControllers<HumanControllerLogic>())
                    {
                        entity.ReadInput(_inputReader);
                        entity.ControlledEntity?.Update();
                    }
                    _inputReader.Clear();
                }
            }
        }

        private struct SyncCallInfo
        {
            public MethodCallDelegate OnSync;
            public InternalEntity Entity;
            public int PrevDataPos;
            public bool IsEntity;
        }
        private SyncCallInfo[] _syncCalls;
        private int _syncCallsCount;

        private struct SetEntityIdInfo
        {
            public InternalEntity Entity;
            public ushort Id;
            public int FieldOffset;
        }
        private SetEntityIdInfo[] _setEntityIds;
        private int _setEntityIdsCount;

        private InternalEntity[] _entitiesToConstruct = new InternalEntity[64];
        private int _entitiesToConstructCount;

        private unsafe void ReadEntityState(NetDataReader reader, ushort entityInstanceId, bool fullSync)
        {
            var entity = EntitiesArray[entityInstanceId];

            //full sync
            if (fullSync)
            {
                byte version = reader.GetByte();
                ushort classId = reader.GetUShort();

                //remove old entity
                if (entity != null && entity.Version != version)
                {
                    //this can be only on logics (not on singletons)
                    Logger.Log($"[CEM] Replace entity by new: {version}");
                    ((EntityLogic)entity).DestroyInternal();
                }

                //create new
                entity = AddEntity(new EntityParams(classId, entityInstanceId, version, this));
                Utils.ResizeIfFull(ref _entitiesToConstruct, _entitiesToConstructCount);
                _entitiesToConstruct[_entitiesToConstructCount++] = entity;
            }
            else if (entity == null)
            {
                Logger.LogError($"EntityNull? : {entityInstanceId}");
                return;
            }
            
            var classData = ClassDataDict[entity.ClassId];

            //create predicted entities
            var stateSerializer = entity.IsLocalControlled 
                ? _predictedEntities[entity.Id] 
                : null;

            //create interpolation buffers
            ref byte[] interpolatedInitialData = ref _interpolatedInitialData[entity.Id];
            Utils.ResizeOrCreate(ref interpolatedInitialData, classData.InterpolatedFieldsSize);
            Utils.ResizeOrCreate(ref _syncCalls, _syncCallsCount + classData.FieldsCount);
            Utils.ResizeOrCreate(ref _setEntityIds, _setEntityIdsCount + classData.FieldsCount);
            
            var fixedFields = classData.Fields;
            byte* entityPtr = (byte*) Unsafe.As<InternalEntity, IntPtr>(ref entity);
            int readerPosition = reader.Position;
            int fieldsFlagsOffset = readerPosition - classData.FieldsFlagsSize;
            int fixedDataOffset = 0;
            byte* tempData = stackalloc byte[MaxFieldSize];
            bool writeInterpolationData = entity.IsServerControlled || fullSync;

            fixed (byte* rawData = reader.RawData, interpDataPtr = interpolatedInitialData)
            {
                for (int i = 0; i < classData.FieldsCount; i++)
                {
                    ref var entityFieldInfo = ref fixedFields[i];
                    if (!fullSync && (rawData[fieldsFlagsOffset + i / 8] & (1 << (i % 8))) == 0)
                    {
                        fixedDataOffset += entityFieldInfo.IntSize;
                        continue;
                    }
                    byte* fieldPtr = entityPtr + entityFieldInfo.Offset;
                    byte* readDataPtr = rawData + readerPosition;

                    if (entityFieldInfo.IsEntity)
                    {
                        _setEntityIds[_setEntityIdsCount++] = new SetEntityIdInfo
                        {
                            Entity = entity,
                            FieldOffset = entityFieldInfo.Offset,
                            Id = *(ushort*)readDataPtr
                        };
                        //put prev data into reader for SyncCalls
                        stateSerializer?.WritePredicted(fixedDataOffset, readDataPtr, entityFieldInfo.Size);
                        ushort prevId = Unsafe.AsRef<InternalEntity>(fieldPtr)?.Id ?? InvalidEntityId;
                        Unsafe.CopyBlock(readDataPtr, &prevId, entityFieldInfo.Size);
                    }
                    else
                    {
                        if (i < classData.InterpolatedMethods.Length && writeInterpolationData)
                        {
                            //this is interpolated save for future
                            Unsafe.CopyBlock(interpDataPtr + fixedDataOffset, readDataPtr, entityFieldInfo.Size);
                        }
                        Unsafe.CopyBlock(tempData, fieldPtr, entityFieldInfo.Size);
                        Unsafe.CopyBlock(fieldPtr, readDataPtr, entityFieldInfo.Size);
                        stateSerializer?.WritePredicted(fixedDataOffset, readDataPtr, entityFieldInfo.Size);
                        //put prev data into reader for SyncCalls
                        Unsafe.CopyBlock(readDataPtr, tempData, entityFieldInfo.Size);
                    }

                    if(entityFieldInfo.OnSync != null)
                        _syncCalls[_syncCallsCount++] = new SyncCallInfo
                        {
                            OnSync = entityFieldInfo.OnSync,
                            Entity = entity,
                            PrevDataPos = readerPosition,
                            IsEntity = entityFieldInfo.IsEntity
                        };
                    
                    readerPosition += entityFieldInfo.IntSize;
                    fixedDataOffset += entityFieldInfo.IntSize;
                }
                if (fullSync)
                {
                    for (int i = 0; i < classData.SyncableFields.Length; i++)
                    {
                        Unsafe.AsRef<SyncableField>(entityPtr + classData.SyncableFields[i].Offset).FullSyncRead(rawData, ref readerPosition);
                    }
                }
            }
            reader.SetPosition(readerPosition);
        }

        public void Deserialize(NetPacketReader reader)
        {
            byte packetType = reader.GetByte();
            if(packetType == PacketEntityFullSync)
            {
                int decompressedSize = reader.GetInt();
                Utils.ResizeOrCreate(ref _compressionBuffer, decompressedSize);
                int decodedBytes = LZ4Codec.Decode(
                    reader.RawData,
                    reader.Position,
                    reader.AvailableBytes,
                    _compressionBuffer,
                    0,
                    decompressedSize);
                if (decodedBytes != decompressedSize)
                {
                    Logger.LogError("Error on decompress");
                }
                
                _stateA = new ServerStateData
                {
                    IsBaseline = true
                };
                _stateA.FinalReader.SetSource(_compressionBuffer, 0, decompressedSize);
                _stateA.Tick = _stateA.FinalReader.GetUShort();
                _lastServerTick = _stateA.Tick;
                ReadEntityStates();
                _isSyncReceived = true;
            }
            else
            {
                bool isLastPart = packetType == PacketEntitySyncLast;
                ushort newServerTick = reader.GetUShort();
                if (SequenceDiff(newServerTick, _stateA.Tick) <= 0)
                {
                    reader.Recycle();
                    return;
                }
                
                if(!_receivedStates.TryGetValue(newServerTick, out var serverState))
                {
                    if (_receivedStates.Count > MaxSavedStateDiff)
                    {
                        var minimal = _receivedStates.Keys[0];
                        if (SequenceDiff(newServerTick, minimal) > 0)
                        {
                            serverState = _receivedStates[minimal];
                            _receivedStates.Remove(minimal);
                            serverState.Reset(newServerTick);
                        }
                        else
                        {
                            reader.Recycle();
                            return;
                        }
                    }
                    else if (_statesPool.Count > 0)
                    {
                        serverState = _statesPool.Dequeue();
                        serverState.Reset(newServerTick);
                    }
                    else
                    {
                        serverState = new ServerStateData { Tick = newServerTick };
                    }
                    _receivedStates.Add(newServerTick, serverState);
                }
                
                //if got full state - add to lerp buffer
                if(serverState.ReadPart(isLastPart, reader))
                {
                    if (SequenceDiff(serverState.Tick, _lastServerTick) > 0)
                    {
                        _lastServerTick = serverState.Tick;
                    }
                    
                    _receivedStates.Remove(serverState.Tick);
                    
                    if (_lerpBuffer.Count >= InterpolateBufferSize)
                    {
                        if (SequenceDiff(serverState.Tick, _lerpBuffer.Min.Tick) > 0)
                        {
                            _lerpBuffer.Remove(_lerpBuffer.Min);
                            _lerpBuffer.Add(serverState);
                        }
                        else
                        {
                            _statesPool.Enqueue(serverState);
                        }
                    }
                    else
                    {
                        _lerpBuffer.Add(serverState);
                    }
                }
            }
        }
    }
}