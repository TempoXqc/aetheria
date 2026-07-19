using Aetheria.Shared.Combat;
using Aetheria.Shared.Math;
using Aetheria.Shared.Protocol;
using UnityEngine;

namespace Aetheria.UnityClient
{
    /// <summary>
    /// The 3D backdrop behind the login / server-browser / character-creation screens: a night
    /// campsite (fire, torches, pines, mountains, moon) dressed with the REAL asset kits — pines,
    /// rocks, grass and flowers from the Nature MegaKit, torches, banners, bench and barrels from
    /// the Fantasy Props MegaKit, plus Nature Starter Kit 2 undergrowth when the user imported it.
    /// Every piece degrades to the old primitives when a kit is missing, so the scene always
    /// builds. Far away from the world origin so it never collides with in-game content. Also
    /// hosts the live 3D PREVIEW of the character being created, standing on a stone dais.
    /// While active it drives the camera itself (perspective); on teardown it hands the camera
    /// back to the isometric rig.
    /// </summary>
    public sealed class LobbyStage
    {
        private static readonly Vector3 Origin = new Vector3(500f, 0f, 500f);

        private GameObject _root;
        private Transform _previewSlot;
        private GameObject _previewModel;
        private ModelRig _previewRig;
        private Light _fireLight;
        private readonly System.Collections.Generic.List<Transform> _flames =
            new System.Collections.Generic.List<Transform>();
        private readonly System.Collections.Generic.List<Vector3> _flameBase =
            new System.Collections.Generic.List<Vector3>();

        private IsoCameraRig _rig;
        private float _t;

        public bool Active { get { return _root != null; } }

        /// <summary>Build the stage (idempotent) and take over the camera.</summary>
        public void EnsureBuilt()
        {
            if (_root != null)
            {
                return;
            }

            _root = new GameObject("LobbyStage");
            _root.transform.position = Origin;
            Transform r = _root.transform;

            // Night ground: real grass texture, tiled tight so the blades actually READ,
            // lightly dimmed for the night.
            Tex.Apply(Block(r, "Ground", new Vector3(0f, -0.1f, 0f), new Vector3(40f, 0.2f, 40f),
                new Color(0.14f, 0.19f, 0.14f)), "grass", 26f, 26f, new Color(0.62f, 0.68f, 0.60f));

            // Stone dais where the preview character stands.
            Tex.Apply(Block(r, "Dais", new Vector3(0f, 0.08f, 0f), new Vector3(2.6f, 0.16f, 2.6f),
                new Color(0.42f, 0.42f, 0.46f)), "stone", 3f, 3f, new Color(0.85f, 0.85f, 0.9f));
            Tex.Apply(Block(r, "DaisTrim", new Vector3(0f, 0.19f, 0f), new Vector3(2.0f, 0.06f, 2.0f),
                new Color(0.50f, 0.50f, 0.55f)), "stone", 2f, 2f);

            _previewSlot = new GameObject("PreviewSlot").transform;
            _previewSlot.SetParent(r, false);
            _previewSlot.localPosition = new Vector3(0f, 0.22f, 0f);

            // Campfire to the side: stone ring (real kit pebbles when available), bark logs,
            // animated flames, warm light.
            Vector3 fire = new Vector3(2.6f, 0f, -1.6f);
            for (int i = 0; i < 6; i++)
            {
                float a = i * Mathf.PI * 2f / 6f;
                Vector3 at = fire + new Vector3(Mathf.Cos(a) * 0.55f, 0f, Mathf.Sin(a) * 0.55f);
                if (!NatureModels.Available ||
                    NatureModels.Spawn(r, "Pebble_Round_" + ((i % 3) + 1), at, 0.18f, i * 61f) == null)
                {
                    Tex.Apply(Block(r, "FireStone", at + new Vector3(0f, 0.08f, 0f),
                        new Vector3(0.22f, 0.16f, 0.22f), new Color(0.35f, 0.34f, 0.36f)),
                        "stone", 1f, 1f);
                }
            }

            // Logs: real cylinders with bark, leaning into the fire like a proper camp pyre.
            for (int i = 0; i < 4; i++)
            {
                float a = (i * 90f) + 25f;
                GameObject log = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                log.name = "Log";
                Object.Destroy(log.GetComponent<Collider>());
                log.transform.SetParent(r, false);
                log.transform.localPosition = fire + new Vector3(
                    Mathf.Cos(a * Mathf.Deg2Rad) * 0.16f, 0.16f, Mathf.Sin(a * Mathf.Deg2Rad) * 0.16f);
                log.transform.localScale = new Vector3(0.09f, 0.34f, 0.09f);
                log.transform.localRotation = Quaternion.Euler(52f, a, 0f);
                Tint(log, new Color(0.30f, 0.19f, 0.10f));
                Tex.Apply(log, "bark", 1f, 2f, new Color(0.85f, 0.75f, 0.65f));
            }

            // Flames: layered teardrop spheres (no more orange cube) that flicker in Tick.
            _flames.Clear();
            _flames.Add(FlameOrb(r, fire + new Vector3(0f, 0.42f, 0f), new Vector3(0.42f, 0.60f, 0.42f),
                new Color(0.95f, 0.35f, 0.05f)));
            _flames.Add(FlameOrb(r, fire + new Vector3(0.04f, 0.52f, -0.02f), new Vector3(0.28f, 0.46f, 0.28f),
                new Color(1f, 0.62f, 0.10f)));
            _flames.Add(FlameOrb(r, fire + new Vector3(-0.02f, 0.58f, 0.02f), new Vector3(0.16f, 0.32f, 0.16f),
                new Color(1f, 0.88f, 0.35f)));

            var lightGo = new GameObject("FireLight");
            lightGo.transform.SetParent(r, false);
            lightGo.transform.localPosition = fire + new Vector3(0f, 0.8f, 0f);
            _fireLight = lightGo.AddComponent<Light>();
            _fireLight.type = LightType.Point;
            _fireLight.color = new Color(1f, 0.62f, 0.25f);
            _fireLight.range = 12f;
            _fireLight.intensity = 2.2f;

            // Torches framing the dais — real metal torches from the props kit when available.
            Torch(r, new Vector3(-2.2f, 0f, 1.6f));
            Torch(r, new Vector3(2.2f, 0f, 1.6f));

            // Banners behind the dais, framing the character like a hero podium.
            if (PropModels.Available)
            {
                PropModels.Spawn(r, "Banner_1", new Vector3(-1.5f, 0f, 2.6f), 3.0f, 180f);
                PropModels.Spawn(r, "Banner_2", new Vector3(1.5f, 0f, 2.6f), 3.0f, 180f);

                // Camp life around the fire: somewhere to sit, supplies, an adventurer's chest.
                PropModels.Spawn(r, "Bench", new Vector3(4.1f, 0f, -0.6f), 0.85f, 245f);
                PropModels.Spawn(r, "Barrel", new Vector3(3.9f, 0f, -3.0f), 0.9f, 30f);
                PropModels.Spawn(r, "Crate_Wooden", new Vector3(4.6f, 0f, -2.4f), 0.65f, 70f);
                PropModels.Spawn(r, "Chest_Wood", new Vector3(-2.9f, 0f, -1.9f), 0.75f, 130f);
                PropModels.Spawn(r, "Cauldron", new Vector3(1.6f, 0f, -2.6f), 0.9f, 0f);
            }

            // Pines around the clearing — real textured pines from the nature kit.
            KitPine(r, new Vector3(-5.5f, 0f, -3f), 1.0f);
            KitPine(r, new Vector3(-7f, 0f, 2.5f), 1.3f);
            KitPine(r, new Vector3(6.5f, 0f, 1.5f), 1.1f);
            KitPine(r, new Vector3(5f, 0f, -5f), 0.9f);
            KitPine(r, new Vector3(-3f, 0f, -6.5f), 1.2f);
            KitPine(r, new Vector3(8.5f, 0f, -2f), 1.25f);
            KitPine(r, new Vector3(-9f, 0f, -1f), 1.1f);

            // A leafy tree or two so the treeline isn't all pines.
            if (NatureModels.Available)
            {
                NatureModels.Spawn(r, "CommonTree_2", new Vector3(-6.2f, 0f, 5.5f), 6.5f, 70f);
                NatureModels.Spawn(r, "CommonTree_4", new Vector3(7.5f, 0f, 4.5f), 5.8f, 210f);
            }

            DressClearing(r);

            // Distant mountains and a moon.
            Mountain(r, new Vector3(-14f, 0f, -16f), 9f);
            Mountain(r, new Vector3(0f, 0f, -20f), 12f);
            Mountain(r, new Vector3(13f, 0f, -15f), 8f);

            GameObject moon = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            moon.name = "Moon";
            Object.Destroy(moon.GetComponent<Collider>());
            moon.transform.SetParent(r, false);
            moon.transform.localPosition = new Vector3(-9f, 14f, -24f);
            moon.transform.localScale = new Vector3(2.2f, 2.2f, 2.2f);
            Tint(moon, new Color(0.92f, 0.93f, 0.85f));

            // A wolf prowling at the tree line — decor, sourced from the same model builder.
            var wolfSnap = new EntitySnapshot(0, EntityKind.Monster, Faction.Neutral, Vec2.Zero,
                1, 1, 0, 0, 0f, 1, "", raceId: 2);
            var wolfGo = new GameObject("DecorWolf");
            wolfGo.transform.SetParent(r, false);
            wolfGo.transform.localPosition = new Vector3(-4.2f, 0f, -1.8f);
            wolfGo.transform.localRotation = Quaternion.Euler(0f, 130f, 0f);
            CharacterModelBuilder.Build(wolfGo.transform, wolfSnap);

            // Take over the camera: perspective, aimed so the dais sits right of the UI panel.
            Camera cam = Camera.main;
            if (cam != null)
            {
                _rig = cam.GetComponent<IsoCameraRig>();
                if (_rig != null)
                {
                    _rig.enabled = false;
                }

                cam.orthographic = false;
                cam.fieldOfView = 42f;
                cam.backgroundColor = new Color(0.045f, 0.055f, 0.11f); // night sky
                PlaceCamera(cam);
            }
        }

        /// <summary>Show (or rebuild) the 3D preview of the character being created.</summary>
        public void ShowPreview(byte raceId, byte classId, Gender gender, Appearance look, Faction faction)
        {
            if (_root == null)
            {
                return;
            }

            ClearPreview();

            var snap = new EntitySnapshot(0, EntityKind.Player, faction, Vec2.Zero,
                1, 1, 0, 0, 0f, 1, "", raceId, classId, gender, look);
            _previewModel = new GameObject("Preview");
            _previewModel.transform.SetParent(_previewSlot, false);
            _previewRig = CharacterModelBuilder.Build(_previewModel.transform, snap);
            _torsoBaseY = -1f;

            // Center the model on the dais whatever the pack's pivot is: measure the renderers
            // and shift so the FEET stand exactly on the middle of the stone.
            var bounds = new Bounds(_previewSlot.position, Vector3.zero);
            bool measured = false;
            foreach (Renderer rend in _previewModel.GetComponentsInChildren<Renderer>(true))
            {
                if (!measured) { bounds = rend.bounds; measured = true; }
                else { bounds.Encapsulate(rend.bounds); }
            }

            if (measured)
            {
                Vector3 slotPos = _previewSlot.position;
                _previewModel.transform.position += new Vector3(
                    slotPos.x - bounds.center.x, slotPos.y - bounds.min.y, slotPos.z - bounds.center.z);
            }

            // Tint the tunic with the faction colour so the camp choice reads instantly.
            if (_previewRig.TintTargets.Length > 0)
            {
                Color tunic = faction == Faction.Horde
                    ? new Color(0.75f, 0.22f, 0.18f)
                    : new Color(0.20f, 0.40f, 0.80f);
                for (int i = 0; i < _previewRig.TintTargets.Length; i++)
                {
                    _previewRig.TintTargets[i].material.color = tunic;
                }
            }
        }

        public void ClearPreview()
        {
            if (_previewModel != null)
            {
                Object.Destroy(_previewModel);
                _previewModel = null;
                _previewRig = null;
            }
        }

        /// <summary>Preview yaw in degrees — driven by the player's right-button drag, never automatic.</summary>
        public float PreviewYaw;

        private float _torsoBaseY = -1f;

        /// <summary>Animate flames and apply the player-driven preview yaw; keep the camera in place.</summary>
        public void Tick(float dt)
        {
            if (_root == null)
            {
                return;
            }

            _t += dt;

            for (int i = 0; i < _flames.Count && i < _flameBase.Count; i++)
            {
                float k = 1f + (Mathf.Sin((_t * 13f) + (i * 2.1f)) * 0.16f);
                float w = 1f + (Mathf.Sin((_t * 9f) + (i * 1.3f)) * 0.08f);
                Vector3 b = _flameBase[i];
                _flames[i].localScale = new Vector3(b.x * w, b.y * k, b.z * w);
            }

            if (_fireLight != null)
            {
                _fireLight.intensity = 2.2f + (Mathf.Sin(_t * 9f) * 0.35f);
            }

            if (_previewSlot != null)
            {
                _previewSlot.localRotation = Quaternion.Euler(0f, PreviewYaw, 0f);
            }

            // The character LIVES: a slow breath (torso pulse when the rig has one, otherwise a
            // whisper of whole-body scale) and the arms swaying gently at his sides.
            if (_previewRig != null)
            {
                float sway = Mathf.Sin(_t * 1.7f) * 2.4f;
                _previewRig.SwingX(_previewRig.ArmL, -sway);
                _previewRig.SwingX(_previewRig.ArmR, sway);

                if (_previewRig.Torso != null)
                {
                    if (_torsoBaseY <= 0f) { _torsoBaseY = _previewRig.Torso.localScale.y; }
                    float breathe = 1f + (Mathf.Sin(_t * 2f) * 0.015f);
                    Vector3 s = _previewRig.Torso.localScale;
                    _previewRig.Torso.localScale = new Vector3(s.x, _torsoBaseY * breathe, s.z);
                }
                else if (_previewModel != null)
                {
                    float breathe = 1f + (Mathf.Sin(_t * 2f) * 0.008f);
                    _previewModel.transform.localScale = new Vector3(1f, breathe, 1f);
                }
            }

            Camera cam = Camera.main;
            if (cam != null)
            {
                PlaceCamera(cam);
            }
        }

        /// <summary>Destroy the stage and hand the camera back to the isometric rig.</summary>
        public void Teardown()
        {
            if (_root == null)
            {
                return;
            }

            ClearPreview();
            Object.Destroy(_root);
            _root = null;
            _flames.Clear();
            _fireLight = null;

            Camera cam = Camera.main;
            if (cam != null)
            {
                cam.orthographic = false; // hand back to the third-person chase camera
                cam.fieldOfView = 55f;
                cam.backgroundColor = new Color(0.35f, 0.55f, 0.80f); // daytime sky
            }

            if (_rig != null)
            {
                _rig.enabled = true;
            }
        }

        private void PlaceCamera(Camera cam)
        {
            // The auth panels sit on the LEFT of the screen; frame the dais on the right third.
            Vector3 eye = Origin + new Vector3(-2.6f, 2.0f, 4.6f);
            Vector3 look = Origin + new Vector3(0.9f, 1.05f, -0.6f);
            cam.transform.position = eye;
            cam.transform.rotation = Quaternion.LookRotation(look - eye, Vector3.up);
        }

        // ------------------------------------------------------------- Helpers

        /// <summary>
        /// The undergrowth pass: grass tufts, ferns, flowers, bushes and pebbles scattered
        /// through the clearing (seeded, so the campsite looks the same every launch). Uses the
        /// Nature MegaKit first, then Nature Starter Kit 2 for extra variety when imported.
        /// No-op without the kits — the primitives scene stays clean.
        /// </summary>
        private static void DressClearing(Transform r)
        {
            var rng = new System.Random(7331);
            Vector3 fire = new Vector3(2.6f, 0f, -1.6f);

            if (NatureModels.Available)
            {
                // Grass EVERYWHERE — the clearing should read as meadow, not lawn.
                for (int i = 0; i < 420; i++)
                {
                    Vector3 pos = ScatterSpot(rng, fire, 1.8f, 16f);
                    string tuft = (i % 3) == 0 ? "Grass_Wispy_Tall" : "Grass_Common_Tall";
                    NatureModels.Spawn(r, tuft, pos, 0.28f + ((float)rng.NextDouble() * 0.35f),
                        rng.Next(360));
                }

                // Clumps: real grass grows in patches — 26 clusters of 5-9 tufts.
                for (int c = 0; c < 26; c++)
                {
                    Vector3 center = ScatterSpot(rng, fire, 2.4f, 14f);
                    int n = 5 + rng.Next(5);
                    for (int i = 0; i < n; i++)
                    {
                        float a = (float)rng.NextDouble() * Mathf.PI * 2f;
                        float d = (float)rng.NextDouble() * 1.4f;
                        string plant = (i % 3) == 0 ? "Grass_Wispy_Tall" : "Grass_Common_Tall";
                        NatureModels.Spawn(r, plant,
                            center + new Vector3(Mathf.Cos(a) * d, 0f, Mathf.Sin(a) * d),
                            0.30f + ((float)rng.NextDouble() * 0.4f), rng.Next(360));
                    }
                }

                // Ferns, flowers and clover in loose patches.
                string[] cover = { "Fern_1", "Flower_3_Group", "Flower_4_Group", "Clover_1",
                    "Bush_Common", "Bush_Common_Flowers", "Plant_1_Big" };
                for (int i = 0; i < 26; i++)
                {
                    Vector3 pos = ScatterSpot(rng, fire, 2.6f, 13f);
                    NatureModels.Spawn(r, cover[rng.Next(cover.Length)], pos,
                        0.35f + ((float)rng.NextDouble() * 0.55f), rng.Next(360));
                }

                // Mossy rocks at the clearing's edge.
                for (int i = 0; i < 7; i++)
                {
                    Vector3 pos = ScatterSpot(rng, fire, 5.5f, 14f);
                    NatureModels.Spawn(r, "Rock_Medium_" + ((i % 3) + 1), pos,
                        0.5f + ((float)rng.NextDouble() * 0.8f), rng.Next(360));
                }

                // The odd mushroom by the trees — it's night, after all.
                NatureModels.Spawn(r, "Mushroom_Common", new Vector3(-4.6f, 0f, -2.2f), 0.35f, 40f);
                NatureModels.Spawn(r, "Mushroom_Common", new Vector3(5.6f, 0f, -3.9f), 0.28f, 190f);
            }

            // Nature Starter Kit 2 undergrowth on top, when the user imported it.
            if (NatureKit.Available)
            {
                for (int i = 0; i < 140; i++)
                {
                    Vector3 pos = ScatterSpot(rng, fire, 2.0f, 16f);
                    NatureKit.SpawnGrass(r, rng, pos, 0.4f + ((float)rng.NextDouble() * 0.4f));
                }

                for (int i = 0; i < 8; i++)
                {
                    Vector3 pos = ScatterSpot(rng, fire, 4f, 13f);
                    NatureKit.SpawnBush(r, rng, pos, 0.6f + ((float)rng.NextDouble() * 0.7f));
                }
            }
        }

        /// <summary>A scatter position inside the clearing, kept off the dais and the campfire.</summary>
        private static Vector3 ScatterSpot(System.Random rng, Vector3 fire, float minRadius, float maxRadius)
        {
            for (int guard = 0; guard < 24; guard++)
            {
                float a = (float)rng.NextDouble() * Mathf.PI * 2f;
                float d = minRadius + ((float)rng.NextDouble() * (maxRadius - minRadius));
                var pos = new Vector3(Mathf.Cos(a) * d, 0f, Mathf.Sin(a) * d);
                if ((pos - fire).sqrMagnitude > 1.6f)
                {
                    return pos;
                }
            }

            return new Vector3(0f, 0f, -maxRadius);
        }

        private static void KitPine(Transform r, Vector3 at, float s)
        {
            if (NatureModels.Available)
            {
                int hash = Mathf.Abs((int)((at.x * 7f) + (at.z * 13f)));
                if (NatureModels.Spawn(r, "Pine_" + ((hash % 3) + 1), at, 6.5f * s, hash % 360) != null)
                {
                    return;
                }
            }

            Pine(r, at, s);
        }

        private static void Torch(Transform r, Vector3 at)
        {
            if (PropModels.Available)
            {
                GameObject torch = PropModels.Spawn(r, "Torch_Metal", at, 1.8f, 0f);
                if (torch != null)
                {
                    var glowGo = new GameObject("TorchLight");
                    glowGo.transform.SetParent(torch.transform, false);
                    glowGo.transform.position = torch.transform.position + new Vector3(0f, 1.7f, 0f);
                    Light glow = glowGo.AddComponent<Light>();
                    glow.type = LightType.Point;
                    glow.color = new Color(1f, 0.65f, 0.3f);
                    glow.range = 6f;
                    glow.intensity = 1.2f;
                    return;
                }
            }

            Block(r, "TorchPole", at + new Vector3(0f, 0.7f, 0f), new Vector3(0.12f, 1.4f, 0.12f),
                new Color(0.24f, 0.16f, 0.09f));
            GameObject flame = Block(r, "TorchFlame", at + new Vector3(0f, 1.55f, 0f),
                new Vector3(0.20f, 0.28f, 0.20f), new Color(1f, 0.62f, 0.12f));

            var lightGo = new GameObject("TorchLight");
            lightGo.transform.SetParent(flame.transform, false);
            Light l = lightGo.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = new Color(1f, 0.65f, 0.3f);
            l.range = 6f;
            l.intensity = 1.2f;
        }

        private static void Pine(Transform r, Vector3 at, float s)
        {
            GameObject trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            trunk.name = "Trunk";
            Object.Destroy(trunk.GetComponent<Collider>());
            trunk.transform.SetParent(r, false);
            trunk.transform.localPosition = at + new Vector3(0f, 0.5f * s, 0f);
            trunk.transform.localScale = new Vector3(0.24f * s, 0.5f * s, 0.24f * s);
            Tint(trunk, new Color(0.28f, 0.18f, 0.10f));

            Color needles = new Color(0.10f, 0.30f, 0.16f);
            PineTier(r, at + new Vector3(0f, 1.25f * s, 0f), new Vector3(1.6f * s, 0.85f * s, 1.6f * s), needles);
            PineTier(r, at + new Vector3(0f, 1.9f * s, 0f), new Vector3(1.1f * s, 0.7f * s, 1.1f * s), needles);
            PineTier(r, at + new Vector3(0f, 2.45f * s, 0f), new Vector3(0.65f * s, 0.6f * s, 0.65f * s), needles);
        }

        private static void PineTier(Transform r, Vector3 pos, Vector3 scale, Color color)
        {
            GameObject tier = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            tier.name = "Pine";
            Object.Destroy(tier.GetComponent<Collider>());
            tier.transform.SetParent(r, false);
            tier.transform.localPosition = pos;
            tier.transform.localScale = scale;
            Tint(tier, color);
        }

        private static void Mountain(Transform r, Vector3 at, float s)
        {
            GameObject m = Block(r, "Mountain", at + new Vector3(0f, s * 0.35f, 0f),
                new Vector3(s, s * 0.9f, s), new Color(0.16f, 0.17f, 0.22f));
            Tex.Apply(m, "stone", 4f, 4f, new Color(0.30f, 0.32f, 0.40f));
            m.transform.localRotation = Quaternion.Euler(0f, 45f, 0f);
        }

        private Transform FlameOrb(Transform r, Vector3 pos, Vector3 scale, Color color)
        {
            GameObject orb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            orb.name = "Flame";
            Object.Destroy(orb.GetComponent<Collider>());
            orb.transform.SetParent(r, false);
            orb.transform.localPosition = pos;
            orb.transform.localScale = scale;
            Tint(orb, color);
            _flameBase.Add(scale);
            return orb.transform;
        }

        private static GameObject Block(Transform parent, string name, Vector3 pos, Vector3 scale, Color color)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            Object.Destroy(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            Tint(go, color);
            return go;
        }

        private static void Tint(GameObject go, Color color)
        {
            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = color;
            }
        }
    }
}
