using Aetheria.Shared;
using Aetheria.Shared.Data;
using UnityEngine;

namespace Aetheria.UnityClient
{
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
