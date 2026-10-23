using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem.Transport
{
    /// <summary>
    /// A deterministic in-memory connection between a client and server peer.
    /// </summary>
    public sealed class DirectConnection
    {
        private readonly Queue<byte[]> _clientToServer = new();
        private readonly Queue<byte[]> _serverToClient = new();

        /// <summary>
        /// Creates a connection with the specified maximum unreliable packet size.
        /// </summary>
        /// <param name="maxUnreliablePacketSize">Maximum payload size reported by either peer.</param>
        public DirectConnection(int maxUnreliablePacketSize = 1200)
        {
            if (maxUnreliablePacketSize <= Marshal.SizeOf<LastPartData>() || maxUnreliablePacketSize > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(maxUnreliablePacketSize));

            ClientPeer = new DirectConnectionPeer(_clientToServer, maxUnreliablePacketSize);
            ServerPeer = new DirectConnectionPeer(_serverToClient, maxUnreliablePacketSize);
        }

        /// <summary>
        /// Gets the peer to pass to a client entity manager.
        /// </summary>
        public DirectConnectionPeer ClientPeer { get; }

        /// <summary>
        /// Gets the peer to add to a server entity manager.
        /// </summary>
        public DirectConnectionPeer ServerPeer { get; }

        /// <summary>
        /// Delivers packets queued before this call, client to server and then server to client.
        /// Packets sent by receive callbacks are deferred until the next call.
        /// </summary>
        public void Pump()
        {
            Deliver(_clientToServer, ServerPeer);
            Deliver(_serverToClient, ClientPeer);
        }

        private static void Deliver(Queue<byte[]> queue, DirectConnectionPeer recipient)
        {
            int count = queue.Count;
            for (int i = 0; i < count; i++)
                recipient.Deliver(queue.Dequeue());
        }
    }

    /// <summary>
    /// An endpoint in a <see cref="DirectConnection"/>.
    /// </summary>
    public sealed class DirectConnectionPeer : AbstractNetPeer
    {
        private readonly Queue<byte[]> _outgoingQueue;
        private readonly int _maxUnreliablePacketSize;

        internal DirectConnectionPeer(Queue<byte[]> outgoingQueue, int maxUnreliablePacketSize)
        {
            _outgoingQueue = outgoingQueue;
            _maxUnreliablePacketSize = maxUnreliablePacketSize;
        }

        /// <summary>
        /// Occurs when the connection is pumped and a packet is delivered to this endpoint.
        /// </summary>
        public event Action<ReadOnlyMemory<byte>> PacketReceived;

        /// <inheritdoc />
        public override void TriggerSend()
        {
        }

        /// <inheritdoc />
        public override void SendReliableOrdered(ReadOnlySpan<byte> data) => _outgoingQueue.Enqueue(data.ToArray());

        /// <inheritdoc />
        public override void SendUnreliable(ReadOnlySpan<byte> data) => _outgoingQueue.Enqueue(data.ToArray());

        /// <inheritdoc />
        public override int GetMaxUnreliablePacketSize() => _maxUnreliablePacketSize;

        internal void Deliver(byte[] data) => PacketReceived?.Invoke(data);
    }
}