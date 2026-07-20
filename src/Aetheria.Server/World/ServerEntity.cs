using Aetheria.Server.Items;
using Aetheria.Shared;
using Aetheria.Shared.Combat;
using Aetheria.Shared.Items;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Server.World;

/// <summary>
/// The server's authoritative record of one entity — a player or a monster. Carries combat stats,
/// current health and resource, ability cooldowns, temporary effects (buffs), and (for monsters) a
/// little AI state. Plain mutable class for the skeleton; the natural evolution is a data-oriented
/// (ECS) layout as counts grow.
/// </summary>
public sealed class ServerEntity
{
    // --- Rage tuning (Warriors): built by fighting, decays out of combat. ---
    private const float RageOnDealDamage = 10f;
    private const float RageOnTakeDamage = 5f;
    private const float RageDecayPerSecond = 2f;
    private const float OutOfCombatSeconds = 3f;

    private readonly Dictionary<byte, uint> _abilityReadyTick = new();
    private readonly List<ActiveEffect> _effects = new();
    private float _regenHealthCarry;

    public ServerEntity(int id, EntityKind kind, Vec2 position, StatBlock stats, byte basicAbilityId)
    {
        Id = id;
        Kind = kind;
        Position = position;
        SpawnPosition = position;
        Stats = stats;
        Health = stats.MaxHealth;
        BasicAbilityId = basicAbilityId;
        Inventory = new Inventory(SimulationConstants.PlayerInventoryCapacity);
    }

    public int Id { get; }
    public EntityKind Kind { get; }
    public string Name { get; set; } = string.Empty;

    public Vec2 Position { get; set; }
    public Vec2 SpawnPosition { get; set; }
    public Vec2 MoveIntent { get; set; } = Vec2.Zero;

    /// <summary>Direction the entity faces, radians on the world plane (mouse-driven for players).</summary>
    public float FacingRadians { get; set; }

    public PeerId? Owner { get; set; }
    public uint LastInputSequence { get; set; }

    public StatBlock Stats { get; }
    public int Health { get; private set; }
    public byte BasicAbilityId { get; set; } // druid forms swap it

    // --- Identity ---
    public Faction Faction { get; set; } = Faction.Neutral;
    public Gender Gender { get; set; } = Gender.Male;
    public byte RaceId { get; set; }
    public byte ClassId { get; set; }
    public byte MonsterId { get; set; }
    public byte RacialAbilityId { get; set; }

    /// <summary>Cosmetic customisation chosen at creation (players only); relayed in snapshots.</summary>
    public Appearance Appearance { get; set; }

    // --- Resource (rage/mana/energy) ---
    public ResourceType ResourceType { get; set; } = ResourceType.Mana;
    public int MaxResource { get; set; }
    public float CurrentResource { get; private set; }
    public float ResourceRegenPerTick { get; set; }

    /// <summary>Extra max mana from progression. Only Mana grows; Rage and Energy keep a fixed pool.</summary>
    public int ProgressionResourceBonus { get; set; }

    /// <summary>Effective max resource: mana grows with progression; rage/energy stay fixed at their base.</summary>
    public int EffectiveMaxResource
        => MaxResource + (ResourceType == ResourceType.Mana ? ProgressionResourceBonus : 0);

    public bool IsMonster => Kind == EntityKind.Monster;
    public bool IsDead { get; private set; }
    public bool IsAlive => !IsDead;
    public uint RespawnAtTick { get; private set; }

    /// <summary>Delay before this entity respawns after dying (instances slow monsters down).</summary>
    public int RespawnDelayTicks { get; set; } = SimulationConstants.RespawnDelayTicks;
    public uint LastCombatTick { get; private set; }

    public int? AiTargetId { get; set; }

    /// <summary>Monster is running back to its spawn after a leash break (drops aggro on the way).</summary>
    public bool IsEvading { get; set; }

    /// <summary>Tick at which this entity is removed outright (0 = never). Used by monster corpses.</summary>
    public uint DespawnAtTick { get; set; }

    // --- Combat intent ---

    /// <summary>Entity this player is auto-attacking (0 = none). The server drives the swings.</summary>
    public int AutoAttackTargetId { get; set; }

    /// <summary>The ability the server swings with (wand for mages, weapon strike otherwise).</summary>
    public byte AutoAttackAbilityId { get; set; }

    /// <summary>Tick at which the GLOBAL COOLDOWN ends (manual abilities only).</summary>
    public uint GcdReadyTick { get; set; }

    // --- Incantation (cast-time spells) ---
    public byte CastAbilityId { get; private set; }
    public int CastTargetId { get; private set; }
    public uint CastStartTick { get; private set; }
    public uint CastEndTick { get; private set; }

    public bool IsCasting => CastAbilityId != 0;

    public void BeginCast(byte abilityId, int targetId, uint now, int castTicks)
    {
        CastAbilityId = abilityId;
        CastTargetId = targetId;
        CastStartTick = now;
        CastEndTick = now + (uint)castTicks;
    }

    public void CancelCast()
    {
        CastAbilityId = 0;
        CastTargetId = 0;
        CastStartTick = 0;
        CastEndTick = 0;
    }

    /// <summary>Cast progress 0..255 at the given tick (for cast bars in snapshots).</summary>
    public byte CastProgressAt(uint tick)
    {
        if (!IsCasting || CastEndTick <= CastStartTick)
        {
            return 0;
        }

        float t = (tick - CastStartTick) / (float)(CastEndTick - CastStartTick);
        return (byte)System.Math.Clamp((int)(t * 255f), 0, 255);
    }

    /// <summary>Tick the current cosmetic jump started at (0 = not jumping).</summary>
    public uint JumpStartTick { get; set; }

    /// <summary>A jump lasts this many ticks (~0.6s of hang time).</summary>
    public const int JumpDurationTicks = 12;

    public bool IsJumpingAt(uint tick) =>
        JumpStartTick != 0 && tick - JumpStartTick < JumpDurationTicks;

    // --- Inventory, equipment & currency (players) ---
    public Inventory Inventory { get; }

    private readonly byte[] _equipment = new byte[EquipSlots.Count];

    /// <summary>Item id per equipment slot (index = (int)EquipSlot; 0 = empty).</summary>
    public IReadOnlyList<byte> Equipment => _equipment;

    public byte GetEquipped(EquipSlot slot) => _equipment[(int)slot];

    public void SetEquipped(EquipSlot slot, byte itemId) => _equipment[(int)slot] = itemId;

    /// <summary>Snapshot of the equipment array (for wire messages).</summary>
    public byte[] CopyEquipment()
    {
        var copy = new byte[EquipSlots.Count];
        _equipment.CopyTo(copy, 0);
        return copy;
    }

    // Legacy accessors kept so early call sites read naturally.
    public byte EquippedWeaponId
    {
        get => _equipment[(int)EquipSlot.Weapon];
        set => _equipment[(int)EquipSlot.Weapon] = value;
    }

    public byte EquippedArmorId
    {
        get => _equipment[(int)EquipSlot.Chest];
        set => _equipment[(int)EquipSlot.Chest] = value;
    }

    /// <summary>Equipment stat bonuses, recomputed by the World whenever gear changes.</summary>
    public int EquipmentAttackBonus { get; set; }
    public int EquipmentDefenseBonus { get; set; }
    public int EquipmentHealthBonus { get; set; }

    // --- Progression (players) ---
    public int TotalXp { get; set; }

    // ------------------------------------------------------------ Quest chain
    /// <summary>The quest currently being pursued (0 = none).</summary>
    public byte ActiveQuestId { get; set; }

    /// <summary>Kills counted toward the active quest's objective.</summary>
    public int QuestKills { get; set; }

    /// <summary>Highest quest id already completed (the chain is linear).</summary>
    public byte QuestCompletedUpTo { get; set; }
    public int Level { get; set; } = 1;

    /// <summary>Progression stat bonuses derived from total XP, recomputed by the World.</summary>
    public int ProgressionAttackBonus { get; set; }
    public int ProgressionDefenseBonus { get; set; }
    public int ProgressionHealthBonus { get; set; }

    /// <summary>Loot pile for a corpse entity; null for everything else.</summary>
    public Inventory? LootContainer { get; set; }

    /// <summary>
    /// Druid shapeshift: 0 humanoid, 1 BEAR (tanky, slower hits), 2 OWL (empowered spells),
    /// 3 CAT (fast fierce melee). Each form multiplies the effective stats below.
    /// </summary>
    public byte FormId { get; set; }

    /// <summary>The hearthstone's bound HOME (an inn); (0,0) = the sanctuary by default.</summary>
    public float HomeX { get; set; }
    public float HomeY { get; set; }

    /// <summary>First tick the hearthstone may fire again (15-minute cooldown).</summary>
    public uint HearthReadyTick { get; set; }

    /// <summary>Shared potion cooldown: first tick a potion may be swallowed again.</summary>
    public uint PotionReadyTick { get; set; }

    private float FormAttackFactor => FormId switch { 1 => 0.9f, 2 => 1.25f, 3 => 1.25f, _ => 1f };
    private float FormDefenseFactor => FormId switch { 1 => 1.6f, _ => 1f };
    private float FormHealthFactor => FormId switch { 1 => 1.3f, _ => 1f };

    // --- Effective stats (base + equipment + progression, then multiplied by active buffs) ---
    public int EffectiveAttackPower
        => (int)MathF.Round((Stats.AttackPower + EquipmentAttackBonus + ProgressionAttackBonus)
            * (1f + SumMagnitude(EffectType.BuffAttack)) * FormAttackFactor);

    public int EffectiveDefense
        => (int)MathF.Round((Stats.Defense + EquipmentDefenseBonus + ProgressionDefenseBonus)
            * (1f + SumMagnitude(EffectType.BuffDefense)) * FormDefenseFactor);

    public float EffectiveMoveSpeed => Stats.MoveSpeed * (1f + SumMagnitude(EffectType.BuffMoveSpeed));

    /// <summary>Max health after equipment and progression bonuses (and the bear's thick hide).</summary>
    public int EffectiveMaxHealth
        => (int)MathF.Round((Stats.MaxHealth + EquipmentHealthBonus + ProgressionHealthBonus) * FormHealthFactor);

    /// <summary>Initialize the resource pool for a class (rage starts empty; mana/energy start full).</summary>
    public void InitResource(ResourceType type, int max, float regenPerTick)
    {
        ResourceType = type;
        MaxResource = max;
        ResourceRegenPerTick = regenPerTick;
        CurrentResource = type == ResourceType.Rage ? 0f : max;
    }

    public bool HasResource(int cost) => CurrentResource >= cost;

    public void SpendResource(int cost) => CurrentResource = System.Math.Max(0f, CurrentResource - cost);

    public void GainResource(float amount)
        => CurrentResource = System.Math.Clamp(CurrentResource + amount, 0f, EffectiveMaxResource);

    public bool IsAbilityReady(byte abilityId, uint tick)
        => !_abilityReadyTick.TryGetValue(abilityId, out uint readyAt) || tick >= readyAt;

    public void StartCooldown(byte abilityId, uint readyTick) => _abilityReadyTick[abilityId] = readyTick;

    /// <summary>Apply damage (already floored to at least 1 by the caller). Marks death at 0 HP.</summary>
    public void TakeDamage(int amount, uint tick)
    {
        if (IsDead)
        {
            return;
        }

        LastCombatTick = tick;
        Health -= amount;
        if (Health <= 0)
        {
            Health = 0;
            IsDead = true;
            MoveIntent = Vec2.Zero;
            AiTargetId = null;
            _effects.Clear();
            RespawnAtTick = tick + (uint)RespawnDelayTicks;
        }
    }

    /// <summary>Restore a fraction of max health (instant effect). No-op if dead.</summary>
    public void Heal(float fractionOfMax)
    {
        if (IsDead)
        {
            return;
        }

        int amount = (int)MathF.Round(EffectiveMaxHealth * fractionOfMax);
        Health = System.Math.Min(EffectiveMaxHealth, Health + amount);
    }

    /// <summary>Set health to the current effective maximum (after gear/progression are applied).</summary>
    public void RestoreToFull() => Health = EffectiveMaxHealth;

    /// <summary>Clamp health into the current pool (leaving bear form shrinks it).</summary>
    public void ClampHealthToMax() => Health = System.Math.Min(Health, EffectiveMaxHealth);

    /// <summary>Restore a fraction of the max resource pool (instant effect).</summary>
    public void RestoreResourceFraction(float fractionOfMax) => GainResource(EffectiveMaxResource * fractionOfMax);

    // --- Weapon/spell proficiency (skill lines) ---
    private readonly Dictionary<byte, int> _skill = new();

    /// <summary>Current skill in a proficiency line (0 if untrained).</summary>
    public int GetSkill(byte skillLineId) => _skill.TryGetValue(skillLineId, out int v) ? v : 0;

    /// <summary>Train a skill line by <paramref name="amount"/>, capped at <paramref name="cap"/>.</summary>
    public void AddSkill(byte skillLineId, int amount, int cap)
    {
        if (skillLineId == 0 || amount <= 0)
        {
            return;
        }

        _skill[skillLineId] = System.Math.Min(cap, GetSkill(skillLineId) + amount);
    }

    public void MarkInCombat(uint tick) => LastCombatTick = tick;

    /// <summary>Add a timed buff effect that expires at the given tick.</summary>
    public void AddEffect(EffectType type, float magnitude, uint expiresAtTick)
        => _effects.Add(new ActiveEffect(type, magnitude, expiresAtTick));

    /// <summary>The timed effects currently running (party frames read these).</summary>
    public IReadOnlyList<ActiveEffect> ActiveEffects => _effects;

    public bool HasEffect(EffectType type)
    {
        foreach (ActiveEffect e in _effects)
        {
            if (e.Type == type)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Per-tick upkeep: expire finished effects, apply regen ticks, regenerate/decay resource.</summary>
    public void TickUpkeep(uint tick, float dt)
    {
        // Expire buffs.
        for (int i = _effects.Count - 1; i >= 0; i--)
        {
            if (tick >= _effects[i].ExpiresAtTick)
            {
                _effects.RemoveAt(i);
            }
        }

        if (IsDead)
        {
            return;
        }

        // Regeneration effects: magnitude is a fraction of the max pool restored PER SECOND.
        // Health is integer, so fractional per-tick amounts accumulate in a carry — otherwise a
        // 0.24hp tick would round to zero and the effect would never heal. Mana is float already.
        foreach (ActiveEffect effect in _effects)
        {
            if (effect.Type == EffectType.Regen)
            {
                _regenHealthCarry += EffectiveMaxHealth * effect.Magnitude * dt;
                if (ResourceType == ResourceType.Mana)
                {
                    GainResource(EffectiveMaxResource * effect.Magnitude * dt);
                }
            }
        }

        if (_regenHealthCarry >= 1f)
        {
            int whole = (int)_regenHealthCarry;
            _regenHealthCarry -= whole;
            Health = System.Math.Min(EffectiveMaxHealth, Health + whole);
        }

        if (ResourceType == ResourceType.Rage)
        {
            // Rage decays once the entity has been out of combat for a while.
            float secondsSinceCombat = (tick - LastCombatTick) * SimulationConstants.TickDelta;
            if (secondsSinceCombat >= OutOfCombatSeconds)
            {
                GainResource(-RageDecayPerSecond * dt);
            }
        }
        else if (ResourceRegenPerTick > 0f)
        {
            GainResource(ResourceRegenPerTick);
        }
    }

    /// <summary>Rage generated by landing a hit (Warriors only).</summary>
    public void OnDealtDamage(uint tick)
    {
        MarkInCombat(tick);
        if (ResourceType == ResourceType.Rage)
        {
            GainResource(RageOnDealDamage);
        }
    }

    /// <summary>Rage generated by being hit (Warriors only).</summary>
    public void OnTookDamage(uint tick)
    {
        if (ResourceType == ResourceType.Rage)
        {
            GainResource(RageOnTakeDamage);
        }
    }

    /// <summary>Bring a dead entity back to full health/resource at its spawn point.</summary>
    public void Respawn()
    {
        IsDead = false;
        Health = EffectiveMaxHealth;
        Position = SpawnPosition;
        MoveIntent = Vec2.Zero;
        AiTargetId = null;
        IsEvading = false;
        _effects.Clear();
        _abilityReadyTick.Clear();
        CurrentResource = ResourceType == ResourceType.Rage ? 0f : EffectiveMaxResource;
    }

    /// <summary>
    /// Hardcore permadeath: wipe the character's progression (XP, level, stat bonuses, and trained
    /// skills) so it starts over from scratch. Inventory/equipment are stripped separately (to the
    /// full-loot corpse); the account bank is account-scoped and untouched.
    /// </summary>
    public void ResetForPermadeath()
    {
        TotalXp = 0;
        Level = 1;
        ProgressionAttackBonus = 0;
        ProgressionDefenseBonus = 0;
        ProgressionHealthBonus = 0;
        ProgressionResourceBonus = 0;
        _skill.Clear();
    }

    private float SumMagnitude(EffectType type)
    {
        float sum = 0f;
        foreach (ActiveEffect e in _effects)
        {
            if (e.Type == type)
            {
                sum += e.Magnitude;
            }
        }

        return sum;
    }
}
