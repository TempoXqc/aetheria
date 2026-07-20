using Aetheria.Shared.Math;

namespace Aetheria.Shared.Data;

/// <summary>
/// The open world's STATIC layout, shared by client and server so what you SEE is what BLOCKS you:
/// the server resolves movement collisions against these circles, and the Unity client plants its
/// scenery (trees, stones, fences…) at exactly the same spots. One source of truth, zero drift.
/// </summary>
public static class WorldLayout
{
    /// <summary>A static blocking circle on the ground plane, with a HEIGHT: anything lower than
    /// a jump's clearance (fences, small rocks, the bank chest) can be hopped over mid-air.</summary>
    public readonly struct Obstacle
    {
        /// <summary>Height meaning "you can never jump over this" (trees, menhirs, walls).</summary>
        public const float Unjumpable = 99f;

        public readonly float X;
        public readonly float Y;
        public readonly float Radius;
        public readonly float Height;

        public Obstacle(float x, float y, float radius, float height = Unjumpable)
        {
            X = x;
            Y = y;
            Radius = radius;
            Height = height;
        }

        public Vec2 Position => new(X, Y);

        /// <summary>True if an airborne body clears this obstacle.</summary>
        public bool JumpableOver => Height <= SimulationConstants.JumpClearance;
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

    /// <summary>Rocks near the contested dungeon camp. The small ones can be jumped over.</summary>
    public static readonly Obstacle[] Rocks =
    [
        new(35f, 34f, 1.0f, height: 1.05f), // medium: just too tall to hop
        new(44f, 37f, 0.7f, height: 0.75f), // small: jumpable
        new(37f, 43f, 1.3f, height: 1.35f), // big: no
    ];

    /// <summary>The sanctuary's ring of standing stones (14 menhirs, radius 18; gaps are passable).</summary>
    public static readonly Obstacle[] Menhirs = BuildMenhirs();

    /// <summary>The bank canopy's four posts — and the chest, low enough to vault over.</summary>
    public static readonly Obstacle[] BankPosts =
    [
        new(SimulationConstants.BankChestX - 1.8f, SimulationConstants.BankChestY - 1.5f, 0.25f),
        new(SimulationConstants.BankChestX + 1.8f, SimulationConstants.BankChestY - 1.5f, 0.25f),
        new(SimulationConstants.BankChestX - 1.8f, SimulationConstants.BankChestY + 1.5f, 0.25f),
        new(SimulationConstants.BankChestX + 1.8f, SimulationConstants.BankChestY + 1.5f, 0.25f),
        new(SimulationConstants.BankChestX, SimulationConstants.BankChestY, 0.7f, height: 0.85f),
    ];

    /// <summary>Wolf-field fence posts (walkable gaps between posts are intentional).</summary>
    public static readonly Obstacle[] FencePosts = BuildFences();

    /// <summary>Everything that blocks movement, concatenated. The server iterates this.</summary>
    /// <summary>
    /// The starting town's building shells (Marla's inn and Mira's market house): solid cores
    /// so nobody strolls through a stone wall. The bank pavilion and the forge stay open.
    /// </summary>
    public static readonly Obstacle[] TownHouses =
    [
        new Obstacle(8.2f, -7.2f, 2.4f),
        new Obstacle(-7.8f, 8.6f, 2.4f),
    ];

    public static readonly Obstacle[] All = Concat(Trees, Rocks, Menhirs, BankPosts, FencePosts, TownHouses);

    /// <summary>
    /// LINE OF SIGHT between two points in the OPEN WORLD: false when a TALL obstacle (tree,
    /// menhir — anything unjumpable) crosses the segment. Fences and low rocks never block a
    /// spell. Shared, so the client can predict exactly what the server will refuse.
    /// </summary>
    public static bool HasLineOfSight(Vec2 from, Vec2 to)
    {
        float dx = to.X - from.X;
        float dy = to.Y - from.Y;
        float lenSq = (dx * dx) + (dy * dy);

        foreach (Obstacle o in All)
        {
            if (o.JumpableOver)
            {
                continue; // low things don't block sight
            }

            // Closest point of the segment to the obstacle's centre.
            float t = lenSq < 0.0001f ? 0f
                : (((o.X - from.X) * dx) + ((o.Y - from.Y) * dy)) / lenSq;
            t = t < 0f ? 0f : (t > 1f ? 1f : t);
            float cx = from.X + (t * dx) - o.X;
            float cy = from.Y + (t * dy) - o.Y;

            // A slightly slimmer radius: brushing a trunk's edge doesn't eat your spell.
            float r = o.Radius * 0.8f;
            if ((cx * cx) + (cy * cy) < r * r &&
                t > 0.02f && t < 0.98f) // standing against the trunk still sees past it
            {
                return false;
            }
        }

        return true;
    }

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
            posts.Add(new Obstacle(p.X, p.Y, 0.3f, height: 0.9f)); // fences are made to be hopped
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
