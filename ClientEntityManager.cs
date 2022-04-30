using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using K4os.Compression.LZ4;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;

namespace LiteEntitySystem
{
    public interface IInputGenerator
    {
        void GenerateInput(NetDataWriter writer);
    }

    /// <summary>
    /// Client entity manager
    /// </summary>
    public sealed class ClientEntityManager : EntityManager
    {
        private struct StatePreloadData
        {
            public ushort EntityId;
            public int EntityFieldsOffset;
            public ushort TotalSize;
            public int DataOffset;
            public int InterpolatedCachesCount;
            public InterpolatedCache[] InterpolatedCaches;
            public int RemoteCallsCount;
            public RemoteCallsCache[] RemoteCallsCaches;
        }

        private struct RemoteCallsCache
        {
            
        }

        private struct InterpolatedCache
        {
            public int Field;
            public int StateReaderOffset;
            public int InitialDataOffset;
            public InterpolatorDelegate Interpolator;
        }
        
        private struct ServerStateComparer : IComparer<ServerStateData>
        {
            public int Compare(ServerStateData x, ServerStateData y)
            {
                return SequenceDiff(x!.Tick, y!.Tick);
            }
        }
        
        private class ServerStateData
        {
            public readonly NetDataReader FinalReader = new NetDataReader();

            public ushort Tick;
            public ushort ProcessedTick;
            public bool IsBaseline;
            public StatePreloadData[] PreloadDataArray = new StatePreloadData[32];
            public int PreloadDataCount;
            public int[] InterpolatedFields = new int[8];
            public int InterpolatedCount;
            
            private readonly NetPacketReader[] _packetReaders = new NetPacketReader[MaxParts];
            private readonly NetDataWriter _finalWriter = new NetDataWriter();
            private int _totalPartsCount;
            private int _receivedPartsCount;
            private int _maxReceivedPart;
  
            public void Reset(ushort tick)
            {
                for (int i = 0; i <= _maxReceivedPart; i++)
                {
                    ref var statePart = ref _packetReaders[i];
                    statePart?.Recycle();
                    statePart = null;
                }

                IsBaseline = false;
                Tick = tick;
                InterpolatedCount = 0;
                PreloadDataCount = 0;
                _maxReceivedPart = 0;
                _receivedPartsCount = 0;
                _totalPartsCount = 0;
            }

            public void Preload(ClientEntityManager entityManager)
            {
                byte[] readerData = FinalReader.RawData;
                //preload some data
                while (FinalReader.AvailableBytes > 0)
                {
                    Utils.ResizeIfFull(ref PreloadDataArray, PreloadDataCount);
                    ref var preloadData = ref PreloadDataArray[PreloadDataCount++];
                    int initialReaderPosition = FinalReader.Position;
                    
                    ushort fullSyncAndTotalSize = FinalReader.GetUShort();
                    preloadData.TotalSize = (ushort)(fullSyncAndTotalSize >> 1);
                    preloadData.EntityId = FinalReader.GetUShort();
                    FinalReader.SetPosition(initialReaderPosition + preloadData.TotalSize);
                    if (preloadData.EntityId > MaxEntityCount)
                    {
                        //Should remove at all
                        Logger.LogError($"[CEM] Invalid entity id: {preloadData.EntityId}");
                        return;
                    }

                    if ((fullSyncAndTotalSize & 1) == 1)
                    {
                        preloadData.EntityFieldsOffset = -1;
                        preloadData.DataOffset = initialReaderPosition + StateSerializer.DiffHeaderSize;
                    }
                    else
                    {
                        //it should be here at preload
                        var entity = entityManager.EntitiesArray[preloadData.EntityId];
                        var classData = entityManager.ClassDataDict[entity.ClassId];
                        preloadData.EntityFieldsOffset = initialReaderPosition + StateSerializer.DiffHeaderSize;
                        preloadData.DataOffset = 
                            initialReaderPosition + 
                            StateSerializer.DiffHeaderSize + 
                            classData.FieldsFlagsSize;
                        preloadData.InterpolatedCachesCount = 0;

                        int stateReaderOffset = preloadData.DataOffset;
                        int initialDataOffset = 0;
                        int fieldIndex = 0;
                        
                        //preload interpolation info
                        if (!entity.IsLocalControlled && classData.InterpolatedMethods != null)
                        {
                            Utils.ResizeIfFull(ref InterpolatedFields, InterpolatedCount);
                            Utils.ResizeOrCreate(ref preloadData.InterpolatedCaches, classData.InterpolatedMethods.Length);
                            InterpolatedFields[InterpolatedCount++] = PreloadDataCount - 1;
                            
                            for (; fieldIndex < classData.InterpolatedMethods.Length; fieldIndex++)
                            {
                                if ((readerData[preloadData.EntityFieldsOffset + fieldIndex/8] & (1 << fieldIndex%8)) != 0)
                                {
                                    preloadData.InterpolatedCaches[preloadData.InterpolatedCachesCount++] = new InterpolatedCache
                                    {
                                        Field = fieldIndex,
                                        Interpolator = classData.InterpolatedMethods[fieldIndex],
                                        StateReaderOffset = stateReaderOffset,
                                        InitialDataOffset = initialDataOffset
                                    };
                                    stateReaderOffset += classData.Fields[fieldIndex].IntSize;
                                }
                                initialDataOffset += classData.Fields[fieldIndex].IntSize;
                            }
                        }
                        
                        //preload rpcs
                        for (; fieldIndex < classData.FieldsCount; fieldIndex++)
                        {
                            if ((readerData[preloadData.EntityFieldsOffset + fieldIndex / 8] & (1 << fieldIndex % 8)) != 0)
                                stateReaderOffset += classData.Fields[fieldIndex].IntSize;
                        }

                        if (stateReaderOffset < initialReaderPosition + preloadData.TotalSize)
                        {
                            Logger.Log("There is RPC!");
                        }
                    }
                }
            }

            public bool ReadPart(bool isLastPart, NetPacketReader reader)
            {
                //check processed tick
                byte partNumber = reader.GetByte();
                if (partNumber == 0)
                {
                    ProcessedTick = reader.GetUShort();
                }

                if (isLastPart)
                {
                    _totalPartsCount = partNumber + 1;
                    //Debug.Log($"TPC: {partNumber} {serverState.TotalPartsCount}");
                }
                        
                //duplicate ?
                if (_packetReaders[partNumber] != null)
                {
                    reader.Recycle();
                    return false;
                }
                        
                _packetReaders[partNumber] = reader;
                _receivedPartsCount++;
                _maxReceivedPart = Math.Max(_maxReceivedPart, partNumber);

                if (_receivedPartsCount == _totalPartsCount)
                {
                    _finalWriter.Reset();
                    for (int i = 0; i < _totalPartsCount; i++)
                    {
                        ref var statePart = ref _packetReaders[i];
                        _finalWriter.Put(statePart.RawData, statePart.Position, statePart.AvailableBytes);
                        statePart.Recycle();
                        statePart = null;
                    }
                    FinalReader.SetSource(_finalWriter);
                    return true;
                }
                return false;
            }
        }
        
        
        public override byte PlayerId => (byte)(_localPeer.RemoteId + 1);
        public int StoredCommands => _inputCommands.Count;
        public int LastProcessedTick => _lastProcessedTick;
        public int LerpBufferCount => _lerpBuffer.Count;
        public bool PredictionReset { get; private set; }
        
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
        
        private ServerStateData _stateA;
        private ServerStateData _stateB;
        private float _lerpTime;
        private double _timer;
        private bool _isSyncReceived;
        private int _lastProcessedTick;
        
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
            Utils.ResizeOrCreate(ref _interpolatePrevData[entity.Id], classData.InterpolatedFieldsSize);
        }

        protected override void OnLogicTick()
        {
            if (_inputCommands.IsFull)
            {
                _inputCommands.RemoveFromStart(1);
            }
            
            var inputWriter = _inputCommands.Add();
            inputWriter.SetPosition(2);
            inputWriter.Put(ServerTick);
            inputWriter.Put(Tick);
            
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
                unsafe
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
        }

        public override unsafe void Update()
        {
            CheckStart();

            if (!_isSyncReceived)
                return;

            base.Update();
            
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
                    if (prevDataPtr == null)
                        continue;
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

            foreach (var inputCommand in _inputCommands)
            {
                _localPeer.Send(inputCommand, DeliveryMethod.Unreliable);
            }
        }

        private unsafe void ReadEntityStates()
        {
            ServerTick = _stateA.Tick;
            var reader = _stateA.FinalReader;

            if (_stateA.IsBaseline)
            {
                while (reader.AvailableBytes > 0)
                {
                    ushort entityId = reader.GetUShort();
                    ReadEntityState(reader, entityId, true);
                }
 
                if (reader.AvailableBytes != 0)
                {
                    Logger.LogWarning($"[CEM] Something broken, available: {reader.AvailableBytes}");
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

            _lastProcessedTick = _stateA.ProcessedTick;

            //reset entities
            if (_inputCommands.Count > 0)
            {
                PredictionReset = true;
                foreach (var entity in OwnedEntities)
                {
                    var localEntity = entity;
                    _predictWriter.Reset();
                    _predictedEntities[entity.Id].MakeBaseline(Tick, _predictWriter);
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
                            byte* fieldPtr = entityPtr + entityFieldInfo.Offset;
                            byte* readDataPtr = rawData + readerPosition;
                            if (entityFieldInfo.IsEntity)
                            {
                                //put prev data into reader for SyncCalls
                                ushort id = *(ushort*) readDataPtr;
                                Unsafe.AsRef<InternalEntity>(fieldPtr) = id == InvalidEntityId ? null : EntitiesArray[id];
                            }
                            else
                            {
                                Unsafe.CopyBlock(fieldPtr, readDataPtr, entityFieldInfo.Size);
                            }
                            readerPosition += entityFieldInfo.IntSize;
                        }
                    }
                }
                PredictionReset = false;
                
                int commandsToRemove = 0;
                //reapply input
                foreach (var inputCommand in _inputCommands)
                {
                    ushort inputTick = BitConverter.ToUInt16(inputCommand.Data, 4);

                    if (SequenceDiff(_lastProcessedTick, inputTick) >= 0)
                    {
                        //remove processed inputs
                        commandsToRemove++;
                    }
                    else
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
                _inputCommands.RemoveFromStart(commandsToRemove);
            }
        }

        private struct SyncCallInfo
        {
            public OnSyncDelegate OnSync;
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

        private InternalEntity[] _entitiesToConstruct = new InternalEntity[8];
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
                        //put prev data into reader for SyncCalls
                        Unsafe.CopyBlock(readDataPtr, tempData, entityFieldInfo.Size);
                    }
                    stateSerializer?.WritePredicted(fixedDataOffset, readDataPtr, entityFieldInfo.Size);

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

        private byte[] _compressionBuffer;
        
        public void Deserialize(NetPacketReader reader)
        {
            byte packetType = reader.GetByte();
            switch (packetType)
            {
                case PacketEntityFullSync:
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
                    ReadEntityStates();
                    _isSyncReceived = true;
                    break;
                }
                
                case PacketEntitySyncLast:
                case PacketEntitySync:
                {
                    bool isLastPart = packetType == PacketEntitySyncLast;
                    ushort newServerTick = reader.GetUShort();
                    if (SequenceDiff(newServerTick, ServerTick) <= 0)
                    {
                        reader.Recycle();
                        break;
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
                                break;
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
                    break;
                }

                case PacketEntityCall:
                {
                    ushort entityInstanceId = reader.GetUShort();
                    byte packetId = reader.GetByte();
                    GetEntityById(entityInstanceId)?.ProcessPacket(packetId, reader);
                    reader.Recycle();
                    break;
                }
            }
        }
    }
}