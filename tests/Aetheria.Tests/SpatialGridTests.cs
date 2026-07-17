using Aetheria.Shared.Math;
using Aetheria.Shared.Spatial;

namespace Aetheria.Tests;

public static class SpatialGridTests
{
    [Test("Entities within the radius are returned; those outside are not.")]
    public static void QueryRadius_ReturnsOnlyEntitiesInRange()
    {
        var grid = new SpatialGrid(cellSize: 16f);
        grid.InsertOrUpdate(1, new Vec2(0, 0));
        grid.InsertOrUpdate(2, new Vec2(5, 0));    // within radius 10
        grid.InsertOrUpdate(3, new Vec2(100, 100)); // far away

        var results = new List<int>();
        grid.QueryRadius(new Vec2(0, 0), radius: 10f, results);

        Assert.Contains(1, results);
        Assert.Contains(2, results);
        Assert.DoesNotContain(3, results);
    }

    [Test("The query point's own entity is included (distance 0).")]
    public static void QueryRadius_IncludesEntityAtCenter()
    {
        var grid = new SpatialGrid(16f);
        grid.InsertOrUpdate(42, new Vec2(3, 3));

        var results = new List<int>();
        grid.QueryRadius(new Vec2(3, 3), 1f, results);

        Assert.Contains(42, results);
    }

    [Test("Updating a position rebuckets the entity so stale cells are not consulted.")]
    public static void InsertOrUpdate_MovingEntity_Rebuckets()
    {
        var grid = new SpatialGrid(16f);
        grid.InsertOrUpdate(1, new Vec2(0, 0));

        // Move far outside the query radius.
        grid.InsertOrUpdate(1, new Vec2(500, 500));

        var results = new List<int>();
        grid.QueryRadius(new Vec2(0, 0), 20f, results);

        Assert.DoesNotContain(1, results);
        Assert.Equal(1, grid.Count); // still one entity, just relocated
    }

    [Test("Removed entities disappear from queries and the count.")]
    public static void Remove_DropsEntity()
    {
        var grid = new SpatialGrid(16f);
        grid.InsertOrUpdate(7, new Vec2(1, 1));
        grid.Remove(7);

        var results = new List<int>();
        grid.QueryRadius(new Vec2(1, 1), 5f, results);

        Assert.DoesNotContain(7, results);
        Assert.Equal(0, grid.Count);
    }

    [Test("Works across cell boundaries and with negative coordinates.")]
    public static void QueryRadius_SpansCellBoundariesAndNegatives()
    {
        var grid = new SpatialGrid(16f);
        grid.InsertOrUpdate(1, new Vec2(-1, -1));
        grid.InsertOrUpdate(2, new Vec2(1, 1));   // different cell, but close in space

        var results = new List<int>();
        grid.QueryRadius(new Vec2(0, 0), 5f, results);

        Assert.Contains(1, results);
        Assert.Contains(2, results);
    }

    [Test("A non-positive cell size is rejected.")]
    public static void Constructor_RejectsNonPositiveCellSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _ = new SpatialGrid(0f));
    }
}
