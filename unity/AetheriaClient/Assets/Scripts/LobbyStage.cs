using Aetheria.Shared.Combat;
using Aetheria.Shared.Math;
using Aetheria.Shared.Protocol;
using UnityEngine;

namespace Aetheria.UnityClient
{
    /// <summary>
    /// The 3D backdrop behind the login / server-browser / character-creation screens: a night
    /// campsite (fire, torches, pines, mountains, moon) built entirely from primitives, far away
    /// from the world origin so it never collides with in-game content. Also hosts the live 3D
    /// PREVIEW of the character being created, standing on a stone dais and slowly turning.
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

            // Night ground.
            Block(r, "Ground", new Vector3(0f, -0.1f, 0f), new Vector3(34f, 0.2f, 34f),
                new Color(0.14f, 0.19f, 0.14f));

            // Stone dais where the preview character stands.
            Block(r, "Dais", new Vector3(0f, 0.08f, 0f), new Vector3(2.6f, 0.16f, 2.6f),
                new Color(0.42f, 0.42f, 0.46f));
            Block(r, "DaisTrim", new Vector3(0f, 0.19f, 0f), new Vector3(2.0f, 0.06f, 2.0f),
                new Color(0.50f, 0.50f, 0.55f));

            _previewSlot = new GameObject("PreviewSlot").transform;
            _previewSlot.SetParent(r, false);
            _previewSlot.localPosition = new Vector3(0f, 0.22f, 0f);

            // Campfire to the side: stone ring, logs, animated flames, warm light.
            Vector3 fire = new Vector3(2.6f, 0f, -1.6f);
            for (int i = 0; i < 6; i++)
            {
                float a = i * Mathf.PI * 2f / 6f;
                Block(r, "FireStone", fire + new Vector3(Mathf.Cos(a) * 0.55f, 0.08f, Mathf.Sin(a) * 0.55f),
                    new Vector3(0.22f, 0.16f, 0.22f), new Color(0.35f, 0.34f, 0.36f));
            }

            GameObject log1 = Block(r, "Log", fire + new Vector3(0f, 0.10f, 0f), new Vector3(0.12f, 0.10f, 0.7f),
                new Color(0.30f, 0.19f, 0.10f));
            log1.transform.localRotation = Quaternion.Euler(0f, 35f, 0f);
            GameObject log2 = Block(r, "Log", fire + new Vector3(0f, 0.10f, 0f), new Vector3(0.12f, 0.10f, 0.7f),
                new Color(0.28f, 0.17f, 0.09f));
            log2.transform.localRotation = Quaternion.Euler(0f, -40f, 0f);

            _flames.Clear();
            _flames.Add(Block(r, "Flame", fire + new Vector3(0f, 0.35f, 0f), new Vector3(0.28f, 0.5f, 0.28f),
                new Color(1f, 0.55f, 0.10f)).transform);
            _flames.Add(Block(r, "FlameIn", fire + new Vector3(0f, 0.45f, 0f), new Vector3(0.14f, 0.34f, 0.14f),
                new Color(1f, 0.85f, 0.25f)).transform);

            var lightGo = new GameObject("FireLight");
            lightGo.transform.SetParent(r, false);
            lightGo.transform.localPosition = fire + new Vector3(0f, 0.8f, 0f);
            _fireLight = lightGo.AddComponent<Light>();
            _fireLight.type = LightType.Point;
            _fireLight.color = new Color(1f, 0.62f, 0.25f);
            _fireLight.range = 12f;
            _fireLight.intensity = 2.2f;

            // Torches framing the dais.
            Torch(r, new Vector3(-2.2f, 0f, 1.6f));
            Torch(r, new Vector3(2.2f, 0f, 1.6f));

            // A few pines around the clearing.
            Pine(r, new Vector3(-5.5f, 0f, -3f), 1.0f);
            Pine(r, new Vector3(-7f, 0f, 2.5f), 1.3f);
            Pine(r, new Vector3(6.5f, 0f, 1.5f), 1.1f);
            Pine(r, new Vector3(5f, 0f, -5f), 0.9f);
            Pine(r, new Vector3(-3f, 0f, -6.5f), 1.2f);

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

        /// <summary>Animate flames and the slowly turning preview; keep the camera in place.</summary>
        public void Tick(float dt)
        {
            if (_root == null)
            {
                return;
            }

            _t += dt;

            for (int i = 0; i < _flames.Count; i++)
            {
                float k = 1f + (Mathf.Sin((_t * 13f) + (i * 2.1f)) * 0.18f);
                Vector3 s = _flames[i].localScale;
                _flames[i].localScale = new Vector3(s.x, 0.5f * k * (i == 0 ? 1f : 0.68f), s.z);
            }

            if (_fireLight != null)
            {
                _fireLight.intensity = 2.2f + (Mathf.Sin(_t * 9f) * 0.35f);
            }

            if (_previewSlot != null)
            {
                _previewSlot.localRotation = Quaternion.Euler(0f, _t * 24f, 0f);
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

        private static void Torch(Transform r, Vector3 at)
        {
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
            m.transform.localRotation = Quaternion.Euler(0f, 45f, 0f);
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
