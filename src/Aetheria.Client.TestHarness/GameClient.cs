using Aetheria.Shared;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Client.TestHarness;

/// <summary>
/// A headless client connection state machine used to exercise the server end-to-end without a
/// rendering engine. The Unity client will grow its own richer version (prediction, interpolation,
/// rendering), but the wire handling and message flow modelled here are exactly what it must do.
/// </summary>
public sealed class GameClient
{
    private readonly IClientTransport _transport;
    private readonly PacketWriter _writer = new();
    private uint _inputSequence;

    public GameClient(IClientTransport transport) => _transport = transport;

    /// <summary>Our own entity id once the handshake succeeds; null until then.</summary>
    public int? EntityId { get; private set; }

    public bool WasRejected { get; private set; }
    public string? RejectReason { get; private set; }

    /// <summary>Most recent server tick we have received a snapshot for.</summary>
    public uint LastTick { get; private set; }

    /// <summary>Entities inside our area of interest as of the last snapshot.</summary>
    public IReadOnlyList<EntitySnapshot> Visible { get; private set; } = [];

    /// <summary>Last measured round-trip time in milliseconds, or -1 if unknown.</summary>
    public long LastRttMs { get; private set; } = -1;

    public void Connect(string host, int port, string name)
    {
        _transport.Connect(host, port);
        Send(new ConnectRequest(SimulationConstants.ProtocolVersion, name));
    }

    public void SendInput(Vec2 direction)
    {
        _inputSequence++;
        Send(new InputCommand(_inputSequence, direction));
    }

    public void SendPing() => Send(new Ping(Environment.TickCount64));

    public void SendDisconnect() => Send(new Disconnect());

    /// <summary>Process every datagram currently waiting from the server.</summary>
    public void Pump()
    {
        while (_transport.Poll(out byte[] payload))
        {
            Handle(payload);
        }
    }

    private void Handle(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return;
        }

        try
        {
            var reader = new PacketReader(payload);
            var type = (MessageType)reader.ReadByte();

            switch (type)
            {
                case MessageType.ConnectAccepted:
                    ConnectAccepted accepted = ConnectAccepted.Read(ref reader);
                    EntityId = accepted.EntityId;
                    break;

                case MessageType.ConnectRejected:
                    ConnectRejected rejected = ConnectRejected.Read(ref reader);
                    WasRejected = true;
                    RejectReason = rejected.Reason;
                    break;

                case MessageType.Snapshot:
                    SnapshotMessage snapshot = SnapshotMessage.Read(ref reader);
                    LastTick = snapshot.Tick;
                    Visible = snapshot.Entities;
                    break;

                case MessageType.Pong:
                    Pong pong = Pong.Read(ref reader);
                    LastRttMs = Environment.TickCount64 - pong.ClientTimeMs;
                    break;

                default:
                    break;
            }
        }
        catch (MalformedPacketException)
        {
            // Ignore corrupt datagrams.
        }
    }

    /// <summary>Find our own entity in the latest snapshot, if present.</summary>
    public bool TryGetSelf(out EntitySnapshot self)
    {
        if (EntityId is int id)
        {
            foreach (EntitySnapshot e in Visible)
            {
                if (e.Id == id)
                {
                    self = e;
                    return true;
                }
            }
        }

        self = default;
        return false;
    }

    private void Send(ConnectRequest msg) => SendWith(msg.Write);
    private void Send(InputCommand msg) => SendWith(msg.Write);
    private void Send(Ping msg) => SendWith(msg.Write);
    private void Send(Disconnect msg) => SendWith(msg.Write);

    private void SendWith(Action<PacketWriter> write)
    {
        _writer.Reset();
        write(_writer);
        _transport.Send(_writer.WrittenSpan);
    }
}
