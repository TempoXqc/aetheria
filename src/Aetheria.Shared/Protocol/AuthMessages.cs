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

    /// <summary>
    /// True: explicitly REGISTER this account (fails if it already exists). False: sign in to an
    /// existing account (fails if unknown). No more silent account auto-creation on first login.
    /// </summary>
    public readonly bool CreateAccount;

    public Login(byte protocolVersion, string accountId, string accountSecret, bool createAccount = false)
    {
        ProtocolVersion = protocolVersion;
        AccountId = accountId;
        AccountSecret = accountSecret;
        CreateAccount = createAccount;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.Login);
        w.WriteByte(ProtocolVersion);
        w.WriteString(AccountId);
        w.WriteString(AccountSecret);
        w.WriteBool(CreateAccount);
    }

    public static Login Read(ref PacketReader r)
    {
        byte version = r.ReadByte();
        string accountId = r.ReadString();
        string secret = r.ReadString();
        bool create = r.ReadBool();
        return new Login(version, accountId, secret, create);
    }
}

/// <summary>
/// Asks a server to describe itself (name, population, whether this account has a character
/// there). Answered in the AwaitingLogin phase, before any authentication — the server browser
/// uses it to draw its table. Carries the protocol version so incompatible servers say so.
/// </summary>
public readonly struct ServerInfoRequest
{
    public readonly byte ProtocolVersion;
    public readonly string AccountId; // may be empty: population info only

    public ServerInfoRequest(byte protocolVersion, string accountId)
    {
        ProtocolVersion = protocolVersion;
        AccountId = accountId;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.ServerInfoRequest);
        w.WriteByte(ProtocolVersion);
        w.WriteString(AccountId);
    }

    public static ServerInfoRequest Read(ref PacketReader r)
    {
        byte version = r.ReadByte();
        string accountId = r.ReadString();
        return new ServerInfoRequest(version, accountId);
    }
}

/// <summary>
/// A server's card in the server browser: its NAME (servers are named, not numbered), its
/// population, and — if the request carried an account — that account's character there.
/// A full server still lets existing characters play, but refuses new character creation.
/// </summary>
public readonly struct ServerInfo
{
    public readonly string Name;
    public readonly int Online;
    public readonly int Capacity;

    /// <summary>False when the server is full — you can still play an EXISTING character.</summary>
    public readonly bool AcceptsNewCharacters;

    public readonly bool HasAccount;
    public readonly bool HasCharacter;
    public readonly string CharacterName;
    public readonly byte CharacterLevel;

    public ServerInfo(string name, int online, int capacity, bool acceptsNewCharacters,
        bool hasAccount, bool hasCharacter, string characterName, byte characterLevel)
    {
        Name = name;
        Online = online;
        Capacity = capacity;
        AcceptsNewCharacters = acceptsNewCharacters;
        HasAccount = hasAccount;
        HasCharacter = hasCharacter;
        CharacterName = characterName;
        CharacterLevel = characterLevel;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.ServerInfo);
        w.WriteString(Name);
        w.WriteInt(Online);
        w.WriteInt(Capacity);
        w.WriteBool(AcceptsNewCharacters);
        w.WriteBool(HasAccount);
        w.WriteBool(HasCharacter);
        w.WriteString(CharacterName);
        w.WriteByte(CharacterLevel);
    }

    public static ServerInfo Read(ref PacketReader r)
    {
        string name = r.ReadString();
        int online = r.ReadInt();
        int capacity = r.ReadInt();
        bool accepts = r.ReadBool();
        bool hasAccount = r.ReadBool();
        bool hasCharacter = r.ReadBool();
        string characterName = r.ReadString();
        byte level = r.ReadByte();
        return new ServerInfo(name, online, capacity, accepts, hasAccount, hasCharacter, characterName, level);
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

    /// <summary>The character's cosmetic customisation, so the lobby can render a 3D preview.</summary>
    public readonly Appearance Appearance;

    /// <summary>
    /// Item id per equipment slot (index = (int)EquipSlot), so the lobby preview shows the
    /// character EXACTLY as they stand in the world — same armor, same weapon. May be empty.
    /// </summary>
    public readonly byte[] Equipment;

    public LoginResult(bool ok, string message, bool hasCharacter,
        string characterName, byte raceId, byte classId, Gender gender, byte level,
        Appearance appearance = default, byte[]? equipment = null)
    {
        Ok = ok;
        Message = message;
        HasCharacter = hasCharacter;
        CharacterName = characterName;
        RaceId = raceId;
        ClassId = classId;
        Gender = gender;
        Level = level;
        Appearance = appearance;
        Equipment = equipment ?? System.Array.Empty<byte>();
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
        Appearance.Write(w);
        byte[] equipment = Equipment ?? System.Array.Empty<byte>();
        w.WriteByte((byte)equipment.Length);
        for (int i = 0; i < equipment.Length; i++)
        {
            w.WriteByte(equipment[i]);
        }
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
        Appearance appearance = Appearance.Read(ref r);
        var equipment = new byte[r.ReadByte()];
        for (int i = 0; i < equipment.Length; i++)
        {
            equipment[i] = r.ReadByte();
        }

        return new LoginResult(ok, message, hasCharacter, name, raceId, classId, gender, level,
            appearance, equipment);
    }
}

/// <summary>Create this server's ONE character for the logged-in account, then enter the world.</summary>
public readonly struct CreateCharacter
{
    public readonly string Name;
    public readonly byte RaceId;
    public readonly byte ClassId;
    public readonly Gender Gender;
    public readonly Appearance Appearance;

    public CreateCharacter(string name, byte raceId, byte classId, Gender gender, Appearance appearance = default)
    {
        Name = name;
        RaceId = raceId;
        ClassId = classId;
        Gender = gender;
        Appearance = appearance;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.CreateCharacter);
        w.WriteString(Name);
        w.WriteByte(RaceId);
        w.WriteByte(ClassId);
        w.WriteByte((byte)Gender);
        Appearance.Write(w);
    }

    public static CreateCharacter Read(ref PacketReader r)
    {
        string name = r.ReadString();
        byte raceId = r.ReadByte();
        byte classId = r.ReadByte();
        var gender = (Gender)r.ReadByte();
        Appearance appearance = Appearance.Read(ref r);
        return new CreateCharacter(name, raceId, classId, gender, appearance);
    }
}

/// <summary>Enter the world with the account's existing character (no payload).</summary>
public readonly struct EnterWorld
{
    public void Write(PacketWriter w) => w.WriteByte((byte)MessageType.EnterWorld);

    public static EnterWorld Read(ref PacketReader r) => default;
}

/// <summary>
/// Permanently delete the account's ONE character on this server (sent from the character
/// screen only — the server refuses it in-world). The server answers with a fresh
/// <see cref="LoginResult"/> (HasCharacter = false), which flips the client to creation.
/// </summary>
public readonly struct DeleteCharacter
{
    public void Write(PacketWriter w) => w.WriteByte((byte)MessageType.DeleteCharacter);

    public static DeleteCharacter Read(ref PacketReader r) => default;
}

/// <summary>
/// Use the HEARTHSTONE: teleport home (the bound inn). 15-minute cooldown, server-enforced.
/// From an instance it walks you out first. Refusals come back as an InstanceResult message.
/// </summary>
public readonly struct Hearthstone
{
    public void Write(PacketWriter w) => w.WriteByte((byte)MessageType.Hearthstone);

    public static Hearthstone Read(ref PacketReader r) => default;
}

/// <summary>Bind the hearthstone HERE — accepted only while standing near an innkeeper.</summary>
public readonly struct SetHome
{
    public void Write(PacketWriter w) => w.WriteByte((byte)MessageType.SetHome);

    public static SetHome Read(ref PacketReader r) => default;
}
