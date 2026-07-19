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

    /// <summary>
    /// The bag CELLS in player order. May contain EMPTY placeholders (ItemId 0) — holes left by
    /// removals and drag-organisation, so every stack keeps the cell its owner chose.
    /// </summary>
    public IReadOnlyList<ItemStack> Stacks => _stacks;

    /// <summary>True when a cell holds nothing (placeholder kept for layout).</summary>
    public static bool IsEmptyCell(ItemStack s) => s.ItemId == 0 || s.Quantity <= 0;

    public bool IsEmpty
    {
        get
        {
            if (Gold != 0)
            {
                return false;
            }

            foreach (ItemStack s in _stacks)
            {
                if (!IsEmptyCell(s))
                {
                    return false;
                }
            }

            return true;
        }
    }

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

        // Fill the HOLES first (cells freed by removals/organisation), then append.
        for (int i = 0; i < _stacks.Count && quantity > 0; i++)
        {
            if (!IsEmptyCell(_stacks[i]))
            {
                continue;
            }

            int take = System.Math.Min(perStackCap, quantity);
            _stacks[i] = new ItemStack(itemId, take);
            quantity -= take;
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

            // A emptied cell becomes a HOLE (placeholder), so the other cells keep their place.
            _stacks[i] = remaining > 0 ? _stacks[i].WithQuantity(remaining) : new ItemStack(0, 0);

            quantity -= take;
            removed += take;
        }

        TrimTail();
        return removed;
    }

    /// <summary>Drop trailing empty cells — holes only matter BETWEEN items.</summary>
    private void TrimTail()
    {
        while (_stacks.Count > 0 && IsEmptyCell(_stacks[_stacks.Count - 1]))
        {
            _stacks.RemoveAt(_stacks.Count - 1);
        }
    }

    /// <summary>Index of the first stack holding this item, or -1.</summary>
    public int IndexOfItem(byte itemId)
    {
        for (int i = 0; i < _stacks.Count; i++)
        {
            if (_stacks[i].ItemId == itemId)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Player-driven bag ordering: drag a stack onto ANY cell (even a far empty one — holes are
    /// padded in so the stack really lands where the player dropped it). Occupied target = swap.
    /// </summary>
    public bool MoveSlot(int from, int to)
    {
        if (from < 0 || from >= _stacks.Count || IsEmptyCell(_stacks[from]) ||
            to < 0 || to >= Capacity || from == to)
        {
            return false;
        }

        while (_stacks.Count <= to)
        {
            _stacks.Add(new ItemStack(0, 0)); // pad holes up to the chosen cell
        }

        (_stacks[from], _stacks[to]) = (_stacks[to], _stacks[from]);
        TrimTail();
        return true;
    }

    /// <summary>Put one item into a precise cell (equip-swap drops the old piece where the
    /// new one was taken): fills that hole, else inserts, else takes any hole, else false.</summary>
    public bool TryInsertAt(int index, byte itemId, int quantity)
    {
        if (quantity <= 0)
        {
            return false;
        }

        if (index >= 0 && index < _stacks.Count && IsEmptyCell(_stacks[index]))
        {
            _stacks[index] = new ItemStack(itemId, quantity);
            return true;
        }

        if (_stacks.Count < Capacity)
        {
            _stacks.Insert(System.Math.Clamp(index, 0, _stacks.Count), new ItemStack(itemId, quantity));
            return true;
        }

        for (int i = 0; i < _stacks.Count; i++)
        {
            if (IsEmptyCell(_stacks[i]))
            {
                _stacks[i] = new ItemStack(itemId, quantity);
                return true;
            }
        }

        return false;
    }

    /// <summary>Restore the EXACT saved cell layout (holes included). Trusted data only.</summary>
    public void RestoreLayout(IReadOnlyList<ItemStack> cells)
    {
        _stacks.Clear();
        for (int i = 0; i < cells.Count && i < Capacity; i++)
        {
            _stacks.Add(cells[i]);
        }

        TrimTail();
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
