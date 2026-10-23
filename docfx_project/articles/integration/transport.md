---
description: The transport abstraction - what AbstractNetPeer must provide, sharing a connection with your own packets, and writing a custom transport.
---

# Transport layer

The library never talks to a socket: it hands byte spans to an `AbstractNetPeer` and expects two delivery modes back. LiteNetLib is the shipped implementation; anything that can offer reliable-ordered and unreliable delivery can replace it.

## What a transport must provide

`AbstractNetPeer` has four members:

| Member | Contract |
|---|---|
| `SendReliableOrdered(ReadOnlySpan<byte>)` | Guaranteed delivery, in order. Used for baselines and client requests |
| `SendUnreliable(ReadOnlySpan<byte>)` | Best effort, no retransmission. Used for state deltas and inputs |
| `GetMaxUnreliablePacketSize()` | Largest payload that fits one unreliable packet, so state can be split into parts |
| `TriggerSend()` | Flush now rather than at the transport's next opportunity |

The library relies on those semantics being honest. Unreliable must really be allowed to drop packets — layering it on a reliable stream adds head-of-line blocking exactly where the design assumes there is none, and stale states would queue up behind lost ones instead of being superseded.

## Using LiteNetLib

Wrap the connection and pass it in — server side per player, client side once:

```csharp
// server
var player = _entityManager.AddPlayer(new LiteNetLibNetPeer(peer, assignToTag: true));

// client
_entityManager = new ClientEntityManager(typesMap, new LiteNetLibNetPeer(peer, true), headerByte);
```

`assignToTag: true` stores the adapter in LiteNetLib's `Tag`, which is how later callbacks recover it (`(LiteNetLibNetPeer)peer.Tag`). The extension methods `GetLiteNetLibNetPeerFromTag()` and `GetLiteNetLibNetPeer()` do the same lookups in one call.

## Sharing a connection
## In-memory direct connection

`DirectConnection` provides a deterministic, socket-free client/server pair. Bind each endpoint's `PacketReceived` event to the matching manager and call `Pump()` after updates to deliver queued packets:

```csharp
var connection = new DirectConnection();
var player = serverManager.AddPlayer(connection.ServerPeer);
var clientManager = new ClientEntityManager(typesMap, connection.ClientPeer, headerByte);

connection.ServerPeer.PacketReceived += packet =>
    serverManager.Deserialize(connection.ServerPeer, packet.Span);
connection.ClientPeer.PacketReceived += packet =>
    clientManager.Deserialize(packet.Span);

// After server/client updates, deliver packets queued in either direction.
connection.Pump();
```

The connection copies payloads when they are sent. `Pump()` processes packets already queued at its start, client to server before server to client; packets sent from a receive callback are delivered by the next call. Both delivery methods are deterministic and loss-free, so this is useful for integration tests but does not simulate unreliable packet loss. The optional constructor argument sets the maximum unreliable packet size reported by both peers (default `1200`).

## Sharing a connection

Every packet the library produces starts with the header byte given to the manager, so your own protocol can travel on the same connection. Route by peeking at the first byte:

```csharp
// server side
if ((PacketType)reader.PeekByte() == PacketType.EntitySystem)
    _entityManager.Deserialize((LiteNetLibNetPeer)peer.Tag, reader.GetRemainingBytesSpan());
else
    _myProtocol.Read(reader, peer);
```

`Deserialize` also validates the header itself and returns `HeaderCheckFailed` for anything that is not a library packet, so a mistake here is diagnosable rather than corrupting.

## Writing a custom transport

Implement the four members over your own connection object and use it wherever `LiteNetLibNetPeer` would appear — the managers accept any `AbstractNetPeer`:

**MyTransportPeer.cs**

```csharp
using System;
using LiteEntitySystem.Transport;

public class MyTransportPeer : AbstractNetPeer
{
    private readonly MyConnection _connection;

    public MyTransportPeer(MyConnection connection) => _connection = connection;

    public override void SendReliableOrdered(ReadOnlySpan<byte> data) =>
        _connection.Send(data, reliable: true);

    public override void SendUnreliable(ReadOnlySpan<byte> data) =>
        _connection.Send(data, reliable: false);

    public override int GetMaxUnreliablePacketSize() => _connection.Mtu - MyConnection.HeaderOverhead;

    public override void TriggerSend() => _connection.Flush();
}
```

Then feed received bytes to `Deserialize` — with the sending player on the server, without arguments on the client — and the rest of the library is unchanged. Peer-management APIs still work: `AddPlayer(peer)` accepts your type, and `NetPlayer.Peer` hands it back.

Report `GetMaxUnreliablePacketSize` honestly: too large and packets fragment or drop below the library, too small and state is split into more parts than necessary.

> [!WARNING]
> **Common mistakes**
>
> * Implementing "unreliable" as reliable — deltas then queue behind lost packets, adding latency the design specifically avoids.
> * An optimistic `GetMaxUnreliablePacketSize` — states are sized against it; overshooting means silent drops at the network layer.
> * Skipping `TriggerSend` — everything still works, but each send waits for the transport's own schedule, adding avoidable latency.
> * Feeding foreign packets into `Deserialize` — check the header byte first; the return value tells you when you got it wrong.

## Related pages

- [hosting.md](hosting.md)
- [limits.md](limits.md)
