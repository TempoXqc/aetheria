using Aetheria.Shared.Math;
using Aetheria.Shared.Protocol;

namespace Aetheria.Tests;

public static class ProtocolTests
{
    [Test("ConnectRequest survives a write/read round trip.")]
    public static void ConnectRequest_RoundTrips()
    {
        var writer = new PacketWriter();
        new ConnectRequest(protocolVersion: 1, name: "Aria").Write(writer);

        var reader = new PacketReader(writer.WrittenSpan);
        Assert.Equal(MessageType.ConnectRequest, (MessageType)reader.ReadByte());
        ConnectRequest decoded = ConnectRequest.Read(ref reader);

        Assert.Equal((byte)1, decoded.ProtocolVersion);
        Assert.Equal("Aria", decoded.Name);
        Assert.Equal(0, reader.Remaining);
    }

    [Test("InputCommand survives a write/read round trip.")]
    public static void InputCommand_RoundTrips()
    {
        var writer = new PacketWriter();
        new InputCommand(sequence: 99, new Vec2(0.5f, -0.25f)).Write(writer);

        var reader = new PacketReader(writer.WrittenSpan);
        Assert.Equal(MessageType.InputCommand, (MessageType)reader.ReadByte());
        InputCommand decoded = InputCommand.Read(ref reader);

        Assert.Equal(99u, decoded.Sequence);
        Assert.Close(0.5f, decoded.MoveDirection.X);
        Assert.Close(-0.25f, decoded.MoveDirection.Y);
    }

    [Test("A Snapshot with multiple entities round-trips exactly.")]
    public static void Snapshot_RoundTrips()
    {
        var entities = new[]
        {
            new EntitySnapshot(1, EntityKind.Player, new Vec2(10f, 20f)),
            new EntitySnapshot(2, EntityKind.Monster, new Vec2(-3f, 4f)),
        };

        var writer = new PacketWriter();
        new SnapshotMessage(tick: 12345, entities).Write(writer);

        var reader = new PacketReader(writer.WrittenSpan);
        Assert.Equal(MessageType.Snapshot, (MessageType)reader.ReadByte());
        SnapshotMessage decoded = SnapshotMessage.Read(ref reader);

        Assert.Equal(12345u, decoded.Tick);
        Assert.Equal(2, decoded.Entities.Count);
        Assert.Equal(1, decoded.Entities[0].Id);
        Assert.Equal(EntityKind.Monster, decoded.Entities[1].Kind);
        Assert.Close(-3f, decoded.Entities[1].Position.X);
    }

    [Test("Pong round-trips both timestamps.")]
    public static void Pong_RoundTrips()
    {
        var writer = new PacketWriter();
        new Pong(clientTimeMs: 111, serverTimeMs: 222).Write(writer);

        var reader = new PacketReader(writer.WrittenSpan);
        Assert.Equal(MessageType.Pong, (MessageType)reader.ReadByte());
        Pong decoded = Pong.Read(ref reader);

        Assert.Equal(111L, decoded.ClientTimeMs);
        Assert.Equal(222L, decoded.ServerTimeMs);
    }

    [Test("Reading past the end of a truncated packet throws instead of reading garbage.")]
    public static void TruncatedPacket_Throws()
    {
        // A PacketReader is a ref struct, so it is created *inside* the lambda (it cannot be
        // captured). Here only one byte is available where an int (4 bytes) is expected.
        Assert.Throws<MalformedPacketException>(() =>
        {
            var reader = new PacketReader(new byte[] { 0x01 });
            reader.ReadInt();
        });
    }
}
