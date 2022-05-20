using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using K4os.Compression.LZ4;
using LiteEntitySystem.Internal;
using LiteNetLib;
using LiteNetLib.Utils;

namespace LiteEntitySystem
{
    internal struct InputPacketHeader
    {
        public ushort StateA;
        public ushort StateB;
        public ushort SimulatedServerTick;
        public ushort LerpMsec;
    }

    /// <summary>
    /// Client entity manager
    /// </summary>
    public sealed class ClientEntityManager : EntityManager
    {
        /// <summary>
        /// Current interpolated server tick
        /// </summary>
        public ushort ServerTick { get; private set; }
        
        /// <summary>
        /// Stored input commands count for prediction correction
        /// </summary>
        public int StoredCommands => _inputCommands.Count;
        
        /// <summary>
        /// Player tick processed by server
        /// </summary>
        public int LastProcessedTick => _stateA?.ProcessedTick ?? 0;
        
        /// <summary>
        /// States count in interpolation buffer
        /// </summary>
        public int LerpBufferCount => _lerpBuffer.Count;

        private const int InterpolateBufferSize = 10;
        private const int InputBufferSize = 128;
        private static readonly int InputHeaderSize = Unsafe.SizeOf<InputPacketHeader>();
        
        private readonly NetPeer _localPeer;
        private readonly SortedList<ushort, ServerStateData> _receivedStates = new SortedList<ushort, ServerStateData>(new SequenceComparer());
        private readonly Queue<ServerStateData> _statesPool = new Queue<ServerStateData>(MaxSavedStateDiff);
        private readonly NetDataReader _inputReader = new NetDataReader();
        private readonly Queue<NetDataWriter> _inputCommands = new Queue<NetDataWriter>(InputBufferSize);
        private readonly Queue<NetDataWriter> _inputPool = new Queue<NetDataWriter>(InputBufferSize);
        private readonly SortedSet<ServerStateData> _lerpBuffer = new SortedSet<ServerStateData>(new ServerStateComparer());
        private readonly Queue<(ushort, EntityLogic)> _spawnPredictedEntities = new Queue<(ushort, EntityLogic)>();
        private readonly byte[][] _interpolatedInitialData = new byte[ushort.MaxValue][];
        private readonly byte[][] _interpolatePrevData = new byte[ushort.MaxValue][];
        private readonly byte[][] _predictedEntities = new byte[ushort.MaxValue][];

        private ServerStateData _stateA;
        private ServerStateData _stateB;
        private float _lerpTime;
        private double _timer;
        private bool _isSyncReceived;
        private bool _isLogicTicked;

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
        private readonly byte[] _sendBuffer = new byte[NetConstants.MaxPacketSize];
        
        private readonly EntityFilter<EntityLogic> _ownedEntities = new EntityFilter<EntityLogic>();
        private ushort _lerpMsec;
        private ushort _remoteCallsTick;
        private ushort _lastReceivedInputTick;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="typesMap">EntityTypesMap with registered entity types</param>
        /// <param name="localPeer">Local NetPeer</param>
        /// <param name="headerByte">Header byte that will be used for packets (to distinguish entity system packets)</param>
        /// <param name="framesPerSecond">Fixed framerate of game logic</param>
        public ClientEntityManager(EntityTypesMap typesMap, NetPeer localPeer, byte headerByte, byte framesPerSecond) : base(typesMap, NetworkMode.Client, framesPerSecond)
        {
            _localPeer = localPeer;
            _sendBuffer[0] = headerByte;
            _sendBuffer[1] = PacketClientSync;
            AliveEntities.OnAdded += InitInterpolation;
        }

        /// <summary>
        /// Read incoming data in case of first byte is == headerByte
        /// </summary>
        /// <param name="reader">Reader with data (will be recycled inside, also works with autorecycle)</param>
        /// <returns>true if first byte is == headerByte</returns>
        public bool DeserializeWithHeaderCheck(NetPacketReader reader)
        {
            if (reader.PeekByte() == _sendBuffer[0])
            {
                reader.SkipBytes(1);
                Deserialize(reader);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Read incoming data omitting header byte
        /// </summary>
        /// <param name="reader"></param>
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
                InternalPlayerId = reader.GetByte();
                Logger.Log($"[CEM] Got baseline sync. Assigned player id: {InternalPlayerId}");
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
                    return;
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
                if (Utils.SequenceDiff(newServerTick, _stateA.Tick) <= 0)
                {
                    reader.Recycle();
                    return;
                }
                
                if(!_receivedStates.TryGetValue(newServerTick, out var serverState))
                {
                    if (_receivedStates.Count > MaxSavedStateDiff)
                    {
                        var minimal = _receivedStates.Keys[0];
                        if (Utils.SequenceDiff(newServerTick, minimal) > 0)
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
                    if (Utils.SequenceDiff(serverState.LastReceivedTick, _lastReceivedInputTick) > 0)
                        _lastReceivedInputTick = serverState.LastReceivedTick;
                    
                    _receivedStates.Remove(serverState.Tick);
                    
                    if (_lerpBuffer.Count >= InterpolateBufferSize)
                    {
                        if (Utils.SequenceDiff(serverState.Tick, _lerpBuffer.Min.Tick) > 0)
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

        private void PreloadNextState()
        {
            if (_stateB != null || _lerpBuffer.Count == 0) 
                return;
            
            _stateB = _lerpBuffer.Min;
            _lerpBuffer.Remove(_stateB);
            _lerpTime = Utils.SequenceDiff(_stateB.Tick, _stateA.Tick) * DeltaTime * (1.04f - _lerpBuffer.Count * 0.02f);
            _stateB.Preload(EntitiesDict);

            //remove processed inputs
            while (_inputCommands.Count > 0)
            {
                if (Utils.SequenceDiff(_stateB.ProcessedTick, (ushort)(Tick - _inputCommands.Count + 1)) >= 0)
                {
                    var inputWriter = _inputCommands.Dequeue();
                    inputWriter.Reset();
                    _inputPool.Enqueue(inputWriter);
                }
                else
                {
                    break;
                }
            }
        }

        private unsafe void ProcessNextState()
        {
            if (_stateB == null)
                return;
            
            _timer += VisualDeltaTime;
            if (_timer >= _lerpTime)
            {
                _statesPool.Enqueue(_stateA);
                _stateA = _stateB;
                _stateB = null;

                ReadEntityStates();
                
                _timer -= _lerpTime;
                
                //reset owned entities
                foreach (var entity in _ownedEntities)
                {
                    var localEntity = entity;
                    fixed (byte* latestEntityData = _predictedEntities[entity.Id])
                    {
                        ref var classData = ref entity.GetClassData();
                        byte* entityPtr = InternalEntity.GetPtr(ref localEntity);
                        for (int i = 0; i < classData.FieldsCount; i++)
                        {
                            ref var field = ref classData.Fields[i];
                            if (!field.IsEntity)
                                field.SetFromFixedOffset(entityPtr, latestEntityData);
                        }
                    }
                }
                
                //reapply input
                UpdateMode = UpdateMode.PredictionRollback;
                foreach (var inputCommand in _inputCommands)
                {
                    //reapply input data
                    _inputReader.SetSource(inputCommand.Data, InputHeaderSize, inputCommand.Length);
                    foreach(var controller in GetControllers<HumanControllerLogic>())
                    {
                        controller.ReadInput(_inputReader);
                    }
                    foreach (var entity in _ownedEntities)
                    {
                        entity.Update();
                    }
                }
                UpdateMode = UpdateMode.Normal;
                
                //update interpolated position
                foreach (var entity in _ownedEntities)
                {
                    ref var classData = ref entity.GetClassData();
                    var localEntity = entity;
                    byte* entityPtr = InternalEntity.GetPtr(ref localEntity);
                    
                    for(int i = 0; i < classData.InterpolatedCount; i++)
                    {
                        fixed (byte* currentDataPtr = _interpolatedInitialData[entity.Id])
                            classData.Fields[i].GetToFixedOffset(entityPtr, currentDataPtr);
                    }
                }
                
                //delete predicted
                while (_spawnPredictedEntities.TryPeek(out var info))
                {
                    if (Utils.SequenceDiff(_stateA.ProcessedTick, info.Item1) >= 0)
                    {
                        _spawnPredictedEntities.Dequeue();
                        info.Item2.DestroyInternal();
                    }
                    else
                    {
                        break;
                    }
                }
            }
            else //remote interpolation
            {
                float fTimer = (float)(_timer/_lerpTime);
                _lerpMsec = (ushort)(fTimer * 65535f);
                for(int i = 0; i < _stateB.InterpolatedCount; i++)
                {
                    ref var preloadData = ref _stateB.PreloadDataArray[_stateB.InterpolatedFields[i]];
                    var entity = EntitiesDict[preloadData.EntityId];
                    var fields = entity.GetClassData().Fields;
                    byte* entityPtr = InternalEntity.GetPtr(ref entity);
                    fixed (byte* initialDataPtr = _interpolatedInitialData[entity.Id], nextDataPtr = _stateB.Data)
                    {
                        for (int j = 0; j < preloadData.InterpolatedCachesCount; j++)
                        {
                            var interpolatedCache = preloadData.InterpolatedCaches[j];
                            var field = fields[interpolatedCache.Field];
                            field.Interpolator(
                                initialDataPtr + field.FixedOffset,
                                nextDataPtr + interpolatedCache.StateReaderOffset,
                                entityPtr + field.FieldOffset,
                                fTimer);
                        }
                    }
                }
            }
        }

        public override unsafe void Update()
        {
            if (!_isSyncReceived)
                return;
            
            //logic update
            base.Update();
            
            PreloadNextState();
            ProcessNextState();

            //local interpolation
            float localLerpT = LerpFactor;
            foreach (var entity in AliveEntities)
            {
                if (!entity.IsLocalControlled && !entity.IsLocal)
                    continue;
                
                var entityLocal = entity;
                ref var classData = ref entity.GetClassData();
                byte* entityPtr = InternalEntity.GetPtr(ref entityLocal);
                fixed (byte* currentDataPtr = _interpolatedInitialData[entity.Id],
                       prevDataPtr = _interpolatePrevData[entity.Id])
                {
                    for(int i = 0; i < classData.InterpolatedCount; i++)
                    {
                        var field = classData.Fields[i];
                        field.Interpolator(
                            prevDataPtr + field.FixedOffset,
                            currentDataPtr + field.FixedOffset,
                            entityPtr + field.FieldOffset,
                            localLerpT);
                    }
                }
            }

            //send input
            if (_isLogicTicked)
            {
                _isLogicTicked = false;
                
                //pack tick first
                int offset = 4;
                fixed (byte* sendBuffer = _sendBuffer)
                {
                    ushort currentTick = (ushort)(Tick - _inputCommands.Count + 1);
                    ushort tickIndex = 0;
                    
                    foreach (var inputCommand in _inputCommands)
                    {
                        if (Utils.SequenceDiff(currentTick, _lastReceivedInputTick) <= 0)
                        {
                            currentTick++;
                            continue;
                        }
                        
                        fixed (byte* inputData = inputCommand.Data)
                        {
                            if (offset + inputCommand.Length + sizeof(ushort) > NetConstants.MaxUnreliableDataSize)
                            {
                                Unsafe.Write(sendBuffer + 2, currentTick);
                                _localPeer.Send(_sendBuffer, 0, offset, DeliveryMethod.Unreliable);
                                offset = 4;
                                
                                currentTick += tickIndex;
                                tickIndex = 0;
                            }
                            
                            //put size
                            Unsafe.Write(sendBuffer + offset, (ushort)(inputCommand.Length - InputHeaderSize));
                            offset += sizeof(ushort);
                            
                            //put data
                            Unsafe.CopyBlock(sendBuffer + offset, inputData, (uint)inputCommand.Length);
                            offset += inputCommand.Length;
                        }

                        tickIndex++;
                    }
                    Unsafe.Write(sendBuffer + 2, currentTick);
                    _localPeer.Send(_sendBuffer, 0, offset, DeliveryMethod.Unreliable);
                    _localPeer.NetManager.TriggerUpdate();
                }
            }
            
            //local only and UpdateOnClient
            foreach (var entity in AliveEntities)
            {
                entity.VisualUpdate();
            }
            //controllers
            foreach (var entity in GetControllers<HumanControllerLogic>())
            {
                entity.VisualUpdate();
            }
        }

        internal void AddOwned(EntityLogic entity)
        {
            _ownedEntities.Add(entity);
            if(entity.GetClassData().IsUpdateable)
                AliveEntities.Add(entity);
        }

        private unsafe void InitInterpolation(InternalEntity entity)
        {
            ref byte[] predictedData = ref _predictedEntities[entity.Id];
            ref var classData = ref ClassDataDict[entity.ClassId];
            byte* entityPtr = InternalEntity.GetPtr(ref entity);
            if(!entity.IsLocal)
                Utils.ResizeOrCreate(ref predictedData, classData.FixedFieldsSize);
            Utils.ResizeOrCreate(ref _interpolatePrevData[entity.Id], classData.InterpolatedFieldsSize);
            Utils.ResizeOrCreate(ref _interpolatedInitialData[entity.Id], classData.InterpolatedFieldsSize);
            
            fixed (byte* predictedPtr = predictedData, interpDataPtr = _interpolatedInitialData[entity.Id])
            {
                for (int i = 0; i < classData.FieldsCount; i++)
                {
                    var field = classData.Fields[i];
                    if (!entity.IsLocal && !field.IsEntity)
                        field.GetToFixedOffset(entityPtr, predictedPtr);
                    if (field.Interpolator != null)
                        field.GetToFixedOffset(entityPtr, interpDataPtr);
                }
            }
        }

        internal void RemoveOwned(EntityLogic entity)
        {
            _ownedEntities.Remove(entity);
        }

        internal void AddPredictedInfo(EntityLogic e)
        {
            _spawnPredictedEntities.Enqueue((Tick, e));
        }

        protected override unsafe void OnLogicTick()
        {
            ServerTick++;
            if (_stateB != null)
            {
                fixed (byte* rawData = _stateB.Data)
                {
                    for (int i = _stateB.RemoteCallsProcessed; i < _stateB.RemoteCallsCount; i++)
                    {
                        ref var rpcCache = ref _stateB.RemoteCallsCaches[i];
                        if (Utils.SequenceDiff(rpcCache.Tick, _remoteCallsTick) >= 0 && Utils.SequenceDiff(rpcCache.Tick, ServerTick) <= 0)
                        {
                            _remoteCallsTick = rpcCache.Tick;
                            var entity = EntitiesDict[rpcCache.EntityId];
                            if (rpcCache.FieldId == byte.MaxValue)
                            {
                                rpcCache.Delegate(Unsafe.AsPointer(ref entity), rawData + rpcCache.Offset, rpcCache.Count);
                            }
                            else
                            {
                                var fieldPtr = InternalEntity.GetPtr(ref entity) + ClassDataDict[entity.ClassId].SyncableFields[rpcCache.FieldId].FieldOffset;
                                rpcCache.Delegate(fieldPtr, rawData + rpcCache.Offset, rpcCache.Count);
                            }
                            _stateB.RemoteCallsProcessed++;
                        }
                    }
                }        
            }

            if (_inputCommands.Count > InputBufferSize)
                _inputCommands.Dequeue();
            var inputWriter = _inputPool.Count > 0 ? _inputPool.Dequeue() : new NetDataWriter(true, InputHeaderSize);
            var inputPacketHeader = new InputPacketHeader
            {
                StateA   = _stateA.Tick,
                StateB   = _stateB?.Tick ?? _stateA.Tick,
                LerpMsec = _lerpMsec,
                SimulatedServerTick = ServerTick
            };
            fixed(void* writerData = inputWriter.Data)
                Unsafe.Copy(writerData, ref inputPacketHeader);
            inputWriter.SetPosition(InputHeaderSize);
            
            //generate inputs
            foreach(var controller in GetControllers<HumanControllerLogic>())
            {
                controller.GenerateInput(inputWriter);
                if (inputWriter.Length > NetConstants.MaxUnreliableDataSize - 2)
                {
                    Logger.LogError($"Input too large: {inputWriter.Length-InputHeaderSize} bytes");
                    break;
                }
                
                _inputCommands.Enqueue(inputWriter);
                _isLogicTicked = true;
                _inputReader.SetSource(inputWriter.Data, InputHeaderSize, inputWriter.Length);
                
                controller.ReadInput(_inputReader);
            }

            //local only and UpdateOnClient
            foreach (var entity in AliveEntities)
            {
                if (entity.IsLocal || entity.IsLocalControlled)
                {
                    //save data for interpolation before update
                    ref var classData = ref ClassDataDict[entity.ClassId];
                    var entityLocal = entity;
                    byte* entityPtr = InternalEntity.GetPtr(ref entityLocal);
                    fixed (byte* currentDataPtr = _interpolatedInitialData[entity.Id],
                           prevDataPtr = _interpolatePrevData[entity.Id])
                    {
                        Unsafe.CopyBlock(prevDataPtr, currentDataPtr, (uint)classData.InterpolatedFieldsSize);
                                        
                        //update
                        entity.Update();
                
                        //save current
                        for(int i = 0; i < classData.InterpolatedCount; i++)
                        {
                            var field = classData.Fields[i];
                            field.GetToFixedOffset(entityPtr, currentDataPtr);
                        }
                    }
                }
                else
                {
                    entity.Update();
                }
            }
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
                        ReadEntityState(readerData, ref bytesRead, entityId, true);
                        if (bytesRead == -1)
                            return;
                    }
                }
                else
                {

                    for (int i = 0; i < _stateA.PreloadDataCount; i++)
                    {
                        ref var preloadData = ref _stateA.PreloadDataArray[i];
                        int offset = preloadData.DataOffset;
                        ReadEntityState(readerData, ref offset, preloadData.EntityId,
                            preloadData.EntityFieldsOffset == -1);
                        if (offset == -1)
                            return;
                    }
                }
            }

            //SetEntityIds
            for (int i = 0; i < _setEntityIdsCount; i++)
            {
                ref var setIdInfo = ref _setEntityIds[i];
                Unsafe.AsRef<InternalEntity>(InternalEntity.GetPtr(ref setIdInfo.Entity) + setIdInfo.FieldOffset) =
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
                            prevEntity != null ? Unsafe.AsPointer(ref prevEntity) : null,
                            1); //TODO: count!!!
                    }
                    else
                    {
                        syncCall.OnSync(
                            Unsafe.AsPointer(ref syncCall.Entity),
                            readerData + syncCall.PrevDataPos,
                            1); //TODO: count!!!
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

        private unsafe void ReadEntityState(byte* rawData, ref int readerPosition, ushort entityInstanceId, bool fullSync)
        {
            if (entityInstanceId >= MaxEntityCount)
            {
                Logger.LogError($"Bad data (id > MaxEntityCount) {entityInstanceId} >= {MaxEntityCount}");
                readerPosition = -1;
                return;
            }
            var entity = EntitiesDict[entityInstanceId];

            //full sync
            if (fullSync)
            {
                byte version = rawData[readerPosition];
                ushort classId = Unsafe.Read<ushort>(rawData + readerPosition + 1);
                readerPosition += 3;

                //remove old entity
                if (entity != null && entity.Version != version)
                {
                    //this can be only on logics (not on singletons)
                    Logger.Log($"[CEM] Replace entity by new: {version}");
                    var entityLogic = (EntityLogic) entity;
                    if(!entityLogic.IsDestroyed)
                        entityLogic.DestroyInternal();
                    entity = null;
                }
                if(entity == null)
                {
                    //create new
                    entity = AddEntity(new EntityParams(classId, entityInstanceId, version, this));
                    Utils.ResizeIfFull(ref _entitiesToConstruct, _entitiesToConstructCount);
                    _entitiesToConstruct[_entitiesToConstructCount++] = entity;
                    //Logger.Log($"[CEM] Add entity: {entity.GetType()}");
                }
            }
            else if (entity == null)
            {
                Logger.LogError($"EntityNull? : {entityInstanceId}");
                readerPosition = -1;
                return;
            }
            
            ref var classData = ref ClassDataDict[entity.ClassId];

            //create interpolation buffers
            ref byte[] interpolatedInitialData = ref _interpolatedInitialData[entity.Id];
            Utils.ResizeOrCreate(ref interpolatedInitialData, classData.InterpolatedFieldsSize);
            Utils.ResizeOrCreate(ref _syncCalls, _syncCallsCount + classData.FieldsCount);
            Utils.ResizeOrCreate(ref _setEntityIds, _setEntityIdsCount + classData.FieldsCount);
            
            byte* entityPtr = InternalEntity.GetPtr(ref entity);
            int fieldsFlagsOffset = readerPosition - classData.FieldsFlagsSize;
            bool writeInterpolationData = entity.IsServerControlled || fullSync;
            
            fixed (byte* interpDataPtr = interpolatedInitialData, tempData = _tempData, latestEntityData = _predictedEntities[entity.Id])
            {
                for (int i = 0; i < classData.FieldsCount; i++)
                {
                    if (!fullSync && !Utils.IsBitSet(rawData + fieldsFlagsOffset, i))
                        continue;
                    
                    ref var field = ref classData.Fields[i];
                    byte* fieldPtr = entityPtr + field.FieldOffset;
                    byte* readDataPtr = rawData + readerPosition;
                    bool hasChanges = false;
                    
                    if (field.IsEntity)
                    {
                        ushort prevId = Unsafe.AsRef<InternalEntity>(fieldPtr)?.Id ?? InvalidEntityId;
                        ushort *nextId = (ushort*)readDataPtr;
                        if (prevId != *nextId)
                        {
                            _setEntityIds[_setEntityIdsCount++] = new SetEntityIdInfo
                            {
                                Entity = entity,
                                FieldOffset = field.FieldOffset,
                                Id = *nextId
                            };
                            //put prev data into reader for SyncCalls
                            *nextId = prevId;
                            hasChanges = true;
                        }
                    }
                    else
                    {
                        if (field.Interpolator != null && writeInterpolationData)
                        {
                            //this is interpolated save for future
                            Unsafe.CopyBlock(interpDataPtr + field.FixedOffset, readDataPtr, field.Size);
                        }
                        Unsafe.CopyBlock(tempData, fieldPtr, field.Size);
                        Unsafe.CopyBlock(fieldPtr, readDataPtr, field.Size);
                        if(latestEntityData != null && entity.IsLocalControlled)
                            Unsafe.CopyBlock(latestEntityData + field.FixedOffset, readDataPtr, field.Size);
                        //put prev data into reader for SyncCalls
                        Unsafe.CopyBlock(readDataPtr, tempData, field.Size);
                        hasChanges =  Utils.memcmp(readDataPtr, fieldPtr, field.PtrSize) != 0;
                    }

                    if(field.OnSync != null && hasChanges)
                        _syncCalls[_syncCallsCount++] = new SyncCallInfo
                        {
                            OnSync = field.OnSync,
                            Entity = entity,
                            PrevDataPos = readerPosition,
                            IsEntity = field.IsEntity
                        };
                    
                    readerPosition += field.IntSize;
                }
                if (fullSync)
                {
                    for (int i = 0; i < classData.SyncableFields.Length; i++)
                    {
                        Unsafe.AsRef<SyncableField>(entityPtr + classData.SyncableFields[i].FieldOffset).FullSyncRead(rawData, ref readerPosition);
                    }
                }
            }
        }
    }
}