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
/// Allocates entity ids. Shared across every <see cref="World"/> a server runs (open world +
/// instances), so an entity keeps its id when it transfers between worlds and ids never collide.
/// </summary>
public sealed class EntityIdAllocator
{
    private int _next = 1;

    public int Next() => _next++;
}

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

    // Active duels: entity id -> (opponent id, to-death?). Both directions are stored.
    private readonly Dictionary<int, (int opponentId, bool toDeath)> _duels = new();
    private readonly List<(int winnerId, int loserId, bool toDeath)> _duelEndings = new();
    private readonly EntityIdAllocator _ids;

    private int _spawnCounter;

    public World(GameData? gameData = null, EntityIdAllocator? ids = null)
    {
        _gameData = gameData ?? GameData.CreateDefault();
        _ids = ids ?? new EntityIdAllocator();
    }

    /// <summary>The current simulation tick (increments once per fixed step).</summary>
    public uint Tick { get; private set; }

    /// <summary>
    /// True for the open world: a sanctuary circle around the spawn where players can neither
    /// attack nor be attacked (PvP AND PvE), and monsters never aggro. Instances have none.
    /// </summary>
    public bool HasSafeZone { get; set; }

    /// <summary>Is this position inside the sanctuary?</summary>
    public bool IsSafePosition(Vec2 position)
        => HasSafeZone && Vec2.DistanceSquared(
               position,
               new Vec2(SimulationConstants.SafeZoneCenterX, SimulationConstants.SafeZoneCenterY))
           <= SimulationConstants.SafeZoneRadius * SimulationConstants.SafeZoneRadius;

    /// <summary>The content registry backing this world.</summary>
    public GameData GameData => _gameData;

    /// <summary>All entities, alive or awaiting respawn, keyed by id.</summary>
    public IReadOnlyDictionary<int, ServerEntity> Entities => _entities;

    /// <summary>Create a player entity owned by <paramref name="owner"/>, with stats from race + class.</summary>
    public ServerEntity SpawnPlayer(
        PeerId owner, string name = "", byte raceId = 1, byte classId = 1, Gender gender = Gender.Male,
        Appearance appearance = default)
    {
        ClassDefinition cls = _gameData.GetClass(classId);
        RaceDefinition race = _gameData.GetRace(raceId);
        StatBlock stats = StatBlock.Combine(cls.ToBaseStats(), race.ToModifiers());

        int id = _ids.Next();
        var entity = new ServerEntity(id, EntityKind.Player, NextSpawnPosition(), stats, cls.BasicAbilityId)
        {
            Owner = owner,
            Name = string.IsNullOrWhiteSpace(name) ? $"Player{id}" : name,
            RaceId = race.Id,
            ClassId = cls.Id,
            Gender = gender,
            Appearance = appearance.Clamped(), // trust boundary: hostile clients can't smuggle bad indexes
            Faction = race.Faction,
            RacialAbilityId = race.RacialAbilityId,
            AutoAttackAbilityId = cls.EffectiveAutoAttackId, // wand for mages, weapon strike otherwise
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
        entity.EquippedWeaponId = entity.ClassId switch
        {
            2 => (byte)6, // Mage: Worn Staff
            3 => (byte)5, // Ranger: Worn Bow
            _ => (byte)1, // Warrior: Rusty Sword
        };
        AddItem(entity, itemId: 20, quantity: 2);  // Minor Healing Potions
        RecomputeEquipment(entity);

        // Only top up health for a living spawn. When re-kitting a corpse for permadeath the entity is
        // still dead; Respawn() sets its health when it actually returns.
        if (entity.IsAlive)
        {
            entity.RestoreToFull();
        }
    }

    /// <summary>Add items to an entity's inventory, honouring the item's stacking rules.</summary>
    public int AddItem(ServerEntity entity, byte itemId, int quantity)
    {
        ItemDefinition def = _gameData.GetItem(itemId);
        return entity.Inventory.TryAdd(itemId, quantity, def.Stackable, def.MaxStack);
    }

    /// <summary>Set an entity's equipped gear and recompute its equipment bonuses.</summary>
    public void Equip(ServerEntity entity, byte weaponId, byte armorId)
    {
        entity.EquippedWeaponId = weaponId;
        entity.EquippedArmorId = armorId;
        RecomputeEquipment(entity);
    }

    /// <summary>
    /// Equip a weapon/armor from the bags into its slot, swapping the current piece back into the
    /// bags — or unequip a slot (itemId 0) if the bags have room. Server-validated: the item must
    /// really be in the bags and really be equippable.
    /// </summary>
    /// <summary>Reorder the player's bag: move/swap the stack at <paramref name="from"/> to
    /// <paramref name="to"/> (pure presentation — no items created or destroyed).</summary>
    public bool TryMoveItem(int playerId, int from, int to)
    {
        return _entities.TryGetValue(playerId, out ServerEntity? player) &&
               !player.IsDead && player.Kind == EntityKind.Player &&
               player.Inventory.MoveSlot(from, to);
    }

    public bool TryEquipItem(int playerId, byte itemId, EquipSlot slot, int bagIndex = -1)
    {
        if (!_entities.TryGetValue(playerId, out ServerEntity? player) ||
            player.IsDead || player.Kind != EntityKind.Player)
        {
            return false;
        }

        if (itemId == 0)
        {
            // Unequip the given slot back into the bags — into the CHOSEN cell when the player
            // dragged the piece onto one (falls back to the first free cell).
            if (slot == EquipSlot.None || (int)slot >= EquipSlots.Count)
            {
                return false;
            }

            byte current = player.GetEquipped(slot);
            if (current == 0)
            {
                return false;
            }

            ItemDefinition currentDef = _gameData.GetItem(current);
            bool stored = bagIndex >= 0 && player.Inventory.TryInsertAt(bagIndex, current, 1);
            if (!stored)
            {
                stored = player.Inventory.TryAdd(current, 1, currentDef.Stackable, currentDef.MaxStack) == 0;
            }

            if (!stored)
            {
                return false; // bags full: the piece stays on
            }

            player.SetEquipped(slot, 0);
            RecomputeEquipment(player);
            return true;
        }

        if (!_gameData.HasItem(itemId) || player.Inventory.CountOf(itemId) <= 0)
        {
            return false;
        }

        ItemDefinition def = _gameData.GetItem(itemId);
        if (def.Slot == EquipSlot.None)
        {
            return false; // not equippable
        }

        // The item's OWN slot decides where it goes (a helm can only sit on the head).
        EquipSlot target = def.Slot;

        // Take the new piece out first — and remember WHERE it sat, so the replaced piece
        // lands in that exact bag slot (WoW behaviour: the swap happens in place).
        int fromIndex = player.Inventory.IndexOfItem(itemId);
        player.Inventory.RemoveQuantity(itemId, 1);
        byte old = player.GetEquipped(target);
        if (old != 0)
        {
            ItemDefinition oldDef = _gameData.GetItem(old);
            if (!player.Inventory.TryInsertAt(fromIndex, old, 1))
            {
                player.Inventory.TryAdd(old, 1, oldDef.Stackable, oldDef.MaxStack);
            }
        }

        player.SetEquipped(target, itemId);
        RecomputeEquipment(player);
        return true;
    }

    /// <summary>Recompute an entity's equipment stat bonuses from its currently equipped gear.</summary>
    private void RecomputeEquipment(ServerEntity entity)
    {
        int atk = 0, def = 0, hp = 0;

        // Every worn piece counts — head to boots, weapon to off-hand.
        for (int i = 0; i < EquipSlots.Count; i++)
        {
            byte itemId = entity.Equipment[i];
            if (itemId == 0 || !_gameData.HasItem(itemId))
            {
                continue;
            }

            ItemDefinition piece = _gameData.GetItem(itemId);
            atk += piece.AttackBonus; def += piece.DefenseBonus; hp += piece.HealthBonus;
        }

        entity.EquipmentAttackBonus = atk;
        entity.EquipmentDefenseBonus = def;
        entity.EquipmentHealthBonus = hp;
    }

    /// <summary>Grant experience to an entity (public entry point for events, quests, and tests).</summary>
    public void GrantExperience(ServerEntity entity, int xp) => ApplyXp(entity, xp);

    /// <summary>Grant experience and recompute the resulting level and progression stat bonuses.</summary>
    private void ApplyXp(ServerEntity entity, int xp)
    {
        entity.TotalXp += System.Math.Max(0, xp);
        ProgressionConfig p = _gameData.Progression;
        entity.Level = p.LevelForXp(entity.TotalXp);
        entity.ProgressionAttackBonus = p.AttackBonusForXp(entity.TotalXp);
        entity.ProgressionDefenseBonus = p.DefenseBonusForXp(entity.TotalXp);
        entity.ProgressionHealthBonus = p.HealthBonusForXp(entity.TotalXp);
        entity.ProgressionResourceBonus = p.ManaBonusForXp(entity.TotalXp); // only mana uses it
    }

    /// <summary>
    /// Create a monster entity from a monster definition at a fixed spawn position, optionally scaled
    /// (instances multiply monster health/damage by group size).
    /// </summary>
    public ServerEntity SpawnMonster(byte monsterId, Vec2 position, float healthMult = 1f, float damageMult = 1f)
    {
        MonsterDefinition def = _gameData.GetMonster(monsterId);
        StatBlock baseStats = def.ToStats();
        StatBlock stats = healthMult == 1f && damageMult == 1f
            ? baseStats
            : new StatBlock(
                (int)MathF.Round(baseStats.MaxHealth * healthMult),
                baseStats.MoveSpeed,
                (int)MathF.Round(baseStats.AttackPower * damageMult),
                baseStats.Defense,
                baseStats.AggroRadius);

        int id = _ids.Next();
        var entity = new ServerEntity(id, EntityKind.Monster, position, stats, def.BasicAbilityId)
        {
            Name = def.Name,
            MonsterId = def.Id,
            Level = def.Level,
        };

        AddAlive(entity);
        return entity;
    }

    /// <summary>Move an entity instantly AND update the interest grid (tests, future admin tools).</summary>
    public void Teleport(ServerEntity entity, Vec2 position)
    {
        ArgumentNullException.ThrowIfNull(entity);
        entity.Position = position;
        _grid.InsertOrUpdate(entity.Id, position);
    }

    /// <summary>Spawn a friendly interactive NPC/object (bank chest, future vendors). Invulnerable.</summary>
    public ServerEntity SpawnNpc(string name, Vec2 position, byte npcType = 1)
    {
        int id = _ids.Next();
        var npc = new ServerEntity(id, EntityKind.Npc, position, new StatBlock(1, 0f, 0, 0, 0f), 0)
        {
            Name = name,
            Faction = Faction.Neutral,
            RaceId = npcType, // 1 = bank chest, 2 = quest giver, 3 = flavour villager
        };

        AddAlive(npc);
        return npc;
    }

    // ------------------------------------------------------------------ Quests

    private readonly List<int> _questDirty = new();

    /// <summary>Players whose quest progress changed since the last drain (server pushes state).</summary>
    public IReadOnlyList<int> DrainQuestDirty()
    {
        if (_questDirty.Count == 0)
        {
            return [];
        }

        var copy = _questDirty.ToArray();
        _questDirty.Clear();
        return copy;
    }

    private bool NearQuestGiver(ServerEntity player)
    {
        foreach (ServerEntity e in _entities.Values)
        {
            if (e.Kind == EntityKind.Npc && e.RaceId == 2 &&
                Vec2.DistanceSquared(player.Position, e.Position) <= SimulationConstants.QuestGiverRange * SimulationConstants.QuestGiverRange)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Accept the next quest in the chain, or turn in the finished one — both validated: the
    /// quest must exist, follow the chain, and the player must stand at the quest giver.
    /// </summary>
    public bool TryQuestAction(int playerId, byte questId, bool turnIn)
    {
        if (!_entities.TryGetValue(playerId, out ServerEntity? player) ||
            player.IsDead || player.Kind != EntityKind.Player || !NearQuestGiver(player))
        {
            return false;
        }

        QuestDefinition? quest = _gameData.GetQuest(questId);
        if (quest == null)
        {
            return false;
        }

        if (!turnIn)
        {
            // Accept: nothing active, and it must be the NEXT link of the chain.
            if (player.ActiveQuestId != 0 || questId != player.QuestCompletedUpTo + 1)
            {
                return false;
            }

            player.ActiveQuestId = questId;
            player.QuestKills = 0;
            return true;
        }

        // Turn in: the objective must be complete.
        if (player.ActiveQuestId != questId || player.QuestKills < quest.RequiredKills)
        {
            return false;
        }

        ApplyXp(player, quest.RewardXp);
        player.Inventory.AddGold(quest.RewardGold);
        if (quest.RewardItemId != 0 && _gameData.HasItem(quest.RewardItemId))
        {
            AddItem(player, quest.RewardItemId, 1);
        }

        player.QuestCompletedUpTo = questId;
        player.ActiveQuestId = 0;
        player.QuestKills = 0;
        return true;
    }

    /// <summary>
    /// Pull an entity out of this world (for transfer into another world). Returns null if absent.
    /// The entity keeps its id — ids come from an allocator shared across worlds.
    /// </summary>
    public ServerEntity? RemoveForTransfer(int entityId)
    {
        if (!_entities.Remove(entityId, out ServerEntity? entity))
        {
            return null;
        }

        _grid.Remove(entityId);
        return entity;
    }

    /// <summary>Insert a transferred entity into this world at the given position (also its new respawn point).</summary>
    public void AdoptEntity(ServerEntity entity, Vec2 position)
    {
        entity.Position = position;
        entity.SpawnPosition = position;
        entity.MoveIntent = Vec2.Zero;
        entity.AiTargetId = null;
        _entities[entity.Id] = entity;
        if (entity.IsAlive)
        {
            _grid.InsertOrUpdate(entity.Id, position);
        }
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
    public void ApplyInput(int entityId, uint sequence, Vec2 moveDirection, float facingRadians = 0f,
        bool jump = false)
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
        entity.FacingRadians = facingRadians;

        // WoW rule: MOVING breaks the incantation.
        if (entity.IsCasting && entity.MoveIntent.LengthSquared > 0.0001f)
        {
            entity.CancelCast();
        }

        if (jump && !entity.IsJumpingAt(Tick))
        {
            entity.JumpStartTick = Tick == 0 ? 1 : Tick; // cosmetic hop, relayed via snapshot flags
        }
    }

    /// <summary>
    /// Attempt to use an ability from one entity on another. Validates that both are alive, the ability
    /// is off cooldown, and the target is in range, then applies authoritative damage. Returns true if
    /// the ability resolved. Any resulting combat event is queued for <see cref="DrainCombatEvents"/>.
    /// </summary>
    public bool TryUseAbility(int attackerId, byte abilityId, int targetId, bool fromAuto = false)
    {
        // GLOBAL COOLDOWN (players, manual abilities only — server auto-attacks bypass it).
        if (!fromAuto &&
            _entities.TryGetValue(attackerId, out ServerEntity? gcdCheck) &&
            gcdCheck.Kind == EntityKind.Player && Tick < gcdCheck.GcdReadyTick)
        {
            return false;
        }

        // Self-cast abilities (range 0, e.g. Renew) ignore the target entirely.
        if (_entities.TryGetValue(attackerId, out ServerEntity? selfCaster) && selfCaster.IsAlive)
        {
            AbilityDefinition selfDef = _gameData.GetAbility(abilityId);
            if (selfDef.Range <= 0f && selfDef.Id == abilityId)
            {
                if (selfCaster.Kind == EntityKind.Player)
                {
                    ClassDefinition casterClass = _gameData.GetClass(selfCaster.ClassId);
                    if (!casterClass.HasAbility(selfDef.Id) || selfCaster.Level < selfDef.UnlockLevel)
                    {
                        return false;
                    }
                }

                if (!selfCaster.IsAbilityReady(selfDef.Id, Tick) ||
                    (selfDef.ResourceCost > 0 && !selfCaster.HasResource(selfDef.ResourceCost)))
                {
                    return false;
                }

                selfCaster.StartCooldown(selfDef.Id, Tick + (uint)selfDef.CooldownTicks);
                selfCaster.SpendResource(selfDef.ResourceCost);
                ApplyEffectToSelf(selfCaster, selfDef);
                TouchGcd(selfCaster, fromAuto);
                return true;
            }
        }

        if (!_entities.TryGetValue(attackerId, out ServerEntity? attacker) || attacker.IsDead ||
            !_entities.TryGetValue(targetId, out ServerEntity? target) || target.IsDead ||
            attackerId == targetId ||
            target.Kind == EntityKind.Corpse ||        // corpses are looted, not attacked
            target.Kind == EntityKind.MonsterCorpse || // cosmetic remains
            target.Kind == EntityKind.Npc)             // bank chests & friends are invulnerable
        {
            return false;
        }

        // Faction rule: players cannot attack players of their own camp. Opposite factions can —
        // that is open-world PvP. Exception: an active DUEL pair may always fight each other.
        if (attacker.Kind == EntityKind.Player && target.Kind == EntityKind.Player &&
            attacker.Faction == target.Faction && !IsDuelPair(attackerId, targetId))
        {
            return false;
        }

        // Sanctuary: inside the safe zone a player can neither strike nor be struck — by anyone.
        if ((attacker.Kind == EntityKind.Player && IsSafePosition(attacker.Position)) ||
            (target.Kind == EntityKind.Player && IsSafePosition(target.Position)))
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

        // One incantation at a time: while casting, further requests are refused (the auto-attack
        // loop simply retries after the cast lands).
        if (attacker.IsCasting)
        {
            return false;
        }

        // Cast-time spell (players): start the INCANTATION. Resource and cooldown are only paid
        // when the cast completes; moving cancels it — see ProcessCasts/ApplyInput.
        if (ability.CastTimeTicks > 0 && attacker.Kind == EntityKind.Player)
        {
            attacker.BeginCast(ability.Id, targetId, Tick, ability.CastTimeTicks);
            TouchGcd(attacker, fromAuto);
            return true;
        }

        ExecuteAbility(attacker, target, ability);
        TouchGcd(attacker, fromAuto);
        return true;
    }

    /// <summary>Arm the global cooldown after a MANUAL player action (auto-attacks bypass it).</summary>
    private void TouchGcd(ServerEntity entity, bool fromAuto)
    {
        if (!fromAuto && entity.Kind == EntityKind.Player)
        {
            entity.GcdReadyTick = Tick + SimulationConstants.GlobalCooldownTicks;
        }
    }

    /// <summary>Set (or clear, with 0) the entity this player wants to fight. The server swings.</summary>
    public void SetAttackTarget(int playerId, int targetId)
    {
        if (_entities.TryGetValue(playerId, out ServerEntity? player) && player.Kind == EntityKind.Player)
        {
            player.AutoAttackTargetId = targetId;
        }
    }

    /// <summary>
    /// WoW-style auto-attack: for every player with an attack intent, swing the class's basic
    /// attack (or start its basic incantation) whenever it is ready and in range. Clears itself
    /// when the target dies or disappears.
    /// </summary>
    private void ProcessAutoAttacks()
    {
        _aiScratch.Clear();
        foreach (ServerEntity e in _entities.Values)
        {
            if (e.Kind == EntityKind.Player && e.AutoAttackTargetId != 0 && e.IsAlive)
            {
                _aiScratch.Add(e);
            }
        }

        foreach (ServerEntity player in _aiScratch)
        {
            if (!_entities.TryGetValue(player.AutoAttackTargetId, out ServerEntity? target) ||
                target.IsDead || target.Kind == EntityKind.Corpse ||
                target.Kind == EntityKind.MonsterCorpse || target.Kind == EntityKind.Npc)
            {
                player.AutoAttackTargetId = 0; // target gone: stand down
                continue;
            }

            if (player.IsCasting)
            {
                continue; // busy incanting (possibly the auto-recast itself)
            }

            // Silent attempt: range, swing timer (the ability's own cooldown), resource and the
            // faction/sanctuary rules are all enforced inside TryUseAbility.
            byte swing = player.AutoAttackAbilityId != 0 ? player.AutoAttackAbilityId : player.BasicAbilityId;
            TryUseAbility(player.Id, swing, player.AutoAttackTargetId, fromAuto: true);
        }
    }

    /// <summary>Pay the costs and land the ability (shared by instant casts and finished incantations).</summary>
    private void ExecuteAbility(ServerEntity attacker, ServerEntity target, AbilityDefinition ability)
    {
        attacker.StartCooldown(ability.Id, Tick + (uint)ability.CooldownTicks);
        attacker.SpendResource(ability.ResourceCost);

        if (ability.BaseDamage > 0)
        {
            DealDamage(attacker, target, ability);
        }
    }

    /// <summary>
    /// Resolve finished incantations: re-validate the world (alive, range with a small grace,
    /// sanctuary, resource) and land the spell — or fizzle silently if the world moved on.
    /// </summary>
    private void ProcessCasts()
    {
        // Snapshot the casters FIRST: resolving a lethal cast spawns corpses/remains, which
        // mutates _entities — enumerating the live dictionary here crashed the server.
        _aiScratch.Clear();
        foreach (ServerEntity e in _entities.Values)
        {
            if (e.IsCasting)
            {
                _aiScratch.Add(e);
            }
        }

        foreach (ServerEntity caster in _aiScratch)
        {
            if (caster.IsDead)
            {
                caster.CancelCast();
                continue;
            }

            if (Tick < caster.CastEndTick)
            {
                continue; // still incanting
            }

            AbilityDefinition ability = _gameData.GetAbility(caster.CastAbilityId);
            int targetId = caster.CastTargetId;
            caster.CancelCast();

            if (!_entities.TryGetValue(targetId, out ServerEntity? target) || target.IsDead ||
                (ability.ResourceCost > 0 && !caster.HasResource(ability.ResourceCost)))
            {
                continue; // target gone or resource drained: the cast fizzles
            }

            // Range check with a small grace (the target may have stepped back mid-cast).
            float slack = ability.Range + 2f;
            if (Vec2.DistanceSquared(caster.Position, target.Position) > slack * slack)
            {
                continue;
            }

            // Sanctuary still protects.
            if ((caster.Kind == EntityKind.Player && IsSafePosition(caster.Position)) ||
                (target.Kind == EntityKind.Player && IsSafePosition(target.Position)))
            {
                continue;
            }

            ExecuteAbility(caster, target, ability);
        }
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
            case EffectType.Regen:
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
        ProcessDespawns();
        ProcessCasts();
        ProcessAutoAttacks();
        RunMonsterAi();
        IntegrateMovement(dt);
    }

    /// <summary>Remove timed entities (monster corpses) whose despawn tick has passed.</summary>
    private void ProcessDespawns()
    {
        _respawnScratch.Clear();
        foreach (ServerEntity e in _entities.Values)
        {
            if (e.DespawnAtTick != 0 && Tick >= e.DespawnAtTick)
            {
                _respawnScratch.Add(e.Id);
            }
        }

        foreach (int id in _respawnScratch)
        {
            _entities.Remove(id);
            _grid.Remove(id);
        }

        _respawnScratch.Clear();
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
                // Appearance: for monsters (and their remains) the "race" slot carries the monster
                // definition id so the client picks the right model; players send race/class/gender.
                byte raceOrDef = e.Kind == EntityKind.Monster || e.Kind == EntityKind.MonsterCorpse
                    ? e.MonsterId
                    : e.RaceId;
                byte flags = e.IsJumpingAt(Tick) ? (byte)1 : (byte)0;
                result.Add(new EntitySnapshot(
                    e.Id, e.Kind, e.Faction, e.Position,
                    e.Health, e.EffectiveMaxHealth, (int)e.CurrentResource, e.EffectiveMaxResource,
                    e.FacingRadians, (byte)System.Math.Clamp(e.Level, 1, 255), e.Name,
                    raceOrDef, e.ClassId, e.Gender, e.Appearance, flags,
                    e.CastAbilityId, e.CastProgressAt(Tick),
                    e.Kind == EntityKind.Player ? e.CopyEquipment() : null));
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

        float leashSq = SimulationConstants.MonsterLeashRadius * SimulationConstants.MonsterLeashRadius;

        foreach (ServerEntity monster in _aiScratch)
        {
            if (monster.IsDead)
            {
                continue;
            }

            // LEASH: dragged too far from home → drop aggro and run back (classic evade).
            if (monster.AiTargetId is not null &&
                Vec2.DistanceSquared(monster.Position, monster.SpawnPosition) > leashSq)
            {
                monster.AiTargetId = null;
                monster.IsEvading = true;
            }

            ServerEntity? target = monster.IsEvading ? null : AcquireOrKeepTarget(monster);
            if (target is null)
            {
                // No prey: walk back to the spawn point if away from it, then stand fresh.
                Vec2 home = monster.SpawnPosition - monster.Position;
                if (home.LengthSquared > 2f)
                {
                    monster.IsEvading = true;
                    monster.MoveIntent = home.Normalized();
                    monster.FacingRadians = MathF.Atan2(monster.MoveIntent.Y, monster.MoveIntent.X);
                }
                else
                {
                    if (monster.IsEvading)
                    {
                        monster.IsEvading = false;
                        monster.RestoreToFull(); // back home: wounds shrugged off
                    }

                    monster.MoveIntent = Vec2.Zero;
                }

                continue;
            }

            // Face the prey — chasing or striking, the model looks where it acts.
            Vec2 toTarget = target.Position - monster.Position;
            if (toTarget.LengthSquared > 0.0001f)
            {
                monster.FacingRadians = MathF.Atan2(toTarget.Y, toTarget.X);
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
                monster.MoveIntent = toTarget.Normalized(); // Chase.
            }
        }
    }

    private ServerEntity? AcquireOrKeepTarget(ServerEntity monster)
    {
        // Keep the current target if it is still valid, in aggro range, and not in sanctuary.
        if (monster.AiTargetId is int currentId &&
            _entities.TryGetValue(currentId, out ServerEntity? current) &&
            current.IsAlive &&
            !IsSafePosition(current.Position) &&
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
                candidate.Kind != EntityKind.Player || candidate.IsDead ||
                IsSafePosition(candidate.Position)) // sanctuary: monsters never aggro
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

    /// <summary>Static blocking circles (trees, stones, fences…). Empty in instances.</summary>
    public IReadOnlyList<WorldLayout.Obstacle> Obstacles { get; set; } = [];

    /// <summary>How wide a walking body is for collision purposes.</summary>
    private const float BodyRadius = 0.45f;

    private void IntegrateMovement(float dt)
    {
        foreach (ServerEntity entity in _entities.Values)
        {
            if (entity.IsDead || entity.MoveIntent.LengthSquared <= 0f)
            {
                continue;
            }

            Vec2 next = entity.Position + (entity.MoveIntent * (entity.EffectiveMoveSpeed * dt));
            next = ResolveObstacles(next, airborne: entity.IsJumpingAt(Tick));
            entity.Position = next;
            _grid.InsertOrUpdate(entity.Id, entity.Position);
        }
    }

    /// <summary>
    /// Push a position out of any blocking circle it overlaps. Pushing out (rather than refusing
    /// the move) makes bodies SLIDE along trees and walls instead of sticking to them. An AIRBORNE
    /// body (mid-jump) clears anything lower than the jump — fences, small rocks, the bank chest.
    /// </summary>
    private Vec2 ResolveObstacles(Vec2 position, bool airborne = false)
    {
        IReadOnlyList<WorldLayout.Obstacle> obstacles = Obstacles;
        for (int pass = 0; pass < 2; pass++) // two passes settle corner overlaps
        {
            bool touched = false;
            for (int i = 0; i < obstacles.Count; i++)
            {
                WorldLayout.Obstacle o = obstacles[i];
                if (airborne && o.JumpableOver)
                {
                    continue; // sailing over it
                }

                float minDist = o.Radius + BodyRadius;
                Vec2 delta = position - o.Position;
                float distSq = delta.LengthSquared;
                if (distSq >= minDist * minDist)
                {
                    continue;
                }

                // Standing dead-centre on an obstacle: pick an arbitrary push direction.
                Vec2 push = distSq > 0.0001f ? delta.Normalized() : new Vec2(1f, 0f);
                position = o.Position + (push * minDist);
                touched = true;
            }

            if (!touched)
            {
                break;
            }
        }

        return position;
    }

    private void DealDamage(ServerEntity attacker, ServerEntity target, AbilityDefinition ability)
    {
        // Weapon/spell proficiency: a player's skill in this ability's line scales its damage up.
        bool trainsSkill = attacker.Kind == EntityKind.Player && ability.SkillLineId != 0;
        float skillMult = trainsSkill
            ? _gameData.Progression.SkillDamageMultiplier(attacker.GetSkill(ability.SkillLineId))
            : 1f;

        int raw = (int)MathF.Round((ability.BaseDamage + attacker.EffectiveAttackPower) * skillMult);
        int damage = System.Math.Max(1, raw - target.EffectiveDefense);

        // FRIENDLY duel: the killing blow is pulled — the loser survives at 1 hp and the duel ends.
        if (_duels.TryGetValue(attacker.Id, out (int opponentId, bool toDeath) duel) &&
            duel.opponentId == target.Id && !duel.toDeath && damage >= target.Health)
        {
            damage = System.Math.Max(0, target.Health - 1);
            if (damage > 0)
            {
                target.TakeDamage(damage, Tick);
            }

            _combatEvents.Add(new CombatEventMessage(
                attacker.Id, target.Id, ability.Id, damage, target.Health, false));
            EndDuel(attacker.Id, winnerId: attacker.Id);
            return;
        }

        target.TakeDamage(damage, Tick);
        attacker.OnDealtDamage(Tick); // rage generation + combat timestamp (Warriors)
        target.OnTookDamage(Tick);

        // Using the ability trains its skill line, so the style grows stronger the more it's used.
        if (trainsSkill)
        {
            attacker.AddSkill(ability.SkillLineId, _gameData.Progression.SkillGainPerUse, _gameData.Progression.MaxSkill);
        }

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
        // A duel TO THE DEATH ends the hardcore way: the loser's death is entirely real.
        if (victim.Kind == EntityKind.Player && _duels.ContainsKey(victim.Id))
        {
            EndDuel(victim.Id, winnerId: killer.Id);
        }

        if (victim.Kind == EntityKind.Player)
        {
            // Full loot: the player's carried inventory, equipped gear, and gold drop as a lootable
            // corpse anyone can plunder.
            SpawnCorpse(victim);

            // Hardcore permadeath: the character resets to a fresh level-1 with a new starter kit.
            // Their account bank (held by the GameServer, per account) is untouched — deposit before
            // you die and you keep it. The respawn then brings the reborn character back.
            victim.ResetForPermadeath();
            GrantStarterKit(victim);
        }
        else if (victim.IsMonster && killer.Kind == EntityKind.Player)
        {
            // XP scales with the level difference: fighting up pays more, farming greys pays little.
            MonsterDefinition def = _gameData.GetMonster(victim.MonsterId);
            float mult = _gameData.Progression.XpMultiplierForKill(killer.Level, def.Level);
            ApplyXp(killer, (int)MathF.Round(def.XpReward * mult));

            // Quest progress: the kill counts when it matches the active quest's target.
            if (killer.ActiveQuestId != 0)
            {
                QuestDefinition? quest = _gameData.GetQuest(killer.ActiveQuestId);
                if (quest != null && quest.TargetMonsterId == victim.MonsterId &&
                    killer.QuestKills < quest.RequiredKills)
                {
                    killer.QuestKills++;
                    _questDirty.Add(killer.Id);
                }
            }

            // NOTHING is auto-looted: gold, body parts and gear all wait inside the corpse
            // on the ground — right-click it (or press Interact) to open its loot window.
            SpawnMonsterCorpse(victim, def);
        }
    }

    /// <summary>
    /// Leave the slain creature's remains on the ground as a LOOTABLE corpse holding its gold,
    /// guaranteed body parts and rolled gear. Despawns after ~90 seconds or once emptied.
    /// </summary>
    private void SpawnMonsterCorpse(ServerEntity victim, MonsterDefinition def)
    {
        int id = _ids.Next();
        var remains = new ServerEntity(id, EntityKind.MonsterCorpse, victim.Position,
            new StatBlock(1, 0f, 0, 0, 0f), 0)
        {
            Name = victim.Name,
            Faction = Faction.Neutral,
            MonsterId = victim.MonsterId,
            Level = victim.Level,
            LootContainer = BuildMonsterLoot(def),
            DespawnAtTick = Tick + (uint)(SimulationConstants.TickRate * 90),
        };

        _entities[id] = remains;
        _grid.InsertOrUpdate(id, remains.Position);
    }

    /// <summary>RNG for gear drops. Tests inject a seeded instance for determinism.</summary>
    public Random LootRng { get; set; } = new();

    /// <summary>
    /// Kill loot, WoW-style windowed: the monster's gold, its GUARANTEED body parts (no RNG —
    /// "bring me 10 goblin heads" is deterministic), and one chance roll per listed gear piece.
    /// </summary>
    private Inventory BuildMonsterLoot(MonsterDefinition def)
    {
        var loot = new Inventory(SimulationConstants.CorpseLootCapacity);
        loot.AddGold(def.GoldReward);

        foreach (LootEntry part in def.BodyParts)
        {
            if (!_gameData.HasItem(part.ItemId) || part.Quantity <= 0)
            {
                continue;
            }

            ItemDefinition item = _gameData.GetItem(part.ItemId);
            loot.TryAdd(part.ItemId, part.Quantity, item.Stackable, item.MaxStack);
        }

        // Equipment: one roll per listed piece. This is where the loot thrill lives.
        foreach (GearDrop drop in def.GearDrops)
        {
            if (!_gameData.HasItem(drop.ItemId) || LootRng.Next(100) >= drop.ChancePercent)
            {
                continue;
            }

            ItemDefinition item = _gameData.GetItem(drop.ItemId);
            loot.TryAdd(drop.ItemId, 1, item.Stackable, item.MaxStack);
        }

        return loot;
    }

    private void SpawnCorpse(ServerEntity dead)
    {
        var loot = new Inventory(SimulationConstants.CorpseLootCapacity);
        loot.AddGold(dead.Inventory.TakeAllGold());

        foreach (ItemStack stack in dead.Inventory.Stacks.ToArray())
        {
            if (Inventory.IsEmptyCell(stack)) { continue; } // layout holes carry nothing

            ItemDefinition def = _gameData.GetItem(stack.ItemId);
            loot.TryAdd(stack.ItemId, stack.Quantity, def.Stackable, def.MaxStack);
        }

        dead.Inventory.Clear();

        for (int i = 0; i < EquipSlots.Count; i++)
        {
            MoveEquippedToLoot(loot, dead.Equipment[i]);
            dead.SetEquipped((EquipSlot)i, 0);
        }

        RecomputeEquipment(dead); // the respawned body has no gear until it loots/replaces it

        int id = _ids.Next();
        var corpse = new ServerEntity(id, EntityKind.Corpse, dead.Position, new StatBlock(1, 0f, 0, 0, 0f), 0)
        {
            Name = $"{dead.Name}'s Corpse",
            Faction = dead.Faction,
            LootContainer = loot,
            RaceId = dead.RaceId,
            Gender = dead.Gender,
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

    // ------------------------------------------------------------------ Duels

    /// <summary>True if these two entities are currently dueling each other.</summary>
    public bool IsDuelPair(int a, int b)
        => _duels.TryGetValue(a, out (int opponentId, bool toDeath) duel) && duel.opponentId == b;

    /// <summary>True if this entity is in any duel.</summary>
    public bool IsDueling(int entityId) => _duels.ContainsKey(entityId);

    /// <summary>Begin a duel between two living players (both must be duel-free).</summary>
    public bool StartDuel(int a, int b, bool toDeath)
    {
        if (a == b || _duels.ContainsKey(a) || _duels.ContainsKey(b) ||
            !_entities.TryGetValue(a, out ServerEntity? ea) || ea.IsDead || ea.Kind != EntityKind.Player ||
            !_entities.TryGetValue(b, out ServerEntity? eb) || eb.IsDead || eb.Kind != EntityKind.Player)
        {
            return false;
        }

        _duels[a] = (b, toDeath);
        _duels[b] = (a, toDeath);
        return true;
    }

    /// <summary>End the duel this entity is part of, recording the winner (0 = no winner, e.g. forfeit).</summary>
    public void EndDuel(int entityId, int winnerId)
    {
        if (!_duels.Remove(entityId, out (int opponentId, bool toDeath) duel))
        {
            return;
        }

        _duels.Remove(duel.opponentId);
        int loserId = winnerId == entityId ? duel.opponentId : entityId;
        if (winnerId != 0)
        {
            _duelEndings.Add((winnerId, loserId, duel.toDeath));
        }
    }

    /// <summary>End this entity's duel as a forfeit: the opponent wins (e.g. on disconnect).</summary>
    public void ForfeitDuel(int entityId)
    {
        if (_duels.TryGetValue(entityId, out (int opponentId, bool toDeath) duel))
        {
            EndDuel(entityId, winnerId: duel.opponentId);
        }
    }

    /// <summary>Return and clear the duel results recorded since the last drain.</summary>
    public IReadOnlyList<(int winnerId, int loserId, bool toDeath)> DrainDuelEndings()
    {
        if (_duelEndings.Count == 0)
        {
            return [];
        }

        var copy = _duelEndings.ToArray();
        _duelEndings.Clear();
        return copy;
    }

    // ------------------------------------------------------------ Ground drops

    /// <summary>
    /// Drop a quantity of an item from a player's bag onto the ground. It becomes a lootable sack
    /// (a corpse-kind container) at their feet, reusing all the corpse-loot plumbing — anyone can
    /// pick it up, and it vanishes once emptied. Returns true if something was dropped.
    /// </summary>
    public bool TryDropItem(int playerId, byte itemId, int quantity)
    {
        if (!_entities.TryGetValue(playerId, out ServerEntity? player) || player.IsDead ||
            player.Kind != EntityKind.Player || quantity <= 0)
        {
            return false;
        }

        int removed = player.Inventory.RemoveQuantity(itemId, quantity);
        if (removed <= 0)
        {
            return false;
        }

        ItemDefinition def = _gameData.GetItem(itemId);
        var loot = new Inventory(SimulationConstants.CorpseLootCapacity);
        loot.TryAdd(itemId, removed, def.Stackable, def.MaxStack);

        int id = _ids.Next();
        var sack = new ServerEntity(id, EntityKind.Corpse, player.Position, new StatBlock(1, 0f, 0, 0, 0f), 0)
        {
            Name = "Sac de " + player.Name,
            Faction = player.Faction,
            LootContainer = loot,
        };

        _entities[id] = sack;
        _grid.InsertOrUpdate(id, sack.Position);
        return true;
    }

    /// <summary>Common validation for all corpse interactions: living player, real corpse, in range.</summary>
    private bool ValidateLooter(int looterId, int corpseId, out ServerEntity looter, out ServerEntity corpse)
    {
        looter = null!;
        corpse = null!;

        if (!_entities.TryGetValue(looterId, out ServerEntity? l) || l.IsDead ||
            l.Kind != EntityKind.Player ||
            !_entities.TryGetValue(corpseId, out ServerEntity? c) ||
            (c.Kind != EntityKind.Corpse && c.Kind != EntityKind.MonsterCorpse) ||
            c.LootContainer is null)
        {
            return false;
        }

        if (Vec2.DistanceSquared(l.Position, c.Position)
            > SimulationConstants.LootRange * SimulationConstants.LootRange)
        {
            return false; // too far away
        }

        looter = l;
        corpse = c;
        return true;
    }

    /// <summary>
    /// Inspect a corpse's contents (for the loot window). Range-validated; returns false if the
    /// corpse cannot be opened by this looter.
    /// </summary>
    public bool TryOpenCorpse(int looterId, int corpseId, out int gold, out List<ItemStack> items)
    {
        gold = 0;
        items = new List<ItemStack>();

        if (!ValidateLooter(looterId, corpseId, out _, out ServerEntity corpse))
        {
            return false;
        }

        gold = corpse.LootContainer!.Gold;
        foreach (ItemStack stack in corpse.LootContainer.Stacks)
        {
            if (!Inventory.IsEmptyCell(stack)) { items.Add(stack); } // holes are not loot
        }

        return true;
    }

    /// <summary>
    /// Take ONE thing from a corpse: all of the given item id, or the gold when itemId is 0.
    /// The corpse despawns once fully emptied. Returns true if something was taken.
    /// </summary>
    public bool TryLootItem(int looterId, int corpseId, byte itemId)
    {
        if (!ValidateLooter(looterId, corpseId, out ServerEntity looter, out ServerEntity corpse))
        {
            return false;
        }

        Inventory loot = corpse.LootContainer!;
        bool took = false;

        if (itemId == 0)
        {
            int gold = loot.TakeAllGold();
            if (gold > 0)
            {
                looter.Inventory.AddGold(gold);
                took = true;
            }
        }
        else
        {
            int available = loot.CountOf(itemId);
            if (available > 0)
            {
                ItemDefinition def = _gameData.GetItem(itemId);
                int leftover = looter.Inventory.TryAdd(itemId, available, def.Stackable, def.MaxStack);
                int moved = available - leftover;
                if (moved > 0)
                {
                    loot.RemoveQuantity(itemId, moved);
                    took = true;
                }
            }
        }

        if (loot.IsEmpty)
        {
            OnLootEmptied(corpse);
        }

        return took;
    }

    /// <summary>
    /// A fully looted container: sacks and player corpses vanish, but a monster's body STAYS on
    /// the ground (WoW-style) until its own despawn timer — there is just nothing left to take.
    /// </summary>
    private void OnLootEmptied(ServerEntity corpse)
    {
        if (corpse.Kind == EntityKind.MonsterCorpse)
        {
            corpse.LootContainer = null;
        }
        else
        {
            Despawn(corpse.Id);
        }
    }

    /// <summary>
    /// Loot everything from a corpse into the looter's inventory (gold + as many items as fit).
    /// Validates the looter is a living player within range. The corpse despawns once emptied.
    /// Returns true if anything was taken.
    /// </summary>
    public bool TryLootCorpse(int looterId, int corpseId)
    {
        if (!ValidateLooter(looterId, corpseId, out ServerEntity looter, out ServerEntity corpse))
        {
            return false;
        }

        Inventory loot = corpse.LootContainer!;
        bool tookSomething = false;

        int gold = loot.TakeAllGold();
        if (gold > 0)
        {
            looter.Inventory.AddGold(gold);
            tookSomething = true;
        }

        foreach (ItemStack stack in loot.Stacks.ToArray())
        {
            if (Inventory.IsEmptyCell(stack)) { continue; }

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
            OnLootEmptied(corpse);
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
