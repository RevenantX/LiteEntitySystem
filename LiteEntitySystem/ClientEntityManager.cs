using K4os.Compression.LZ4;
using LiteEntitySystem.Collections;
using LiteEntitySystem.Internal;
using LiteEntitySystem.Transport;
using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;

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
        /// Preferred input and incoming states buffer length in seconds lowest bound
        /// Buffer automatically increases to Jitter time + PreferredBufferTimeLowest
        /// </summary>
        public float PreferredBufferTimeLowest = 0.010f;

        /// <summary>
        /// Preferred input and incoming states buffer length in seconds lowest bound
        /// Buffer automatically decreases to Jitter time + PreferredBufferTimeHighest
        /// </summary>
        public float PreferredBufferTimeHighest = 0.100f;

        private const int InputBufferSize = 128;
        private const float TimeSpeedChangeFadeTime = 0.1f;
        private const float MaxJitter = 0.2f;

        private struct InputCommand
        {
            public ushort Tick;
            public byte[] Data;

            public InputCommand(ushort tick, byte[] data)
            {
                Tick = tick;
                Data = data;
            }
        }

        private readonly AVLTree<InternalEntity> _predictedEntityFilter = new();
        private readonly AbstractNetPeer _netPeer;
        private readonly Queue<ServerStateData> _statesPool = new(MaxSavedStateDiff);
        private readonly Dictionary<ushort, ServerStateData> _receivedStates = new();
        private readonly SequenceBinaryHeap<ServerStateData> _readyStates = new(MaxSavedStateDiff);
        private readonly Queue<InputCommand> _inputCommands = new(InputBufferSize);
        private readonly Queue<byte[]> _inputPool = new(InputBufferSize);
        private readonly Queue<(ushort tick, EntityLogic entity)> _spawnPredictedEntities = new();
        private readonly byte[] _sendBuffer = new byte[NetConstants.MaxPacketSize];

        private ServerSendRate _serverSendRate;
        private ServerStateData _stateA;
        private ServerStateData _stateB;
        private float _lerpTime;
        private double _timer;
        private bool _isSyncReceived;

        private readonly struct SyncCallInfo
        {
            public readonly InternalEntity Entity;

            private readonly OnSyncCallDelegate _onSync;
            private readonly int _prevDataPos;

            public SyncCallInfo(OnSyncCallDelegate onSync, InternalEntity entity, int prevDataPos)
            {
                _onSync = onSync;
                Entity = entity;
                _prevDataPos = prevDataPos;
            }

            public void Execute(ServerStateData state) => _onSync(Entity, new ReadOnlySpan<byte>(state.Data, _prevDataPos, state.Size - _prevDataPos));
        }

        private SyncCallInfo[] _syncCalls;
        private int _syncCallsCount;
        private SyncCallInfo[] _syncCallsBeforeConstruct;
        private int _syncCallsBeforeConstructCount;

        private InternalEntity[] _entitiesToConstruct = new InternalEntity[64];
        private int _entitiesToConstructCount;
        private ushort _lastReceivedInputTick;
        private float _logicLerpMsec;
        private ushort _lastReadyTick;

        //time manipulation
        private readonly float[] _jitterSamples = new float[10];

        private int _jitterSampleIdx;
        private readonly Stopwatch _jitterTimer = new();
        private float _jitterMiddle;

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
            foreach (var controller in GetControllers<T>())
                if (controller.InternalOwnerId.Value == _localPlayer.Id)
                    return controller;
            return null;
        }

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
            byte framesPerSecond) where TInput : unmanaged =>
            new(typesMap,
                new InputProcessor<TInput>(),
                netPeer,
                headerByte,
                framesPerSecond);

        internal override void RemoveEntity(InternalEntity e)
        {
            base.RemoveEntity(e);
            _predictedEntityFilter.Remove(e);
        }

        private unsafe void OnAliveConstructed(InternalEntity entity)
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
                    _entitiesToConstructCount = 0;
                    _syncCallsCount = 0;
                    _syncCallsBeforeConstructCount = 0;
                    //read header and decode
                    int decodedBytes;
                    var header = *(BaselineDataHeader*)rawData;
                    if (header.OriginalLength < 0)
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
                    _jitterTimer.Reset();
                    ConstructAndSync(true);
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
                    _jitterSamples[_jitterSampleIdx] = _jitterTimer.ElapsedMilliseconds / 1000f;
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
            _jitterMiddle = 0f;
            for (int i = 0; i < _jitterSamples.Length - 1; i++)
            {
                float jitter = Math.Abs(_jitterSamples[i] - _jitterSamples[i + 1]);
                if (jitter > NetworkJitter)
                    NetworkJitter = jitter;
                _jitterMiddle += jitter;
            }
            _jitterMiddle /= _jitterSamples.Length;

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
            _lerpTime *= 1 - GetSpeedMultiplier(LerpBufferTimeLength) * TimeSpeedChangeCoef;

            //tune game prediction and input generation speed
            SpeedMultiplier = GetSpeedMultiplier(_stateB.BufferedInputsCount * DeltaTimeF);

            //remove processed inputs
            while (_inputCommands.Count > 0 && Utils.SequenceDiff(_stateB.ProcessedTick, _inputCommands.Peek().Tick) >= 0)
                _inputPool.Enqueue(_inputCommands.Dequeue().Data);

            return true;

            float GetSpeedMultiplier(float bufferTime)
            {
                return Utils.Lerp(-1f, 0f, Utils.InvLerp(lowestBound - TimeSpeedChangeFadeTime, lowestBound, bufferTime)) +
                       Utils.Lerp(0f, 1f, Utils.InvLerp(upperBound, upperBound + TimeSpeedChangeFadeTime, bufferTime));
            }
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
                ref readonly var classData = ref entity.ClassData;
                if (entity.IsRemoteControlled && !classData.HasRemoteRollbackFields)
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
                        inputCommand.Data.Length - InputPacketHeader.Size));

                foreach (var entity in AliveEntities)
                {
                    if (entity.IsLocal || !entity.IsLocalControlled)
                        continue;
                    entity.Update();
                }
            }
            UpdateMode = UpdateMode.Normal;

            //update local interpolated position
            foreach (var entity in AliveEntities)
            {
                if (entity.IsLocal || !entity.IsLocalControlled)
                    continue;

                ref readonly var classData = ref entity.ClassData;
                for (int i = 0; i < classData.InterpolatedCount; i++)
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
                    Logger.Log("Delete predicted");
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
                ServerTick = Utils.LerpSequence(_stateA.Tick, _stateB.Tick, (float)(_timer / _lerpTime));
                _stateB.ExecuteRpcs(this, _stateA.Tick, false);
            }

            //remove overflow
            while (_inputCommands.Count >= InputBufferSize)
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
            fixed (byte* writerData = inputWriter)
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
                    fixed (byte* currentDataPtr = classData.ClientInterpolatedNextData(entity),
                           prevDataPtr = classData.ClientInterpolatedPrevData(entity))
                    {
                        //restore previous
                        for (int i = 0; i < classData.InterpolatedCount; i++)
                        {
                            var field = classData.Fields[i];
                            field.TypeProcessor.SetFrom(entity, field.Offset, currentDataPtr + field.FixedOffset);
                        }

                        //update
                        entity.Update();

                        //save current
                        RefMagic.CopyBlock(prevDataPtr, currentDataPtr, (uint)classData.InterpolatedFieldsSize);
                        for (int i = 0; i < classData.InterpolatedCount; i++)
                        {
                            var field = classData.Fields[i];
                            field.TypeProcessor.WriteTo(entity, field.Offset, currentDataPtr + field.FixedOffset);
                        }
                    }
                }
                else if (classData.Flags.HasFlagFast(EntityFlags.UpdateOnClient))
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
            if (!_isSyncReceived)
                return;

            //logic update
            ushort prevTick = _tick;

            base.Update();

            if (PreloadNextState())
            {
                _timer += VisualDeltaTime;
                while (_timer >= _lerpTime)
                {
                    GoToNextState();
                    if (!PreloadNextState())
                        break;
                }
            }

            if (_stateB != null)
            {
                //remote interpolation
                _logicLerpMsec = (float)(_timer / _lerpTime);
                for (int i = 0; i < _stateB.InterpolatedCachesCount; i++)
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

            //local interpolation
            float localLerpT = LerpFactor;
            foreach (var entity in AliveEntities)
            {
                if (!entity.IsLocalControlled && !entity.IsLocal)
                    continue;

                ref readonly var classData = ref entity.ClassData;
                fixed (byte* currentDataPtr = classData.ClientInterpolatedNextData(entity), prevDataPtr = classData.ClientInterpolatedPrevData(entity))
                {
                    for (int i = 0; i < classData.InterpolatedCount; i++)
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
                        if (prevCommand != null)//make delta
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

        internal void AddPredictedInfo(EntityLogic e) =>
            _spawnPredictedEntities.Enqueue((_tick, e));

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
            Array.Sort(_entitiesToConstruct, 0, _entitiesToConstructCount, EntityComparer.Instance);
            for (int i = 0; i < _entitiesToConstructCount; i++)
                ConstructEntity(_entitiesToConstruct[i]);
            _entitiesToConstructCount = 0;

            //Make OnChangeCalls after construct
            ExecuteSyncCalls(_syncCalls, ref _syncCallsCount);

            //execute entity rpcs
            _stateA.ExecuteRpcs(this, minimalTick, firstSync);

            foreach (var lagCompensatedEntity in LagCompensatedEntities)
                lagCompensatedEntity.WriteHistory(ServerTick);
        }

        private unsafe bool ReadEntityState(byte* rawData, bool fistSync)
        {
            for (int readerPosition = 0; readerPosition < _stateA.Size;)
            {
                bool fullSync = true;
                int endPos = 0;
                InternalEntity entity;
                ref readonly var classData = ref EntityClassData.Empty;
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
                        entity = null;
                    }
                    if (entity == null) //create new
                    {
                        entity = AddEntity(new EntityParams(entityDataHeader, this));
                        classData = ref entity.ClassData;
                        if (classData.PredictedSize > 0 || classData.SyncableFields.Length > 0)
                        {
                            _predictedEntityFilter.Add(entity);
                            //Logger.Log($"Add predicted: {entity.GetType()}");
                        }
                        Utils.ResizeIfFull(ref _entitiesToConstruct, _entitiesToConstructCount);
                        _entitiesToConstruct[_entitiesToConstructCount++] = entity;
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
                    if (entity != null)
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