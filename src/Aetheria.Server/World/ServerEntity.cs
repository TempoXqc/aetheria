using Aetheria.Shared.Math;
using Aetheria.Shared.Net;
using Aetheria.Shared.Protocol;

namespace Aetheria.Server.World;

/// <summary>
/// The server's authoritative record of one entity. Player-controlled entities carry an
/// <see cref="Owner"/> peer and the last input the server has accepted for them.
/// This is a plain mutable class for the skeleton; the natural evolution is a data-oriented
/// (ECS) layout once the entity count and per-tick work grow.
/// </summary>
public sealed class ServerEntity
{
    public ServerEntity(int id, EntityKind kind, Vec2 position)
    {
        Id = id;
        Kind = kind;
        Position = position;
    }

    public int Id { get; }
    public EntityKind Kind { get; }
    public Vec2 Position { get; set; }

    /// <summary>Current movement intent (unit-ish direction). Applied each tick by the simulation.</summary>
    public Vec2 MoveIntent { get; set; } = Vec2.Zero;

    /// <summary>Owning peer for player entities; null for server-controlled entities.</summary>
    public PeerId? Owner { get; set; }

    /// <summary>Highest input sequence accepted, so stale/reordered inputs can be dropped.</summary>
    public uint LastInputSequence { get; set; }
}
