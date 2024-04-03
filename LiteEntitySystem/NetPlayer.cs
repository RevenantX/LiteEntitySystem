using LiteEntitySystem.Collections;
using LiteEntitySystem.Transport;

namespace LiteEntitySystem
{
    public class NetPlayer
    {
        public readonly byte Id;
        public readonly AbstractNetPeer Peer;
        
        internal ushort LastProcessedTick;
        internal ushort LastReceivedTick;
        internal ushort CurrentServerTick;
        internal ushort StateATick;
        internal ushort StateBTick;
        internal ushort SimulatedServerTick;
        internal float LerpTime;
        internal NetPlayerState State;
        internal readonly SequenceBinaryHeap<InputBuffer> AvailableInput = new (ServerEntityManager.MaxStoredInputs);

        internal NetPlayer(AbstractNetPeer peer, byte id)
        {
            Id = id;
            Peer = peer;
        }
    }
}