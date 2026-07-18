using Aetheria.Shared.Combat;
using Aetheria.Shared.Math;
using Aetheria.Shared.Protocol;

namespace Aetheria.Tests;

public static class ProtocolTests
{
    [Test("Login and LoginResult survive write/read round trips.")]
    public static void LoginFlow_RoundTrips()
    {
        var w1 = new PacketWriter();
        new Login(protocolVersion: 10, accountId: "acct-42", accountSecret: "hunter2",
            createAccount: true).Write(w1);
        var r1 = new PacketReader(w1.WrittenSpan);
        Assert.Equal(MessageType.Login, (MessageType)r1.ReadByte());
        Login login = Login.Read(ref r1);
        Assert.Equal((byte)10, login.ProtocolVersion);
        Assert.Equal("acct-42", login.AccountId);
        Assert.Equal("hunter2", login.AccountSecret);
        Assert.True(login.CreateAccount);

        var w2 = new PacketWriter();
        new LoginResult(true, "", true, "Aria", 3, 2, Gender.Female, 4).Write(w2);
        var r2 = new PacketReader(w2.WrittenSpan);
        Assert.Equal(MessageType.LoginResult, (MessageType)r2.ReadByte());
        LoginResult result = LoginResult.Read(ref r2);
        Assert.True(result.Ok);
        Assert.True(result.HasCharacter);
        Assert.Equal("Aria", result.CharacterName);
        Assert.Equal(Gender.Female, result.Gender);
        Assert.Equal((byte)4, result.Level);

        var w3 = new PacketWriter();
        new CreateCharacter("Borin", 4, 1, Gender.Male).Write(w3);
        var r3 = new PacketReader(w3.WrittenSpan);
        Assert.Equal(MessageType.CreateCharacter, (MessageType)r3.ReadByte());
        CreateCharacter create = CreateCharacter.Read(ref r3);
        Assert.Equal("Borin", create.Name);
        Assert.Equal((byte)4, create.RaceId);
    }

    [Test("ServerInfoRequest and ServerInfo (the server-browser card) round-trip.")]
    public static void ServerInfo_RoundTrips()
    {
        var w1 = new PacketWriter();
        new ServerInfoRequest(protocolVersion: 13, accountId: "tempo").Write(w1);
        var r1 = new PacketReader(w1.WrittenSpan);
        Assert.Equal(MessageType.ServerInfoRequest, (MessageType)r1.ReadByte());
        ServerInfoRequest request = ServerInfoRequest.Read(ref r1);
        Assert.Equal("tempo", request.AccountId);

        var w2 = new PacketWriter();
        new ServerInfo("Royaume du Nord", online: 87, capacity: 100, acceptsNewCharacters: true,
            hasAccount: true, hasCharacter: true, characterName: "Thorgar", characterLevel: 7).Write(w2);
        var r2 = new PacketReader(w2.WrittenSpan);
        Assert.Equal(MessageType.ServerInfo, (MessageType)r2.ReadByte());
        ServerInfo info = ServerInfo.Read(ref r2);
        Assert.Equal("Royaume du Nord", info.Name);
        Assert.Equal(87, info.Online);
        Assert.Equal(100, info.Capacity);
        Assert.True(info.AcceptsNewCharacters);
        Assert.True(info.HasCharacter);
        Assert.Equal("Thorgar", info.CharacterName);
        Assert.Equal((byte)7, info.CharacterLevel);
    }

    [Test("UseAbility survives a write/read round trip.")]
    public static void UseAbility_RoundTrips()
    {
        var writer = new PacketWriter();
        new UseAbility(abilityId: 2, targetEntityId: 77).Write(writer);

        var reader = new PacketReader(writer.WrittenSpan);
        Assert.Equal(MessageType.UseAbility, (MessageType)reader.ReadByte());
        UseAbility decoded = UseAbility.Read(ref reader);

        Assert.Equal((byte)2, decoded.AbilityId);
        Assert.Equal(77, decoded.TargetEntityId);
    }

    [Test("CombatEvent survives a write/read round trip.")]
    public static void CombatEvent_RoundTrips()
    {
        var writer = new PacketWriter();
        new CombatEventMessage(attackerId: 1, targetId: 2, abilityId: 1, damage: 20,
            targetRemainingHealth: 40, targetKilled: false).Write(writer);

        var reader = new PacketReader(writer.WrittenSpan);
        Assert.Equal(MessageType.CombatEvent, (MessageType)reader.ReadByte());
        CombatEventMessage decoded = CombatEventMessage.Read(ref reader);

        Assert.Equal(1, decoded.AttackerId);
        Assert.Equal(2, decoded.TargetId);
        Assert.Equal(20, decoded.Damage);
        Assert.Equal(40, decoded.TargetRemainingHealth);
        Assert.False(decoded.TargetKilled);
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
            new EntitySnapshot(1, EntityKind.Player, Faction.Horde, new Vec2(10f, 20f), health: 100, maxHealth: 120, resource: 40, maxResource: 100,
                facingRadians: 1.5f, level: 4, name: "Zug", raceId: 2, classId: 1, gender: Gender.Female),
            new EntitySnapshot(2, EntityKind.Monster, Faction.Neutral, new Vec2(-3f, 4f), health: 30, maxHealth: 60, resource: 0, maxResource: 0,
                facingRadians: 0f, level: 3, name: "Loup", raceId: 2),
        };

        var writer = new PacketWriter();
        new SnapshotMessage(tick: 12345, entities).Write(writer);

        var reader = new PacketReader(writer.WrittenSpan);
        Assert.Equal(MessageType.Snapshot, (MessageType)reader.ReadByte());
        SnapshotMessage decoded = SnapshotMessage.Read(ref reader);

        Assert.Equal(12345u, decoded.Tick);
        Assert.Equal(2, decoded.Entities.Count);
        Assert.Equal(1, decoded.Entities[0].Id);
        Assert.Equal(100, decoded.Entities[0].Health);
        Assert.Equal(Faction.Horde, decoded.Entities[0].Faction);
        Assert.Equal(40, decoded.Entities[0].Resource);
        Assert.Equal(EntityKind.Monster, decoded.Entities[1].Kind);
        Assert.Close(-3f, decoded.Entities[1].Position.X);
        Assert.Equal(60, decoded.Entities[1].MaxHealth);
        Assert.Equal((byte)2, decoded.Entities[0].RaceId);
        Assert.Equal((byte)1, decoded.Entities[0].ClassId);
        Assert.Equal(Gender.Female, decoded.Entities[0].Gender);
        Assert.Equal((byte)2, decoded.Entities[1].RaceId);
        Assert.Equal((byte)0, decoded.Entities[1].ClassId);
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
