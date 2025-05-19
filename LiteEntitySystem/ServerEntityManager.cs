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
        private readonly Queue<RemoteCallPacket> _pendingRPCs = new();
                
        private NetPlayer _syncForPlayer;
        private int _maxDataSize;
        
        //use entity filter for correct sort (id+version+creationTime)
        private readonly AVLTree<InternalEntity> _changedEntities = new();
        private readonly AVLTree<InternalEntity> _temporaryEntityTree = new();
        
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
            _pendingRPCs.Clear();
            _maxDataSize = 0;
        }

        /// <summary>
        /// Change SyncVar and RPC synchronization by SyncGroup for player
        /// constructor and destruction will be synchronized anyways
        /// works only on server
        /// </summary>
        /// <param name="forPlayer">For which player</param>
        /// <param name="entity">entity</param>
        /// <param name="syncGroup">syncGroup to enable/disable</param>
        /// <param name="enable">true - enable sync (if was disabled), disable otherwise</param>
        public void ToggleSyncGroup(byte forPlayer, EntityLogic entity, SyncGroup syncGroup, bool enable) =>
            ToggleSyncGroup(GetPlayer(forPlayer), entity, syncGroup, enable);
        
        /// <summary>
        /// Change SyncVar and RPC synchronization by SyncGroup for player
        /// constructor and destruction will be synchronized anyways
        /// works only on server
        /// </summary>
        /// <param name="forPlayer">For which player</param>
        /// <param name="entity">entity</param>
        /// <param name="syncGroup">syncGroup to enable/disable</param>
        /// <param name="enable">true - enable sync (if was disabled), disable otherwise</param>
        public void ToggleSyncGroup(NetPlayer forPlayer, EntityLogic entity, SyncGroup syncGroup, bool enable)
        {
            //ignore destroyed and owned
            if (forPlayer == null || 
                forPlayer.State == NetPlayerState.Removed || 
                entity.IsDestroyed || 
                entity.InternalOwnerId == forPlayer.Id)
                return;
            
            if (forPlayer.EntitySyncInfo.TryGetValue(entity, out var syncGroupData) && syncGroupData.IsInitialized)
            {
                if (syncGroupData.IsGroupEnabled(syncGroup) != enable)
                {
                    syncGroupData.SetGroupEnabled(syncGroup, enable);
                    syncGroupData.LastChangedTick = _tick;
                    if(enable)
                        MarkFieldsChanged(entity, SyncGroupUtils.ToSyncFlags(syncGroup));
                    forPlayer.EntitySyncInfo[entity] = syncGroupData;
                }
            }
            else if(!enable)
            {
                syncGroupData = new SyncGroupData(_tick);
                syncGroupData.SetGroupEnabled(syncGroup, false);
                forPlayer.EntitySyncInfo.Add(entity, syncGroupData);
            }
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
            
            var player = new NetPlayer(peer, _playerIdQueue.GetNewId(), MaxStoredInputs);
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
            player.State = NetPlayerState.Removed;

            if (_netPlayers.Count == 0)
            {
                while (_pendingRPCs.TryDequeue(out var rpc))
                {
                    _maxDataSize -= rpc.TotalSize;
                    _rpcPool.Enqueue(rpc);
                }
            }
            
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
                e.InternalOwnerId.Value = parent?.InternalOwnerId ?? ServerPlayerId;
                e.SetParentInternal(parent);
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
            
            //create new rpc
            stateSerializer.MakeNewRPC();
            
            //init and construct
            initMethod?.Invoke(entity);
            ConstructEntity(entity);
            
            //create OnConstructed rpc
            stateSerializer.MakeConstructedRPC();
            
            _changedEntities.Add(entity);
            _maxDataSize += stateSerializer.GetMaximumSize();
            
            //Debug.Log($"[SEM] Entity create. clsId: {classData.ClassId}, id: {entityId}, v: {version}");
            return entity;
        }

        internal override void OnEntityDestroyed(InternalEntity e)
        {
            //sync all disabled data before destroy
            if (e is EntityLogic el)
            {
                for(int i = 0; i < _netPlayers.Count; i++)
                {
                    if (_netPlayers.GetByIndex(i).EntitySyncInfo.Remove(el, out var syncGroupData))
                        MarkFieldsChanged(e, SyncGroupUtils.ToSyncFlags(~syncGroupData.EnabledGroups));
                }
            }
            
            _stateSerializers[e.Id].MakeDestroyedRPC(_tick);
            base.OnEntityDestroyed(e);
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
            
            //calculate minimalTick
            _minimalTick = _tick;
            bool resizeCompressionBuffer = false;
            int playersCount = _netPlayers.Count;
            for (int pidx = 0; pidx < playersCount; pidx++)
            {
                var player = _netPlayers.GetByIndex(pidx);
                if (player.FirstBaselineSent)
                    _minimalTick = Utils.SequenceDiff(player.StateATick, _minimalTick) < 0 ? player.StateATick : _minimalTick;
                
                if (player.State == NetPlayerState.RequestBaseline)
                {
                    resizeCompressionBuffer = true;
                    continue;
                }
                if (player.AvailableInput.Count == 0)
                {
                    //Logger.LogWarning($"Inputs of player {pidx} is zero");
                    continue;
                }
                
                var inputFrame = player.AvailableInput.ExtractMin();
                player.LoadInputInfo(inputFrame);
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
                    if(!aliveEntity.IsDestroyed)
                        aliveEntity.SafeUpdate();
            }
            else
            {
                foreach (var aliveEntity in AliveEntities)
                    if(!aliveEntity.IsDestroyed)
                        aliveEntity.Update();
            }

            ExecuteLateConstruct();
            
            foreach (var lagCompensatedEntity in LagCompensatedEntities)
                ClassDataDict[lagCompensatedEntity.ClassId].WriteHistory(lagCompensatedEntity, _tick);
            
            //==================================================================
            //Sending part
            //==================================================================
            if (playersCount == 0 || _tick % (int) SendRate != 0)
                return;
            
            //remove old rpcs
            while (_pendingRPCs.TryPeek(out var rpcNode) && Utils.SequenceDiff(rpcNode.Header.Tick, _minimalTick) < 0)
            {
                _maxDataSize -= rpcNode.TotalSize;
                _rpcPool.Enqueue(_pendingRPCs.Dequeue());
            }
            
            int maxBaseline = sizeof(BaselineDataHeader) + _maxDataSize;
            //resize buffers
            if (_packetBuffer.Length < maxBaseline)
                _packetBuffer = new byte[maxBaseline];
            if (resizeCompressionBuffer)
            {
                int maxCompressedSize = LZ4Codec.MaximumOutputSize(_packetBuffer.Length);
                if (_compressionBuffer.Length < maxCompressedSize)
                    _compressionBuffer = new byte[maxCompressedSize];
            }

            //make packets
            fixed (byte* packetBuffer = _packetBuffer, compressionBuffer = _compressionBuffer)
            // ReSharper disable once BadChildStatementIndent
            for (int pidx = 0; pidx < playersCount; pidx++)
            {
                var player = _netPlayers.GetByIndex(pidx);
                _syncForPlayer = null;
                if (player.State == NetPlayerState.RequestBaseline)
                {
                    int originalLength = 0;
                    if (!player.FirstBaselineSent)
                    {
                        player.FirstBaselineSent = true;
                        _syncForPlayer = player;
                        //new rpcs at first
                        _temporaryEntityTree.Clear();
                        foreach (var e in GetEntities<InternalEntity>())
                        {
                            if (_stateSerializers[e.Id].ShouldSync(player.Id, false))
                            {
                                _stateSerializers[e.Id].MakeNewRPC();
                                _temporaryEntityTree.Add(e);
                            }
                        }
                    
                        //then construct rpcs
                        foreach (var e in _temporaryEntityTree)
                            _stateSerializers[e.Id].MakeConstructedRPC();
                        _syncForPlayer = null;
                        
                        foreach (var rpcNode in _pendingRPCs)
                            if (rpcNode.OnlyForPlayer == player)
                                rpcNode.WriteTo(packetBuffer, ref originalLength);
                    }
                    else //like baseline but only queued rpcs
                    {
                        foreach (var rpcNode in _pendingRPCs)
                            if(ShouldSendRPC(rpcNode, player))
                                rpcNode.WriteTo(packetBuffer, ref originalLength);
                    }
                    
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
                    
                    player.Peer.SendReliableOrdered(new ReadOnlySpan<byte>(compressionBuffer, sizeof(BaselineDataHeader) + encodedLength));
                    player.StateATick = _tick;
                    player.CurrentServerTick = _tick;
                    player.State = NetPlayerState.WaitingForFirstInput;
                    Logger.Log($"[SEM] SendWorld to player {player.Id}. orig: {originalLength} b, compressed: {encodedLength} b, ExecutedTick: {_tick}");
                    continue;
                }
                if (player.State != NetPlayerState.Active)
                {
                    //waiting to load initial state
                    continue;
                }
                
                //Partial diff sync
                var header = (DiffPartHeader*)packetBuffer;
                header->UserHeader = HeaderByte;
                header->Part = 0;
                header->Tick = _tick;
                
                int writePosition = sizeof(DiffPartHeader);
                ushort maxPartSize = (ushort)(player.Peer.GetMaxUnreliablePacketSize() - sizeof(LastPartData));
                int rpcSize = 0;
                
                foreach (var rpcNode in _pendingRPCs)
                {
                    if(!ShouldSendRPC(rpcNode, player))
                        continue;
                    
                    rpcSize += rpcNode.TotalSize;
                    rpcNode.WriteTo(packetBuffer, ref writePosition);
                    CheckOverflowAndSend(player, header, packetBuffer, ref writePosition, maxPartSize);
                    if(player.State == NetPlayerState.RequestBaseline)
                        break;
                    //Logger.Log($"[Sever] T: {Tick}, SendRPC Tick: {rpcNode.Header.Tick}, Id: {rpcNode.Header.Id}, EntityId: {rpcNode.Header.EntityId}, ByteCount: {rpcNode.Header.ByteCount}");
                }
                
                //Logger.Log($"pendingRPCS: {_pendingRpcs.Count}, size: {rpcSize}");
                if(player.State == NetPlayerState.RequestBaseline)
                    continue;
                
                foreach (var entity in _changedEntities)
                {
                    ref var stateSerializer = ref _stateSerializers[entity.Id];
                    
                    //all players has actual state so remove from sync
                    if (Utils.SequenceDiff(stateSerializer.LastChangedTick, _minimalTick) <= 0)
                    {
                        //remove from changed list
                        _changedEntities.Remove(entity);
                        
                        //if entity destroyed - free it
                        if (entity.IsDestroyed && !entity.IsRemoved)
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
                            _maxDataSize -= stateSerializer.GetMaximumSize();
                            stateSerializer.Free();
                            //Logger.Log($"[SRV] RemoveEntity: {e.Id}");
                            RemoveEntity(entity);
                        }
                        continue;
                    }

                    if (stateSerializer.MakeDiff(player, _minimalTick, packetBuffer, ref writePosition))
                    {
                        CheckOverflowAndSend(player, header, packetBuffer, ref writePosition, maxPartSize);
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
                    BufferedInputsCount = (byte)player.AvailableInput.Count,
                    EventsSize = rpcSize
                };
                writePosition += sizeof(LastPartData);
                player.Peer.SendUnreliable(new ReadOnlySpan<byte>(packetBuffer, writePosition));
            }

            //trigger only when there is data
            _netPlayers.GetByIndex(0).Peer.TriggerSend();

            //Logger.Log($"ServerSendTick: {_tick}");
            return;
            void CheckOverflowAndSend(NetPlayer player, DiffPartHeader *header, byte* packetBuffer, ref int writePosition, int maxPartSize)
            {
                int overflow = writePosition - maxPartSize;
                while (overflow > 0)
                {
                    if (header->Part == MaxParts-1)
                    {
                        Logger.Log($"P:{player.Id} Request baseline {_tick}");
                        player.State = NetPlayerState.RequestBaseline;
                        break;
                    }
                    header->PacketType = InternalPackets.DiffSync;
                    //Logger.LogWarning($"P:{player.Id} Sending diff part {header->Part}: {_tick}");
                    player.Peer.SendUnreliable(new ReadOnlySpan<byte>(packetBuffer, maxPartSize));
                    header->Part++;

                    //repeat in next packet
                    RefMagic.CopyBlock(packetBuffer + sizeof(DiffPartHeader), packetBuffer + maxPartSize, (uint)overflow);
                    writePosition = sizeof(DiffPartHeader) + overflow;
                    overflow = writePosition - maxPartSize;
                }
            }
            
            bool ShouldSendRPC(RemoteCallPacket rpcNode, NetPlayer player)
            {
                //SyncForPlayer calls?
                if (rpcNode.OnlyForPlayer != null && rpcNode.OnlyForPlayer != player)
                {
                    //Logger.Log($"SkipSend onlyForPlayer: {rpcNode.Header.Tick}, {rpcNode.Header.Id}, EID: {rpcNode.Header.EntityId}");
                    return false;
                }
                
                //old rpc
                if (Utils.SequenceDiff(rpcNode.Header.Tick, player.LastReceivedTick) < 0)
                {
                    //Logger.Log($"SkipSend oldTick: {rpcNode.Header.Tick}, {rpcNode.Header.Id}, EID: {rpcNode.Header.EntityId}");
                    return false;
                }

                ref var stateSerializer = ref _stateSerializers[rpcNode.Header.EntityId];
                
                //mostly controllers
                if(!stateSerializer.ShouldSync(player.Id, true))
                    return false;
                
                var entity = EntitiesDict[rpcNode.Header.EntityId];
                var flags = rpcNode.ExecuteFlags;
                if (!flags.HasFlagFast(ExecuteFlags.SendToAll))
                {
                    if (flags.HasFlagFast(ExecuteFlags.SendToOwner) && entity.OwnerId != player.Id)
                        return false;
                    if (flags.HasFlagFast(ExecuteFlags.SendToOther) && entity.OwnerId == player.Id)
                        return false;
                }
                
                if (entity.OwnerId != player.Id &&
                    entity is EntityLogic el && 
                    player.EntitySyncInfo.TryGetValue(el, out var syncGroups) &&
                    syncGroups.IsInitialized &&
                    SyncGroupUtils.IsRPCDisabled(syncGroups.EnabledGroups, flags))
                {
                    //skip disabled rpcs
                    //Logger.Log($"SkipSend disabled: {rpcNode.Header.Tick}, {rpcNode.Header.Id}, EID: {rpcNode.Header.EntityId}");
                    return false;
                }
                
                if (rpcNode.Header.Id == RemoteCallPacket.ConstructRPCId)
                    stateSerializer.RefreshConstructedRPC(rpcNode);

                return true;
            }       
        }
        
        internal override void EntityFieldChanged<T>(InternalEntity entity, ushort fieldId, ref T newValue)
        {
            if (entity.IsRemoved)
            {
                //old freed entity
                return;
            }
            _changedEntities.Add(entity);
            _stateSerializers[entity.Id].UpdateFieldValue(fieldId, _tick, ref newValue);
        }

        internal void MarkFieldsChanged(InternalEntity entity, SyncFlags onlyWithFlags)
        {
            _changedEntities.Add(entity);
            _stateSerializers[entity.Id].MarkFieldsChanged(_tick, onlyWithFlags);
        }
        
        internal void AddRemoteCall(InternalEntity entity, ushort rpcId, ExecuteFlags flags)
        {
            if (PlayersCount == 0 || 
                entity.IsRemoved || 
                entity is AiControllerLogic ||
                (flags & ExecuteFlags.SendToAll) == 0)
                return;
            
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(_syncForPlayer, entity, _tick, 0, rpcId, flags);
            _pendingRPCs.Enqueue(rpc);
            _maxDataSize += rpc.TotalSize;
            _changedEntities.Add(entity);
        }
        
        internal unsafe void AddRemoteCall<T>(InternalEntity entity, ReadOnlySpan<T> value, ushort rpcId, ExecuteFlags flags) where T : unmanaged
        {
            if (PlayersCount == 0 ||
                entity.IsRemoved || 
                entity is AiControllerLogic ||
                (flags & ExecuteFlags.SendToAll) == 0)
                return;
            
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            int dataSize = sizeof(T) * value.Length;
            if (dataSize > ushort.MaxValue)
            {
                Logger.LogError($"DataSize on rpc: {rpcId}, entity: {entity} is more than {ushort.MaxValue}");
                return;
            }
            
            rpc.Init(_syncForPlayer, entity, _tick, (ushort)dataSize, rpcId, flags);
            if(value.Length > 0)
                fixed(void* rawValue = value, rawData = rpc.Data)
                    RefMagic.CopyBlock(rawData, rawValue, (uint)dataSize);
            _pendingRPCs.Enqueue(rpc);
            _maxDataSize += rpc.TotalSize;
            _changedEntities.Add(entity);
        }
    }
}
