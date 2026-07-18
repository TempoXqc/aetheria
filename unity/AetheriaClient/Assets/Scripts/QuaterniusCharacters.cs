using System.Collections.Generic;
using Aetheria.Shared.Items;
using Aetheria.Shared.Protocol;
using UnityEngine;

namespace Aetheria.UnityClient
{
    /// <summary>
    /// Player characters assembled from the Quaternius "Modular Character Outfits - Fantasy" pack
    /// (CC0, dropped under Assets/Resources/Quaternius). Each character is built from MODULAR
    /// pieces that mirror the equipment slots — bare Peasant clothes by default, Ranger gear on
    /// the pieces you actually wear (chest → body, hands → arms, legs, feet, head → hood,
    /// shoulders → pauldrons) — so your LOOT is your look, now with real meshes.
    ///
    /// All pieces share ONE skeleton: the body piece is the master; every other piece's skinned
    /// mesh is re-bound onto the master's bones by name. The bones (upperarm/thigh/head) are then
    /// handed to the existing <see cref="ModelRig"/> pipeline, which swings them in root space —
    /// the same walk/attack/idle animation code drives FBX bones and procedural pivots alike.
    ///
    /// The pack ships no heads ("works with the Universal Base Character kit"), so a stylised
    /// procedural head — honouring the character's skin tone, face and hair customisation — is
    /// mounted on the head bone until that kit is dropped in as well.
    /// </summary>
    public static class QuaterniusCharacters
    {
        private const string Root = "Quaternius/";
        private const float TargetHeight = 1.95f; // world units, matches the procedural bodies

        private static readonly Dictionary<string, GameObject> ModelCache = new Dictionary<string, GameObject>();
        private static readonly Dictionary<string, Texture2D> TextureCache = new Dictionary<string, Texture2D>();
        private static bool _checked;
        private static bool _available;

        /// <summary>True once the pack's assets are imported (checked once, cached).</summary>
        public static bool Available
        {
            get
            {
                if (!_checked)
                {
                    _checked = true;
                    _available = Load("Male_Peasant_Body") != null;
                }

                return _available;
            }
        }

        /// <summary>Build a fully dressed character under <paramref name="parent"/>.</summary>
        public static ModelRig Create(Transform parent, EntitySnapshot snapshot)
        {
            string g = snapshot.Gender == Aetheria.Shared.Combat.Gender.Female ? "Female_" : "Male_";
            bool female = snapshot.Gender == Aetheria.Shared.Combat.Gender.Female;

            var root = new GameObject("Quaternius");
            root.transform.SetParent(parent, false);

            // MASTER piece: the body (chest slot decides Peasant cloth vs Ranger armor).
            GameObject master = Spawn(root.transform,
                g + (snapshot.EquippedIn(EquipSlot.Chest) != 0 ? "Ranger_Body" : "Peasant_Body"));
            if (master == null)
            {
                Object.Destroy(root);
                return null;
            }

            Dictionary<string, Transform> bones = CollectBones(master.transform);

            // The other modular pieces, re-bound onto the master skeleton.
            Attach(root.transform, bones, g + (snapshot.EquippedIn(EquipSlot.Hands) != 0 ? "Ranger_Arms" : "Peasant_Arms"));
            Attach(root.transform, bones, g + (snapshot.EquippedIn(EquipSlot.Legs) != 0 ? "Ranger_Legs" : "Peasant_Legs"));
            Attach(root.transform, bones, g + (snapshot.EquippedIn(EquipSlot.Feet) != 0
                ? (female ? "Ranger_Feet" : "Ranger_Feet_Boots") : "Peasant_Feet"));

            if (snapshot.EquippedIn(EquipSlot.Head) != 0)
            {
                Attach(root.transform, bones, g + "Ranger_Head_Hood");
            }

            if (snapshot.EquippedIn(EquipSlot.Shoulders) != 0)
            {
                Attach(root.transform, bones, g + (female ? "Ranger_Acc_Pauldrons" : "Ranger_Acc_Pauldron"));
            }

            // Textures: each material keeps its own map (outfit cloth vs bare skin), with an
            // appearance-driven recolour variant so not every adventurer wears the same dye.
            ApplyTextures(root, female, variant: snapshot.Appearance.Face % 2 != 0);

            // Normalise to the world's character height (packs export at their own scale).
            float height = MeasureHeight(root);
            if (height > 0.05f)
            {
                float k = TargetHeight / height;
                root.transform.localScale = new Vector3(k, k, k);
            }

            // Head: the pack ships none — mount the stylised custom head on the head bone.
            Transform headBone = FindBone(bones, "head", "Head", "neck_01");
            if (headBone != null && snapshot.EquippedIn(EquipSlot.Head) == 0)
            {
                BuildPlaceholderHead(headBone, root.transform, snapshot);
            }

            // Hand over the bones to the shared animation rig.
            var rig = new ModelRig
            {
                BoneRoot = root.transform,
                ArmL = FindBone(bones, "upperarm_l"),
                ArmR = FindBone(bones, "upperarm_r"),
                LegL = FindBone(bones, "thigh_l", "calf_l"),
                LegR = FindBone(bones, "thigh_r", "calf_r"),
                Head = headBone,
                HandL = FindBone(bones, "hand_l"),
                HandR = FindBone(bones, "hand_r"),
                HeadHeight = TargetHeight + 0.25f,
            };

            rig.CaptureRestPose();
            return rig;
        }

        // ------------------------------------------------------------- Assembly

        private static GameObject Load(string name)
        {
            GameObject cached;
            if (!ModelCache.TryGetValue(name, out cached))
            {
                cached = Resources.Load<GameObject>(Root + name);
                ModelCache[name] = cached;
            }

            return cached;
        }

        private static GameObject Spawn(Transform parent, string name)
        {
            GameObject prefab = Load(name);
            if (prefab == null)
            {
                return null;
            }

            GameObject go = Object.Instantiate(prefab, parent, false);
            go.name = name;
            return go;
        }

        /// <summary>Every transform under the master, by name — the shared skeleton lookup.</summary>
        private static Dictionary<string, Transform> CollectBones(Transform master)
        {
            var bones = new Dictionary<string, Transform>();
            foreach (Transform t in master.GetComponentsInChildren<Transform>(true))
            {
                if (!bones.ContainsKey(t.name))
                {
                    bones[t.name] = t;
                }
            }

            return bones;
        }

        private static Transform FindBone(Dictionary<string, Transform> bones, params string[] names)
        {
            foreach (string name in names)
            {
                Transform t;
                if (bones.TryGetValue(name, out t))
                {
                    return t;
                }
            }

            return null;
        }

        /// <summary>
        /// Instantiate a clothing piece and re-bind its skinned meshes onto the master skeleton
        /// (bone-by-name), then drop its own duplicate armature.
        /// </summary>
        private static void Attach(Transform root, Dictionary<string, Transform> masterBones, string name)
        {
            GameObject piece = Spawn(root, name);
            if (piece == null)
            {
                return;
            }

            foreach (SkinnedMeshRenderer smr in piece.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Transform[] mapped = new Transform[smr.bones.Length];
                for (int i = 0; i < mapped.Length; i++)
                {
                    Transform target;
                    mapped[i] = smr.bones[i] != null && masterBones.TryGetValue(smr.bones[i].name, out target)
                        ? target
                        : smr.bones[i];
                }

                smr.bones = mapped;
                if (smr.rootBone != null)
                {
                    Transform newRoot;
                    if (masterBones.TryGetValue(smr.rootBone.name, out newRoot))
                    {
                        smr.rootBone = newRoot;
                    }
                }

                smr.updateWhenOffscreen = true; // bounds were authored for the dropped armature
            }

            // The piece's own armature is now dead weight — every mesh points at the master's.
            foreach (Transform child in piece.GetComponentsInChildren<Transform>(true))
            {
                if (child != null && child.parent == piece.transform && child.name.Contains("Armature") &&
                    child.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length == 0)
                {
                    Object.Destroy(child.gameObject);
                }
            }
        }

        // ------------------------------------------------------------- Look

        private static void ApplyTextures(GameObject root, bool female, bool variant)
        {
            Texture2D peasant = LoadTexture(variant ? "T_Peasant_2_BaseColor" : "T_Peasant_BaseColor");
            Texture2D ranger = LoadTexture(variant ? "T_Ranger_3_BaseColor" : "T_Ranger_BaseColor");
            Texture2D skin = LoadTexture(female ? "T_Regular_Female_Dark_BaseColor" : "T_Regular_Male_Dark_BaseColor");

            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material m in r.materials)
                {
                    string n = m != null ? m.name : "";
                    if (m == null) { continue; }

                    if (n.Contains("egular")) { m.mainTexture = skin; }        // bare skin (arms…)
                    else if (n.Contains("anger")) { m.mainTexture = ranger; }  // ranger outfit cloth
                    else if (n.Contains("easant")) { m.mainTexture = peasant; }
                    else { m.mainTexture = r.name.Contains("Ranger") ? ranger : peasant; }
                    m.color = Color.white;
                }
            }
        }

        private static Texture2D LoadTexture(string name)
        {
            Texture2D cached;
            if (!TextureCache.TryGetValue(name, out cached))
            {
                cached = Resources.Load<Texture2D>(Root + name);
                TextureCache[name] = cached;
            }

            return cached;
        }

        /// <summary>
        /// The pack has no heads (they live in the Universal Base Character kit) — build the
        /// stylised customised head on the head bone so faces, hair and beards keep working.
        /// The mount is re-aligned to the CHARACTER's axes and re-scaled to world units, because
        /// FBX bone axes and import scale are conventions of the pack, not of our primitives.
        /// </summary>
        private static void BuildPlaceholderHead(Transform headBone, Transform root, EntitySnapshot snapshot)
        {
            var mount = new GameObject("HeadMount");
            mount.transform.SetParent(headBone, false);
            mount.transform.rotation = root.rotation;

            Vector3 ls = mount.transform.lossyScale;
            mount.transform.localScale = new Vector3(
                ls.x > 0.0001f ? 0.85f / ls.x : 1f,
                ls.y > 0.0001f ? 0.85f / ls.y : 1f,
                ls.z > 0.0001f ? 0.85f / ls.z : 1f);
            mount.transform.localPosition = Vector3.zero;

            CharacterModelBuilder.BuildHeadOn(mount.transform, snapshot.RaceId, snapshot.Gender, snapshot.Appearance);
        }

        private static float MeasureHeight(GameObject root)
        {
            float top = 0f;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                float y = r.bounds.max.y - root.transform.position.y;
                if (y > top) { top = y; }
            }

            return top;
        }
    }
}
