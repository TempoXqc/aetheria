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

    /// <summary>The player pressed jump this frame (cosmetic hop, relayed to everyone).</summary>
    public readonly bool Jump;

    public InputCommand(uint sequence, Vec2 moveDirection, float facingRadians = 0f, bool jump = false)
    {
        Sequence = sequence;
        MoveDirection = moveDirection;
        FacingRadians = facingRadians;
        Jump = jump;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.InputCommand);
        w.WriteUInt(Sequence);
        w.WriteFloat(MoveDirection.X);
        w.WriteFloat(MoveDirection.Y);
        w.WriteFloat(FacingRadians);
        w.WriteBool(Jump);
    }

    public static InputCommand Read(ref PacketReader r)
    {
        uint sequence = r.ReadUInt();
        float x = r.ReadFloat();
        float y = r.ReadFloat();
        float facing = r.ReadFloat();
        bool jump = r.ReadBool();
        return new InputCommand(sequence, new Vec2(x, y), facing, jump);
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

    /// <summary>Player: cosmetic customisation (skin, face, hair, beard). Zeroed for non-players.</summary>
    public readonly Appearance Appearance;

    /// <summary>Animation flag bits — bit 0: currently jumping.</summary>
    public readonly byte Flags;

    public bool IsJumping => (Flags & 1) != 0;

    /// <summary>Monsters: currently chasing/fighting someone (red name + health bar).</summary>
    public bool IsAggro => (Flags & 2) != 0;

    /// <summary>Ability currently being INCANTED (0 = not casting). Drives cast bars.</summary>
    public readonly byte CastAbilityId;

    /// <summary>Cast progress, 0..255 over the incantation.</summary>
    public readonly byte CastProgress;

    /// <summary>Item id per equipment slot (index = (int)EquipSlot) — the character's whole look.</summary>
    public readonly byte[] Equipment;

    /// <summary>Druid shapeshift form: 0 humanoid, 1 bear, 2 owl, 3 cat — decides the model.</summary>
    public readonly byte FormId;

    public byte EquippedWeaponId => Equipment != null && Equipment.Length > 1 ? Equipment[(int)EquipSlot.Weapon] : (byte)0;
    public byte EquippedArmorId => Equipment != null && Equipment.Length > 2 ? Equipment[(int)EquipSlot.Chest] : (byte)0;

    /// <summary>Safe per-slot read (empty when the array is absent/short).</summary>
    public byte EquippedIn(EquipSlot slot)
    {
        int i = (int)slot;
        return Equipment != null && i < Equipment.Length ? Equipment[i] : (byte)0;
    }

    public EntitySnapshot(
        int id, EntityKind kind, Faction faction, Vec2 position,
        int health, int maxHealth, int resource, int maxResource, float facingRadians = 0f,
        byte level = 1, string name = "", byte raceId = 0, byte classId = 0, Gender gender = Gender.Male,
        Appearance appearance = default, byte flags = 0, byte castAbilityId = 0, byte castProgress = 0,
        byte[]? equipment = null, byte formId = 0)
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
        Appearance = appearance;
        Flags = flags;
        CastAbilityId = castAbilityId;
        CastProgress = castProgress;
        Equipment = equipment ?? new byte[EquipSlots.Count];
        FormId = formId;
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
        Appearance.Write(w);
        w.WriteByte(Flags);
        w.WriteByte(CastAbilityId);
        w.WriteByte(CastProgress);
        for (int i = 0; i < EquipSlots.Count; i++)
        {
            w.WriteByte(Equipment != null && i < Equipment.Length ? Equipment[i] : (byte)0);
        }

        w.WriteByte(FormId);
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
        Appearance appearance = Appearance.Read(ref r);
        byte flags = r.ReadByte();
        byte castAbilityId = r.ReadByte();
        byte castProgress = r.ReadByte();
        var equipment = new byte[EquipSlots.Count];
        for (int i = 0; i < EquipSlots.Count; i++)
        {
            equipment[i] = r.ReadByte();
        }

        byte formId = r.ReadByte();
        return new EntitySnapshot(
            id, kind, faction, new Vec2(x, y), health, maxHealth, resource, maxResource, facing, level, name,
            raceId, classId, gender, appearance, flags, castAbilityId, castProgress, equipment, formId);
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
    /// <summary>Item id per equipment slot (index = (int)EquipSlot; length = EquipSlots.Count).</summary>
    public readonly byte[] Equipment;

    public readonly IReadOnlyList<ItemStack> Items;

    /// <summary>The carried inventory's CURRENT cell count (base + the worn bag's bonus).</summary>
    public readonly int Capacity;

    public byte EquippedWeaponId => Equipment[(int)EquipSlot.Weapon];
    public byte EquippedArmorId => Equipment[(int)EquipSlot.Chest];

    public InventoryState(byte[] equipment, IReadOnlyList<ItemStack> items,
        int capacity = SimulationConstants.PlayerInventoryCapacity)
    {
        Equipment = equipment ?? new byte[EquipSlots.Count];
        Items = items;
        Capacity = capacity;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.InventoryState);
        w.WriteInt(Capacity);
        for (int i = 0; i < EquipSlots.Count; i++)
        {
            w.WriteByte(i < Equipment.Length ? Equipment[i] : (byte)0);
        }

        w.WriteUInt((uint)Items.Count);
        for (int i = 0; i < Items.Count; i++)
        {
            w.WriteByte(Items[i].ItemId);
            w.WriteInt(Items[i].Quantity);
        }
    }

    public static InventoryState Read(ref PacketReader r)
    {
        int capacity = r.ReadInt();
        var equipment = new byte[EquipSlots.Count];
        for (int i = 0; i < EquipSlots.Count; i++)
        {
            equipment[i] = r.ReadByte();
        }

        uint count = r.ReadUInt();
        var items = new ItemStack[count];
        for (uint i = 0; i < count; i++)
        {
            byte itemId = r.ReadByte();
            int qty = r.ReadInt();
            items[i] = new ItemStack(itemId, qty);
        }


        return new InventoryState(equipment, items, capacity);
    }
}

/// <summary>Accept a quest from (or turn one in to) the quest giver — server-validated.</summary>
public readonly struct QuestAction
{
    public readonly byte QuestId;
    public readonly bool TurnIn;

    public QuestAction(byte questId, bool turnIn)
    {
        QuestId = questId;
        TurnIn = turnIn;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.QuestAction);
        w.WriteByte(QuestId);
        w.WriteByte(TurnIn ? (byte)1 : (byte)0);
    }

    public static QuestAction Read(ref PacketReader r) => new(r.ReadByte(), r.ReadByte() != 0);
}

/// <summary>Buy from / sell to a merchant NPC (validated server-side: stock, gold, proximity).</summary>
public readonly struct VendorAction
{
    public readonly bool Sell; // false = buy from the merchant, true = sell to them
    public readonly byte ItemId;
    public readonly byte Quantity;

    public VendorAction(bool sell, byte itemId, byte quantity)
    {
        Sell = sell;
        ItemId = itemId;
        Quantity = quantity;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.VendorAction);
        w.WriteByte(Sell ? (byte)1 : (byte)0);
        w.WriteByte(ItemId);
        w.WriteByte(Quantity);
    }

    public static VendorAction Read(ref PacketReader r)
        => new(r.ReadByte() != 0, r.ReadByte(), r.ReadByte());
}

/// <summary>Drag-reorder in the bags: move the stack at FromIndex onto ToIndex (swap/append).</summary>
public readonly struct MoveItem
{
    public readonly byte FromIndex;
    public readonly byte ToIndex;

    public MoveItem(byte fromIndex, byte toIndex)
    {
        FromIndex = fromIndex;
        ToIndex = toIndex;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.MoveItem);
        w.WriteByte(FromIndex);
        w.WriteByte(ToIndex);
    }

    public static MoveItem Read(ref PacketReader r) => new(r.ReadByte(), r.ReadByte());
}

/// <summary>This client's quest progress: active quest, kill count, and chain position.</summary>
public readonly struct QuestStateMessage
{
    public readonly byte ActiveQuestId;
    public readonly int Kills;
    public readonly byte CompletedUpTo;

    public QuestStateMessage(byte activeQuestId, int kills, byte completedUpTo)
    {
        ActiveQuestId = activeQuestId;
        Kills = kills;
        CompletedUpTo = completedUpTo;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.QuestState);
        w.WriteByte(ActiveQuestId);
        w.WriteInt(Kills);
        w.WriteByte(CompletedUpTo);
    }

    public static QuestStateMessage Read(ref PacketReader r)
        => new(r.ReadByte(), r.ReadInt(), r.ReadByte());
}

/// <summary>
/// The server's full quest catalogue, sent once at login. The CLIENT renders quests from this —
/// so quests written in the Studio (data/quests.json) go live with a server restart alone:
/// no client rebuild, every player sees the new texts on their next connection.
/// </summary>
public readonly struct QuestCatalogMessage
{
    public readonly Aetheria.Shared.Data.QuestDefinition[] Quests;

    public QuestCatalogMessage(Aetheria.Shared.Data.QuestDefinition[] quests) => Quests = quests;

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.QuestCatalog);
        w.WriteByte((byte)Quests.Length);
        foreach (Aetheria.Shared.Data.QuestDefinition q in Quests)
        {
            w.WriteByte(q.Id);
            w.WriteByte(q.TargetMonsterId);
            w.WriteByte(q.RewardItemId);
            w.WriteByte(q.NextQuestId);
            w.WriteInt(q.RequiredKills);
            w.WriteInt(q.RewardXp);
            w.WriteInt(q.RewardGold);
            w.WriteString(q.Name);
            w.WriteString(q.Description);
            w.WriteString(q.TurnInText);
            w.WriteFloat(q.ZoneX);
            w.WriteFloat(q.ZoneY);
            w.WriteFloat(q.ZoneRadius);
        }
    }

    public static QuestCatalogMessage Read(ref PacketReader r)
    {
        int count = r.ReadByte();
        var quests = new Aetheria.Shared.Data.QuestDefinition[count];
        for (int i = 0; i < count; i++)
        {
            byte id = r.ReadByte();
            byte target = r.ReadByte();
            byte rewardItem = r.ReadByte();
            byte next = r.ReadByte();
            int kills = r.ReadInt();
            int xp = r.ReadInt();
            int gold = r.ReadInt();
            string name = r.ReadString();
            string description = r.ReadString();
            string turnIn = r.ReadString();
            float zoneX = r.ReadFloat();
            float zoneY = r.ReadFloat();
            float zoneRadius = r.ReadFloat();
            quests[i] = new Aetheria.Shared.Data.QuestDefinition
            {
                Id = id, TargetMonsterId = target, RewardItemId = rewardItem, NextQuestId = next,
                RequiredKills = kills, RewardXp = xp, RewardGold = gold,
                Name = name, Description = description, TurnInText = turnIn,
                ZoneX = zoneX, ZoneY = zoneY, ZoneRadius = zoneRadius,
            };
        }

        return new QuestCatalogMessage(quests);
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

/// <summary>Druid shapeshift request: 0 back to humanoid, 1 bear, 2 owl, 3 cat.</summary>
public readonly struct ShapeShift
{
    public readonly byte FormId;

    public ShapeShift(byte formId) => FormId = formId;

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.ShapeShift);
        w.WriteByte(FormId);
    }

    public static ShapeShift Read(ref PacketReader r) => new(r.ReadByte());
}

/// <summary>Leader-only: throw a member out of the party (by entity id).</summary>
public readonly struct PartyKick
{
    public readonly int TargetEntityId;

    public PartyKick(int targetEntityId) => TargetEntityId = targetEntityId;

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.PartyKick);
        w.WriteInt(TargetEntityId);
    }

    public static PartyKick Read(ref PacketReader r) => new(r.ReadInt());
}

/// <summary>One party member as the party frames need it: identity, LIVE vitals, active buffs.</summary>
public sealed class PartyMemberInfo
{
    public string Name = string.Empty;
    public int EntityId;
    public byte ClassId;
    public byte Level;
    public int Health;
    public int MaxHealth;
    public int Resource;
    public int MaxResource;

    /// <summary>World position, so the maps can show every ally — even across the map.</summary>
    public float X;
    public float Y;

    /// <summary>False = this member is DISCONNECTED: greyed frame, no vitals, no map dot.</summary>
    public bool Online = true;

    /// <summary>Active timed effects: (effect type, seconds remaining).</summary>
    public (byte Type, float Seconds)[] Effects = System.Array.Empty<(byte, float)>();
}

/// <summary>
/// The client's current party: roster + LIVE vitals and buffs (leader first). An empty roster
/// means "not in a party". Sent on every roster change AND a few times per second while grouped,
/// so the party frames track health/mana in real time even across the map.
/// </summary>
public readonly struct PartyState
{
    public readonly string LeaderName;
    public readonly IReadOnlyList<PartyMemberInfo> Members;

    public PartyState(string leaderName, IReadOnlyList<PartyMemberInfo> members)
    {
        LeaderName = leaderName;
        Members = members;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.PartyState);
        w.WriteString(LeaderName);
        w.WriteByte((byte)Members.Count);
        for (int i = 0; i < Members.Count; i++)
        {
            PartyMemberInfo m = Members[i];
            w.WriteString(m.Name);
            w.WriteInt(m.EntityId);
            w.WriteByte(m.ClassId);
            w.WriteByte(m.Level);
            w.WriteInt(m.Health);
            w.WriteInt(m.MaxHealth);
            w.WriteInt(m.Resource);
            w.WriteInt(m.MaxResource);
            w.WriteFloat(m.X);
            w.WriteFloat(m.Y);
            w.WriteBool(m.Online);
            w.WriteByte((byte)m.Effects.Length);
            for (int e = 0; e < m.Effects.Length; e++)
            {
                w.WriteByte(m.Effects[e].Type);
                w.WriteFloat(m.Effects[e].Seconds);
            }
        }
    }

    public static PartyState Read(ref PacketReader r)
    {
        string leader = r.ReadString();
        int count = r.ReadByte();
        var members = new PartyMemberInfo[count];
        for (int i = 0; i < count; i++)
        {
            var m = new PartyMemberInfo
            {
                Name = r.ReadString(),
                EntityId = r.ReadInt(),
                ClassId = r.ReadByte(),
                Level = r.ReadByte(),
                Health = r.ReadInt(),
                MaxHealth = r.ReadInt(),
                Resource = r.ReadInt(),
                MaxResource = r.ReadInt(),
                X = r.ReadFloat(),
                Y = r.ReadFloat(),
                Online = r.ReadBool(),
            };

            int effects = r.ReadByte();
            m.Effects = new (byte, float)[effects];
            for (int e = 0; e < effects; e++)
            {
                byte type = r.ReadByte();
                float seconds = r.ReadFloat();
                m.Effects[e] = (type, seconds);
            }

            members[i] = m;
        }

        return new PartyState(leader, members);
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
