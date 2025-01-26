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
        RequestBaseline
    }

    public struct InputInfo
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
        internal NetPlayerState State;
        internal SequenceBinaryHeap<InputInfo> AvailableInput;

        internal NetPlayer(AbstractNetPeer peer, byte id)
        {
            Id = id;
            Peer = peer;
        }
    }
}