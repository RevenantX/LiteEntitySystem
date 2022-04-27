using System;
using System.Collections.Generic;
using K4os.Compression.LZ4;
using LiteNetLib;
using LiteNetLib.Utils;

namespace LiteEntitySystem
{
    internal struct InputComprarer : IComparer<InputBuffer>
    {
        public int Compare(InputBuffer x, InputBuffer y)
        {
            return EntityManager.SequenceDiff(x.Tick, y.Tick);
        }
    }

    public readonly struct InputBuffer
    {
        public readonly ushort Tick;
        public readonly NetPacketReader Reader;

        public InputBuffer(ushort tick, NetPacketReader reader)
        {
            Tick = tick;
            Reader = reader;
        }
    }
    
    public sealed class NetPlayer
    {
        public readonly byte Id;
        public readonly NetPeer Peer;
        public ushort LastProcessedTick;
        public ushort ServerTick;
        public bool IsFirstStateReceived;
        public bool IsNew;
        public readonly SortedSet<InputBuffer> AvailableInput = new SortedSet<InputBuffer>(new InputComprarer());

        public float TimeBuffer;

        public NetPlayer(NetPeer peer)
        {
            Peer = peer;
            Id = (byte) (Peer.Id + 1);
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
        private readonly Queue<ushort> _possibleId = new Queue<ushort>();
        private readonly NetDataWriter _netDataWriter = new NetDataWriter(false, NetConstants.MaxPacketSize*MaxParts);
        internal readonly StateSerializer[] EntitySerializers = new StateSerializer[MaxEntityCount];
        private readonly NetPlayer[] _netPlayers = new NetPlayer[MaxPlayers];
        
        private int _netPlayersCount;

        private const int MaxPlayers = 128;
        private ushort _nextId;

        public const byte ServerPlayerId = 0;
        public override byte PlayerId => ServerPlayerId;

        public ServerSendRate SendRate;

        public ServerEntityManager(byte packetHeader, int framesPerSecond) : base(NetworkMode.Server, framesPerSecond)
        {
            _netDataWriter.Put(packetHeader);
        }
        
        public void AddPlayer(NetPlayer player)
        {
            _netPlayers[_netPlayersCount++] = player;
            player.IsNew = true;
        }

        public bool RemovePlayer(NetPlayer player)
        {
            for (int i = 0; i < _netPlayersCount; i++)
            {
                if (_netPlayers[i] == player)
                {
                    _netPlayersCount--;
                    _netPlayers[i] = _netPlayers[_netPlayersCount];
                    _netPlayers[_netPlayersCount] = null;
                    return true;
                }
            }
            return false;
        }

        public T AddController<T>(NetPlayer owner, Action<T> initMethod = null) where T : ControllerLogic
        {
            var result = Add<T>(initMethod);
            result.OwnerId = owner.Id;
            return result;
        }

        public T Add<T>(Action<T> initMethod = null) where T : InternalEntity
        {
            //create entity data and filters
            CheckStart();
            
            var classData = ClassDataDict[EntityClassInfo<T>.ClassId];
            ushort entityId = _possibleId.Count > 0 ? _possibleId.Dequeue() : _nextId++;
            
            ref var stateSerializer = ref EntitySerializers[entityId];
            stateSerializer ??= new StateSerializer();

            var entityParams = new EntityParams(
                classData.ClassId, 
                entityId,
                stateSerializer.IncrementVersion(Tick),
                this);
            T entity;

            //unity 2020 thats why.
            if (initMethod != null)
                entity = (T)AddEntity(entityParams, e => initMethod((T)e));
            else
                entity = (T)AddEntity(entityParams, null);
              
            stateSerializer.Init(classData, entity);
            //Debug.Log($"[SEM] Entity create. clsId: {classData.ClassId}, id: {entityId}, v: {version}");
            return entity;
        }

        protected override void OnLogicTick()
        {
            ServerTick = Tick;
            for (int pidx = 0; pidx < _netPlayersCount; pidx++)
            {
                var player = _netPlayers[pidx];
                if (!player.IsFirstStateReceived) 
                    continue;
                
                if (player.TimeBuffer > 0)
                {
                    player.TimeBuffer -= DeltaTime;
                }
                else
                {
                    foreach (var controller in GetEntities<HumanControllerLogic>())
                    {
                        if (player.Id == controller.OwnerId)
                        {
                            if (player.AvailableInput.Count > 0)
                            {
                                var inputFrame = player.AvailableInput.Min;
                                player.AvailableInput.Remove(inputFrame);
                                controller.ReadInput(inputFrame.Reader);
                                player.LastProcessedTick = inputFrame.Tick;
                                inputFrame.Reader.Recycle();
                            }
                            else
                            {
                                //TODO: round trip time maybe?
                                player.TimeBuffer = 0.1f;
                            }
                        }
                    }
                }
            }
            
            foreach (var aliveEntity in AliveEntities)
            {
                aliveEntity.Update();
            }
        }

        private byte[] _compressionBuffer;
        
        public override void Update()
        {
            CheckStart();
            ushort prevTick = Tick;
            base.Update();
            
            //send only if tick changed
            if (_netPlayersCount == 0 || prevTick == Tick || Tick % (int) SendRate != 0)
                return;

            //header byte, packet type (2 bytes)
            _netDataWriter.SetPosition(2);
            _netDataWriter.Put(Tick);
            
            //calculate minimalTick
            ushort minimalTick = _netPlayers[0].ServerTick;
            for (int pidx = 0; pidx < _netPlayersCount; pidx++)
            {
                var netPlayer = _netPlayers[pidx];
                minimalTick = netPlayer.ServerTick < minimalTick ? netPlayer.ServerTick : minimalTick;
            }

            for (int pidx = 0; pidx < _netPlayersCount; pidx++)
            {
                var netPlayer = _netPlayers[pidx];
                //send all data
                if (netPlayer.IsNew)
                {
                    _netDataWriter.Data[1] = PacketEntityFullSync;
                    for (int i = 0; i <= MaxEntityId; i++)
                    {
                        EntitySerializers[i].MakeBaseline(Tick, _netDataWriter);
                    }
                    Utils.ResizeOrCreate(ref _compressionBuffer, _netDataWriter.Length);

                    //compress initial data
                    int originalLength = _netDataWriter.Length - 2;
                    int encodedLength = LZ4Codec.Encode(
                        _netDataWriter.Data,
                        2, 
                        _netDataWriter.Length - 2,
                        _compressionBuffer,
                        0,
                        _compressionBuffer.Length,
                        LZ4Level.L10_OPT);
                    _netDataWriter.SetPosition(2);
                    _netDataWriter.Put(originalLength);
                    _netDataWriter.Put(_compressionBuffer, 0, encodedLength);
                    Logger.Log($"[SEM] SendWorld to player {netPlayer.Id}. orig: {originalLength} bytes, compressed: {encodedLength}, MaxEntityId: {MaxEntityId}");
                    
                    netPlayer.Peer.Send(_netDataWriter, DeliveryMethod.ReliableOrdered);
                    netPlayer.IsNew = false;
                    continue;
                }
                //waiting to load initial state
                if (!netPlayer.IsFirstStateReceived)
                    continue;

                var peer = netPlayer.Peer;
                byte partCount = 0;
                int mtu = peer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable);
                
                //first part full of data
                _netDataWriter.Data[4] = partCount;
                //position = 5
                FastBitConverter.GetBytes(_netDataWriter.Data, 5, netPlayer.LastProcessedTick);
                //position = 7
                _netDataWriter.SetPosition(7);

                for (int i = 0; i <= MaxEntityId; i++)
                {
                    int resultDataSize = EntitySerializers[i].MakeDiff(
                        minimalTick, 
                        Tick, 
                        netPlayer.ServerTick, 
                        _netDataWriter);
                    
                    if(resultDataSize == -1)
                    {
                        //nothing changed: reset and go to next
                        continue;
                    }
                    if (resultDataSize > mtu)
                    {
                        _netDataWriter.Data[1] = PacketEntitySync;
                        peer.Send(_netDataWriter.Data, 0, mtu, DeliveryMethod.Unreliable);
                        partCount++;
                        if (partCount == MaxParts)
                        {
                            Logger.LogWarning("[SEM] PART COUNT MAX");
                            //send at next frame
                            break;
                        }
                        
                        //repeat in next packet
                        _netDataWriter.Data[4] = partCount;
                        _netDataWriter.SetPosition(5);
                        _netDataWriter.Put(_netDataWriter.Data, mtu, resultDataSize - mtu);
                    }
                }
                //Debug.Log($"PARTS: {partCount} {_netDataWriter.Data[4]}");
                _netDataWriter.Data[1] = PacketEntitySyncLast; //lastPart flag
                peer.Send(_netDataWriter, DeliveryMethod.Unreliable);
            }
        }
        
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
                    ushort serverTick = reader.GetUShort();
                    ushort playerTick = reader.GetUShort();
                    
                    //read input
                    if (reader.AvailableBytes == 0)
                    {
                        Logger.LogWarning("[SEM] Player input is 0");
                        reader.Recycle();
                        break;
                    }
                    
                    //Logger.Log($"[SEM] st: {serverTick}, lt: {playerTick}");
                    if(SequenceDiff(serverTick, player.ServerTick) > 0)
                        player.ServerTick = serverTick;

                    var inputBuffer = new InputBuffer(playerTick, reader);
                    if (!player.AvailableInput.Contains(inputBuffer) && SequenceDiff(playerTick, player.LastProcessedTick) > 0)
                    {
                        if (player.AvailableInput.Count >= MaxSavedStateDiff)
                        {
                            var minimal = player.AvailableInput.Min;
                            if (SequenceDiff(playerTick, minimal.Tick) > 0)
                            {
                                minimal.Reader.Recycle();
                                player.AvailableInput.Remove(minimal);
                            }
                            else
                            {
                                reader.Recycle();
                                break;
                            }
                        }
                        player.AvailableInput.Add(inputBuffer);
                    }
                    else
                    {
                        reader.Recycle();
                        break;
                    }

                    player.IsFirstStateReceived = true;
                    break;
                
                case PacketEntityCall:
                    ushort entityId = reader.GetUShort();
                    byte packetId = reader.GetByte();
                    GetEntityById(entityId)?.ProcessPacket(packetId, reader);
                    reader.Recycle();
                    break;
                
                default:
                    Logger.LogWarning($"[SEM] Unknown packet type: {packetType}");
                    reader.Recycle();
                    break;
            }
        }
    }
}
