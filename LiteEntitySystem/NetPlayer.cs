using System;
using System.Collections.Generic;
using LiteEntitySystem.Collections;
using LiteEntitySystem.Internal;
using LiteEntitySystem.Transport;

namespace LiteEntitySystem
{
    public enum NetPlayerState
    {
        Active,
        WaitingForFirstInput,
        WaitingForFirstInputProcess,
        RequestBaseline,
        Removed
    }

    internal struct FirstInputHeader
    {
        public ushort Tick;
        public ushort LatestServerTick;
    }

    internal struct InputInfo
    {
        public FirstInputHeader FirstInputHeader;
        public ushort Tick => FirstInputHeader.Tick;
        public ushort LatestServerTick => FirstInputHeader.LatestServerTick;
        public InputPacketHeader Header;

        public InputInfo(ushort tick, InputPacketHeader header)
        {
            FirstInputHeader.Tick = tick;
            //updated before send
            FirstInputHeader.LatestServerTick = 0;
            Header = header;
        }
    }
    
    public class NetPlayer
    {
        public readonly byte Id;
        public readonly AbstractNetPeer Peer;
        
        internal ushort LastProcessedTick;
        internal ushort LastReceivedTick;
        internal ushort LatestServerTick;
        internal ushort StateATick;
        internal ushort StateBTick;
        internal float LerpTime;
        
        //server only
        internal bool FirstBaselineSent;
        internal NetPlayerState State;
        internal readonly SequenceBinaryHeap<InputInfo> AvailableInput;
        internal readonly Dictionary<EntityLogic, SyncGroupData> EntitySyncInfo;
        internal DateTime ServerTickChangedTime;
        internal readonly Queue<RemoteCallPacket> PendingRPCs;
        internal int PendingRPCSize;
        
        internal NetPlayer(AbstractNetPeer peer, byte id)
        {
            Id = id;
            Peer = peer;
        }
        
        //server constructor
        internal NetPlayer(AbstractNetPeer peer, byte id, int serverMaxStoredInputs) : this(peer, id)
        {
            AvailableInput = new SequenceBinaryHeap<InputInfo>(serverMaxStoredInputs);
            EntitySyncInfo = new();
            State = NetPlayerState.RequestBaseline;
            PendingRPCs = new();
        }

        internal void LoadInputInfo(InputPacketHeader inputData)
        {
            StateATick = inputData.StateA;
            StateBTick = inputData.StateB;
            LerpTime = inputData.LerpMsec;
        }
        
        internal void LoadInputInfo(InputInfo inputData)
        {
            LastProcessedTick = inputData.Tick;
            StateATick = inputData.Header.StateA;
            StateBTick = inputData.Header.StateB;
            LerpTime = inputData.Header.LerpMsec;
        }

        internal void EnqueueRpc(RemoteCallPacket remoteCallPacket)
        {
            PendingRPCSize += remoteCallPacket.TotalSize;
            PendingRPCs.Enqueue(remoteCallPacket);
            remoteCallPacket.RefCount++;
        }

        internal void NotifyRPCResized(int prevTotalSize, int newTotalSize)
        {
            PendingRPCSize -= prevTotalSize;
            PendingRPCSize += newTotalSize;
        }

        internal void RemoveAllRpcs(Queue<RemoteCallPacket> rpcPool)
        {
            while (PendingRPCs.TryDequeue(out var rpcNode))
            {
                rpcNode.RefCount--;
                if(rpcNode.RefCount == 0)
                    rpcPool.Enqueue(rpcNode);
            }
            PendingRPCSize = 0;
        }

        internal void RemoveOldRpcs(Queue<RemoteCallPacket> rpcPool)
        {
            while (PendingRPCs.TryPeek(out var rpcNode) && Utils.SequenceDiff(rpcNode.Header.Tick, LatestServerTick) <= 0)
            {
                PendingRPCSize -= rpcNode.TotalSize;
                PendingRPCs.Dequeue();
                rpcNode.RefCount--;
                if(rpcNode.RefCount == 0)
                    rpcPool.Enqueue(rpcNode);
            }
        }
    }
}