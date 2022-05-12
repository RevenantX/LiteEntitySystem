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

        private readonly Queue<ushort> _possibleId = new Queue<ushort>();
        private readonly byte[] _packetBuffer = new byte[NetConstants.MaxPacketSize*(MaxParts+1)];
        private readonly StateSerializer[] _savedEntityData = new StateSerializer[MaxEntityCount];
        
        private readonly NetPlayer[] _netPlayersArray = new NetPlayer[MaxPlayers];
        private readonly NetPlayer[] _netPlayersDict = new NetPlayer[MaxPlayers];
        
        private readonly Queue<RemoteCallPacket> _rpcPool = new Queue<RemoteCallPacket>();
        private readonly Queue<byte[]> _inputPool = new Queue<byte[]>();
        private readonly NetDataReader _inputReader = new NetDataReader();
        private readonly Queue<byte> _playerIdQueue = new Queue<byte>(MaxPlayers);

        private byte[] _compressionBuffer;
        private int _netPlayersCount;
        private ushort _nextId;
        private bool _lagCompensationEnabled;
        
        public ServerSendRate SendRate;
        public event Action<bool> OnLagCompensation;

        public ServerEntityManager(byte packetHeader, int framesPerSecond) 
            : base(NetworkMode.Server, framesPerSecond)
        {
            InternalPlayerId = ServerPlayerId;
            for (byte i = 1; i < byte.MaxValue; i++)
                _playerIdQueue.Enqueue(i);

            _packetBuffer[0] = packetHeader;
        }
        
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

        public bool RemovePlayerFromPeerTag(NetPeer player)
        {
            return RemovePlayer(player.Tag as NetPlayer);
        }

        public bool RemovePlayer(NetPlayer player)
        {
            if (player == null || _netPlayersDict[player.Id] == null)
                return false;
            
            _netPlayersDict[player.Id] = null;
            _netPlayersCount--;
            _possibleId.Enqueue(player.Id);
            
            if (player.ArrayIndex != _netPlayersCount)
            {
                _netPlayersArray[player.ArrayIndex] = _netPlayersArray[_netPlayersCount];
                _netPlayersArray[player.ArrayIndex].ArrayIndex = _netPlayersCount;
                _netPlayersArray[_netPlayersCount] = null;
            }

            return true;
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
            ref var stateSerializer = ref _savedEntityData[entityId];

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
            if (player.AvailableInput.Count == 0)
                return;
            
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

        protected override void OnLogicTick()
        {
            //write previous history
            ServerTick = Tick;
            for (int pidx = 0; pidx < _netPlayersCount; pidx++)
            {
                var player = _netPlayersArray[pidx];
                if (!player.IsFirstStateReceived) 
                    continue;
                
                foreach (var controller in GetControllers<HumanControllerLogic>())
                {
                    if (player.Id == controller.OwnerId)
                        ProcessInput(controller, player);
                }
            }
            
            foreach (var aliveEntity in AliveEntities)
                aliveEntity.Update();
            foreach (var aliveEntity in AliveEntities)
                _savedEntityData[aliveEntity.Id].WriteHistory(ServerTick);
        }
        
        internal void DestroySavedData(EntityLogic entityLogic)
        {
            _savedEntityData[entityLogic.Id].Destroy(ServerTick);
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
            _savedEntityData[entityId].AddRpcPacket(rpc);
        }
        
        internal unsafe void AddRemoteCall<T>(ushort entityId, T[] value, int count, RemoteCall remoteCallInfo) where T : struct
        {
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(remoteCallInfo, count);
            rpc.Tick = Tick;
            fixed (byte* rawData = rpc.Data, rawValue = Unsafe.As<byte[]>(value))
                Unsafe.CopyBlock(rawData, rawValue, rpc.Size);
            _savedEntityData[entityId].AddRpcPacket(rpc);
        }
        
        public unsafe void AddSyncableCall<T>(SyncableField field, T value, MethodInfo method) where T : struct
        {
            var entity = EntitiesDict[field.EntityId];
            var remoteCallInfo = ClassDataDict[entity.ClassId].SyncableRemoteCalls[method];
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(remoteCallInfo, field.FieldId);
            rpc.Tick = Tick;
            rpc.Flags = ExecuteFlags.ExecuteOnServer | ExecuteFlags.SendToOther | ExecuteFlags.SendToOwner;
            fixed (byte* rawData = rpc.Data)
                Unsafe.Copy(rawData, ref value);
            _savedEntityData[field.EntityId].AddRpcPacket(rpc);
        }
        
        public unsafe void AddSyncableCall<T>(SyncableField field, T[] value, int count, MethodInfo method) where T : struct
        {
            var entity = EntitiesDict[field.EntityId];
            var remoteCallInfo = ClassDataDict[entity.ClassId].SyncableRemoteCalls[method];
            var rpc = _rpcPool.Count > 0 ? _rpcPool.Dequeue() : new RemoteCallPacket();
            rpc.Init(remoteCallInfo, field.FieldId, count);
            rpc.Tick = Tick;
            rpc.Flags = ExecuteFlags.ExecuteOnServer | ExecuteFlags.SendToOther | ExecuteFlags.SendToOwner;
            fixed (byte* rawData = rpc.Data, rawValue = Unsafe.As<byte[]>(value))
                Unsafe.CopyBlock(rawData, rawValue, rpc.Size);
            _savedEntityData[field.EntityId].AddRpcPacket(rpc);
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
                _savedEntityData[entity.Id].EnableLagCompensation(player);
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
                _savedEntityData[entity.Id].DisableLagCompensation();
                //entity.DebugPrint();
            }
            OnLagCompensation?.Invoke(false);
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
                        packetBuffer[1] = PacketBaselineSync;
                        for (int i = 0; i <= MaxEntityId; i++)
                        {
                            _savedEntityData[i].MakeBaseline(Tick, packetBuffer, ref writePosition);
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
                        var diffResult = _savedEntityData[eId].MakeDiff(
                            netPlayer.Id,
                            minimalTick,
                            Tick,
                            netPlayer.LastReceivedState,
                            packetBuffer,
                            ref writePosition);
                        switch (diffResult)
                        {
                            //default and skip - nothing changed: reset and go to next
                            case DiffResult.RequestBaselineSync:
                                netPlayer.IsNew = true;
                                netPlayer.IsFirstStateReceived = false;
                                break;

                            case DiffResult.DoneAndDestroy:
                            case DiffResult.Done:
                                if (diffResult == DiffResult.DoneAndDestroy)
                                    _possibleId.Enqueue(eId);
                                if (writePosition > mtu)
                                {
                                    _packetBuffer[1] = PacketDiffSync;
                                    peer.Send(_packetBuffer, 0, mtu, DeliveryMethod.Unreliable);
                                    (*partCount)++;
                                    if (*partCount == MaxParts)
                                    {
                                        Logger.LogWarning("[SEM] PART COUNT MAX");
                                        //send at next frame
                                        break;
                                    }

                                    //repeat in next packet
                                    Unsafe.CopyBlock(packetBuffer + 5, packetBuffer + mtu, (uint)(writePosition - mtu));
                                    writePosition -= mtu - 5;
                                }

                                break;
                        }
                    }

                    //Debug.Log($"PARTS: {partCount} {_netDataWriter.Data[4]}");
                    packetBuffer[1] = PacketDiffSyncLast; //lastPart flag
                    peer.Send(_packetBuffer, 0, writePosition, DeliveryMethod.Unreliable);
                }
            }

            //trigger only when there is data
            _netPlayersArray[0].Peer.NetManager.TriggerUpdate();
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

        public void Deserialize(NetPeer peer, NetPacketReader reader)
        {
            Deserialize((NetPlayer)peer.Tag, reader);
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
    }
}
