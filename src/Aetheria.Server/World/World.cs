using Aetheria.Shared;
using Aetheria.Shared.Combat;
using Aetheria.Shared.Data;
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
    public ServerEntity SpawnPlayer(PeerId owner, string name = "", byte raceId = 1, byte classId = 1)
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
        };

        AddAlive(entity);
        return entity;
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
            attackerId == targetId)
        {
            return false;
        }

        AbilityDefinition ability = _gameData.GetAbility(abilityId);

        if (!attacker.IsAbilityReady(ability.Id, Tick))
        {
            return false;
        }

        if (Vec2.DistanceSquared(attacker.Position, target.Position) > ability.Range * ability.Range)
        {
            return false;
        }

        attacker.StartCooldown(ability.Id, Tick + (uint)ability.CooldownTicks);
        DealDamage(attacker, target, ability);
        return true;
    }

    /// <summary>Advance the simulation by one fixed step: respawns, monster AI, then movement.</summary>
    public void Step(float dt)
    {
        Tick++;

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
                result.Add(new EntitySnapshot(e.Id, e.Kind, e.Position, e.Health, e.Stats.MaxHealth));
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
        foreach (ServerEntity monster in _entities.Values)
        {
            if (!monster.IsMonster || monster.IsDead)
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

            entity.Position += entity.MoveIntent * (entity.Stats.MoveSpeed * dt);
            _grid.InsertOrUpdate(entity.Id, entity.Position);
        }
    }

    private void DealDamage(ServerEntity attacker, ServerEntity target, AbilityDefinition ability)
    {
        int raw = ability.BaseDamage + attacker.Stats.AttackPower;
        int damage = System.Math.Max(1, raw - target.Stats.Defense);

        target.TakeDamage(damage, Tick);

        if (target.IsDead)
        {
            _grid.Remove(target.Id); // Remove the corpse from the visible/targetable world.
        }

        _combatEvents.Add(new CombatEventMessage(
            attacker.Id, target.Id, ability.Id, damage, target.Health, target.IsDead));
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
