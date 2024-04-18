using System;
using LiteNetLib;

namespace LiteEntitySystem.Transport
{
    public class LiteNetLibNetPeer : AbstractNetPeer
    {
        public readonly NetPeer NetPeer;
        
        public LiteNetLibNetPeer(NetPeer netPeer, bool assignToTag)
        {
            NetPeer = netPeer;
            if(assignToTag)
                NetPeer.Tag = this;
        }

        public override void TriggerSend() => NetPeer.NetManager.TriggerUpdate();
        public override void SendReliableOrdered(ReadOnlySpan<byte> data) => NetPeer.Send(data, 0, DeliveryMethod.ReliableOrdered);
        public override void SendUnreliable(ReadOnlySpan<byte> data) => NetPeer.Send(data, 0, DeliveryMethod.Unreliable);
        public override int GetMaxUnreliablePacketSize() => NetPeer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable);
        public override string ToString() => NetPeer.ToString();
    }

    public static class LiteNetLibExtensions
    {
        public static LiteNetLibNetPeer GetLiteNetLibNetPeerFromTag(this NetPeer peer) => (LiteNetLibNetPeer)peer.Tag;
        public static LiteNetLibNetPeer GetLiteNetLibNetPeer(this NetPlayer player) => (LiteNetLibNetPeer)player.Peer;
    }
}