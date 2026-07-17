using Aetheria.Shared.Math;

namespace Aetheria.Shared.Spatial;

/// <summary>
/// A uniform spatial hash grid used for interest management (area-of-interest queries).
///
/// This is the mechanism that lets a single seamless world scale: the server simulates
/// every entity, but each client is only sent the entities near it. Instead of scanning
/// all N entities for every observer (O(N²)), we bucket entities into fixed-size cells and
/// answer "who is within radius R of point P?" by touching only the handful of cells that
/// overlap that circle.
///
/// The same structure is the natural seam for future server meshing: assigning disjoint
/// ranges of cells to different server nodes turns "query nearby cells" into "query nearby
/// cells, some of which live on a neighbouring node".
/// </summary>
public sealed class SpatialGrid
{
    private readonly float _cellSize;

    // cellKey -> set of entity ids currently in that cell.
    private readonly Dictionary<long, HashSet<int>> _cells = new();

    // entity id -> its current cell key (so we can move/remove in O(1)).
    private readonly Dictionary<int, long> _entityCell = new();

    // entity id -> last known position (lets radius queries do an exact distance refine).
    private readonly Dictionary<int, Vec2> _entityPos = new();

    public SpatialGrid(float cellSize)
    {
        if (cellSize <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), "Cell size must be positive.");
        }

        _cellSize = cellSize;
    }

    /// <summary>Number of entities currently tracked.</summary>
    public int Count => _entityCell.Count;

    /// <summary>Insert a new entity or update an existing one's position, rebucketing if it changed cell.</summary>
    public void InsertOrUpdate(int id, Vec2 position)
    {
        long newKey = CellKey(position);
        _entityPos[id] = position;

        if (_entityCell.TryGetValue(id, out long oldKey))
        {
            if (oldKey == newKey)
            {
                return; // Same cell — nothing to rebucket.
            }

            RemoveFromCell(oldKey, id);
        }

        AddToCell(newKey, id);
        _entityCell[id] = newKey;
    }

    /// <summary>Remove an entity from the grid entirely.</summary>
    public void Remove(int id)
    {
        if (_entityCell.TryGetValue(id, out long key))
        {
            RemoveFromCell(key, id);
            _entityCell.Remove(id);
        }

        _entityPos.Remove(id);
    }

    /// <summary>
    /// Collect the ids of all entities within <paramref name="radius"/> world units of
    /// <paramref name="center"/> into <paramref name="results"/>. Broadphase by cell, then an
    /// exact distance refine so the result is precise. <paramref name="results"/> is cleared first.
    /// </summary>
    public void QueryRadius(Vec2 center, float radius, ICollection<int> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        results.Clear();

        if (radius < 0f)
        {
            return;
        }

        float radiusSq = radius * radius;

        int minCx = FloorToCell(center.X - radius);
        int maxCx = FloorToCell(center.X + radius);
        int minCy = FloorToCell(center.Y - radius);
        int maxCy = FloorToCell(center.Y + radius);

        for (int cx = minCx; cx <= maxCx; cx++)
        {
            for (int cy = minCy; cy <= maxCy; cy++)
            {
                if (!_cells.TryGetValue(CellKey(cx, cy), out HashSet<int>? bucket))
                {
                    continue;
                }

                foreach (int id in bucket)
                {
                    if (_entityPos.TryGetValue(id, out Vec2 pos) &&
                        Vec2.DistanceSquared(center, pos) <= radiusSq)
                    {
                        results.Add(id);
                    }
                }
            }
        }
    }

    private void AddToCell(long key, int id)
    {
        if (!_cells.TryGetValue(key, out HashSet<int>? bucket))
        {
            bucket = new HashSet<int>();
            _cells[key] = bucket;
        }

        bucket.Add(id);
    }

    private void RemoveFromCell(long key, int id)
    {
        if (_cells.TryGetValue(key, out HashSet<int>? bucket))
        {
            bucket.Remove(id);
            if (bucket.Count == 0)
            {
                _cells.Remove(key);
            }
        }
    }

    private int FloorToCell(float worldCoord) => (int)MathF.Floor(worldCoord / _cellSize);

    private long CellKey(Vec2 position) => CellKey(FloorToCell(position.X), FloorToCell(position.Y));

    private static long CellKey(int cx, int cy) => ((long)(uint)cx << 32) | (uint)cy;
}
