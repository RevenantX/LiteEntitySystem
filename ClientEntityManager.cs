using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using K4os.Compression.LZ4;
using LiteEntitySystem.Internal;
using LiteNetLib;

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
        
        public ushort RollBackTick { get; private set; }

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

        public ServerSendRate ServerSendRate => _serverSendRate;
        
        /// <summary>
        /// States count in interpolation buffer
        /// </summary>
        public int LerpBufferCount => _readyStates;
        
        private const int InputBufferSize = 128;

        private readonly NetPeer _localPeer;
        private readonly ServerStateData[] _receivedStates = new ServerStateData[MaxSavedStateDiff];
        private readonly Queue<byte[]> _inputCommands = new (InputBufferSize);
        private readonly Queue<byte[]> _inputPool = new (InputBufferSize);
        private readonly Queue<(ushort, EntityLogic)> _spawnPredictedEntities = new ();
        private readonly byte[][] _interpolatedInitialData = new byte[MaxEntityCount][];
        private readonly byte[][] _interpolatePrevData = new byte[MaxEntityCount][];
        private readonly byte[][] _predictedEntities = new byte[MaxSyncedEntityCount][];
        private readonly byte[] _sendBuffer = new byte[NetConstants.MaxPacketSize];

        private ServerSendRate _serverSendRate;
        private ServerStateData _stateA;
        private ServerStateData _stateB;
        private float _lerpTime;
        private double _timer;
        private ushort _readyStates;
        private ushort _simulatePosition;
        private bool _isSyncReceived;

        private struct SyncCallInfo
        {
            public MethodCallDelegate OnSync;
            public InternalEntity Entity;
            public int PrevDataPos;
        }
        private SyncCallInfo[] _syncCalls;
        private int _syncCallsCount;
        
        private InternalEntity[] _entitiesToConstruct = new InternalEntity[64];
        private int _entitiesToConstructCount;
        private ushort _remoteCallsTick;
        private ushort _lastReceivedInputTick;
        private float _logicLerpMsec;

        //adaptive lerp vars
        private float _adaptiveMiddlePoint = 3f;
        private readonly float[] _jitterSamples = new float[10];
        private int _jitterSampleIdx;
        private readonly Stopwatch _jitterTimer = new ();
        
        //local player
        private NetPlayer _localPlayer;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="typesMap">EntityTypesMap with registered entity types</param>
        /// <param name="localPeer">Local NetPeer</param>
        /// <param name="headerByte">Header byte that will be used for packets (to distinguish entity system packets)</param>
        /// <param name="framesPerSecond">Fixed framerate of game logic</param>
        public ClientEntityManager(
            EntityTypesMap typesMap, 
            InputProcessor inputProcessor, 
            NetPeer localPeer, 
            byte headerByte, 
            byte framesPerSecond) : base(typesMap, inputProcessor, NetworkMode.Client, framesPerSecond)
        {
            _localPeer = localPeer;
            _sendBuffer[0] = headerByte;
            _sendBuffer[1] = PacketClientSync;
            AliveEntities.SubscribeToConstructed(OnAliveConstructed, false);
            AliveEntities.OnDestroyed += OnAliveDestroyed;
            for (int i = 0; i < MaxSavedStateDiff; i++)
            {
                _receivedStates[i] = new ServerStateData();
            }
        }

        private unsafe void OnAliveConstructed(InternalEntity entity)
        {
            ref var classData = ref ClassDataDict[entity.ClassId];

            if (classData.InterpolatedFieldsSize > 0)
            {
                Utils.ResizeOrCreate(ref _interpolatePrevData[entity.Id], classData.InterpolatedFieldsSize);
                
                //for local interpolated
                Utils.ResizeOrCreate(ref _interpolatedInitialData[entity.Id], classData.InterpolatedFieldsSize);
            }

            fixed (byte* interpDataPtr = _interpolatedInitialData[entity.Id])
            {
                for (int i = 0; i < classData.InterpolatedCount; i++)
                {
                    var field = classData.Fields[i];
                    field.TypeProcessor.WriteTo(entity, field.Offset, interpDataPtr + field.FixedOffset);
                }
            }
        }

        private void OnAliveDestroyed(InternalEntity e)
        {
            if(e.Id < MaxSyncedEntityCount)
                _predictedEntities[e.Id] = null;
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
        public unsafe void Deserialize(NetPacketReader reader)
        {
            byte packetType = reader.GetByte();
            if(packetType == PacketBaselineSync)
            {
                _stateB = null;
                _stateA = new ServerStateData
                {
                    Size = reader.GetInt()
                };
                InternalPlayerId = reader.GetByte();
                _serverSendRate = (ServerSendRate)reader.GetByte();
                _localPlayer = new NetPlayer(_localPeer, InternalPlayerId)
                {
                    State = NetPlayerState.Active
                };

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
                _simulatePosition = _stateA.Tick;
                _stateA.Offset = 2;
                fixed (byte* readerData = _stateA.Data)
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
                    _remoteCallsTick = _stateA.Tick;
                }
                ConstructAndSync();
                _isSyncReceived = true;
                _jitterTimer.Restart();
            }
            else
            {
                bool isLastPart = packetType == PacketDiffSyncLast;
                ushort newServerTick = reader.GetUShort();
                int tickDifference = Utils.SequenceDiff(newServerTick, _simulatePosition) / (byte)ServerSendRate;
                if (tickDifference <= 0)
                {
                    reader.Recycle();
                    return;
                }

                while (tickDifference > MaxSavedStateDiff)
                {
                    //fast-forward
                    _timer = _lerpTime;
                    if (_stateB != null || PreloadNextState())
                    {
                        GoToNextState();
                        tickDifference = Utils.SequenceDiff(newServerTick, _simulatePosition) / (byte)ServerSendRate;
                    }
                    else
                    {
                        _simulatePosition = (ushort)(newServerTick - MaxSavedStateDiff);
                        break;
                    }
                }
                
                //sample jitter
                _jitterSamples[_jitterSampleIdx] = _jitterTimer.ElapsedMilliseconds / 1000f;
                _jitterSampleIdx = (_jitterSampleIdx + 1) % _jitterSamples.Length;
                //reset timer
                _jitterTimer.Reset();

                var serverState = _receivedStates[(newServerTick / (byte)_serverSendRate) % MaxSavedStateDiff];
                switch (serverState.Status)
                {
                    case ServerDataStatus.Executed:
                    case ServerDataStatus.Empty: 
                    case ServerDataStatus.Partial:
                        if (serverState.Tick != newServerTick)
                            serverState.Reset(newServerTick);
                        serverState.ReadPart(isLastPart, reader);
                        if(serverState.Status == ServerDataStatus.Ready)
                        {
                            if (Utils.SequenceDiff(serverState.LastReceivedTick, _lastReceivedInputTick) > 0)
                                _lastReceivedInputTick = serverState.LastReceivedTick;
                            _readyStates++;
                        }
                        break;
                    default:
                        Logger.LogError($"Invalid status in read mode: {serverState.Status}");
                        return;
                }
            }
        }

        internal override NetPlayer GetPlayer(byte playerId)
        {
            return UpdateMode == UpdateMode.Normal || playerId != InternalPlayerId ? null : _localPlayer;
        }

        private bool PreloadNextState()
        {
            if (_readyStates == 0)
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

            ushort simPos = (ushort)(_simulatePosition+1);
            while (_receivedStates[(simPos/(byte)ServerSendRate) % MaxSavedStateDiff].Status != ServerDataStatus.Ready)
            {
                simPos = (ushort)(simPos+(byte)ServerSendRate);
            }
            _stateB = _receivedStates[(simPos / (byte)ServerSendRate) % MaxSavedStateDiff];
            _lerpTime = 
                Utils.SequenceDiff(_stateB.Tick, _stateA.Tick) * DeltaTimeF *
                (1f - (_readyStates - _adaptiveMiddlePoint) * 0.02f);
            _stateB.Preload(EntitiesDict);
            _readyStates--;

            //remove processed inputs
            while (_inputCommands.Count > 0)
            {
                if (Utils.SequenceDiff(_stateB.ProcessedTick, (ushort)(_tick - _inputCommands.Count + 1)) >= 0)
                    _inputPool.Enqueue(_inputCommands.Dequeue());
                else
                    break;
            }

            return true;
        }

        private unsafe void GoToNextState()
        {
            _stateA.Status = ServerDataStatus.Executed;
            _stateA = _stateB;
            _simulatePosition = _stateA.Tick;
            _stateB = null;

            fixed (byte* readerData = _stateA.Data)
            {
                for (int i = 0; i < _stateA.PreloadDataCount; i++)
                {
                    ref var preloadData = ref _stateA.PreloadDataArray[i];
                    int offset = preloadData.DataOffset;
                    ReadEntityState(readerData, ref offset, preloadData.EntityId, preloadData.EntityFieldsOffset == -1);
                    if (offset == -1)
                        return;
                }
            }
            ConstructAndSync();
            
            _timer -= _lerpTime;

            //reset owned entities
            foreach (var entity in AliveEntities)
            {
                if(entity.IsLocal)
                    continue;
                
                ref var classData = ref entity.GetClassData();
                if(entity.IsServerControlled && !classData.HasRemotePredictedFields)
                    continue;

                fixed (byte* predictedData = _predictedEntities[entity.Id])
                {
                    for (int i = 0; i < classData.FieldsCount; i++)
                    {
                        ref var field = ref classData.Fields[i];
                        if ((entity.IsServerControlled && !field.Flags.HasFlagFast(SyncFlags.AlwaysPredict)) ||
                            (entity.IsLocalControlled && field.Flags.HasFlagFast(SyncFlags.OnlyForOtherPlayers)))
                            continue;
                        if (field.FieldType == FieldType.SyncableSyncVar)
                        {
                            var syncableField = Utils.RefFieldValue<SyncableField>(entity, field.Offset);
                            field.TypeProcessor.SetFrom(syncableField, field.SyncableSyncVarOffset, predictedData + field.PredictedOffset);
                        }
                        else
                        {
                            field.TypeProcessor.SetFrom(entity, field.Offset, predictedData + field.PredictedOffset);
                        }
                    }
                }
            }

            //reapply input
            UpdateMode = UpdateMode.PredictionRollback;
            RollBackTick = (ushort)(_tick - _inputCommands.Count + 1);
            foreach (var inputCommand in _inputCommands)
            {
                //reapply input data
                fixed (byte* rawInputData = inputCommand)
                {
                    var header = *(InputPacketHeader*)rawInputData;
                    _localPlayer.StateATick = header.StateA;
                    _localPlayer.StateBTick = header.StateB;
                    _localPlayer.LerpTime = header.LerpMsec;
                }
                InputProcessor.ReadInputs(this, _localPlayer.Id, inputCommand, sizeof(InputPacketHeader), inputCommand.Length);
                foreach (var entity in AliveEntities)
                {
                    if(entity.IsLocal || !entity.IsLocalControlled)
                        continue;
                    entity.Update();
                }
                RollBackTick++;
            }
            UpdateMode = UpdateMode.Normal;
            
            //update local interpolated position
            foreach (var entity in AliveEntities)
            {
                if(entity.IsLocal || !entity.IsLocalControlled)
                    continue;
                
                ref var classData = ref entity.GetClassData();
                for(int i = 0; i < classData.InterpolatedCount; i++)
                {
                    fixed (byte* currentDataPtr = _interpolatedInitialData[entity.Id])
                    {
                        ref var field = ref classData.Fields[i];
                        field.TypeProcessor.WriteTo(entity, field.Offset, currentDataPtr + field.FixedOffset);
                    }
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
        
        protected override unsafe void OnLogicTick()
        {
            if (_stateB != null)
            {
                _logicLerpMsec = (float)(_timer / _lerpTime);
                ServerTick = Utils.LerpSequence(_stateA.Tick, _stateB.Tick, _logicLerpMsec);
                int maxTick = -1;
                for (int i = 0; i < _stateB.RemoteCallsCount; i++)
                {
                    ref var rpcCache = ref _stateB.RemoteCallsCaches[i];
                    if (Utils.SequenceDiff(rpcCache.Tick, _remoteCallsTick) > 0 && Utils.SequenceDiff(rpcCache.Tick, ServerTick) <= 0)
                    {
                        if (maxTick == -1 || Utils.SequenceDiff(rpcCache.Tick, (ushort)maxTick) > 0)
                        {
                            maxTick = rpcCache.Tick;
                        }
                        var entity = EntitiesDict[rpcCache.EntityId];
                        if (rpcCache.FieldId == byte.MaxValue)
                        {
                            rpcCache.Delegate(entity, new ReadOnlySpan<byte>(_stateB.Data, rpcCache.Offset, rpcCache.Count));
                        }
                        else
                        {
                            rpcCache.Delegate(
                                Utils.RefFieldValue<SyncableField>(entity, ClassDataDict[entity.ClassId].SyncableFieldOffsets[rpcCache.FieldId]), 
                                new ReadOnlySpan<byte>(_stateB.Data, rpcCache.Offset, rpcCache.Count));
                        }
                    }
                }
                if(maxTick != -1)
                    _remoteCallsTick = (ushort)maxTick;
            }

            if (_inputCommands.Count > InputBufferSize)
            {
                _inputCommands.Clear();
            }
            
            int maxResultSize = sizeof(InputPacketHeader) + InputProcessor.GetInputsSize(this);
            if (_inputPool.TryDequeue(out byte[] inputWriter))
                Utils.ResizeIfFull(ref inputWriter, maxResultSize);
            else
                inputWriter = new byte[maxResultSize];
            
            fixed(void* writerData = inputWriter)
                *(InputPacketHeader*)writerData = new InputPacketHeader
                {
                    StateA   = _stateA.Tick,
                    StateB   = _stateB?.Tick ?? _stateA.Tick,
                    LerpMsec = _logicLerpMsec
                };

            //generate inputs
            int offset = sizeof(InputPacketHeader);
            InputProcessor.GenerateAndWriteInputs(this, inputWriter, ref offset);

            //read
            InputProcessor.ReadInputs(this, _localPlayer.Id, inputWriter, sizeof(InputPacketHeader), offset);
            _inputCommands.Enqueue(inputWriter);

            //local only and UpdateOnClient
            foreach (var entity in AliveEntities)
            {
                ref var classData = ref ClassDataDict[entity.ClassId];
                if (entity.IsLocal || entity.IsLocalControlled)
                {
                    //save data for interpolation before update
                    fixed (byte* currentDataPtr = _interpolatedInitialData[entity.Id],
                           prevDataPtr = _interpolatePrevData[entity.Id])
                    {
                        //restore previous
                        for(int i = 0; i < classData.InterpolatedCount; i++)
                        {
                            var field = classData.Fields[i];
                            field.TypeProcessor.SetFrom(entity, field.Offset, currentDataPtr + field.FixedOffset);
                        }

                        //update
                        entity.Update();
                
                        //save current
                        Unsafe.CopyBlock(prevDataPtr, currentDataPtr, (uint)classData.InterpolatedFieldsSize);
                        for(int i = 0; i < classData.InterpolatedCount; i++)
                        {
                            var field = classData.Fields[i];
                            field.TypeProcessor.WriteTo(entity, field.Offset, currentDataPtr + field.FixedOffset);
                        }
                    }
                }
                else if(classData.UpdateOnClient)
                {
                    entity.Update();
                }
            }
        }

        public override unsafe void Update()
        {
            if (!_isSyncReceived)
                return;
            
            //logic update
            ushort prevTick = _tick;
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
                    fixed (byte* initialDataPtr = _interpolatedInitialData[entity.Id], nextDataPtr = _stateB.Data)
                    {
                        for (int j = 0; j < preloadData.InterpolatedCachesCount; j++)
                        {
                            var interpolatedCache = preloadData.InterpolatedCaches[j];
                            var field = fields[interpolatedCache.Field];
                            field.TypeProcessor.SetInterpolation(
                                entity, 
                                field.Offset,
                                initialDataPtr + field.FixedOffset,
                                nextDataPtr + interpolatedCache.StateReaderOffset, 
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
                
                ref var classData = ref entity.GetClassData();
                fixed (byte* currentDataPtr = _interpolatedInitialData[entity.Id], prevDataPtr = _interpolatePrevData[entity.Id])
                {
                    for(int i = 0; i < classData.InterpolatedCount; i++)
                    {
                        var field = classData.Fields[i];
                        field.TypeProcessor.SetInterpolation(
                            entity,
                            field.Offset,
                            prevDataPtr + field.FixedOffset,
                            currentDataPtr + field.FixedOffset,
                            localLerpT);
                    }
                }
            }

            //send buffered input
            if (_tick != prevTick)
            {
                //pack tick first
                int offset = 4;
                fixed (byte* sendBuffer = _sendBuffer)
                {
                    ushort currentTick = (ushort)(_tick - _inputCommands.Count + 1);
                    ushort tickIndex = 0;
                    
                    //Logger.Log($"SendingCommands start {_tick}");
                    foreach (var inputCommand in _inputCommands)
                    {
                        if (Utils.SequenceDiff(currentTick, _lastReceivedInputTick) <= 0)
                        {
                            currentTick++;
                            continue;
                        }
                        fixed (byte* inputData = inputCommand)
                        {
                            if (offset + inputCommand.Length + sizeof(ushort) > _localPeer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable))
                            {
                                *(ushort*)(sendBuffer + 2) = currentTick;
                                _localPeer.Send(_sendBuffer, 0, offset, DeliveryMethod.Unreliable);
                                offset = 4;
                                
                                currentTick += tickIndex;
                                tickIndex = 0;
                            }
                            
                            //put size
                            *(ushort*)(sendBuffer + offset) = (ushort)(inputCommand.Length - sizeof(InputPacketHeader));
                            offset += sizeof(ushort);
                            
                            //put data
                            Unsafe.CopyBlock(sendBuffer + offset, inputData, (uint)inputCommand.Length);
                            offset += inputCommand.Length;
                        }

                        tickIndex++;
                        if (tickIndex == ServerEntityManager.MaxStoredInputs)
                        {
                            break;
                        }
                    }
                    *(ushort*)(sendBuffer + 2) = currentTick;
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
            if (entity.GetClassData().IsUpdateable && !entity.GetClassData().UpdateOnClient)
                AliveEntities.Add(entity);
        }
        
        internal void RemoveOwned(EntityLogic entity)
        {
            if (entity.GetClassData().IsUpdateable && !entity.GetClassData().UpdateOnClient)
                AliveEntities.Remove(entity);
        }

        internal void AddPredictedInfo(EntityLogic e)
        {
            _spawnPredictedEntities.Enqueue((_tick, e));
        }

        private void ConstructAndSync()
        {
            ServerTick = _stateA.Tick;
            
            //Call construct methods
            for (int i = 0; i < _entitiesToConstructCount; i++)
            {
                ConstructEntity(_entitiesToConstruct[i]);
            }
            _entitiesToConstructCount = 0;
            
            //Make OnSyncCalls
            for (int i = 0; i < _syncCallsCount; i++)
            {
                ref var syncCall = ref _syncCalls[i];
                syncCall.OnSync(syncCall.Entity, new ReadOnlySpan<byte>(_stateA.Data, syncCall.PrevDataPos, _stateA.Size-syncCall.PrevDataPos));
            }
            _syncCallsCount = 0;
            
            foreach (var lagCompensatedEntity in LagCompensatedEntities)
                lagCompensatedEntity.WriteHistory(ServerTick);
        }

        private unsafe void ReadEntityState(byte* rawData, ref int readerPosition, ushort entityInstanceId, bool fullSync)
        {
            if (entityInstanceId == InvalidEntityId || entityInstanceId >= MaxSyncedEntityCount)
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
                ushort classId = *(ushort*)(rawData + readerPosition + 1);
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
                    ref var cd = ref ClassDataDict[entity.ClassId];
                    if(cd.PredictedSize > 0)
                        Utils.ResizeOrCreate(ref _predictedEntities[entity.Id], cd.PredictedSize);
                    if(cd.InterpolatedFieldsSize > 0)
                        Utils.ResizeOrCreate(ref _interpolatedInitialData[entity.Id], cd.InterpolatedFieldsSize);

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
            
            ref var classData = ref entity.GetClassData();
            ref byte[] interpolatedInitialData = ref _interpolatedInitialData[entity.Id];
            int fieldsFlagsOffset = readerPosition - classData.FieldsFlagsSize;
            bool writeInterpolationData = entity.IsServerControlled || fullSync;
            Utils.ResizeOrCreate(ref _syncCalls, _syncCallsCount + classData.FieldsCount);

            fixed (byte* interpDataPtr = interpolatedInitialData, predictedData = _predictedEntities[entity.Id])
            {
                entity.OnSyncStart();
                for (int i = 0; i < classData.FieldsCount; i++)
                {
                    if (!fullSync && !Utils.IsBitSet(rawData + fieldsFlagsOffset, i))
                        continue;
                    
                    ref var field = ref classData.Fields[i];
                    byte* readDataPtr = rawData + readerPosition;
                    
                    if( fullSync || 
                        (entity.IsServerControlled && field.Flags.HasFlagFast(SyncFlags.AlwaysPredict)) || 
                        (entity.IsLocalControlled && !field.Flags.HasFlagFast(SyncFlags.OnlyForOtherPlayers)) )
                        Unsafe.CopyBlock(predictedData + field.PredictedOffset, readDataPtr, field.Size);
                    
                    if (field.FieldType == FieldType.SyncableSyncVar)
                    {
                        var syncableField = Utils.RefFieldValue<SyncableField>(entity, field.Offset);
                        field.TypeProcessor.SetFrom(syncableField, field.SyncableSyncVarOffset, readDataPtr);
                    }
                    else
                    {
                        if (field.Flags.HasFlagFast(SyncFlags.Interpolated) && writeInterpolationData)
                        {
                            //this is interpolated save for future
                            Unsafe.CopyBlock(interpDataPtr + field.FixedOffset, readDataPtr, field.Size);
                        }

                        if (field.OnSync != null)
                        {
                            if (field.TypeProcessor.SetFromAndSync(entity, field.Offset, readDataPtr))
                            {
                                _syncCalls[_syncCallsCount++] = new SyncCallInfo
                                {
                                    OnSync = field.OnSync,
                                    Entity = entity,
                                    PrevDataPos = readerPosition
                                };
                            }
                        }
                        else
                        {
                            field.TypeProcessor.SetFrom(entity, field.Offset, readDataPtr);
                        }
                    }
                    readerPosition += field.IntSize;
                }
                if (fullSync)
                {
                    for (int i = 0; i < classData.SyncableFieldOffsets.Length; i++)
                        Utils.RefFieldValue<SyncableField>(entity, classData.SyncableFieldOffsets[i]).FullSyncRead(new ReadOnlySpan<byte>(rawData, _stateA.Size), ref readerPosition);
                }
                entity.OnSyncEnd();
            }
        }
    }
}