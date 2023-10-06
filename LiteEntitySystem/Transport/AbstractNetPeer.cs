using System;

namespace LiteEntitySystem.Transport
{
    public abstract class AbstractNetPeer
    {
        public abstract void TriggerSend();
        public abstract void SendReliableOrdered(ReadOnlySpan<byte> data);
        public abstract void SendUnreliable(ReadOnlySpan<byte> data);
        public abstract int GetMaxUnreliablePacketSize();
        
        internal NetPlayer AssignedPlayer;
    }
}