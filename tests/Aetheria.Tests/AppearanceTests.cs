using Aetheria.Server.Persistence;
using Aetheria.Server.World;
using Aetheria.Shared.Combat;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Tests;

public static class AppearanceTests
{
    [Test("Appearance survives a CreateCharacter write/read round trip.")]
    public static void CreateCharacter_CarriesAppearance()
    {
        var look = new Appearance(skinTone: 2, face: 1, hairStyle: 3, hairColor: 4, beardStyle: 2, beardColor: 5);
        var w = new PacketWriter();
        new CreateCharacter("Brakk", 2, 1, Gender.Male, look).Write(w);

        var r = new PacketReader(w.WrittenSpan);
        Assert.Equal(MessageType.CreateCharacter, (MessageType)r.ReadByte());
        CreateCharacter decoded = CreateCharacter.Read(ref r);

        Assert.Equal((byte)2, decoded.Appearance.SkinTone);
        Assert.Equal((byte)1, decoded.Appearance.Face);
        Assert.Equal((byte)3, decoded.Appearance.HairStyle);
        Assert.Equal((byte)4, decoded.Appearance.HairColor);
        Assert.Equal((byte)2, decoded.Appearance.BeardStyle);
        Assert.Equal((byte)5, decoded.Appearance.BeardColor);
    }

    [Test("Snapshots relay each player's appearance so everyone renders everyone correctly.")]
    public static void Snapshot_CarriesAppearance()
    {
        var world = new World();
        var look = new Appearance(1, 2, 1, 3, 1, 0);
        ServerEntity p = world.SpawnPlayer(new PeerId(1), "Look", 1, 1, Gender.Female, look);

        List<EntitySnapshot> snap = world.BuildAreaSnapshot(p.Position);
        EntitySnapshot found = default;
        foreach (EntitySnapshot e in snap)
        {
            if (e.Id == p.Id) { found = e; }
        }

        Assert.Equal(p.Id, found.Id);
        Assert.Equal((byte)1, found.Appearance.SkinTone);
        Assert.Equal((byte)2, found.Appearance.Face);
        Assert.Equal((byte)3, found.Appearance.HairColor);
        Assert.Equal((byte)1, found.Appearance.BeardStyle);
    }

    [Test("Out-of-range appearance indexes from a hostile client are clamped at spawn.")]
    public static void HostileAppearance_IsClamped()
    {
        var world = new World();
        var hostile = new Appearance(255, 255, 255, 255, 255, 255);
        ServerEntity p = world.SpawnPlayer(new PeerId(1), "Hax", 1, 1, Gender.Male, hostile);

        Assert.True(p.Appearance.SkinTone < Appearance.SkinToneCount);
        Assert.True(p.Appearance.Face < Appearance.FaceCount);
        Assert.True(p.Appearance.HairStyle < Appearance.HairStyleCount);
        Assert.True(p.Appearance.HairColor < Appearance.HairColorCount);
        Assert.True(p.Appearance.BeardStyle < Appearance.BeardStyleCount);
        Assert.True(p.Appearance.BeardColor < Appearance.BeardColorCount);
    }

    [Test("Appearance is captured into the durable character record (it survives reconnects).")]
    public static void Persistence_CapturesAppearance()
    {
        var world = new World();
        var look = new Appearance(3, 2, 2, 5, 3, 1);
        ServerEntity p = world.SpawnPlayer(new PeerId(1), "Durable", 4, 1, Gender.Male, look);

        CharacterRecord record = CharacterMapper.Capture(p);

        Assert.Equal((byte)3, record.SkinTone);
        Assert.Equal((byte)2, record.Face);
        Assert.Equal((byte)2, record.HairStyle);
        Assert.Equal((byte)5, record.HairColor);
        Assert.Equal((byte)3, record.BeardStyle);
        Assert.Equal((byte)1, record.BeardColor);
    }
}
