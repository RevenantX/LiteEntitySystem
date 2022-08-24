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
    
    internal sealed class SequenceComparer : IComparer<ushort>
    {
        public int Compare(ushort x, ushort y)
        {
            return Utils.SequenceDiff(x, y);
        }
    }

    public enum NetPlayerState
    {
        New,
        WaitingForFirstInput,
        WaitingForFirstInputProcess,
        Active
    }
    
    public sealed class NetPlayer
    {
        public readonly byte Id;
        public readonly NetPeer Peer;
        
        internal ushort LastProcessedTick;
        internal ushort LastReceivedTick;
        internal ushort CurrentServerTick;
        internal ushort StateATick;
        internal ushort StateBTick;
        internal ushort SimulatedServerTick;
        internal float LerpTime;
        internal NetPlayerState State;
        internal int ArrayIndex;

        internal ushort AvailableInputCount;
        internal readonly InputBuffer[] AvailableInput = new InputBuffer[ServerEntityManager.MaxStoredInputs];

        internal void RequestBaselineSync()
        {
            State = NetPlayerState.New;
        }
        
        internal NetPlayer(NetPeer peer, byte id)
        {
            Peer = peer;
            Id = id;
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
        private readonly Queue<ushort> _entityIdQueue = new(MaxSyncedEntityCount);
        private readonly Queue<byte> _playerIdQueue = new(MaxPlayers);
        private readonly Queue<RemoteCallPacket> _rpcPool = new();
        private readonly Queue<byte[]> _inputPool = new();
        private readonly byte[] _packetBuffer = new byte[200 * 1024 * 1024];
        private readonly NetPlayer[] _netPlayersArray = new NetPlayer[MaxPlayers];
        private readonly NetPlayer[] _netPlayersDict = new NetPlayer[MaxPlayers];
        private readonly NetDataReader _inputReader = new();
        private readonly StateSerializer[] _stateSerializers = new StateSerializer[MaxSyncedEntityCount];
        public const int MaxStoredInputs = 30;

        private byte[] _compressionBuffer;
        private int _netPlayersCount;

        /// <summary>
        /// Network players count
        /// </summary>
        public int PlayersCount => _netPlayersCount;
        
        /// <summary>
        /// Rate at which server will make and send packets
        /// </summary>
        public ServerSendRate SendRate = ServerSendRate.ThirdOfFPS;

        private ushort _minimalTick;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="typesMap">EntityTypesMap with registered entity types</param>
        /// <param name="packetHeader">Header byte that will be used for packets (to distinguish entity system packets)</param>
        /// <param name="framesPerSecond">Fixed framerate of game logic</param>
        public ServerEntityManager(EntityTypesMap typesMap, byte packetHeader, byte framesPerSecond) 
            : base(typesMap, NetworkMode.Server, framesPerSecond)
        {
            InternalPlayerId = ServerPlayerId;
            for (int i = 1; i <= byte.MaxValue; i++)
                _playerIdQueue.Enqueue((byte)i);
            for (ushort i = FirstEntityId; i < MaxSyncedEntityCount; i++)
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
        /// <returns>true if player removed successfully, false if player not found</returns>
        public bool RemovePlayerFromPeerTag(NetPeer player)
        {
            return RemovePlayer(player.Tag as NetPlayer);
        }

        /// <summary>
        /// Remove player and it's owned entities
        /// </summary>
        /// <param name="player">player to remove</param>
        /// <returns>true if player removed successfully, false if player not found</returns>
        public bool RemovePlayer(NetPlayer player)
        {
            if (player == null || _netPlayersDict[player.Id] == null)
                return false;
            
            for(int i = FirstEntityId; i < MaxSyncedEntityId; i++)
            {
                var e = EntitiesDict[i];
                if (e.IsControlledBy(player.Id))
                {
                    if (e is HumanControllerLogic controllerLogic)
                        controllerLogic.DestroyInternal();
                    else if (e is EntityLogic entityLogic) 
                        entityLogic.Destroy();
                }
            }
            
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

        public void RemoveAIController<T>(T controller) where T : AiControllerLogic
        {
            controller.StopControl();
            RemoveEntity(controller);
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
        /// Read data from NetPeer with assigned NetPlayer to NetPeer.Tag
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
            if (reader.AvailableBytes < 3)
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

                default:
                    Logger.LogWarning($"[SEM] Unknown packet type: {packetType}");
                    break;
            }
            reader.Recycle();
        }
        
        /// <summary>
        /// Read incoming data in case of first byte is == headerByte
        /// </summary>
        /// <param name="player">Player that sent input</param>
        /// <param name="reader">Reader with data (will be recycled inside, also works with autorecycle)</param>
        /// <returns>true if first byte is == headerByte</returns>
        public bool DeserializeWithHeaderCheck(NetPlayer player, NetPacketReader reader)
        {
            if (reader.PeekByte() == _packetBuffer[0])
            {
                reader.SkipBytes(1);
                Deserialize(player, reader);
                return true;
            }

            return false;
        }
        
        /// <summary>
        /// Read incoming data in case of first byte is == headerByte from NetPeer with assigned NetPlayer to NetPeer.Tag
        /// </summary>
        /// <param name="peer">Player that sent input</param>
        /// <param name="reader">Reader with data (will be recycled inside, also works with autorecycle)</param>
        /// <returns>true if first byte is == headerByte</returns>
        public bool DeserializeWithHeaderCheck(NetPeer peer, NetPacketReader reader)
        {
            if (reader.PeekByte() == _packetBuffer[0])
            {
                reader.SkipBytes(1);
                Deserialize((NetPlayer)peer.Tag, reader);
                return true;
            }

            return false;
        }

        public override unsafe void Update()
        {
            ushort prevTick = _tick;
            base.Update();
            
            //send only if tick changed
            if (_netPlayersCount == 0 || prevTick == _tick || _tick % (int) SendRate != 0)
                return;
            
            //calculate minimalTick
            _minimalTick = _tick;
            for (int pidx = 0; pidx < _netPlayersCount; pidx++)
            {
                var netPlayer = _netPlayersArray[pidx];
                if (netPlayer.State == NetPlayerState.New)
                    continue;
                _minimalTick = Utils.SequenceDiff(netPlayer.StateATick, _minimalTick) < 0 ? netPlayer.StateATick : _minimalTick;
            }

            //make packets
            fixed (byte* packetBuffer = _packetBuffer)
            {
                //header byte, packet type (2 bytes)
                Unsafe.Write(packetBuffer + 2, _tick);
                
                for (int pidx = 0; pidx < _netPlayersCount; pidx++)
                {
                    var netPlayer = _netPlayersArray[pidx];
                    var peer = netPlayer.Peer;
                    int writePosition = 4;
                    
                    //send all data
                    if (netPlayer.State == NetPlayerState.New)
                    {
                        for (int i = FirstEntityId; i <= MaxSyncedEntityId; i++)
                            _stateSerializers[i].MakeBaseline(netPlayer.Id, _tick, _minimalTick, packetBuffer, ref writePosition);

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
                                LZ4Level.L00_FAST);
                            Unsafe.Write(packetBuffer + 2, originalLength);
                            packetBuffer[6] = netPlayer.Id;
                            Unsafe.CopyBlock(packetBuffer + 7, compressionBuffer, (uint)encodedLength);
                        }
                        Logger.Log($"[SEM] SendWorld to player {netPlayer.Id}. orig: {originalLength} bytes, compressed: {encodedLength}");

                        packetBuffer[1] = PacketBaselineSync;
                        peer.Send(_packetBuffer, 0, encodedLength + 7, DeliveryMethod.ReliableOrdered);

                        netPlayer.StateATick = _tick;
                        netPlayer.CurrentServerTick = _tick;
                        netPlayer.State = NetPlayerState.WaitingForFirstInput;
                        continue;
                    }

                    //waiting to load initial state
                    if (netPlayer.State != NetPlayerState.Active)
                        continue;
                    
                    byte* partCount = &packetBuffer[4];
                    *partCount = 0;
                    
                    int mtu = peer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable);

                    //first part full of data
                    Unsafe.Write(packetBuffer + 5, netPlayer.LastProcessedTick);
                    Unsafe.Write(packetBuffer + 7, netPlayer.LastReceivedTick);
                    writePosition = 9;

                    for (ushort eId = FirstEntityId; eId <= MaxSyncedEntityId; eId++)
                    {
                        var diffResult = _stateSerializers[eId].MakeDiff(
                            netPlayer.Id,
                            _tick,
                            _minimalTick,
                            netPlayer.CurrentServerTick,
                            packetBuffer,
                            ref writePosition);
                        if (diffResult == DiffResult.DoneAndDestroy)
                        {
                            _entityIdQueue.Enqueue(eId);
                        }
                        if (diffResult == DiffResult.Done)
                        {
                            if (writePosition > mtu)
                            {
                                if (*partCount == MaxParts-1)
                                {
                                    Logger.Log($"P:{pidx} Request baseline {_tick}");
                                    netPlayer.RequestBaselineSync();
                                    break;
                                }
                                _packetBuffer[1] = PacketDiffSync;
                                //Logger.LogWarning($"P:{pidx} Sending diff part {*partCount}: {_tick}");
                                peer.Send(_packetBuffer, 0, mtu, DeliveryMethod.Unreliable);
                                (*partCount)++;

                                //repeat in next packet
                                Unsafe.CopyBlock(packetBuffer + 5, packetBuffer + mtu, (uint)(writePosition - mtu));
                                writePosition -= mtu - 5;
                            }
                        }
                        else if (diffResult == DiffResult.RequestBaselineSync)
                        {
                            Logger.LogWarning($"P:{pidx} Request baseline {_tick}");
                            netPlayer.RequestBaselineSync();
                            break;
                        }
                        //else skip
                    }
                    //Debug.Log($"PARTS: {partCount} {_netDataWriter.Data[4]}");
                    packetBuffer[1] = PacketDiffSyncLast; //lastPart flag
                    peer.Send(_packetBuffer, 0, writePosition, DeliveryMethod.Unreliable);
                }
            }

            //trigger only when there is data
            _netPlayersArray[0].Peer.NetManager.TriggerUpdate();
        }
        
        private T Add<T>(Action<T> initMethod) where T : InternalEntity
        {
            if (EntityClassInfo<T>.ClassId == 0)
            {
                throw new Exception($"Unregistered entity type: {typeof(T)}");
            }
            //create entity data and filters
            ref var classData = ref ClassDataDict[EntityClassInfo<T>.ClassId];
            T entity;
            
            if (classData.IsLocalOnly)
            {
                entity = AddLocalEntity(initMethod);
            }
            else
            {
                if (_entityIdQueue.Count == 0)
                {
                    Logger.Log($"Cannot add entity. Max entity count reached: {MaxSyncedEntityCount}");
                    return null;
                }
                ushort entityId =_entityIdQueue.Dequeue();
                ref var stateSerializer = ref _stateSerializers[entityId];

                entity = (T)AddEntity(new EntityParams(
                    classData.ClassId, 
                    entityId,
                    stateSerializer.IncrementVersion(_tick),
                    this));
                stateSerializer.Init(ref classData, entity);
                
                initMethod?.Invoke(entity);
                ConstructEntity(entity);
            }
            //Debug.Log($"[SEM] Entity create. clsId: {classData.ClassId}, id: {entityId}, v: {version}");
            return entity;
        }
        
        internal override NetPlayer GetPlayer(byte ownerId)
        {
            return _netPlayersDict[ownerId];
        }

        protected override void OnLogicTick()
        {
            for (int pidx = 0; pidx < _netPlayersCount; pidx++)
            {
                var player = _netPlayersArray[pidx];
                if (player.State == NetPlayerState.New) 
                    continue;
                if (player.AvailableInputCount == 0)
                {
                    //Logger.LogWarning($"Inputs of player {pidx} is zero");
                    continue;
                }

                var emptyInputBuffer = new InputBuffer();
                ref var inputFrame = ref emptyInputBuffer;
                int nextInputTick = player.LastProcessedTick;
                while (inputFrame.Data == null)
                {
                    nextInputTick = (nextInputTick+1) % MaxStoredInputs;
                    inputFrame = ref player.AvailableInput[nextInputTick];
                    if (player.LastProcessedTick == nextInputTick)
                    {
                        Logger.LogError("This shouldn't be happen");
                        break;
                    }
                }

                player.AvailableInputCount--;
                ref var inputData = ref inputFrame.Input;
                player.LastProcessedTick = inputFrame.Tick;
                player.StateATick = inputData.StateA;
                player.StateBTick = inputData.StateB;
                player.LerpTime = inputData.LerpMsec;
                //Logger.Log($"[SEM] CT: {player.LastProcessedTick}, stateA: {player.StateATick}, stateB: {player.StateBTick}");
                player.SimulatedServerTick = Utils.LerpSequence(inputData.StateA, inputData.StateB, inputData.LerpMsec);
                if (player.State == NetPlayerState.WaitingForFirstInputProcess)
                    player.State = NetPlayerState.Active;
                        
                _inputReader.SetSource(inputFrame.Data, 0, inputFrame.Size);
                
                //process input
                foreach (var controller in GetControllers<HumanControllerLogic>())
                {
                    if (player.Id == controller.OwnerId)
                    {
                        controller.ReadInput(_inputReader);
                    }
                }
                
                _inputPool.Enqueue(inputFrame.Data);
                inputFrame.Data = null;
            }

            foreach (var aliveEntity in GetAliveEntities())
                aliveEntity.Update();

            foreach (var lagCompensatedEntity in LagCompensatedEntities)
                lagCompensatedEntity.WriteHistory(_tick);
        }
        
        internal void DestroySavedData(InternalEntity entityLogic)
        {
            _stateSerializers[entityLogic.Id].Destroy(_tick, _minimalTick);
        }
        
        internal void PoolRpc(RemoteCallPacket rpcNode)
        {
            _rpcPool.Enqueue(rpcNode);
        }
        
        internal void AddRemoteCall(ushort entityId, RemoteCall remoteCallInfo) 
        {
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(_tick, remoteCallInfo);
            _stateSerializers[entityId].AddRpcPacket(rpc);
        }
        
        internal unsafe void AddRemoteCall<T>(ushort entityId, T value, RemoteCall remoteCallInfo) where T : struct
        {
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(_tick, remoteCallInfo);
            fixed (byte* rawData = rpc.Data)
                Unsafe.Copy(rawData, ref value);
            _stateSerializers[entityId].AddRpcPacket(rpc);
        }
        
        internal unsafe void AddRemoteCall<T>(ushort entityId, T[] value, int count, RemoteCall remoteCallInfo) where T : struct
        {
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(_tick, remoteCallInfo, count);
            fixed (byte* rawData = rpc.Data, rawValue = Unsafe.As<byte[]>(value))
                Unsafe.CopyBlock(rawData, rawValue, rpc.Size);
            _stateSerializers[entityId].AddRpcPacket(rpc);
        }
        
        internal void AddSyncableCall(SyncableField field, MethodInfo method)
        {
            var entity = EntitiesDict[field.EntityId];
            var remoteCallInfo = ClassDataDict[entity.ClassId].SyncableRemoteCalls[method];
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(_tick, remoteCallInfo, field.FieldId);
            _stateSerializers[field.EntityId].AddRpcPacket(rpc);
        }
        
        internal unsafe void AddSyncableCall<T>(SyncableField field, T value, MethodInfo method) where T : struct
        {
            var entity = EntitiesDict[field.EntityId];
            var remoteCallInfo = ClassDataDict[entity.ClassId].SyncableRemoteCalls[method];
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(_tick, remoteCallInfo, field.FieldId);
            fixed (byte* rawData = rpc.Data)
                Unsafe.Copy(rawData, ref value);
            _stateSerializers[field.EntityId].AddRpcPacket(rpc);
        }
        
        internal unsafe void AddSyncableCall<T>(SyncableField field, T[] value, int count, MethodInfo method) where T : struct
        {
            var entity = EntitiesDict[field.EntityId];
            var remoteCallInfo = ClassDataDict[entity.ClassId].SyncableRemoteCalls[method];
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(_tick, remoteCallInfo, field.FieldId, count);
            fixed (byte* rawData = rpc.Data, rawValue = Unsafe.As<byte[]>(value))
                Unsafe.CopyBlock(rawData, rawValue, rpc.Size);
            _stateSerializers[field.EntityId].AddRpcPacket(rpc);
        }

        private unsafe void ReadInput(NetPlayer player, NetPacketReader reader)
        {
            ushort clientTick = reader.GetUShort();

            while (reader.AvailableBytes >= sizeof(ushort) + Unsafe.SizeOf<InputPacketHeader>())
            {
                var inputBuffer = new InputBuffer
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
                fixed (byte* rawData = reader.RawData)
                {
                    Unsafe.Copy(ref input, rawData + reader.Position);
                }
                reader.SkipBytes(Unsafe.SizeOf<InputPacketHeader>());

                if (Utils.SequenceDiff(input.StateB, player.CurrentServerTick) > 0)
                    player.CurrentServerTick = input.StateB;
                    
                //read input
                if (player.AvailableInput[inputBuffer.Tick % MaxStoredInputs].Data == null && Utils.SequenceDiff(inputBuffer.Tick, player.LastProcessedTick) > 0)
                {
                    _inputPool.TryDequeue(out inputBuffer.Data);
                    Utils.ResizeOrCreate(ref inputBuffer.Data, inputBuffer.Size);
                    fixed(byte* inputData = inputBuffer.Data, readerData = reader.RawData)
                        Unsafe.CopyBlock(inputData, readerData + reader.Position, inputBuffer.Size);
                    player.AvailableInput[inputBuffer.Tick % MaxStoredInputs] = inputBuffer;
                    player.AvailableInputCount++;

                    //to reduce data
                    if (Utils.SequenceDiff(inputBuffer.Tick, player.LastReceivedTick) > 0)
                        player.LastReceivedTick = inputBuffer.Tick;
                }
                reader.SkipBytes(inputBuffer.Size);
            }
            if(player.State == NetPlayerState.WaitingForFirstInput)
                player.State = NetPlayerState.WaitingForFirstInputProcess;
        }
    }
}
