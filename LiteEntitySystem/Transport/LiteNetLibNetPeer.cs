using System;
using LiteNetLib;

namespace LiteEntitySystem.Transport
{
    public class LiteNetLibNetPeer : AbstractNetPeer
    {
        public readonly LiteNetPeer NetPeer;
        
        public LiteNetLibNetPeer(LiteNetPeer netPeer, bool assignToTag)
        {
            NetPeer = netPeer;
            if(assignToTag)
                NetPeer.Tag = this;
        }

        public override void TriggerSend() => NetPeer.NetManager.TriggerUpdate();
        public override void SendReliableOrdered(ReadOnlySpan<byte> data) => NetPeer.Send(data, DeliveryMethod.ReliableOrdered);
        public override void SendUnreliable(ReadOnlySpan<byte> data) => NetPeer.Send(data, DeliveryMethod.Unreliable);
        public override int GetMaxUnreliablePacketSize() => NetPeer.GetMaxSinglePacketSize(DeliveryMethod.Unreliable);
        public override string ToString() => NetPeer.ToString();
    }

    public static class LiteNetLibExtensions
    {
        public static LiteNetLibNetPeer GetLiteNetLibNetPeerFromTag(this LiteNetPeer peer) => (LiteNetLibNetPeer)peer.Tag;
        public static LiteNetLibNetPeer GetLiteNetLibNetPeer(this NetPlayer player) => (LiteNetLibNetPeer)player.Peer;
    }
}