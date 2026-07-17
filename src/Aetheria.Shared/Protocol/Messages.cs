using Aetheria.Shared.Combat;
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

/// <summary>
/// First packet a client sends. Carries the protocol version (mismatches are rejected) plus the
/// chosen race and class ids, which the server resolves against its <see cref="Data.GameData"/>.
/// </summary>
public readonly struct ConnectRequest
{
    public readonly byte ProtocolVersion;
    public readonly string Name;
    public readonly byte RaceId;
    public readonly byte ClassId;
    public readonly Gender Gender;

    public ConnectRequest(byte protocolVersion, string name, byte raceId, byte classId, Gender gender)
    {
        ProtocolVersion = protocolVersion;
        Name = name;
        RaceId = raceId;
        ClassId = classId;
        Gender = gender;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.ConnectRequest);
        w.WriteByte(ProtocolVersion);
        w.WriteString(Name);
        w.WriteByte(RaceId);
        w.WriteByte(ClassId);
        w.WriteByte((byte)Gender);
    }

    public static ConnectRequest Read(ref PacketReader r)
    {
        byte version = r.ReadByte();
        string name = r.ReadString();
        byte raceId = r.ReadByte();
        byte classId = r.ReadByte();
        var gender = (Gender)r.ReadByte();
        return new ConnectRequest(version, name, raceId, classId, gender);
    }
}

/// <summary>Cast the caster's racial ability on themselves (no target, no resource cost).</summary>
public readonly struct UseRacial
{
    public void Write(PacketWriter w) => w.WriteByte((byte)MessageType.UseRacial);

    public static UseRacial Read(ref PacketReader r) => default;
}

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

/// <summary>A movement intent. The server is authoritative: it decides what this input produces.</summary>
public readonly struct InputCommand
{
    public readonly uint Sequence;
    public readonly Vec2 MoveDirection;

    public InputCommand(uint sequence, Vec2 moveDirection)
    {
        Sequence = sequence;
        MoveDirection = moveDirection;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.InputCommand);
        w.WriteUInt(Sequence);
        w.WriteFloat(MoveDirection.X);
        w.WriteFloat(MoveDirection.Y);
    }

    public static InputCommand Read(ref PacketReader r)
    {
        uint sequence = r.ReadUInt();
        float x = r.ReadFloat();
        float y = r.ReadFloat();
        return new InputCommand(sequence, new Vec2(x, y));
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

    public EntitySnapshot(
        int id, EntityKind kind, Faction faction, Vec2 position,
        int health, int maxHealth, int resource, int maxResource)
    {
        Id = id;
        Kind = kind;
        Faction = faction;
        Position = position;
        Health = health;
        MaxHealth = maxHealth;
        Resource = resource;
        MaxResource = maxResource;
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
        return new EntitySnapshot(id, kind, faction, new Vec2(x, y), health, maxHealth, resource, maxResource);
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
