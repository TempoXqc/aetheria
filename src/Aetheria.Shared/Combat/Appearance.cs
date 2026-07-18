using Aetheria.Shared.Protocol;

namespace Aetheria.Shared.Combat;

/// <summary>
/// A character's cosmetic customisation, chosen at creation and visible to everyone: skin tone,
/// face variant, hair style and colour, beard style and colour. Values are palette/style INDEXES —
/// the client maps them to actual colours and meshes, the server just stores and relays them
/// (after clamping, so a hostile client cannot smuggle out-of-range values into snapshots).
/// </summary>
public readonly struct Appearance
{
    public const byte SkinToneCount = 4;
    public const byte FaceCount = 3;
    public const byte HairStyleCount = 4;  // 0 short, 1 long, 2 mohawk, 3 bald
    public const byte HairColorCount = 6;
    public const byte BeardStyleCount = 4; // 0 none, 1 short, 2 long, 3 braided
    public const byte BeardColorCount = 6;

    public readonly byte SkinTone;
    public readonly byte Face;
    public readonly byte HairStyle;
    public readonly byte HairColor;
    public readonly byte BeardStyle;
    public readonly byte BeardColor;

    public Appearance(byte skinTone, byte face, byte hairStyle, byte hairColor, byte beardStyle, byte beardColor)
    {
        SkinTone = skinTone;
        Face = face;
        HairStyle = hairStyle;
        HairColor = hairColor;
        BeardStyle = beardStyle;
        BeardColor = beardColor;
    }

    /// <summary>Every index forced into its valid range (server-side trust boundary).</summary>
    public Appearance Clamped() => new(
        Min(SkinTone, SkinToneCount - 1),
        Min(Face, FaceCount - 1),
        Min(HairStyle, HairStyleCount - 1),
        Min(HairColor, HairColorCount - 1),
        Min(BeardStyle, BeardStyleCount - 1),
        Min(BeardColor, BeardColorCount - 1));

    private static byte Min(byte value, int max) => value > max ? (byte)max : value;

    public void Write(PacketWriter w)
    {
        w.WriteByte(SkinTone);
        w.WriteByte(Face);
        w.WriteByte(HairStyle);
        w.WriteByte(HairColor);
        w.WriteByte(BeardStyle);
        w.WriteByte(BeardColor);
    }

    public static Appearance Read(ref PacketReader r)
    {
        byte skin = r.ReadByte();
        byte face = r.ReadByte();
        byte hairStyle = r.ReadByte();
        byte hairColor = r.ReadByte();
        byte beardStyle = r.ReadByte();
        byte beardColor = r.ReadByte();
        return new Appearance(skin, face, hairStyle, hairColor, beardStyle, beardColor);
    }
}
