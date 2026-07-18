using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Shared.Client;

/// <summary>
/// A one-shot, unauthenticated query of a server's card for the server browser: connect, send
/// <see cref="ServerInfoRequest"/>, wait for the <see cref="ServerInfo"/> answer, done. Owns its
/// own transport so many probes can run in parallel (one per row of the browser). Call
/// <see cref="Pump"/> every frame until <see cref="Completed"/> or <see cref="TimedOut"/>,
/// then <see cref="Dispose"/>.
/// </summary>
public sealed class ServerProbe : IDisposable
{
    private readonly IClientTransport _transport;
    private readonly long _deadlineMs;
    private readonly long _resendEveryMs;
    private long _nextSendMs;

    public ServerProbe(IClientTransport transport, string host, int port, string accountId, long timeoutMs = 3000)
    {
        Guard.NotNull(transport, nameof(transport));
        _transport = transport;
        AccountId = accountId ?? string.Empty;
        _deadlineMs = SharedClock.NowMs + timeoutMs;
        _resendEveryMs = 500; // UDP: re-ask a few times rather than trusting one datagram
        _nextSendMs = 0;
        _transport.Connect(host, port);
    }

    public string AccountId { get; }

    /// <summary>Set once the server answered; check <see cref="Completed"/> first.</summary>
    public ServerInfo Info { get; private set; }

    public bool Completed { get; private set; }

    public bool TimedOut { get; private set; }

    /// <summary>Drive the probe; cheap to call every frame.</summary>
    public void Pump()
    {
        if (Completed || TimedOut)
        {
            return;
        }

        long now = SharedClock.NowMs;
        if (now >= _nextSendMs)
        {
            var writer = new PacketWriter();
            new ServerInfoRequest(SimulationConstants.ProtocolVersion, AccountId).Write(writer);
            _transport.Send(writer.WrittenSpan);
            _nextSendMs = now + _resendEveryMs;
        }

        while (_transport.Poll(out byte[] payload))
        {
            try
            {
                var reader = new PacketReader(payload);
                if ((MessageType)reader.ReadByte() == MessageType.ServerInfo)
                {
                    Info = ServerInfo.Read(ref reader);
                    Completed = true;
                    return;
                }
            }
            catch (MalformedPacketException)
            {
                // A hostile or corrupted datagram must never crash the browser; ignore it.
            }
        }

        if (now > _deadlineMs)
        {
            TimedOut = true;
        }
    }

    public void Dispose() => _transport.Dispose();
}
