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

            // The pack's models face the viewer (-Z); our characters face +Z. Without this the
            // whole world seems to moonwalk.
            root.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);

            // What you WEAR is what you see: an empty slot shows SKIN (the base piece tinted to
            // the character's skin tone) — naked but decent: underwear is added further down.
            bool bareChest = snapshot.EquippedIn(EquipSlot.Chest) == 0;
            bool bareHands = snapshot.EquippedIn(EquipSlot.Hands) == 0;
            bool bareLegs = snapshot.EquippedIn(EquipSlot.Legs) == 0;
            bool bareFeet = snapshot.EquippedIn(EquipSlot.Feet) == 0;
            Color skin = CharacterModelBuilder.SkinColor(snapshot.RaceId, snapshot.Appearance.SkinTone);

            // MASTER piece: the body (chest slot decides bare skin vs Ranger armor).
            GameObject master = Spawn(root.transform, g + (bareChest ? "Peasant_Body" : "Ranger_Body"));
            if (master == null)
            {
                Object.Destroy(root);
                return null;
            }

            if (bareChest) { MakeSkin(master, skin); }

            Dictionary<string, Transform> bones = CollectBones(master.transform);

            // The other modular pieces, re-bound onto the master skeleton.
            GameObject arms = Attach(root.transform, bones, g + (bareHands ? "Peasant_Arms" : "Ranger_Arms"));
            if (bareHands && arms != null) { MakeSkin(arms, skin); }
            GameObject legs = Attach(root.transform, bones, g + (bareLegs ? "Peasant_Legs" : "Ranger_Legs"));
            if (bareLegs && legs != null) { MakeSkin(legs, skin); }
            GameObject feet = Attach(root.transform, bones, g + (bareFeet
                ? "Peasant_Feet" : (female ? "Ranger_Feet" : "Ranger_Feet_Boots")));
            if (bareFeet && feet != null) { MakeSkin(feet, skin); }

            // Modesty layer: plain underwear over bare hips — and a band over a bare female chest.
            if (bareLegs)
            {
                AttachBand(bones, "pelvis", new Vector3(0.34f, 0.16f, 0.26f), new Vector3(0f, 0.02f, 0f));
            }

            if (female && bareChest)
            {
                AttachBand(bones, "spine_03", new Vector3(0.32f, 0.10f, 0.24f), new Vector3(0f, 0.04f, 0.01f));
            }

            if (snapshot.EquippedIn(EquipSlot.Head) != 0)
            {
                Attach(root.transform, bones, g + "Ranger_Head_Hood");
            }

            if (snapshot.EquippedIn(EquipSlot.Shoulders) != 0)
            {
                Attach(root.transform, bones, g + (female ? "Ranger_Acc_Pauldrons" : "Ranger_Acc_Pauldron"));
            }

            // The REAL head (Universal Base Characters kit): face, eyes and brows re-bound onto
            // the outfit skeleton — the rest of the base body is pruned to avoid clipping.
            Transform headBone = FindBone(bones, "head", "Head", "neck_01");
            bool hasRealHead = AttachBaseHead(root.transform, bones, female, headBone);
            if (!hasRealHead && headBone != null && snapshot.EquippedIn(EquipSlot.Head) == 0)
            {
                // Kit not imported: fall back to the stylised procedural head.
                BuildPlaceholderHead(headBone, root.transform, snapshot);
            }

            // Hair (skipped under a hood) and beard, from the same kit.
            if (hasRealHead)
            {
                if (snapshot.EquippedIn(EquipSlot.Head) == 0)
                {
                    string hair = HairFor(snapshot.Appearance.HairStyle, female);
                    if (hair != null) { Attach(root.transform, bones, hair); }
                }

                if (snapshot.Appearance.BeardStyle > 0)
                {
                    Attach(root.transform, bones, "Hair_Beard");
                }
            }

            // Textures: outfit cloth, bare skin (tone-aware), eyes, tinted hair — per material.
            ApplyTextures(root, female, snapshot.Appearance);

            // Normalise to the world's character height (packs export at their own scale).
            float height = MeasureHeight(root);
            if (height > 0.05f)
            {
                float k = TargetHeight / height;
                root.transform.localScale = new Vector3(k, k, k);
            }

            // Hand over the bones to the shared animation rig. The reference frame is the
            // PARENT (whose +Z is the character's facing), not the 180°-flipped model root —
            // weapons and attack swings point where the character looks.
            var rig = new ModelRig
            {
                BoneRoot = parent,
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

            // The pack binds with raised arms: point each upper arm DOWN and slightly outward,
            // measured from where the limb actually is — immune to the export's axis conventions.
            LowerArm(rig, rig.ArmL, FindBone(bones, "lowerarm_l"), root.transform);
            LowerArm(rig, rig.ArmR, FindBone(bones, "lowerarm_r"), root.transform);
            return rig;
        }

        /// <summary>Fold one arm down to a natural stance (out to which side is measured, not assumed).</summary>
        private static void LowerArm(ModelRig rig, Transform upperArm, Transform lowerArm, Transform root)
        {
            if (upperArm == null || lowerArm == null)
            {
                return;
            }

            float side = upperArm.position.x - root.position.x >= 0f ? 1f : -1f;
            rig.PoseTowards(upperArm, lowerArm, new Vector3(side * 0.22f, -1f, 0f));
        }

        /// <summary>Hairstyle resource for our creation-screen styles (0 court, 1 long, 2 iroquois, 3 chauve).</summary>
        private static string HairFor(int style, bool female)
        {
            switch (style)
            {
                case 0: return female ? "Hair_Buns" : "Hair_SimpleParted";
                case 1: return "Hair_Long";
                case 2: return female ? "Hair_BuzzedFemale" : "Hair_Buzzed";
                default: return null; // chauve
            }
        }

        /// <summary>
        /// Instantiate the base body and keep ONLY its head: eyes and brows are separate meshes,
        /// but the face SKIN lives inside the full-body mesh — so that one goes through triangle
        /// surgery, keeping only the triangles skinned to the head bone and its descendants
        /// (pack guidance: "only the head is required"; the outfit replaces the body).
        /// Returns false when the Universal Base Characters kit is not imported.
        /// </summary>
        private static bool AttachBaseHead(Transform root, Dictionary<string, Transform> masterBones,
            bool female, Transform headBone)
        {
            string name = female ? "Superhero_Female_FullBody" : "Superhero_Male_FullBody";
            GameObject spawned = Attach(root, masterBones, name);
            if (spawned == null)
            {
                return false;
            }

            foreach (SkinnedMeshRenderer smr in spawned.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.gameObject.name.Contains("Eye"))
                {
                    continue; // Eyes / Eyebrows: already head-only meshes
                }

                PruneToHead(smr, headBone);
            }

            return true;
        }

        /// <summary>
        /// Rebuild a skinned mesh keeping only the triangles whose vertices follow the head bone
        /// (or its grafted facial children) — runtime scissors for the body-with-head mesh.
        /// </summary>
        private static void PruneToHead(SkinnedMeshRenderer smr, Transform headBone)
        {
            try
            {
                PruneToHeadUnsafe(smr, headBone);
            }
            catch (System.Exception e)
            {
                // Typically "isReadable is false" before the editor import fixer has run:
                // drop the body mesh rather than spamming errors every spawn.
                Debug.LogWarning("[Aetheria] Découpe de tête impossible (" + e.Message +
                                 ") — réimporte Assets/Resources/Quaternius (Read/Write).");
                Object.Destroy(smr.gameObject);
            }
        }

        private static void PruneToHeadUnsafe(SkinnedMeshRenderer smr, Transform headBone)
        {
            Mesh src = smr.sharedMesh;
            if (src == null || headBone == null)
            {
                Object.Destroy(smr.gameObject);
                return;
            }

            Transform[] bones = smr.bones;
            bool[] headBoneIndex = new bool[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                headBoneIndex[i] = bones[i] != null && (bones[i] == headBone || bones[i].IsChildOf(headBone));
            }

            BoneWeight[] weights = src.boneWeights;
            if (weights == null || weights.Length == 0)
            {
                Object.Destroy(smr.gameObject);
                return;
            }

            // A vertex belongs to the head when its HEAVIEST bone is the head (or under it).
            bool[] headVertex = new bool[weights.Length];
            for (int v = 0; v < weights.Length; v++)
            {
                BoneWeight w = weights[v];
                int dominant = w.boneIndex0;
                float best = w.weight0;
                if (w.weight1 > best) { best = w.weight1; dominant = w.boneIndex1; }
                if (w.weight2 > best) { best = w.weight2; dominant = w.boneIndex2; }
                if (w.weight3 > best) { dominant = w.boneIndex3; }

                headVertex[v] = dominant >= 0 && dominant < headBoneIndex.Length && headBoneIndex[dominant];
            }

            Mesh copy = Object.Instantiate(src);
            int keptTotal = 0;
            for (int s = 0; s < src.subMeshCount; s++)
            {
                int[] tris = src.GetTriangles(s);
                var kept = new List<int>(tris.Length);
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    if (headVertex[tris[t]] && headVertex[tris[t + 1]] && headVertex[tris[t + 2]])
                    {
                        kept.Add(tris[t]);
                        kept.Add(tris[t + 1]);
                        kept.Add(tris[t + 2]);
                    }
                }

                copy.SetTriangles(kept, s);
                keptTotal += kept.Count;
            }

            if (keptTotal == 0)
            {
                Object.Destroy(smr.gameObject); // nothing head-bound in this mesh at all
                return;
            }

            smr.sharedMesh = copy;
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

        /// <summary>Strip a clothing piece down to flat SKIN colour (textures off).</summary>
        private static void MakeSkin(GameObject piece, Color skin)
        {
            foreach (Renderer r in piece.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material m in r.materials)
                {
                    if (m == null) { continue; }
                    m.mainTexture = null;
                    m.color = skin;
                }
            }
        }

        /// <summary>A plain dark band (underwear) glued to a bone, scale-compensated.</summary>
        private static void AttachBand(Dictionary<string, Transform> bones, string boneName,
            Vector3 worldSize, Vector3 offset)
        {
            Transform bone = FindBone(bones, boneName, "pelvis", "spine_02", "spine_01");
            if (bone == null) { return; }

            GameObject band = GameObject.CreatePrimitive(PrimitiveType.Cube);
            band.name = "Underwear";
            Object.Destroy(band.GetComponent<Collider>());
            band.transform.SetParent(bone, false);
            band.transform.localPosition = offset;

            // The pack skeleton carries import scaling: compensate so the band's WORLD size holds.
            Vector3 ls = bone.lossyScale;
            band.transform.localScale = new Vector3(
                worldSize.x / Mathf.Max(0.0001f, Mathf.Abs(ls.x)),
                worldSize.y / Mathf.Max(0.0001f, Mathf.Abs(ls.y)),
                worldSize.z / Mathf.Max(0.0001f, Mathf.Abs(ls.z)));
            band.GetComponent<Renderer>().material.color = new Color(0.28f, 0.28f, 0.32f);
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
        private static GameObject Attach(Transform root, Dictionary<string, Transform> masterBones, string name)
        {
            GameObject piece = Spawn(root, name);
            if (piece == null)
            {
                return null;
            }

            // GRAFT bones the master skeleton lacks (facial/eye bones on the base head, etc.):
            // move them under their master parent so the meshes that need them keep working.
            foreach (Transform t in piece.GetComponentsInChildren<Transform>(true))
            {
                Transform masterParent;
                if (t != null && t.parent != null && !masterBones.ContainsKey(t.name) &&
                    t.GetComponent<SkinnedMeshRenderer>() == null && !t.name.Contains("Armature") &&
                    masterBones.TryGetValue(t.parent.name, out masterParent))
                {
                    t.SetParent(masterParent, false);
                    masterBones[t.name] = t;
                }
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

            return piece;
        }

        // ------------------------------------------------------------- Look

        private static void ApplyTextures(GameObject root, bool female, Aetheria.Shared.Combat.Appearance look)
        {
            bool variant = look.Face % 2 != 0; // outfit recolour so not everyone wears the same dye
            bool darkSkin = look.SkinTone >= 2; // palette tones 0-1 light, 2-3 dark

            Texture2D peasant = LoadTexture(variant ? "T_Peasant_2_BaseColor" : "T_Peasant_BaseColor");
            Texture2D ranger = LoadTexture(variant ? "T_Ranger_3_BaseColor" : "T_Ranger_BaseColor");
            Texture2D outfitSkin = LoadTexture(female ? "T_Regular_Female_Dark_BaseColor" : "T_Regular_Male_Dark_BaseColor");
            Texture2D bodySkin = LoadTexture("Skin_" + (female ? "Female_" : "Male_") + (darkSkin ? "Dark" : "Light"));
            Texture2D eyes = LoadTexture("T_Eye_Brown");
            Texture2D hair1 = LoadTexture("T_Hair_1_BaseColor");
            Texture2D hair2 = LoadTexture("T_Hair_2_BaseColor");

            Color hairColor = CharacterModelBuilder.HairColors[
                Mathf.Clamp(look.HairColor, 0, CharacterModelBuilder.HairColors.Length - 1)];
            Color beardColor = CharacterModelBuilder.HairColors[
                Mathf.Clamp(look.BeardColor, 0, CharacterModelBuilder.HairColors.Length - 1)];

            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                bool beardMesh = r.gameObject.name.Contains("Beard");
                bool hairMesh = beardMesh || r.gameObject.name.Contains("Hair") ||
                                r.gameObject.name.Contains("Eyebrow");

                foreach (Material m in r.materials)
                {
                    if (m == null) { continue; }

                    string n = m.name;
                    m.color = Color.white;

                    if (n.Contains("Hair") || hairMesh)
                    {
                        m.mainTexture = n.Contains("Hair_2") ? hair2 : hair1;
                        m.color = beardMesh ? beardColor : hairColor; // dyed by the customisation
                    }
                    else if (n.Contains("Eye")) { m.mainTexture = eyes; }        // eyeballs
                    else if (n.Contains("uperhero")) { m.mainTexture = bodySkin; } // the real head
                    else if (n.Contains("egular")) { m.mainTexture = outfitSkin; } // bare arms…
                    else if (n.Contains("anger")) { m.mainTexture = ranger; }      // ranger cloth
                    else if (n.Contains("easant")) { m.mainTexture = peasant; }
                    else { m.mainTexture = r.name.Contains("Ranger") ? ranger : peasant; }
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
            mount.transform.rotation = root.parent != null ? root.parent.rotation : root.rotation;

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
