using System;
using System.Collections.Generic;
using System.Diagnostics;
using K4os.Compression.LZ4;
using LiteEntitySystem.Internal;
using LiteEntitySystem.Transport;
using LiteEntitySystem.Collections;
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
        /// Tick of currently executing rpc (check only in client RPC methods)
        /// </summary>
        public ushort CurrentRPCTick { get; internal set; }

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
        public float PreferredBufferTimeLowest = 0.01f; 
        
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
        private int _entitiesToRemoveCount;

        private ServerSendRate _serverSendRate;
        private ServerStateData _stateA;
        private ServerStateData _stateB;
        private float _lerpTime;
        private double _timer;
        
        private readonly IdGeneratorUShort _localIdQueue = new(MaxSyncedEntityCount, MaxEntityCount);

        private readonly struct SyncCallInfo
        {
            public readonly InternalEntity Entity;
            
            private readonly MethodCallDelegate _onSync;
            private readonly int _prevDataPos;

            public SyncCallInfo(MethodCallDelegate onSync, InternalEntity entity, int prevDataPos)
            {
                _onSync = onSync;
                Entity = entity;
                _prevDataPos = prevDataPos;
            }

            public void Execute(ServerStateData state) => _onSync(Entity, new ReadOnlySpan<byte>(state.Data, _prevDataPos, state.Size-_prevDataPos));
        }
        private SyncCallInfo[] _syncCalls;
        private int _syncCallsCount;
        private SyncCallInfo[] _syncCallsBeforeConstruct;
        private int _syncCallsBeforeConstructCount;
        
        private readonly AVLTree<InternalEntity> _entitiesToConstruct = new();
        private ushort _lastReceivedInputTick;
        private float _logicLerpMsec;
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

        /// <summary>
        /// Return client controller if exist
        /// </summary>
        /// <typeparam name="T">controller type</typeparam>
        /// <returns>controller if exist otherwise null</returns>
        public T GetPlayerController<T>() where T : ControllerLogic
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
            _localIdQueue.Reset();
            _entitiesToRemoveCount = 0;
        }
        
        /// <summary>
        /// Add local entity that will be not synchronized
        /// </summary>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Created entity or null if entities limit is reached (<see cref="EntityManager.MaxEntityCount"/>)</returns>
        internal T AddLocalEntity<T>(Action<T> initMethod = null) where T : EntityLogic
        {
            if (_localIdQueue.AvailableIds == 0)
            {
                Logger.LogError("Max local entities count reached");
                return null;
            }
            
            var entity = AddEntity<T>(new EntityParams(
                new EntityDataHeader(
                    _localIdQueue.GetNewId(), 
                    EntityClassInfo<T>.ClassId, 
                    0, 
                    0),
                this,
                ClassDataDict[EntityClassInfo<T>.ClassId].AllocateDataCache()));
            
            //Logger.Log($"AddPredicted, tick: {_tick}, rb: {InRollBackState}, id: {entity.Id}");
            
            entity.InternalOwnerId.Value = InternalPlayerId;
            initMethod?.Invoke(entity);
            ConstructEntity(entity);
            _spawnPredictedEntities.Enqueue((_tick, entity));
            
            return entity;
        }
        internal void SendServerRPC<T>(ushort entityId, ushort rpcId, T value) where T : unmanaged
        {
            // Prepare a NetDataWriter
            NetDataWriter writer = new NetDataWriter();

            // Add packet header and type
            writer.Put(HeaderByte); // Header byte for this entity system
            writer.Put((byte)InternalPackets.ClientRPC); // Denote it's a client->server RPC

            // Add entity ID and RPC ID
            writer.Put(entityId);
            writer.Put(rpcId);

            unsafe
            {
                byte[] byteArray = new byte[sizeof(T)];
                fixed (byte* byteArrayPtr = byteArray)
                {
                    Buffer.MemoryCopy(&value, byteArrayPtr, sizeof(T), sizeof(T));
                }

                writer.Put(byteArray);
            }

            _netPeer.SendReliableOrdered(writer.AsReadOnlySpan());
        }

        // For "RemoteCallSpan<T>"
        internal void SendServerRPC<T>(ushort entityId, ushort rpcId, ReadOnlySpan<T> data) where T : unmanaged
        {
            // Prepare a NetDataWriter
            NetDataWriter writer = new NetDataWriter();

            // Add packet header and type
            writer.Put(HeaderByte);
            writer.Put(InternalPackets.ClientRPC);

            // Add entity ID and RPC ID
            writer.Put(entityId);
            writer.Put(rpcId);

            byte[] byteArray;
            // Convert span to byte array
            unsafe
            {
                byteArray = new byte[data.Length * sizeof(T)];
                fixed (T* dataPtr = data)
                fixed (byte* bytePtr = byteArray)
                {
                    Buffer.MemoryCopy(dataPtr, bytePtr, byteArray.Length, byteArray.Length);
                }
            }

            // Write the byte array
            writer.Put(byteArray);

            // Send the data
            _netPeer.SendReliableOrdered(writer.AsReadOnlySpan());
        }

        // For "RemoteCallSerializable<T>"
        internal void SendServerRPC(ushort entityId, ushort rpcId, ReadOnlySpan<byte> data)
        {
            // Prepare a NetDataWriter
            NetDataWriter writer = new NetDataWriter();

            // Add packet header and type
            writer.Put(HeaderByte);
            writer.Put(InternalPackets.ClientRPC);

            // Add entity ID and RPC ID
            writer.Put(entityId);
            writer.Put(rpcId);

            // Convert span to byte array and write
            writer.Put(data.ToArray());

            // Send the data
            _netPeer.SendReliableOrdered(writer.AsReadOnlySpan());
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

        internal override void EntityFieldChanged<T>(InternalEntity entity, ushort fieldId, ref T newValue)
        {
            //currently nothing
        }

        protected override unsafe void OnAliveEntityAdded(InternalEntity entity)
        {
            ref var classData = ref ClassDataDict[entity.ClassId];
            fixed (byte* interpDataPtr = classData.ClientInterpolatedNextData(entity),
                   prevDataPtr = classData.ClientInterpolatedPrevData(entity))
            {
                for (int i = 0; i < classData.InterpolatedCount; i++)
                {
                    var field = classData.Fields[i];
                    field.TypeProcessor.WriteTo(entity, field.Offset, interpDataPtr + field.FixedOffset);
                    field.TypeProcessor.WriteTo(entity, field.Offset, prevDataPtr + field.FixedOffset);
                }
            }
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
                    
                    _entitiesToConstruct.Clear();
                    _syncCallsCount = 0;
                    _syncCallsBeforeConstructCount = 0;
                    //read header and decode
                    int decodedBytes;
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
                    {
                        _statesPool.Enqueue(_readyStates.ExtractMin());
                    }

                    foreach (var stateData in _receivedStates.Values)
                    {
                        _statesPool.Enqueue(stateData);
                    }

                    _receivedStates.Clear();

                    _stateA ??= _statesPool.Dequeue();
                    _stateA.Reset(header.Tick);
                    _stateA.Size = header.OriginalLength;
                    _stateA.Data = new byte[header.OriginalLength];
                    InternalPlayerId = header.PlayerId;
                    _localPlayer = new NetPlayer(_netPeer, InternalPlayerId);

                    fixed (byte* stateData = _stateA.Data)
                    {
                        decodedBytes = LZ4Codec.Decode(
                            rawData + sizeof(BaselineDataHeader),
                            inData.Length - sizeof(BaselineDataHeader),
                            stateData,
                            _stateA.Size);
                        if (decodedBytes != header.OriginalLength)
                        {
                            Logger.LogError("Error on decompress");
                            return DeserializeResult.Error;
                        }
                        if (ReadEntityState(stateData, true) == false)
                            return DeserializeResult.Error;
                    }

                    ServerTick = _stateA.Tick;
                    _lastReadyTick = ServerTick;
                    foreach (var controller in GetEntities<HumanControllerLogic>())
                        controller.ClearClientStoredInputs();
                    _storedInputHeaders.Clear();
                    _jitterTimer.Reset();
                    ConstructAndSync(true);
                    _entitiesToConstruct.Clear();
                    Logger.Log($"[CEM] Got baseline sync. Assigned player id: {header.PlayerId}, Original: {decodedBytes}, Tick: {header.Tick}, SendRate: {_serverSendRate}");
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
            float lowestBound = NetworkJitter + PreferredBufferTimeLowest;
            float upperBound = NetworkJitter + PreferredBufferTimeHighest;

            //tune buffer playing speed 
            _lerpTime = Utils.SequenceDiff(_stateB.Tick, _stateA.Tick) * DeltaTimeF;
            _lerpTime *= 1 - GetSpeedMultiplier(LerpBufferTimeLength)*TimeSpeedChangeCoef;

            //tune game prediction and input generation speed
            SpeedMultiplier = GetSpeedMultiplier(_stateB.BufferedInputsCount * DeltaTimeF);
            
            //remove processed inputs
            foreach (var controller in GetEntities<HumanControllerLogic>())
                controller.RemoveClientProcessedInputs(_stateB.ProcessedTick);
            while (_storedInputHeaders.Count > 0 && Utils.SequenceDiff(_stateB.ProcessedTick, _storedInputHeaders.Front().Tick) >= 0)
                _storedInputHeaders.PopFront();
            
            return true;

            float GetSpeedMultiplier(float bufferTime) =>
                Utils.Lerp(-1f, 0f, Utils.InvLerp(lowestBound - TimeSpeedChangeFadeTime, lowestBound, bufferTime)) +
                Utils.Lerp(0f, 1f, Utils.InvLerp(upperBound, upperBound + TimeSpeedChangeFadeTime, bufferTime));
        }

        private unsafe void GoToNextState()
        {
            ushort minimalTick = _stateA.Tick;
            _statesPool.Enqueue(_stateA);
            _stateA = _stateB;
            _stateB = null;
            
            //Logger.Log($"GotoState: IST: {ServerTick}, TST:{_stateA.Tick}");
            fixed (byte* stateData = _stateA.Data)
                if (ReadEntityState(stateData, false) == false)
                    return;
            ConstructAndSync(false, minimalTick);
            
            _timer -= _lerpTime;
            
            //reset owned entities
            foreach (var entity in _predictedEntities)
            {
                ref var classData = ref ClassDataDict[entity.ClassId];
                if(entity.IsRemoteControlled && !classData.HasRemoteRollbackFields)
                    continue;
                entity.OnBeforeRollback();

                fixed (byte* predictedData = classData.ClientPredictedData(entity))
                {
                    for (int i = 0; i < classData.FieldsCount; i++)
                    {
                        ref var field = ref classData.Fields[i];
                        if ((entity.IsRemoteControlled && !field.Flags.HasFlagFast(SyncFlags.AlwaysRollback)) ||
                            field.Flags.HasFlagFast(SyncFlags.NeverRollBack) ||
                            field.Flags.HasFlagFast(SyncFlags.OnlyForOtherPlayers))
                            continue;
                        if (field.FieldType == FieldType.SyncableSyncVar)
                        {
                            var syncableField = RefMagic.RefFieldValue<SyncableField>(entity, field.Offset);
                            field.TypeProcessor.SetFrom(syncableField, field.SyncableSyncVarOffset, predictedData + field.PredictedOffset);
                        }
                        else
                        {
                            field.TypeProcessor.SetFrom(entity, field.Offset, predictedData + field.PredictedOffset);
                        }
                    }
                }
                for (int i = 0; i < classData.SyncableFields.Length; i++)
                    RefMagic.RefFieldValue<SyncableField>(entity, classData.SyncableFields[i].Offset).OnRollback();
                entity.OnRollback();
            }

            //reapply input
            UpdateMode = UpdateMode.PredictionRollback;
            
            for(int cmdNum = 0; cmdNum < _storedInputHeaders.Count; cmdNum++)
            {
                //reapply input data
                var storedInput = _storedInputHeaders[cmdNum];
                _localPlayer.StateATick = storedInput.Header.StateA;
                _localPlayer.StateBTick = storedInput.Header.StateB;
                _localPlayer.LerpTime = storedInput.Header.LerpMsec;
                RollBackTick = storedInput.Tick;
                foreach (var controller in GetEntities<HumanControllerLogic>())
                    controller.ReadStoredInput(cmdNum);
                
                foreach (var entity in AliveEntities)
                {
                    if(entity.IsLocal || !entity.IsLocalControlled)
                        continue;
                    
                    //if new entity set previous interp data from data that was before latest rollback update
                    if (cmdNum == _storedInputHeaders.Count - 1 && _entitiesToConstruct.Contains(entity))
                    {
                        ref var classData = ref ClassDataDict[entity.ClassId];
                        fixed (byte* prevDataPtr = classData.ClientInterpolatedPrevData(entity))
                        {
                            for (int i = 0; i < classData.InterpolatedCount; i++)
                            {
                                ref var field = ref classData.Fields[i];
                                field.TypeProcessor.WriteTo(entity, field.Offset, prevDataPtr + field.FixedOffset);
                            }
                        }
                    }
                    
                    entity.Update();
                }
            }
            UpdateMode = UpdateMode.Normal;
                        
            _entitiesToConstruct.Clear();
                        
            //update local interpolated position
            foreach (var entity in AliveEntities)
            {
                if(entity.IsLocal || !entity.IsLocalControlled)
                    continue;
                
                ref var classData = ref ClassDataDict[entity.ClassId];
                for(int i = 0; i < classData.InterpolatedCount; i++)
                {
                    fixed (byte* currentDataPtr = classData.ClientInterpolatedNextData(entity))
                    {
                        ref var field = ref classData.Fields[i];
                        field.TypeProcessor.WriteTo(entity, field.Offset, currentDataPtr + field.FixedOffset);
                    }
                }
            }
            
            //delete predicted
            while (_spawnPredictedEntities.TryPeek(out var info))
            {
                if (Utils.SequenceDiff(_stateA.ProcessedTick, info.tick) >= 0)
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

        protected override unsafe void OnLogicTick()
        {
            if (_stateB != null)
            {
                ServerTick = Utils.LerpSequence(_stateA.Tick, _stateB.Tick, (float)(_timer/_lerpTime));
                _stateB.ExecuteRpcs(this, _stateA.Tick, false);
            }

            //apply input
            var humanControllers = GetEntities<HumanControllerLogic>();
            if (humanControllers.Count > 0)
            {
                _storedInputHeaders.PushBack(new InputInfo(_tick, new InputPacketHeader
                {
                    StateA = _stateA.Tick,
                    StateB = RawTargetServerTick,
                    LerpMsec = _logicLerpMsec
                }));
                foreach (var controller in humanControllers)
                {
                    controller.ApplyPendingInput();
                }
            }

            //local only and UpdateOnClient
            foreach (var entity in AliveEntities)
            {
                ref var classData = ref ClassDataDict[entity.ClassId];
                if (entity.IsLocal || entity.IsLocalControlled)
                {
                    //save data for interpolation before update
                    fixed (byte* currentDataPtr = classData.ClientInterpolatedNextData(entity),
                           prevDataPtr = classData.ClientInterpolatedPrevData(entity))
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
                else if(classData.Flags.HasFlagFast(EntityFlags.UpdateOnClient))
                {
                    entity.Update();
                }
            }

            if (NetworkJitter > _jitterMiddle)
                NetworkJitter -= DeltaTimeF * 0.1f;
        }

        /// <summary>
        /// Update method, call this every frame
        /// </summary>
        public override unsafe void Update()
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
            
            if (PreloadNextState())
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
                    //remote interpolation
                    _logicLerpMsec = (float)(_timer/_lerpTime);
                    for(int i = 0; i < _stateB.InterpolatedCachesCount; i++)
                    {
                        ref var interpolatedCache = ref _stateB.InterpolatedCaches[i];
                        fixed (byte* initialDataPtr = interpolatedCache.Entity.ClassData.ClientInterpolatedNextData(interpolatedCache.Entity), nextDataPtr = _stateB.Data)
                            interpolatedCache.TypeProcessor.SetInterpolation(
                                interpolatedCache.Entity, 
                                interpolatedCache.FieldOffset,
                                initialDataPtr + interpolatedCache.FieldFixedOffset,
                                nextDataPtr + interpolatedCache.StateReaderOffset, 
                                _logicLerpMsec);
                    }
                }
            }

            //local interpolation
            float localLerpT = LerpFactor;
            foreach (var entity in AliveEntities)
            {
                if (!entity.IsLocalControlled && !entity.IsLocal)
                    continue;
                
                ref var classData = ref ClassDataDict[entity.ClassId];
                fixed (byte* currentDataPtr = classData.ClientInterpolatedNextData(entity), prevDataPtr = classData.ClientInterpolatedPrevData(entity))
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
            
            //local only and UpdateOnClient
            foreach (var entity in AliveEntities)
            {
                entity.VisualUpdate();
            }
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
                        LerpMsec = _logicLerpMsec, 
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

            int maxDeltaSize = 0;
            int maxInputSize = 0;
            foreach (var humanControllerLogic in GetEntities<HumanControllerLogic>())
            {
                maxDeltaSize += humanControllerLogic.MaxInputDeltaSize;
                maxInputSize += humanControllerLogic.InputSize + InputPacketHeader.Size;
            }

            if (offset + maxInputSize > maxSinglePacketSize)
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
                    if(prevInputIndex >= 0)//make delta
                    {
                        //overflow
                        if (offset + InputPacketHeader.Size + maxDeltaSize > maxSinglePacketSize)
                        {
                            prevInputIndex = -1;
                            *(ushort*)(sendBuffer + 2) = currentTick;
                            _netPeer.SendUnreliable(new ReadOnlySpan<byte>(sendBuffer, offset));
                            offset = 4;
                            currentTick += tickIndex;
                            tickIndex = 0;
                        }
                        else
                        {
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
                        }
                    }
                    if (prevInputIndex == -1) //first full input
                    {
                        //write header
                        *(InputPacketHeader*)(sendBuffer + offset) = _storedInputHeaders[i].Header;
                        offset += InputPacketHeader.Size;
                        //write data
                        foreach (var controller in GetEntities<HumanControllerLogic>())
                        {
                            controller.WriteStoredInput(i, new Span<byte>(sendBuffer + offset, controller.InputSize));
                            offset += controller.InputSize;
                        }
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

        private void ExecuteSyncCalls(SyncCallInfo[] callInfos, ref int count)
        {
            for (int i = 0; i < count; i++)
            {
                try
                {
                    callInfos[i].Execute(_stateA);
                }
                catch (Exception e)
                {
                    Logger.LogError($"OnChange error in user code. Entity: {callInfos[i].Entity}. Error: {e}");
                }
            }
            count = 0;
        }

        private void ConstructAndSync(bool firstSync, ushort minimalTick = 0)
        {
            //execute all previous rpcs
            ServerTick = _stateA.Tick;
            
            //execute syncable fields first
            _stateA.ExecuteSyncableRpcs(this, minimalTick, firstSync);
            
            //Make OnChangeCalls before construct
            ExecuteSyncCalls(_syncCallsBeforeConstruct, ref _syncCallsBeforeConstructCount);

            //Call construct methods
            foreach(var entity in _entitiesToConstruct)
                ConstructEntity(entity);
            
            //Make OnChangeCalls after construct
            ExecuteSyncCalls(_syncCalls, ref _syncCallsCount);
            
            //execute entity rpcs
            _stateA.ExecuteRpcs(this, minimalTick, firstSync);
            
            foreach (var lagCompensatedEntity in LagCompensatedEntities)
                ClassDataDict[lagCompensatedEntity.ClassId].WriteHistory(lagCompensatedEntity, ServerTick);
        }
        
        private unsafe bool ReadEntityState(byte* rawData, bool fistSync)
        {
            var emptyClassData = new EntityClassData();
            _changedEntities.Clear();
            
            for (int readerPosition = 0; readerPosition < _stateA.Size;)
            {
                bool fullSync = true;
                int endPos = 0;
                InternalEntity entity;
                ref var classData = ref emptyClassData;
                bool writeInterpolationData;
                
                if (!fistSync) //diff data
                {
                    ushort fullSyncAndTotalSize = *(ushort*)(rawData + readerPosition);
                    fullSync = (fullSyncAndTotalSize & 1) == 1;
                    endPos = readerPosition + (fullSyncAndTotalSize >> 1);
                    readerPosition += sizeof(ushort);
                }
                
                if (fullSync)
                {
                    var entityDataHeader = *(EntityDataHeader*)(rawData + readerPosition);
                    readerPosition += sizeof(EntityDataHeader);
                    if (!IsEntityIdValid(entityDataHeader.Id))
                        return false;
                    entity = EntitiesDict[entityDataHeader.Id];
                    
                    //Logger.Log($"[CEM] ReadBaseline Entity: {entityId} pos: {bytesRead}");
                    //remove old entity
                    if (entity != null && entity.Version != entityDataHeader.Version)
                    {
                        //this can be only on logics (not on singletons)
                        Logger.Log($"[CEM] Replace entity by new: {entityDataHeader.Version}");
                        entity.DestroyInternal();
                        RemoveEntity(entity);
                        entity = null;
                    } 
                    if (entity == null) //create new
                    {
                        classData = ref ClassDataDict[entityDataHeader.ClassId];
                        entity = AddEntity<InternalEntity>(new EntityParams(entityDataHeader, this, classData.AllocateDataCache()));
                     
                        if (classData.PredictedSize > 0 || classData.SyncableFields.Length > 0)
                        {
                            _predictedEntities.Add(entity);
                            //Logger.Log($"Add predicted: {entity.GetType()}");
                        }
                        _entitiesToConstruct.Add(entity);
                        writeInterpolationData = true;
                    }
                    else //update "old"
                    {
                        classData = ref entity.ClassData;
                        writeInterpolationData = entity.IsRemoteControlled;
                    }
                }
                else //diff sync
                {
                    ushort entityId = *(ushort*)(rawData + readerPosition);
                    readerPosition += sizeof(ushort);
                    if (!IsEntityIdValid(entityId))
                        return false;
                    entity = EntitiesDict[entityId];
                    if(entity != null)
                    {
                        classData = ref entity.ClassData;
                        writeInterpolationData = entity.IsRemoteControlled;
                        readerPosition += classData.FieldsFlagsSize;
                    }
                    else //entity null -> and diff sync -> skip
                    {
                        readerPosition = endPos;
                        continue;
                    }
                }

                _changedEntities.Add(entity);
                
                Utils.ResizeOrCreate(ref _syncCalls, _syncCallsCount + classData.FieldsCount);
                Utils.ResizeOrCreate(ref _syncCallsBeforeConstruct, _syncCallsBeforeConstructCount + classData.FieldsCount);
                
                int fieldsFlagsOffset = readerPosition - classData.FieldsFlagsSize;
                fixed (byte* interpDataPtr = classData.ClientInterpolatedNextData(entity), predictedData = classData.ClientPredictedData(entity))
                    for (int i = 0; i < classData.FieldsCount; i++)
                    {
                        if (!fullSync && !Utils.IsBitSet(rawData + fieldsFlagsOffset, i))
                            continue;
                        ref var field = ref classData.Fields[i];
                        byte* readDataPtr = rawData + readerPosition;
                        if (field.IsPredicted)
                            RefMagic.CopyBlock(predictedData + field.PredictedOffset, readDataPtr, field.Size);
                        if (field.FieldType == FieldType.SyncableSyncVar)
                        {
                            var syncableField = RefMagic.RefFieldValue<SyncableField>(entity, field.Offset);
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
                                {
                                    if (field.OnSyncExecutionOrder == OnSyncExecutionOrder.BeforeConstruct)
                                        _syncCallsBeforeConstruct[_syncCallsBeforeConstructCount++] = new SyncCallInfo(field.OnSync, entity, readerPosition);
                                    else //call on sync immediately
                                        _syncCalls[_syncCallsCount++] = new SyncCallInfo(field.OnSync, entity, readerPosition);
                                }
                            }
                            else
                            {
                                field.TypeProcessor.SetFrom(entity, field.Offset, readDataPtr);
                            }
                        }
                        //Logger.Log($"E {entity.Id} Field updated: {field.Name}");
                        readerPosition += field.IntSize;
                    }
                
                if (fullSync)
                {
                    _stateA.ReadRPCs(rawData, ref readerPosition, new EntitySharedReference(entity.Id, entity.Version), classData);
                    continue;
                }
                readerPosition = endPos;
            }
            
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
            return true;

            bool IsEntityIdValid(ushort id)
            {
                if (id == InvalidEntityId || id >= MaxSyncedEntityCount)
                {
                    Logger.LogError($"Bad data (id > {MaxSyncedEntityCount} or Id == 0) Id: {id}");
                    return false;
                }
                return true;
            }
        }
    }
}