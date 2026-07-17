namespace Aetheria.Shared.Items;

/// <summary>Broad category of an item.</summary>
public enum ItemType : byte
{
    Material = 0,   // crafting/bankable junk, not equippable
    Weapon = 1,
    Armor = 2,
    Consumable = 3,
}

/// <summary>Equipment slot an item occupies, or None for non-equippable items.</summary>
public enum EquipSlot : byte
{
    None = 0,
    Weapon = 1,
    Armor = 2,
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
