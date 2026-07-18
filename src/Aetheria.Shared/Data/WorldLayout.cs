using Aetheria.Shared.Math;

namespace Aetheria.Shared.Data;

/// <summary>
/// The open world's STATIC layout, shared by client and server so what you SEE is what BLOCKS you:
/// the server resolves movement collisions against these circles, and the Unity client plants its
/// scenery (trees, stones, fences…) at exactly the same spots. One source of truth, zero drift.
/// </summary>
public static class WorldLayout
{
    /// <summary>A static blocking circle on the ground plane.</summary>
    public readonly struct Obstacle
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Radius;

        public Obstacle(float x, float y, float radius)
        {
            X = x;
            Y = y;
            Radius = radius;
        }

        public Vec2 Position => new(X, Y);
    }

    /// <summary>Trees of the open world (trunk position, kept out of walking paths).</summary>
    public static readonly Obstacle[] Trees =
    [
        new(-20f, 22f, 0.55f),
        new(-28f, -14f, 0.6f),
        new(14f, -20f, 0.5f),
        new(30f, 24f, 0.6f),
        new(-8f, 26f, 0.45f),
        new(20f, -8f, 0.55f),
    ];

    /// <summary>Rocks near the contested dungeon camp.</summary>
    public static readonly Obstacle[] Rocks =
    [
        new(35f, 34f, 1.0f),
        new(44f, 37f, 0.7f),
        new(37f, 43f, 1.3f),
    ];

    /// <summary>The sanctuary's ring of standing stones (14 menhirs, radius 18; gaps are passable).</summary>
    public static readonly Obstacle[] Menhirs = BuildMenhirs();

    /// <summary>The bank canopy's four posts.</summary>
    public static readonly Obstacle[] BankPosts =
    [
        new(SimulationConstants.BankChestX - 1.8f, SimulationConstants.BankChestY - 1.5f, 0.25f),
        new(SimulationConstants.BankChestX + 1.8f, SimulationConstants.BankChestY - 1.5f, 0.25f),
        new(SimulationConstants.BankChestX - 1.8f, SimulationConstants.BankChestY + 1.5f, 0.25f),
        new(SimulationConstants.BankChestX + 1.8f, SimulationConstants.BankChestY + 1.5f, 0.25f),
        new(SimulationConstants.BankChestX, SimulationConstants.BankChestY, 0.7f), // the chest itself
    ];

    /// <summary>Wolf-field fence posts (walkable gaps between posts are intentional).</summary>
    public static readonly Obstacle[] FencePosts = BuildFences();

    /// <summary>Everything that blocks movement, concatenated. The server iterates this.</summary>
    public static readonly Obstacle[] All = Concat(Trees, Rocks, Menhirs, BankPosts, FencePosts);

    private static Obstacle[] BuildMenhirs()
    {
        const int Stones = 14;
        var result = new Obstacle[Stones];
        for (int i = 0; i < Stones; i++)
        {
            float a = i * MathF.PI * 2f / Stones;
            result[i] = new Obstacle(MathF.Cos(a) * 18f, MathF.Sin(a) * 18f, 0.55f);
        }

        return result;
    }

    private static Obstacle[] BuildFences()
    {
        var posts = new List<Obstacle>();
        AddFenceLine(posts, new Vec2(-61f, -9f), new Vec2(-35f, -9f));
        AddFenceLine(posts, new Vec2(-61f, 15f), new Vec2(-35f, 15f));
        AddFenceLine(posts, new Vec2(-61f, -9f), new Vec2(-61f, 15f));
        return posts.ToArray();
    }

    private static void AddFenceLine(List<Obstacle> posts, Vec2 from, Vec2 to)
    {
        Vec2 delta = to - from;
        float length = MathF.Sqrt(delta.LengthSquared);
        int count = System.Math.Max(2, (int)MathF.Round(length / 3f) + 1);
        for (int i = 0; i < count; i++)
        {
            Vec2 p = from + (delta * (i / (float)(count - 1)));
            posts.Add(new Obstacle(p.X, p.Y, 0.3f));
        }
    }

    private static Obstacle[] Concat(params Obstacle[][] groups)
    {
        int total = 0;
        foreach (Obstacle[] g in groups)
        {
            total += g.Length;
        }

        var all = new Obstacle[total];
        int at = 0;
        foreach (Obstacle[] g in groups)
        {
            g.CopyTo(all, at);
            at += g.Length;
        }

        return all;
    }
}
