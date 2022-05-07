using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
        private readonly byte[] _packetBuffer = new byte[NetConstants.MaxPacketSize*(MaxParts+1)];
        private byte[] _compressionBuffer;
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
            _packetBuffer[0] = packetHeader;
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

        public T AddController<T>(NetPlayer owner, Action<T> initMethod = null) where T : HumanControllerLogic
        {
            var result = Add<T>(ent =>
            {
                ent.InternalOwnerId = owner.Id;
                initMethod?.Invoke(ent);
            });
            return result;
        }
        
        public T AddAIController<T>(Action<T> initMethod = null) where T : AiControllerLogic
        {
            return Add(initMethod);
        }

        public T AddSignleton<T>(Action<T> initMethod = null) where T : SingletonEntityLogic
        {
            return Add(initMethod);
        }

        public T AddEntity<T>(Action<T> initMethod = null) where T : EntityLogic
        {
            return Add(initMethod);
        }
        
        private T Add<T>(Action<T> initMethod) where T : InternalEntity
        {
            //create entity data and filters
            CheckStart();
            
            var classData = ClassDataDict[EntityClassInfo<T>.ClassId];
            ushort entityId = _possibleId.Count > 0 ? _possibleId.Dequeue() : _nextId++;
            
            ref var stateSerializer = ref EntitySerializers[entityId];
            stateSerializer ??= new StateSerializer();
            
            var entity = (T)AddEntity(new EntityParams(
                classData.ClassId, 
                entityId,
                stateSerializer.IncrementVersion(Tick),
                this));
            initMethod?.Invoke(entity);
            ConstructEntity(entity);
              
            stateSerializer.Init(classData, entity);
            //Debug.Log($"[SEM] Entity create. clsId: {classData.ClassId}, id: {entityId}, v: {version}");
            return entity;
        }

        private void ProcessInput(HumanControllerLogic controller, NetPlayer player)
        {
            if (player.AvailableInput.Count > 0)
            {
                var inputFrame = player.AvailableInput.Min;
                while(inputFrame.Tick <= player.LastProcessedTick)
                {
                    player.AvailableInput.Remove(inputFrame);
                    inputFrame.Reader.Recycle();
                    if (player.AvailableInput.Count == 0)
                        return;
                    inputFrame = player.AvailableInput.Min;
                } 
                
                controller.ReadInput(inputFrame.Reader);
                player.LastProcessedTick = inputFrame.Tick;
                player.AvailableInput.Remove(inputFrame);
                inputFrame.Reader.Recycle();
            }
            else
            {
                player.LastProcessedTick++;
            }
        }

        protected override void OnLogicTick()
        {
            ServerTick = Tick;
            for (int pidx = 0; pidx < _netPlayersCount; pidx++)
            {
                var player = _netPlayers[pidx];
                if (!player.IsFirstStateReceived) 
                    continue;
                
                foreach (var controller in GetControllers<HumanControllerLogic>())
                {
                    if (player.Id == controller.OwnerId)
                    {
                        ProcessInput(controller, player);
                    }
                }
            }
            
            foreach (var aliveEntity in AliveEntities)
            {
                aliveEntity.Update();
            }
        }

        internal override void RemoveEntity(EntityLogic e)
        {
            base.RemoveEntity(e);
            EntitySerializers[e.Id].Destroy(ServerTick);
        }

        public override unsafe void Update()
        {
            CheckStart();
            ushort prevTick = Tick;
            base.Update();
            
            //send only if tick changed
            if (_netPlayersCount == 0 || prevTick == Tick || Tick % (int) SendRate != 0)
                return;

            //header byte, packet type (2 bytes)
            FastBitConverter.GetBytes(_packetBuffer, 2, Tick);
            
            //calculate minimalTick
            ushort minimalTick = _netPlayers[0].ServerTick;
            for (int pidx = 1; pidx < _netPlayersCount; pidx++)
            {
                var netPlayer = _netPlayers[pidx];
                minimalTick = netPlayer.ServerTick < minimalTick ? netPlayer.ServerTick : minimalTick;
            }

            fixed(byte* packetBuffer = _packetBuffer)
            for (int pidx = 0; pidx < _netPlayersCount; pidx++)
            {
                var netPlayer = _netPlayers[pidx];
                int writePosition = 4;
                //send all data
                if (netPlayer.IsNew)
                {
                    _packetBuffer[1] = PacketBaselineSync;
                    for (int i = 0; i <= MaxEntityId; i++)
                    {
                        EntitySerializers[i].MakeBaseline(Tick, packetBuffer, ref writePosition);
                    }
                    Utils.ResizeOrCreate(ref _compressionBuffer, writePosition);

                    //compress initial data
                    int originalLength = writePosition - 2;
                    int encodedLength = LZ4Codec.Encode(
                        _packetBuffer,
                        2, 
                        writePosition - 2,
                        _compressionBuffer,
                        0,
                        _compressionBuffer.Length,
                        LZ4Level.L10_OPT);
                    FastBitConverter.GetBytes(_packetBuffer, 2, originalLength);
                    Buffer.BlockCopy(_compressionBuffer, 0, _packetBuffer, 6, encodedLength);
                    Logger.Log($"[SEM] SendWorld to player {netPlayer.Id}. orig: {originalLength} bytes, compressed: {encodedLength}, MaxEntityId: {MaxEntityId}");
                    
                    netPlayer.Peer.Send(_packetBuffer, 0, encodedLength+6, DeliveryMethod.ReliableOrdered);
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
                _packetBuffer[4] = partCount;
                //position = 5
                FastBitConverter.GetBytes(_packetBuffer, 5, netPlayer.LastProcessedTick);
                //position = 7
                writePosition = 7;

                for (ushort eId = 0; eId <= MaxEntityId; eId++)
                {
                    var diffResult = EntitySerializers[eId].MakeDiff(
                        netPlayer.Id,
                        minimalTick, 
                        Tick, 
                        netPlayer.ServerTick, 
                        packetBuffer,
                        ref writePosition);
                    switch (diffResult)
                    { 
                        case DiffResult.RequestBaselineSync:
                            netPlayer.IsNew = true;
                            netPlayer.IsFirstStateReceived = false;
                            break;

                        case DiffResult.Skip:
                            //nothing changed: reset and go to next
                            break;
                        
                        case DiffResult.DoneAndDestroy:
                        case DiffResult.Done:
                            if(diffResult == DiffResult.DoneAndDestroy)
                                _possibleId.Enqueue(eId);
                            if (writePosition > mtu)
                            {
                                _packetBuffer[1] = PacketDiffSync;
                                peer.Send(_packetBuffer, 0, mtu, DeliveryMethod.Unreliable);
                                partCount++;
                                if (partCount == MaxParts)
                                {
                                    Logger.LogWarning("[SEM] PART COUNT MAX");
                                    //send at next frame
                                    break;
                                }
                        
                                //repeat in next packet
                                _packetBuffer[4] = partCount;

                                Unsafe.CopyBlock(packetBuffer + 5, packetBuffer + mtu, (uint)(writePosition - mtu));
                                writePosition -= mtu - 5;
                            }
                            break;
                    }
                }
                //Debug.Log($"PARTS: {partCount} {_netDataWriter.Data[4]}");
                _packetBuffer[1] = PacketDiffSyncLast; //lastPart flag
                peer.Send(_packetBuffer, 0, writePosition, DeliveryMethod.Unreliable);
            }
            //trigger only when there is data
            _netPlayers[0].Peer.NetManager.TriggerUpdate();
        }
    }

    public static class ServerEntityManagerExt
    {
        //hack to avoid callvirt
        public static void Deserialize(this NetPlayer player, NetPacketReader reader)
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
                case EntityManager.PacketClientSync:
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
                    if(EntityManager.SequenceDiff(serverTick, player.ServerTick) > 0)
                        player.ServerTick = serverTick;

                    var inputBuffer = new InputBuffer(playerTick, reader);
                    if (!player.AvailableInput.Contains(inputBuffer) && EntityManager.SequenceDiff(playerTick, player.LastProcessedTick) > 0)
                    {
                        if (player.AvailableInput.Count >= EntityManager.MaxSavedStateDiff)
                        {
                            var minimal = player.AvailableInput.Min;
                            if (EntityManager.SequenceDiff(playerTick, minimal.Tick) > 0)
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
                
                case EntityManager.PacketEntityCall:
                    ushort entityId = reader.GetUShort();
                    byte packetId = reader.GetByte();
                    //GetEntityById(entityId)?.ProcessPacket(packetId, reader);
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
