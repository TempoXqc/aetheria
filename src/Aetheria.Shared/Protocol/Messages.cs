using Aetheria.Shared.Combat;
using Aetheria.Shared.Items;
using Aetheria.Shared.Math;

namespace Aetheria.Shared.Protocol;

/*
 * Wire format
 * -----------
 * Every packet is a single UDP datagram laid out as:
 *
 *     [1 byte MessageType][payload...]
 *
 * Each message type below writes its own leading MessageType byte in Write(), and expects the
 * type byte to have ALREADY been consumed before its Read() runs (the transport/dispatch layer
 * peeks the first byte to decide which Read to call). Keeping encode/decode next to each other,
 * per message, is what stops the client and server wire formats from silently drifting apart.
 */

// ----------------------------------------------------------------------------------------------
// Client -> Server
// ----------------------------------------------------------------------------------------------

/// <summary>A request to use an ability on a target entity. The server validates range and cooldown.</summary>
public readonly struct UseAbility
{
    public readonly byte AbilityId;
    public readonly int TargetEntityId;

    public UseAbility(byte abilityId, int targetEntityId)
    {
        AbilityId = abilityId;
        TargetEntityId = targetEntityId;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.UseAbility);
        w.WriteByte(AbilityId);
        w.WriteInt(TargetEntityId);
    }

    public static UseAbility Read(ref PacketReader r)
    {
        byte abilityId = r.ReadByte();
        int targetEntityId = r.ReadInt();
        return new UseAbility(abilityId, targetEntityId);
    }
}

/// <summary>
/// A movement intent plus the direction the character is facing (radians on the world plane,
/// e.g. from the mouse cursor). The server is authoritative: it decides what this input produces.
/// </summary>
public readonly struct InputCommand
{
    public readonly uint Sequence;
    public readonly Vec2 MoveDirection;
    public readonly float FacingRadians;

    public InputCommand(uint sequence, Vec2 moveDirection, float facingRadians = 0f)
    {
        Sequence = sequence;
        MoveDirection = moveDirection;
        FacingRadians = facingRadians;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.InputCommand);
        w.WriteUInt(Sequence);
        w.WriteFloat(MoveDirection.X);
        w.WriteFloat(MoveDirection.Y);
        w.WriteFloat(FacingRadians);
    }

    public static InputCommand Read(ref PacketReader r)
    {
        uint sequence = r.ReadUInt();
        float x = r.ReadFloat();
        float y = r.ReadFloat();
        float facing = r.ReadFloat();
        return new InputCommand(sequence, new Vec2(x, y), facing);
    }
}

/// <summary>Liveness / round-trip-time probe. Server echoes it back as a <see cref="Pong"/>.</summary>
public readonly struct Ping
{
    public readonly long ClientTimeMs;

    public Ping(long clientTimeMs) => ClientTimeMs = clientTimeMs;

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.Ping);
        w.WriteLong(ClientTimeMs);
    }

    public static Ping Read(ref PacketReader r) => new(r.ReadLong());
}

/// <summary>Graceful disconnect notice (no payload).</summary>
public readonly struct Disconnect
{
    public void Write(PacketWriter w) => w.WriteByte((byte)MessageType.Disconnect);

    public static Disconnect Read(ref PacketReader r) => default;
}

// ----------------------------------------------------------------------------------------------
// Server -> Client
// ----------------------------------------------------------------------------------------------

/// <summary>Handshake success. Tells the client which entity is "theirs" and the server tick rate.</summary>
public readonly struct ConnectAccepted
{
    public readonly int EntityId;
    public readonly byte TickRate;

    public ConnectAccepted(int entityId, byte tickRate)
    {
        EntityId = entityId;
        TickRate = tickRate;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.ConnectAccepted);
        w.WriteInt(EntityId);
        w.WriteByte(TickRate);
    }

    public static ConnectAccepted Read(ref PacketReader r)
    {
        int entityId = r.ReadInt();
        byte tickRate = r.ReadByte();
        return new ConnectAccepted(entityId, tickRate);
    }
}

/// <summary>Handshake failure with a human-readable reason (protocol mismatch, server full, ...).</summary>
public readonly struct ConnectRejected
{
    public readonly string Reason;

    public ConnectRejected(string reason) => Reason = reason;

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.ConnectRejected);
        w.WriteString(Reason);
    }

    public static ConnectRejected Read(ref PacketReader r) => new(r.ReadString());
}

/// <summary>Per-entity state as it appears inside a <see cref="SnapshotMessage"/>.</summary>
public readonly struct EntitySnapshot
{
    public readonly int Id;
    public readonly EntityKind Kind;
    public readonly Faction Faction;
    public readonly Vec2 Position;
    public readonly int Health;
    public readonly int MaxHealth;
    public readonly int Resource;
    public readonly int MaxResource;

    /// <summary>Direction the entity faces, radians on the world plane (0 = +X).</summary>
    public readonly float FacingRadians;

    /// <summary>Character/creature level (for nameplates and con-colouring).</summary>
    public readonly byte Level;

    /// <summary>Display name (player name or monster name), shown on nameplates.</summary>
    public readonly string Name;

    /// <summary>
    /// Player: race id. Monster: monster definition id (so the client picks the right model).
    /// Corpse: race id of the fallen character (0 for monster corpses).
    /// </summary>
    public readonly byte RaceId;

    /// <summary>Player: class id (drives the weapon model). 0 for non-players.</summary>
    public readonly byte ClassId;

    /// <summary>Player: cosmetic gender. Male for non-players.</summary>
    public readonly Gender Gender;

    public EntitySnapshot(
        int id, EntityKind kind, Faction faction, Vec2 position,
        int health, int maxHealth, int resource, int maxResource, float facingRadians = 0f,
        byte level = 1, string name = "", byte raceId = 0, byte classId = 0, Gender gender = Gender.Male)
    {
        Id = id;
        Kind = kind;
        Faction = faction;
        Position = position;
        Health = health;
        MaxHealth = maxHealth;
        Resource = resource;
        MaxResource = maxResource;
        FacingRadians = facingRadians;
        Level = level;
        Name = name;
        RaceId = raceId;
        ClassId = classId;
        Gender = gender;
    }

    public void Write(PacketWriter w)
    {
        w.WriteInt(Id);
        w.WriteByte((byte)Kind);
        w.WriteByte((byte)Faction);
        w.WriteFloat(Position.X);
        w.WriteFloat(Position.Y);
        w.WriteInt(Health);
        w.WriteInt(MaxHealth);
        w.WriteInt(Resource);
        w.WriteInt(MaxResource);
        w.WriteFloat(FacingRadians);
        w.WriteByte(Level);
        w.WriteString(Name);
        w.WriteByte(RaceId);
        w.WriteByte(ClassId);
        w.WriteByte((byte)Gender);
    }

    public static EntitySnapshot Read(ref PacketReader r)
    {
        int id = r.ReadInt();
        var kind = (EntityKind)r.ReadByte();
        var faction = (Faction)r.ReadByte();
        float x = r.ReadFloat();
        float y = r.ReadFloat();
        int health = r.ReadInt();
        int maxHealth = r.ReadInt();
        int resource = r.ReadInt();
        int maxResource = r.ReadInt();
        float facing = r.ReadFloat();
        byte level = r.ReadByte();
        string name = r.ReadString();
        byte raceId = r.ReadByte();
        byte classId = r.ReadByte();
        var gender = (Gender)r.ReadByte();
        return new EntitySnapshot(
            id, kind, faction, new Vec2(x, y), health, maxHealth, resource, maxResource, facing, level, name,
            raceId, classId, gender);
    }
}

/// <summary>
/// The world as this client should see it right now: only the entities inside the client's
/// area of interest (see <see cref="Spatial.SpatialGrid"/>). Sent every tick per connected player.
/// </summary>
public readonly struct SnapshotMessage
{
    public readonly uint Tick;
    public readonly IReadOnlyList<EntitySnapshot> Entities;

    public SnapshotMessage(uint tick, IReadOnlyList<EntitySnapshot> entities)
    {
        Tick = tick;
        Entities = entities;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.Snapshot);
        w.WriteUInt(Tick);
        w.WriteUInt((uint)Entities.Count);
        for (int i = 0; i < Entities.Count; i++)
        {
            Entities[i].Write(w);
        }
    }

    public static SnapshotMessage Read(ref PacketReader r)
    {
        uint tick = r.ReadUInt();
        uint count = r.ReadUInt();
        var entities = new EntitySnapshot[count];
        for (uint i = 0; i < count; i++)
        {
            entities[i] = EntitySnapshot.Read(ref r);
        }

        return new SnapshotMessage(tick, entities);
    }
}

/// <summary>Reply to a <see cref="Ping"/>; carries the original client time plus the server time.</summary>
public readonly struct Pong
{
    public readonly long ClientTimeMs;
    public readonly long ServerTimeMs;

    public Pong(long clientTimeMs, long serverTimeMs)
    {
        ClientTimeMs = clientTimeMs;
        ServerTimeMs = serverTimeMs;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.Pong);
        w.WriteLong(ClientTimeMs);
        w.WriteLong(ServerTimeMs);
    }

    public static Pong Read(ref PacketReader r)
    {
        long clientTime = r.ReadLong();
        long serverTime = r.ReadLong();
        return new Pong(clientTime, serverTime);
    }
}

/// <summary>
/// A resolved combat interaction, broadcast to clients near the participants so they can play hit
/// reactions, floating damage numbers, and death effects. The authoritative outcome (damage, whether
/// the target died) has already been applied server-side; this is a notification, not a request.
/// </summary>
public readonly struct CombatEventMessage
{
    public readonly int AttackerId;
    public readonly int TargetId;
    public readonly byte AbilityId;
    public readonly int Damage;
    public readonly int TargetRemainingHealth;
    public readonly bool TargetKilled;

    public CombatEventMessage(
        int attackerId,
        int targetId,
        byte abilityId,
        int damage,
        int targetRemainingHealth,
        bool targetKilled)
    {
        AttackerId = attackerId;
        TargetId = targetId;
        AbilityId = abilityId;
        Damage = damage;
        TargetRemainingHealth = targetRemainingHealth;
        TargetKilled = targetKilled;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.CombatEvent);
        w.WriteInt(AttackerId);
        w.WriteInt(TargetId);
        w.WriteByte(AbilityId);
        w.WriteInt(Damage);
        w.WriteInt(TargetRemainingHealth);
        w.WriteBool(TargetKilled);
    }

    public static CombatEventMessage Read(ref PacketReader r)
    {
        int attackerId = r.ReadInt();
        int targetId = r.ReadInt();
        byte abilityId = r.ReadByte();
        int damage = r.ReadInt();
        int remaining = r.ReadInt();
        bool killed = r.ReadBool();
        return new CombatEventMessage(attackerId, targetId, abilityId, damage, remaining, killed);
    }
}

/// <summary>The caster's own progression + currency + combat stats, sent to that client (not others).</summary>
public readonly struct PlayerStatus
{
    public readonly int Level;
    public readonly int TotalXp;

    /// <summary>Total XP needed for the next level, or -1 at the cap.</summary>
    public readonly int XpForNextLevel;
    public readonly int Gold;
    public readonly int EffectiveAttack;
    public readonly int EffectiveDefense;

    public PlayerStatus(int level, int totalXp, int xpForNextLevel, int gold,
        int effectiveAttack = 0, int effectiveDefense = 0)
    {
        Level = level;
        TotalXp = totalXp;
        XpForNextLevel = xpForNextLevel;
        Gold = gold;
        EffectiveAttack = effectiveAttack;
        EffectiveDefense = effectiveDefense;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.PlayerStatus);
        w.WriteInt(Level);
        w.WriteInt(TotalXp);
        w.WriteInt(XpForNextLevel);
        w.WriteInt(Gold);
        w.WriteInt(EffectiveAttack);
        w.WriteInt(EffectiveDefense);
    }

    public static PlayerStatus Read(ref PacketReader r)
    {
        int level = r.ReadInt();
        int totalXp = r.ReadInt();
        int xpForNext = r.ReadInt();
        int gold = r.ReadInt();
        int attack = r.ReadInt();
        int defense = r.ReadInt();
        return new PlayerStatus(level, totalXp, xpForNext, gold, attack, defense);
    }
}

/// <summary>The caster's own inventory and equipped gear, sent to that client.</summary>
public readonly struct InventoryState
{
    public readonly byte EquippedWeaponId;
    public readonly byte EquippedArmorId;
    public readonly IReadOnlyList<ItemStack> Items;

    public InventoryState(byte equippedWeaponId, byte equippedArmorId, IReadOnlyList<ItemStack> items)
    {
        EquippedWeaponId = equippedWeaponId;
        EquippedArmorId = equippedArmorId;
        Items = items;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.InventoryState);
        w.WriteByte(EquippedWeaponId);
        w.WriteByte(EquippedArmorId);
        w.WriteUInt((uint)Items.Count);
        for (int i = 0; i < Items.Count; i++)
        {
            w.WriteByte(Items[i].ItemId);
            w.WriteInt(Items[i].Quantity);
        }
    }

    public static InventoryState Read(ref PacketReader r)
    {
        byte weapon = r.ReadByte();
        byte armor = r.ReadByte();
        uint count = r.ReadUInt();
        var items = new ItemStack[count];
        for (uint i = 0; i < count; i++)
        {
            byte itemId = r.ReadByte();
            int qty = r.ReadInt();
            items[i] = new ItemStack(itemId, qty);
        }

        return new InventoryState(weapon, armor, items);
    }
}

/// <summary>Someone invited this client into a party (display name shown to the user).</summary>
public readonly struct PartyInviteNotice
{
    public readonly string InviterName;

    public PartyInviteNotice(string inviterName) => InviterName = inviterName;

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.PartyInviteNotice);
        w.WriteString(InviterName);
    }

    public static PartyInviteNotice Read(ref PacketReader r) => new(r.ReadString());
}

/// <summary>
/// The client's current party roster (names, leader first). An empty roster means "not in a party".
/// Sent to every member whenever the roster changes.
/// </summary>
public readonly struct PartyState
{
    public readonly string LeaderName;
    public readonly IReadOnlyList<string> MemberNames;

    public PartyState(string leaderName, IReadOnlyList<string> memberNames)
    {
        LeaderName = leaderName;
        MemberNames = memberNames;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.PartyState);
        w.WriteString(LeaderName);
        w.WriteUInt((uint)MemberNames.Count);
        for (int i = 0; i < MemberNames.Count; i++)
        {
            w.WriteString(MemberNames[i]);
        }
    }

    public static PartyState Read(ref PacketReader r)
    {
        string leader = r.ReadString();
        uint count = r.ReadUInt();
        var names = new string[count];
        for (uint i = 0; i < count; i++)
        {
            names[i] = r.ReadString();
        }

        return new PartyState(leader, names);
    }
}

/// <summary>Outcome of an EnterInstance/LeaveInstance request (or a forced move), with a reason.</summary>
public readonly struct InstanceResult
{
    public readonly bool Ok;
    public readonly byte InstanceDefId; // 0 = the open world
    public readonly string Message;

    public InstanceResult(bool ok, byte instanceDefId, string message)
    {
        Ok = ok;
        InstanceDefId = instanceDefId;
        Message = message;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.InstanceResult);
        w.WriteBool(Ok);
        w.WriteByte(InstanceDefId);
        w.WriteString(Message);
    }

    public static InstanceResult Read(ref PacketReader r)
    {
        bool ok = r.ReadBool();
        byte defId = r.ReadByte();
        string message = r.ReadString();
        return new InstanceResult(ok, defId, message);
    }
}

/// <summary>The caster's account bank contents (gold + items), sent to that client after any change.</summary>
public readonly struct BankState
{
    public readonly int Gold;
    public readonly IReadOnlyList<ItemStack> Items;

    public BankState(int gold, IReadOnlyList<ItemStack> items)
    {
        Gold = gold;
        Items = items;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.BankState);
        w.WriteInt(Gold);
        w.WriteUInt((uint)Items.Count);
        for (int i = 0; i < Items.Count; i++)
        {
            w.WriteByte(Items[i].ItemId);
            w.WriteInt(Items[i].Quantity);
        }
    }

    public static BankState Read(ref PacketReader r)
    {
        int gold = r.ReadInt();
        uint count = r.ReadUInt();
        var items = new ItemStack[count];
        for (uint i = 0; i < count; i++)
        {
            byte itemId = r.ReadByte();
            int qty = r.ReadInt();
            items[i] = new ItemStack(itemId, qty);
        }

        return new BankState(gold, items);
    }
}
