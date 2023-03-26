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
        /// Current rollback tick (valid only in Rollback state)
        /// </summary>
        public ushort RollBackTick { get; private set; }

        /// <summary>
        /// Current state server tick
        /// </summary>
        public ushort RawServerTick => _stateAIndex != -1 ? _receivedStates[_stateAIndex].Tick : (ushort)0;

        /// <summary>
        /// Target state server tick
        /// </summary>
        public ushort RawTargetServerTick => _stateBIndex != -1 ? _receivedStates[_stateBIndex].Tick : RawServerTick;

        /// <summary>
        /// Our local player
        /// </summary>
        public NetPlayer LocalPlayer => _localPlayer;
        
        /// <summary>
        /// Stored input commands count for prediction correction
        /// </summary>
        public int StoredCommands => _inputCommands.Count;
        
        /// <summary>
        /// Player tick processed by server
        /// </summary>
        public ushort LastProcessedTick => _stateAIndex != -1 ? _receivedStates[_stateAIndex].ProcessedTick : (ushort)0;

        /// <summary>
        /// Last received player tick by server
        /// </summary>
        public ushort LastReceivedTick => _stateAIndex != -1 ? _receivedStates[_stateAIndex].LastReceivedTick : (ushort)0;

        /// <summary>
        /// Send rate of server
        /// </summary>
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
        private readonly Queue<(ushort id, EntityLogic entity)> _spawnPredictedEntities = new ();
        private readonly byte[][] _interpolatedInitialData = new byte[MaxEntityCount][];
        private readonly byte[][] _interpolatePrevData = new byte[MaxEntityCount][];
        private readonly byte[][] _predictedEntities = new byte[MaxSyncedEntityCount][];
        private readonly byte[] _sendBuffer = new byte[NetConstants.MaxPacketSize];

        private ServerSendRate _serverSendRate;
        private int _stateAIndex = -1;
        private int _stateBIndex = -1;
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
        private ushort _lastReceivedInputTick;
        private float _logicLerpMsec;

        //adaptive lerp vars
        private float _adaptiveMiddlePoint = 3f;
        private readonly float[] _jitterSamples = new float[10];
        private int _jitterSampleIdx;
        private readonly Stopwatch _jitterTimer = new();
        
        //local player
        private NetPlayer _localPlayer;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="typesMap">EntityTypesMap with registered entity types</param>
        /// <param name="inputProcessor">Input processor (you can use default InputProcessor/<T/> or derive from abstract one to make your own input serialization</param>
        /// <param name="localPeer">Local NetPeer</param>
        /// <param name="headerByte">Header byte that will be used for packets (to distinguish entity system packets)</param>
        /// <param name="framesPerSecond">Fixed framerate of game logic</param>
        public ClientEntityManager(
            EntityTypesMap typesMap, 
            InputProcessor inputProcessor, 
            NetPeer localPeer, 
            byte headerByte, 
            byte framesPerSecond) : base(typesMap, inputProcessor, NetworkMode.Client, framesPerSecond, headerByte)
        {
            _localPeer = localPeer;
            _sendBuffer[0] = headerByte;
            _sendBuffer[1] = InternalPackets.ClientSync;

            AliveEntities.SubscribeToConstructed(OnAliveConstructed, false);
            AliveEntities.OnDestroyed += OnAliveDestroyed;
            for (int i = 0; i < MaxSavedStateDiff; i++)
            {
                _receivedStates[i] = new ServerStateData();
                _receivedStates[i].Init();
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
        public bool DeserializeWithHeaderCheck(NetDataReader reader)
        {
            if (reader.PeekByte() == _sendBuffer[0])
            {
                reader.SkipBytes(1);
                Deserialize(reader);
                return true;
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetStateIndex(ushort tick)
        {
            return (tick / (byte)_serverSendRate) % MaxSavedStateDiff;
        }

        /// <summary>
        /// Read incoming data omitting header byte
        /// </summary>
        /// <param name="reader">NetDataReader with data</param>
        public unsafe void Deserialize(NetDataReader reader)
        {
            fixed (byte* rawData = reader.RawData)
                Deserialize(rawData + reader.UserDataOffset, reader.UserDataSize);
        }
        
        private unsafe void Deserialize(byte* rawData, int size)
        {
            byte packetType = rawData[1];
            if(packetType == InternalPackets.BaselineSync)
            {
                //read header and decode
                int decodedBytes;
                var header = *(BaselineDataHeader*)rawData;
                _serverSendRate = (ServerSendRate)header.SendRate;
                _stateAIndex = GetStateIndex(header.Tick);
                _stateBIndex = -1;
                ref var firstState = ref _receivedStates[_stateAIndex];
                firstState.Size = header.OriginalLength;
                firstState.Tick = header.Tick;
                firstState.Data = new byte[header.OriginalLength];
                InternalPlayerId = header.PlayerId;
                _localPlayer = new NetPlayer(_localPeer, InternalPlayerId);

                fixed (byte* stateData = firstState.Data)
                {
                    decodedBytes = LZ4Codec.Decode(
                        rawData + sizeof(BaselineDataHeader),
                        size - sizeof(BaselineDataHeader),
                        stateData,
                        firstState.Size);
                    if (decodedBytes != header.OriginalLength)
                    {
                        Logger.LogError("Error on decompress");
                        return;
                    }
                    int bytesRead = 0;
                    while (bytesRead < firstState.Size)
                    {
                        ushort entityId = *(ushort*)(stateData + bytesRead);
                        //Logger.Log($"[CEM] ReadBaseline Entity: {entityId} pos: {bytesRead}");
                        bytesRead += sizeof(ushort);
                        
                        if (entityId == InvalidEntityId || entityId >= MaxSyncedEntityCount)
                        {
                            Logger.LogError($"Bad data (id > {MaxSyncedEntityCount} or Id == 0) Id: {entityId}");
                            return;
                        }

                        ReadEntityState(stateData, ref bytesRead, entityId, true);
                        if (bytesRead == -1)
                            return;
                    }
                }
                
                _simulatePosition = firstState.Tick;
                _inputCommands.Clear();
                _isSyncReceived = true;
                _jitterTimer.Restart();
                ConstructAndSync(true, firstState.Tick);
                Logger.Log($"[CEM] Got baseline sync. Assigned player id: {header.PlayerId}, Original: {decodedBytes}, Tick: {header.Tick}, SendRate: {_serverSendRate}");
            }
            else
            {
                var diffHeader = *(DiffPartHeader*)rawData;

                int tickDifference = Utils.SequenceDiff(diffHeader.Tick, _simulatePosition) / (byte)ServerSendRate;
                if (tickDifference <= 0)
                {
                    //old state
                    return;
                }

                while (tickDifference > MaxSavedStateDiff)
                {
                    //fast-forward
                    _timer = _lerpTime;
                    if (_stateBIndex != -1 || PreloadNextState())
                    {
                        Logger.Log("TickOverflow, GoToNext");
                        GoToNextState();
                        tickDifference = Utils.SequenceDiff(diffHeader.Tick, _simulatePosition) / (byte)ServerSendRate;
                    }
                    else
                    {
                        _readyStates = 0;
                        Logger.Log("TickOverflow, _simulatePosition"); //TODO fix
                        _simulatePosition = (ushort)(diffHeader.Tick - MaxSavedStateDiff);
                        break;
                    }
                }
                
                //sample jitter
                _jitterSamples[_jitterSampleIdx] = _jitterTimer.ElapsedMilliseconds / 1000f;
                _jitterSampleIdx = (_jitterSampleIdx + 1) % _jitterSamples.Length;
                //reset timer
                _jitterTimer.Reset();

                ref var serverState = ref _receivedStates[GetStateIndex(diffHeader.Tick)];
                switch (serverState.Status)
                {
                    case ServerDataStatus.Executed:
                    case ServerDataStatus.Empty: 
                    case ServerDataStatus.Partial:
                        serverState.ReadPart(diffHeader, rawData, size);
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
            while (_receivedStates[GetStateIndex(simPos)].Status != ServerDataStatus.Ready)
            {
                simPos = (ushort)(simPos+(byte)ServerSendRate);
            }

            _stateBIndex = GetStateIndex(simPos);
            ref var stateB = ref _receivedStates[_stateBIndex];
            _lerpTime = 
                Utils.SequenceDiff(stateB.Tick, RawServerTick) * DeltaTimeF *
                (1f - (_readyStates - _adaptiveMiddlePoint) * 0.02f);
            stateB.Preload(EntitiesDict);
            _readyStates--;

            //remove processed inputs
            while (_inputCommands.Count > 0)
            {
                if (Utils.SequenceDiff(stateB.ProcessedTick, (ushort)(_tick - _inputCommands.Count + 1)) >= 0)
                    _inputPool.Enqueue(_inputCommands.Dequeue());
                else
                    break;
            }

            return true;
        }

        private unsafe void GoToNextState()
        {
            ref var stateA = ref _receivedStates[_stateAIndex];
            stateA.Status = ServerDataStatus.Executed;
            _stateAIndex = _stateBIndex;
            _stateBIndex = -1;

            ref var newState = ref _receivedStates[_stateAIndex];
            _simulatePosition = newState.Tick;

            fixed (byte* readerData = newState.Data)
            {
                for (int i = 0; i < newState.PreloadDataCount; i++)
                {
                    ref var preloadData = ref newState.PreloadDataArray[i];
                    ReadEntityState(readerData, ref preloadData.DataOffset, preloadData.EntityId, preloadData.EntityFieldsOffset == -1);
                    if (preloadData.DataOffset == -1)
                        return;
                }
            }
            ConstructAndSync(false, stateA.Tick);
            
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
                if (Utils.SequenceDiff(newState.ProcessedTick, info.id) >= 0)
                {
                    _spawnPredictedEntities.Dequeue();
                    info.entity.DestroyInternal();
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
            ref var stateA = ref _receivedStates[_stateAIndex]; 
            
            if (_stateBIndex != -1)
            {
                ref var stateB = ref _receivedStates[_stateBIndex]; 
                _logicLerpMsec = (float)(_timer / _lerpTime);
                ServerTick = Utils.LerpSequence(stateA.Tick, stateB.Tick, _logicLerpMsec);
                stateB.ExecuteRpcs(this, stateA.Tick, false);
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
            
            fixed(byte* writerData = inputWriter)
                *(InputPacketHeader*)writerData = new InputPacketHeader
                {
                    StateA   = stateA.Tick,
                    StateB   = RawTargetServerTick,
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
                        RefMagic.CopyBlock(prevDataPtr, currentDataPtr, (uint)classData.InterpolatedFieldsSize);
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

        /// <summary>
        /// Update method, call this every frame
        /// </summary>
        public override unsafe void Update()
        {
            if (!_isSyncReceived)
                return;
            
            //logic update
            ushort prevTick = _tick;
            base.Update();
            
            if (_stateBIndex != -1 || PreloadNextState())
            {
                _timer += VisualDeltaTime;
                if (_timer >= _lerpTime)
                {
                    GoToNextState();
                }
            }

            if (_stateBIndex != -1)
            {
                //remote interpolation
                float fTimer = (float)(_timer/_lerpTime);
                ref var stateB = ref _receivedStates[_stateBIndex];
                for(int i = 0; i < stateB.InterpolatedCount; i++)
                {
                    ref var preloadData = ref stateB.PreloadDataArray[stateB.InterpolatedFields[i]];
                    var entity = EntitiesDict[preloadData.EntityId];
                    var fields = entity.GetClassData().Fields;
                    fixed (byte* initialDataPtr = _interpolatedInitialData[entity.Id], nextDataPtr = stateB.Data)
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
                            RefMagic.CopyBlock(sendBuffer + offset, inputData, (uint)inputCommand.Length);
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

        private void ConstructAndSync(bool firstSync, ushort minimalTick)
        {
            ref var stateA = ref _receivedStates[_stateAIndex];

            //execute all previous rpcs
            ServerTick = stateA.Tick;
            stateA.ExecuteRpcs(this, minimalTick, firstSync);

            //Call construct methods
            for (int i = 0; i < _entitiesToConstructCount; i++)
                ConstructEntity(_entitiesToConstruct[i]);
            _entitiesToConstructCount = 0;
            
            //Make OnSyncCalls
            for (int i = 0; i < _syncCallsCount; i++)
            {
                ref var syncCall = ref _syncCalls[i];
                syncCall.OnSync(syncCall.Entity, new ReadOnlySpan<byte>(stateA.Data, syncCall.PrevDataPos, stateA.Size-syncCall.PrevDataPos));
            }
            _syncCallsCount = 0;
            
            foreach (var lagCompensatedEntity in LagCompensatedEntities)
                lagCompensatedEntity.WriteHistory(ServerTick);
        }

        private unsafe void ReadEntityState(byte* rawData, ref int readerPosition, ushort entityInstanceId, bool fullSync)
        {
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

            entity.OnSyncStart();
            fixed (byte* interpDataPtr = interpolatedInitialData, predictedData = _predictedEntities[entity.Id])
            {
                for (int i = 0; i < classData.FieldsCount; i++)
                {
                    if (!fullSync && !Utils.IsBitSet(rawData + fieldsFlagsOffset, i))
                        continue;
                    
                    ref var field = ref classData.Fields[i];
                    byte* readDataPtr = rawData + readerPosition;
                    
                    if(fullSync || 
                       (entity.IsServerControlled && field.Flags.HasFlagFast(SyncFlags.AlwaysPredict)) || 
                       (entity.IsLocalControlled && !field.Flags.HasFlagFast(SyncFlags.OnlyForOtherPlayers)))
                        RefMagic.CopyBlock(predictedData + field.PredictedOffset, readDataPtr, field.Size);
                    
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
                            RefMagic.CopyBlock(interpDataPtr + field.FixedOffset, readDataPtr, field.Size);
                        }

                        if (field.OnSync != null)
                        {
                            if (field.TypeProcessor.SetFromAndSync(entity, field.Offset, readDataPtr))
                                _syncCalls[_syncCallsCount++] = new SyncCallInfo
                                {
                                    OnSync = field.OnSync,
                                    Entity = entity,
                                    PrevDataPos = readerPosition
                                };
                        }
                        else
                        {
                            field.TypeProcessor.SetFrom(entity, field.Offset, readDataPtr);
                        }
                    }
                    readerPosition += field.IntSize;
                }
            }

            if (fullSync)
                _receivedStates[_stateAIndex].ReadRPCs(rawData, ref readerPosition, new EntitySharedReference(entity.Id, entity.Version), classData);
            entity.OnSyncEnd();
        }
    }
}