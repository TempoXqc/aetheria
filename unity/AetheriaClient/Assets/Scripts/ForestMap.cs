using UnityEngine;

namespace Aetheria.UnityClient
{
    /// <summary>
    /// The REAL game ground: the sculpted relief of TriForge's « Fantasy Forest Environment »
    /// demo terrain (a 60×60 glade — a soft wooded basin), sampled for its HEIGHTS only and
    /// rendered as our own textured mesh. We deliberately do NOT spawn a Unity Terrain: the
    /// terrain surface shader gets stripped from headless player builds and turns bright pink.
    /// A plain mesh with a Standard-shaded grass material always renders. The relief is purely
    /// visual — the simulation stays 2D; entities, camera and nameplates ask <see cref="HeightAt"/>.
    /// </summary>
    public static class ForestMap
    {
        // The flattest patch of the demo terrain, measured offline from its heightmap: this
        // terrain-local point sits at the world origin, under the sanctuary glade.
        private const float AnchorX = 36f;
        private const float AnchorZ = 26.5f;

        // The visible ground mesh: a square carpet centred on the origin, sampled on this grid.
        private const float GroundHalf = 155f;  // covers the whole playable area (±120 mapped)
        private const float GroundStep = 2f;     // vertex spacing (gentle relief, 2u is plenty)
        private const float GrassTile = 6f;      // grass texture repeats every 6 world units

        private static bool _checked;
        private static TerrainData _source;
        private static float _tile = 60f;        // one terrain tile's width in world units
        private static float _originHeight;      // terrain height sampled under the world origin
        private static GameObject _treePrefab;
        private static bool _treeChecked;

        public static bool Available
        {
            get
            {
                if (!_checked)
                {
                    _checked = true;
                    _source = Resources.Load<TerrainData>("FantasyForest/Scenes/New Terrain");
                    if (_source != null)
                    {
                        _tile = Mathf.Max(1f, _source.size.x);
                        _originHeight = SampleRaw(AnchorX, AnchorZ);
                    }
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

            // MIRROR-tile the single glade so the ground repeats seamlessly with no cliffs and
            // no forced-flat seams: reflecting every other tile makes shared edges line up.
            float lx = Mirror(x + AnchorX, _tile);
            float lz = Mirror(z + AnchorZ, _tile);
            return _source.GetInterpolatedHeight(lx / _tile, lz / _tile) - _originHeight;
        }

        /// <summary>The grounded world position for flat-plane coordinates.</summary>
        public static Vector3 At(float x, float z)
        {
            return new Vector3(x, HeightAt(x, z), z);
        }

        /// <summary>Build the visible forest floor. Parented to the decor root; tears down with it.</summary>
        public static void Build(Transform parent)
        {
            if (!Available)
            {
                return;
            }

            int n = Mathf.RoundToInt((GroundHalf * 2f) / GroundStep);
            int span = n + 1;
            var vertices = new Vector3[span * span];
            var uv = new Vector2[span * span];
            for (int z = 0; z <= n; z++)
            {
                for (int x = 0; x <= n; x++)
                {
                    float wx = -GroundHalf + (x * GroundStep);
                    float wz = -GroundHalf + (z * GroundStep);
                    int idx = (z * span) + x;
                    vertices[idx] = new Vector3(wx, HeightAt(wx, wz), wz);
                    uv[idx] = new Vector2(wx / GrassTile, wz / GrassTile);
                }
            }

            var tris = new int[n * n * 6];
            int t = 0;
            for (int z = 0; z < n; z++)
            {
                for (int x = 0; x < n; x++)
                {
                    int i0 = (z * span) + x;
                    int i1 = i0 + 1;
                    int i2 = i0 + span;
                    int i3 = i2 + 1;
                    tris[t++] = i0; tris[t++] = i2; tris[t++] = i1;
                    tris[t++] = i1; tris[t++] = i2; tris[t++] = i3;
                }
            }

            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var go = new GameObject("ForestGround");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().mesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.material = GroundMaterial();
        }

        /// <summary>
        /// A demo forest tree (tree_1) at a flat-plane spot, grounded and height-normalized.
        /// Materials are retargeted onto a guaranteed-included shader so nothing turns pink.
        /// Returns null when the pack is missing so the caller can fall back to a primitive tree.
        /// </summary>
        public static GameObject SpawnTree(Transform parent, Vector3 flatPos, float targetHeight)
        {
            if (!_treeChecked)
            {
                _treeChecked = true;
                _treePrefab = Resources.Load<GameObject>("FantasyForest/Meshes/Prefabs/tree_1");
            }

            if (_treePrefab == null)
            {
                return null;
            }

            GameObject tree = Object.Instantiate(_treePrefab, parent, false);
            tree.name = "ForestTree";
            tree.transform.position = At(flatPos.x, flatPos.z);
            tree.transform.rotation = Quaternion.Euler(0f, (flatPos.x * 47f) + (flatPos.z * 13f), 0f);

            // Normalize to the wanted height by measured bounds; feet on the ground.
            Bounds b = MeasureBounds(tree);
            if (b.size.y > 0.001f)
            {
                float k = targetHeight / b.size.y;
                tree.transform.localScale = new Vector3(k, k, k);
            }

            RetargetToStandard(tree);
            return tree;
        }

        private static Material GroundMaterial()
        {
            // Copy a primitive's default material — the exact shader that already renders our
            // ground fine in the build — then drape the demo's grass texture over it.
            GameObject probe = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Material m = new Material(probe.GetComponent<Renderer>().sharedMaterial);
            Object.Destroy(probe);

            Texture2D grass = Resources.Load<Texture2D>("FantasyForest/Textures/grass01");
            if (grass != null)
            {
                grass.wrapMode = TextureWrapMode.Repeat;
                m.mainTexture = grass;
            }

            m.color = new Color(0.86f, 0.90f, 0.72f); // let the grass carry the colour
            return m;
        }

        /// <summary>Foliage/bark materials may reference the pack's custom cull-off shader, which
        /// can be stripped from the build (→ pink). Rebuild each material on the always-present
        /// Standard shader, keeping its texture and colour.</summary>
        private static void RetargetToStandard(GameObject root)
        {
            Shader standard = Shader.Find("Standard");
            if (standard == null)
            {
                return;
            }

            foreach (Renderer rend in root.GetComponentsInChildren<Renderer>())
            {
                Material[] mats = rend.materials;
                for (int i = 0; i < mats.Length; i++)
                {
                    Material src = mats[i];
                    if (src == null)
                    {
                        continue;
                    }

                    var fresh = new Material(standard);
                    if (src.mainTexture != null) { fresh.mainTexture = src.mainTexture; }
                    mats[i] = fresh;
                }

                rend.materials = mats;
            }
        }

        private static Bounds MeasureBounds(GameObject root)
        {
            Renderer[] parts = root.GetComponentsInChildren<Renderer>();
            var bounds = new Bounds(root.transform.position, Vector3.zero);
            bool first = true;
            foreach (Renderer part in parts)
            {
                if (first) { bounds = part.bounds; first = false; }
                else { bounds.Encapsulate(part.bounds); }
            }

            return bounds;
        }

        private static float SampleRaw(float localX, float localZ)
        {
            return _source.GetInterpolatedHeight(localX / _tile, localZ / _tile);
        }

        /// <summary>Reflecting tiler: maps any coordinate into [0, len] by ping-ponging.</summary>
        private static float Mirror(float v, float len)
        {
            float p = Mathf.Repeat(v, 2f * len);
            return p > len ? (2f * len) - p : p;
        }
    }
}
