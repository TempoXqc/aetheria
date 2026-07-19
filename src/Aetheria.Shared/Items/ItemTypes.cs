namespace Aetheria.Shared.Items;

/// <summary>Broad category of an item.</summary>
public enum ItemType : byte
{
    Material = 0,   // crafting/bankable junk, not equippable
    Weapon = 1,
    Armor = 2,
    Consumable = 3,
    Bag = 4,        // worn in the Bag slot: raises the carried-inventory capacity
}

/// <summary>
/// Equipment slot an item occupies (WoW-style loadout), or None for non-equippable items.
/// Values are wire-stable: they index the equipment array in snapshots and persistence.
/// </summary>
public enum EquipSlot : byte
{
    None = 0,
    Weapon = 1,
    Chest = 2,
    Armor = Chest, // legacy alias (early builds had a single "armor" slot)
    Head = 3,
    Shoulders = 4,
    Legs = 5,
    Feet = 6,
    Hands = 7,
    Waist = 8,
    Back = 9,
    OffHand = 10,
    Bag = 11, // the carried bag (raises inventory capacity; not part of the visual loadout)
}

/// <summary>Helpers for iterating the real equipment slots.</summary>
public static class EquipSlots
{
    /// <summary>Array length for per-slot storage (index = (int)EquipSlot).</summary>
    public const int Count = 12;

    /// <summary>Every wearable slot, in display order (the character sheet's layout).</summary>
    public static readonly EquipSlot[] All =
    [
        EquipSlot.Head, EquipSlot.Shoulders, EquipSlot.Back, EquipSlot.Chest, EquipSlot.Hands,
        EquipSlot.Waist, EquipSlot.Legs, EquipSlot.Feet, EquipSlot.Weapon, EquipSlot.OffHand,
    ];
}

/// <summary>A bank transaction direction: move gold or an item between the player and their account bank.</summary>
public enum BankOp : byte
{
    DepositGold = 0,
    WithdrawGold = 1,
    DepositItem = 2,
    WithdrawItem = 3,
}

/// <summary>A quantity of one item id — the unit of storage in an inventory or on a corpse.</summary>
public readonly struct ItemStack
{
    public readonly byte ItemId;
    public readonly int Quantity;

    public ItemStack(byte itemId, int quantity)
    {
        ItemId = itemId;
        Quantity = quantity;
    }

    public ItemStack WithQuantity(int quantity) => new(ItemId, quantity);
}
