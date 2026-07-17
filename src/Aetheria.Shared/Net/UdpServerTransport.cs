using System.Net;
using System.Net.Sockets;

namespace Aetheria.Shared.Net;

/// <summary>
/// A raw-UDP <see cref="IServerTransport"/> built on <see cref="Socket"/>. Peers are synthesized
/// from remote endpoints: the first datagram from a new endpoint creates a peer and raises a
/// <see cref="TransportEventKind.PeerConnected"/> event; peers that go silent past the timeout are
/// dropped with a <see cref="TransportEventKind.PeerDisconnected"/> event.
///
/// This deliberately provides ONLY unreliable, unordered delivery — that is what raw UDP is. The
/// snapshot model tolerates loss (each snapshot is a full picture of the client's AoI). Anything
/// that needs reliability (handshake, chat, inventory) should move to a transport that offers
/// reliable channels; that is exactly why this sits behind <see cref="IServerTransport"/>.
/// </summary>
public sealed class UdpServerTransport : IServerTransport
{
    private const int ReceiveBufferSize = 8 * 1024;

    private readonly long _timeoutMs;
    private readonly Dictionary<EndPoint, PeerId> _peersByEndpoint = new();
    private readonly Dictionary<PeerId, PeerConnection> _peers = new();
    private readonly Queue<ServerTransportEvent> _events = new();
    private readonly byte[] _receiveBuffer = new byte[ReceiveBufferSize];

    private Socket? _socket;
    private int _nextPeerId = 1;

    public UdpServerTransport(float timeoutSeconds = SimulationConstants.PeerTimeoutSeconds)
    {
        _timeoutMs = (long)(timeoutSeconds * 1000f);
    }

    public void Start(int port)
    {
        if (_socket is not null)
        {
            throw new InvalidOperationException("Transport already started.");
        }

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            Blocking = false,
        };
        _socket.Bind(new IPEndPoint(IPAddress.Any, port));
    }

    public bool Poll(out ServerTransportEvent evt)
    {
        DrainSocket();
        CheckTimeouts();

        if (_events.Count > 0)
        {
            evt = _events.Dequeue();
            return true;
        }

        evt = default;
        return false;
    }

    public void Send(PeerId peer, ReadOnlySpan<byte> data)
    {
        if (_socket is null || !_peers.TryGetValue(peer, out PeerConnection? conn))
        {
            return;
        }

        try
        {
            _socket.SendTo(data, SocketFlags.None, conn.EndPoint);
        }
        catch (SocketException)
        {
            // A send failure on a connectionless socket is transient from our perspective;
            // the peer will be reaped by timeout if it has genuinely gone away.
        }
    }

    public void Kick(PeerId peer)
    {
        if (_peers.Remove(peer, out PeerConnection? conn))
        {
            _peersByEndpoint.Remove(conn.EndPoint);
            _events.Enqueue(ServerTransportEvent.Disconnected(peer));
        }
    }

    public void Dispose()
    {
        _socket?.Dispose();
        _socket = null;
    }

    private void DrainSocket()
    {
        if (_socket is null)
        {
            return;
        }

        while (true)
        {
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int received;
            try
            {
                received = _socket.ReceiveFrom(_receiveBuffer, ref remote);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
            {
                return; // Nothing more to read this poll.
            }
            catch (SocketException)
            {
                // On Windows a previous SendTo to a closed endpoint can surface here as a reset
                // (ConnectionReset) on the next receive. Ignore and keep draining.
                continue;
            }

            if (received <= 0)
            {
                continue;
            }

            PeerId peer = ResolvePeer(remote);
            byte[] payload = _receiveBuffer.AsSpan(0, received).ToArray();
            _events.Enqueue(ServerTransportEvent.Received(peer, payload));
        }
    }

    private PeerId ResolvePeer(EndPoint remote)
    {
        if (_peersByEndpoint.TryGetValue(remote, out PeerId existing))
        {
            _peers[existing].LastSeenMs = Environment.TickCount64;
            return existing;
        }

        var peer = new PeerId(_nextPeerId++);
        var clonedEndpoint = (EndPoint)remote; // ReceiveFrom hands back a fresh IPEndPoint each call.
        _peersByEndpoint[clonedEndpoint] = peer;
        _peers[peer] = new PeerConnection(clonedEndpoint) { LastSeenMs = Environment.TickCount64 };
        _events.Enqueue(ServerTransportEvent.Connected(peer));
        return peer;
    }

    private void CheckTimeouts()
    {
        long now = Environment.TickCount64;
        List<PeerId>? expired = null;

        foreach ((PeerId peer, PeerConnection conn) in _peers)
        {
            if (now - conn.LastSeenMs >= _timeoutMs)
            {
                (expired ??= new List<PeerId>()).Add(peer);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (PeerId peer in expired)
        {
            if (_peers.Remove(peer, out PeerConnection? conn))
            {
                _peersByEndpoint.Remove(conn.EndPoint);
                _events.Enqueue(ServerTransportEvent.Disconnected(peer));
            }
        }
    }

    private sealed class PeerConnection(EndPoint endPoint)
    {
        public EndPoint EndPoint { get; } = endPoint;
        public long LastSeenMs { get; set; }
    }
}
