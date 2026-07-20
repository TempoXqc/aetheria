using UnityEngine;

namespace Aetheria.UnityClient
{
    /// <summary>
    /// The REAL game ground: TriForge's « Fantasy Forest Environment » demo terrain (a 60×60
    /// glade — a soft basin ringed by low wooded hills, trees and grass painted in), tiled
    /// seamlessly to carpet the whole playable area. The sanctuary lands on the flattest part
    /// of the basin; entities, props and nameplates ask <see cref="HeightAt"/> so the flat
    /// server plane drapes over the relief. Purely visual — the simulation stays 2D.
    /// </summary>
    public static class ForestMap
    {
        // The flattest 36-unit patch of the demo terrain, measured offline from its heightmap:
        // this terrain-local point sits at the world origin, under the sanctuary ring.
        private const float AnchorX = 36f;
        private const float AnchorZ = 26.5f;

        /// <summary>Tiles from the centre in each direction (2 → a 5×5 carpet ≈ 300×300 units).</summary>
        private const int TileRing = 2;

        private static bool _checked;
        private static TerrainData _source;
        private static TerrainData _data;   // runtime clone, borders tapered for seamless tiling
        private static float _tile = 60f;   // tile width in world units
        private static float _originHeight; // terrain height under the world origin

        public static bool Available
        {
            get
            {
                if (!_checked)
                {
                    _checked = true;
                    _source = Resources.Load<TerrainData>("FantasyForest/Scenes/New Terrain");
                }

                return _source != null;
            }
        }

        /// <summary>Terrain height (world Y) under the flat-plane point (x, z). 0 without the pack.</summary>
        public static float HeightAt(float x, float z)
        {
            if (!Available)
            {
                return 0f;
            }

            EnsureData();
            float lx = Mathf.Repeat(x + AnchorX, _tile);
            float lz = Mathf.Repeat(z + AnchorZ, _tile);
            return _data.GetInterpolatedHeight(lx / _tile, lz / _tile) - _originHeight;
        }

        /// <summary>The grounded world position for flat-plane coordinates.</summary>
        public static Vector3 At(float x, float z)
        {
            return new Vector3(x, HeightAt(x, z), z);
        }

        /// <summary>
        /// Plant the tiled forest. Parented to the decor root so it tears down with the zone.
        /// </summary>
        public static void Build(Transform parent)
        {
            if (!Available)
            {
                return;
            }

            EnsureData();
            for (int i = -TileRing; i <= TileRing; i++)
            {
                for (int j = -TileRing; j <= TileRing; j++)
                {
                    GameObject go = Terrain.CreateTerrainGameObject(_data);
                    go.name = "ForestTerrain_" + i + "_" + j;
                    go.transform.SetParent(parent, false);
                    go.transform.position = new Vector3(
                        (i * _tile) - AnchorX, -_originHeight, (j * _tile) - AnchorZ);

                    Terrain t = go.GetComponent<Terrain>();
                    if (t != null)
                    {
                        t.heightmapPixelError = 6f;   // crisp silhouettes on the hill rims
                        t.basemapDistance = 120f;
                        t.treeDistance = 170f;        // the painted woods stay visible far out
                        t.detailObjectDistance = 70f; // grass only where the eye can resolve it
                    }
                }
            }
        }

        private static void EnsureData()
        {
            if (_data != null)
            {
                return;
            }

            // Work on a CLONE and taper its borders to zero so the 5×5 tiles meet without
            // cliffs or cracks — the demo's edges aren't naturally flat (up to 2.5u steps).
            _data = Object.Instantiate(_source);
            _tile = Mathf.Max(1f, _data.size.x);

            int res = _data.heightmapResolution;
            float[,] heights = _data.GetHeights(0, 0, res, res);
            int fade = Mathf.Max(2, res / 25); // ≈ 2.3 world units of blend per side
            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    int edge = Mathf.Min(Mathf.Min(x, res - 1 - x), Mathf.Min(z, res - 1 - z));
                    if (edge < fade)
                    {
                        heights[z, x] *= edge / (float)fade;
                    }
                }
            }

            _data.SetHeights(0, 0, heights);
            _originHeight = _data.GetInterpolatedHeight(AnchorX / _tile, AnchorZ / _tile);
        }
    }
}
