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

    internal struct InputInfo
    {
        public ushort Tick;
        public InputPacketHeader Header;

        public InputInfo(ushort tick, InputPacketHeader header)
        {
            Tick = tick;
            Header = header;
        }
    }
    
    public class NetPlayer
    {
        public readonly byte Id;
        public readonly AbstractNetPeer Peer;
        
        internal ushort LastProcessedTick;
        internal ushort LastReceivedTick;
        internal ushort CurrentServerTick;
        internal ushort StateATick;
        internal ushort StateBTick;
        internal float LerpTime;
        
        //server only
        internal bool FirstBaselineSent;
        internal NetPlayerState State;
        internal readonly SequenceBinaryHeap<InputInfo> AvailableInput;
        internal readonly Dictionary<EntityLogic, SyncGroupData> EntitySyncInfo;

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
    }
}