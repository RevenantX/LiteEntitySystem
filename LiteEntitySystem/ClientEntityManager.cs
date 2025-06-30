using System;
using System.Collections.Generic;
using System.Diagnostics;
using LiteEntitySystem.Internal;
using LiteEntitySystem.Transport;
using LiteEntitySystem.Collections;
using LiteNetLib;

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
        /// Tick of currently executing rpc (check only in client RPC methods)
        /// </summary>
        public ushort CurrentRPCTick { get; internal set; }

        /// <summary>
        /// Is server->client rpc currently executing
        /// </summary>
        public bool IsExecutingRPC { get; private set; }

        /// <summary>
        /// Current state server tick
        /// </summary>
        public ushort RawServerTick => _stateA?.Tick ?? 0;

        /// <summary>
        /// Target state server tick
        /// </summary>
        public ushort RawTargetServerTick => _stateB?.Tick ?? RawServerTick;

        /// <summary>
        /// Our local player
        /// </summary>
        public NetPlayer LocalPlayer => _localPlayer;
        
        /// <summary>
        /// Stored input commands count for prediction correction
        /// </summary>
        public int StoredCommands => _storedInputHeaders.Count;
        
        /// <summary>
        /// Player tick processed by server
        /// </summary>
        public ushort LastProcessedTick => _stateA?.ProcessedTick ?? 0;

        /// <summary>
        /// Last received player tick by server
        /// </summary>
        public ushort LastReceivedTick => _stateA?.LastReceivedTick ?? 0;
        
        /// <summary>
        /// Inputs count in server input buffer
        /// </summary>
        public byte ServerInputBuffer => _stateB?.BufferedInputsCount ?? _stateA?.BufferedInputsCount ?? 0;

        /// <summary>
        /// Send rate of server
        /// </summary>
        public ServerSendRate ServerSendRate => _serverSendRate;
        
        /// <summary>
        /// States count in interpolation buffer
        /// </summary>
        public int LerpBufferCount => _readyStates.Count;

        /// <summary>
        /// Total states time in interpolation buffer
        /// </summary>
        public float LerpBufferTimeLength => _readyStates.Count * DeltaTimeF * (int)_serverSendRate;
        
        /// <summary>
        /// Current state size in bytes
        /// </summary>
        public int StateSize => _stateA?.Size ?? 0;

        /// <summary>
        /// Client network peer
        /// </summary>
        public AbstractNetPeer NetPeer => _netPeer;

        /// <summary>
        /// Network jitter in milliseconds
        /// </summary>
        public float NetworkJitter { get; private set; }

        /// <summary>
        /// Average jitter
        /// </summary>
        public float AverageJitter => _jitterMiddle;

        /// <summary>
        /// Preferred input and incoming states buffer length in seconds lowest bound
        /// Buffer automatically increases to Jitter time + PreferredBufferTimeLowest
        /// </summary>
        public float PreferredBufferTimeLowest = 0.025f; 
        
        /// <summary>
        /// Preferred input and incoming states buffer length in seconds lowest bound
        /// Buffer automatically decreases to Jitter time + PreferredBufferTimeHighest
        /// </summary>
        public float PreferredBufferTimeHighest = 0.05f;

        /// <summary>
        /// Entities that waiting for remove
        /// </summary>
        public int PendingToRemoveEntites => _entitiesToRemoveCount;
        
        private const float TimeSpeedChangeFadeTime = 0.1f;
        private const float MaxJitter = 0.2f;
        private const float MinJitter = 0.001f;
        
        /// <summary>
        /// Maximum stored inputs count
        /// </summary>
        public const int InputBufferSize = 64;

        //predicted entities that should use rollback
        private readonly AVLTree<InternalEntity> _predictedEntities = new();
        
        private readonly AbstractNetPeer _netPeer;
        private readonly Queue<ServerStateData> _statesPool = new(MaxSavedStateDiff);
        private readonly Dictionary<ushort, ServerStateData> _receivedStates = new();
        private readonly SequenceBinaryHeap<ServerStateData> _readyStates = new(MaxSavedStateDiff);
        private readonly Queue<(ushort tick, EntityLogic entity)> _spawnPredictedEntities = new ();
        private readonly byte[] _sendBuffer = new byte[NetConstants.MaxPacketSize];
        private readonly HashSet<InternalEntity> _changedEntities = new();
        private readonly CircularBuffer<InputInfo> _storedInputHeaders = new(InputBufferSize);
        private InternalEntity[] _entitiesToRemove = new InternalEntity[64];
        private readonly Queue<EntityLogic> _entitiesToRollback = new();
        private int _entitiesToRemoveCount;

        private ServerSendRate _serverSendRate;
        private ServerStateData _stateA;
        private ServerStateData _stateB;
        private float _lerpTime;
        private double _timer;
        
        private readonly IdGeneratorUShort _localIdQueue = new(MaxSyncedEntityCount, MaxEntityCount);
        
        private SyncCallInfo[] _syncCalls;
        private int _syncCallsCount;
        
        private ushort _lastReceivedInputTick;
        private float _remoteLerpMsec;
        private ushort _lastReadyTick;

        //time manipulation
        private readonly float[] _jitterSamples = new float[50];
        private int _jitterSampleIdx;
        private readonly Stopwatch _jitterTimer = new();
        private float _jitterPrevTime;
        private float _jitterMiddle;
        private float _jitterSum;
        
        //local player
        private NetPlayer _localPlayer;

        private readonly struct SyncCallInfo
        {
            private readonly InternalEntity _entity;
            private readonly MethodCallDelegate _onSync;
            private readonly int _prevDataPos;
            private readonly int _dataSize;

            public SyncCallInfo(MethodCallDelegate onSync, InternalEntity entity, int prevDataPos, int dataSize)
            {
                _dataSize = dataSize;
                _onSync = onSync;
                _entity = entity;
                _prevDataPos = prevDataPos;
            }

            public void Execute(ServerStateData state)
            {
                try
                {
                    _onSync(_entity, new ReadOnlySpan<byte>(state.Data, _prevDataPos, _dataSize));
                }
                catch (Exception e)
                {
                    Logger.LogError($"OnChange error in user code. Entity: {_entity}. Error: {e}");
                }
            }
        }

        /// <summary>
        /// Return client controller if exist
        /// </summary>
        /// <typeparam name="T">controller type</typeparam>
        /// <returns>controller if exist otherwise null</returns>
        public T GetPlayerController<T>() where T : HumanControllerLogic
        {
            if (_localPlayer == null)
                return null;
            foreach (var controller in GetEntities<T>())
                if (controller.InternalOwnerId.Value == _localPlayer.Id)
                    return controller;
            return null;
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="typesMap">EntityTypesMap with registered entity types</param>
        /// <param name="netPeer">Local AbstractPeer</param>
        /// <param name="headerByte">Header byte that will be used for packets (to distinguish entity system packets)</param>
        /// <param name="maxHistorySize">Maximum size of lag compensation history in ticks</param>
        public ClientEntityManager(
            EntityTypesMap typesMap, 
            AbstractNetPeer netPeer, 
            byte headerByte, 
            MaxHistorySize maxHistorySize = MaxHistorySize.Size32) : base(typesMap, NetworkMode.Client, headerByte, maxHistorySize)
        {
            _netPeer = netPeer;
            _sendBuffer[0] = headerByte;
            _sendBuffer[1] = InternalPackets.ClientInput;
            
            for (int i = 0; i < MaxSavedStateDiff; i++)
                _statesPool.Enqueue(new ServerStateData());
        }

        public override void Reset()
        {
            base.Reset();
            _predictedEntities.Clear();
            _localIdQueue.Reset();
            _readyStates.Clear();
            _syncCallsCount = 0;
            _entitiesToRemoveCount = 0;
        }
        
        /// <summary>
        /// Add local entity that will be not synchronized
        /// </summary>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Created entity or null if entities limit is reached (<see cref="EntityManager.MaxEntityCount"/>)</returns>
        internal T AddLocalEntity<T>(EntityLogic parent, Action<T> initMethod) where T : EntityLogic
        {
            if (_localIdQueue.AvailableIds == 0)
            {
                Logger.LogError("Max local entities count reached");
                return null;
            }

            ref var classData = ref ClassDataDict[EntityClassInfo<T>.ClassId];
            var entity = AddEntity<T>(new EntityParams(
                _localIdQueue.GetNewId(),
                new EntityDataHeader(
                    EntityClassInfo<T>.ClassId, 
                    0, 
                    0),
                this,
                classData.AllocateDataCache()));
            
            //Logger.Log($"AddPredicted, tick: {_tick}, rb: {InRollBackState}, id: {entity.Id}");
            
            entity.InternalOwnerId.Value = parent.InternalOwnerId;
            entity.SetParentInternal(parent);
            initMethod(entity);
            ConstructEntity(entity);
            _spawnPredictedEntities.Enqueue((_tick, entity));
            
            for(int i = 0; i < classData.InterpolatedCount; i++)
            {
                ref var field = ref classData.Fields[i];
                field.TypeProcessor.SetInterpValueFromCurrentValue(entity, field.Offset);
            }
            
            return entity;
        }

        internal EntityLogic FindEntityByPredictedId(ushort tick, ushort parentId, ushort predictedId)
        {
            foreach (var predictedEntity in _spawnPredictedEntities)
            {
                if (predictedEntity.tick == tick && 
                    predictedEntity.entity.ParentId.Id == parentId &&
                    predictedEntity.entity.PredictedId == predictedId)
                    return predictedEntity.entity;
            }
            return null;
        }
        
        /// Read incoming data
        /// <param name="inData">Incoming data including header byte</param>
        /// <returns>Deserialization result</returns>
        public unsafe DeserializeResult Deserialize(ReadOnlySpan<byte> inData)
        {
            if (inData[0] != HeaderByte)
                return DeserializeResult.HeaderCheckFailed;
            fixed (byte* rawData = inData)
            {
                if (inData[1] == InternalPackets.BaselineSync)
                {
                    if (inData.Length < sizeof(BaselineDataHeader))
                        return DeserializeResult.Error;
                    
                    //read header and decode
                    var header = *(BaselineDataHeader*)rawData;
                    if (header.OriginalLength < 0)
                        return DeserializeResult.Error;
                    SetTickrate(header.Tickrate);
                    _serverSendRate = (ServerSendRate)header.SendRate;

                    //reset pooled
                    if (_stateB != null)
                    {
                        _statesPool.Enqueue(_stateB);
                        _stateB = null;
                    }

                    while (_readyStates.Count > 0)
                        _statesPool.Enqueue(_readyStates.ExtractMin());

                    foreach (var stateData in _receivedStates.Values)
                        _statesPool.Enqueue(stateData);

                    _receivedStates.Clear();

                    _stateA ??= _statesPool.Dequeue();
                    if(!_stateA.ReadBaseline(header, rawData, inData.Length))
                        return DeserializeResult.Error;
                    
                    InternalPlayerId = header.PlayerId;
                    if(_localPlayer == null || _localPlayer.Id != InternalPlayerId)
                        _localPlayer = new NetPlayer(_netPeer, InternalPlayerId);
                    ServerTick = _stateA.Tick;
                    _lastReadyTick = ServerTick;
                    foreach (var controller in GetEntities<HumanControllerLogic>())
                        controller.ClearClientStoredInputs();
                    _storedInputHeaders.Clear();
                    _jitterTimer.Reset();
                    
                    IsExecutingRPC = true;
                    _stateA.ExecuteRpcs(this, 0, true);
                    IsExecutingRPC = false;
                    int readerPosition = _stateA.DataOffset;
                    ReadDiff(ref readerPosition);
                    ExecuteSyncCallsAndWriteHistory(_stateA);
                    
                    Logger.Log($"[CEM] Got baseline sync. Assigned player id: {header.PlayerId}, Original: {_stateA.Size}, Tick: {header.Tick}, SendRate: {_serverSendRate}");
                }
                else
                {
                    if (inData.Length < sizeof(DiffPartHeader))
                        return DeserializeResult.Error;
                    var diffHeader = *(DiffPartHeader*)rawData;
                    int tickDifference = Utils.SequenceDiff(diffHeader.Tick, _lastReadyTick);
                    if (tickDifference <= 0)
                    {
                        //old state
                        return DeserializeResult.Done;
                    }

                    //sample jitter
                    float currentJitterTimer = _jitterTimer.ElapsedMilliseconds / 1000f;
                    ref float jitterSample = ref _jitterSamples[_jitterSampleIdx];
                    
                    _jitterSum -= jitterSample;
                    jitterSample = Math.Abs(currentJitterTimer - _jitterPrevTime);
                    _jitterSum += jitterSample;
                    _jitterPrevTime = currentJitterTimer;
                    _jitterSampleIdx = (_jitterSampleIdx + 1) % _jitterSamples.Length;
                    //reset timer
                    _jitterTimer.Restart();

                    if (!_receivedStates.TryGetValue(diffHeader.Tick, out var serverState))
                    {
                        if (_statesPool.Count == 0)
                        {
                            serverState = new ServerStateData { Tick = diffHeader.Tick };
                        }
                        else
                        {
                            serverState = _statesPool.Dequeue();
                            serverState.Reset(diffHeader.Tick);
                        }

                        _receivedStates.Add(diffHeader.Tick, serverState);
                    }

                    if (serverState.ReadPart(diffHeader, rawData, inData.Length))
                    {
                        //if(serverState.TotalPartsCount > 1)
                        //    Logger.Log($"Parts: {serverState.TotalPartsCount}");
                        if (Utils.SequenceDiff(serverState.LastReceivedTick, _lastReceivedInputTick) > 0)
                            _lastReceivedInputTick = serverState.LastReceivedTick;
                        if (Utils.SequenceDiff(serverState.Tick, _lastReadyTick) > 0)
                            _lastReadyTick = serverState.Tick;
                        _receivedStates.Remove(serverState.Tick);

                        if (_readyStates.Count == MaxSavedStateDiff)
                        {
                            //one state should be already preloaded
                            _timer = _lerpTime;
                            //fast-forward
                            GoToNextState();
                        }

                        _readyStates.Add(serverState, serverState.Tick);
                        PreloadNextState();
                    }
                }
            }

            return DeserializeResult.Done;
        }

        private bool PreloadNextState()
        {
            if (_stateB != null)
                return true;
            if (_readyStates.Count == 0)
                return false;
            
            //get max and middle jitter
            _jitterMiddle = _jitterSum / _jitterSamples.Length;
            if (_jitterMiddle > NetworkJitter)
                NetworkJitter = _jitterMiddle;
            
            _stateB = _readyStates.ExtractMin();
            _stateB.Preload(EntitiesDict);
            //Logger.Log($"Preload A: {_stateA.Tick}, B: {_stateB.Tick}");

            //limit jitter for pause scenarios
            if (NetworkJitter > MaxJitter)
                NetworkJitter = MaxJitter;
            float lowestBound = NetworkJitter * 1.5f + PreferredBufferTimeLowest;
            float upperBound = NetworkJitter * 1.5f + PreferredBufferTimeHighest;

            //tune buffer playing speed 
            _lerpTime = Utils.SequenceDiff(_stateB.Tick, _stateA.Tick) * DeltaTimeF;
            _lerpTime *= 1 - GetSpeedMultiplier(LerpBufferTimeLength)*TimeSpeedChangeCoef;

            //tune game prediction and input generation speed
            SpeedMultiplier = GetSpeedMultiplier(_stateB.BufferedInputsCount * DeltaTimeF);
            
            return true;

            float GetSpeedMultiplier(float bufferTime) =>
                Utils.Lerp(-1f, 0f, Utils.InvLerp(lowestBound - TimeSpeedChangeFadeTime, lowestBound, bufferTime)) +
                Utils.Lerp(0f, 1f, Utils.InvLerp(upperBound, upperBound + TimeSpeedChangeFadeTime, bufferTime));
        }

        private unsafe void GoToNextState()
        {
            //remove processed inputs
            foreach (var controller in GetEntities<HumanControllerLogic>())
                controller.RemoveClientProcessedInputs(_stateB.ProcessedTick);
            while (_storedInputHeaders.Count > 0 && Utils.SequenceDiff(_stateB.ProcessedTick, _storedInputHeaders.Front().Tick) >= 0)
                _storedInputHeaders.PopFront();
            
            //delete predicted
            while (_spawnPredictedEntities.TryPeek(out var info))
            {
                if (Utils.SequenceDiff(_stateB.ProcessedTick, info.tick) >= 0)
                {
                    //Logger.Log($"Delete predicted. Tick: {info.tick}, Entity: {info.entity}");
                    _spawnPredictedEntities.Dequeue();
                    info.entity.DestroyInternal();
                    RemoveEntity(info.entity);
                    _localIdQueue.ReuseId(info.entity.Id);
                }
                else
                {
                    break;
                }
            }
            
            ushort minimalTick = _stateA.Tick;
            _statesPool.Enqueue(_stateA);
            _stateA = _stateB;
            _stateB = null;
            
            //Logger.Log($"GotoState: IST: {ServerTick}, TST:{_stateA.Tick}}");
            
            //================== ReadEntityStates BEGIN ==================
            _changedEntities.Clear();
            ServerTick = _stateA.Tick;
            IsExecutingRPC = true;
            _stateA.ExecuteRpcs(this, minimalTick, false);
            IsExecutingRPC = false;
            int readerPosition = _stateA.DataOffset;
            ReadDiff(ref readerPosition);
            ExecuteSyncCallsAndWriteHistory(_stateA);

            for(int i = 0; i < _entitiesToRemoveCount; i++)
            {
                //skip changed
                var entityToRemove = _entitiesToRemove[i];
                if (_changedEntities.Contains(entityToRemove))
                    continue;

                _predictedEntities.Remove(entityToRemove);
                
                //Logger.Log($"[CLI] RemovingEntity: {_entitiesToRemove[i].Id}");
                RemoveEntity(entityToRemove);
                
                _entitiesToRemoveCount--;
                _entitiesToRemove[i] = _entitiesToRemove[_entitiesToRemoveCount];
                _entitiesToRemove[_entitiesToRemoveCount] = null;
                i--;
            }
            
            //================== ReadEntityStates END ====================
            
            _timer -= _lerpTime;
            
            //================== Rollback part ===========================
            //reset predicted entities
            foreach (var entity in _predictedEntities)
            {
                ref var classData = ref ClassDataDict[entity.ClassId];
                var rollbackFields = classData.GetRollbackFields(entity.IsLocalControlled);
                if(rollbackFields == null || rollbackFields.Length == 0)
                    continue;
                entity.OnBeforeRollback();

                fixed (byte* predictedData = classData.ClientPredictedData(entity))
                {
                    for (int i = 0; i < rollbackFields.Length; i++)
                    {
                        ref var field = ref rollbackFields[i];
                        if (field.FieldType == FieldType.SyncableSyncVar)
                        {
                            var syncableField = RefMagic.GetFieldValue<SyncableField>(entity, field.Offset);
                            field.TypeProcessor.SetFrom(syncableField, field.SyncableSyncVarOffset, predictedData + field.PredictedOffset);
                        }
                        else
                        {
                            field.TypeProcessor.SetFrom(entity, field.Offset, predictedData + field.PredictedOffset);
                        }
                    }
                }
                for (int i = 0; i < classData.SyncableFields.Length; i++)
                    RefMagic.GetFieldValue<SyncableField>(entity, classData.SyncableFields[i].Offset).OnRollback();
                entity.OnRollback();
            }
    
            //reapply input
            UpdateMode = UpdateMode.PredictionRollback;
            ushort targetTick = _tick;
            for(int cmdNum = 0; cmdNum < _storedInputHeaders.Count; cmdNum++)
            {
                //reapply input data
                var storedInput = _storedInputHeaders[cmdNum];
                _localPlayer.LoadInputInfo(storedInput.Header);
                _tick = storedInput.Tick;
                foreach (var controller in GetEntities<HumanControllerLogic>())
                    controller.ReadStoredInput(cmdNum);
                //simple update
                foreach (var entity in AliveEntities)
                {
                    if (entity.IsLocal || !entity.IsLocalControlled)
                        continue;
                    entity.Update();
                }
            }
            _tick = targetTick;
            UpdateMode = UpdateMode.Normal;
        }

        internal override void OnEntityDestroyed(InternalEntity e)
        {
            if (!e.IsLocal)
            {
                if(e.IsLocalControlled && e is EntityLogic eLogic)
                    RemoveOwned(eLogic);
                Utils.AddToArrayDynamic(ref _entitiesToRemove, ref _entitiesToRemoveCount, e);
            }
            
            base.OnEntityDestroyed(e);
        }
        
        protected override void OnLogicTick()
        {
            if (_stateB != null)
            {
                ServerTick = Utils.LerpSequence(_stateA.Tick, _stateB.Tick, (float)(_timer/_lerpTime));
                
                foreach (var entity in AliveEntities)
                {
                    if (!entity.IsLocal && !entity.IsLocalControlled)
                        continue;
                    
                    ref var classData = ref ClassDataDict[entity.ClassId];
                    //save data for interpolation before update
                    for (int i = 0; i < classData.InterpolatedCount; i++)
                    {
                        ref var field = ref classData.Fields[i];
                        field.TypeProcessor.SetInterpValueFromCurrentValue(entity, field.Offset);
                    }
                }
            }

            //apply input
            var humanControllers = GetEntities<HumanControllerLogic>();
            if (humanControllers.Count > 0)
            {
                _storedInputHeaders.PushBack(new InputInfo(_tick, new InputPacketHeader
                {
                    StateA = _stateA.Tick,
                    StateB = RawTargetServerTick,
                    LerpMsec = _remoteLerpMsec
                }));
                foreach (var controller in humanControllers)
                {
                    controller.ApplyPendingInput();
                }
            }

            //local only and UpdateOnClient
            foreach (var entity in AliveEntities)
                entity.Update();

            if (_stateB != null)
            {
                //execute rpcs and spawn entities
                IsExecutingRPC = true;
                _stateB.ExecuteRpcs(this, _stateA.Tick, false);
                IsExecutingRPC = false;
                ExecuteSyncCallsAndWriteHistory(_stateB);
            }

            if (NetworkJitter > _jitterMiddle)
            {
                NetworkJitter -= DeltaTimeF * 0.1f;
                if (NetworkJitter < MinJitter)
                    NetworkJitter = MinJitter;
            }
        }

        /// <summary>
        /// Update method, call this every frame
        /// </summary>
        public override void Update()
        {
            //skip update until receive first sync and tickrate
            if (Tickrate == 0)
                return;
            
            //logic update
            ushort prevTick = _tick;
            
            base.Update();
            
            //send buffered input
            if (_tick != prevTick)
                SendBufferedInput();
            
            if (_stateB != null)
            {
                _timer += VisualDeltaTime;
                
                while(_timer >= _lerpTime)
                {
                    GoToNextState();
                    if (!PreloadNextState())
                        break;
                }
                
                if (_stateB != null)
                {
                    _remoteLerpMsec = (float)(_timer/_lerpTime);
                    _stateB.PreloadInterpolationForNewEntities(EntitiesDict);
                }
            }
            
            //local only and UpdateOnClient
            foreach (var entity in AliveEntities)
            {
                entity.VisualUpdate();
            }
        }

        internal T GetInterpolatedValue<T>(ref SyncVar<T> syncVar, T interpValue) where T : unmanaged
        {
            var typeProcessor = (ValueTypeProcessor<T>)ClassDataDict[syncVar.Container.ClassId].Fields[syncVar.FieldId].TypeProcessor;
            return syncVar.Container.IsLocalControlled
                ? typeProcessor.GetInterpolatedValue(interpValue, syncVar.Value, LerpFactor)
                : typeProcessor.GetInterpolatedValue(syncVar.Value, interpValue, _remoteLerpMsec);
        }

        private unsafe void SendBufferedInput()
        {
            if (_storedInputHeaders.Count == 0)
            {
                fixed (byte* sendBuffer = _sendBuffer)
                {
                    *(ushort*)(sendBuffer + 2) = Tick;
                    *(InputPacketHeader*)(sendBuffer + 4) = new InputPacketHeader
                    {
                        LerpMsec = _remoteLerpMsec, 
                        StateA = _stateA.Tick, 
                        StateB = RawTargetServerTick
                    };
                    _netPeer.SendUnreliable(new ReadOnlySpan<byte>(sendBuffer, 4+InputPacketHeader.Size));
                    _netPeer.TriggerSend();
                }
                return;
            }
            
            //pack tick first
            int offset = 4;
            int maxSinglePacketSize = _netPeer.GetMaxUnreliablePacketSize();
            ushort currentTick = _storedInputHeaders[0].Tick;
            ushort tickIndex = 0;
            int prevInputIndex = -1;

            int maxDeltaSize = InputPacketHeader.Size;
            foreach (var humanControllerLogic in GetEntities<HumanControllerLogic>())
                maxDeltaSize += humanControllerLogic.MaxInputDeltaSize;

            if (offset + maxDeltaSize > maxSinglePacketSize)
            {
                Logger.LogError($"Input data from controllers is more than MTU: {maxSinglePacketSize}");
                return;
            }
            
            fixed (byte* sendBuffer = _sendBuffer)
            {
                //Logger.Log($"SendingCommands start {_tick}");
                for(int i = 0; i < _storedInputHeaders.Count; i++)
                {
                    if (Utils.SequenceDiff(currentTick, _lastReceivedInputTick) <= 0)
                    {
                        currentTick++;
                        continue;
                    }

                    //overflow
                    if (offset + maxDeltaSize > maxSinglePacketSize)
                    {
                        prevInputIndex = -1;
                        *(ushort*)(sendBuffer + 2) = currentTick;
                        _netPeer.SendUnreliable(new ReadOnlySpan<byte>(sendBuffer, offset));
                        offset = 4;
                        currentTick += tickIndex;
                        tickIndex = 0;
                    }
       
                    //write header
                    *(InputPacketHeader*)(sendBuffer + offset) = _storedInputHeaders[i].Header;
                    offset += InputPacketHeader.Size;
                    
                    //write current into temporary buffer for delta encoding
                    foreach (var controller in GetEntities<HumanControllerLogic>())
                    {
                        offset += controller.DeltaEncode(
                            prevInputIndex,
                            i,
                            new Span<byte>(sendBuffer + offset, controller.MaxInputDeltaSize));
                    }
                    prevInputIndex = i;
                    tickIndex++;
                    if (tickIndex == ServerEntityManager.MaxStoredInputs)
                        break;
                }
                *(ushort*)(sendBuffer + 2) = currentTick;
                _netPeer.SendUnreliable(new ReadOnlySpan<byte>(sendBuffer, offset));
                _netPeer.TriggerSend();
            }
        }

        internal void AddOwned(EntityLogic entity)
        {
            var flags = entity.ClassData.Flags;
            if (flags.HasFlagFast(EntityFlags.Updateable) && !flags.HasFlagFast(EntityFlags.UpdateOnClient))
                AliveEntities.Add(entity);
        }
        
        internal void RemoveOwned(EntityLogic entity)
        {
            var flags = entity.ClassData.Flags;
            if (flags.HasFlagFast(EntityFlags.Updateable) && !flags.HasFlagFast(EntityFlags.UpdateOnClient))
                AliveEntities.Remove(entity);
        }

        internal unsafe void ReadNewRPC(ushort entityId, byte* rawData)
        {
            //Logger.Log("NewRPC");
            var entityDataHeader = *(EntityDataHeader*)rawData;
            if (!IsEntityIdValid(entityId))
            {
                Logger.LogError($"Entity is invalid. Id {entityId}");
                return;
            }
            var entity = EntitiesDict[entityId];
                    
            //Logger.Log($"[CEM] ReadBaseline Entity: {entityId} pos: {bytesRead}");
            //remove old entity
            if (entity != null && entity.Version != entityDataHeader.Version)
            {
                //this can be only on logics (not on singletons)
                Logger.Log($"[CEM] Replace entity by new: {entityDataHeader.Version}. Class: {entityDataHeader.ClassId}. Id: {entityId}");
                entity.DestroyInternal();
                RemoveEntity(entity);
                _predictedEntities.Remove(entity);
                entity = null;
            } 
            if (entity == null) //create new
            {
                ref var classData = ref ClassDataDict[entityDataHeader.ClassId];
                entity = AddEntity<InternalEntity>(new EntityParams(entityId, entityDataHeader, this, classData.AllocateDataCache()));
                     
                if (classData.PredictedSize > 0 || classData.SyncableFields.Length > 0)
                {
                    _predictedEntities.Add(entity);
                    //Logger.Log($"Add predicted: {entity.GetType()}");
                }
            }
        }
        
        private void ExecuteSyncCallsAndWriteHistory(ServerStateData stateData)
        {
            if (_entitiesToRollback.Count > 0)
            {
                //fast forward new but predicted entities
                UpdateMode = UpdateMode.PredictionRollback;
                ushort targetTick = _tick;
                for(int cmdNum = Utils.SequenceDiff(ServerTick, _stateA.Tick); cmdNum < _storedInputHeaders.Count; cmdNum++)
                {
                    //reapply input data
                    var storedInput = _storedInputHeaders[cmdNum];
                    _localPlayer.LoadInputInfo(storedInput.Header);
                    _tick = storedInput.Tick;
                    foreach (var controller in GetEntities<HumanControllerLogic>())
                        controller.ReadStoredInput(cmdNum);
                    
                    foreach (var e in _entitiesToRollback)
                    {
                        if (cmdNum == _storedInputHeaders.Count - 1)
                        {
                            ref var classData = ref ClassDataDict[e.ClassId];
                            for(int i = 0; i < classData.InterpolatedCount; i++)
                            {
                                ref var field = ref classData.Fields[i];
                                field.TypeProcessor.SetInterpValueFromCurrentValue(e, field.Offset);
                            }
                        }
                        e.SafeUpdate();
                    }
                }
                _tick = targetTick;
                UpdateMode = UpdateMode.Normal;
                _entitiesToRollback.Clear();
            }
            
            ExecuteLateConstruct();
            for (int i = 0; i < _syncCallsCount; i++)
                _syncCalls[i].Execute(stateData);
            _syncCallsCount = 0;
            foreach (var lagCompensatedEntity in LagCompensatedEntities)
                ClassDataDict[lagCompensatedEntity.ClassId].WriteHistory(lagCompensatedEntity, ServerTick);
        }
        
        internal unsafe void ReadConstructRPC(ushort entityId, byte* rawData, int readerPosition)
        {
            //Logger.Log("ConstructRPC");
            if (!IsEntityIdValid(entityId))
            {
                Logger.LogError($"Entity is invalid. Id {entityId}");
                return;
            }
    
            var entity = EntitiesDict[entityId];
            bool writeInterpolationData = !entity.IsConstructed || entity.IsRemoteControlled;
            ref var classData = ref entity.ClassData;
            Utils.ResizeOrCreate(ref _syncCalls, _syncCallsCount + classData.FieldsCount);

            fixed (byte* predictedData = classData.ClientPredictedData(entity))
            {
                for (int i = 0; i < classData.FieldsCount; i++)
                {
                    ref var field = ref classData.Fields[i];
                    if (writeInterpolationData && field.Flags.HasFlagFast(SyncFlags.Interpolated))
                        field.TypeProcessor.SetInterpValue(entity, field.Offset, rawData + readerPosition);
                    
                    if (field.ReadField(entity, rawData + readerPosition, predictedData))
                        _syncCalls[_syncCallsCount++] = new SyncCallInfo(field.OnSync, entity, readerPosition, field.IntSize);

                    //Logger.Log($"E {entity.Id} Field updated: {field.Name}");
                    readerPosition += field.IntSize;
                }
            }

            //Construct and fast forward predicted entities
            if (ConstructEntity(entity) == false)
                return;

            if (entity is EntityLogic entityLogic)
            {
                entityLogic.RefreshOwnerInfo(null);
                if(entity.IsLocal || !entity.IsLocalControlled || !entityLogic.IsPredicted || !AliveEntities.Contains(entity))
                    return;
                _entitiesToRollback.Enqueue(entityLogic);
            }

            //Logger.Log($"ConstructedEntity: {entityId}, pid: {entityLogic.PredictedId}");
        }

        private unsafe void ReadDiff(ref int readerPosition)
        {
            fixed (byte* rawData = _stateA.Data)
            {
                while (readerPosition < _stateA.DataOffset + _stateA.DataSize)
                {
                    ushort totalSize = *(ushort*)(rawData + readerPosition);
                    int endPos = readerPosition + totalSize;
                    readerPosition += sizeof(ushort);
                    ushort entityId = *(ushort*)(rawData + readerPosition);
                    readerPosition += sizeof(ushort);
                    if (!IsEntityIdValid(entityId))
                        break;
                    var entity = EntitiesDict[entityId];
                    if (entity == null)
                    {
                        readerPosition = endPos;
                        continue;
                    }

                    ref var classData = ref entity.ClassData;
                    readerPosition += classData.FieldsFlagsSize;
                    _changedEntities.Add(entity);
                    Utils.ResizeOrCreate(ref _syncCalls, _syncCallsCount + classData.FieldsCount);

                    int fieldsFlagsOffset = readerPosition - classData.FieldsFlagsSize;
                    fixed (byte* predictedData = classData.ClientPredictedData(entity))
                    {
                        for (int i = 0; i < classData.FieldsCount; i++)
                        {
                            if (!Utils.IsBitSet(rawData + fieldsFlagsOffset, i))
                                continue;
                            ref var field = ref classData.Fields[i];
                            if (field.ReadField(entity, rawData + readerPosition, predictedData))
                                _syncCalls[_syncCallsCount++] = new SyncCallInfo(field.OnSync, entity, readerPosition,
                                    field.IntSize);

                            //Logger.Log($"E {entity.Id} Field updated: {field.Name}");
                            readerPosition += field.IntSize;
                        }
                    }

                    readerPosition = endPos;
                }
            }
        }
        
        private static bool IsEntityIdValid(ushort id)
        {
            if (id == InvalidEntityId || id >= MaxSyncedEntityCount)
            {
                Logger.LogError($"Bad data (id > {MaxSyncedEntityCount} or Id == 0) Id: {id}");
                return false;
            }
            return true;
        }
        
        public void GetDiagnosticData(Dictionary<int, LESDiagnosticDataEntry> diagnosticDataDict)
        {
            diagnosticDataDict.Clear();
            _stateA?.GetDiagnosticData(EntitiesDict, ClassDataDict, diagnosticDataDict);
        }
    }
}