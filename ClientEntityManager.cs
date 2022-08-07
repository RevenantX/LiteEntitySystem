using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        public float LerpMsec;
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

        public ushort RawServerTick => _stateA != null ? _stateA.Tick : (ushort)0;

        public ushort RawTargetServerTick => _stateB != null ? _stateB.Tick : RawServerTick;
        
        /// <summary>
        /// Stored input commands count for prediction correction
        /// </summary>
        public int StoredCommands => _inputCommands.Count;
        
        /// <summary>
        /// Player tick processed by server
        /// </summary>
        public ushort LastProcessedTick => _stateA?.ProcessedTick ?? 0;

        public ushort LastReceivedTick => _stateA?.LastReceivedTick ?? 0;
        
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
        private readonly byte[] _tempData = new byte[MaxFieldSize];
        private readonly byte[] _sendBuffer = new byte[NetConstants.MaxPacketSize];

        private ServerStateData _stateA;
        private ServerStateData _stateB;
        private float _lerpTime;
        private double _timer;
        private bool _isSyncReceived;

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
        private ushort _remoteCallsTick;
        private ushort _lastReceivedInputTick;
        private float _logicLerpMsec;

        //adaptive lerp vars
        private float _adaptiveMiddlePoint = 3f;
        private readonly float[] _jitterSamples = new float[10];
        private int _jitterSampleIdx;
        private readonly Stopwatch _jitterTimer = new Stopwatch();

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
                _stateA.Tick = BitConverter.ToUInt16(_stateA.Data, 0);
                _stateA.Offset = 2;
                ReadEntityStates();
                _isSyncReceived = true;
                _jitterTimer.Restart();
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
                
                //sample jitter
                _jitterSamples[_jitterSampleIdx] = _jitterTimer.ElapsedMilliseconds / 1000f;
                _jitterSampleIdx = (_jitterSampleIdx + 1) % _jitterSamples.Length;
                //reset timer
                _jitterTimer.Reset();
                
                if(!_receivedStates.TryGetValue(newServerTick, out var serverState))
                {
                    if (_receivedStates.Count > MaxSavedStateDiff)
                    {
                        Logger.LogWarning("[CEM] Too much states received: this should be rare thing");
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
                            _timer = _lerpTime;
                            GoToNextState();
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

        private bool PreloadNextState()
        {
            if (_lerpBuffer.Count == 0)
            {
                if (_adaptiveMiddlePoint < 3f)
                    _adaptiveMiddlePoint = 3f;
                return false;
            }

            float jitterSum = 0f;
            bool adaptiveIncreased = false;
            for (int i = 0; i < _jitterSamples.Length - 1; i++)
            {
                float jitter = Math.Abs(_jitterSamples[i] - _jitterSamples[i + 1]) * FramesPerSecond;
                jitterSum += jitter;
                if (jitter > _adaptiveMiddlePoint)
                {
                    _adaptiveMiddlePoint = jitter;
                    adaptiveIncreased = true;
                }
            }

            if (!adaptiveIncreased)
            {
                jitterSum /= _jitterSamples.Length;
                _adaptiveMiddlePoint = Utils.Lerp(_adaptiveMiddlePoint, Math.Max(1f, jitterSum), 0.05f);
            }

            _stateB = _lerpBuffer.Min;
            _lerpBuffer.Remove(_stateB);
            _lerpTime = 
                Utils.SequenceDiff(_stateB.Tick, _stateA.Tick) * DeltaTimeF *
                (1f - (_lerpBuffer.Count - _adaptiveMiddlePoint) * 0.02f);
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

            return true;
        }

        private unsafe void GoToNextState()
        {
            _statesPool.Enqueue(_stateA);
            _stateA = _stateB;
            _stateB = null;

            ReadEntityStates();
            
            _timer -= _lerpTime;
            
            //reset owned entities
            foreach (var entity in AliveEntities)
            {
                if(entity.IsLocal || !entity.IsLocalControlled)
                    continue;
                
                var localEntity = entity;
                fixed (byte* latestEntityData = _predictedEntities[entity.Id])
                {
                    ref var classData = ref entity.GetClassData();
                    byte* entityPtr = Utils.GetPtr(ref localEntity);
                    for (int i = 0; i < classData.FieldsCount; i++)
                    {
                        ref var field = ref classData.Fields[i];
                        if (field.Flags.HasFlagFast(SyncFlags.OnlyForRemote))
                            continue;
                        if (field.FieldType == FieldType.Value)
                        {
                            Unsafe.CopyBlock(entityPtr + field.Offset, latestEntityData + field.FixedOffset, field.Size);
                        }
                        else if (field.FieldType == FieldType.SyncableSyncVar)
                        {
                            ref var syncableField = ref Unsafe.AsRef<SyncableField>(entityPtr + field.Offset);
                            byte* syncVarPtr = Utils.GetPtr(ref syncableField) + field.SyncableSyncVarOffset;
                            Unsafe.CopyBlock(syncVarPtr, latestEntityData + field.FixedOffset, field.Size);
                        }
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
                foreach (var entity in AliveEntities)
                {
                    if(entity.IsLocal || !entity.IsLocalControlled)
                        continue;
                    entity.Update();
                }
            }
            UpdateMode = UpdateMode.Normal;
            
            //update interpolated position
            foreach (var entity in AliveEntities)
            {
                if(entity.IsLocal || !entity.IsLocalControlled)
                    continue;
                ref var classData = ref entity.GetClassData();
                var localEntity = entity;
                byte* entityPtr = Utils.GetPtr(ref localEntity);
                
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
            
            //load next state
            double prevLerpTime = _lerpTime;
            if (PreloadNextState())
            {
                //adjust lerp timer
                _timer *= (prevLerpTime / _lerpTime);
            }
        }

        public override unsafe void Update()
        {
            if (!_isSyncReceived)
                return;
            
            //logic update
            ushort prevTick = Tick;
            base.Update();
            
            if (_stateB != null || PreloadNextState())
            {
                _timer += VisualDeltaTime;
                if (_timer >= _lerpTime)
                {
                    GoToNextState();
                }
            }

            if (_stateB != null)
            {
                //remote interpolation
                float fTimer = (float)(_timer/_lerpTime);
                for(int i = 0; i < _stateB.InterpolatedCount; i++)
                {
                    ref var preloadData = ref _stateB.PreloadDataArray[_stateB.InterpolatedFields[i]];
                    var entity = EntitiesDict[preloadData.EntityId];
                    var fields = entity.GetClassData().Fields;
                    byte* entityPtr = Utils.GetPtr(ref entity);
                    fixed (byte* initialDataPtr = _interpolatedInitialData[entity.Id], nextDataPtr = _stateB.Data)
                    {
                        for (int j = 0; j < preloadData.InterpolatedCachesCount; j++)
                        {
                            var interpolatedCache = preloadData.InterpolatedCaches[j];
                            var field = fields[interpolatedCache.Field];
                            field.Interpolator(
                                initialDataPtr + field.FixedOffset,
                                nextDataPtr + interpolatedCache.StateReaderOffset,
                                entityPtr + field.Offset,
                                fTimer);
                        }
                    }
                }
            }

            //local interpolation
            float localLerpT = LerpFactor;
            foreach (var entity in AliveEntities)
            {
                if (!entity.IsLocalControlled && !entity.IsLocal)
                    continue;
                
                var entityLocal = entity;
                ref var classData = ref entity.GetClassData();
                byte* entityPtr = Utils.GetPtr(ref entityLocal);
                fixed (byte* currentDataPtr = _interpolatedInitialData[entity.Id],
                       prevDataPtr = _interpolatePrevData[entity.Id])
                {
                    for(int i = 0; i < classData.InterpolatedCount; i++)
                    {
                        var field = classData.Fields[i];
                        field.Interpolator(
                            prevDataPtr + field.FixedOffset,
                            currentDataPtr + field.FixedOffset,
                            entityPtr + field.Offset,
                            localLerpT);
                    }
                }
            }

            //send buffered input
            if (Tick != prevTick)
            {
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
                            if (offset + inputCommand.Length + sizeof(ushort) > _localPeer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable))
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
                        if (tickIndex == MaxSavedStateDiff)
                        {
                            break;
                        }
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
        }

        internal void AddOwned(EntityLogic entity)
        {
            if(entity.GetClassData().IsUpdateable && !entity.GetClassData().UpdateOnClient)
                AliveEntities.Add(entity);
        }
        
        internal void RemoveOwned(EntityLogic entity)
        {
            AliveEntities.Remove(entity);
        }

        private unsafe void InitInterpolation(InternalEntity entity)
        {
            ref byte[] predictedData = ref _predictedEntities[entity.Id];
            ref var classData = ref ClassDataDict[entity.ClassId];
            byte* entityPtr = Utils.GetPtr(ref entity);
            if(!entity.IsLocal)
                Utils.ResizeOrCreate(ref predictedData, classData.FixedFieldsSize);

            if (classData.InterpolatedFieldsSize > 0)
            {
                Utils.ResizeOrCreate(ref _interpolatePrevData[entity.Id], classData.InterpolatedFieldsSize);
                Utils.ResizeOrCreate(ref _interpolatedInitialData[entity.Id], classData.InterpolatedFieldsSize);
            }

            fixed (byte* predictedPtr = predictedData, interpDataPtr = _interpolatedInitialData[entity.Id])
            {
                for (int i = 0; i < classData.FieldsCount; i++)
                {
                    var field = classData.Fields[i];
                    if (field.FieldType == FieldType.Entity)
                        continue;
                    if (!entity.IsLocal)
                    {
                        if (field.FieldType == FieldType.Value)
                        {
                            Unsafe.CopyBlock(predictedPtr + field.FixedOffset, entityPtr + field.Offset, field.Size);
                        }
                        else if (field.FieldType == FieldType.SyncableSyncVar)
                        {
                            ref var syncableField = ref Unsafe.AsRef<SyncableField>(entityPtr + field.Offset);
                            byte* syncVarPtr = Utils.GetPtr(ref syncableField) + field.SyncableSyncVarOffset;
                            Unsafe.CopyBlock(predictedPtr + field.FixedOffset, syncVarPtr, field.Size);
                        }
                    }

                    if (field.Interpolator != null)
                    {
                        Unsafe.CopyBlock(interpDataPtr + field.FixedOffset, entityPtr + field.Offset, field.Size);
                    }
                }
            }
        }

        internal void AddPredictedInfo(EntityLogic e)
        {
            _spawnPredictedEntities.Enqueue((Tick, e));
        }

        protected override unsafe void OnLogicTick()
        {
            if (_stateB != null)
            {
                _logicLerpMsec = (float)(_timer / _lerpTime);
                ServerTick = (ushort)(_stateA.Tick + Math.Round(Utils.SequenceDiff(_stateB.Tick, _stateA.Tick) * _logicLerpMsec));
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
                                var fieldPtr = Utils.GetPtr(ref entity) + ClassDataDict[entity.ClassId].SyncableFields[rpcCache.FieldId].Offset;
                                rpcCache.Delegate(fieldPtr, rawData + rpcCache.Offset, rpcCache.Count);
                            }
                            _stateB.RemoteCallsProcessed++;
                        }
                    }
                }        
            }

            if (_inputCommands.Count > InputBufferSize)
            {
                _inputCommands.Clear();
            }
            var inputWriter = _inputPool.Count > 0 ? _inputPool.Dequeue() : new NetDataWriter(true, InputHeaderSize);
            var inputPacketHeader = new InputPacketHeader
            {
                StateA   = _stateA.Tick,
                StateB   = _stateB?.Tick ?? _stateA.Tick,
                LerpMsec = _logicLerpMsec
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
            }
            
            //read
            _inputReader.SetSource(inputWriter.Data, InputHeaderSize, inputWriter.Length);
            foreach (var controller in GetControllers<HumanControllerLogic>())
            {
                controller.ReadInput(_inputReader);
            }
            _inputCommands.Enqueue(inputWriter);

            //local only and UpdateOnClient
            foreach (var entity in AliveEntities)
            {
                if (entity.IsLocal || entity.IsLocalControlled)
                {
                    //save data for interpolation before update
                    ref var classData = ref ClassDataDict[entity.ClassId];
                    var entityLocal = entity;
                    byte* entityPtr = Utils.GetPtr(ref entityLocal);
                    fixed (byte* currentDataPtr = _interpolatedInitialData[entity.Id],
                           prevDataPtr = _interpolatePrevData[entity.Id])
                    {
                        //restore previous
                        for(int i = 0; i < classData.InterpolatedCount; i++)
                        {
                            var field = classData.Fields[i];
                            field.SetFromFixedOffset(entityPtr, currentDataPtr);
                        }

                        //update
                        entity.Update();
                
                        //save current
                        Unsafe.CopyBlock(prevDataPtr, currentDataPtr, (uint)classData.InterpolatedFieldsSize);
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
                Unsafe.AsRef<InternalEntity>(Utils.GetPtr(ref setIdInfo.Entity) + setIdInfo.FieldOffset) =
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
            if (entityInstanceId >= MaxSyncedEntityCount)
            {
                Logger.LogError($"Bad data (id > MaxEntityCount) {entityInstanceId} >= {MaxSyncedEntityCount}");
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
            
            byte* entityPtr = Utils.GetPtr(ref entity);
            int fieldsFlagsOffset = readerPosition - classData.FieldsFlagsSize;
            bool writeInterpolationData = entity.IsServerControlled || fullSync;
            
            fixed (byte* interpDataPtr = interpolatedInitialData, tempData = _tempData, latestEntityData = _predictedEntities[entity.Id])
            {
                entity.OnSyncStart();
                for (int i = 0; i < classData.FieldsCount; i++)
                {
                    if (!fullSync && !Utils.IsBitSet(rawData + fieldsFlagsOffset, i))
                        continue;
                    
                    ref var field = ref classData.Fields[i];
                    byte* fieldPtr = entityPtr + field.Offset;
                    byte* readDataPtr = rawData + readerPosition;
                    bool hasChanges = false;
                    
                    if (field.FieldType == FieldType.Entity)
                    {
                        ushort prevId = Unsafe.AsRef<InternalEntity>(fieldPtr)?.Id ?? InvalidEntityId;
                        ushort *nextId = (ushort*)readDataPtr;
                        if (prevId != *nextId)
                        {
                            _setEntityIds[_setEntityIdsCount++] = new SetEntityIdInfo
                            {
                                Entity = entity,
                                FieldOffset = field.Offset,
                                Id = *nextId
                            };
                            //put prev data into reader for SyncCalls
                            *nextId = prevId;
                            hasChanges = true;
                        }
                    }
                    else if (field.FieldType == FieldType.SyncableSyncVar)
                    {
                        ref var syncableField = ref Unsafe.AsRef<SyncableField>(fieldPtr);
                        byte* syncVarPtr = Utils.GetPtr(ref syncableField) + field.SyncableSyncVarOffset;
                        Unsafe.CopyBlock(syncVarPtr, readDataPtr, field.Size);
                        if(latestEntityData != null && entity.IsLocalControlled)
                            Unsafe.CopyBlock(latestEntityData + field.FixedOffset, readDataPtr, field.Size);
                    }
                    else
                    {
                        if (field.Interpolator != null && writeInterpolationData)
                        {
                            //this is interpolated save for future
                            Unsafe.CopyBlock(interpDataPtr + field.FixedOffset, readDataPtr, field.Size);
                        }

                        if (field.OnSync != null)
                        {
                            Unsafe.CopyBlock(tempData, fieldPtr, field.Size);
                            Unsafe.CopyBlock(fieldPtr, readDataPtr, field.Size);
                            if(latestEntityData != null && entity.IsLocalControlled)
                                Unsafe.CopyBlock(latestEntityData + field.FixedOffset, readDataPtr, field.Size);
                            //put prev data into reader for SyncCalls
                            Unsafe.CopyBlock(readDataPtr, tempData, field.Size);
                            hasChanges =  Utils.memcmp(readDataPtr, fieldPtr, field.PtrSize) != 0;
                        }
                        else
                        {
                            Unsafe.CopyBlock(fieldPtr, readDataPtr, field.Size);
                            if(latestEntityData != null && entity.IsLocalControlled)
                                Unsafe.CopyBlock(latestEntityData + field.FixedOffset, readDataPtr, field.Size);
                        }
                    }

                    if(field.OnSync != null && hasChanges)
                        _syncCalls[_syncCallsCount++] = new SyncCallInfo
                        {
                            OnSync = field.OnSync,
                            Entity = entity,
                            PrevDataPos = readerPosition,
                            IsEntity = field.FieldType == FieldType.Entity
                        };
                    
                    readerPosition += field.IntSize;
                }
                if (fullSync)
                {
                    for (int i = 0; i < classData.SyncableFields.Length; i++)
                    {
                        Unsafe.AsRef<SyncableField>(entityPtr + classData.SyncableFields[i].Offset).FullSyncRead(rawData, ref readerPosition);
                    }
                }
                entity.OnSyncEnd();
            }
        }
    }
}