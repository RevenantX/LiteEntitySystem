using System;
using LiteNetLib;

namespace LiteEntitySystem.Transport
{
    public class LiteNetLibNetPeer : AbstractNetPeer
    {
        private readonly NetPeer _localPeer;
        
        public LiteNetLibNetPeer(NetPeer localPeer, bool assignToTag)
        {
            _localPeer = localPeer;
            if(assignToTag)
                _localPeer.Tag = this;
        }
        
        public override void TriggerSend()
        {
            _localPeer.NetManager.TriggerUpdate();
        }

        public override void SendReliableOrdered(ReadOnlySpan<byte> data)
        {
            _localPeer.Send(data, 0, DeliveryMethod.ReliableOrdered);
        }

        public override void SendUnreliable(ReadOnlySpan<byte> data)
        {
            _localPeer.Send(data, 0, DeliveryMethod.Unreliable);
        }

        public override int GetMaxUnreliablePacketSize()
        {
            return _localPeer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable);
        }

        public override string ToString()
        {
            return _localPeer.EndPoint.ToString();
        }
    }

    public static class LiteNetLibExtensions
    {
        public static AbstractNetPeer GetAbstractNetPeerFromTag(this NetPeer peer)
        {
            return (AbstractNetPeer)peer.Tag;
        }
    }
}