using Aetheria.Shared.Items;

namespace Aetheria.Shared.Protocol;

// Client → server commands (and the corpse-contents response), grouped here.

/// <summary>Cast the caster's racial ability on themselves (no target, no resource cost).</summary>
public readonly struct UseRacial
{
    public void Write(PacketWriter w) => w.WriteByte((byte)MessageType.UseRacial);

    public static UseRacial Read(ref PacketReader r) => default;
}

/// <summary>Request to loot everything from a corpse. The server validates range and ownership rules.</summary>
public readonly struct LootCorpse
{
    public readonly int CorpseEntityId;

    public LootCorpse(int corpseEntityId) => CorpseEntityId = corpseEntityId;

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.LootCorpse);
        w.WriteInt(CorpseEntityId);
    }

    public static LootCorpse Read(ref PacketReader r) => new(r.ReadInt());
}

/// <summary>Ask to inspect a corpse's contents (opens the loot window). Range-validated server-side.</summary>
public readonly struct OpenCorpse
{
    public readonly int CorpseEntityId;

    public OpenCorpse(int corpseEntityId) => CorpseEntityId = corpseEntityId;

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.OpenCorpse);
        w.WriteInt(CorpseEntityId);
    }

    public static OpenCorpse Read(ref PacketReader r) => new(r.ReadInt());
}

/// <summary>Take one thing from a corpse: all of the given item id, or the gold when ItemId is 0.</summary>
public readonly struct LootItem
{
    public readonly int CorpseEntityId;
    public readonly byte ItemId; // 0 = take the gold

    public LootItem(int corpseEntityId, byte itemId)
    {
        CorpseEntityId = corpseEntityId;
        ItemId = itemId;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.LootItem);
        w.WriteInt(CorpseEntityId);
        w.WriteByte(ItemId);
    }

    public static LootItem Read(ref PacketReader r)
    {
        int corpseId = r.ReadInt();
        byte itemId = r.ReadByte();
        return new LootItem(corpseId, itemId);
    }
}

/// <summary>
/// A corpse's current contents, sent after OpenCorpse and after each loot action. Empty contents
/// (no gold, no items) means the corpse is spent — the client closes the loot window.
/// </summary>
public readonly struct CorpseContentsMessage
{
    public readonly int CorpseEntityId;
    public readonly int Gold;
    public readonly IReadOnlyList<ItemStack> Items;

    public CorpseContentsMessage(int corpseEntityId, int gold, IReadOnlyList<ItemStack> items)
    {
        CorpseEntityId = corpseEntityId;
        Gold = gold;
        Items = items;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.CorpseContents);
        w.WriteInt(CorpseEntityId);
        w.WriteInt(Gold);
        w.WriteUInt((uint)Items.Count);
        for (int i = 0; i < Items.Count; i++)
        {
            w.WriteByte(Items[i].ItemId);
            w.WriteInt(Items[i].Quantity);
        }
    }

    public static CorpseContentsMessage Read(ref PacketReader r)
    {
        int corpseId = r.ReadInt();
        int gold = r.ReadInt();
        uint count = r.ReadUInt();
        var items = new ItemStack[count];
        for (uint i = 0; i < count; i++)
        {
            byte itemId = r.ReadByte();
            int qty = r.ReadInt();
            items[i] = new ItemStack(itemId, qty);
        }

        return new CorpseContentsMessage(corpseId, gold, items);
    }
}

/// <summary>A request to move gold or an item between the player's inventory and their account bank.</summary>
public readonly struct BankTransaction
{
    public readonly BankOp Op;
    public readonly byte ItemId; // ignored for gold ops
    public readonly int Amount;  // gold amount, or item quantity

    public BankTransaction(BankOp op, byte itemId, int amount)
    {
        Op = op;
        ItemId = itemId;
        Amount = amount;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.BankTransaction);
        w.WriteByte((byte)Op);
        w.WriteByte(ItemId);
        w.WriteInt(Amount);
    }

    public static BankTransaction Read(ref PacketReader r)
    {
        var op = (BankOp)r.ReadByte();
        byte itemId = r.ReadByte();
        int amount = r.ReadInt();
        return new BankTransaction(op, itemId, amount);
    }
}

/// <summary>Invite another player (by entity id) into the sender's party. Same faction only.</summary>
public readonly struct PartyInvite
{
    public readonly int TargetEntityId;

    public PartyInvite(int targetEntityId) => TargetEntityId = targetEntityId;

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.PartyInvite);
        w.WriteInt(TargetEntityId);
    }

    public static PartyInvite Read(ref PacketReader r) => new(r.ReadInt());
}

/// <summary>Accept or decline the sender's pending party invite.</summary>
public readonly struct PartyRespond
{
    public readonly bool Accept;

    public PartyRespond(bool accept) => Accept = accept;

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.PartyRespond);
        w.WriteBool(Accept);
    }

    public static PartyRespond Read(ref PacketReader r) => new(r.ReadBool());
}

/// <summary>Leave the current party (no payload).</summary>
public readonly struct PartyLeave
{
    public void Write(PacketWriter w) => w.WriteByte((byte)MessageType.PartyLeave);

    public static PartyLeave Read(ref PacketReader r) => default;
}

/// <summary>Ask to enter an instanced zone (solo, or with the whole party if the sender leads one).</summary>
public readonly struct EnterInstance
{
    public readonly byte InstanceDefId;

    public EnterInstance(byte instanceDefId) => InstanceDefId = instanceDefId;

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.EnterInstance);
        w.WriteByte(InstanceDefId);
    }

    public static EnterInstance Read(ref PacketReader r) => new(r.ReadByte());
}

/// <summary>Leave the current instance and return to the open world (no payload).</summary>
public readonly struct LeaveInstance
{
    public void Write(PacketWriter w) => w.WriteByte((byte)MessageType.LeaveInstance);

    public static LeaveInstance Read(ref PacketReader r) => default;
}

/// <summary>
/// Equip a weapon/armor item from the bags into its slot (swapping the current piece back into
/// the bags), or unequip a slot when ItemId is 0. The server validates ownership and item type.
/// </summary>
public readonly struct EquipItem
{
    public readonly byte ItemId;      // 0 = unequip the given slot
    public readonly byte Slot;        // (byte)EquipSlot — used for unequip; inferred from the item otherwise

    public EquipItem(byte itemId, byte slot)
    {
        ItemId = itemId;
        Slot = slot;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.EquipItem);
        w.WriteByte(ItemId);
        w.WriteByte(Slot);
    }

    public static EquipItem Read(ref PacketReader r)
    {
        byte itemId = r.ReadByte();
        byte slot = r.ReadByte();
        return new EquipItem(itemId, slot);
    }
}

/// <summary>
/// WoW-style attack intent: "fight THIS target". The SERVER then auto-swings the class's basic
/// attack (or auto-recasts its basic incantation) whenever ready and in range, until the target
/// dies, becomes invalid, or the player sends 0 to stop. No more client-side button mashing.
/// </summary>
public readonly struct AttackTarget
{
    public readonly int TargetEntityId; // 0 = stop attacking

    public AttackTarget(int targetEntityId) => TargetEntityId = targetEntityId;

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.AttackTarget);
        w.WriteInt(TargetEntityId);
    }

    public static AttackTarget Read(ref PacketReader r) => new(r.ReadInt());
}

/// <summary>A player speaks in the world chat. The chat carries ONLY player words — no system logs.</summary>
public readonly struct ChatSend
{
    public readonly string Text;

    public ChatSend(string text) => Text = text;

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.ChatSend);
        w.WriteString(Text);
    }

    public static ChatSend Read(ref PacketReader r) => new(r.ReadString());
}

/// <summary>A chat line relayed to everyone in the same world: who said what.</summary>
public readonly struct ChatMessage
{
    public readonly string From;
    public readonly string Text;

    public ChatMessage(string from, string text)
    {
        From = from;
        Text = text;
    }

    public void Write(PacketWriter w)
    {
        w.WriteByte((byte)MessageType.ChatMessage);
        w.WriteString(From);
        w.WriteString(Text);
    }

    public static ChatMessage Read(ref PacketReader r)
    {
        string from = r.ReadString();
        string text = r.ReadString();
        return new ChatMessage(from, text);
    }
}
