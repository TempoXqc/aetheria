using Aetheria.Shared.Combat;

namespace Aetheria.Shared.Protocol;

/// <summary>
/// Step 1 of the handshake: authenticate the ACCOUNT (no character data yet). The server answers
/// with a <see cref="LoginResult"/> describing the account's character on this server, if any.
/// </summary>
public readonly struct Login
{
    public readonly byte ProtocolVersion;
    public readonly string AccountId;
    public readonly string AccountSecret;

    public Login(byte protocolVersion, string accountId, string accountSecret)
    {
        ProtocolVersion = protocolVersion;
        AccountId = accountId;
        AccountSecret = accountSecret;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.Login);
        w.WriteByte(ProtocolVersion);
        w.WriteString(AccountId);
        w.WriteString(AccountSecret);
    }

    public static Login Read(ref PacketReader r)
    {
        byte version = r.ReadByte();
        string accountId = r.ReadString();
        string secret = r.ReadString();
        return new Login(version, accountId, secret);
    }
}

/// <summary>
/// Answer to <see cref="Login"/> — and also the error channel for character creation/entry.
/// When Ok and HasCharacter, the summary fields describe the account's ONE character on this
/// server (one character per account per server; other servers hold other characters).
/// </summary>
public readonly struct LoginResult
{
    public readonly bool Ok;
    public readonly string Message;
    public readonly bool HasCharacter;
    public readonly string CharacterName;
    public readonly byte RaceId;
    public readonly byte ClassId;
    public readonly Gender Gender;
    public readonly byte Level;

    public LoginResult(bool ok, string message, bool hasCharacter,
        string characterName, byte raceId, byte classId, Gender gender, byte level)
    {
        Ok = ok;
        Message = message;
        HasCharacter = hasCharacter;
        CharacterName = characterName;
        RaceId = raceId;
        ClassId = classId;
        Gender = gender;
        Level = level;
    }

    public static LoginResult Failure(string message)
        => new(false, message, false, string.Empty, 0, 0, Gender.Male, 1);

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.LoginResult);
        w.WriteBool(Ok);
        w.WriteString(Message);
        w.WriteBool(HasCharacter);
        w.WriteString(CharacterName);
        w.WriteByte(RaceId);
        w.WriteByte(ClassId);
        w.WriteByte((byte)Gender);
        w.WriteByte(Level);
    }

    public static LoginResult Read(ref PacketReader r)
    {
        bool ok = r.ReadBool();
        string message = r.ReadString();
        bool hasCharacter = r.ReadBool();
        string name = r.ReadString();
        byte raceId = r.ReadByte();
        byte classId = r.ReadByte();
        var gender = (Gender)r.ReadByte();
        byte level = r.ReadByte();
        return new LoginResult(ok, message, hasCharacter, name, raceId, classId, gender, level);
    }
}

/// <summary>Create this server's ONE character for the logged-in account, then enter the world.</summary>
public readonly struct CreateCharacter
{
    public readonly string Name;
    public readonly byte RaceId;
    public readonly byte ClassId;
    public readonly Gender Gender;

    public CreateCharacter(string name, byte raceId, byte classId, Gender gender)
    {
        Name = name;
        RaceId = raceId;
        ClassId = classId;
        Gender = gender;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.CreateCharacter);
        w.WriteString(Name);
        w.WriteByte(RaceId);
        w.WriteByte(ClassId);
        w.WriteByte((byte)Gender);
    }

    public static CreateCharacter Read(ref PacketReader r)
    {
        string name = r.ReadString();
        byte raceId = r.ReadByte();
        byte classId = r.ReadByte();
        var gender = (Gender)r.ReadByte();
        return new CreateCharacter(name, raceId, classId, gender);
    }
}

/// <summary>Enter the world with the account's existing character (no payload).</summary>
public readonly struct EnterWorld
{
    public void Write(PacketWriter w) => w.WriteByte((byte)MessageType.EnterWorld);

    public static EnterWorld Read(ref PacketReader r) => default;
}
