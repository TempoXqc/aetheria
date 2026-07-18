using Aetheria.Shared.Items;

namespace Aetheria.Shared.Protocol;

// ----------------------------------------------------------------------------------------------
// Inspect
// ----------------------------------------------------------------------------------------------

/// <summary>Ask to inspect a same-faction player's character sheet.</summary>
public readonly struct Inspect
{
    public readonly int TargetEntityId;

    public Inspect(int targetEntityId) => TargetEntityId = targetEntityId;

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.Inspect);
        w.WriteInt(TargetEntityId);
    }

    public static Inspect Read(ref PacketReader r) => new(r.ReadInt());
}

/// <summary>Another player's public character sheet.</summary>
public readonly struct InspectResult
{
    public readonly string Name;
    public readonly byte Level;
    public readonly byte RaceId;
    public readonly byte ClassId;
    public readonly int MaxHealth;
    public readonly int Attack;
    public readonly int Defense;
    public readonly byte WeaponId;
    public readonly byte ArmorId;
    public readonly int TotalXp;

    public InspectResult(string name, byte level, byte raceId, byte classId,
        int maxHealth, int attack, int defense, byte weaponId, byte armorId, int totalXp)
    {
        Name = name;
        Level = level;
        RaceId = raceId;
        ClassId = classId;
        MaxHealth = maxHealth;
        Attack = attack;
        Defense = defense;
        WeaponId = weaponId;
        ArmorId = armorId;
        TotalXp = totalXp;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.InspectResult);
        w.WriteString(Name);
        w.WriteByte(Level);
        w.WriteByte(RaceId);
        w.WriteByte(ClassId);
        w.WriteInt(MaxHealth);
        w.WriteInt(Attack);
        w.WriteInt(Defense);
        w.WriteByte(WeaponId);
        w.WriteByte(ArmorId);
        w.WriteInt(TotalXp);
    }

    public static InspectResult Read(ref PacketReader r)
    {
        string name = r.ReadString();
        byte level = r.ReadByte();
        byte raceId = r.ReadByte();
        byte classId = r.ReadByte();
        int maxHealth = r.ReadInt();
        int attack = r.ReadInt();
        int defense = r.ReadInt();
        byte weaponId = r.ReadByte();
        byte armorId = r.ReadByte();
        int totalXp = r.ReadInt();
        return new InspectResult(name, level, raceId, classId, maxHealth, attack, defense, weaponId, armorId, totalXp);
    }
}

// ----------------------------------------------------------------------------------------------
// Duels
// ----------------------------------------------------------------------------------------------

/// <summary>Challenge a player to a duel — friendly (loser survives at 1 hp) or to the DEATH.</summary>
public readonly struct DuelRequest
{
    public readonly int TargetEntityId;
    public readonly bool ToDeath;

    public DuelRequest(int targetEntityId, bool toDeath)
    {
        TargetEntityId = targetEntityId;
        ToDeath = toDeath;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.DuelRequest);
        w.WriteInt(TargetEntityId);
        w.WriteBool(ToDeath);
    }

    public static DuelRequest Read(ref PacketReader r)
    {
        int target = r.ReadInt();
        bool toDeath = r.ReadBool();
        return new DuelRequest(target, toDeath);
    }
}

/// <summary>Accept or decline the pending duel challenge.</summary>
public readonly struct DuelRespond
{
    public readonly bool Accept;

    public DuelRespond(bool accept) => Accept = accept;

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.DuelRespond);
        w.WriteBool(Accept);
    }

    public static DuelRespond Read(ref PacketReader r) => new(r.ReadBool());
}

/// <summary>Someone challenged this client to a duel.</summary>
public readonly struct DuelNotice
{
    public readonly string ChallengerName;
    public readonly bool ToDeath;

    public DuelNotice(string challengerName, bool toDeath)
    {
        ChallengerName = challengerName;
        ToDeath = toDeath;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.DuelNotice);
        w.WriteString(ChallengerName);
        w.WriteBool(ToDeath);
    }

    public static DuelNotice Read(ref PacketReader r)
    {
        string name = r.ReadString();
        bool toDeath = r.ReadBool();
        return new DuelNotice(name, toDeath);
    }
}

/// <summary>Duel lifecycle: Active=true when it starts; Active=false with a result message when it ends.</summary>
public readonly struct DuelState
{
    public readonly bool Active;
    public readonly int OpponentEntityId;
    public readonly bool ToDeath;
    public readonly string Message;

    public DuelState(bool active, int opponentEntityId, bool toDeath, string message)
    {
        Active = active;
        OpponentEntityId = opponentEntityId;
        ToDeath = toDeath;
        Message = message;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.DuelState);
        w.WriteBool(Active);
        w.WriteInt(OpponentEntityId);
        w.WriteBool(ToDeath);
        w.WriteString(Message);
    }

    public static DuelState Read(ref PacketReader r)
    {
        bool active = r.ReadBool();
        int opponent = r.ReadInt();
        bool toDeath = r.ReadBool();
        string message = r.ReadString();
        return new DuelState(active, opponent, toDeath, message);
    }
}

// ----------------------------------------------------------------------------------------------
// Trade
// ----------------------------------------------------------------------------------------------

/// <summary>Propose a trade to a nearby same-faction player.</summary>
public readonly struct TradeRequest
{
    public readonly int TargetEntityId;

    public TradeRequest(int targetEntityId) => TargetEntityId = targetEntityId;

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.TradeRequest);
        w.WriteInt(TargetEntityId);
    }

    public static TradeRequest Read(ref PacketReader r) => new(r.ReadInt());
}

/// <summary>Accept or decline the pending trade proposal.</summary>
public readonly struct TradeRespond
{
    public readonly bool Accept;

    public TradeRespond(bool accept) => Accept = accept;

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.TradeRespond);
        w.WriteBool(Accept);
    }

    public static TradeRespond Read(ref PacketReader r) => new(r.ReadBool());
}

/// <summary>Someone proposed a trade to this client.</summary>
public readonly struct TradeNotice
{
    public readonly string FromName;

    public TradeNotice(string fromName) => FromName = fromName;

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.TradeNotice);
        w.WriteString(FromName);
    }

    public static TradeNotice Read(ref PacketReader r) => new(r.ReadString());
}

/// <summary>Replace my whole offer (gold + item list). Any change un-accepts both sides.</summary>
public readonly struct TradeSetOffer
{
    public readonly int Gold;
    public readonly IReadOnlyList<ItemStack> Items;

    public TradeSetOffer(int gold, IReadOnlyList<ItemStack> items)
    {
        Gold = gold;
        Items = items;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.TradeSetOffer);
        w.WriteInt(Gold);
        w.WriteUInt((uint)Items.Count);
        for (int i = 0; i < Items.Count; i++)
        {
            w.WriteByte(Items[i].ItemId);
            w.WriteInt(Items[i].Quantity);
        }
    }

    public static TradeSetOffer Read(ref PacketReader r)
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

        return new TradeSetOffer(gold, items);
    }
}

/// <summary>Lock in my side of the trade (no payload). When both sides accept, the swap executes.</summary>
public readonly struct TradeAccept
{
    public void Write(PacketWriter w) => w.WriteByte((byte)MessageType.TradeAccept);

    public static TradeAccept Read(ref PacketReader r) => default;
}

/// <summary>Walk away from the trade (no payload).</summary>
public readonly struct TradeCancel
{
    public void Write(PacketWriter w) => w.WriteByte((byte)MessageType.TradeCancel);

    public static TradeCancel Read(ref PacketReader r) => default;
}

/// <summary>
/// Full trade window state for this client: both offers, both accept flags. Active=false closes the
/// window (Message says why: completed, cancelled, out of range...).
/// </summary>
public readonly struct TradeState
{
    public readonly bool Active;
    public readonly string PartnerName;
    public readonly int MyGold;
    public readonly IReadOnlyList<ItemStack> MyItems;
    public readonly int TheirGold;
    public readonly IReadOnlyList<ItemStack> TheirItems;
    public readonly bool MyAccepted;
    public readonly bool TheirAccepted;
    public readonly string Message;

    public TradeState(bool active, string partnerName, int myGold, IReadOnlyList<ItemStack> myItems,
        int theirGold, IReadOnlyList<ItemStack> theirItems, bool myAccepted, bool theirAccepted, string message)
    {
        Active = active;
        PartnerName = partnerName;
        MyGold = myGold;
        MyItems = myItems;
        TheirGold = theirGold;
        TheirItems = theirItems;
        MyAccepted = myAccepted;
        TheirAccepted = theirAccepted;
        Message = message;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.TradeState);
        w.WriteBool(Active);
        w.WriteString(PartnerName);
        w.WriteInt(MyGold);
        WriteItems(w, MyItems);
        w.WriteInt(TheirGold);
        WriteItems(w, TheirItems);
        w.WriteBool(MyAccepted);
        w.WriteBool(TheirAccepted);
        w.WriteString(Message);
    }

    private static void WriteItems(PacketWriter w, IReadOnlyList<ItemStack> items)
    {
        w.WriteUInt((uint)items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            w.WriteByte(items[i].ItemId);
            w.WriteInt(items[i].Quantity);
        }
    }

    public static TradeState Read(ref PacketReader r)
    {
        bool active = r.ReadBool();
        string partner = r.ReadString();
        int myGold = r.ReadInt();
        ItemStack[] myItems = ReadItems(ref r);
        int theirGold = r.ReadInt();
        ItemStack[] theirItems = ReadItems(ref r);
        bool myAccepted = r.ReadBool();
        bool theirAccepted = r.ReadBool();
        string message = r.ReadString();
        return new TradeState(active, partner, myGold, myItems, theirGold, theirItems, myAccepted, theirAccepted, message);
    }

    private static ItemStack[] ReadItems(ref PacketReader r)
    {
        uint count = r.ReadUInt();
        var items = new ItemStack[count];
        for (uint i = 0; i < count; i++)
        {
            byte itemId = r.ReadByte();
            int qty = r.ReadInt();
            items[i] = new ItemStack(itemId, qty);
        }

        return items;
    }
}

// ----------------------------------------------------------------------------------------------
// Drop to ground
// ----------------------------------------------------------------------------------------------

/// <summary>Drop a quantity of an item on the ground (it becomes a lootable sack at your feet).</summary>
public readonly struct DropItem
{
    public readonly byte ItemId;
    public readonly int Quantity;

    public DropItem(byte itemId, int quantity)
    {
        ItemId = itemId;
        Quantity = quantity;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.DropItem);
        w.WriteByte(ItemId);
        w.WriteInt(Quantity);
    }

    public static DropItem Read(ref PacketReader r)
    {
        byte itemId = r.ReadByte();
        int qty = r.ReadInt();
        return new DropItem(itemId, qty);
    }
}
