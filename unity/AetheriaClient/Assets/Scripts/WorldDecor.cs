using Aetheria.Shared;
using Aetheria.Shared.Data;
using UnityEngine;

namespace Aetheria.UnityClient
{
    /// <summary>
    /// Loader for the Quaternius "Stylized Nature MegaKit" (CC0, Assets/Resources/Nature):
    /// spawns a named model, normalises it to a target height, and wires its textures by
    /// material name (Bark_NormalTree → Bark_NormalTree.png, Pebbles → PathRocks_Diffuse…).
    /// When the kit isn't imported, callers fall back to the old procedural primitives.
    /// </summary>
    public static class NatureModels
    {
        private const string Root = "Nature/";

        private static readonly System.Collections.Generic.Dictionary<string, GameObject> Cache =
            new System.Collections.Generic.Dictionary<string, GameObject>();
        private static readonly System.Collections.Generic.Dictionary<string, Texture2D> TexCache =
            new System.Collections.Generic.Dictionary<string, Texture2D>();
        private static bool _checked;
        private static bool _available;

        public static bool Available
        {
            get
            {
                if (!_checked)
                {
                    _checked = true;
                    _available = Load("CommonTree_1") != null;
                }

                return _available;
            }
        }

        private static GameObject Load(string name)
        {
            GameObject cached;
            if (!Cache.TryGetValue(name, out cached))
            {
                cached = Resources.Load<GameObject>(Root + name);
                Cache[name] = cached;
            }

            return cached;
        }

        /// <summary>Spawn a kit model at a spot, scaled so its height ≈ targetHeight.</summary>
        public static GameObject Spawn(Transform parent, string name, Vector3 pos, float targetHeight, float yawDegrees)
        {
            GameObject prefab = Load(name);
            if (prefab == null)
            {
                return null;
            }

            GameObject go = Object.Instantiate(prefab, parent, false);
            go.name = name;
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(0f, yawDegrees, 0f);

            float height = 0f;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                float y = r.bounds.max.y - pos.y;
                if (y > height) { height = y; }
            }

            if (height > 0.05f)
            {
                float k = targetHeight / height;
                go.transform.localScale = new Vector3(k, k, k);
            }

            ApplyTextures(go);
            return go;
        }

        private static void ApplyTextures(GameObject go)
        {
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material m in r.materials)
                {
                    if (m == null) { continue; }

                    Texture2D tex = TextureFor(m.name);
                    if (tex != null)
                    {
                        m.mainTexture = tex;
                        m.color = Color.white;
                    }

                    // Foliage is authored as CARDS with an alpha texture: without alpha-cutout
                    // the transparent parts render as solid white sheets.
                    if (IsFoliage(m.name))
                    {
                        MakeCutout(m);
                    }
                }
            }
        }

        private static bool IsFoliage(string materialName)
        {
            return materialName.Contains("Leaves") || materialName.Contains("Leaf") ||
                   materialName.Contains("Grass") || materialName.Contains("Flower") ||
                   materialName.Contains("Petal") || materialName.Contains("Clover") ||
                   materialName.Contains("Fern");
        }

        /// <summary>Switch a Standard-shader material to alpha-cutout rendering.</summary>
        private static void MakeCutout(Material m)
        {
            m.SetFloat("_Mode", 1f);
            m.SetFloat("_Cutoff", 0.45f);
            m.SetOverrideTag("RenderType", "TransparentCutout");
            m.EnableKeyword("_ALPHATEST_ON");
            m.DisableKeyword("_ALPHABLEND_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            m.renderQueue = 2450;
        }

        /// <summary>The kit names materials after their texture — resolve with light fuzzing.</summary>
        private static Texture2D TextureFor(string materialName)
        {
            string n = materialName.Replace(" (Instance)", "").Replace("(Instance)", "").Trim();
            Texture2D tex = LoadTexture(n);
            if (tex == null) { tex = LoadTexture(n + "_Diffuse"); }
            if (tex == null && n.Contains("Rock")) { tex = LoadTexture("Rocks_Diffuse"); }
            if (tex == null && n.Contains("Pebble")) { tex = LoadTexture("PathRocks_Diffuse"); }
            if (tex == null && n.Contains("Leaf")) { tex = LoadTexture("Leaves"); }
            return tex;
        }

        private static Texture2D LoadTexture(string name)
        {
            Texture2D cached;
            if (!TexCache.TryGetValue(name, out cached))
            {
                cached = Resources.Load<Texture2D>(Root + name);
                TexCache[name] = cached;
            }

            return cached;
        }
    }

    /// <summary>
    /// Loader for the Quaternius "Fantasy Props MegaKit" (CC0, Assets/Resources/Props): market
    /// stalls, barrels, torches, anvils… spawned by name, height-normalised, textures wired by
    /// their trim-atlas material names (MI_Trim_Metal → T_Trim_Metal_BaseColor…).
    /// </summary>
    public static class PropModels
    {
        private const string Root = "Props/";

        private static readonly System.Collections.Generic.Dictionary<string, GameObject> Cache =
            new System.Collections.Generic.Dictionary<string, GameObject>();
        private static readonly System.Collections.Generic.Dictionary<string, Texture2D> TexCache =
            new System.Collections.Generic.Dictionary<string, Texture2D>();
        private static bool _checked;
        private static bool _available;

        public static bool Available
        {
            get
            {
                if (!_checked)
                {
                    _checked = true;
                    _available = Load("Barrel") != null;
                }

                return _available;
            }
        }

        private static GameObject Load(string name)
        {
            GameObject cached;
            if (!Cache.TryGetValue(name, out cached))
            {
                cached = Resources.Load<GameObject>(Root + name);
                Cache[name] = cached;
            }

            return cached;
        }

        public static GameObject Spawn(Transform parent, string name, Vector3 pos, float targetHeight, float yawDegrees)
        {
            GameObject prefab = Load(name);
            if (prefab == null)
            {
                return null;
            }

            GameObject go = Object.Instantiate(prefab, parent, false);
            go.name = name;
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(0f, yawDegrees, 0f);

            float height = 0f;
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                float y = r.bounds.max.y - pos.y;
                if (y > height) { height = y; }
            }

            if (height > 0.02f)
            {
                float k = targetHeight / height;
                go.transform.localScale = new Vector3(k, k, k);
            }

            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material m in r.materials)
                {
                    if (m == null) { continue; }

                    string n = m.name;
                    Texture2D tex =
                        n.Contains("Cloth") ? LoadTexture("T_Trim_Cloth_BaseColor") :
                        n.Contains("Furniture") ? LoadTexture("T_Trim_Furniture_BaseColor") :
                        n.Contains("Metal") ? LoadTexture("T_Trim_Metal_BaseColor") :
                        LoadTexture("T_Trim_Props_BaseColor");
                    if (tex != null)
                    {
                        m.mainTexture = tex;
                        m.color = Color.white;
                    }
                }
            }

            return go;
        }

        private static Texture2D LoadTexture(string name)
        {
            Texture2D cached;
            if (!TexCache.TryGetValue(name, out cached))
            {
                cached = Resources.Load<Texture2D>(Root + name);
                TexCache[name] = cached;
            }

            return cached;
        }
    }

    /// <summary>
    /// Static scenery for the open world, built from primitives to match the server's layout:
    /// the paved SANCTUARY with its standing stones and bank corner, a dirt path east to the
    /// goblin starter camp, and the WOLF FIELD to the west — golden wheat, fences and haystacks.
    /// Pure visuals: the server knows nothing about any of it.
    /// </summary>
    public sealed class WorldDecor
    {
        private GameObject _root;

        public bool Active { get { return _root != null; } }

        public void EnsureBuilt()
        {
            if (_root != null)
            {
                return;
            }

            _root = new GameObject("WorldDecor");
            Transform r = _root.transform;

            // Hand-built world in the scene: the artist's placement wins — no auto-decor at all.
            if (GameObject.Find("HandmadeWorld") != null || Object.FindObjectOfType<Terrain>() != null)
            {
                return;
            }

            // --- SANCTUARY (radius 18 around the origin) ---
            // Paved plaza and a ring of standing stones marking the safe border.
            Tex.Apply(Block(r, "Plaza", new Vector3(0f, 0.02f, 0f), new Vector3(9f, 0.04f, 9f),
                new Color(0.47f, 0.46f, 0.44f)), "stone", 5f, 5f);
            Tex.Apply(Block(r, "PlazaTrim", new Vector3(0f, 0.045f, 0f), new Vector3(5f, 0.04f, 5f),
                new Color(0.55f, 0.54f, 0.50f)), "stone", 3f, 3f, new Color(1.0f, 1.0f, 0.95f));

            // Menhirs come from the SHARED layout: they block movement exactly where they stand.
            WorldLayout.Obstacle[] menhirs = WorldLayout.Menhirs;
            for (int i = 0; i < menhirs.Length; i++)
            {
                float h = 1.7f + ((i % 3) * 0.3f);

                if (NatureModels.Available)
                {
                    // A kit rock stretched upright — a real standing stone, not a grey pill.
                    GameObject stone = NatureModels.Spawn(r, "Rock_Medium_" + ((i % 3) + 1),
                        new Vector3(menhirs[i].X, 0f, menhirs[i].Y), h * 1.5f, i * 47f);
                    if (stone != null)
                    {
                        Vector3 sc = stone.transform.localScale;
                        stone.transform.localScale = new Vector3(sc.x * 0.55f, sc.y, sc.z * 0.55f);
                        continue;
                    }
                }

                Tex.Apply(Round(PrimitiveType.Capsule, r, "Menhir",
                    new Vector3(menhirs[i].X, h * 0.45f, menhirs[i].Y),
                    new Vector3(1.0f, h * 0.5f, 0.8f),
                    new Color(0.50f, 0.52f, 0.58f)), "stone", 1.5f, 1.5f);
            }

            // Bank corner: a canopy over the chest (the chest itself is a server entity).
            Vector3 bank = new Vector3(SimulationConstants.BankChestX, 0f, SimulationConstants.BankChestY);
            Tex.Apply(Block(r, "BankFloor", bank + new Vector3(0f, 0.03f, 0f), new Vector3(4.5f, 0.06f, 4f),
                new Color(0.38f, 0.33f, 0.28f)), "wood", 3f, 3f);
            Block(r, "BankPost1", bank + new Vector3(-1.8f, 1.1f, -1.5f), new Vector3(0.18f, 2.2f, 0.18f),
                new Color(0.30f, 0.20f, 0.11f));
            Block(r, "BankPost2", bank + new Vector3(1.8f, 1.1f, -1.5f), new Vector3(0.18f, 2.2f, 0.18f),
                new Color(0.30f, 0.20f, 0.11f));
            Block(r, "BankPost3", bank + new Vector3(-1.8f, 1.1f, 1.5f), new Vector3(0.18f, 2.2f, 0.18f),
                new Color(0.30f, 0.20f, 0.11f));
            Block(r, "BankPost4", bank + new Vector3(1.8f, 1.1f, 1.5f), new Vector3(0.18f, 2.2f, 0.18f),
                new Color(0.30f, 0.20f, 0.11f));
            Tex.Apply(Block(r, "BankRoof", bank + new Vector3(0f, 2.35f, 0f), new Vector3(4.8f, 0.14f, 4.3f),
                new Color(0.55f, 0.25f, 0.18f)), "wood", 3f, 3f, new Color(1f, 0.62f, 0.5f));

            // --- Dirt path EAST toward the goblin starter camp ---
            for (int i = 0; i < 6; i++)
            {
                Tex.Apply(Block(r, "Path", new Vector3(6f + (i * 3.4f), 0.015f, 2f + (i * 1.4f)),
                    new Vector3(3.0f, 0.03f, 2.2f), new Color(0.42f, 0.33f, 0.22f)), "dirt", 2f, 2f);
            }

            // Goblin camp dressing: crude tents and a totem.
            GoblinTent(r, new Vector3(23f, 0f, 12.5f));
            GoblinTent(r, new Vector3(27.5f, 0f, 9f));
            Block(r, "Totem", new Vector3(25f, 1.0f, 8f), new Vector3(0.35f, 2.0f, 0.35f),
                new Color(0.35f, 0.42f, 0.25f));

            // --- WOLF FIELD to the WEST: wheat, fences, haystacks ---
            Tex.Apply(Block(r, "Wheat", new Vector3(-48f, 0.015f, 3f), new Vector3(26f, 0.03f, 24f),
                new Color(0.72f, 0.62f, 0.28f)), "wheat", 14f, 12f);

            // Fence posts from the SHARED layout (they block); rails are pure decoration.
            foreach (WorldLayout.Obstacle post in WorldLayout.FencePosts)
            {
                Tex.Apply(Round(PrimitiveType.Cylinder, r, "Post", new Vector3(post.X, 0.45f, post.Y),
                    new Vector3(0.22f, 0.45f, 0.22f), new Color(0.36f, 0.25f, 0.14f)), "bark", 1f, 1f);
            }

            Rail(r, new Vector3(-61f, 0f, -9f), new Vector3(-35f, 0f, -9f));
            Rail(r, new Vector3(-61f, 0f, 15f), new Vector3(-35f, 0f, 15f));
            Rail(r, new Vector3(-61f, 0f, -9f), new Vector3(-61f, 0f, 15f));

            Haystack(r, new Vector3(-42f, 0f, 10f));
            Haystack(r, new Vector3(-54f, 0f, -4f));
            Haystack(r, new Vector3(-47f, 0f, -6f));

            // Trees and rocks from the SHARED layout: what you see is what blocks you.
            float[] treeScales = { 1.1f, 1.3f, 1.0f, 1.2f, 0.9f, 1.15f };
            WorldLayout.Obstacle[] trees = WorldLayout.Trees;
            for (int i = 0; i < trees.Length; i++)
            {
                Tree(r, new Vector3(trees[i].X, 0f, trees[i].Y), treeScales[i % treeScales.Length]);
            }

            float[] rockScales = { 1.4f, 1.0f, 1.8f };
            WorldLayout.Obstacle[] rocks = WorldLayout.Rocks;
            for (int i = 0; i < rocks.Length; i++)
            {
                Rock(r, new Vector3(rocks[i].X, 0f, rocks[i].Y), rockScales[i % rockScales.Length]);
            }

            // Ashmaw's scorched ground (world raid boss at 80,80).
            Block(r, "Scorch", new Vector3(80f, 0.01f, 80f), new Vector3(16f, 0.02f, 16f),
                new Color(0.20f, 0.12f, 0.10f));

            DressWithNature(r);
            DressWithProps(r);
        }

        /// <summary>
        /// PURE-VISUAL props (Fantasy Props MegaKit): a little market on the sanctuary plaza,
        /// torches on the menhir ring, a smith's corner, a training dummy — and set dressing for
        /// the goblin camp and the wolf farm. No-op when the kit isn't imported.
        /// </summary>
        private static void DressWithProps(Transform r)
        {
            if (!PropModels.Available)
            {
                return;
            }

            // Market row on the plaza's north edge, Northshire-style stalls with banners.
            PropModels.Spawn(r, "Stall_Empty", new Vector3(-4.5f, 0f, 6.5f), 2.6f, 175f);
            PropModels.Spawn(r, "Stall_Cart_Empty", new Vector3(-0.5f, 0f, 7.2f), 2.4f, 185f);
            PropModels.Spawn(r, "Banner_1", new Vector3(-6.8f, 0f, 6.2f), 3.0f, 180f);
            PropModels.Spawn(r, "Banner_2", new Vector3(1.8f, 0f, 7.0f), 3.0f, 180f);
            PropModels.Spawn(r, "Barrel_Apples", new Vector3(-3.0f, 0f, 5.4f), 0.9f, 30f);
            PropModels.Spawn(r, "FarmCrate_Apple", new Vector3(-2.1f, 0f, 5.8f), 0.5f, 70f);
            PropModels.Spawn(r, "FarmCrate_Carrot", new Vector3(-1.4f, 0f, 5.3f), 0.5f, 120f);

            // A smith's corner near the bank canopy.
            PropModels.Spawn(r, "Anvil", new Vector3(-6.5f, 0f, -4.5f), 0.9f, 40f);
            PropModels.Spawn(r, "Workbench", new Vector3(-7.6f, 0f, -3.2f), 1.0f, 100f);
            PropModels.Spawn(r, "WeaponStand", new Vector3(-5.2f, 0f, -5.6f), 1.7f, 320f);
            PropModels.Spawn(r, "Bucket_Wooden_1", new Vector3(-6.0f, 0f, -3.6f), 0.45f, 0f);

            // Somewhere to sit, something to hit.
            PropModels.Spawn(r, "Bench", new Vector3(4.8f, 0f, -5.6f), 0.85f, 205f);
            PropModels.Spawn(r, "Dummy", new Vector3(7.2f, 0f, -3.8f), 2.0f, 250f);
            PropModels.Spawn(r, "Barrel", new Vector3(5.9f, 0f, -6.4f), 0.9f, 90f);
            PropModels.Spawn(r, "Crate_Wooden", new Vector3(6.6f, 0f, -6.0f), 0.7f, 15f);

            // Torches on four of the menhirs' shoulders — the ring glows at the border.
            PropModels.Spawn(r, "Torch_Metal", new Vector3(10f, 0f, 10f), 1.8f, 45f);
            PropModels.Spawn(r, "Torch_Metal", new Vector3(-10f, 0f, 10f), 1.8f, 135f);
            PropModels.Spawn(r, "Torch_Metal", new Vector3(-10f, 0f, -10f), 1.8f, 225f);
            PropModels.Spawn(r, "Torch_Metal", new Vector3(10f, 0f, -10f), 1.8f, 315f);

            // Goblin camp: a stew cauldron, a prisoner cage, plunder.
            PropModels.Spawn(r, "Cauldron", new Vector3(25.4f, 0f, 10.2f), 1.0f, 0f);
            PropModels.Spawn(r, "Cage_Small", new Vector3(28.6f, 0f, 11.6f), 1.4f, 200f);
            PropModels.Spawn(r, "Chest_Wood", new Vector3(24.2f, 0f, 12.8f), 0.8f, 145f);

            // Wolf farm: crates and a table by the haystacks.
            PropModels.Spawn(r, "Crate_Wooden", new Vector3(-43.5f, 0f, 9f), 0.6f, 60f);
            PropModels.Spawn(r, "Table_Large", new Vector3(-53f, 0f, -5.2f), 0.95f, 20f);
            PropModels.Spawn(r, "Stool", new Vector3(-52.2f, 0f, -4.4f), 0.55f, 300f);
            PropModels.Spawn(r, "Bucket_Wooden_1", new Vector3(-46.4f, 0f, -6.4f), 0.45f, 0f);
        }

        /// <summary>
        /// PURE-VISUAL vegetation from the nature kit (nothing here blocks movement): grass and
        /// flowers scattered across the fields, mushrooms and gnarled trees at the goblin camp,
        /// dead trees around the raid boss's scorched ground. Deterministic (seeded) so every
        /// client sees the same world. No-op when the kit isn't imported.
        /// </summary>
        private static void DressWithNature(Transform r)
        {
            if (!NatureModels.Available)
            {
                return;
            }

            // Scatter: small ground cover in a wide ring around the sanctuary (kept out of the
            // plaza, the path corridor and the boss ground).
            string[] cover = { "Grass_Common_Tall", "Grass_Wispy_Tall", "Flower_3_Group",
                "Flower_4_Group", "Bush_Common", "Bush_Common_Flowers", "Fern_1", "Clover_1",
                "Plant_1_Big", "Pebble_Round_1", "Pebble_Round_2", "Pebble_Round_3" };
            var rng = new System.Random(4242);
            for (int i = 0; i < 140; i++)
            {
                float x = ((float)rng.NextDouble() * 150f) - 75f;
                float z = ((float)rng.NextDouble() * 150f) - 75f;
                float dOrigin = Mathf.Sqrt((x * x) + (z * z));
                if (dOrigin < 19f) { continue; }                                    // sanctuary
                if (x > 70f && z > 70f) { continue; }                               // scorched ground
                float h = 0.35f + ((float)rng.NextDouble() * 0.6f);
                string name = cover[rng.Next(cover.Length)];
                NatureModels.Spawn(r, name, new Vector3(x, 0f, z), h, rng.Next(360));
            }

            // DENSE grass pass, Northshire-style: the fields should read as meadow, not lawn.
            for (int i = 0; i < 1400; i++)
            {
                float x = ((float)rng.NextDouble() * 190f) - 95f;
                float z = ((float)rng.NextDouble() * 190f) - 95f;
                if (Mathf.Sqrt((x * x) + (z * z)) < 17f) { continue; }
                if (x > 70f && z > 70f) { continue; }
                string tuft = (i % 3) == 0 ? "Grass_Wispy_Tall" : "Grass_Common_Tall";
                NatureModels.Spawn(r, tuft, new Vector3(x, 0f, z),
                    0.28f + ((float)rng.NextDouble() * 0.35f), rng.Next(360));
            }

            // CLUMPS: real meadows grow in patches, not white noise — 70 clusters of 6-10 tufts
            // (with the odd flower) so the ground reads as living grass everywhere you look.
            for (int c = 0; c < 70; c++)
            {
                float cx = ((float)rng.NextDouble() * 180f) - 90f;
                float cz = ((float)rng.NextDouble() * 180f) - 90f;
                if (Mathf.Sqrt((cx * cx) + (cz * cz)) < 18f) { continue; }
                if (cx > 68f && cz > 68f) { continue; }
                int n = 6 + rng.Next(5);
                for (int i = 0; i < n; i++)
                {
                    float a = (float)rng.NextDouble() * Mathf.PI * 2f;
                    float d = (float)rng.NextDouble() * 2.4f;
                    string plant = (i == 0 && (c % 4) == 0) ? "Flower_3_Group"
                        : (i % 3) == 0 ? "Grass_Wispy_Tall" : "Grass_Common_Tall";
                    NatureModels.Spawn(r, plant,
                        new Vector3(cx + (Mathf.Cos(a) * d), 0f, cz + (Mathf.Sin(a) * d)),
                        0.30f + ((float)rng.NextDouble() * 0.4f), rng.Next(360));
                }
            }

            // Goblin camp: gnarled trees and mushrooms — it should feel WRONG there.
            NatureModels.Spawn(r, "TwistedTree_1", new Vector3(21f, 0f, 15.5f), 5.0f, 40f);
            NatureModels.Spawn(r, "TwistedTree_2", new Vector3(29.5f, 0f, 13f), 4.4f, 210f);
            NatureModels.Spawn(r, "Mushroom_Common", new Vector3(24f, 0f, 10.5f), 0.5f, 15f);
            NatureModels.Spawn(r, "Mushroom_Common", new Vector3(26.4f, 0f, 11.8f), 0.35f, 150f);

            // Pines along the road east, framing the walk to the camp.
            NatureModels.Spawn(r, "Pine_1", new Vector3(10f, 0f, -1.5f), 7f, 80f);
            NatureModels.Spawn(r, "Pine_2", new Vector3(16f, 0f, 8.5f), 6.2f, 300f);
            NatureModels.Spawn(r, "Pine_3", new Vector3(13.5f, 0f, 2.5f), 7.5f, 200f);

            // The boss's dead grove: burnt trunks ringing the scorch.
            NatureModels.Spawn(r, "DeadTree_1", new Vector3(72f, 0f, 74f), 6f, 20f);
            NatureModels.Spawn(r, "DeadTree_2", new Vector3(87f, 0f, 73.5f), 5.4f, 130f);
            NatureModels.Spawn(r, "DeadTree_1", new Vector3(74.5f, 0f, 87f), 5.8f, 250f);
            NatureModels.Spawn(r, "DeadTree_2", new Vector3(86f, 0f, 86.5f), 6.4f, 310f);
        }

        public void Teardown()
        {
            if (_root != null)
            {
                Object.Destroy(_root);
                _root = null;
            }
        }

        // ------------------------------------------------------------- Helpers

        private static void GoblinTent(Transform r, Vector3 at)
        {
            var a = Block(r, "TentA", at + new Vector3(-0.55f, 0.55f, 0f), new Vector3(0.12f, 1.7f, 1.6f),
                new Color(0.45f, 0.35f, 0.22f));
            Tex.Apply(a, "wood", 1.5f, 1.5f);
            a.transform.localRotation = Quaternion.Euler(0f, 0f, 38f);
            var b = Block(r, "TentB", at + new Vector3(0.55f, 0.55f, 0f), new Vector3(0.12f, 1.7f, 1.6f),
                new Color(0.42f, 0.32f, 0.20f));
            Tex.Apply(b, "wood", 1.5f, 1.5f);
            b.transform.localRotation = Quaternion.Euler(0f, 0f, -38f);
        }

        private static void Rail(Transform r, Vector3 from, Vector3 to)
        {
            Vector3 delta = to - from;
            float length = delta.magnitude;
            var rail = Round(PrimitiveType.Cylinder, r, "Rail",
                from + (delta * 0.5f) + new Vector3(0f, 0.72f, 0f),
                new Vector3(0.07f, length * 0.5f, 0.07f), new Color(0.36f, 0.25f, 0.14f));
            Tex.Apply(rail, "wood", 1f, 6f);
            rail.transform.localRotation =
                Quaternion.FromToRotation(Vector3.up, delta.normalized); // cylinder axis → fence line
        }

        private static void Haystack(Transform r, Vector3 at)
        {
            Color hay = new Color(0.80f, 0.68f, 0.30f);
            Tex.Apply(Round(PrimitiveType.Sphere, r, "Hay1", at + new Vector3(0f, 0.55f, 0f), new Vector3(1.7f, 1.2f, 1.7f), hay), "straw", 2f, 2f);
            Tex.Apply(Round(PrimitiveType.Sphere, r, "Hay2", at + new Vector3(0f, 1.25f, 0f), new Vector3(1.0f, 0.7f, 1.0f), hay), "straw", 2f, 2f);
        }

        private static void Tree(Transform r, Vector3 at, float s)
        {
            if (NatureModels.Available)
            {
                // A real tree from the kit: variant and yaw picked deterministically per spot.
                int hash = Mathf.Abs((int)((at.x * 7f) + (at.z * 13f)));
                string name = "CommonTree_" + ((hash % 5) + 1);
                if (NatureModels.Spawn(r, name, at, 5.5f * s, hash % 360) != null)
                {
                    return;
                }
            }

            Tex.Apply(Round(PrimitiveType.Cylinder, r, "Trunk", at + new Vector3(0f, 0.7f * s, 0f),
                new Vector3(0.3f * s, 0.7f * s, 0.3f * s), new Color(0.30f, 0.20f, 0.11f)), "bark", 1.5f, 2f);
            Color leaves = new Color(0.16f, 0.38f, 0.18f);
            Tex.Apply(Round(PrimitiveType.Sphere, r, "Crown", at + new Vector3(0f, 2.0f * s, 0f),
                new Vector3(1.9f * s, 1.5f * s, 1.9f * s), leaves), "leaves", 2.5f, 2.5f);
            Tex.Apply(Round(PrimitiveType.Sphere, r, "Crown2", at + new Vector3(0.5f * s, 2.5f * s, 0.3f * s),
                new Vector3(1.1f * s, 0.9f * s, 1.1f * s), leaves * 1.12f), "leaves", 2f, 2f);
        }

        private static void Rock(Transform r, Vector3 at, float s)
        {
            if (NatureModels.Available)
            {
                int hash = Mathf.Abs((int)((at.x * 11f) + (at.z * 5f)));
                string name = "Rock_Medium_" + ((hash % 3) + 1);
                if (NatureModels.Spawn(r, name, at, 1.1f * s, hash % 360) != null)
                {
                    return;
                }
            }

            var rock = Round(PrimitiveType.Sphere, r, "Rock", at + new Vector3(0f, 0.35f * s, 0f),
                new Vector3(1.3f * s, 0.75f * s, 1.05f * s), new Color(0.40f, 0.40f, 0.43f));
            Tex.Apply(rock, "stone", 2f, 2f, new Color(0.75f, 0.75f, 0.78f));
            rock.transform.localRotation = Quaternion.Euler(0f, 30f * s, 8f);
        }

        private static GameObject Round(PrimitiveType type, Transform parent, string name,
            Vector3 pos, Vector3 scale, Color color)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = color;
            }

            return go;
        }

        private static GameObject Block(Transform parent, string name, Vector3 pos, Vector3 scale, Color color)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = color;
            }

            return go;
        }
    }

    /// <summary>
    /// The day/night cycle, driven by the SERVER tick so every player lives under the same sky.
    /// One full day lasts <see cref="DayLengthSeconds"/>; the sun wheels across the sky, its
    /// light warms and fades, the ambient and sky colours follow, night falls blue and dim.
    /// </summary>
    public static class DayNight
    {
        /// <summary>Real seconds for one full in-game day (10 min: you SEE time pass).</summary>
        public const float DayLengthSeconds = 600f;

        private static readonly Color DaySun = new Color(1f, 0.90f, 0.72f);
        private static readonly Color DawnSun = new Color(1f, 0.62f, 0.38f);
        private static readonly Color MoonLight = new Color(0.45f, 0.55f, 0.85f);
        private static readonly Color DayAmbient = new Color(0.48f, 0.44f, 0.40f);
        private static readonly Color NightAmbient = new Color(0.13f, 0.15f, 0.24f);
        private static readonly Color DaySky = new Color(0.35f, 0.55f, 0.80f);
        private static readonly Color DawnSky = new Color(0.75f, 0.45f, 0.30f);
        private static readonly Color NightSky = new Color(0.03f, 0.045f, 0.10f);

        /// <summary>Phase for a given world time: 0 = midnight, 0.25 = dawn, 0.5 = noon.</summary>
        public static float PhaseFor(float worldSeconds)
            => (worldSeconds % DayLengthSeconds) / DayLengthSeconds;

        public static void Apply(Light sun, Camera cam, float phase)
        {
            if (sun == null) { return; }

            // The sun wheels a full turn per day: below the horizon at night.
            sun.transform.rotation = Quaternion.Euler((phase * 360f) - 90f, 40f, 0f);

            // Daylight strength: 0 at night, 1 at noon; "dawnness" peaks at sunrise/sunset.
            float daylight = Mathf.Clamp01(Mathf.Sin((phase - 0.25f) * 2f * Mathf.PI) * 1.6f);
            float dawn = Mathf.Clamp01(1f - (Mathf.Abs(daylight - 0.25f) * 4f)) *
                         (daylight > 0.01f ? 1f : 0f);

            sun.intensity = 0.10f + (1.15f * daylight);
            sun.color = Color.Lerp(Color.Lerp(MoonLight, DaySun, daylight), DawnSun, dawn * 0.7f);
            sun.shadowStrength = 0.35f + (0.45f * daylight);

            RenderSettings.ambientLight = Color.Lerp(NightAmbient, DayAmbient, daylight);
            if (cam != null)
            {
                Color sky = Color.Lerp(NightSky, DaySky, daylight);
                cam.backgroundColor = Color.Lerp(sky, DawnSky, dawn * 0.6f);
            }
        }
    }

    /// <summary>
    /// The WoW-style minimap: a second orthographic camera looking straight down at the player,
    /// rendered into a RenderTexture the HUD draws in the top-right corner.
    /// </summary>
    public sealed class MinimapView
    {
        private GameObject _root;
        private Camera _camera;

        public RenderTexture Texture { get; private set; }

        public void EnsureBuilt()
        {
            if (_root != null)
            {
                return;
            }

            _root = new GameObject("MinimapCam");
            Texture = new RenderTexture(192, 192, 16);
            _camera = _root.AddComponent<Camera>();
            _camera.orthographic = true;
            _camera.orthographicSize = 26f;
            _camera.targetTexture = Texture;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.24f, 0.34f, 0.18f); // grass from above
            _root.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }

        /// <summary>Keep the camera parked high above the player, looking straight down.</summary>
        public void Tick(Vector3 playerPos)
        {
            if (_root != null)
            {
                _root.transform.position = playerPos + new Vector3(0f, 70f, 0f);
            }
        }

        public void Teardown()
        {
            if (_root != null)
            {
                Object.Destroy(_root);
                _root = null;
                _camera = null;
            }

            if (Texture != null)
            {
                Texture.Release();
                Object.Destroy(Texture);
                Texture = null;
            }
        }
    }

    /// <summary>
    /// The character sheet's live 3D portrait: the player's model on a tiny hidden stage far from
    /// the world, rendered by its own camera into a RenderTexture the OnGUI sheet displays.
    /// </summary>
    public sealed class SheetPreview
    {
        private static readonly Vector3 Origin = new Vector3(700f, -40f, 700f);

        private GameObject _root;
        private Transform _slot;
        private Camera _camera;
        private int _key = -1;

        public RenderTexture Texture { get; private set; }

        /// <summary>Build/refresh the portrait for this snapshot of yourself. Cheap when unchanged.</summary>
        public void EnsureFor(Aetheria.Shared.Protocol.EntitySnapshot self)
        {
            int key = self.RaceId | (self.ClassId << 4) | ((byte)self.Gender << 8) |
                      (self.Appearance.SkinTone << 9) | (self.Appearance.Face << 12) |
                      (self.Appearance.HairStyle << 15) | (self.Appearance.HairColor << 18) |
                      (self.Appearance.BeardStyle << 21) | (self.Appearance.BeardColor << 24);

            // Fold the whole loadout in, so equipping a piece refreshes the portrait too.
            for (int i = 0; i < Aetheria.Shared.Items.EquipSlots.Count; i++)
            {
                key = (key * 31) ^ (self.EquippedIn((Aetheria.Shared.Items.EquipSlot)i) + (i << 8));
            }

            if (_root == null)
            {
                _root = new GameObject("SheetPreview");
                _root.transform.position = Origin;

                var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Object.Destroy(floor.GetComponent<Collider>());
                floor.transform.SetParent(_root.transform, false);
                floor.transform.localPosition = new Vector3(0f, -0.05f, 0f);
                floor.transform.localScale = new Vector3(3f, 0.1f, 3f);
                floor.GetComponent<Renderer>().material.color = new Color(0.16f, 0.16f, 0.20f);

                _slot = new GameObject("Slot").transform;
                _slot.SetParent(_root.transform, false);

                Texture = new RenderTexture(256, 340, 16);
                var camGo = new GameObject("SheetCam");
                camGo.transform.SetParent(_root.transform, false);
                camGo.transform.localPosition = new Vector3(0f, 1.35f, 3.1f);
                camGo.transform.localRotation = Quaternion.LookRotation(
                    new Vector3(0f, 1.0f, 0f) - new Vector3(0f, 1.35f, 3.1f), Vector3.up);
                _camera = camGo.AddComponent<Camera>();
                _camera.targetTexture = Texture;
                _camera.fieldOfView = 38f;
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = new Color(0.10f, 0.10f, 0.14f);

                var lightGo = new GameObject("SheetLight");
                lightGo.transform.SetParent(_root.transform, false);
                lightGo.transform.localPosition = new Vector3(1.5f, 2.5f, 2f);
                Light l = lightGo.AddComponent<Light>();
                l.type = LightType.Point;
                l.range = 10f;
                l.intensity = 1.4f;
            }

            if (key != _key)
            {
                _key = key;
                for (int i = _slot.childCount - 1; i >= 0; i--)
                {
                    Object.Destroy(_slot.GetChild(i).gameObject);
                }

                var model = new GameObject("Portrait");
                model.transform.SetParent(_slot, false);
                CharacterModelBuilder.Build(model.transform, self);
            }
        }

        /// <summary>Portrait yaw in degrees — driven by the player's right-button drag, never automatic.</summary>
        public float Yaw;

        public void Tick(float dt)
        {
            if (_slot != null)
            {
                _slot.localRotation = Quaternion.Euler(0f, Yaw, 0f);
            }
        }

        public void Teardown()
        {
            if (_root != null)
            {
                Object.Destroy(_root);
                _root = null;
                _key = -1;
            }

            if (Texture != null)
            {
                Texture.Release();
                Object.Destroy(Texture);
                Texture = null;
            }
        }
    }
}
