using Aetheria.Shared.Combat;
using Aetheria.Shared.Items;
using Aetheria.Shared.Protocol;
using UnityEngine;

namespace Aetheria.UnityClient
{
    /// <summary>
    /// The moving parts of a procedurally built model. <see cref="EntityView"/> drives these
    /// pivots every frame to animate walking, attacking and idling — no imported animations,
    /// no asset files, everything is assembled from Unity primitives at runtime.
    /// </summary>
    public sealed class ModelRig
    {
        /// <summary>Shoulder pivots (humanoids) or front-leg pivots (quadrupeds). May be null.</summary>
        public Transform ArmL;
        public Transform ArmR;

        /// <summary>Hip pivots (humanoids) or hind-leg pivots (quadrupeds). May be null.</summary>
        public Transform LegL;
        public Transform LegR;

        /// <summary>Head pivot (bite/nod animations). May be null for corpses.</summary>
        public Transform Head;

        /// <summary>Torso block, used for the idle "breathing" scale. May be null.</summary>
        public Transform Torso;

        /// <summary>Tail pivot for quadrupeds (wag animation). Null otherwise.</summary>
        public Transform Tail;

        /// <summary>Hand bones (skinned FBX rigs) — weapons mount here when present.</summary>
        public Transform HandL;
        public Transform HandR;

        /// <summary>Renderers tinted by relation (self / ally / enemy) — the character's tunic.</summary>
        public Renderer[] TintTargets = new Renderer[0];

        /// <summary>World-space height (before root scaling) where nameplates should anchor.</summary>
        public float HeadHeight = 2.1f;

        /// <summary>Quadrupeds swing legs in diagonal pairs and "bite" instead of arm-swinging.</summary>
        public bool Quadruped;

        /// <summary>
        /// Non-null for skinned FBX rigs (Quaternius/Mixamo bones): swings then compose with the
        /// captured bind pose AROUND THE CHARACTER'S OWN AXES, so animation code never needs to
        /// know each bone's local axis convention. Null for procedural rigs (pivots start at
        /// identity, plain local rotation is enough).
        /// </summary>
        public Transform BoneRoot;

        private readonly System.Collections.Generic.Dictionary<Transform, Quaternion> _rest =
            new System.Collections.Generic.Dictionary<Transform, Quaternion>();

        /// <summary>Record every animated bone's bind rotation relative to the root (bone rigs).</summary>
        public void CaptureRestPose()
        {
            _rest.Clear();
            if (BoneRoot == null) { return; }

            foreach (Transform t in new[] { ArmL, ArmR, LegL, LegR, Head })
            {
                if (t != null)
                {
                    _rest[t] = Quaternion.Inverse(BoneRoot.rotation) * t.rotation;
                }
            }
        }

        /// <summary>
        /// Re-aim a limb so the segment from <paramref name="pivot"/> to <paramref name="child"/>
        /// points along a WORLD direction, folding the fix into the captured rest. Convention-proof:
        /// it measures where the limb actually points instead of assuming the pack's bind pose or
        /// facing, so "lower the arms" works whatever the export's axes were.
        /// </summary>
        public void PoseTowards(Transform pivot, Transform child, Vector3 worldDirection)
        {
            if (pivot == null || child == null || BoneRoot == null || !_rest.ContainsKey(pivot))
            {
                return;
            }

            Vector3 dir = child.position - pivot.position;
            if (dir.sqrMagnitude < 0.000001f)
            {
                return;
            }

            pivot.rotation = Quaternion.FromToRotation(dir.normalized, worldDirection.normalized) * pivot.rotation;
            _rest[pivot] = Quaternion.Inverse(BoneRoot.rotation) * pivot.rotation;
        }

        /// <summary>Swing a pivot by X degrees around the character's sideways axis.</summary>
        public void SwingX(Transform pivot, float degrees)
        {
            if (pivot == null) { return; }

            if (BoneRoot == null)
            {
                pivot.localRotation = Quaternion.Euler(degrees, 0f, 0f);
                return;
            }

            Quaternion rest;
            if (_rest.TryGetValue(pivot, out rest))
            {
                pivot.rotation = BoneRoot.rotation * Quaternion.Euler(degrees, 0f, 0f) * rest;
            }
        }
    }

    /// <summary>
    /// Builds low-poly character, monster and corpse models out of Unity primitives, entirely in
    /// code. Races have distinct silhouettes (Dwarves are short and wide, Orcs bulky and green,
    /// Elves slim with pointed ears), gender tweaks the build, and the class decides the weapon
    /// held in hand (sword / focus orb / bow). Monsters map from their definition id.
    /// </summary>
    public static class CharacterModelBuilder
    {
        // Race ids (mirror the server data): 1 Human, 2 Orc, 3 Elf, 4 Dwarf.
        // Class ids: 1 Warrior, 2 Mage, 3 Ranger.
        // Monster def ids: 1 Goblin Grunt, 2 Dire Wolf, 3 Goblin King, 4 Ashmaw.

        private struct BodyParams
        {
            public float Height;      // whole-model Y multiplier
            public float Width;       // whole-model X/Z multiplier
            public Color Skin;
            public Color Hair;
            public float HeadScale;   // 1 = normal; goblins have big heads
            public bool Beard;        // default beard for non-customised humanoids (monsters)
            public bool Tusks;
            public int Ears;          // 0 none, 1 pointed, 2 large
            public bool Crown;
            public Color? Tunic;      // fixed tunic colour (monsters); null = relation-tinted
            public bool UseCustom;    // players: apply the Appearance palettes below
        }

        /// <summary>Hair/beard colour palette (indexes match Appearance.HairColor/BeardColor).</summary>
        public static readonly Color[] HairColors =
        {
            new Color(0.30f, 0.20f, 0.12f), // brun
            new Color(0.08f, 0.08f, 0.08f), // noir
            new Color(0.85f, 0.75f, 0.45f), // blond
            new Color(0.72f, 0.30f, 0.12f), // roux
            new Color(0.90f, 0.90f, 0.88f), // blanc
            new Color(0.25f, 0.35f, 0.75f), // bleu nuit
        };

        /// <summary>Skin tone as a brightness ramp over the race's base skin (index = Appearance.SkinTone).</summary>
        private static readonly float[] SkinToneFactors = { 1.15f, 1.0f, 0.80f, 0.60f };

        // Face variants: 0 standard, 1 gros nez, 2 fin (small nose, larger eyes).
        private static readonly float[] FaceNoseScale = { 1f, 1.6f, 0.75f };
        private static readonly float[] FaceEyeScale = { 1f, 1f, 1.3f };

        /// <summary>The exact skin colour a race+tone resolves to (used by the creation-screen swatches).</summary>
        public static Color SkinColor(byte raceId, byte tone)
        {
            BodyParams p = RaceParams(raceId);
            float f = SkinToneFactors[Mathf.Clamp(tone, 0, SkinToneFactors.Length - 1)];
            return new Color(p.Skin.r * f, p.Skin.g * f, p.Skin.b * f);
        }

        public static ModelRig Build(Transform parent, EntitySnapshot snapshot)
        {
            switch (snapshot.Kind)
            {
                case EntityKind.Corpse:
                    return BuildCorpse(parent);
                case EntityKind.Monster:
                    return BuildMonster(parent, snapshot.RaceId); // RaceId carries the monster def id
                case EntityKind.MonsterCorpse:
                    return BuildMonsterRemains(parent, snapshot.RaceId);
                case EntityKind.Npc:
                    // Npc "race" is a TYPE: 1 = bank chest, 2 = quest giver, 3+ = villager.
                    return snapshot.RaceId >= 2 ? BuildVillager(parent, snapshot) : BuildBankChest(parent);
                default:
                    // Real modular meshes (Quaternius pack) when imported; procedural otherwise.
                    if (QuaterniusCharacters.Available)
                    {
                        ModelRig q = QuaterniusCharacters.Create(parent, snapshot);
                        if (q != null)
                        {
                            AttachHandheld(q, snapshot);
                            return q;
                        }
                    }

                    return BuildPlayer(parent, snapshot);
            }
        }

        /// <summary>Weapon + shield for an FBX-based character (its clothes are real meshes).</summary>
        private static void AttachHandheld(ModelRig rig, EntitySnapshot snapshot)
        {
            // No weapon equipped = EMPTY hands. What you see is what you wear.
            byte weaponItemId = snapshot.EquippedIn(EquipSlot.Weapon);
            if (weaponItemId != 0)
            {
                AttachWeaponItem(rig, weaponItemId);
            }

            AttachOffHand(rig, snapshot.EquippedIn(EquipSlot.OffHand));
        }

        /// <summary>The slain creature lying on its side, darkened — cosmetic remains on a timer.</summary>
        private static ModelRig BuildMonsterRemains(Transform parent, byte defId)
        {
            // Real monster models: play the Death clip once and stay down — no tilt needed.
            if (MonsterModels.Available)
            {
                MonsterHandle real = MonsterModels.CreateRemains(parent, defId);
                if (real != null)
                {
                    return new ModelRig { HeadHeight = 0.9f };
                }
            }

            // A tilt node rolls the whole body onto its side just above the ground.
            var tilt = new GameObject("Remains");
            tilt.transform.SetParent(parent, false);
            tilt.transform.localPosition = new Vector3(0f, 0.28f, 0f);
            tilt.transform.localRotation = Quaternion.Euler(0f, 0f, 82f);

            _ = BuildMonster(tilt.transform, defId);

            // Death pallor: darken every part.
            Renderer[] parts = tilt.GetComponentsInChildren<Renderer>();
            for (int i = 0; i < parts.Length; i++)
            {
                Color c = parts[i].material.color;
                parts[i].material.color = new Color(c.r * 0.45f, c.g * 0.45f, c.b * 0.45f);
            }

            // Remains never animate: hand back an empty rig (pivots stay at rest).
            return new ModelRig { HeadHeight = 0.9f };
        }

        /// <summary>
        /// A sanctuary NPC as a person: deterministic appearance from the name (every client sees
        /// the same villager), the quest giver in watchman's gear, the others in peasant clothes.
        /// </summary>
        private static ModelRig BuildVillager(Transform parent, EntitySnapshot snapshot)
        {
            int seed = 7;
            string name = snapshot.Name ?? "";
            for (int i = 0; i < name.Length; i++)
            {
                seed = (seed * 31) + name[i];
            }

            seed = Mathf.Abs(seed);
            bool female = name.Contains("Mira") || (!name.Contains("Aldric") && !name.Contains("Brom") && seed % 2 == 0);
            var look = new Appearance(
                (byte)(seed % 4), (byte)(seed % 3), (byte)(female ? 1 : seed % 3),
                (byte)(seed % 6), (byte)(female ? 0 : (seed % 4)), (byte)(seed % 6));

            // Villagers are always DRESSED (bare slots read as naked since 0.39): cloth basics
            // for everyone, watchman's mail + boots for the quest giver.
            var gear = new byte[Aetheria.Shared.Items.EquipSlots.Count];
            gear[(int)Aetheria.Shared.Items.EquipSlot.Chest] = snapshot.RaceId == 2 ? (byte)9 : (byte)12;
            gear[(int)Aetheria.Shared.Items.EquipSlot.Legs] = 17;
            gear[(int)Aetheria.Shared.Items.EquipSlot.Feet] = 15;

            var person = new EntitySnapshot(snapshot.Id, EntityKind.Player, Faction.Neutral, snapshot.Position,
                1, 1, 0, 0, 0f, 1, name, raceId: 1, classId: 1,
                gender: female ? Gender.Female : Gender.Male, appearance: look, equipment: gear);

            if (QuaterniusCharacters.Available)
            {
                ModelRig q = QuaterniusCharacters.Create(parent, person);
                if (q != null)
                {
                    return q;
                }
            }

            return BuildPlayer(parent, person);
        }

        /// <summary>The sanctuary's bank chest: sturdy wood, iron bands, a golden lock.</summary>
        private static ModelRig BuildBankChest(Transform parent)
        {
            var rig = new ModelRig { HeadHeight = 1.6f };

            var model = new GameObject("Model");
            model.transform.SetParent(parent, false);

            // The RPG items pack has a proper chest model — use it when imported.
            GameObject chestPrefab = RpgItemModels.Load("Chest_Closed");
            if (chestPrefab != null)
            {
                GameObject chest = Object.Instantiate(chestPrefab, model.transform, false);
                float top = 0f;
                foreach (Renderer rr in chest.GetComponentsInChildren<Renderer>(true))
                {
                    float y = rr.bounds.max.y - model.transform.position.y;
                    if (y > top) { top = y; }
                }

                if (top > 0.05f)
                {
                    float k = 1.1f / top;
                    chest.transform.localScale = new Vector3(k, k, k);
                }

                Cube(model.transform, "Plinth", new Vector3(0f, 0.03f, 0f), new Vector3(1.5f, 0.06f, 1.1f),
                    new Color(0.45f, 0.45f, 0.50f));
                return rig;
            }

            Color wood = new Color(0.42f, 0.27f, 0.13f);
            Color iron = new Color(0.35f, 0.35f, 0.40f);

            Cube(model.transform, "Base", new Vector3(0f, 0.30f, 0f), new Vector3(1.15f, 0.60f, 0.75f), wood);
            var lid = Cube(model.transform, "Lid", new Vector3(0f, 0.68f, -0.06f), new Vector3(1.15f, 0.26f, 0.72f),
                new Color(0.48f, 0.32f, 0.16f));
            lid.transform.localRotation = Quaternion.Euler(-14f, 0f, 0f);
            Cube(model.transform, "BandL", new Vector3(-0.36f, 0.38f, 0f), new Vector3(0.10f, 0.82f, 0.80f), iron);
            Cube(model.transform, "BandR", new Vector3(0.36f, 0.38f, 0f), new Vector3(0.10f, 0.82f, 0.80f), iron);
            Cube(model.transform, "Lock", new Vector3(0f, 0.52f, 0.40f), new Vector3(0.16f, 0.20f, 0.08f),
                new Color(0.95f, 0.80f, 0.20f));
            Cube(model.transform, "Plinth", new Vector3(0f, 0.03f, 0f), new Vector3(1.5f, 0.06f, 1.1f),
                new Color(0.45f, 0.45f, 0.50f));

            return rig;
        }

        // ------------------------------------------------------------- Players

        private static ModelRig BuildPlayer(Transform parent, EntitySnapshot snapshot)
        {
            BodyParams p = RaceParams(snapshot.RaceId);
            p.UseCustom = true;
            if (snapshot.Gender == Gender.Female)
            {
                p.Width *= 0.88f;
            }

            ModelRig rig = BuildHumanoid(parent, p, snapshot.Gender, snapshot.Appearance);

            // The EQUIPPED weapon decides what sits in the hand — nothing equipped, bare hands.
            byte weaponItemId = snapshot.EquippedIn(EquipSlot.Weapon);
            if (weaponItemId != 0)
            {
                AttachWeaponItem(rig, weaponItemId);
            }

            // Every other visible slot: your LOOT is your look, piece by piece.
            AttachArmor(rig, snapshot.EquippedIn(EquipSlot.Chest));
            AttachHelm(rig, snapshot.EquippedIn(EquipSlot.Head));
            AttachShoulders(rig, snapshot.EquippedIn(EquipSlot.Shoulders));
            AttachCloak(rig, snapshot.EquippedIn(EquipSlot.Back));
            AttachLegs(rig, snapshot.EquippedIn(EquipSlot.Legs));
            AttachBoots(rig, snapshot.EquippedIn(EquipSlot.Feet));
            AttachOffHand(rig, snapshot.EquippedIn(EquipSlot.OffHand));
            return rig;
        }

        /// <summary>The equipped weapon, as a model in the hand: your LOOT is your look.</summary>
        private static void AttachWeaponItem(ModelRig rig, byte itemId)
        {
            // Real weapon meshes (Ultimate RPG Items pack) when imported; primitives otherwise.
            if (RpgItemModels.Available)
            {
                bool offHand = itemId == 5 || itemId == 7;
                string model = itemId switch
                {
                    1 => "Sword", 2 => "Sword", 4 => "Sword_big",
                    5 => "Bow_Wooden", 7 => "Bow_Golden",
                    _ => null,
                };

                if (model != null)
                {
                    GameObject go = RpgItemModels.Mount(GripFor(rig, offHand), model,
                        targetLength: offHand ? 1.1f : 0.95f,
                        gripFraction: offHand ? 0.5f : 0.16f);
                    if (go != null)
                    {
                        if (itemId == 1)
                        {
                            RpgItemModels.Tint(go, new Color(0.72f, 0.60f, 0.48f)); // rusty
                        }

                        return;
                    }
                }
            }

            switch (itemId)
            {
                case 1: // Rusty Sword: pitted, brownish blade
                    Sword(rig, new Color(0.55f, 0.45f, 0.35f), 0.60f);
                    break;
                case 2: // Iron Sword: clean steel
                    Sword(rig, new Color(0.75f, 0.78f, 0.82f), 0.66f);
                    break;
                case 4: // Steel Sword: bright, longer blade
                    Sword(rig, new Color(0.88f, 0.92f, 1.0f), 0.78f);
                    break;
                case 5: // Worn Bow
                    Bow(rig, new Color(0.40f, 0.28f, 0.15f), 0.9f);
                    break;
                case 7: // Hunting Bow: bigger, darker
                    Bow(rig, new Color(0.32f, 0.20f, 0.10f), 1.1f);
                    break;
                case 6: // Worn Staff
                    Staff(rig, new Color(0.45f, 0.32f, 0.18f), new Color(0.55f, 0.75f, 0.9f));
                    break;
                case 8: // Oak Staff: taller wood, brighter focus
                    Staff(rig, new Color(0.38f, 0.26f, 0.12f), new Color(0.35f, 0.95f, 1f));
                    break;
                default: // unknown weapon: a plain steel sword silhouette
                    Sword(rig, new Color(0.7f, 0.7f, 0.75f), 0.66f);
                    break;
            }
        }

        /// <summary>
        /// Loader for the "Ultimate RPG Items" pack (CC0, Assets/Resources/Items): weapons and
        /// props mounted by MEASUREMENT — the longest axis is aimed forward and the model is
        /// scaled/offset from its bounds, so the pack's pivot and axis conventions never matter.
        /// </summary>
        public static class RpgItemModels
        {
            private const string Root = "Items/";

            private static readonly System.Collections.Generic.Dictionary<string, GameObject> Cache =
                new System.Collections.Generic.Dictionary<string, GameObject>();
            private static bool _checked;
            private static bool _available;

            public static bool Available
            {
                get
                {
                    if (!_checked)
                    {
                        _checked = true;
                        _available = Load("Sword") != null;
                    }

                    return _available;
                }
            }

            public static GameObject Load(string name)
            {
                GameObject cached;
                if (!Cache.TryGetValue(name, out cached))
                {
                    cached = Resources.Load<GameObject>(Root + name);
                    Cache[name] = cached;
                }

                return cached;
            }

            /// <summary>Multiply every material colour (e.g. rust on a worn blade).</summary>
            public static void Tint(GameObject go, Color tint)
            {
                foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                {
                    foreach (Material m in r.materials)
                    {
                        if (m != null)
                        {
                            m.color = new Color(m.color.r * tint.r, m.color.g * tint.g, m.color.b * tint.b, m.color.a);
                        }
                    }
                }
            }

            /// <summary>
            /// Mount an item under a world-aligned grip: longest axis → forward (+Z), scaled to
            /// <paramref name="targetLength"/>, with the grip point at <paramref name="gripFraction"/>
            /// of the way along the length (0.16 = a sword's handle, 0.5 = a bow's middle).
            /// </summary>
            public static GameObject Mount(Transform grip, string name, float targetLength, float gripFraction)
            {
                GameObject prefab = Load(name);
                if (prefab == null)
                {
                    return null;
                }

                GameObject go = Object.Instantiate(prefab, grip, false);
                go.name = name;

                Bounds b;
                if (!CombinedBounds(go, out b) || b.size.magnitude < 0.001f)
                {
                    return go; // stub/no renderers: leave as imported
                }

                // Aim the longest measured axis forward.
                if (b.size.y >= b.size.x && b.size.y >= b.size.z)
                {
                    go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                }
                else if (b.size.x >= b.size.z)
                {
                    go.transform.localRotation = Quaternion.Euler(0f, -90f, 0f);
                }

                float longest = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                float k = targetLength / longest;
                go.transform.localScale = new Vector3(k, k, k);

                // Slide it so the grip point sits at the hand.
                Bounds after;
                if (CombinedBounds(go, out after))
                {
                    Vector3 gripPoint = new Vector3(after.center.x, after.center.y,
                        after.min.z + (gripFraction * after.size.z));
                    go.transform.position += grip.position - gripPoint;
                }

                return go;
            }

            private static bool CombinedBounds(GameObject go, out Bounds bounds)
            {
                bounds = default;
                bool first = true;
                foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (first) { bounds = r.bounds; first = false; }
                    else { bounds.Encapsulate(r.bounds); }
                }

                return !first;
            }
        }

        /// <summary>Where a held item mounts: the HAND bone on FBX rigs, the forearm on procedural ones.</summary>
        private static Transform GripFor(ModelRig rig, bool offHand)
        {
            Transform hand = offHand ? rig.HandL : rig.HandR;
            if (hand != null && rig.BoneRoot != null)
            {
                Transform grip = Pivot(hand, "Grip", Vector3.zero);
                grip.rotation = rig.BoneRoot.rotation; // align to the character, not the bone's axes
                Vector3 ls = grip.lossyScale;          // undo import/bone scaling → world scale ≈ 1
                grip.localScale = new Vector3(
                    ls.x > 0.0001f ? 1f / ls.x : 1f,
                    ls.y > 0.0001f ? 1f / ls.y : 1f,
                    ls.z > 0.0001f ? 1f / ls.z : 1f);
                return grip;
            }

            return Pivot(offHand ? rig.ArmL : rig.ArmR, "Grip",
                new Vector3(0f, -0.66f, offHand ? 0.08f : 0.10f));
        }

        private static void Sword(ModelRig rig, Color blade, float length)
        {
            var w = Pivot(GripFor(rig, offHand: false), "Sword", Vector3.zero);
            w.localRotation = Quaternion.Euler(90f, 0f, 0f);
            Cube(w, "Blade", new Vector3(0f, 0.10f + (length / 2f), 0f), new Vector3(0.06f, length, 0.10f), blade);
            Cube(w, "Guard", new Vector3(0f, 0.08f, 0f), new Vector3(0.20f, 0.05f, 0.06f), new Color(0.55f, 0.45f, 0.20f));
            Cube(w, "Grip", new Vector3(0f, -0.05f, 0f), new Vector3(0.05f, 0.12f, 0.05f), new Color(0.30f, 0.18f, 0.10f));
        }

        private static void Bow(ModelRig rig, Color wood, float scale)
        {
            var w = Pivot(GripFor(rig, offHand: true), "Bow", Vector3.zero);
            var upper = Caps(w, "LimbUp", new Vector3(0f, 0.24f * scale, 0.06f), new Vector3(0.05f, 0.26f * scale, 0.05f), wood);
            upper.transform.localRotation = Quaternion.Euler(-14f, 0f, 0f);
            var lower = Caps(w, "LimbDown", new Vector3(0f, -0.24f * scale, 0.06f), new Vector3(0.05f, 0.26f * scale, 0.05f), wood);
            lower.transform.localRotation = Quaternion.Euler(14f, 0f, 0f);
            Shape(PrimitiveType.Cylinder, w, "String", new Vector3(0f, 0f, -0.02f),
                new Vector3(0.015f, 0.45f * scale, 0.015f), new Color(0.85f, 0.85f, 0.80f));
        }

        private static void Staff(ModelRig rig, Color wood, Color focus)
        {
            var w = Pivot(GripFor(rig, offHand: false), "Staff", Vector3.zero);
            Shape(PrimitiveType.Cylinder, w, "Shaft", new Vector3(0f, 0.35f, 0f),
                new Vector3(0.05f, 0.75f, 0.05f), wood);
            var orb = Ball(w, "Focus", new Vector3(0f, 1.12f, 0f), new Vector3(0.16f, 0.16f, 0.16f), focus);
            _ = orb;
        }

        /// <summary>The equipped armor, worn on the body (shape and trims; the tunic keeps its tint).</summary>
        private static void AttachArmor(ModelRig rig, byte itemId)
        {
            if (rig.Torso == null)
            {
                return;
            }

            Transform model = rig.Torso.parent;
            switch (itemId)
            {
                case 3: // Leather Vest: a brown chest plate over the tunic
                    Cube(model, "Vest", new Vector3(0f, 1.24f, 0.16f), new Vector3(0.44f, 0.5f, 0.08f),
                        new Color(0.45f, 0.30f, 0.16f));
                    break;

                case 9: // Chain Mail: metallic shoulder guards + a chest plate
                    Ball(model, "PauldronL", new Vector3(-0.36f, 1.52f, 0f), new Vector3(0.24f, 0.18f, 0.24f),
                        new Color(0.60f, 0.62f, 0.68f));
                    Ball(model, "PauldronR", new Vector3(0.36f, 1.52f, 0f), new Vector3(0.24f, 0.18f, 0.24f),
                        new Color(0.60f, 0.62f, 0.68f));
                    Cube(model, "Breastplate", new Vector3(0f, 1.24f, 0.16f), new Vector3(0.46f, 0.52f, 0.08f),
                        new Color(0.55f, 0.58f, 0.64f));
                    break;

                case 12: // Padded Robe: a long violet skirt below the tunic
                    Cube(model, "Robe", new Vector3(0f, 0.62f, 0f), new Vector3(0.55f, 0.6f, 0.36f),
                        new Color(0.35f, 0.22f, 0.45f));
                    break;
            }
        }

        /// <summary>Headgear, parented to the head pivot so it nods with the skull.</summary>
        private static void AttachHelm(ModelRig rig, byte itemId)
        {
            if (rig.Head == null) { return; }

            switch (itemId)
            {
                case 13: // Leather Cap: a soft brown dome
                    Ball(rig.Head, "Cap", new Vector3(0f, 0.31f, -0.02f), new Vector3(0.37f, 0.20f, 0.36f),
                        new Color(0.48f, 0.32f, 0.18f));
                    break;

                case 14: // Iron Helm: a metal dome with a nose guard
                    Ball(rig.Head, "Helm", new Vector3(0f, 0.30f, -0.01f), new Vector3(0.39f, 0.24f, 0.38f),
                        new Color(0.62f, 0.64f, 0.70f));
                    Cube(rig.Head, "NoseGuard", new Vector3(0f, 0.18f, 0.17f), new Vector3(0.05f, 0.16f, 0.04f),
                        new Color(0.55f, 0.57f, 0.62f));
                    break;
            }
        }

        /// <summary>Shoulder pads, parented to the arm pivots so they follow the swing.</summary>
        private static void AttachShoulders(ModelRig rig, byte itemId)
        {
            if (rig.ArmL == null || rig.ArmR == null) { return; }

            switch (itemId)
            {
                case 16: // Wolf-fur Shoulders: shaggy grey tufts
                    Color fur = new Color(0.46f, 0.43f, 0.40f);
                    Ball(rig.ArmL, "FurPadL", new Vector3(-0.03f, 0.04f, 0f), new Vector3(0.27f, 0.20f, 0.27f), fur);
                    Ball(rig.ArmR, "FurPadR", new Vector3(0.03f, 0.04f, 0f), new Vector3(0.27f, 0.20f, 0.27f), fur);
                    break;
            }
        }

        /// <summary>The cloak, hanging flat down the back.</summary>
        private static void AttachCloak(ModelRig rig, byte itemId)
        {
            if (rig.Torso == null) { return; }

            switch (itemId)
            {
                case 18: // Traveler's Cloak: forest green
                    Cube(rig.Torso.parent, "Cloak", new Vector3(0f, 1.12f, -0.24f), new Vector3(0.46f, 0.78f, 0.05f),
                        new Color(0.20f, 0.38f, 0.22f));
                    break;
            }
        }

        /// <summary>Leg armour: overlay sleeves on the shin capsules.</summary>
        private static void AttachLegs(ModelRig rig, byte itemId)
        {
            if (rig.LegL == null || rig.LegR == null) { return; }

            switch (itemId)
            {
                case 17: // Leather Pants: brown thigh sleeves
                    Color leather = new Color(0.44f, 0.29f, 0.15f);
                    Caps(rig.LegL, "PantsL", new Vector3(0f, -0.26f, 0f), new Vector3(0.25f, 0.26f, 0.27f), leather);
                    Caps(rig.LegR, "PantsR", new Vector3(0f, -0.26f, 0f), new Vector3(0.25f, 0.26f, 0.27f), leather);
                    break;
            }
        }

        /// <summary>Footwear at the bottom of the legs.</summary>
        private static void AttachBoots(ModelRig rig, byte itemId)
        {
            if (rig.LegL == null || rig.LegR == null) { return; }

            switch (itemId)
            {
                case 15: // Leather Boots: dark, slightly forward toes
                    Color boot = new Color(0.30f, 0.19f, 0.10f);
                    Cube(rig.LegL, "BootL", new Vector3(0f, -0.72f, 0.04f), new Vector3(0.24f, 0.16f, 0.32f), boot);
                    Cube(rig.LegR, "BootR", new Vector3(0f, -0.72f, 0.04f), new Vector3(0.24f, 0.16f, 0.32f), boot);
                    break;
            }
        }

        /// <summary>The off-hand: a shield strapped to the left arm.</summary>
        private static void AttachOffHand(ModelRig rig, byte itemId)
        {
            if (rig.ArmL == null) { return; }

            switch (itemId)
            {
                case 22: // Wooden Shield: a round disc with an iron boss
                    bool onHand = rig.HandL != null && rig.BoneRoot != null;
                    Transform mount = Pivot(onHand ? GripFor(rig, offHand: true) : rig.ArmL, "Shield",
                        onHand ? Vector3.zero : new Vector3(-0.14f, -0.40f, 0.02f));
                    mount.localRotation = Quaternion.Euler(0f, 0f, 90f); // disc faces outward
                    Shape(PrimitiveType.Cylinder, mount, "Disc", Vector3.zero,
                        new Vector3(0.40f, 0.025f, 0.40f), new Color(0.45f, 0.30f, 0.16f));
                    Ball(mount, "Boss", new Vector3(0f, 0.035f, 0f), new Vector3(0.12f, 0.05f, 0.12f),
                        new Color(0.50f, 0.52f, 0.58f));
                    break;
            }
        }

        private static BodyParams RaceParams(byte raceId)
        {
            var p = new BodyParams
            {
                Height = 1f,
                Width = 1f,
                Skin = new Color(0.85f, 0.68f, 0.55f),
                Hair = new Color(0.30f, 0.20f, 0.12f),
                HeadScale = 1f,
            };

            switch (raceId)
            {
                case 2: // Orc: bulky, green, tusks
                    p.Height = 1.08f;
                    p.Width = 1.22f;
                    p.Skin = new Color(0.42f, 0.65f, 0.32f);
                    p.Hair = new Color(0.10f, 0.10f, 0.10f);
                    p.Tusks = true;
                    break;
                case 3: // Elf: slim, pale, pointed ears
                    p.Height = 1.05f;
                    p.Width = 0.85f;
                    p.Skin = new Color(0.93f, 0.87f, 0.78f);
                    p.Hair = new Color(0.85f, 0.75f, 0.45f);
                    p.Ears = 1;
                    break;
                case 4: // Dwarf: short, wide, bearded
                    p.Height = 0.78f;
                    p.Width = 1.30f;
                    p.Skin = new Color(0.82f, 0.60f, 0.50f);
                    p.Hair = new Color(0.65f, 0.30f, 0.15f);
                    p.Beard = true;
                    break;
            }

            return p;
        }

        // ------------------------------------------------------------ Monsters

        private static ModelRig BuildMonster(Transform parent, byte defId)
        {
            switch (defId)
            {
                case 2: // Dire Wolf
                    return BuildQuadruped(parent, 1f,
                        new Color(0.45f, 0.42f, 0.40f), new Color(0.9f, 0.75f, 0.2f), horns: false);
                case 4: // Ashmaw the Devourer: a huge, dark, horned beast
                    return BuildQuadruped(parent, 2.3f,
                        new Color(0.40f, 0.14f, 0.10f), new Color(1f, 0.25f, 0.1f), horns: true);
                case 3: // Goblin King: a big goblin with a crown
                    return BuildGoblin(parent, 1.55f, crown: true);
                default: // Goblin Grunt (and any unknown monster: small green humanoid)
                    return BuildGoblin(parent, 1f, crown: false);
            }
        }

        private static ModelRig BuildGoblin(Transform parent, float scale, bool crown)
        {
            var p = new BodyParams
            {
                Height = 0.62f * scale,
                Width = 0.85f * scale,
                Skin = new Color(0.45f, 0.75f, 0.35f),
                Hair = new Color(0.20f, 0.30f, 0.15f),
                HeadScale = 1.35f,
                Ears = 2,
                Crown = crown,
                Tunic = new Color(0.45f, 0.30f, 0.20f), // ragged leathers
            };

            ModelRig rig = BuildHumanoid(parent, p, Gender.Male, default(Appearance));

            // A crude wooden club in the right hand.
            Transform hand = rig.ArmR;
            var club = Caps(hand, "Club", new Vector3(0f, -0.62f, 0.16f), new Vector3(0.10f, 0.24f, 0.10f),
                new Color(0.55f, 0.40f, 0.22f));
            club.transform.localRotation = Quaternion.Euler(-70f, 0f, 0f);
            return rig;
        }

        // ------------------------------------------------------------ Humanoid

        private static ModelRig BuildHumanoid(Transform parent, BodyParams p, Gender gender, Appearance look)
        {
            var rig = new ModelRig();

            // One scale root so race proportions apply to every part at once.
            var model = new GameObject("Model");
            model.transform.SetParent(parent, false);
            model.transform.localScale = new Vector3(p.Width, p.Height, p.Width);

            // Resolve the customisation palettes (players) or keep the fixed monster colours.
            Color skin = p.Skin;
            if (p.UseCustom)
            {
                float f = SkinToneFactors[Mathf.Clamp(look.SkinTone, 0, SkinToneFactors.Length - 1)];
                skin = new Color(p.Skin.r * f, p.Skin.g * f, p.Skin.b * f);
            }

            Color hairColor = p.UseCustom
                ? HairColors[Mathf.Clamp(look.HairColor, 0, HairColors.Length - 1)]
                : p.Hair;
            Color beardColor = p.UseCustom
                ? HairColors[Mathf.Clamp(look.BeardColor, 0, HairColors.Length - 1)]
                : p.Hair;
            int face = p.UseCustom ? Mathf.Clamp(look.Face, 0, FaceNoseScale.Length - 1) : 0;
            int hairStyle = p.UseCustom ? look.HairStyle : 0;   // 0 court, 1 long, 2 iroquois, 3 chauve
            int beardStyle = p.UseCustom ? look.BeardStyle : (p.Beard ? 1 : 0);

            Color pants = new Color(0.25f, 0.22f, 0.20f);

            // ROUNDED style: capsule limbs, capsule torso, sphere head — no more cube-people.
            // Legs: pivots at the hips, capsules hanging below (rotating the pivot swings the leg).
            rig.LegL = Pivot(model.transform, "LegL", new Vector3(-0.15f, 0.85f, 0f));
            rig.LegR = Pivot(model.transform, "LegR", new Vector3(0.15f, 0.85f, 0f));
            Caps(rig.LegL, "Shin", new Vector3(0f, -0.42f, 0f), new Vector3(0.22f, 0.41f, 0.24f), pants);
            Caps(rig.LegR, "Shin", new Vector3(0f, -0.42f, 0f), new Vector3(0.22f, 0.41f, 0.24f), pants);

            // Torso (the relation-tinted tunic) and belt.
            var torso = Caps(model.transform, "Torso", new Vector3(0f, 1.20f, 0f),
                new Vector3(0.50f, 0.38f, 0.36f), p.Tunic ?? new Color(0.5f, 0.5f, 0.55f));
            rig.Torso = torso.transform;
            Shape(PrimitiveType.Cylinder, model.transform, "Belt", new Vector3(0f, 0.92f, 0f),
                new Vector3(0.46f, 0.05f, 0.36f), new Color(0.20f, 0.15f, 0.10f));

            // Arms: pivots at the shoulders.
            rig.ArmL = Pivot(model.transform, "ArmL", new Vector3(-0.34f, 1.44f, 0f));
            rig.ArmR = Pivot(model.transform, "ArmR", new Vector3(0.34f, 1.44f, 0f));
            Caps(rig.ArmL, "Arm", new Vector3(0f, -0.34f, 0f), new Vector3(0.17f, 0.36f, 0.18f), skin);
            Caps(rig.ArmR, "Arm", new Vector3(0f, -0.34f, 0f), new Vector3(0.17f, 0.36f, 0.18f), skin);

            // Head: its own pivot so it can nod; a sphere skull with sculpted features.
            rig.Head = Pivot(model.transform, "Head", new Vector3(0f, 1.62f, 0f));
            BuildHeadParts(rig.Head, p, skin, hairColor, beardColor, face, hairStyle, beardStyle);

            // Only relation-tint player tunics; monsters keep their fixed colours.
            if (p.Tunic == null)
            {
                rig.TintTargets = new Renderer[] { torso.GetComponent<Renderer>() };
            }

            rig.HeadHeight = 2.15f * p.Height;
            return rig;
        }

        /// <summary>
        /// The whole customised head (skull, face, hair, beard, race features) built onto any
        /// parent — the procedural body's head pivot, or an FBX rig's head bone.
        /// </summary>
        public static void BuildHeadOn(Transform headParent, byte raceId, Gender gender, Appearance look)
        {
            _ = gender;
            BodyParams p = RaceParams(raceId);
            p.UseCustom = true;

            float f = SkinToneFactors[Mathf.Clamp(look.SkinTone, 0, SkinToneFactors.Length - 1)];
            Color skin = new Color(p.Skin.r * f, p.Skin.g * f, p.Skin.b * f);
            Color hairColor = HairColors[Mathf.Clamp(look.HairColor, 0, HairColors.Length - 1)];
            Color beardColor = HairColors[Mathf.Clamp(look.BeardColor, 0, HairColors.Length - 1)];
            int face = Mathf.Clamp(look.Face, 0, FaceNoseScale.Length - 1);

            BuildHeadParts(headParent, p, skin, hairColor, beardColor, face, look.HairStyle, look.BeardStyle);
        }

        private static void BuildHeadParts(Transform head, BodyParams p, Color skin, Color hairColor,
            Color beardColor, int face, int hairStyle, int beardStyle)
        {
            float hs = 0.32f * p.HeadScale;
            Ball(head, "Skull", new Vector3(0f, 0.17f, 0f), new Vector3(hs * 1.05f, hs, hs * 1.02f), skin);

            // Face variant: nose and eye proportions.
            float noseScale = FaceNoseScale[face];
            float eyeScale = FaceEyeScale[face];
            Ball(head, "Nose", new Vector3(0f, 0.14f, hs * 0.52f),
                new Vector3(0.08f * noseScale, 0.08f * noseScale, 0.11f * noseScale), skin);
            Color eye = new Color(0.08f, 0.08f, 0.10f);
            Ball(head, "EyeL", new Vector3(-hs * 0.24f, 0.21f, hs * 0.45f),
                new Vector3(0.055f * eyeScale, 0.055f * eyeScale, 0.03f), eye);
            Ball(head, "EyeR", new Vector3(hs * 0.24f, 0.21f, hs * 0.45f),
                new Vector3(0.055f * eyeScale, 0.055f * eyeScale, 0.03f), eye);

            // Hair style: 0 court, 1 long, 2 iroquois, 3 chauve.
            if (hairStyle == 0 || hairStyle == 1)
            {
                Ball(head, "Hair", new Vector3(0f, 0.17f + (hs * 0.38f), -hs * 0.08f),
                    new Vector3(hs * 1.12f, hs * 0.62f, hs * 1.1f), hairColor);
            }

            if (hairStyle == 1) // long: cap + hair falling down the back
            {
                Caps(head, "HairBack", new Vector3(0f, -0.02f, -hs * 0.5f),
                    new Vector3(hs * 0.85f, 0.30f, 0.14f), hairColor);
            }
            else if (hairStyle == 2) // iroquois: a tall narrow crest
            {
                Ball(head, "Mohawk", new Vector3(0f, 0.17f + (hs * 0.62f), 0f),
                    new Vector3(0.10f, 0.24f, hs * 1.15f), hairColor);
            }

            // Beard style: 0 aucune, 1 courte, 2 longue, 3 tressée.
            if (beardStyle == 1)
            {
                Ball(head, "Beard", new Vector3(0f, 0.02f, hs * 0.40f),
                    new Vector3(hs * 0.78f, 0.22f, 0.16f), beardColor);
            }
            else if (beardStyle == 2)
            {
                Ball(head, "Beard", new Vector3(0f, -0.10f, hs * 0.38f),
                    new Vector3(hs * 0.78f, 0.44f, 0.16f), beardColor);
            }
            else if (beardStyle == 3)
            {
                Ball(head, "Beard", new Vector3(0f, -0.02f, hs * 0.38f),
                    new Vector3(hs * 0.78f, 0.28f, 0.16f), beardColor);
                Caps(head, "Braid", new Vector3(0f, -0.30f, hs * 0.40f),
                    new Vector3(0.08f, 0.17f, 0.08f), beardColor);
            }

            if (p.Tusks)
            {
                Color bone = new Color(0.92f, 0.90f, 0.82f);
                Caps(head, "TuskL", new Vector3(-0.07f, 0.06f, hs * 0.48f), new Vector3(0.045f, 0.06f, 0.045f), bone);
                Caps(head, "TuskR", new Vector3(0.07f, 0.06f, hs * 0.48f), new Vector3(0.045f, 0.06f, 0.045f), bone);
            }

            if (p.Ears == 1) // pointed elf ears
            {
                var earL = Caps(head, "EarL", new Vector3(-hs * 0.56f, 0.26f, 0f), new Vector3(0.05f, 0.09f, 0.05f), skin);
                earL.transform.localRotation = Quaternion.Euler(0f, 0f, 28f);
                var earR = Caps(head, "EarR", new Vector3(hs * 0.56f, 0.26f, 0f), new Vector3(0.05f, 0.09f, 0.05f), skin);
                earR.transform.localRotation = Quaternion.Euler(0f, 0f, -28f);
            }
            else if (p.Ears == 2) // large goblin ears
            {
                Ball(head, "EarL", new Vector3(-hs * 0.64f, 0.19f, 0f), new Vector3(0.16f, 0.11f, 0.06f), skin);
                Ball(head, "EarR", new Vector3(hs * 0.64f, 0.19f, 0f), new Vector3(0.16f, 0.11f, 0.06f), skin);
            }

            if (p.Crown)
            {
                Shape(PrimitiveType.Cylinder, head, "Crown", new Vector3(0f, 0.17f + (hs * 0.62f), 0f),
                    new Vector3(hs * 0.85f, 0.06f, hs * 0.85f), new Color(0.95f, 0.80f, 0.20f));
            }
        }

        private static void AttachWeapon(ModelRig rig, byte classId)
        {
            switch (classId)
            {
                case 1: // Warrior: a sword held ready, blade forward.
                {
                    var w = Pivot(GripFor(rig, offHand: false), "Sword", Vector3.zero);
                    w.localRotation = Quaternion.Euler(90f, 0f, 0f); // +Y of the blade → forward (+Z)
                    Color steel = new Color(0.75f, 0.78f, 0.82f);
                    Cube(w, "Blade", new Vector3(0f, 0.38f, 0f), new Vector3(0.06f, 0.66f, 0.10f), steel);
                    Cube(w, "Guard", new Vector3(0f, 0.08f, 0f), new Vector3(0.20f, 0.05f, 0.06f), new Color(0.55f, 0.45f, 0.20f));
                    Cube(w, "Grip", new Vector3(0f, -0.05f, 0f), new Vector3(0.05f, 0.12f, 0.05f), new Color(0.30f, 0.18f, 0.10f));
                    break;
                }

                case 2: // Mage: a glowing focus orb floating at the hand.
                {
                    GameObject orb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    orb.name = "Orb";
                    Object.Destroy(orb.GetComponent<Collider>());
                    orb.transform.SetParent(GripFor(rig, offHand: false), false);
                    orb.transform.localPosition = new Vector3(0f, -0.04f, 0.04f);
                    orb.transform.localScale = new Vector3(0.17f, 0.17f, 0.17f);
                    Paint(orb, new Color(0.30f, 0.90f, 1f));
                    break;
                }

                case 3: // Ranger: a bow in the off-hand.
                {
                    var w = Pivot(GripFor(rig, offHand: true), "Bow", Vector3.zero);
                    Color wood = new Color(0.45f, 0.30f, 0.15f);
                    var upper = Caps(w, "LimbUp", new Vector3(0f, 0.24f, 0.06f), new Vector3(0.05f, 0.26f, 0.05f), wood);
                    upper.transform.localRotation = Quaternion.Euler(-14f, 0f, 0f);
                    var lower = Caps(w, "LimbDown", new Vector3(0f, -0.24f, 0.06f), new Vector3(0.05f, 0.26f, 0.05f), wood);
                    lower.transform.localRotation = Quaternion.Euler(14f, 0f, 0f);
                    Shape(PrimitiveType.Cylinder, w, "String", new Vector3(0f, 0f, -0.02f),
                        new Vector3(0.015f, 0.45f, 0.015f), new Color(0.85f, 0.85f, 0.80f));
                    break;
                }
            }
        }

        // ----------------------------------------------------------- Quadruped

        private static ModelRig BuildQuadruped(Transform parent, float scale, Color fur, Color eyes, bool horns)
        {
            var rig = new ModelRig { Quadruped = true };

            var model = new GameObject("Model");
            model.transform.SetParent(parent, false);
            model.transform.localScale = new Vector3(scale, scale, scale);

            Color dark = fur * 0.75f;

            // ROUNDED beast: a stretched sphere body, sphere head, capsule legs — in real FUR.
            var body = Ball(model.transform, "Body", new Vector3(0f, 0.58f, 0f), new Vector3(0.52f, 0.5f, 1.15f), fur);
            Tex.Apply(body, "fur", 2.2f, 1.6f, Lighten(fur));
            rig.Torso = body.transform;

            // Head + snout on a pivot at the front, so it can lunge/bite.
            rig.Head = Pivot(model.transform, "Head", new Vector3(0f, 0.72f, 0.55f));
            var skull = Ball(rig.Head, "Skull", new Vector3(0f, 0.05f, 0.12f), new Vector3(0.36f, 0.32f, 0.36f), fur);
            Tex.Apply(skull, "fur", 1.4f, 1.2f, Lighten(fur));
            Ball(rig.Head, "Snout", new Vector3(0f, -0.02f, 0.36f), new Vector3(0.20f, 0.16f, 0.32f), dark);
            Ball(rig.Head, "EyeL", new Vector3(-0.10f, 0.12f, 0.26f), new Vector3(0.055f, 0.055f, 0.03f), eyes);
            Ball(rig.Head, "EyeR", new Vector3(0.10f, 0.12f, 0.26f), new Vector3(0.055f, 0.055f, 0.03f), eyes);
            var earL = Caps(rig.Head, "EarL", new Vector3(-0.12f, 0.27f, 0.05f), new Vector3(0.07f, 0.09f, 0.05f), dark);
            earL.transform.localRotation = Quaternion.Euler(0f, 0f, 15f);
            var earR = Caps(rig.Head, "EarR", new Vector3(0.12f, 0.27f, 0.05f), new Vector3(0.07f, 0.09f, 0.05f), dark);
            earR.transform.localRotation = Quaternion.Euler(0f, 0f, -15f);

            if (horns)
            {
                Color bone = new Color(0.90f, 0.85f, 0.70f);
                var hl = Caps(rig.Head, "HornL", new Vector3(-0.16f, 0.30f, 0.02f), new Vector3(0.07f, 0.16f, 0.07f), bone);
                hl.transform.localRotation = Quaternion.Euler(-25f, 0f, -20f);
                var hr = Caps(rig.Head, "HornR", new Vector3(0.16f, 0.30f, 0.02f), new Vector3(0.07f, 0.16f, 0.07f), bone);
                hr.transform.localRotation = Quaternion.Euler(-25f, 0f, 20f);
            }

            // Four legs: front pair on the "arm" pivots, hind pair on the "leg" pivots.
            rig.ArmL = Pivot(model.transform, "FrontL", new Vector3(-0.17f, 0.52f, 0.38f));
            rig.ArmR = Pivot(model.transform, "FrontR", new Vector3(0.17f, 0.52f, 0.38f));
            rig.LegL = Pivot(model.transform, "HindL", new Vector3(-0.17f, 0.52f, -0.38f));
            rig.LegR = Pivot(model.transform, "HindR", new Vector3(0.17f, 0.52f, -0.38f));
            Caps(rig.ArmL, "Leg", new Vector3(0f, -0.26f, 0f), new Vector3(0.14f, 0.27f, 0.15f), dark);
            Caps(rig.ArmR, "Leg", new Vector3(0f, -0.26f, 0f), new Vector3(0.14f, 0.27f, 0.15f), dark);
            Caps(rig.LegL, "Leg", new Vector3(0f, -0.26f, 0f), new Vector3(0.14f, 0.27f, 0.15f), dark);
            Caps(rig.LegR, "Leg", new Vector3(0f, -0.26f, 0f), new Vector3(0.14f, 0.27f, 0.15f), dark);

            // Tail, angled up and back, wags while moving.
            rig.Tail = Pivot(model.transform, "Tail", new Vector3(0f, 0.68f, -0.52f));
            var tail = Caps(rig.Tail, "TailMesh", new Vector3(0f, 0.08f, -0.20f), new Vector3(0.09f, 0.24f, 0.09f), dark);
            tail.transform.localRotation = Quaternion.Euler(-110f, 0f, 0f);

            rig.HeadHeight = 1.25f * scale;
            return rig;
        }

        // -------------------------------------------------------------- Corpse

        private static ModelRig BuildCorpse(Transform parent)
        {
            var rig = new ModelRig { HeadHeight = 1.0f };

            var model = new GameObject("Model");
            model.transform.SetParent(parent, false);

            Ball(model.transform, "Mound", new Vector3(0f, 0.05f, 0f), new Vector3(1.0f, 0.22f, 1.0f),
                new Color(0.35f, 0.30f, 0.26f));
            var stone = Caps(model.transform, "Stone", new Vector3(0f, 0.38f, -0.25f),
                new Vector3(0.50f, 0.32f, 0.14f), new Color(0.55f, 0.55f, 0.58f));
            stone.transform.localRotation = Quaternion.Euler(-6f, 0f, 0f);

            GameObject skull = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            skull.name = "Skull";
            Object.Destroy(skull.GetComponent<Collider>());
            skull.transform.SetParent(model.transform, false);
            skull.transform.localPosition = new Vector3(0.18f, 0.20f, 0.15f);
            skull.transform.localScale = new Vector3(0.17f, 0.17f, 0.17f);
            Paint(skull, new Color(0.85f, 0.82f, 0.75f));

            return rig;
        }

        // ------------------------------------------------------------- Helpers

        /// <summary>Boost a tint so "colour × texture" lands back on the intended shade.</summary>
        private static Color Lighten(Color c) =>
            new Color(Mathf.Min(1f, c.r * 2.1f), Mathf.Min(1f, c.g * 2.1f), Mathf.Min(1f, c.b * 2.1f));

        private static Transform Pivot(Transform parent, string name, Vector3 localPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            return go.transform;
        }

        private static GameObject Cube(Transform parent, string name, Vector3 localPos, Vector3 scale, Color color)
            => Shape(PrimitiveType.Cube, parent, name, localPos, scale, color);

        private static GameObject Ball(Transform parent, string name, Vector3 localPos, Vector3 scale, Color color)
            => Shape(PrimitiveType.Sphere, parent, name, localPos, scale, color);

        private static GameObject Caps(Transform parent, string name, Vector3 localPos, Vector3 scale, Color color)
            => Shape(PrimitiveType.Capsule, parent, name, localPos, scale, color);

        private static GameObject Shape(PrimitiveType type, Transform parent, string name,
            Vector3 localPos, Vector3 scale, Color color)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.Destroy(go.GetComponent<Collider>()); // visuals only; the server owns hits
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = scale;
            Paint(go, color);
            return go;
        }

        private static void Paint(GameObject go, Color color)
        {
            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = color;
            }
        }
    }
}
