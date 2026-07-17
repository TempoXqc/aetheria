using Aetheria.Server.Items;
using Aetheria.Shared;
using Aetheria.Shared.Combat;
using Aetheria.Shared.Data;
using Aetheria.Shared.Items;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;
using Aetheria.Shared.Spatial;

namespace Aetheria.Server.World;

/// <summary>
/// The authoritative game world: the single source of truth for every entity's state, plus the rules
/// that turn client input into outcomes. Clients propose; the world disposes. All mutation happens
/// here, on the simulation thread, which is what makes the server trustworthy for PvE and PvP.
///
/// The interest grid holds only *alive* entities, so dead entities are neither rendered nor targetable
/// while they wait to respawn.
/// </summary>
public sealed class World
{
    private readonly GameData _gameData;
    private readonly Dictionary<int, ServerEntity> _entities = new();
    private readonly SpatialGrid _grid = new(SimulationConstants.GridCellSize);
    private readonly List<int> _queryScratch = new(64);
    private readonly List<CombatEventMessage> _combatEvents = new();
    private readonly List<int> _respawnScratch = new();
    private readonly List<ServerEntity> _aiScratch = new();

    private int _nextEntityId = 1;
    private int _spawnCounter;

    public World(GameData? gameData = null) => _gameData = gameData ?? GameData.CreateDefault();

    /// <summary>The current simulation tick (increments once per fixed step).</summary>
    public uint Tick { get; private set; }

    /// <summary>The content registry backing this world.</summary>
    public GameData GameData => _gameData;

    /// <summary>All entities, alive or awaiting respawn, keyed by id.</summary>
    public IReadOnlyDictionary<int, ServerEntity> Entities => _entities;

    /// <summary>Create a player entity owned by <paramref name="owner"/>, with stats from race + class.</summary>
    public ServerEntity SpawnPlayer(
        PeerId owner, string name = "", byte raceId = 1, byte classId = 1, Gender gender = Gender.Male)
    {
        ClassDefinition cls = _gameData.GetClass(classId);
        RaceDefinition race = _gameData.GetRace(raceId);
        StatBlock stats = StatBlock.Combine(cls.ToBaseStats(), race.ToModifiers());

        int id = _nextEntityId++;
        var entity = new ServerEntity(id, EntityKind.Player, NextSpawnPosition(), stats, cls.BasicAbilityId)
        {
            Owner = owner,
            Name = string.IsNullOrWhiteSpace(name) ? $"Player{id}" : name,
            RaceId = race.Id,
            ClassId = cls.Id,
            Gender = gender,
            Faction = race.Faction,
            RacialAbilityId = race.RacialAbilityId,
        };
        entity.InitResource(cls.Resource, cls.MaxResource, cls.ResourceRegenPerSec * SimulationConstants.TickDelta);
        ApplyXp(entity, 0); // sets level 1 and zero progression bonuses
        entity.RestoreToFull();

        AddAlive(entity);
        return entity;
    }

    /// <summary>
    /// Give a freshly spawned player their starting gold, gear, and consumables — so a new character
    /// has something to lose (and drop as loot) on death. Kept separate from <see cref="SpawnPlayer"/>
    /// so the base spawn stays a clean, gear-free character for tests and future custom flows.
    /// </summary>
    public void GrantStarterKit(ServerEntity entity)
    {
        entity.Inventory.AddGold(SimulationConstants.StartingGold);
        entity.EquippedWeaponId = 1;               // Rusty Sword
        AddItem(entity, itemId: 20, quantity: 2);  // Minor Healing Potions
        RecomputeEquipment(entity);
        entity.RestoreToFull();
    }

    /// <summary>Add items to an entity's inventory, honouring the item's stacking rules.</summary>
    public int AddItem(ServerEntity entity, byte itemId, int quantity)
    {
        ItemDefinition def = _gameData.GetItem(itemId);
        return entity.Inventory.TryAdd(itemId, quantity, def.Stackable, def.MaxStack);
    }

    /// <summary>Recompute an entity's equipment stat bonuses from its currently equipped gear.</summary>
    private void RecomputeEquipment(ServerEntity entity)
    {
        int atk = 0, def = 0, hp = 0;

        if (entity.EquippedWeaponId != 0 && _gameData.HasItem(entity.EquippedWeaponId))
        {
            ItemDefinition w = _gameData.GetItem(entity.EquippedWeaponId);
            atk += w.AttackBonus; def += w.DefenseBonus; hp += w.HealthBonus;
        }

        if (entity.EquippedArmorId != 0 && _gameData.HasItem(entity.EquippedArmorId))
        {
            ItemDefinition a = _gameData.GetItem(entity.EquippedArmorId);
            atk += a.AttackBonus; def += a.DefenseBonus; hp += a.HealthBonus;
        }

        entity.EquipmentAttackBonus = atk;
        entity.EquipmentDefenseBonus = def;
        entity.EquipmentHealthBonus = hp;
    }

    /// <summary>Grant experience and recompute the resulting level and progression stat bonuses.</summary>
    private void ApplyXp(ServerEntity entity, int xp)
    {
        entity.TotalXp += System.Math.Max(0, xp);
        ProgressionConfig p = _gameData.Progression;
        entity.Level = p.LevelForXp(entity.TotalXp);
        entity.ProgressionAttackBonus = p.AttackBonusForXp(entity.TotalXp);
        entity.ProgressionDefenseBonus = p.DefenseBonusForXp(entity.TotalXp);
        entity.ProgressionHealthBonus = p.HealthBonusForXp(entity.TotalXp);
    }

    /// <summary>Create a monster entity from a monster definition at a fixed spawn position.</summary>
    public ServerEntity SpawnMonster(byte monsterId, Vec2 position)
    {
        MonsterDefinition def = _gameData.GetMonster(monsterId);

        int id = _nextEntityId++;
        var entity = new ServerEntity(id, EntityKind.Monster, position, def.ToStats(), def.BasicAbilityId)
        {
            Name = def.Name,
            MonsterId = def.Id,
        };

        AddAlive(entity);
        return entity;
    }

    /// <summary>Remove an entity from the world and the interest grid.</summary>
    public void Despawn(int entityId)
    {
        if (_entities.Remove(entityId))
        {
            _grid.Remove(entityId);
        }
    }

    /// <summary>
    /// Record a movement intent for a living entity, ignoring inputs that arrive out of order and
    /// clamping the direction to unit length (so a client cannot move faster with an oversized vector).
    /// </summary>
    public void ApplyInput(int entityId, uint sequence, Vec2 moveDirection)
    {
        if (!_entities.TryGetValue(entityId, out ServerEntity? entity) || entity.IsDead)
        {
            return;
        }

        if (sequence != 0 && sequence <= entity.LastInputSequence)
        {
            return; // Stale or duplicate input.
        }

        entity.LastInputSequence = sequence;
        entity.MoveIntent = ClampToUnit(moveDirection);
    }

    /// <summary>
    /// Attempt to use an ability from one entity on another. Validates that both are alive, the ability
    /// is off cooldown, and the target is in range, then applies authoritative damage. Returns true if
    /// the ability resolved. Any resulting combat event is queued for <see cref="DrainCombatEvents"/>.
    /// </summary>
    public bool TryUseAbility(int attackerId, byte abilityId, int targetId)
    {
        if (!_entities.TryGetValue(attackerId, out ServerEntity? attacker) || attacker.IsDead ||
            !_entities.TryGetValue(targetId, out ServerEntity? target) || target.IsDead ||
            attackerId == targetId ||
            target.Kind == EntityKind.Corpse) // corpses are looted, not attacked
        {
            return false;
        }

        AbilityDefinition ability = _gameData.GetAbility(abilityId);

        // Players may only use abilities in their class kit that their level has unlocked.
        if (attacker.Kind == EntityKind.Player)
        {
            ClassDefinition cls = _gameData.GetClass(attacker.ClassId);
            if (!cls.HasAbility(ability.Id) || attacker.Level < ability.UnlockLevel)
            {
                return false;
            }
        }

        if (!attacker.IsAbilityReady(ability.Id, Tick))
        {
            return false;
        }

        if (ability.ResourceCost > 0 && !attacker.HasResource(ability.ResourceCost))
        {
            return false; // Not enough rage/mana/energy.
        }

        if (Vec2.DistanceSquared(attacker.Position, target.Position) > ability.Range * ability.Range)
        {
            return false;
        }

        attacker.StartCooldown(ability.Id, Tick + (uint)ability.CooldownTicks);
        attacker.SpendResource(ability.ResourceCost);

        if (ability.BaseDamage > 0)
        {
            DealDamage(attacker, target, ability);
        }

        return true;
    }

    /// <summary>
    /// Cast the entity's racial ability on itself (heal, resource restore, or a timed self-buff).
    /// Racials cost no resource but have a long cooldown. Returns true if it fired.
    /// </summary>
    public bool TryUseRacial(int entityId)
    {
        if (!_entities.TryGetValue(entityId, out ServerEntity? entity) ||
            entity.IsDead || entity.RacialAbilityId == 0)
        {
            return false;
        }

        AbilityDefinition racial = _gameData.GetAbility(entity.RacialAbilityId);
        if (!entity.IsAbilityReady(racial.Id, Tick))
        {
            return false;
        }

        entity.StartCooldown(racial.Id, Tick + (uint)racial.CooldownTicks);
        ApplyEffectToSelf(entity, racial);
        return true;
    }

    private void ApplyEffectToSelf(ServerEntity entity, AbilityDefinition ability)
    {
        switch (ability.Effect)
        {
            case EffectType.Heal:
                entity.Heal(ability.EffectMagnitude);
                break;
            case EffectType.RestoreResource:
                entity.RestoreResourceFraction(ability.EffectMagnitude);
                break;
            case EffectType.BuffAttack:
            case EffectType.BuffDefense:
            case EffectType.BuffMoveSpeed:
                entity.AddEffect(ability.Effect, ability.EffectMagnitude, Tick + (uint)ability.EffectDurationTicks);
                break;
            case EffectType.None:
            default:
                break;
        }
    }

    /// <summary>Advance the simulation by one fixed step: upkeep, respawns, monster AI, then movement.</summary>
    public void Step(float dt)
    {
        Tick++;

        foreach (ServerEntity e in _entities.Values)
        {
            e.TickUpkeep(Tick, dt); // expire buffs, regenerate/decay resource
        }

        ProcessRespawns();
        RunMonsterAi();
        IntegrateMovement(dt);
    }

    /// <summary>
    /// Build the set of *alive* entities visible to an observer at <paramref name="center"/> — the
    /// contents of one client's area of interest, with health included for HP bars.
    /// </summary>
    public List<EntitySnapshot> BuildAreaSnapshot(Vec2 center)
    {
        _grid.QueryRadius(center, SimulationConstants.AreaOfInterestRadius, _queryScratch);

        var result = new List<EntitySnapshot>(_queryScratch.Count);
        foreach (int id in _queryScratch)
        {
            if (_entities.TryGetValue(id, out ServerEntity? e) && e.IsAlive)
            {
                result.Add(new EntitySnapshot(
                    e.Id, e.Kind, e.Faction, e.Position,
                    e.Health, e.EffectiveMaxHealth, (int)e.CurrentResource, e.MaxResource));
            }
        }

        return result;
    }

    /// <summary>Return and clear the combat events generated since the last drain.</summary>
    public IReadOnlyList<CombatEventMessage> DrainCombatEvents()
    {
        if (_combatEvents.Count == 0)
        {
            return [];
        }

        var copy = _combatEvents.ToArray();
        _combatEvents.Clear();
        return copy;
    }

    private void ProcessRespawns()
    {
        _respawnScratch.Clear();
        foreach (ServerEntity e in _entities.Values)
        {
            if (e.IsDead && Tick >= e.RespawnAtTick)
            {
                _respawnScratch.Add(e.Id);
            }
        }

        foreach (int id in _respawnScratch)
        {
            ServerEntity e = _entities[id];
            e.Respawn();
            _grid.InsertOrUpdate(e.Id, e.Position);
        }
    }

    private void RunMonsterAi()
    {
        // Snapshot the monster list: a monster's attack can kill a player and spawn a corpse
        // (mutating _entities), which would otherwise invalidate a live enumerator.
        _aiScratch.Clear();
        foreach (ServerEntity e in _entities.Values)
        {
            if (e.IsMonster && e.IsAlive)
            {
                _aiScratch.Add(e);
            }
        }

        foreach (ServerEntity monster in _aiScratch)
        {
            if (monster.IsDead)
            {
                continue;
            }

            ServerEntity? target = AcquireOrKeepTarget(monster);
            if (target is null)
            {
                monster.MoveIntent = Vec2.Zero; // Idle.
                continue;
            }

            AbilityDefinition ability = _gameData.GetAbility(monster.BasicAbilityId);
            float distSq = Vec2.DistanceSquared(monster.Position, target.Position);

            if (distSq <= ability.Range * ability.Range)
            {
                monster.MoveIntent = Vec2.Zero; // In range: stand and attack.
                if (monster.IsAbilityReady(ability.Id, Tick))
                {
                    monster.StartCooldown(ability.Id, Tick + (uint)ability.CooldownTicks);
                    DealDamage(monster, target, ability);
                }
            }
            else
            {
                monster.MoveIntent = (target.Position - monster.Position).Normalized(); // Chase.
            }
        }
    }

    private ServerEntity? AcquireOrKeepTarget(ServerEntity monster)
    {
        // Keep the current target if it is still valid and within aggro range.
        if (monster.AiTargetId is int currentId &&
            _entities.TryGetValue(currentId, out ServerEntity? current) &&
            current.IsAlive &&
            Vec2.DistanceSquared(monster.Position, current.Position)
                <= monster.Stats.AggroRadius * monster.Stats.AggroRadius)
        {
            return current;
        }

        // Otherwise find the nearest alive player inside the aggro radius.
        _grid.QueryRadius(monster.Position, monster.Stats.AggroRadius, _queryScratch);
        ServerEntity? best = null;
        float bestDistSq = float.MaxValue;

        foreach (int id in _queryScratch)
        {
            if (!_entities.TryGetValue(id, out ServerEntity? candidate) ||
                candidate.Kind != EntityKind.Player || candidate.IsDead)
            {
                continue;
            }

            float d = Vec2.DistanceSquared(monster.Position, candidate.Position);
            if (d < bestDistSq)
            {
                bestDistSq = d;
                best = candidate;
            }
        }

        monster.AiTargetId = best?.Id;
        return best;
    }

    private void IntegrateMovement(float dt)
    {
        foreach (ServerEntity entity in _entities.Values)
        {
            if (entity.IsDead || entity.MoveIntent.LengthSquared <= 0f)
            {
                continue;
            }

            entity.Position += entity.MoveIntent * (entity.EffectiveMoveSpeed * dt);
            _grid.InsertOrUpdate(entity.Id, entity.Position);
        }
    }

    private void DealDamage(ServerEntity attacker, ServerEntity target, AbilityDefinition ability)
    {
        int raw = ability.BaseDamage + attacker.EffectiveAttackPower;
        int damage = System.Math.Max(1, raw - target.EffectiveDefense);

        target.TakeDamage(damage, Tick);
        attacker.OnDealtDamage(Tick); // rage generation + combat timestamp (Warriors)
        target.OnTookDamage(Tick);

        if (target.IsDead)
        {
            _grid.Remove(target.Id); // Pull the fallen entity out of the live world.
            OnEntityKilled(attacker, target);
        }

        _combatEvents.Add(new CombatEventMessage(
            attacker.Id, target.Id, ability.Id, damage, target.Health, target.IsDead));
    }

    private void OnEntityKilled(ServerEntity killer, ServerEntity victim)
    {
        if (victim.Kind == EntityKind.Player)
        {
            // Full loot: the player's carried inventory, equipped gear, and gold drop as a lootable
            // corpse anyone can plunder. The player itself will respawn empty-handed.
            SpawnCorpse(victim);
        }
        else if (victim.IsMonster && killer.Kind == EntityKind.Player)
        {
            MonsterDefinition def = _gameData.GetMonster(victim.MonsterId);
            ApplyXp(killer, def.XpReward);
            killer.Inventory.AddGold(def.GoldReward);
        }
    }

    private void SpawnCorpse(ServerEntity dead)
    {
        var loot = new Inventory(SimulationConstants.CorpseLootCapacity);
        loot.AddGold(dead.Inventory.TakeAllGold());

        foreach (ItemStack stack in dead.Inventory.Stacks.ToArray())
        {
            ItemDefinition def = _gameData.GetItem(stack.ItemId);
            loot.TryAdd(stack.ItemId, stack.Quantity, def.Stackable, def.MaxStack);
        }

        dead.Inventory.Clear();

        MoveEquippedToLoot(loot, dead.EquippedWeaponId);
        MoveEquippedToLoot(loot, dead.EquippedArmorId);
        dead.EquippedWeaponId = 0;
        dead.EquippedArmorId = 0;
        RecomputeEquipment(dead); // the respawned body has no gear until it loots/replaces it

        int id = _nextEntityId++;
        var corpse = new ServerEntity(id, EntityKind.Corpse, dead.Position, new StatBlock(1, 0f, 0, 0, 0f), 0)
        {
            Name = $"{dead.Name}'s Corpse",
            Faction = dead.Faction,
            LootContainer = loot,
        };

        _entities[id] = corpse;
        _grid.InsertOrUpdate(id, corpse.Position);
    }

    private void MoveEquippedToLoot(Inventory loot, byte itemId)
    {
        if (itemId != 0 && _gameData.HasItem(itemId))
        {
            loot.TryAdd(itemId, 1, stackable: false, maxStack: 1);
        }
    }

    /// <summary>
    /// Loot everything from a corpse into the looter's inventory (gold + as many items as fit).
    /// Validates the looter is a living player within range. The corpse despawns once emptied.
    /// Returns true if anything was taken.
    /// </summary>
    public bool TryLootCorpse(int looterId, int corpseId)
    {
        if (!_entities.TryGetValue(looterId, out ServerEntity? looter) || looter.IsDead ||
            looter.Kind != EntityKind.Player ||
            !_entities.TryGetValue(corpseId, out ServerEntity? corpse) ||
            corpse.Kind != EntityKind.Corpse || corpse.LootContainer is null)
        {
            return false;
        }

        if (Vec2.DistanceSquared(looter.Position, corpse.Position)
            > SimulationConstants.LootRange * SimulationConstants.LootRange)
        {
            return false; // too far away
        }

        Inventory loot = corpse.LootContainer;
        bool tookSomething = false;

        int gold = loot.TakeAllGold();
        if (gold > 0)
        {
            looter.Inventory.AddGold(gold);
            tookSomething = true;
        }

        foreach (ItemStack stack in loot.Stacks.ToArray())
        {
            ItemDefinition def = _gameData.GetItem(stack.ItemId);
            int leftover = looter.Inventory.TryAdd(stack.ItemId, stack.Quantity, def.Stackable, def.MaxStack);
            int moved = stack.Quantity - leftover;
            if (moved > 0)
            {
                loot.RemoveQuantity(stack.ItemId, moved);
                tookSomething = true;
            }
        }

        if (loot.IsEmpty)
        {
            Despawn(corpse.Id); // all loot recovered — the body disappears
        }

        return tookSomething;
    }

    private void AddAlive(ServerEntity entity)
    {
        _entities[entity.Id] = entity;
        _grid.InsertOrUpdate(entity.Id, entity.Position);
    }

    private Vec2 NextSpawnPosition()
    {
        // Spread player spawns along a short line near the origin so freshly connected players start
        // inside one another's area of interest. Real spawn logic (towns, graveyards) replaces this.
        int slot = _spawnCounter++;
        float x = ((slot % 10) - 5) * 2f;
        float y = (slot / 10) * 2f;
        return new Vec2(x, y);
    }

    private static Vec2 ClampToUnit(Vec2 v)
    {
        float lenSq = v.LengthSquared;
        return lenSq > 1f ? v.Normalized() : v;
    }
}
