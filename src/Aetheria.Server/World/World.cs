using Aetheria.Shared;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;
using Aetheria.Shared.Spatial;

namespace Aetheria.Server.World;

/// <summary>
/// The authoritative game world: the single source of truth for every entity's state. Clients
/// send inputs; the world decides what actually happens. All mutation goes through here on the
/// simulation thread, which is what makes the server trustworthy for PvP.
/// </summary>
public sealed class World
{
    private readonly Dictionary<int, ServerEntity> _entities = new();
    private readonly SpatialGrid _grid = new(SimulationConstants.GridCellSize);
    private readonly List<int> _queryScratch = new(64);

    private int _nextEntityId = 1;
    private int _spawnCounter;

    /// <summary>The current simulation tick (increments once per fixed step).</summary>
    public uint Tick { get; private set; }

    /// <summary>All live entities, keyed by id.</summary>
    public IReadOnlyDictionary<int, ServerEntity> Entities => _entities;

    /// <summary>Create a player entity owned by <paramref name="owner"/> at a spawn position.</summary>
    public ServerEntity SpawnPlayer(PeerId owner)
    {
        int id = _nextEntityId++;
        var entity = new ServerEntity(id, EntityKind.Player, NextSpawnPosition())
        {
            Owner = owner,
        };

        _entities[id] = entity;
        _grid.InsertOrUpdate(id, entity.Position);
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
    /// Record a movement intent for an entity, ignoring inputs that arrive out of order
    /// (UDP can reorder). The direction is clamped to unit length so a client cannot move
    /// faster by sending an oversized vector.
    /// </summary>
    public void ApplyInput(int entityId, uint sequence, Vec2 moveDirection)
    {
        if (!_entities.TryGetValue(entityId, out ServerEntity? entity))
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

    /// <summary>Advance the simulation by one fixed step of <paramref name="dt"/> seconds.</summary>
    public void Step(float dt)
    {
        Tick++;

        foreach (ServerEntity entity in _entities.Values)
        {
            if (entity.MoveIntent.LengthSquared <= 0f)
            {
                continue;
            }

            Vec2 displacement = entity.MoveIntent * (SimulationConstants.PlayerMoveSpeed * dt);
            entity.Position += displacement;
            _grid.InsertOrUpdate(entity.Id, entity.Position);
        }
    }

    /// <summary>
    /// Build the set of entities visible to an observer at <paramref name="center"/> — i.e. the
    /// contents of one client's area of interest. Only these are serialized into that client's
    /// snapshot, so per-client bandwidth stays bounded no matter how large the world gets.
    /// </summary>
    public List<EntitySnapshot> BuildAreaSnapshot(Vec2 center)
    {
        _grid.QueryRadius(center, SimulationConstants.AreaOfInterestRadius, _queryScratch);

        var result = new List<EntitySnapshot>(_queryScratch.Count);
        foreach (int id in _queryScratch)
        {
            if (_entities.TryGetValue(id, out ServerEntity? entity))
            {
                result.Add(new EntitySnapshot(entity.Id, entity.Kind, entity.Position));
            }
        }

        return result;
    }

    private Vec2 NextSpawnPosition()
    {
        // Spread spawns along a short line near the origin so freshly connected players start
        // inside one another's area of interest — handy while testing. Real spawn logic
        // (towns, graveyards, instance entrances) replaces this later.
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
