using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using K4os.Compression.LZ4;
using LiteNetLib;
using LiteEntitySystem.Internal;
using LiteNetLib.Utils;

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
        Active,
        WaitingForFirstInput,
        WaitingForFirstInputProcess,
        RequestBaseline
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

        internal NetPlayer(NetPeer peer, byte id)
        {
            Peer = peer;
            Id = id;
        }
    }

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
        private readonly Queue<ushort> _entityIdQueue = new(MaxSyncedEntityCount);
        private readonly Queue<byte> _playerIdQueue = new(MaxPlayers);
        private readonly Queue<RemoteCallPacket> _rpcPool = new();
        private readonly Queue<byte[]> _inputPool = new();
        private byte[] _packetBuffer = new byte[4096];
        private readonly NetPlayer[] _netPlayersArray = new NetPlayer[MaxPlayers];
        private readonly NetPlayer[] _netPlayersDict = new NetPlayer[MaxPlayers+1];
        private readonly StateSerializer[] _stateSerializers = new StateSerializer[MaxSyncedEntityCount];
        public const int MaxStoredInputs = 30;

        private byte[] _compressionBuffer = new byte[4096];
        private int _netPlayersCount;

        /// <summary>
        /// Network players count
        /// </summary>
        public int PlayersCount => _netPlayersCount;
        
        /// <summary>
        /// Rate at which server will make and send packets
        /// </summary>
        public readonly ServerSendRate SendRate;

        private ushort _minimalTick;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="typesMap">EntityTypesMap with registered entity types</param>
        /// <param name="inputProcessor">Input processor (you can use default InputProcessor/<T/> or derive from abstract one to make your own input serialization</param>
        /// <param name="packetHeader">Header byte that will be used for packets (to distinguish entity system packets)</param>
        /// <param name="framesPerSecond">Fixed framerate of game logic</param>
        /// <param name="sendRate">Send rate of server (depends on fps)</param>
        public ServerEntityManager(
            EntityTypesMap typesMap, 
            InputProcessor inputProcessor,
            byte packetHeader, 
            byte framesPerSecond,
            ServerSendRate sendRate) 
            : base(typesMap, inputProcessor, NetworkMode.Server, framesPerSecond)
        {
            InternalPlayerId = ServerPlayerId;
            for (int i = 1; i <= byte.MaxValue; i++)
                _playerIdQueue.Enqueue((byte)i);
            for (ushort i = FirstEntityId; i < MaxSyncedEntityCount; i++)
                _entityIdQueue.Enqueue(i);

            _packetBuffer[0] = packetHeader;
            SendRate = sendRate;
        }
        
        /// <summary>
        /// Create and add new player
        /// </summary>
        /// <param name="peer">NetPeer to use</param>
        /// <param name="assignToTag">assign new player to NetPeer.Tag for usability</param>
        /// <returns>Newly created player, null if players count is maximum</returns>
        public NetPlayer AddPlayer(NetPeer peer, bool assignToTag)
        {
            if (_netPlayersCount == MaxPlayers)
                return null;
            var player = new NetPlayer(peer, _playerIdQueue.Dequeue()) { State = NetPlayerState.RequestBaseline };
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
                if (EntitiesDict[i] is ControllerLogic controllerLogic && controllerLogic.OwnerId == player.Id)
                    controllerLogic.DestroyWithControlledEntity();
            }
            
            _netPlayersDict[player.Id] = null;
            _netPlayersCount--;
            _entityIdQueue.Enqueue(player.Id);
            
            if (player.ArrayIndex != _netPlayersCount)
            {
                _netPlayersArray[player.ArrayIndex] = _netPlayersArray[_netPlayersCount];
                _netPlayersArray[player.ArrayIndex].ArrayIndex = player.ArrayIndex;
            }
            _netPlayersArray[_netPlayersCount] = null;

            return true;
        }

        /// <summary>
        /// Add new player controller entity
        /// </summary>
        /// <param name="owner">Player that owns this controller</param>
        /// <param name="initMethod">Method that will be called after entity construction</param>
        /// <typeparam name="T">Entity type</typeparam>
        /// <returns>Created entity or null in case of limit</returns>
        public T AddController<T>(NetPlayer owner, Action<T> initMethod = null) where T : ControllerLogic
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
        public void Deserialize(NetPeer peer, NetDataReader reader)
        {
            Deserialize((NetPlayer)peer.Tag, reader);
        }

        /// <summary>
        /// Read incoming data in case of first byte is == headerByte
        /// </summary>
        /// <param name="player">Player that sent input</param>
        /// <param name="reader">Reader with data</param>
        /// <returns>true if first byte is == headerByte</returns>
        public bool DeserializeWithHeaderCheck(NetPlayer player, NetDataReader reader)
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
        /// <param name="reader">Reader with data</param>
        /// <returns>true if first byte is == headerByte</returns>
        public bool DeserializeWithHeaderCheck(NetPeer peer, NetDataReader reader)
        {
            if (reader.PeekByte() == _packetBuffer[0])
            {
                reader.SkipBytes(1);
                Deserialize((NetPlayer)peer.Tag, reader);
                return true;
            }

            return false;
        }
        
        /// <summary>
        /// Read data from NetPlayer
        /// </summary>
        /// <param name="player">Player that sent input</param>
        /// <param name="reader">Reader with data</param>
        public unsafe void Deserialize(NetPlayer player, NetDataReader reader)
        {
            if (reader.AvailableBytes < 3)
            {
                Logger.LogWarning($"Invalid data received: {reader.AvailableBytes}");
                return;
            }
            byte packetType = reader.GetByte();
            if (packetType != PacketClientSync)
            {
                Logger.LogWarning($"[SEM] Unknown packet type: {packetType}");
                return;
            }
            ushort clientTick = reader.GetUShort();
            while (reader.AvailableBytes >= sizeof(ushort) + sizeof(InputPacketHeader))
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
                    input = *(InputPacketHeader*)(rawData + reader.Position);
                }
                reader.SkipBytes(sizeof(InputPacketHeader));

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

        public override unsafe void Update()
        {
            ushort prevTick = _tick;
            base.Update();
            
            //send only if tick changed
            if (_netPlayersCount == 0 || prevTick == _tick || _tick % (int) SendRate != 0)
                return;
            
            //calculate minimalTick and potential baseline size
            byte headerByte = _packetBuffer[0];
            _minimalTick = _tick;
            int maxBaseline = 0;
            for (int pidx = 0; pidx < _netPlayersCount; pidx++)
            {
                var player = _netPlayersArray[pidx];
                if (player.State == NetPlayerState.RequestBaseline && maxBaseline == 0)
                {
                    maxBaseline = sizeof(BaselineDataHeader);
                    for (ushort i = FirstEntityId; i <= MaxSyncedEntityId; i++)
                        maxBaseline += _stateSerializers[i].GetMaximumSize();
                    if (_packetBuffer.Length < maxBaseline)
                    {
                        _packetBuffer = new byte[maxBaseline + maxBaseline / 2];
                        _packetBuffer[0] = headerByte;
                    }
                    int maxCompressedSize = LZ4Codec.MaximumOutputSize(_packetBuffer.Length);
                    if (_compressionBuffer.Length < maxCompressedSize)
                        _compressionBuffer = new byte[maxCompressedSize];
                }
                else
                {
                    _minimalTick = Utils.SequenceDiff(player.StateATick, _minimalTick) < 0 ? player.StateATick : _minimalTick;
                }
            }

            //make packets
            fixed (byte* packetBuffer = _packetBuffer, compressionBuffer = _compressionBuffer)
            // ReSharper disable once BadChildStatementIndent
            for (int pidx = 0; pidx < _netPlayersCount; pidx++)
            {
                var player = _netPlayersArray[pidx];
                if (player.State == NetPlayerState.RequestBaseline)
                {
                    int originalLength = 0;
                    for (ushort i = FirstEntityId; i <= MaxSyncedEntityId; i++)
                        _stateSerializers[i].MakeBaseline(player.Id, _tick, _minimalTick, packetBuffer, ref originalLength);
                    int encodedLength = LZ4Codec.Encode(
                        packetBuffer,
                        originalLength,
                        compressionBuffer,
                        _compressionBuffer.Length,
                        LZ4Level.L00_FAST);
                    
                    *(BaselineDataHeader*)packetBuffer = new BaselineDataHeader
                    {
                        UserHeader = headerByte,
                        PacketType = PacketBaselineSync,
                        OriginalLength = originalLength,
                        Tick = _tick,
                        PlayerId = player.Id,
                        SendRate = (byte)SendRate
                    };
                    Unsafe.CopyBlock(packetBuffer + sizeof(BaselineDataHeader), compressionBuffer, (uint)encodedLength);
                    player.Peer.Send(_packetBuffer, 0, sizeof(BaselineDataHeader) + encodedLength, DeliveryMethod.ReliableOrdered);
                    player.StateATick = _tick;
                    player.CurrentServerTick = _tick;
                    player.State = NetPlayerState.WaitingForFirstInput;
                    Logger.Log($"[SEM] SendWorld to player {player.Id}. orig: {originalLength}, bytes, compressed: {encodedLength}");
                    continue;
                }
                if (player.State != NetPlayerState.Active)
                {
                    //waiting to load initial state
                    continue;
                }
                
                //Partial diff sync
                var header = (FirstPartHeader*)packetBuffer;
                header->Part = 0;
                header->Tick = _tick;
                header->LastProcessedTick = player.LastProcessedTick;
                header->LastReceivedTick = player.LastReceivedTick;
                int writePosition = sizeof(FirstPartHeader);
                
                //minus ushort MTU value at end of last packet
                int mtu = player.Peer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable) - sizeof(ushort);
                for (ushort eId = FirstEntityId; eId <= MaxSyncedEntityId; eId++)
                {
                    var diffResult = _stateSerializers[eId].MakeDiff(
                        player.Id,
                        _tick,
                        _minimalTick,
                        player.CurrentServerTick,
                        packetBuffer,
                        ref writePosition);
                    if (diffResult == DiffResult.DoneAndDestroy)
                    {
                        _entityIdQueue.Enqueue(eId);
                    }
                    if (diffResult == DiffResult.Done)
                    {
                        int overflow = writePosition - mtu;
                        while (overflow > 0)
                        {
                            if (header->Part == MaxParts-1)
                            {
                                Logger.Log($"P:{pidx} Request baseline {_tick}");
                                player.State = NetPlayerState.RequestBaseline;
                                break;
                            }
                            header->PacketType = PacketDiffSync;
                            //Logger.LogWarning($"P:{pidx} Sending diff part {*partCount}: {_tick}");
                            player.Peer.Send(_packetBuffer, 0, mtu, DeliveryMethod.Unreliable);
                            header->Part++;

                            //repeat in next packet
                            Unsafe.CopyBlock(packetBuffer + sizeof(DiffPartHeader), packetBuffer + mtu, (uint)overflow);
                            writePosition = sizeof(DiffPartHeader) + overflow;
                            overflow = writePosition - mtu;
                        }
                    }
                    else if (diffResult == DiffResult.RequestBaselineSync)
                    {
                        Logger.LogWarning($"P:{pidx} Request baseline {_tick}");
                        player.State = NetPlayerState.RequestBaseline;
                        break;
                    }
                    //else skip
                }
                //Debug.Log($"PARTS: {partCount} {_netDataWriter.Data[4]}");
                header->PacketType = PacketDiffSyncLast; //lastPart flag
                if (header->Part > 0)
                {
                    //put mtu at last packet
                    *(ushort*)(packetBuffer + writePosition) = (ushort)(mtu + sizeof(ushort));
                    writePosition += sizeof(ushort);
                }
                player.Peer.Send(_packetBuffer, 0, writePosition, DeliveryMethod.Unreliable);
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
                if (player.State == NetPlayerState.RequestBaseline) 
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

                //process input
                InputProcessor.ReadInputs(this, player.Id, inputFrame.Data, 0, inputFrame.Size);

                _inputPool.Enqueue(inputFrame.Data);
                inputFrame.Data = null;
            }

            foreach (var aliveEntity in AliveEntities)
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
        
        internal void AddRemoteCall(ushort entityId, byte rpcId, ExecuteFlags flags) 
        {
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(_tick, 0, rpcId, flags);
            _stateSerializers[entityId].AddRpcPacket(rpc);
        }
        
        internal unsafe void AddRemoteCall<T>(ushort entityId, T value, byte rpcId, ExecuteFlags flags) where T : unmanaged
        {
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(_tick, (ushort)sizeof(T), rpcId, flags);
            fixed (byte* rawData = rpc.Data)
                *(T*)rawData = value;
            _stateSerializers[entityId].AddRpcPacket(rpc);
        }
        
        internal unsafe void AddRemoteCall<T>(ushort entityId, ReadOnlySpan<T> value, byte rpcId, ExecuteFlags flags) where T : unmanaged
        {
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(_tick, (ushort)sizeof(T), rpcId, flags, value.Length);
            MemoryMarshal.AsBytes(value).CopyTo(rpc.Data);
            _stateSerializers[entityId].AddRpcPacket(rpc);
        }
        
        internal void AddSyncableCall(ushort entityId, byte rpcId, byte fieldId)
        {
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(_tick, 0, rpcId, fieldId);
            _stateSerializers[entityId].AddRpcPacket(rpc);
        }
        
        internal unsafe void AddSyncableCall<T>(ushort entityId, byte rpcId, byte fieldId, T value) where T : unmanaged
        {
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(_tick, (ushort)sizeof(T), rpcId, fieldId);
            fixed (byte* rawData = rpc.Data)
                *(T*)rawData = value;
            _stateSerializers[entityId].AddRpcPacket(rpc);
        }
        
        internal unsafe void AddSyncableCall<T>(ushort entityId, byte rpcId, byte fieldId, ReadOnlySpan<T> value) where T : unmanaged
        {
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(_tick, (ushort)sizeof(T), rpcId, fieldId, value.Length);
            MemoryMarshal.AsBytes(value).CopyTo(rpc.Data);
            _stateSerializers[entityId].AddRpcPacket(rpc);
        }
    }
}
