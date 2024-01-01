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

        public AbstractNetPeer NetPeer => _netPeer;
        
        private const int InputBufferSize = 128;

        private struct InputCommand
        {
            public readonly ushort Tick;
            public readonly byte[] Data;

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
        private readonly byte[][] _interpolateCurrentData = new byte[MaxEntityCount][];
        private readonly byte[][] _interpolatePrevData = new byte[MaxEntityCount][];
        private readonly byte[][] _predictedEntitiesData = new byte[MaxSyncedEntityCount][];
        private readonly byte[] _sendBuffer = new byte[NetConstants.MaxPacketSize];

        private ServerSendRate _serverSendRate;
        private ServerStateData _stateA;
        private ServerStateData _stateB;
        private float _lerpTime;
        private double _timer;
        private bool _isSyncReceived;

        internal readonly struct SyncCallInfo
        {
            public readonly SpanAction<InternalEntity, byte> OnChangeAction;
            public readonly InternalEntity Entity;
            public readonly int PrevDataPos;

            public SyncCallInfo(SpanAction<InternalEntity, byte> onChangeAction, InternalEntity entity, int prevDataPos)
            {
                OnChangeAction = onChangeAction;
                Entity = entity;
                PrevDataPos = prevDataPos;
            }
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

        private void OnAliveConstructed(InternalEntity entity)
        {
            var classMetadata = entity.GetClassMetadata();
            if (classMetadata.InterpolatedFieldsSize > 0)
            {
                Helpers.ResizeOrCreate(ref _interpolatePrevData[entity.Id], classMetadata.InterpolatedFieldsSize);
                
                //for local interpolated
                Helpers.ResizeOrCreate(ref _interpolateCurrentData[entity.Id], classMetadata.InterpolatedFieldsSize);

                var interpolationDataSpan = new Span<byte>(_interpolateCurrentData[entity.Id], 0, classMetadata.InterpolatedFieldsSize);
                entity.GetFieldManipulator().DumpInterpolated(ref interpolationDataSpan);
                Buffer.BlockCopy(_interpolateCurrentData[entity.Id], 0, _interpolatePrevData[entity.Id], 0, classMetadata.InterpolatedFieldsSize);
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

                        int bytesRead = 0;
                        while (bytesRead < _stateA.Size)
                        {
                            ushort entityId = *(ushort*)(stateData + bytesRead);
                            //Logger.Log($"[CEM] ReadBaseline Entity: {entityId} pos: {bytesRead}");
                            bytesRead += sizeof(ushort);

                            if (entityId == InvalidEntityId || entityId >= MaxSyncedEntityCount)
                            {
                                Logger.LogError($"Bad data (id > {MaxSyncedEntityCount} or Id == 0) Id: {entityId}");
                                return DeserializeResult.Error;
                            }

                            ReadEntityState(stateData, ref bytesRead, _stateA.Size, entityId, true, true);
                            if (bytesRead == -1)
                                return DeserializeResult.Error;
                        }
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
                    int tickDifference = Helpers.SequenceDiff(diffHeader.Tick, _lastReadyTick);
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
                        if (Helpers.SequenceDiff(serverState.LastReceivedTick, _lastReceivedInputTick) > 0)
                            _lastReceivedInputTick = serverState.LastReceivedTick;
                        if (Helpers.SequenceDiff(serverState.Tick, _lastReadyTick) > 0)
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
                _adaptiveMiddlePoint = Helpers.Lerp(_adaptiveMiddlePoint, Math.Max(1f, jitterSum), 0.05f);
            }
            
            _stateB = _readyStates.ExtractMin();
            _stateB.Preload(EntitiesDict, _interpolateCurrentData);
            //Logger.Log($"Preload A: {_stateA.Tick}, B: {_stateB.Tick}");
            _lerpTime = 
                Helpers.SequenceDiff(_stateB.Tick, _stateA.Tick) * DeltaTimeF *
                (1f - (_readyStates.Count + 1 - _adaptiveMiddlePoint) * 0.02f);

            //remove processed inputs
            while (_inputCommands.Count > 0 && Helpers.SequenceDiff(_stateB.ProcessedTick, _inputCommands.Peek().Tick) >= 0)
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
            for (int i = 0; i < _stateA.InterpolatedEntitiesCount; i++)
            {
                var entity = _stateA.InterpolatedEntities[i];
                var prevSpan = new Span<byte>(_interpolatePrevData[entity.Id]);
                entity.GetFieldManipulator().DumpInterpolated(ref prevSpan);
            }

            fixed (byte* readerData = _stateA.Data)
            {
                for (int i = 0; i < _stateA.PreloadDataCount; i++)
                {
                    ref var preloadData = ref _stateA.PreloadDataArray[i];
                    ReadEntityState(readerData, ref preloadData.DataOffset, _stateA.Size, preloadData.EntityId, preloadData.EntityFieldsOffset == -1, false);
                    if (preloadData.DataOffset == -1)
                        return;
                }
            }
            ConstructAndSync(false, minimalTick);
            
            _timer -= _lerpTime;
            
            //reset owned entities
            foreach (var entity in _predictedEntityFilter)
            {
                if(entity.IsRemoteControlled && !entity.GetClassMetadata().HasRemoteRollbackFields)
                    continue;
                var predictedSpan = new ReadOnlySpan<byte>(_predictedEntitiesData[entity.Id]);
                entity.GetFieldManipulator().LoadPredicted(ref predictedSpan);
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
                if (!entity.IsLocal && entity.IsLocalControlled)
                {
                    var interpolationDataSpan = new Span<byte>(_interpolateCurrentData[entity.Id]);
                    entity.GetFieldManipulator().DumpInterpolated(ref interpolationDataSpan);
                }
            }
            
            //delete predicted
            while (_spawnPredictedEntities.TryPeek(out var info))
            {
                if (Helpers.SequenceDiff(_stateA.ProcessedTick, info.id) >= 0)
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
                ServerTick = Helpers.LerpSequence(_stateA.Tick, _stateB.Tick, (float)(_timer/_lerpTime));
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
                var classMetadata = entity.GetClassMetadata();
                var fieldManipulator = entity.GetFieldManipulator();
                if (entity.IsLocal || entity.IsLocalControlled)
                {
                    //save data for interpolation before update
                    fixed (byte* currentDataPtr = _interpolateCurrentData[entity.Id], prevDataPtr = _interpolatePrevData[entity.Id])
                    {
                        //restore previous
                        var interpDataReadSpan = new ReadOnlySpan<byte>(currentDataPtr, classMetadata.InterpolatedFieldsSize);
                        fieldManipulator.LoadInterpolated(ref interpDataReadSpan);

                        //update
                        entity.Update();
                
                        //save current
                        Unsafe.CopyBlock(prevDataPtr, currentDataPtr, (uint)classMetadata.InterpolatedFieldsSize);
                        var interpDataWriteSpan = new Span<byte>(currentDataPtr, classMetadata.InterpolatedFieldsSize);
                        fieldManipulator.DumpInterpolated(ref interpDataWriteSpan);
                    }
                }
                else if(classMetadata.UpdateOnClient)
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

            //interpolation
            float localLerpT = LerpFactor;
            if (_stateB != null)
            {
                _logicLerpMsec = (float)(_timer/_lerpTime);
                for (int i = 0; i < _stateB.InterpolatedEntitiesCount; i++)
                {
                    var entity = _stateB.InterpolatedEntities[i];
                    var prevSpan = new ReadOnlySpan<byte>(_interpolatePrevData[entity.Id]);
                    var currSpan = new ReadOnlySpan<byte>(_interpolateCurrentData[entity.Id]);
                    entity.GetFieldManipulator().Interpolate(ref prevSpan, ref currSpan, _logicLerpMsec);
                }
            }
            foreach (var entity in AliveEntities)
            {
                if (!entity.IsLocalControlled && !entity.IsLocal)
                    continue;
                var prevSpan = new ReadOnlySpan<byte>(_interpolatePrevData[entity.Id]);
                var currSpan = new ReadOnlySpan<byte>(_interpolateCurrentData[entity.Id]);
                entity.GetFieldManipulator().Interpolate(ref prevSpan, ref currSpan, localLerpT);
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
                        if (Helpers.SequenceDiff(currentTick, _lastReceivedInputTick) <= 0)
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
                                    Unsafe.CopyBlock(sendBuffer + offset, inputData, (uint)InputPacketHeader.Size);
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
                                Unsafe.CopyBlock(sendBuffer + offset, rawInputCommand, (uint)InputProcessor.InputSizeWithHeader);
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
            if (entity.GetClassMetadata().IsUpdateable && !entity.GetClassMetadata().UpdateOnClient)
                AliveEntities.Add(entity);
        }
        
        internal void RemoveOwned(EntityLogic entity)
        {
            if (entity.GetClassMetadata().IsUpdateable && !entity.GetClassMetadata().UpdateOnClient)
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
            {
                ref var syncCall = ref _syncCalls[i];
                syncCall.OnChangeAction(syncCall.Entity,
                    new ReadOnlySpan<byte>(_stateA.Data, syncCall.PrevDataPos, _stateA.Size - syncCall.PrevDataPos));
                //Logger.Log($"OnChange: {syncCall.Field.Name} - {syncCall.Field.Id}");
            }
            _syncCallsCount = 0;
            
            //execute entity rpcs
            _stateA.ExecuteRpcs(this, minimalTick, firstSync);
            
            foreach (var lagCompensatedEntity in LagCompensatedEntities)
                lagCompensatedEntity.WriteHistory(ServerTick);
        }

        private unsafe void ReadEntityState(byte* rawData, ref int readerPosition, int rawDataSize, ushort entityInstanceId, bool fullSync, bool fistSync)
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
                    entity.DestroyInternal();
                    entity = null;
                }
                if(entity == null)
                {
                    //create new
                    entity = AddEntity(new EntityParams(classId, entityInstanceId, version, this));
                    var cd = entity.GetClassMetadata();
                    if (cd.PredictedSize > 0)
                    {
                        Helpers.ResizeOrCreate(ref _predictedEntitiesData[entity.Id], cd.PredictedSize);
                        _predictedEntityFilter.Add(entity);
                    }

                    if (cd.InterpolatedFieldsSize > 0)
                    {
                        Helpers.ResizeOrCreate(ref _interpolateCurrentData[entity.Id], cd.InterpolatedFieldsSize);
                        Helpers.ResizeOrCreate(ref _interpolatePrevData[entity.Id], cd.InterpolatedFieldsSize);
                    }

                    Helpers.ResizeIfFull(ref _entitiesToConstruct, _entitiesToConstructCount);
                    _entitiesToConstruct[_entitiesToConstructCount++] = entity;
                    //Logger.Log($"[CEM] Add entity: {entity.GetType()}");
                }
                else if(!fistSync)
                {
                    //skip full sync data if we already have correct entity
                    return;
                }
            }
            else if (entity == null)
            {
                Logger.LogError($"EntityNull? : {entityInstanceId}");
                readerPosition = -1;
                return;
            }
            
            var classData = entity.GetClassMetadata();
            Helpers.ResizeOrCreate(ref _syncCalls, _syncCallsCount + classData.FieldsCount);
            var fieldsData = new DeltaFieldsData
            {
                FieldsBits = new ReadOnlySpan<byte>(rawData + readerPosition - classData.FieldsFlagsSize, classData.FieldsFlagsSize),
                FullSync = fullSync,
                WriteInterpolationData = entity.IsRemoteControlled || fullSync,
                RawData = new Span<byte>(rawData + readerPosition, rawDataSize - readerPosition),
                InterpolatedData = _interpolatePrevData[entity.Id],
                PredictedData = _predictedEntitiesData[entity.Id],
                ReaderPosition = readerPosition,
                SyncCallInfos = _syncCalls,
                SyncCallsCount = _syncCallsCount
            };
            fieldsData.InitialLength = fieldsData.RawData.Length;
            entity.GetFieldManipulator().ReadChanged(ref fieldsData);
            readerPosition += fieldsData.InitialLength - fieldsData.RawData.Length;
            _syncCallsCount = fieldsData.SyncCallsCount;
            
            if (fullSync)
                _stateA.ReadRPCs(rawData, ref readerPosition, new EntitySharedReference(entity.Id, entity.Version), classData);
        }
    }
}