using System;
using System.Collections.Generic;
using K4os.Compression.LZ4;
using LiteEntitySystem.Collections;
using LiteNetLib;
using LiteEntitySystem.Internal;
using LiteEntitySystem.Transport;
using LiteNetLib.Utils;

namespace LiteEntitySystem
{
    public enum ServerSendRate : byte
    {
        EqualToFPS = 1,
        HalfOfFPS = 2,
        ThirdOfFPS = 3
    }

    /// <summary>
    /// Server entity manager
    /// </summary>
    public sealed class ServerEntityManager : EntityManager
    {
        public const int MaxStoredInputs = 30;
        
        private readonly IdGeneratorUShort _entityIdQueue = new(1, MaxSyncedEntityCount);
        private readonly IdGeneratorByte _playerIdQueue = new(1, MaxPlayers);
        private readonly Queue<RemoteCallPacket> _rpcPool = new();
        private readonly Queue<byte[]> _pendingClientRequests = new();
        private byte[] _packetBuffer = new byte[(MaxParts+1) * NetConstants.MaxPacketSize + StateSerializer.MaxStateSize];
        private readonly SparseMap<NetPlayer> _netPlayers = new (MaxPlayers+1);
        private readonly StateSerializer[] _stateSerializers = new StateSerializer[MaxSyncedEntityCount];
        private readonly byte[] _inputDecodeBuffer = new byte[NetConstants.MaxUnreliableDataSize];
        private readonly NetDataReader _requestsReader = new();
        
        //use entity filter for correct sort (id+version+creationTime)
        private readonly AVLTree<InternalEntity> _changedEntities = new();
        
        private byte[] _compressionBuffer = new byte[4096];
        
        /// <summary>
        /// Network players count
        /// </summary>
        public int PlayersCount => _netPlayers.Count;
        
        /// <summary>
        /// Rate at which server will make and send packets
        /// </summary>
        public readonly ServerSendRate SendRate;

        /// <summary>
        /// Add try catch to entity updates
        /// </summary>
        public bool SafeEntityUpdate = false;

        private ushort _minimalTick;
        
        private int _nextOrderNum;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="typesMap">EntityTypesMap with registered entity types</param>
        /// <param name="packetHeader">Header byte that will be used for packets (to distinguish entity system packets)</param>
        /// <param name="framesPerSecond">Fixed framerate of game logic</param>
        /// <param name="sendRate">Send rate of server (depends on fps)</param>
        /// <param name="maxHistorySize">Maximum size of lag compensation history in ticks</param>
        public ServerEntityManager(
            EntityTypesMap typesMap, 
            byte packetHeader, 
            byte framesPerSecond,
            ServerSendRate sendRate,
            MaxHistorySize maxHistorySize = MaxHistorySize.Size32) 
            : base(typesMap, NetworkMode.Server, packetHeader, maxHistorySize)
        {
            InternalPlayerId = ServerPlayerId;
            _packetBuffer[0] = packetHeader;
            SendRate = sendRate;
            SetTickrate(framesPerSecond);
        }

        public override void Reset()
        {
            base.Reset();
            _nextOrderNum = 0;
            _changedEntities.Clear();
        }

        /// <summary>
        /// Create and add new player
        /// </summary>
        /// <param name="peer">AbstractPeer to use</param>
        /// <returns>Newly created player, null if players count is maximum</returns>
        public NetPlayer AddPlayer(AbstractNetPeer peer)
        {
            if (_netPlayers.Count == MaxPlayers)
                return null;
            if (peer.AssignedPlayer != null)
            {
                Logger.LogWarning("Peer already has an assigned player");
                return peer.AssignedPlayer;
            }
            if (_netPlayers.Count == 0)
                _changedEntities.Clear();
            
            var player = new NetPlayer(peer, _playerIdQueue.GetNewId())
            {
                State = NetPlayerState.RequestBaseline,
                AvailableInput = new SequenceBinaryHeap<InputInfo>(MaxStoredInputs)
            };
            _netPlayers.Set(player.Id, player);
            peer.AssignedPlayer = player;
            return player;
        }

        /// <summary>
        /// Get player by owner id
        /// </summary>
        /// <param name="ownerId">id of player owner (Entity.OwnerId)</param>
        /// <returns></returns>
        public NetPlayer GetPlayer(byte ownerId) =>
            _netPlayers.TryGetValue(ownerId, out var p) ? p : null;

        /// <summary>
        /// Remove player using NetPeer.Tag (is you assigned it or used <see cref="AddPlayer"/> with assignToTag)
        /// </summary>
        /// <param name="player">player to remove</param>
        /// <returns>true if player removed successfully, false if player not found</returns>
        public bool RemovePlayer(AbstractNetPeer player) =>
            RemovePlayer(player.AssignedPlayer);
        
        /// <summary>
        /// Remove player and it's owned entities
        /// </summary>
        /// <param name="player">player to remove</param>
        /// <returns>true if player removed successfully, false if player not found</returns>
        public bool RemovePlayer(NetPlayer player)
        {
            if (player == null || !_netPlayers.Contains(player.Id))
                return false;

            GetPlayerController(player)?.DestroyWithControlledEntity();

            bool result = _netPlayers.Remove(player.Id);
            _playerIdQueue.ReuseId(player.Id);
            return result;
        }

        /// <summary>
        /// Returns controller owned by the player
        /// </summary>
        /// <param name="player">player</param>
        /// <returns>Instance if found, null if not</returns>
        public HumanControllerLogic GetPlayerController(AbstractNetPeer player) =>
            GetPlayerController(player.AssignedPlayer);
        
        /// <summary>
        /// Returns controller owned by the player
        /// </summary>
        /// <param name="playerId">player</param>
        /// <returns>Instance if found, null if not</returns>
        public HumanControllerLogic GetPlayerController(byte playerId) =>
            GetPlayerController(_netPlayers.TryGetValue(playerId, out var p) ? p : null);
        
        /// <summary>
        /// Returns controller owned by the player
        /// </summary>
        /// <param name="player">player to remove</param>
        /// <returns>Instance if found, null if not</returns>
        public HumanControllerLogic GetPlayerController(NetPlayer player)
        {
            if (player == null || !_netPlayers.Contains(player.Id))
                return null;
            foreach (var controller in GetEntities<HumanControllerLogic>())
            {
                if (controller.InternalOwnerId.Value == player.Id)
                    return controller;
            }
            return null;
        }

        /// <summary>
        /// Add new player controller entity
        /// </summary>
        /// <param name="owner">Player that owns this controller</param>
        /// <param name="initMethod">Method that will be called after entity construction</param>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Created entity or null in case of limit</returns>
        public T AddController<T>(NetPlayer owner, Action<T> initMethod = null) where T : HumanControllerLogic =>
            Add<T>(ent =>
            {
                ent.InternalOwnerId.Value = owner.Id;
                initMethod?.Invoke(ent);
            });
        
        /// <summary>
        /// Add new player controller entity and start controlling entityToControl
        /// </summary>
        /// <param name="owner">Player that owns this controller</param>
        /// <param name="entityToControl">pawn that will be controlled</param>
        /// <param name="initMethod">Method that will be called after entity construction</param>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Created entity or null in case of limit</returns>
        public T AddController<T>(NetPlayer owner, PawnLogic entityToControl, Action<T> initMethod = null) where T : HumanControllerLogic =>
            Add<T>(ent =>
            {
                ent.InternalOwnerId.Value = owner.Id;
                ent.StartControl(entityToControl);
                initMethod?.Invoke(ent);
            });
        
        /// <summary>
        /// Add new AI controller entity
        /// </summary>
        /// <param name="initMethod">Method that will be called after entity construction</param>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Created entity or null in case of limit</returns>
        public T AddAIController<T>(Action<T> initMethod = null) where T : AiControllerLogic => 
            Add(initMethod);

        /// <summary>
        /// Add new entity
        /// </summary>
        /// <param name="initMethod">Method that will be called after entity construction</param>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Created entity or null in case of limit</returns>
        public T AddSingleton<T>(Action<T> initMethod = null) where T : SingletonEntityLogic => 
            Add(initMethod);

        /// <summary>
        /// Add new entity
        /// </summary>
        /// <param name="initMethod">Method that will be called after entity construction</param>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Created entity or null in case of limit</returns>
        public T AddEntity<T>(Action<T> initMethod = null) where T : EntityLogic => 
            Add(initMethod);
        
        /// <summary>
        /// Add new entity and set parent entity
        /// </summary>
        /// <param name="parent">Parent entity</param>
        /// <param name="initMethod">Method that will be called after entity construction</param>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Created entity or null in case of limit</returns>
        public T AddEntity<T>(EntityLogic parent, Action<T> initMethod = null) where T : EntityLogic =>
            Add<T>(e =>
            {
                e.SetParent(parent);
                initMethod?.Invoke(e);
            });

        /// <summary>
        /// Read data for player linked to AbstractNetPeer
        /// </summary>
        /// <param name="peer">Player that sent input</param>
        /// <param name="inData">incoming data with header</param>
        public DeserializeResult Deserialize(AbstractNetPeer peer, ReadOnlySpan<byte> inData) =>
            Deserialize(peer.AssignedPlayer, inData);

        /// <summary>
        /// Read data from NetPlayer
        /// </summary>
        /// <param name="player">Player that sent input</param>
        /// <param name="inData">incoming data with header</param>
        public unsafe DeserializeResult Deserialize(NetPlayer player, ReadOnlySpan<byte> inData)
        {
            if (inData[0] != HeaderByte)
                return DeserializeResult.HeaderCheckFailed;
            inData = inData.Slice(1);
            
            if (inData.Length < 3)
            {
                Logger.LogWarning($"Invalid data received. Length < 3: {inData.Length}");
                return DeserializeResult.Error;
            }
            
            byte packetType = inData[0];
            inData = inData.Slice(1);
            
            if (packetType == InternalPackets.ClientRequest)
            {
                if (inData.Length < HumanControllerLogic.MinRequestSize)
                {
                    Logger.LogError("size less than minRequest");
                    return DeserializeResult.Error;
                }
                _pendingClientRequests.Enqueue(inData.ToArray());
                return DeserializeResult.Done;
            }
            
            if (packetType != InternalPackets.ClientInput)
            {
                Logger.LogWarning($"[SEM] Unknown packet type: {packetType}");
                return DeserializeResult.Error;
            }
            
            int minInputSize = 0;
            int minDeltaSize = 0;
            foreach (var humanControllerLogic in GetEntities<HumanControllerLogic>())
            {
                if(humanControllerLogic.OwnerId != player.Id)
                    continue;
                minInputSize += humanControllerLogic.InputSize;
                minDeltaSize += humanControllerLogic.MinInputDeltaSize;
            }
            
            ushort clientTick = BitConverter.ToUInt16(inData);
            inData = inData.Slice(2);
            bool isFirstInput = true;
            while (inData.Length >= InputPacketHeader.Size)
            {
                var inputInfo = new InputInfo{ Tick = clientTick };
                fixed (byte* rawData = inData)
                    inputInfo.Header = *(InputPacketHeader*)rawData;
                
                inData = inData.Slice(InputPacketHeader.Size);
                bool correctInput = player.State == NetPlayerState.WaitingForFirstInput ||
                                    Utils.SequenceDiff(inputInfo.Tick, player.LastReceivedTick) > 0;
                
                if (correctInput && inData.Length == 0)
                {
                    //empty input when no controllers
                    player.AvailableInput.AddAndOverwrite(inputInfo, inputInfo.Tick);
                    player.LastReceivedTick = inputInfo.Tick;
                    break;
                }
                
                //possibly empty but with header
                if (isFirstInput && inData.Length < minInputSize)
                {
                    Logger.LogError($"Bad input from: {player.Id} - {player.Peer} too small input");
                    return DeserializeResult.Error;
                }
                if (!isFirstInput && inData.Length < minDeltaSize)
                {
                    Logger.LogError($"Bad input from: {player.Id} - {player.Peer} too small delta");
                    return DeserializeResult.Error;
                }
                if (Utils.SequenceDiff(inputInfo.Header.StateA, Tick) > 0 ||
                    Utils.SequenceDiff(inputInfo.Header.StateB, Tick) > 0)
                {
                    Logger.LogError($"Bad input from: {player.Id} - {player.Peer} invalid sequence");
                    return DeserializeResult.Error;
                }
                inputInfo.Header.LerpMsec = Math.Clamp(inputInfo.Header.LerpMsec, 0f, 1f);
                if (Utils.SequenceDiff(inputInfo.Header.StateB, player.CurrentServerTick) > 0)
                    player.CurrentServerTick = inputInfo.Header.StateB;
                //Logger.Log($"ReadInput: {clientTick} stateA: {inputInfo.Header.StateA}");
                clientTick++;
                
                //read input
                foreach (var controller in GetEntities<HumanControllerLogic>())
                {
                    if(controller.OwnerId != player.Id)
                        continue;
                    
                    //decode delta
                    ReadOnlySpan<byte> actualData;
                
                    if (!isFirstInput) //delta
                    {
                        var decodedData = new Span<byte>(_inputDecodeBuffer, 0, controller.InputSize);
                        decodedData.Clear();
                        int readBytes = controller.DeltaDecode(inData, decodedData);
                        actualData = decodedData;
                        inData = inData.Slice(readBytes);
                    }
                    else //full
                    {                  
                        isFirstInput = false;
                        actualData = inData.Slice(0, controller.InputSize);
                        controller.DeltaDecodeInit(actualData);
                        inData = inData.Slice(controller.InputSize);
                    }

                    if (correctInput)
                        controller.AddIncomingInput(inputInfo.Tick, actualData);
                }

                if (correctInput)
                {
                    player.AvailableInput.AddAndOverwrite(inputInfo, inputInfo.Tick);
                    //to reduce data
                    player.LastReceivedTick = inputInfo.Tick;
                }
            }
            if(player.State == NetPlayerState.WaitingForFirstInput)
                player.State = NetPlayerState.WaitingForFirstInputProcess;
            return DeserializeResult.Done;
        }
        
        private T Add<T>(Action<T> initMethod) where T : InternalEntity
        {
            if (EntityClassInfo<T>.ClassId == 0)
                throw new Exception($"Unregistered entity type: {typeof(T)}");
            
            //create entity data and filters
            ref var classData = ref ClassDataDict[EntityClassInfo<T>.ClassId];
            if (_entityIdQueue.AvailableIds == 0)
            {
                Logger.Log($"Cannot add entity. Max entity count reached: {MaxSyncedEntityCount}");
                return null;
            }
            ushort entityId = _entityIdQueue.GetNewId();
            ref var stateSerializer = ref _stateSerializers[entityId];

            byte[] ioBuffer = classData.AllocateDataCache();
            stateSerializer.AllocateMemory(ref classData, ioBuffer);
            var entity = AddEntity<T>(new EntityParams(
                new EntityDataHeader(
                    entityId,
                    classData.ClassId, 
                    stateSerializer.NextVersion,
                    ++_nextOrderNum),
                this,
                ioBuffer));
            stateSerializer.Init(entity, _tick);
            initMethod?.Invoke(entity);
            ConstructEntity(entity);
            _changedEntities.Add(entity);
            
            //Debug.Log($"[SEM] Entity create. clsId: {classData.ClassId}, id: {entityId}, v: {version}");
            return entity;
        }
        
        protected override unsafe void OnLogicTick()
        {
            //read pending client requests
            while (_pendingClientRequests.Count > 0)
            {
                _requestsReader.SetSource(_pendingClientRequests.Dequeue());
                ushort controllerId = _requestsReader.GetUShort();
                byte controllerVersion = _requestsReader.GetByte();
                if (TryGetEntityById<HumanControllerLogic>(new EntitySharedReference(controllerId, controllerVersion), out var controller))
                    controller.ReadClientRequest(_requestsReader);
            }
            
            int playersCount = _netPlayers.Count;
            for (int pidx = 0; pidx < playersCount; pidx++)
            {
                var player = _netPlayers.GetByIndex(pidx);
                if (player.State == NetPlayerState.RequestBaseline) 
                    continue;
                if (player.AvailableInput.Count == 0)
                {
                    //Logger.LogWarning($"Inputs of player {pidx} is zero");
                    continue;
                }
                
                var inputFrame = player.AvailableInput.ExtractMin();
                ref var inputData = ref inputFrame.Header;
                player.LastProcessedTick = inputFrame.Tick;
                player.StateATick = inputData.StateA;
                player.StateBTick = inputData.StateB;
                player.LerpTime = inputData.LerpMsec;
                //Logger.Log($"[SEM] CT: {player.LastProcessedTick}, stateA: {player.StateATick}, stateB: {player.StateBTick}");
                if (player.State == NetPlayerState.WaitingForFirstInputProcess)
                    player.State = NetPlayerState.Active;

                //process input
                foreach (var controller in GetEntities<HumanControllerLogic>())
                {
                    if (controller.InternalOwnerId.Value != player.Id)
                        continue;
                    controller.ApplyIncomingInput(inputFrame.Tick);
                }
            }

            if (SafeEntityUpdate)
            {
                foreach (var aliveEntity in AliveEntities)
                    aliveEntity.SafeUpdate();
            }
            else
            {
                foreach (var aliveEntity in AliveEntities)
                    aliveEntity.Update();
            }
            
            foreach (var lagCompensatedEntity in LagCompensatedEntities)
                ClassDataDict[lagCompensatedEntity.ClassId].WriteHistory(lagCompensatedEntity, _tick);
            
            //==================================================================
            //Sending part
            //==================================================================
            if (playersCount == 0 || _tick % (int) SendRate != 0)
                return;

            //calculate minimalTick and potential baseline size
            _minimalTick = _tick;
            int maxBaseline = 0;
            for (int pidx = 0; pidx < playersCount; pidx++)
            {
                var player = _netPlayers.GetByIndex(pidx);
                if (player.State != NetPlayerState.RequestBaseline)
                    _minimalTick = Utils.SequenceDiff(player.StateATick, _minimalTick) < 0 ? player.StateATick : _minimalTick;
                else if (maxBaseline == 0)
                {
                    maxBaseline = sizeof(BaselineDataHeader);
                    foreach (var e in GetEntities<InternalEntity>())
                        maxBaseline += _stateSerializers[e.Id].GetMaximumSize(_tick);
                    if (_packetBuffer.Length < maxBaseline)
                        _packetBuffer = new byte[maxBaseline];
                    int maxCompressedSize = LZ4Codec.MaximumOutputSize(_packetBuffer.Length) + sizeof(BaselineDataHeader);
                    if (_compressionBuffer.Length < maxCompressedSize)
                        _compressionBuffer = new byte[maxCompressedSize];
                }
            }

            //make packets
            fixed (byte* packetBuffer = _packetBuffer, compressionBuffer = _compressionBuffer)
            // ReSharper disable once BadChildStatementIndent
            for (int pidx = 0; pidx < playersCount; pidx++)
            {
                var player = _netPlayers.GetByIndex(pidx);
                if (player.State == NetPlayerState.RequestBaseline)
                {
                    int originalLength = 0;
                    foreach (var e in GetEntities<InternalEntity>())
                        _stateSerializers[e.Id].MakeBaseline(player.Id, _tick, packetBuffer, ref originalLength);
                    
                    //set header
                    *(BaselineDataHeader*)compressionBuffer = new BaselineDataHeader
                    {
                        UserHeader = HeaderByte,
                        PacketType = InternalPackets.BaselineSync,
                        OriginalLength = originalLength,
                        Tick = _tick,
                        PlayerId = player.Id,
                        SendRate = (byte)SendRate,
                        Tickrate = Tickrate
                    };
                    
                    //compress
                    int encodedLength = LZ4Codec.Encode(
                        packetBuffer,
                        originalLength,
                        compressionBuffer + sizeof(BaselineDataHeader),
                        _compressionBuffer.Length - sizeof(BaselineDataHeader),
                        LZ4Level.L00_FAST);
                    
                    player.Peer.SendReliableOrdered(new ReadOnlySpan<byte>(_compressionBuffer, 0, sizeof(BaselineDataHeader) + encodedLength));
                    player.StateATick = _tick;
                    player.CurrentServerTick = _tick;
                    player.State = NetPlayerState.WaitingForFirstInput;
                    Logger.Log($"[SEM] SendWorld to player {player.Id}. orig: {originalLength}, bytes, compressed: {encodedLength}, ExecutedTick: {_tick}");
                    continue;
                }
                if (player.State != NetPlayerState.Active)
                {
                    //waiting to load initial state
                    continue;
                }

                var playerController = GetPlayerController(player);
                
                //Partial diff sync
                var header = (DiffPartHeader*)packetBuffer;
                header->UserHeader = HeaderByte;
                header->Part = 0;
                header->Tick = _tick;
                int writePosition = sizeof(DiffPartHeader);
                
                ushort maxPartSize = (ushort)(player.Peer.GetMaxUnreliablePacketSize() - sizeof(LastPartData));
                foreach (var entity in _changedEntities)
                {
                    ref var stateSerializer = ref _stateSerializers[entity.Id];
                    
                    //all players has actual state so remove from sync
                    if (Utils.SequenceDiff(stateSerializer.LastChangedTick, _minimalTick) <= 0)
                    {
                        //remove from changed list
                        _changedEntities.Remove(entity);
                        
                        //if entity destroyed - free it
                        if (entity.IsDestroyed)
                        {
                            if (entity.UpdateOrderNum == _nextOrderNum)
                            {
                                //this was highest
                                _nextOrderNum = GetEntities<InternalEntity>().TryGetMax(out var highestEntity)
                                    ? highestEntity.UpdateOrderNum
                                    : 0;
                                //Logger.Log($"Removed highest order entity: {e.UpdateOrderNum}, new highest: {_nextOrderNum}");
                            }
                            _entityIdQueue.ReuseId(entity.Id);
                            stateSerializer.Free();
                            //Logger.Log($"[SRV] RemoveEntity: {e.Id}");
                            
                            RemoveEntity(entity);
                        }
                        continue;
                    }
                    //skip known
                    if (Utils.SequenceDiff(stateSerializer.LastChangedTick, player.StateATick) <= 0)
                        continue;
                    
                    if (stateSerializer.MakeDiff(
                        player.Id,
                        _tick,
                        _minimalTick,
                        player.CurrentServerTick,
                        packetBuffer,
                        ref writePosition,
                        playerController))
                    {
                        int overflow = writePosition - maxPartSize;
                        while (overflow > 0)
                        {
                            if (header->Part == MaxParts-1)
                            {
                                Logger.Log($"P:{pidx} Request baseline {_tick}");
                                player.State = NetPlayerState.RequestBaseline;
                                break;
                            }
                            header->PacketType = InternalPackets.DiffSync;
                            //Logger.LogWarning($"P:{pidx} Sending diff part {*partCount}: {_tick}");
                            player.Peer.SendUnreliable(new ReadOnlySpan<byte>(packetBuffer, maxPartSize));
                            header->Part++;

                            //repeat in next packet
                            RefMagic.CopyBlock(packetBuffer + sizeof(DiffPartHeader), packetBuffer + maxPartSize, (uint)overflow);
                            writePosition = sizeof(DiffPartHeader) + overflow;
                            overflow = writePosition - maxPartSize;
                        }
                        //if request baseline break entity loop
                        if(player.State == NetPlayerState.RequestBaseline)
                            break;
                    }
                    //else skip
                }
                
                //if request baseline continue to other players
                if(player.State == NetPlayerState.RequestBaseline)
                    continue;

                //Debug.Log($"PARTS: {partCount} {_netDataWriter.Data[4]}");
                header->PacketType = InternalPackets.DiffSyncLast;
                //put mtu at last packet
                *(LastPartData*)(packetBuffer + writePosition) = new LastPartData
                {
                    LastProcessedTick = player.LastProcessedTick,
                    LastReceivedTick = player.LastReceivedTick,
                    Mtu = maxPartSize,
                    BufferedInputsCount = (byte)player.AvailableInput.Count
                };
                writePosition += sizeof(LastPartData);
                player.Peer.SendUnreliable(new ReadOnlySpan<byte>(_packetBuffer, 0, writePosition));
            }

            //trigger only when there is data
            _netPlayers.GetByIndex(0).Peer.TriggerSend();
        }
        
        internal override void EntityFieldChanged<T>(InternalEntity entity, ushort fieldId, ref T newValue)
        {
            if (entity.IsDestroyed && _stateSerializers[entity.Id].Entity != entity)
            {
                //old freed entity
                return;
            }
            _changedEntities.Add(entity);
            _stateSerializers[entity.Id].MarkFieldChanged(fieldId, _tick, ref newValue);
        }
        
        internal void ForceEntitySync(InternalEntity entity)
        {
            _changedEntities.Add(entity);
            _stateSerializers[entity.Id].ForceFullSync(_tick);
        }

        internal void PoolRpc(RemoteCallPacket rpcNode) =>
            _rpcPool.Enqueue(rpcNode);
        
        internal void AddRemoteCall(InternalEntity entity, ushort rpcId, ExecuteFlags flags)
        {
            if (PlayersCount == 0)
                return;
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(_tick, 0, rpcId, flags);
            _stateSerializers[entity.Id].AddRpcPacket(rpc);
            _changedEntities.Add(entity);
        }
        
        internal unsafe void AddRemoteCall<T>(InternalEntity entity, ReadOnlySpan<T> value, ushort rpcId, ExecuteFlags flags) where T : unmanaged
        {
            if (PlayersCount == 0)
                return;
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            int dataSize = sizeof(T) * value.Length;
            if (dataSize > ushort.MaxValue)
            {
                Logger.LogError($"DataSize on rpc: {rpcId}, entity: {entity} is more than {ushort.MaxValue}");
                return;
            }
            rpc.Init(_tick, (ushort)dataSize, rpcId, flags);
            if(value.Length > 0)
                fixed(void* rawValue = value, rawData = rpc.Data)
                    RefMagic.CopyBlock(rawData, rawValue, (uint)dataSize);
            _stateSerializers[entity.Id].AddRpcPacket(rpc);
            _changedEntities.Add(entity);
        }
    }
}
