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
        private const int InputBufferSize = 32;
        
        private readonly NetPeer _localPeer;
        private readonly SortedList<ushort, ServerStateData> _receivedStates = new SortedList<ushort, ServerStateData>();
        private readonly Queue<ServerStateData> _statesPool = new Queue<ServerStateData>(MaxSavedStateDiff);
        private readonly NetDataReader _inputReader = new NetDataReader();
        private readonly Queue<NetDataWriter> _inputCommands = new Queue<NetDataWriter>(InputBufferSize);
        private readonly IInputGenerator _inputGenerator;
        private readonly SortedSet<ServerStateData> _lerpBuffer = new SortedSet<ServerStateData>(new ServerStateComparer());
        private readonly byte[][] _interpolatedInitialData = new byte[MaxEntityCount][];
        private readonly byte[][] _interpolatePrevData = new byte[MaxEntityCount][];
        private readonly StateSerializer[] _predictedEntities = new StateSerializer[MaxEntityCount];

        private ServerStateData _stateA;
        private ServerStateData _stateB;
        private float _lerpTime;
        private double _timer;
        private bool _isSyncReceived;
        private bool _inputGenerated;

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
        private readonly byte[] _tempData = new byte[MaxFieldSize];
        
        internal readonly EntityFilter<EntityLogic> OwnedEntities = new EntityFilter<EntityLogic>();
        private readonly byte _headerByte;

        public ClientEntityManager(NetPeer localPeer, byte headerByte, int framesPerSecond, IInputGenerator inputGenerator) : base(NetworkMode.Client, framesPerSecond)
        {
            _headerByte = headerByte;
            _localPeer = localPeer;
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

            if (_stateB != null)
            {
                fixed (byte* rawData = _stateB.Data)
                {
                    for (int i = 0; i < _stateB.RemoteCallsCount; i++)
                    {
                        ref var rpcCache = ref _stateB.RemoteCallsCaches[i];
                        if (SequenceDiff(rpcCache.Tick, ServerTick) == 0)
                        {
                            var entity = EntitiesDict[rpcCache.EntityId];
                            rpcCache.Delegate(Unsafe.AsPointer(ref entity), rawData + rpcCache.Offset);
                        }
                    }
                }        
            }

            if (_inputCommands.Count > InputBufferSize)
                _inputCommands.Dequeue();
            var inputWriter = new NetDataWriter();
            inputWriter.Put(_headerByte);
            inputWriter.Put(PacketClientSync);
            inputWriter.Put(ServerTick);
            inputWriter.Put(Tick);
            _inputCommands.Enqueue(inputWriter);
            _inputGenerated = true;
            _inputGenerator.GenerateInput(inputWriter);
            _inputReader.SetSource(inputWriter.Data, 6, inputWriter.Length);
            foreach(var controller in GetControllers<HumanControllerLogic>())
            {
                controller.ReadInput(_inputReader);
            }
            
            //local update
            foreach (var entity in OwnedEntities)
            {
                //save data for interpolation before update
                var entityLocal = entity;
                var classData = ClassDataDict[entity.ClassId];
                byte* entityPtr = (byte*) Unsafe.As<EntityLogic, IntPtr>(ref entityLocal);
                fixed (byte* currentDataPtr = _interpolatedInitialData[entity.Id],
                       prevDataPtr = _interpolatePrevData[entity.Id])
                {
                    Unsafe.CopyBlock(prevDataPtr, currentDataPtr, (uint)classData.InterpolatedFieldsSize);
                                        
                    //update
                    entity.Update();
                
                    //save current
                    int offset = 0;
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
            
            //logic update
            base.Update();

            //preload next state
            if (_stateB == null && _lerpBuffer.Count > 0)
            {
                _stateB = _lerpBuffer.Min;
                _lerpBuffer.Remove(_stateB);
                _lerpTime = SequenceDiff(_stateB.Tick, _stateA.Tick) * DeltaTime * (1.04f - 0.02f * _lerpBuffer.Count);
                _stateB.Preload(this);

                //remove processed inputs
                while (_inputCommands.TryPeek(out var inputCommand))
                {
                    ushort inputTick = BitConverter.ToUInt16(inputCommand.Data, 4);
                    if (SequenceDiff(_stateB.ProcessedTick, inputTick) >= 0)
                        _inputCommands.Dequeue();
                    else
                        break;
                }
            }
            
            //remote interpolation
            if (_stateB != null)
            {
                float fTimer = (float)(_timer/_lerpTime);
                for(int i = 0; i < _stateB.InterpolatedCount; i++)
                {
                    ref var preloadData = ref _stateB.PreloadDataArray[_stateB.InterpolatedFields[i]];
                    var entity = EntitiesDict[preloadData.EntityId];
                    var fields = ClassDataDict[entity.ClassId].Fields;
                    byte* entityPtr = (byte*)Unsafe.As<InternalEntity, IntPtr>(ref entity);
                    fixed (byte* initialDataPtr = _interpolatedInitialData[entity.Id], nextDataPtr = _stateB.Data)
                    {
                        for (int j = 0; j < preloadData.InterpolatedCachesCount; j++)
                        {
                            var interpolatedCache = preloadData.InterpolatedCaches[j];
                            interpolatedCache.Interpolator(
                                initialDataPtr + interpolatedCache.InitialDataOffset,
                                nextDataPtr + interpolatedCache.StateReaderOffset,
                                entityPtr + fields[interpolatedCache.Field].Offset,
                                fTimer);
                        }
                    }
                }
                _timer += CurrentDelta;
                if (_timer >= _lerpTime)
                {
                    _statesPool.Enqueue(_stateA);
                    _stateA = _stateB;
                    _stateB = null;

                    ReadEntityStates();
                    
                    _timer -= _lerpTime;
                    
                    //reset entities
                    foreach (var entity in OwnedEntities)
                    {
                        var localEntity = entity;
                        fixed (byte* latestEntityData = _predictedEntities[entity.Id].Data)
                        {
                            var classData = ClassDataDict[entity.ClassId];
                            byte* entityPtr = (byte*) Unsafe.As<EntityLogic, IntPtr>(ref localEntity);
                            int pos = StateSerializer.HeaderSize;
                            for (int i = 0; i < classData.FieldsCount; i++)
                            {
                                ref var entityFieldInfo = ref classData.Fields[i];
                                if (!entityFieldInfo.IsEntity)
                                    Unsafe.CopyBlock(entityPtr + entityFieldInfo.Offset, latestEntityData + pos, entityFieldInfo.Size);
                                pos += entityFieldInfo.IntSize;
                            }
                        }
                    }
                    //reapply input
                    UpdateMode = UpdateMode.PredictionRollback;
                    foreach (var inputCommand in _inputCommands)
                    {
                        //reapply input data
                        _inputReader.SetSource(inputCommand.Data, 6, inputCommand.Length);
                        foreach(var controller in GetControllers<HumanControllerLogic>())
                        {
                            controller.ReadInput(_inputReader);
                        }
                        foreach (var entity in OwnedEntities)
                        {
                            entity.Update();
                        }
                    }
                    UpdateMode = UpdateMode.Normal;
                }
            }

            //local interpolation
            float localLerpT = LerpFactor;
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

            //send input
            if (_inputGenerated)
            {
                _inputGenerated = false;
                foreach (var inputCommand in _inputCommands)
                    _localPeer.Send(inputCommand, DeliveryMethod.Unreliable);
                _localPeer.NetManager.TriggerUpdate();
            }
        }

        internal override void RemoveEntity(EntityLogic e)
        {
            base.RemoveEntity(e);
            if(e.IsLocalControlled)
                OwnedEntities.Remove(e);
        }

        private unsafe void ReadEntityStates()
        {
            ServerTick = _stateA.Tick;

            fixed (byte* readerData = _stateA.Data)
            {
                if (_stateA.IsBaseline)
                {
                    _inputCommands.Clear();
                    int bytesRead = _stateA.Offset;
                    while (bytesRead < _stateA.Size)
                    {
                        ushort entityId = BitConverter.ToUInt16(_stateA.Data, bytesRead);
                        bytesRead += 2;
                        bytesRead += ReadEntityState(readerData + bytesRead, entityId, true);
                    }
                }
                else
                {

                    for (int i = 0; i < _stateA.PreloadDataCount; i++)
                    {
                        ref var preloadData = ref _stateA.PreloadDataArray[i];
                        ReadEntityState(readerData + preloadData.DataOffset, preloadData.EntityId,
                            preloadData.EntityFieldsOffset == -1);
                    }
                }
            }

            //SetEntityIds
            for (int i = 0; i < _setEntityIdsCount; i++)
            {
                ref var setIdInfo = ref _setEntityIds[i];
                byte* entityPtr = (byte*) Unsafe.As<InternalEntity, IntPtr>(ref setIdInfo.Entity);
                Unsafe.AsRef<InternalEntity>(entityPtr + setIdInfo.FieldOffset) =
                    setIdInfo.Id == InvalidEntityId ? null : EntitiesDict[setIdInfo.Id];
            }
            _setEntityIdsCount = 0;

            //Make OnSyncCalls
            for (int i = 0; i < _syncCallsCount; i++)
            {
                ref var syncCall = ref _syncCalls[i];
                fixed (byte* readerData = _stateA.Data)
                {
                    if (syncCall.IsEntity)
                    {
                        ushort prevId = *(ushort*)(readerData + syncCall.PrevDataPos);
                        var prevEntity = prevId == InvalidEntityId ? null : EntitiesDict[prevId];
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
        }

        private unsafe int ReadEntityState(byte* rawData, ushort entityInstanceId, bool fullSync)
        { 
            var entity = EntitiesDict[entityInstanceId];
            int readerPosition = 0;
            
            //full sync
            if (fullSync)
            {
                byte version = rawData[0];
                ushort classId = *(ushort*)(rawData + 1);
                readerPosition += 3;

                //remove old entity
                if (entity != null && entity.Version != version)
                {
                    //this can be only on logics (not on singletons)
                    Logger.Log($"[CEM] Replace entity by new: {version}");
                    var entityLogic = (EntityLogic) entity;
                    if(!entityLogic.IsDestroyed)
                        entityLogic.DestroyInternal();
                }

                //create new
                entity = AddEntity(new EntityParams(classId, entityInstanceId, version, this));
                Utils.ResizeIfFull(ref _entitiesToConstruct, _entitiesToConstructCount);
                _entitiesToConstruct[_entitiesToConstructCount++] = entity;
            }
            else if (entity == null)
            {
                Logger.LogError($"EntityNull? : {entityInstanceId}");
                return 0;
            }
            
            var classData = ClassDataDict[entity.ClassId];

            //create interpolation buffers
            ref byte[] interpolatedInitialData = ref _interpolatedInitialData[entity.Id];
            Utils.ResizeOrCreate(ref interpolatedInitialData, classData.InterpolatedFieldsSize);
            Utils.ResizeOrCreate(ref _syncCalls, _syncCallsCount + classData.FieldsCount);
            Utils.ResizeOrCreate(ref _setEntityIds, _setEntityIdsCount + classData.FieldsCount);
            
            byte* entityPtr = (byte*) Unsafe.As<InternalEntity, IntPtr>(ref entity);
            int fieldsFlagsOffset = readerPosition - classData.FieldsFlagsSize;
            int fixedDataOffset = 0;
            bool writeInterpolationData = entity.IsServerControlled || fullSync;
            byte[] predictedData = _predictedEntities[entity.Id]?.Data;

            fixed (byte* interpDataPtr = interpolatedInitialData, tempData = _tempData)
            {
                for (int i = 0; i < classData.FieldsCount; i++)
                {
                    ref var entityFieldInfo = ref classData.Fields[i];
                    if (!fullSync && !Utils.IsBitSet(rawData + fieldsFlagsOffset, i))
                    {
                        fixedDataOffset += entityFieldInfo.IntSize;
                        continue;
                    }
                    byte* fieldPtr = entityPtr + entityFieldInfo.Offset;
                    byte* readDataPtr = rawData + readerPosition;

                    bool hasChanges = false;
                    if (entityFieldInfo.IsEntity)
                    {
                        ushort prevId = Unsafe.AsRef<InternalEntity>(fieldPtr)?.Id ?? InvalidEntityId;
                        if (Utils.memcmp(readDataPtr, &prevId, entityFieldInfo.PtrSize) != 0)
                        {
                            _setEntityIds[_setEntityIdsCount++] = new SetEntityIdInfo
                            {
                                Entity = entity,
                                FieldOffset = entityFieldInfo.Offset,
                                Id = *(ushort*)readDataPtr
                            };
                            
                            //put prev data into reader for SyncCalls
                            Unsafe.CopyBlock(readDataPtr, &prevId, entityFieldInfo.Size);
                            hasChanges = true;
                        }
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
                        if(predictedData != null && entity.IsLocalControlled)
                            fixed(byte* latestEntityData = predictedData)
                                Unsafe.CopyBlock(latestEntityData + StateSerializer.HeaderSize + fixedDataOffset, readDataPtr, entityFieldInfo.Size);
                        //put prev data into reader for SyncCalls
                        Unsafe.CopyBlock(readDataPtr, tempData, entityFieldInfo.Size);
                        hasChanges =  Utils.memcmp(readDataPtr, fieldPtr, entityFieldInfo.PtrSize) != 0;
                    }

                    if(entityFieldInfo.OnSync != null && hasChanges)
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

            return readerPosition;
        }

        public void Deserialize(NetPacketReader reader)
        {
            byte packetType = reader.GetByte();
            if(packetType == PacketBaselineSync)
            {
                _stateA = new ServerStateData
                {
                    IsBaseline = true,
                    Size = reader.GetInt()
                };
                Utils.ResizeOrCreate(ref _stateA.Data, _stateA.Size);
                
                int decodedBytes = LZ4Codec.Decode(
                    reader.RawData,
                    reader.Position,
                    reader.AvailableBytes,
                    _stateA.Data,
                    0,
                    _stateA.Size);
                if (decodedBytes != _stateA.Size)
                {
                    Logger.LogError("Error on decompress");
                }
                _stateA.Tick = BitConverter.ToUInt16(_stateA.Data);
                _stateA.Offset = 2;
                ReadEntityStates();
                _isSyncReceived = true;
            }
            else
            {
                bool isLastPart = packetType == PacketDiffSyncLast;
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