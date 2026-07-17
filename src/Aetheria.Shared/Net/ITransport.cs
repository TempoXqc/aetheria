namespace Aetheria.Shared.Net;

/// <summary>Opaque identifier for a connected peer, assigned by the server transport.</summary>
public readonly record struct PeerId(int Value)
{
    public override string ToString() => $"peer#{Value}";
}

/// <summary>What kind of thing <see cref="IServerTransport.Poll"/> just surfaced.</summary>
public enum TransportEventKind : byte
{
    PeerConnected,
    PacketReceived,
    PeerDisconnected,
}

/// <summary>A single event drained from the server transport.</summary>
public readonly struct ServerTransportEvent
{
    public readonly TransportEventKind Kind;
    public readonly PeerId Peer;

    /// <summary>The datagram bytes for <see cref="TransportEventKind.PacketReceived"/>; otherwise empty.</summary>
    public readonly byte[] Payload;

    private ServerTransportEvent(TransportEventKind kind, PeerId peer, byte[] payload)
    {
        Kind = kind;
        Peer = peer;
        Payload = payload;
    }

    public static ServerTransportEvent Connected(PeerId peer)
        => new(TransportEventKind.PeerConnected, peer, []);

    public static ServerTransportEvent Received(PeerId peer, byte[] payload)
        => new(TransportEventKind.PacketReceived, peer, payload);

    public static ServerTransportEvent Disconnected(PeerId peer)
        => new(TransportEventKind.PeerDisconnected, peer, []);
}

/// <summary>
/// Server-side network transport, abstracted so the game logic never depends on a concrete
/// networking library. The walking skeleton ships <see cref="UdpServerTransport"/> (raw UDP via
/// System.Net.Sockets). A production build can drop in a LiteNetLib/ENet-backed implementation
/// (reliable channels, encryption, connection management) behind this same interface without the
/// simulation code changing.
///
/// Intended usage is single-threaded and poll-driven: the game loop calls <see cref="Poll"/> to
/// drain events at the top of each tick, then <see cref="Send"/> outbound packets.
/// </summary>
public interface IServerTransport : IDisposable
{
    /// <summary>Bind and begin listening on the given UDP port.</summary>
    void Start(int port);

    /// <summary>
    /// Dequeue the next pending transport event, returning false when none remain this poll.
    /// Also advances internal bookkeeping (receiving datagrams, detecting timeouts).
    /// </summary>
    bool Poll(out ServerTransportEvent evt);

    /// <summary>Send a datagram to a specific peer. No-op if the peer is unknown.</summary>
    void Send(PeerId peer, ReadOnlySpan<byte> data);

    /// <summary>Forcibly drop a peer; a subsequent poll surfaces a disconnect event for it.</summary>
    void Kick(PeerId peer);
}

/// <summary>
/// Client-side counterpart of <see cref="IServerTransport"/>: a single logical connection to one
/// server. Also poll-driven.
/// </summary>
public interface IClientTransport : IDisposable
{
    /// <summary>Resolve and bind toward the given server host/port.</summary>
    void Connect(string host, int port);

    /// <summary>Send a datagram to the server.</summary>
    void Send(ReadOnlySpan<byte> data);

    /// <summary>Dequeue the next datagram received from the server, or false if none are pending.</summary>
    bool Poll(out byte[] payload);
}
