using Aetheria.Shared.Items;

namespace Aetheria.Server.Items;

/// <summary>
/// A bag of item stacks plus a gold balance, with a fixed slot capacity. Used both by players (their
/// carried inventory) and by corpses (the full-loot pile left behind on death). Stacking rules are
/// passed in per call so this type needs no reference to the content registry.
/// </summary>
public sealed class Inventory
{
    private readonly List<ItemStack> _stacks = new();

    public Inventory(int capacity) => Capacity = capacity;

    /// <summary>Maximum number of distinct stacks (slots) this inventory can hold.</summary>
    public int Capacity { get; }

    public int Gold { get; private set; }

    public IReadOnlyList<ItemStack> Stacks => _stacks;

    public bool IsEmpty => _stacks.Count == 0 && Gold == 0;

    public void AddGold(int amount)
    {
        if (amount > 0)
        {
            Gold += amount;
        }
    }

    /// <summary>Remove and return all gold.</summary>
    public int TakeAllGold()
    {
        int g = Gold;
        Gold = 0;
        return g;
    }

    /// <summary>Remove up to <paramref name="amount"/> gold; returns the amount actually removed.</summary>
    public int RemoveGold(int amount)
    {
        int moved = System.Math.Clamp(amount, 0, Gold);
        Gold -= moved;
        return moved;
    }

    /// <summary>
    /// Add up to <paramref name="quantity"/> of an item, respecting stacking rules and capacity.
    /// Returns the amount that did NOT fit (0 means everything was added).
    /// </summary>
    public int TryAdd(byte itemId, int quantity, bool stackable, int maxStack)
    {
        if (quantity <= 0)
        {
            return 0;
        }

        int perStackCap = stackable ? System.Math.Max(1, maxStack) : 1;

        if (stackable)
        {
            for (int i = 0; i < _stacks.Count && quantity > 0; i++)
            {
                if (_stacks[i].ItemId != itemId || _stacks[i].Quantity >= perStackCap)
                {
                    continue;
                }

                int space = perStackCap - _stacks[i].Quantity;
                int take = System.Math.Min(space, quantity);
                _stacks[i] = _stacks[i].WithQuantity(_stacks[i].Quantity + take);
                quantity -= take;
            }
        }

        while (quantity > 0 && _stacks.Count < Capacity)
        {
            int take = System.Math.Min(perStackCap, quantity);
            _stacks.Add(new ItemStack(itemId, take));
            quantity -= take;
        }

        return quantity; // leftover that did not fit
    }

    /// <summary>Remove up to <paramref name="quantity"/> of an item; returns the amount actually removed.</summary>
    public int RemoveQuantity(byte itemId, int quantity)
    {
        int removed = 0;
        for (int i = _stacks.Count - 1; i >= 0 && quantity > 0; i--)
        {
            if (_stacks[i].ItemId != itemId)
            {
                continue;
            }

            int take = System.Math.Min(_stacks[i].Quantity, quantity);
            int remaining = _stacks[i].Quantity - take;
            if (remaining > 0)
            {
                _stacks[i] = _stacks[i].WithQuantity(remaining);
            }
            else
            {
                _stacks.RemoveAt(i);
            }

            quantity -= take;
            removed += take;
        }

        return removed;
    }

    public int CountOf(byte itemId)
    {
        int total = 0;
        foreach (ItemStack s in _stacks)
        {
            if (s.ItemId == itemId)
            {
                total += s.Quantity;
            }
        }

        return total;
    }

    public void Clear()
    {
        _stacks.Clear();
        Gold = 0;
    }
}
