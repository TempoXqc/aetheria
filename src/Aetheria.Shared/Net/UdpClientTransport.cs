using System.Net;
using System.Net.Sockets;

namespace Aetheria.Shared.Net;

/// <summary>
/// A raw-UDP <see cref="IClientTransport"/>. Connects the socket to the server endpoint so
/// send/receive need no per-call address, then polls non-blocking for inbound datagrams.
/// The same swap-behind-the-interface story as the server transport applies here.
/// </summary>
public sealed class UdpClientTransport : IClientTransport
{
    private const int ReceiveBufferSize = 8 * 1024;

    private readonly byte[] _receiveBuffer = new byte[ReceiveBufferSize];
    private Socket? _socket;

    public void Connect(string host, int port)
    {
        if (_socket is not null)
        {
            throw new InvalidOperationException("Transport already connected.");
        }

        IPAddress address = ResolveHost(host);
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            Blocking = false,
        };

        // "Connecting" a UDP socket just fixes the default peer for Send/Receive.
        _socket.Connect(new IPEndPoint(address, port));
    }

    public void Send(ReadOnlySpan<byte> data)
    {
        if (_socket is null)
        {
            throw new InvalidOperationException("Transport is not connected.");
        }

        try
        {
            _socket.Send(data, SocketFlags.None);
        }
        catch (SocketException)
        {
            // Unreliable by design — a dropped send is not fatal.
        }
    }

    public bool Poll(out byte[] payload)
    {
        payload = [];
        if (_socket is null)
        {
            return false;
        }

        try
        {
            int received = _socket.Receive(_receiveBuffer, SocketFlags.None);
            if (received <= 0)
            {
                return false;
            }

            payload = _receiveBuffer.AsSpan(0, received).ToArray();
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
        {
            return false;
        }
        catch (SocketException)
        {
            // e.g. ConnectionReset when the server is not up yet — treat as "nothing received".
            return false;
        }
    }

    public void Dispose()
    {
        _socket?.Dispose();
        _socket = null;
    }

    private static IPAddress ResolveHost(string host)
    {
        if (IPAddress.TryParse(host, out IPAddress? parsed))
        {
            return parsed;
        }

        IPAddress[] addresses = Dns.GetHostAddresses(host);
        foreach (IPAddress candidate in addresses)
        {
            if (candidate.AddressFamily == AddressFamily.InterNetwork)
            {
                return candidate;
            }
        }

        throw new ArgumentException($"Could not resolve host '{host}' to an IPv4 address.", nameof(host));
    }
}
