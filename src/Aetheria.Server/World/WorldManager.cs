using Aetheria.Shared.Data;
using Aetheria.Shared.Math;

namespace Aetheria.Server.World;

/// <summary>
/// Owns every running world: the single shared OPEN world (seamless, where non-instanced dungeons and
/// world raid bosses live and PvP is possible) plus any number of private INSTANCE worlds created per
/// group from an <see cref="InstanceDefinition"/>. All worlds share one entity-id allocator so players
/// keep their id as they transfer. This is also the architectural seam for future server meshing:
/// a world per node instead of a world per instance is the same shape.
/// </summary>
public sealed class WorldManager
{
    private readonly GameData _gameData;
    private readonly EntityIdAllocator _ids = new();
    private readonly Dictionary<int, RunningInstance> _instances = new();
    private int _nextInstanceId = 1;

    public WorldManager(GameData? gameData = null)
    {
        _gameData = gameData ?? GameData.CreateDefault();
        OpenWorld = new World(_gameData, _ids)
        {
            HasSafeZone = true,              // spawn sanctuary
            Obstacles = WorldLayout.All,     // trees/stones/fences block movement (and match visuals)
        };
    }

    /// <summary>The shared, seamless open world.</summary>
    public World OpenWorld { get; }

    public GameData GameData => _gameData;

    /// <summary>Every running world: the open world plus all live instances.</summary>
    public IEnumerable<World> AllWorlds
    {
        get
        {
            yield return OpenWorld;
            foreach (RunningInstance instance in _instances.Values)
            {
                yield return instance.World;
            }
        }
    }

    /// <summary>Can a group of <paramref name="groupSize"/> players enter this instance template?</summary>
    public static bool CanEnter(InstanceDefinition def, int groupSize, out string reason)
    {
        reason = string.Empty;

        if (groupSize < def.MinPlayers)
        {
            reason = def.IsRaid
                ? $"{def.Name} est un raid : il faut un groupe d'au moins {def.MinPlayers} joueurs."
                : $"{def.Name} demande un groupe d'au moins {def.MinPlayers} joueurs.";
            return false;
        }

        if (groupSize > def.MaxPlayers)
        {
            reason = $"{def.Name} est limité à {def.MaxPlayers} joueurs.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Create a private copy of an instance template, its monsters scaled to the group size:
    /// mult = 1 + scalingPerExtraPlayer * (groupSize - 1).
    /// </summary>
    public World CreateInstance(InstanceDefinition def, int groupSize)
    {
        float hpMult = 1f + (def.HealthScalingPerExtraPlayer * (groupSize - 1));
        float dmgMult = 1f + (def.DamageScalingPerExtraPlayer * (groupSize - 1));

        var world = new World(_gameData, _ids);

        // Same clock as the open world: cooldown/GCD stamps ride along with transferring
        // players, and a tick-0 instance would refuse their every ability for hours.
        world.SyncClock(OpenWorld.Tick);

        // Dungeon packs come back SLOWLY (3 min), not on the open world's 5-second treadmill.
        world.MonsterRespawnDelayTicks = Aetheria.Shared.SimulationConstants.InstanceRespawnDelayTicks;

        foreach (InstanceSpawn spawn in def.Spawns)
        {
            world.SpawnMonster(spawn.MonsterId, new Vec2(spawn.X, spawn.Y), hpMult, dmgMult);
        }

        // The way OUT: an exit portal behind the entrance — walk into it to leave (no key).
        world.SpawnNpc("Portail de sortie", new Vec2(-4f, -4f), npcType: 5);

        int id = _nextInstanceId++;
        _instances[id] = new RunningInstance(id, def, world);
        return world;
    }

    /// <summary>Move a player entity from one world to another (id preserved).</summary>
    public static bool TransferPlayer(World from, World to, int entityId, Vec2 targetPosition)
    {
        ServerEntity? entity = from.RemoveForTransfer(entityId);
        if (entity is null)
        {
            return false;
        }

        to.AdoptEntity(entity, targetPosition);
        return true;
    }

    /// <summary>Tear down an instance world once no players remain inside it.</summary>
    public void DestroyInstanceIfEmpty(World world)
    {
        if (ReferenceEquals(world, OpenWorld))
        {
            return;
        }

        foreach ((int id, RunningInstance instance) in _instances)
        {
            if (!ReferenceEquals(instance.World, world))
            {
                continue;
            }

            bool hasPlayers = false;
            foreach (ServerEntity e in world.Entities.Values)
            {
                if (e.Kind == Aetheria.Shared.Protocol.EntityKind.Player)
                {
                    hasPlayers = true;
                    break;
                }
            }

            if (!hasPlayers)
            {
                _instances.Remove(id);
            }

            return;
        }
    }

    /// <summary>Number of live instance worlds (for diagnostics/tests).</summary>
    public int InstanceCount => _instances.Count;

    private sealed record RunningInstance(int Id, InstanceDefinition Definition, World World);
}
