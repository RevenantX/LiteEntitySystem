using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using K4os.Compression.LZ4;
using LiteEntitySystem.Internal;
using LiteEntitySystem.Transport;
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
        /// Current rollback tick (valid only in Rollback state)
        /// </summary>
        public ushort RollBackTick { get; private set; }

        /// <summary>
        /// Tick of currently executing rpc (check only in client RPC methods)
        /// </summary>
        public ushort CurrentRPCTick { get; internal set; }

        /// <summary>
        /// Is rpc currently executing
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
        public int StoredCommands => _inputCommands.Count;
        
        /// <summary>
        /// Player tick processed by server
        /// </summary>
        public ushort LastProcessedTick => _stateA?.ProcessedTick ?? 0;

        /// <summary>
        /// Last received player tick by server
        /// </summary>
        public ushort LastReceivedTick => _stateA?.LastReceivedTick ?? 0;

        /// <summary>
        /// Send rate of server
        /// </summary>
        public ServerSendRate ServerSendRate => _serverSendRate;
        
        /// <summary>
        /// States count in interpolation buffer
        /// </summary>
        public int LerpBufferCount => _readyStates.Count;

        /// <summary>
        /// Client network peer
        /// </summary>
        public AbstractNetPeer NetPeer => _netPeer;
        
        private const int InputBufferSize = 128;

        struct InputCommand
        {
            public ushort Tick;
            public byte[] Data;

            public InputCommand(ushort tick, byte[] data)
            {
                Tick = tick;
                Data = data;
            }
        }

        private readonly EntityFilter<InternalEntity> _predictedEntityFilter = new();
        private readonly AbstractNetPeer _netPeer;
        private readonly Queue<ServerStateData> _statesPool = new(MaxSavedStateDiff);
        private readonly Dictionary<ushort, ServerStateData> _receivedStates = new();
        private readonly SequenceBinaryHeap<ServerStateData> _readyStates = new(MaxSavedStateDiff);
        private readonly Queue<InputCommand> _inputCommands = new (InputBufferSize);
        private readonly Queue<byte[]> _inputPool = new (InputBufferSize);
        private readonly Queue<(ushort id, EntityLogic entity)> _spawnPredictedEntities = new ();
        private readonly byte[][] _interpolatedInitialData = new byte[MaxEntityCount][];
        private readonly byte[][] _interpolatePrevData = new byte[MaxEntityCount][];
        private readonly byte[][] _predictedEntitiesData = new byte[MaxSyncedEntityCount][];
        private readonly byte[] _sendBuffer = new byte[NetConstants.MaxPacketSize];

        private ServerSendRate _serverSendRate;
        private ServerStateData _stateA;
        private ServerStateData _stateB;
        private float _lerpTime;
        private double _timer;
        private bool _isSyncReceived;

        private readonly struct SyncCallInfo
        {
            public readonly MethodCallDelegate OnSync;
            public readonly InternalEntity Entity;
            public readonly int PrevDataPos;

            public SyncCallInfo(MethodCallDelegate onSync, InternalEntity entity, int prevDataPos)
            {
                OnSync = onSync;
                Entity = entity;
                PrevDataPos = prevDataPos;
            }

            public void Execute(ServerStateData state) => OnSync(Entity, new ReadOnlySpan<byte>(state.Data, PrevDataPos, state.Size-PrevDataPos));
        }
        private SyncCallInfo[] _syncCalls;
        private int _syncCallsCount;
        
        private InternalEntity[] _entitiesToConstruct = new InternalEntity[64];
        private int _entitiesToConstructCount;
        private ushort _lastReceivedInputTick;
        private float _logicLerpMsec;
        private ushort _lastReadyTick;

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
        /// <param name="netPeer">Local AbstractPeer</param>
        /// <param name="headerByte">Header byte that will be used for packets (to distinguish entity system packets)</param>
        /// <param name="framesPerSecond">Fixed framerate of game logic</param>
        public ClientEntityManager(
            EntityTypesMap typesMap, 
            InputProcessor inputProcessor, 
            AbstractNetPeer netPeer, 
            byte headerByte, 
            byte framesPerSecond) : base(typesMap, inputProcessor, NetworkMode.Client, framesPerSecond, headerByte)
        {
            _netPeer = netPeer;
            _sendBuffer[0] = headerByte;
            _sendBuffer[1] = InternalPackets.ClientInput;

            AliveEntities.SubscribeToConstructed(OnAliveConstructed, false);

            for (int i = 0; i < MaxSavedStateDiff; i++)
            {
                _statesPool.Enqueue(new ServerStateData());
            }
        }
        
        /// <summary>
        /// Simplified constructor
        /// </summary>
        /// <param name="typesMap">EntityTypesMap with registered entity types</param>
        /// <param name="netPeer">Local AbstractPeer</param>
        /// <param name="headerByte">Header byte that will be used for packets (to distinguish entity system packets)</param>
        /// <param name="framesPerSecond">Fixed framerate of game logic</param>
        /// <typeparam name="TInput">Main input packet type</typeparam>
        public static ClientEntityManager Create<TInput>(
            EntityTypesMap typesMap, 
            AbstractNetPeer netPeer, 
            byte headerByte, 
            byte framesPerSecond) where TInput : unmanaged
        {
            return new ClientEntityManager(
                typesMap, 
                new InputProcessor<TInput>(),
                netPeer,
                headerByte,
                framesPerSecond);
        }

        internal override void RemoveEntity(InternalEntity e)
        {
            base.RemoveEntity(e);
            if (_predictedEntityFilter.Contains(e))
            {
                _predictedEntitiesData[e.Id] = null;
                _predictedEntityFilter.Remove(e);
                _predictedEntityFilter.Refresh();
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

            fixed (byte* interpDataPtr = _interpolatedInitialData[entity.Id], prevDataPtr = _interpolatePrevData[entity.Id])
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
                    if (inData.Length < sizeof(BaselineDataHeader) + 2)
                        return DeserializeResult.Error;
                    _entitiesToConstructCount = 0;
                    _syncCallsCount = 0;
                    //read header and decode
                    int decodedBytes;
                    var header = *(BaselineDataHeader*)rawData;
                    if (header.OriginalLength <= 0)
                        return DeserializeResult.Error;
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
                    _inputCommands.Clear();
                    _isSyncReceived = true;
                    _jitterTimer.Restart();
                    ConstructAndSync(true);
                    Logger.Log($"[CEM] Got baseline sync. Assigned player id: {header.PlayerId}, Original: {decodedBytes}, Tick: {header.Tick}, SendRate: {_serverSendRate}");
                }
                else
                {
                    if (inData.Length < sizeof(DiffPartHeader) + 2)
                        return DeserializeResult.Error;
                    var diffHeader = *(DiffPartHeader*)rawData;
                    int tickDifference = Utils.SequenceDiff(diffHeader.Tick, _lastReadyTick);
                    if (tickDifference <= 0)
                    {
                        //old state
                        return DeserializeResult.Done;
                    }

                    //sample jitter
                    _jitterSamples[_jitterSampleIdx] = _jitterTimer.ElapsedMilliseconds / 1000f;
                    _jitterSampleIdx = (_jitterSampleIdx + 1) % _jitterSamples.Length;
                    //reset timer
                    _jitterTimer.Reset();

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
            
            _stateB = _readyStates.ExtractMin();
            _stateB.Preload(EntitiesDict);
            //Logger.Log($"Preload A: {_stateA.Tick}, B: {_stateB.Tick}");
            _lerpTime = 
                Utils.SequenceDiff(_stateB.Tick, _stateA.Tick) * DeltaTimeF *
                (1f - (_readyStates.Count + 1 - _adaptiveMiddlePoint) * 0.02f);

            //remove processed inputs
            while (_inputCommands.Count > 0 && Utils.SequenceDiff(_stateB.ProcessedTick, _inputCommands.Peek().Tick) >= 0)
                _inputPool.Enqueue(_inputCommands.Dequeue().Data);

            return true;
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
            foreach (var entity in _predictedEntityFilter)
            {
                ref var classData = ref entity.GetClassData();
                if(entity.IsRemoteControlled && !classData.HasRemoteRollbackFields)
                    continue;

                fixed (byte* predictedData = _predictedEntitiesData[entity.Id])
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
            }

            //reapply input
            UpdateMode = UpdateMode.PredictionRollback;
            foreach (var inputCommand in _inputCommands)
            {
                //reapply input data
                fixed (byte* rawInputData = inputCommand.Data)
                {
                    var header = *(InputPacketHeader*)rawInputData;
                    _localPlayer.StateATick = header.StateA;
                    _localPlayer.StateBTick = header.StateB;
                    _localPlayer.LerpTime = header.LerpMsec;
                }
                RollBackTick = inputCommand.Tick;
                InputProcessor.ReadInput(
                    this,
                    _localPlayer.Id, 
                    new ReadOnlySpan<byte>(
                        inputCommand.Data,
                        InputPacketHeader.Size, 
                        inputCommand.Data.Length-InputPacketHeader.Size));
                
                foreach (var entity in AliveEntities)
                {
                    if(entity.IsLocal || !entity.IsLocalControlled)
                        continue;
                    entity.Update();
                }
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
                if (Utils.SequenceDiff(_stateA.ProcessedTick, info.id) >= 0)
                {
                    _spawnPredictedEntities.Dequeue();
                    info.entity.DestroyInternal();
                }
                else
                {
                    break;
                }
            }
        }

        protected override unsafe void OnLogicTick()
        {
            if (_stateB != null)
            {
                ServerTick = Utils.LerpSequence(_stateA.Tick, _stateB.Tick, (float)(_timer/_lerpTime));
                _stateB.ExecuteRpcs(this, _stateA.Tick, false);
            }
            
            //remove overflow
            while(_inputCommands.Count >= InputBufferSize)
                _inputPool.Enqueue(_inputCommands.Dequeue().Data);
            
            if (!_inputPool.TryDequeue(out byte[] inputWriter) || inputWriter.Length < InputProcessor.InputSizeWithHeader)
                inputWriter = new byte[InputProcessor.InputSizeWithHeader];

            //generate input
            var inputHeader = new InputPacketHeader
            {
                StateA = _stateA.Tick,
                StateB = RawTargetServerTick,
                LerpMsec = _logicLerpMsec
            };
            fixed(byte* writerData = inputWriter)
                *(InputPacketHeader*)writerData = inputHeader;
            InputProcessor.GenerateAndWriteInput(this, _localPlayer.Id, inputWriter, InputPacketHeader.Size);

            //read
            InputProcessor.ReadInput(
                this,
                _localPlayer.Id,
                new ReadOnlySpan<byte>(inputWriter, InputPacketHeader.Size, inputWriter.Length - InputPacketHeader.Size));
            _inputCommands.Enqueue(new InputCommand(_tick, inputWriter));

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
            
            float rtt = _netPeer.RoundTripTimeMs;
            float totalInputTime = _inputCommands.Count * DeltaTimeF * 1000f;
            if (totalInputTime > rtt + DeltaTimeF * 5000f)
            {
                SlowDownEnabled = true;
            }
            else if (totalInputTime < rtt + DeltaTimeF * 3000f)
            {
                SlowDownEnabled = false;
            }
            base.Update();

            if (PreloadNextState())
            {
                _timer += VisualDeltaTime;
                while(_timer >= _lerpTime)
                {
                    GoToNextState();
                    if (!PreloadNextState())
                        break;
                }
            }

            if (_stateB != null)
            {
                //remote interpolation
                _logicLerpMsec = (float)(_timer/_lerpTime);
                for(int i = 0; i < _stateB.InterpolatedCachesCount; i++)
                {
                    ref var interpolatedCache = ref _stateB.InterpolatedCaches[i];
                    fixed (byte* initialDataPtr = _interpolatedInitialData[interpolatedCache.Entity.Id], nextDataPtr = _stateB.Data)
                        interpolatedCache.TypeProcessor.SetInterpolation(
                            interpolatedCache.Entity, 
                            interpolatedCache.FieldOffset,
                            initialDataPtr + interpolatedCache.FieldFixedOffset,
                            nextDataPtr + interpolatedCache.StateReaderOffset, 
                            _logicLerpMsec);
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
                int maxSinglePacketSize = _netPeer.GetMaxUnreliablePacketSize();
                
                fixed (byte* sendBuffer = _sendBuffer)
                {
                    ushort currentTick = _inputCommands.Peek().Tick;
                    ushort tickIndex = 0;
                    byte[] prevCommand = null;
                    
                    //Logger.Log($"SendingCommands start {_tick}");
                    foreach (var inputCommand in _inputCommands)
                    {
                        if (Utils.SequenceDiff(currentTick, _lastReceivedInputTick) <= 0)
                        {
                            currentTick++;
                            continue;
                        }
                        if(prevCommand != null)//make delta
                        {
                            //overflow
                            if (offset + InputPacketHeader.Size + InputProcessor.MaxDeltaSize > maxSinglePacketSize)
                            {
                                prevCommand = null;
                                *(ushort*)(sendBuffer + 2) = currentTick;
                                _netPeer.SendUnreliable(new ReadOnlySpan<byte>(_sendBuffer, 0, offset));
                                offset = 4;
                                currentTick += tickIndex;
                                tickIndex = 0;
                            }
                            else
                            {
                                //put header
                                fixed (byte* inputData = inputCommand.Data)
                                    RefMagic.CopyBlock(sendBuffer + offset, inputData, (uint)InputPacketHeader.Size);
                                offset += InputPacketHeader.Size;
                                //put delta
                                offset += InputProcessor.DeltaEncode(
                                    new ReadOnlySpan<byte>(prevCommand, InputPacketHeader.Size, prevCommand.Length - InputPacketHeader.Size), 
                                    new ReadOnlySpan<byte>(inputCommand.Data, InputPacketHeader.Size, inputCommand.Data.Length - InputPacketHeader.Size), 
                                    new Span<byte>(sendBuffer + offset, InputProcessor.MaxDeltaSize));
                            }
                        }
                        if (prevCommand == null) //first full input
                        {
                            //put data
                            fixed (byte* rawInputCommand = inputCommand.Data)
                                RefMagic.CopyBlock(sendBuffer + offset, rawInputCommand, (uint)InputProcessor.InputSizeWithHeader);
                            offset += InputProcessor.InputSizeWithHeader;
                        }
                        prevCommand = inputCommand.Data;
                        tickIndex++;
                        if (tickIndex == ServerEntityManager.MaxStoredInputs)
                            break;
                    }
                    *(ushort*)(sendBuffer + 2) = currentTick;
                    _netPeer.SendUnreliable(new ReadOnlySpan<byte>(_sendBuffer, 0, offset));
                    _netPeer.TriggerSend();
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

        private void ConstructAndSync(bool firstSync, ushort minimalTick = 0)
        {
            //execute all previous rpcs
            ServerTick = _stateA.Tick;
            
            //execute syncable fields first
            _stateA.ExecuteSyncFieldRpcs(this, minimalTick, firstSync);

            //Call construct methods
            for (int i = 0; i < _entitiesToConstructCount; i++)
                ConstructEntity(_entitiesToConstruct[i]);
            _entitiesToConstructCount = 0;

            //Make OnSyncCalls
            for (int i = 0; i < _syncCallsCount; i++)
                _syncCalls[i].Execute(_stateA);
            _syncCallsCount = 0;
            
            //execute entity rpcs
            _stateA.ExecuteRpcs(this, minimalTick, firstSync);
            
            foreach (var lagCompensatedEntity in LagCompensatedEntities)
                lagCompensatedEntity.WriteHistory(ServerTick);
        }

        private unsafe bool ReadEntityState(byte* rawData, bool fistSync)
        {
            for (int readerPosition = 0; readerPosition < _stateA.Size;)
            {
                bool writeInterpolationData;
                bool fullSync = true;
                int endPos = 0;
                if (!fistSync) //diff data
                {
                    ushort fullSyncAndTotalSize = *(ushort*)(rawData + readerPosition);
                    fullSync = (fullSyncAndTotalSize & 1) == 1;
                    endPos = readerPosition + (fullSyncAndTotalSize >> 1);
                    readerPosition += sizeof(ushort);
                }
                ushort entityId = *(ushort*)(rawData + readerPosition);
                readerPosition += sizeof(ushort);
                if (entityId == InvalidEntityId || entityId >= MaxSyncedEntityCount)
                {
                    Logger.LogError($"Bad data (id > {MaxSyncedEntityCount} or Id == 0) Id: {entityId}");
                    return false;
                }
                var entity = EntitiesDict[entityId];
                ref var classData = ref Unsafe.AsRef<EntityClassData>(null);
                if (fullSync)
                {
                    //Logger.Log($"[CEM] ReadBaseline Entity: {entityId} pos: {bytesRead}");
                    byte version = rawData[readerPosition];
                    //remove old entity
                    if (entity != null && entity.Version != version)
                    {
                        //this can be only on logics (not on singletons)
                        Logger.Log($"[CEM] Replace entity by new: {version}");
                        entity.DestroyInternal();
                        entity = null;
                    } 
                    if (entity == null) //create new
                    {
                        ushort classId = *(ushort*)(rawData + readerPosition + 1);
                        entity = AddEntity(new EntityParams(classId, entityId, version, this));
                        classData = ref entity.GetClassData();
                        if (classData.PredictedSize > 0)
                        {
                            Utils.ResizeOrCreate(ref _predictedEntitiesData[entity.Id], classData.PredictedSize);
                            _predictedEntityFilter.Add(entity);
                        }
                        if (classData.InterpolatedFieldsSize > 0)
                        {
                            Utils.ResizeOrCreate(ref _interpolatedInitialData[entity.Id], classData.InterpolatedFieldsSize);
                        }
                        Utils.ResizeIfFull(ref _entitiesToConstruct, _entitiesToConstructCount);
                        _entitiesToConstruct[_entitiesToConstructCount++] = entity;
                        writeInterpolationData = true;
                    }
                    else //update "old"
                    {
                        classData = ref entity.GetClassData();
                        writeInterpolationData = entity.IsRemoteControlled;
                    }
                    readerPosition += 3;
                }
                else if(entity != null) //diff sync
                {
                    classData = ref entity.GetClassData();
                    writeInterpolationData = entity.IsRemoteControlled;
                    readerPosition += classData.FieldsFlagsSize;
                }
                else //entity null -> and diff sync -> skip
                {
                    readerPosition = endPos;
                    continue;
                }
                Utils.ResizeOrCreate(ref _syncCalls, _syncCallsCount + classData.FieldsCount);
                int fieldsFlagsOffset = readerPosition - classData.FieldsFlagsSize;
                fixed (byte* interpDataPtr = _interpolatedInitialData[entity.Id], predictedData = _predictedEntitiesData[entity.Id])
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
                                    _syncCalls[_syncCallsCount++] = new SyncCallInfo(field.OnSync, entity, readerPosition);
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
            return true;
        }
    }
}