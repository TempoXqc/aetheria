using Aetheria.Shared;
using Aetheria.Shared.Combat;
using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Server.World;

/// <summary>
/// The server's authoritative record of one entity — a player or a monster. Carries its combat stats,
/// current health, ability cooldowns, and (for monsters) a small amount of AI state. Plain mutable
/// class for the skeleton; the natural evolution is a data-oriented (ECS) layout as counts grow.
/// </summary>
public sealed class ServerEntity
{
    private readonly Dictionary<byte, uint> _abilityReadyTick = new();

    public ServerEntity(int id, EntityKind kind, Vec2 position, StatBlock stats, byte basicAbilityId)
    {
        Id = id;
        Kind = kind;
        Position = position;
        SpawnPosition = position;
        Stats = stats;
        Health = stats.MaxHealth;
        BasicAbilityId = basicAbilityId;
    }

    public int Id { get; }
    public EntityKind Kind { get; }
    public string Name { get; set; } = string.Empty;

    public Vec2 Position { get; set; }

    /// <summary>Where this entity (re)spawns.</summary>
    public Vec2 SpawnPosition { get; set; }

    /// <summary>Current movement intent (unit-ish direction), applied each tick by the simulation.</summary>
    public Vec2 MoveIntent { get; set; } = Vec2.Zero;

    /// <summary>Owning peer for player entities; null for monsters.</summary>
    public PeerId? Owner { get; set; }

    /// <summary>Highest input sequence accepted, so stale/reordered inputs can be dropped.</summary>
    public uint LastInputSequence { get; set; }

    public StatBlock Stats { get; }
    public int Health { get; private set; }
    public byte BasicAbilityId { get; }

    /// <summary>Definition ids, for reference/telemetry. Zero when not applicable.</summary>
    public byte RaceId { get; set; }
    public byte ClassId { get; set; }
    public byte MonsterId { get; set; }

    public bool IsMonster => Kind == EntityKind.Monster;
    public bool IsDead { get; private set; }
    public bool IsAlive => !IsDead;

    /// <summary>Tick at which a dead entity should respawn.</summary>
    public uint RespawnAtTick { get; private set; }

    /// <summary>Monster AI: the entity currently being chased/attacked, if any.</summary>
    public int? AiTargetId { get; set; }

    /// <summary>True if the ability is off cooldown at the given tick.</summary>
    public bool IsAbilityReady(byte abilityId, uint tick)
        => !_abilityReadyTick.TryGetValue(abilityId, out uint readyAt) || tick >= readyAt;

    /// <summary>Start an ability's cooldown so it becomes usable again at <paramref name="readyTick"/>.</summary>
    public void StartCooldown(byte abilityId, uint readyTick) => _abilityReadyTick[abilityId] = readyTick;

    /// <summary>Apply damage (already floored to at least 1 by the caller). Marks death at 0 HP.</summary>
    public void TakeDamage(int amount, uint tick)
    {
        if (IsDead)
        {
            return;
        }

        Health -= amount;
        if (Health <= 0)
        {
            Health = 0;
            IsDead = true;
            MoveIntent = Vec2.Zero;
            AiTargetId = null;
            RespawnAtTick = tick + (uint)SimulationConstants.RespawnDelayTicks;
        }
    }

    /// <summary>Bring a dead entity back to full health at its spawn point.</summary>
    public void Respawn()
    {
        IsDead = false;
        Health = Stats.MaxHealth;
        Position = SpawnPosition;
        MoveIntent = Vec2.Zero;
        AiTargetId = null;
        _abilityReadyTick.Clear();
    }
}
