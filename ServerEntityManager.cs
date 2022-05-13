using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using K4os.Compression.LZ4;
using LiteNetLib;
using LiteNetLib.Utils;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem
{
    internal struct InputBuffer
    {
        public ushort Tick;
        public InputPacketHeader Input;
        public byte[] Data;
        public ushort Size;
    }
    
    public sealed class NetPlayer
    {
        public readonly byte Id;
        public readonly NetPeer Peer;
        
        internal ushort LastProcessedTick;
        internal ushort LastReceivedTick;
        internal ushort LastReceivedState;
        internal ushort StateATick;
        internal ushort StateBTick;
        internal float LerpTime;
        internal bool IsFirstStateReceived;
        internal bool IsNew;
        internal int ArrayIndex;
        
        internal readonly SortedList<ushort, InputBuffer> AvailableInput = new SortedList<ushort, InputBuffer>(new SequenceComparer());

        internal void RequestBaselineSync()
        {
            IsNew = true;
            IsFirstStateReceived = false;
        }
        
        internal NetPlayer(NetPeer peer, byte id)
        {
            Peer = peer;
            Id = id;
            IsNew = true;
        }
    }

    public enum ServerSendRate
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
        public const byte ServerPlayerId = 0;
        
        private const int MaxPlayers = byte.MaxValue-1;

        private readonly Queue<ushort> _entityIdQueue = new Queue<ushort>(MaxEntityCount);
        private readonly Queue<byte> _playerIdQueue = new Queue<byte>(MaxPlayers);
        private readonly Queue<RemoteCallPacket> _rpcPool = new Queue<RemoteCallPacket>();
        private readonly Queue<byte[]> _inputPool = new Queue<byte[]>();
        private readonly byte[] _packetBuffer = new byte[NetConstants.MaxPacketSize*(MaxParts+1)];
        private readonly NetPlayer[] _netPlayersArray = new NetPlayer[MaxPlayers];
        private readonly NetPlayer[] _netPlayersDict = new NetPlayer[MaxPlayers];
        private readonly NetDataReader _inputReader = new NetDataReader();
        
        internal readonly StateSerializer[] SavedEntityData = new StateSerializer[MaxEntityCount];

        private byte[] _compressionBuffer;
        private int _netPlayersCount;
        private bool _lagCompensationEnabled;
        
        /// <summary>
        /// Rate at which server will make and send packets
        /// </summary>
        public ServerSendRate SendRate;
        
        /// <summary>
        /// Event that called when entity is enabling/disabling lag compensation
        /// </summary>
        public event Action<bool> OnLagCompensation;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="packetHeader">Header byte that will be used for packets (to distinguish entity system packets)</param>
        /// <param name="framesPerSecond">Fixed framerate of game logic</param>
        public ServerEntityManager(byte packetHeader, byte framesPerSecond) 
            : base(NetworkMode.Server, framesPerSecond)
        {
            InternalPlayerId = ServerPlayerId;
            for (int i = 1; i <= byte.MaxValue; i++)
                _playerIdQueue.Enqueue((byte)i);
            for (ushort i = 0; i < MaxEntityCount; i++)
                _entityIdQueue.Enqueue(i);

            _packetBuffer[0] = packetHeader;
        }
        
        /// <summary>
        /// Create and add new player
        /// </summary>
        /// <param name="peer">NetPeer to use</param>
        /// <param name="assignToTag">assign new player to NetPeer.Tag for usability</param>
        /// <returns></returns>
        public NetPlayer AddPlayer(NetPeer peer, bool assignToTag)
        {
            if (_netPlayersCount == MaxPlayers)
                return null;
            
            var player = new NetPlayer(peer, _playerIdQueue.Dequeue());
            _netPlayersDict[player.Id] = player;
            player.ArrayIndex = _netPlayersCount;
            _netPlayersArray[_netPlayersCount++] = player;
            if (assignToTag)
                peer.Tag = player;
            return player;
        }

        /// <summary>
        /// Remove player using NetPeer.Tag (is you assigned it or used <see cref="AddPlayer"/> with assignToTag)
        /// </summary>
        /// <param name="player">player to remove</param>
        /// <returns>true if player removed succesfully, false if player not found</returns>
        public bool RemovePlayerFromPeerTag(NetPeer player)
        {
            return RemovePlayer(player.Tag as NetPlayer);
        }

        /// <summary>
        /// Remove player
        /// </summary>
        /// <param name="player">player to remove</param>
        /// <returns>true if player removed succesfully, false if player not found</returns>
        public bool RemovePlayer(NetPlayer player)
        {
            if (player == null || _netPlayersDict[player.Id] == null)
                return false;
            
            _netPlayersDict[player.Id] = null;
            _netPlayersCount--;
            _entityIdQueue.Enqueue(player.Id);
            
            if (player.ArrayIndex != _netPlayersCount)
            {
                _netPlayersArray[player.ArrayIndex] = _netPlayersArray[_netPlayersCount];
                _netPlayersArray[player.ArrayIndex].ArrayIndex = _netPlayersCount;
                _netPlayersArray[_netPlayersCount] = null;
            }

            return true;
        }

        /// <summary>
        /// Add new player controller entity
        /// </summary>
        /// <param name="owner">Player that owns this controller</param>
        /// <param name="initMethod">Method that will be called after entity construction</param>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Created entity or null in case of limit</returns>
        public T AddController<T>(NetPlayer owner, Action<T> initMethod = null) where T : HumanControllerLogic
        {
            var result = Add<T>(ent =>
            {
                ent.InternalOwnerId = owner.Id;
                initMethod?.Invoke(ent);
            });
            return result;
        }
        
        /// <summary>
        /// Add new AI controller entity
        /// </summary>
        /// <param name="initMethod">Method that will be called after entity construction</param>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Created entity or null in case of limit</returns>
        public T AddAIController<T>(Action<T> initMethod = null) where T : AiControllerLogic
        {
            return Add(initMethod);
        }

        /// <summary>
        /// Add new entity
        /// </summary>
        /// <param name="initMethod">Method that will be called after entity construction</param>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Created entity or null in case of limit</returns>
        public T AddSignleton<T>(Action<T> initMethod = null) where T : SingletonEntityLogic
        {
            return Add(initMethod);
        }

        /// <summary>
        /// Add new entity
        /// </summary>
        /// <param name="initMethod">Method that will be called after entity construction</param>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Created entity or null in case of limit</returns>
        public T AddEntity<T>(Action<T> initMethod = null) where T : EntityLogic
        {
            return Add(initMethod);
        }
        
        /// <summary>
        /// Read data from NetPeer with assigne NetPlayer to NetPeer.Tag
        /// </summary>
        /// <param name="peer">Player that sent input</param>
        /// <param name="reader">Reader with data</param>
        public void Deserialize(NetPeer peer, NetPacketReader reader)
        {
            Deserialize((NetPlayer)peer.Tag, reader);
        }
        
        /// <summary>
        /// Read data from NetPlayer
        /// </summary>
        /// <param name="player">Player that sent input</param>
        /// <param name="reader">Reader with data</param>
        public void Deserialize(NetPlayer player, NetPacketReader reader)
        {
            if (reader.AvailableBytes <= 3)
            {
                Logger.LogWarning($"Invalid data received: {reader.AvailableBytes}");
                reader.Recycle();
                return;
            }
            byte packetType = reader.GetByte();
            switch (packetType)
            {
                case PacketClientSync:
                    ReadInput(player, reader);
                    break;
                
                case PacketEntityCall:
                    ushort entityId = reader.GetUShort();
                    byte packetId = reader.GetByte();
                    //GetEntityById(entityId)?.ProcessPacket(packetId, reader);
                    break;
                
                default:
                    Logger.LogWarning($"[SEM] Unknown packet type: {packetType}");
                    break;
            }
            reader.Recycle();
        }

        public override unsafe void Update()
        {
            CheckStart();
            ushort prevTick = Tick;
            base.Update();
            
            //send only if tick changed
            if (_netPlayersCount == 0 || prevTick == Tick || Tick % (int) SendRate != 0)
                return;
            
            //calculate minimalTick
            ushort minimalTick = _netPlayersArray[0].StateATick;
            for (int pidx = 1; pidx < _netPlayersCount; pidx++)
            {
                var netPlayer = _netPlayersArray[pidx];
                minimalTick = netPlayer.StateATick < minimalTick ? netPlayer.StateATick : minimalTick;
            }

            //make packets
            fixed (byte* packetBuffer = _packetBuffer)
            {
                //header byte, packet type (2 bytes)
                Unsafe.Write(packetBuffer + 2, Tick);
                
                for (int pidx = 0; pidx < _netPlayersCount; pidx++)
                {
                    var netPlayer = _netPlayersArray[pidx];
                    var peer = netPlayer.Peer;
                    int writePosition = 4;
                    
                    //send all data
                    if (netPlayer.IsNew)
                    {
                        for (int i = 0; i <= MaxEntityId; i++)
                        {
                            SavedEntityData[i].MakeBaseline(netPlayer.Id, Tick, packetBuffer, ref writePosition);
                        }

                        Utils.ResizeOrCreate(ref _compressionBuffer, writePosition);

                        //compress initial data
                        int originalLength = writePosition - 2;
                        int encodedLength;

                        fixed (byte* compressionBuffer = _compressionBuffer)
                        {
                            encodedLength = LZ4Codec.Encode(
                                packetBuffer + 2,
                                writePosition - 2,
                                compressionBuffer,
                                _compressionBuffer.Length,
                                LZ4Level.L10_OPT);
                            Unsafe.Write(packetBuffer + 2, originalLength);
                            packetBuffer[6] = netPlayer.Id;
                            Unsafe.CopyBlock(packetBuffer + 7, compressionBuffer, (uint)encodedLength);
                        }
                        Logger.Log(
                            $"[SEM] SendWorld to player {netPlayer.Id}. orig: {originalLength} bytes, compressed: {encodedLength}");

                        packetBuffer[1] = PacketBaselineSync;
                        peer.Send(_packetBuffer, 0, encodedLength + 7, DeliveryMethod.ReliableOrdered);
                        
                        netPlayer.IsNew = false;
                        continue;
                    }

                    //waiting to load initial state
                    if (!netPlayer.IsFirstStateReceived)
                        continue;
                    
                    byte* partCount = &packetBuffer[4];
                    *partCount = 0;
                    
                    int mtu = peer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable);

                    //first part full of data
                    Unsafe.Write(packetBuffer + 5, netPlayer.LastProcessedTick);
                    Unsafe.Write(packetBuffer + 7, netPlayer.LastReceivedTick);
                    writePosition = 9;

                    for (ushort eId = 0; eId <= MaxEntityId; eId++)
                    {
                        var diffResult = SavedEntityData[eId].MakeDiff(
                            netPlayer.Id,
                            minimalTick,
                            Tick,
                            netPlayer.LastReceivedState,
                            packetBuffer,
                            ref writePosition);
                        if (diffResult == DiffResult.Done || diffResult == DiffResult.DoneAndDestroy)
                        {
                            if (diffResult == DiffResult.DoneAndDestroy)
                                _entityIdQueue.Enqueue(eId);
                            if (writePosition > mtu)
                            {
                                _packetBuffer[1] = PacketDiffSync;
                                peer.Send(_packetBuffer, 0, mtu, DeliveryMethod.Unreliable);
                                    
                                if (*partCount == MaxParts-1)
                                {
                                    netPlayer.RequestBaselineSync();
                                    break;
                                }
                                (*partCount)++;

                                //repeat in next packet
                                Unsafe.CopyBlock(packetBuffer + 5, packetBuffer + mtu, (uint)(writePosition - mtu));
                                writePosition -= mtu - 5;
                            }
                        }
                        if (diffResult == DiffResult.RequestBaselineSync)
                        {
                            netPlayer.RequestBaselineSync();
                            break;
                        }
                        //else skip
                    }

                    if (writePosition > 9 || *partCount > 0)
                    {
                        //Debug.Log($"PARTS: {partCount} {_netDataWriter.Data[4]}");
                        packetBuffer[1] = PacketDiffSyncLast; //lastPart flag
                        peer.Send(_packetBuffer, 0, writePosition, DeliveryMethod.Unreliable);
                    }
                }
            }

            //trigger only when there is data
            _netPlayersArray[0].Peer.NetManager.TriggerUpdate();
        }
        
        private T Add<T>(Action<T> initMethod) where T : InternalEntity
        {
            //create entity data and filters
            CheckStart();
            var classData = ClassDataDict[EntityClassInfo<T>.ClassId];
            T entity;
            
            if (classData.IsLocalOnly)
            {
                entity = (T)AddLocalEntity(classData.ClassId);
            }
            else
            {
                if (_entityIdQueue.Count == 0)
                {
                    Logger.Log($"Cannot add entity. Max entity count reached: {MaxEntityCount}");
                    return null;
                }
                ushort entityId =_entityIdQueue.Dequeue();
                ref var stateSerializer = ref SavedEntityData[entityId];

                entity = (T)AddEntity(new EntityParams(
                    classData.ClassId, 
                    entityId,
                    stateSerializer.IncrementVersion(Tick),
                    this));
                stateSerializer.Init(classData, entity);
            }
            initMethod?.Invoke(entity);
            ConstructEntity(entity);
            //Debug.Log($"[SEM] Entity create. clsId: {classData.ClassId}, id: {entityId}, v: {version}");
            return entity;
        }
        
        protected override void OnLogicTick()
        {
            for (int pidx = 0; pidx < _netPlayersCount; pidx++)
            {
                var player = _netPlayersArray[pidx];
                if (!player.IsFirstStateReceived) 
                    continue;
                
                //process input
                foreach (var controller in GetControllers<HumanControllerLogic>())
                {
                    if (player.Id == controller.OwnerId)
                    {
                        if (player.AvailableInput.Count == 0)
                            continue;
            
                        var inputFrame = player.AvailableInput.Minimal();
                        ref var inputData = ref inputFrame.Input;
                        _inputReader.SetSource(inputFrame.Data, 0, inputFrame.Size);
                        controller.ReadInput(_inputReader);
                        player.LastProcessedTick = inputFrame.Tick;
                        player.StateATick = inputData.StateA;
                        player.StateBTick = inputData.StateB;
                        player.LerpTime = inputData.LerpMsec / 1000f;
                        player.AvailableInput.Remove(inputFrame.Tick);
                        _inputPool.Enqueue(inputFrame.Data);
                    }
                }
            }
            
            foreach (var aliveEntity in AliveEntities)
                aliveEntity.Update();
            
            //write history
            foreach (var aliveEntity in AliveEntities)
                SavedEntityData[aliveEntity.Id].WriteHistory(Tick);
        }
        
        internal void DestroySavedData(EntityLogic entityLogic)
        {
            SavedEntityData[entityLogic.Id].Destroy(Tick);
        }
        
        internal void PoolRpc(RemoteCallPacket rpcNode)
        {
            _rpcPool.Enqueue(rpcNode);
        }
        
        internal unsafe void AddRemoteCall<T>(ushort entityId, T value, RemoteCall remoteCallInfo) where T : struct
        {
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(remoteCallInfo);
            rpc.Tick = Tick;
            fixed (byte* rawData = rpc.Data)
                Unsafe.Copy(rawData, ref value);
            SavedEntityData[entityId].AddRpcPacket(rpc);
        }
        
        internal unsafe void AddRemoteCall<T>(ushort entityId, T[] value, int count, RemoteCall remoteCallInfo) where T : struct
        {
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(remoteCallInfo, count);
            rpc.Tick = Tick;
            fixed (byte* rawData = rpc.Data, rawValue = Unsafe.As<byte[]>(value))
                Unsafe.CopyBlock(rawData, rawValue, rpc.Size);
            SavedEntityData[entityId].AddRpcPacket(rpc);
        }
        
        internal unsafe void AddSyncableCall<T>(SyncableField field, T value, MethodInfo method) where T : struct
        {
            var entity = EntitiesDict[field.EntityId];
            var remoteCallInfo = ClassDataDict[entity.ClassId].SyncableRemoteCalls[method];
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(remoteCallInfo, field.FieldId);
            rpc.Tick = Tick;
            rpc.Flags = ExecuteFlags.ExecuteOnServer | ExecuteFlags.SendToOther | ExecuteFlags.SendToOwner;
            fixed (byte* rawData = rpc.Data)
                Unsafe.Copy(rawData, ref value);
            SavedEntityData[field.EntityId].AddRpcPacket(rpc);
        }
        
        internal unsafe void AddSyncableCall<T>(SyncableField field, T[] value, int count, MethodInfo method) where T : struct
        {
            var entity = EntitiesDict[field.EntityId];
            var remoteCallInfo = ClassDataDict[entity.ClassId].SyncableRemoteCalls[method];
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(remoteCallInfo, field.FieldId, count);
            rpc.Tick = Tick;
            rpc.Flags = ExecuteFlags.ExecuteOnServer | ExecuteFlags.SendToOther | ExecuteFlags.SendToOwner;
            fixed (byte* rawData = rpc.Data, rawValue = Unsafe.As<byte[]>(value))
                Unsafe.CopyBlock(rawData, rawValue, rpc.Size);
            SavedEntityData[field.EntityId].AddRpcPacket(rpc);
        }

        internal void EnableLagCompensation(PawnLogic pawn)
        {
            if (_lagCompensationEnabled || pawn.OwnerId == ServerPlayerId)
                return;

            NetPlayer player = _netPlayersDict[pawn.OwnerId];
            if (!player.IsFirstStateReceived)
                return;
            
            _lagCompensationEnabled = true;
            //Logger.Log($"compensated: {player.ServerInterpolatedTick} =====");
            foreach (var entity in AliveEntities)
            {
                SavedEntityData[entity.Id].EnableLagCompensation(player);
                //entity.DebugPrint();
            }
            OnLagCompensation?.Invoke(true);
        }

        internal void DisableLagCompensation()
        {
            if(!_lagCompensationEnabled)
                return;
            _lagCompensationEnabled = false;
            //Logger.Log($"restored: {Tick} =====");
            foreach (var entity in AliveEntities)
            {
                SavedEntityData[entity.Id].DisableLagCompensation();
                //entity.DebugPrint();
            }
            OnLagCompensation?.Invoke(false);
        }

        private unsafe void ReadInput(NetPlayer player, NetPacketReader reader)
        {
            ushort clientTick = reader.GetUShort();

            while (reader.AvailableBytes >= 8)
            {
                InputBuffer inputBuffer = new InputBuffer
                {
                    Size = reader.GetUShort(),
                    Tick = clientTick
                };
                if (inputBuffer.Size > NetConstants.MaxUnreliableDataSize || inputBuffer.Size > reader.AvailableBytes)
                {
                    Logger.LogError($"Bad input from: {player.Id} - {player.Peer.EndPoint}");
                    return;
                }
                clientTick++;
                
                ref var input = ref inputBuffer.Input;
                input.StateA = reader.GetUShort();
                input.StateB = reader.GetUShort();
                input.LerpMsec = reader.GetUShort();

                if (Utils.SequenceDiff(input.StateB, player.LastReceivedState) > 0)
                    player.LastReceivedState = input.StateB;
                    
                //read input
                if (!player.AvailableInput.ContainsKey(inputBuffer.Tick) && Utils.SequenceDiff(inputBuffer.Tick, player.LastProcessedTick) > 0)
                {
                    if (player.AvailableInput.Count >= MaxSavedStateDiff)
                    {
                        var minimal = player.AvailableInput.Minimal();
                        if (Utils.SequenceDiff(inputBuffer.Tick, minimal.Tick) > 0)
                        {
                            _inputPool.Enqueue(minimal.Data);
                            player.AvailableInput.Remove(minimal.Tick);
                        }
                        else
                        {
                            reader.SkipBytes(inputBuffer.Size);
                            continue;
                        }
                    }

                    _inputPool.TryDequeue(out inputBuffer.Data);
                    Utils.ResizeOrCreate(ref inputBuffer.Data, inputBuffer.Size);
                    fixed(byte* inputData = inputBuffer.Data, readerData = reader.RawData)
                        Unsafe.CopyBlock(inputData, readerData + reader.Position, inputBuffer.Size);
                    player.AvailableInput.Add(inputBuffer.Tick, inputBuffer);

                    //to reduce data
                    if (Utils.SequenceDiff(inputBuffer.Tick, player.LastReceivedTick) > 0)
                        player.LastReceivedTick = inputBuffer.Tick;
                }
                reader.SkipBytes(inputBuffer.Size);
            }
          
            player.IsFirstStateReceived = true;
        }
    }
}
