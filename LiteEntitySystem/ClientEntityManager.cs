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
        /// Is server->client rpc currently executing
        /// </summary>
        public bool IsExecutingRPC { get; internal set; }

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
        private readonly AVLTree<InternalEntity> _modifiedEntitiesToRollback = new();
        
        private readonly AbstractNetPeer _netPeer;
        private readonly Queue<ServerStateData> _statesPool = new(MaxSavedStateDiff);
        private readonly Dictionary<ushort, ServerStateData> _receivedStates = new();
        private readonly SequenceBinaryHeap<ServerStateData> _readyStates = new(MaxSavedStateDiff);
        private readonly Queue<PredictableEntityLogic> _tempLocalEntities = new ();
        private readonly byte[] _sendBuffer = new byte[NetConstants.MaxPacketSize];
        private readonly HashSet<InternalEntity> _changedEntities = new();
        private readonly CircularBuffer<InputInfo> _storedInputHeaders = new(InputBufferSize);
        private InternalEntity[] _entitiesToRemove = new InternalEntity[64];
        private readonly Queue<InternalEntity> _entitiesToRollback = new();
        private int _entitiesToRemoveCount;

        private ServerSendRate _serverSendRate;
        private ServerStateData _stateA;
        private ServerStateData _stateB;
        private float _remoteInterpolationTotalTime;
        private float _remoteInterpolationTimer;
        private float _remoteLerpFactor;
        private ushort _prevTick;
        
        private readonly IdGeneratorUShort _localIdQueue = new(MaxSyncedEntityCount, MaxEntityCount);
        
        private SyncCallInfo[] _syncCalls;
        private int _syncCallsCount;
        
        private ushort _lastReceivedInputTick;
        private ushort _lastReadyTick;

        //used for debug info
        private ushort _rollbackStep;
        private ushort _rollbackTotalSteps;
        private ushort _tickBeforeRollback;
        
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
            private readonly int _prevDataPos;
            private readonly int _fieldId;

            public SyncCallInfo(InternalEntity entity, int prevDataPos, int fieldId)
            {
                _fieldId = fieldId;
                _entity = entity;
                _prevDataPos = prevDataPos;
            }

            public void Execute(ServerStateData state)
            {
                try
                {
                    ref var classData = ref _entity.ClassData;
                    ref var fieldInfo = ref classData.Fields[_fieldId];
                    fieldInfo.OnSync(fieldInfo.GetTargetObject(_entity), new ReadOnlySpan<byte>(state.Data, _prevDataPos, fieldInfo.IntSize));
                }
                catch (Exception e)
                {
                    Logger.LogError($"OnChange error in user code. Entity: {_entity}. Error: {e}");
                }
            }
        }

        public override string GetCurrentFrameDebugInfo(DebugFrameModes modes) => 
            UpdateMode == UpdateMode.PredictionRollback && modes.HasFlagFast(DebugFrameModes.Rollback)
                ? $"[ClientRollback({_rollbackStep}/{_rollbackTotalSteps})] Tick {_tickBeforeRollback}. RollbackTick: {_tick}. ServerTick: {ServerTick}" 
                : modes.HasFlagFast(DebugFrameModes.Client) && UpdateMode == UpdateMode.Normal
                    ? $"[Client] Tick {_tick}. ServerTick: {ServerTick}"
                    : string.Empty;

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
                _statesPool.Enqueue(new ServerStateData(this));
        }

        public override void Reset()
        {
            base.Reset();
            _modifiedEntitiesToRollback.Clear();
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
        internal unsafe T AddLocalEntity<T>(EntityLogic parent, Action<T> initMethod) where T : PredictableEntityLogic
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
            _tempLocalEntities.Enqueue(entity);

            fixed (byte* predictedData = classData.GetLastServerData(entity))
            {
                for (int i = 0; i < classData.FieldsCount; i++)
                {
                    ref var field = ref classData.Fields[i];
                    var targetObject = field.GetTargetObjectAndOffset(entity, out int offset);
                    
                    if(field.Flags.HasFlagFast(SyncFlags.Interpolated))
                        field.TypeProcessor.SetInterpValueFromCurrentValue(targetObject, offset);
                    if(field.IsPredicted)
                        field.TypeProcessor.WriteTo(targetObject, offset, predictedData + field.PredictedOffset);
                }
            }
            
            //init syncable rollback
            for (int i = 0; i < classData.SyncableFieldsCustomRollback.Length; i++)
            {
                var syncableField = RefMagic.GetFieldValue<SyncableFieldCustomRollback>(entity, classData.SyncableFieldsCustomRollback[i].Offset);
                syncableField.BeforeReadRPC();
                syncableField.AfterReadRPC();
            }

            return entity;
        }

        internal PredictableEntityLogic FindEntityByPredictedId(ushort tick, ushort parentId, ushort predictedId)
        {
            foreach (var predictedEntity in _tempLocalEntities)
                if (predictedEntity.IsEntityMatch(predictedId, parentId, tick))
                    return predictedEntity;
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
                    _remoteInterpolationTimer = 0f;
                    _remoteLerpFactor = 0f;
                    foreach (var controller in GetEntities<HumanControllerLogic>())
                        controller.ClearClientStoredInputs();
                    _storedInputHeaders.Clear();
                    _jitterTimer.Reset();
                    
                    _stateA.ExecuteRpcs((ushort)(_stateA.Tick - 1),RPCExecuteMode.FirstSync);
                    ReadDiff();
                    ExecuteSyncCalls(_stateA);
                    foreach (var lagCompensatedEntity in LagCompensatedEntities)
                        ClassDataDict[lagCompensatedEntity.ClassId].WriteHistory(lagCompensatedEntity, ServerTick);
                    
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
                            serverState = new ServerStateData(this) { Tick = diffHeader.Tick };
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
                            _remoteInterpolationTimer = _remoteInterpolationTotalTime;
                            //fast-forward
                            GoToNextState();
                            
                            //to add space to _readyStates
                            PreloadNextState();
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
            _remoteInterpolationTotalTime = Utils.SequenceDiff(_stateB.Tick, _stateA.Tick) * DeltaTimeF;
            _remoteInterpolationTotalTime *= 1 - GetSpeedMultiplier(LerpBufferTimeLength)*TimeSpeedChangeCoef;

            //tune game prediction and input generation speed
            SpeedMultiplier = GetSpeedMultiplier(_stateB.BufferedInputsCount * DeltaTimeF);
            
            return true;

            float GetSpeedMultiplier(float bufferTime) =>
                Utils.Lerp(-1f, 0f, Utils.InvLerp(lowestBound - TimeSpeedChangeFadeTime, lowestBound, bufferTime)) +
                Utils.Lerp(0f, 1f, Utils.InvLerp(upperBound, upperBound + TimeSpeedChangeFadeTime, bufferTime));
        }

        private unsafe void GoToNextState()
        {
            _remoteInterpolationTimer -= _remoteInterpolationTotalTime;

            _tickBeforeRollback = _tick;
            _rollbackStep = 0;
            _rollbackTotalSteps = (ushort)_storedInputHeaders.Count;
            
            ushort targetTick = _tick;
            var humanControllerFilter = GetEntities<HumanControllerLogic>();
            
            //================== Rollback part ===========================
            _entitiesToRollback.Clear();
            foreach (var entity in _modifiedEntitiesToRollback)
            {
                if(!entity.IsRemoved)
                    _entitiesToRollback.Enqueue(entity);
            }
            
            UpdateMode = UpdateMode.PredictionRollback;
            //reset predicted entities
            foreach (var entity in _entitiesToRollback)
            {
                ref var classData = ref ClassDataDict[entity.ClassId];
                var rollbackFields = classData.GetRollbackFields(entity.IsLocalControlled);
                entity.OnBeforeRollback();

                fixed (byte* lastServerData = classData.GetLastServerData(entity))
                {
                    for (int i = 0; i < rollbackFields.Length; i++)
                    {
                        ref var field = ref classData.Fields[rollbackFields[i]];
                        var target = field.GetTargetObjectAndOffset(entity, out int offset);
                        
                        if (field.OnSync != null && (field.OnSyncFlags & BindOnChangeFlags.ExecuteOnRollbackReset) != 0)
                        {
                            //this is currently only place which doesn't call classData.CallOnSync
                            field.TypeProcessor.SetFromAndSync(target, offset, lastServerData + field.PredictedOffset, field.OnSync);
                        }
                        else
                        {
                            field.TypeProcessor.SetFrom(target, offset, lastServerData + field.PredictedOffset);
                        }
                    }
                }
                for (int i = 0; i < classData.SyncableFieldsCustomRollback.Length; i++)
                    RefMagic.GetFieldValue<SyncableFieldCustomRollback>(entity, classData.SyncableFieldsCustomRollback[i].Offset).OnRollback();
                entity.OnRollback();
            }
            
            //clear modified here to readd changes after RollbackUpdate
            _modifiedEntitiesToRollback.Clear();
            
            //Step a little to match "predicted" state at server processed tick
            //for correct BindOnSync execution
            int cmdNum = 0;
            while(_storedInputHeaders.Count > 0 && Utils.SequenceDiff(_stateB.ProcessedTick, _storedInputHeaders.Front().Tick) >= 0)
            {
                var storedInput = _storedInputHeaders.Front();
                _storedInputHeaders.PopFront();
                _localPlayer.LoadInputInfo(storedInput.Header);
                _tick = storedInput.Tick;
                foreach (var controller in humanControllerFilter)
                    controller.ReadStoredInput(cmdNum);
                cmdNum++;
                _rollbackStep++;
                
                //simple update
                foreach (var entity in _entitiesToRollback)
                {
                    if (!entity.IsLocalControlled || !AliveEntities.Contains(entity))
                        continue;
                    
                    //skip local entities that spawned later
                    if (entity.IsLocal && entity is PredictableEntityLogic el && Utils.SequenceDiff(el.CreatedAtTick, _tick) >= 0)
                        continue;
                    
                    entity.Update();
                }
            }
            UpdateMode = UpdateMode.Normal;
            
            //remove processed inputs
            foreach (var controller in humanControllerFilter)
                controller.RemoveClientProcessedInputs(_stateB.ProcessedTick);
            
            ushort minimalTick = _stateA.Tick;
            _statesPool.Enqueue(_stateA);
            _stateA = _stateB;
            _stateB = null;
            ServerTick = _stateA.Tick;
            
            //Logger.Log($"GotoState: IST: {ServerTick}, TST:{_stateA.Tick}");
            _changedEntities.Clear();
            _stateA.ExecuteRpcs(minimalTick, RPCExecuteMode.OnNextState);
            ReadDiff();
            ExecuteSyncCalls(_stateA);
            
            //delete predicted
            while (_tempLocalEntities.TryPeek(out var entity))
            {
                if (Utils.SequenceDiff(_stateA.ProcessedTick, entity.CreatedAtTick) >= 0)
                {
                    //Logger.Log($"Delete predicted. Tick: {info.tick}, Entity: {info.entity}");
                    _tempLocalEntities.Dequeue();
                    entity.DestroyInternal();
                    RemoveEntity(entity);
                    _localIdQueue.ReuseId(entity.Id);
                }
                else
                {
                    break;
                }
            }
            
            //write lag comp history for remote rollbackable lagcomp fields
            foreach (var lagCompensatedEntity in LagCompensatedEntities)
                ClassDataDict[lagCompensatedEntity.ClassId].WriteHistory(lagCompensatedEntity, ServerTick);

            for(int i = 0; i < _entitiesToRemoveCount; i++)
            {
                //skip changed
                var entityToRemove = _entitiesToRemove[i];
                if (_changedEntities.Contains(entityToRemove))
                    continue;
                
                //Logger.Log($"[CLI] RemovingEntity: {_entitiesToRemove[i].Id}");
                RemoveEntity(entityToRemove);
                
                _entitiesToRemoveCount--;
                _entitiesToRemove[i] = _entitiesToRemove[_entitiesToRemoveCount];
                _entitiesToRemove[_entitiesToRemoveCount] = null;
                i--;
            }

            //reapply input
            UpdateMode = UpdateMode.PredictionRollback; 
            for(cmdNum = 0; cmdNum < _storedInputHeaders.Count; cmdNum++)
            {
                //reapply input data
                var storedInput = _storedInputHeaders[cmdNum];
                _localPlayer.LoadInputInfo(storedInput.Header);
                _tick = storedInput.Tick;
                foreach (var controller in humanControllerFilter)
                    controller.ReadStoredInput(cmdNum);
                _rollbackStep++;
                
                //simple update
                foreach (var entity in _entitiesToRollback)
                {
                    if (!entity.IsLocalControlled || !AliveEntities.Contains(entity))
                        continue;
                    
                    //skip local entities that spawned later
                    if (entity.IsLocal && entity is PredictableEntityLogic el && Utils.SequenceDiff(el.CreatedAtTick, _tick) >= 0)
                        continue;
                    
                    //update interpolation data
                    if (cmdNum == _storedInputHeaders.Count - 1)
                    {
                        ref var classData = ref ClassDataDict[entity.ClassId];
                        for(int i = 0; i < classData.InterpolatedCount; i++)
                        {
                            ref var field = ref classData.Fields[i];
                            var target = field.GetTargetObjectAndOffset(entity, out int offset);
                            field.TypeProcessor.SetInterpValueFromCurrentValue(target, offset);
                        }
                    }
                    
                    entity.Update();
                }
            }
            UpdateMode = UpdateMode.Normal;
            
            _tick = targetTick;
            _entitiesToRollback.Clear();
        }

        internal void MarkEntityChanged(InternalEntity entity)
        {
            if (entity.IsRemoved || IsExecutingRPC)
                return;
            _modifiedEntitiesToRollback.Add(entity);
        }

        internal override unsafe void EntityFieldChanged<T>(InternalEntity entity, ushort fieldId, ref T newValue, ref T oldValue, bool skipOnSync)
        {
            if (entity.IsRemoved)
                return;
            
            ref var classData = ref ClassDataDict[entity.ClassId];
            ref var fieldInfo = ref classData.Fields[fieldId];
            
            if (!skipOnSync && (fieldInfo.OnSyncFlags & BindOnChangeFlags.ExecuteOnPrediction) != 0)
            {
                T oldValueCopy = oldValue;
                fieldInfo.OnSync(fieldInfo.GetTargetObject(entity), new ReadOnlySpan<byte>(&oldValueCopy, fieldInfo.IntSize));
            }
            
            var rollbackFields = classData.GetRollbackFields(entity.IsLocalControlled);
            if (rollbackFields != null && rollbackFields.Length > 0 && fieldInfo.IsPredicted)
                _modifiedEntitiesToRollback.Add(entity);
        }

        internal override void OnEntityDestroyed(InternalEntity e)
        {
            if (!e.IsLocal)
            {
                //because remote and server alive entities managed by EntityManager
                if(e.IsLocalControlled && e is EntityLogic eLogic)
                    RemoveOwned(eLogic);
                Utils.AddToArrayDynamic(ref _entitiesToRemove, ref _entitiesToRemoveCount, e);
            }
            _modifiedEntitiesToRollback.Remove(e);
            base.OnEntityDestroyed(e);
        }
        
        protected override void OnLogicTick()
        {
            //apply input
            var humanControllers = GetEntities<HumanControllerLogic>();
            if (humanControllers.Count > 0)
            {
                _storedInputHeaders.PushBack(new InputInfo(_tick, new InputPacketHeader
                {
                    StateA = _stateA.Tick,
                    StateB = RawTargetServerTick,
                    LerpMsec = _remoteLerpFactor
                }));
                foreach (var controller in humanControllers)
                {
                    controller.ApplyPendingInput();
                }
            }

            //local only and UpdateOnClient
            foreach (var entity in AliveEntities)
            {
                //first logic tick in Update
                if (_tick == _prevTick && (entity.IsLocal || entity.IsLocalControlled))
                {
                    ref var classData = ref ClassDataDict[entity.ClassId];
                    //save data for interpolation before update
                    for (int i = 0; i < classData.InterpolatedCount; i++)
                    {
                        ref var field = ref classData.Fields[i];
                        var target = field.GetTargetObjectAndOffset(entity, out int offset);
                        field.TypeProcessor.SetInterpValueFromCurrentValue(target, offset);
                    }
                }
                
                entity.Update();
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
            _prevTick = _tick;
            ServerTick = Utils.LerpSequence(_stateA.Tick, _stateB?.Tick ?? _stateA.Tick, _remoteLerpFactor);

            base.Update();
            
            //reduce max jitter to middle
            float dt = (float)VisualDeltaTime;
            if (NetworkJitter > _jitterMiddle)
            {
                NetworkJitter -= dt * 0.1f;
                if (NetworkJitter < MinJitter)
                    NetworkJitter = MinJitter;
            }
            
            //send buffered input
            if (_tick != _prevTick)
                SendBufferedInput();
            
            if (_stateB != null)
            {
                //execute rpcs and spawn entities
                _stateB.ExecuteRpcs(_stateA.Tick, RPCExecuteMode.BetweenStates);
                ExecuteSyncCalls(_stateB);
                
                while(_remoteInterpolationTimer >= _remoteInterpolationTotalTime)
                {
                    GoToNextState();
                    if (!PreloadNextState())
                        break;
                }
                _stateB?.PreloadInterpolationForNewEntities();
                _remoteLerpFactor = _remoteInterpolationTimer / _remoteInterpolationTotalTime;
                _remoteInterpolationTimer += dt;
            }
            
            //local only and UpdateOnClient
            foreach (var entity in AliveEntities)
            {
                entity.VisualUpdate();
            }
        }

        internal T GetInterpolatedValue<T>(ref SyncVar<T> syncVar, T interpValue) where T : unmanaged
        {
            if (IsLagCompensationEnabled && IsEntityLagCompensated(syncVar.Container))
                return syncVar.Value;
            
            var typeProcessor = (ValueTypeProcessor<T>)ClassDataDict[syncVar.Container.ClassId].Fields[syncVar.FieldId].TypeProcessor;
            return syncVar.Container.IsLocalControlled
                ? typeProcessor.GetInterpolatedValue(interpValue, syncVar.Value, LerpFactor)
                : typeProcessor.GetInterpolatedValue(syncVar.Value, interpValue, _remoteLerpFactor);
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
                        LerpMsec = _remoteLerpFactor, 
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
                    
            //Logger.Log($"[CEM] New Entity: {entityId}");
            //remove old entity
            if (entity != null && entity.Version != entityDataHeader.Version)
            {
                //can happen when entities in remove list but new arrived
                //this can be only on logics (not on singletons)
                Logger.Log($"[CEM] Replace entity by new: {entityDataHeader.Version}. Class: {entityDataHeader.ClassId}. Id: {entityId}");
                entity.DestroyInternal();
                
                RemoveEntity(entity);
                entity = null;
            } 
            if (entity == null) //create new
            {
                ref var classData = ref ClassDataDict[entityDataHeader.ClassId];
                AddEntity<InternalEntity>(new EntityParams(entityId, entityDataHeader, this, classData.AllocateDataCache()));
            }
        }
        
        private void ExecuteSyncCalls(ServerStateData stateData)
        {
            ExecuteLateConstruct();
            for (int i = 0; i < _syncCallsCount; i++)
                _syncCalls[i].Execute(stateData);
            _syncCallsCount = 0;
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
            
            //set values to same as predicted entity for correct OnSync calls
            if (!entity.IsConstructed && entity is PredictableEntityLogic pel)
            {
                foreach (var localEntity in _tempLocalEntities)
                {
                    if (!pel.IsSameAsLocal(localEntity))
                        continue;
                    
                    //Logger.Log($"Found predictable entity. LocalId was: {localEntity.Id}, server id: {entityId}");

                    for (int i = 0; i < classData.FieldsCount; i++)
                    {
                        ref var field = ref classData.Fields[i];
                        //skip some fields because they need to trigger onChange
                        if (field.OnSync == null || i == pel.InternalOwnerId.FieldId || i == pel.IsSyncEnabledFieldId)
                            continue;
                        
                        var target = field.GetTargetObjectAndOffset(entity, out int offset);
                        field.TypeProcessor.CopyFrom(target, localEntity, offset);
                    }
                    
                    break;
                }
            }

            fixed (byte* predictedData = classData.GetLastServerData(entity))
            {
                for (int i = 0; i < classData.FieldsCount; i++)
                {
                    ref var field = ref classData.Fields[i];
                    var target = field.GetTargetObjectAndOffset(entity, out int offset);
                    
                    if (writeInterpolationData && field.Flags.HasFlagFast(SyncFlags.Interpolated))
                        field.TypeProcessor.SetInterpValue(target, offset, rawData + readerPosition);

                    if (field.IsPredicted)
                        RefMagic.CopyBlock(predictedData + field.PredictedOffset, rawData + readerPosition, field.Size);
                    
                    if (field.OnSync != null && (field.OnSyncFlags & BindOnChangeFlags.ExecuteOnSync) != 0)
                    {
                        if (field.TypeProcessor.SetFromAndSync(target, offset, rawData + readerPosition))
                            _syncCalls[_syncCallsCount++] = new SyncCallInfo(entity, readerPosition, i);
                    }
                    else
                    {
                        field.TypeProcessor.SetFrom(target, offset, rawData + readerPosition);
                    }

                    //Logger.Log($"E {entity.Id} Field updated: {field.Name} = {field.TypeProcessor.ToString(entity, field.Offset)}");
                    readerPosition += field.IntSize;
                }
            }

            //add owned entities to rollback queue
            if(!entity.IsConstructed && entity.InternalOwnerId.Value == InternalPlayerId)
                _entitiesToRollback.Enqueue(entity);
            
            //Construct
            ConstructEntity(entity);

            //Logger.Log($"ConstructedEntity: {entityId}, pid: {entityLogic.PredictedId}");
        }

        private unsafe void ReadDiff()
        {
            int readerPosition = _stateA.DataOffset;
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
                    fixed (byte* predictedData = classData.GetLastServerData(entity))
                    {
                        for (int i = 0; i < classData.FieldsCount; i++)
                        {
                            if (!Utils.IsBitSet(rawData + fieldsFlagsOffset, i))
                                continue;
                            ref var field = ref classData.Fields[i];
                            var target = field.GetTargetObjectAndOffset(entity, out int offset);
                            
                            if (field.IsPredicted)
                                RefMagic.CopyBlock(predictedData + field.PredictedOffset, rawData + readerPosition, field.Size);
                    
                            if (field.OnSync != null && (field.OnSyncFlags & BindOnChangeFlags.ExecuteOnSync) != 0)
                            {
                                if (field.TypeProcessor.SetFromAndSync(target, offset, rawData + readerPosition))
                                    _syncCalls[_syncCallsCount++] = new SyncCallInfo(entity, readerPosition, i);
                            }
                            else
                            {
                                field.TypeProcessor.SetFrom(target, offset, rawData + readerPosition);
                            }

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
            _stateA?.GetDiagnosticData(diagnosticDataDict);
        }
    }
}