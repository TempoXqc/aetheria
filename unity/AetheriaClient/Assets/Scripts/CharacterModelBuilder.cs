using Aetheria.Shared.Combat;
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

        /// <summary>Renderers tinted by relation (self / ally / enemy) — the character's tunic.</summary>
        public Renderer[] TintTargets = new Renderer[0];

        /// <summary>World-space height (before root scaling) where nameplates should anchor.</summary>
        public float HeadHeight = 2.1f;

        /// <summary>Quadrupeds swing legs in diagonal pairs and "bite" instead of arm-swinging.</summary>
        public bool Quadruped;
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
                    return BuildBankChest(parent);
                default:
                    return BuildPlayer(parent, snapshot.RaceId, snapshot.ClassId, snapshot.Gender, snapshot.Appearance);
            }
        }

        /// <summary>The slain creature lying on its side, darkened — cosmetic remains on a timer.</summary>
        private static ModelRig BuildMonsterRemains(Transform parent, byte defId)
        {
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

        /// <summary>The sanctuary's bank chest: sturdy wood, iron bands, a golden lock.</summary>
        private static ModelRig BuildBankChest(Transform parent)
        {
            var rig = new ModelRig { HeadHeight = 1.6f };

            var model = new GameObject("Model");
            model.transform.SetParent(parent, false);

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

        private static ModelRig BuildPlayer(Transform parent, byte raceId, byte classId, Gender gender,
            Appearance appearance)
        {
            BodyParams p = RaceParams(raceId);
            p.UseCustom = true;
            if (gender == Gender.Female)
            {
                p.Width *= 0.88f;
            }

            ModelRig rig = BuildHumanoid(parent, p, gender, appearance);
            AttachWeapon(rig, classId);
            return rig;
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
            var club = Cube(hand, "Club", new Vector3(0f, -0.62f, 0.16f), new Vector3(0.09f, 0.42f, 0.09f),
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

            // Legs: pivots at the hips, blocks hanging below (rotating the pivot swings the leg).
            rig.LegL = Pivot(model.transform, "LegL", new Vector3(-0.15f, 0.85f, 0f));
            rig.LegR = Pivot(model.transform, "LegR", new Vector3(0.15f, 0.85f, 0f));
            Cube(rig.LegL, "Shin", new Vector3(0f, -0.42f, 0f), new Vector3(0.20f, 0.82f, 0.24f), pants);
            Cube(rig.LegR, "Shin", new Vector3(0f, -0.42f, 0f), new Vector3(0.20f, 0.82f, 0.24f), pants);

            // Torso (the relation-tinted tunic) and belt.
            var torso = Cube(model.transform, "Torso", new Vector3(0f, 1.20f, 0f),
                new Vector3(0.52f, 0.62f, 0.30f), p.Tunic ?? new Color(0.5f, 0.5f, 0.55f));
            rig.Torso = torso.transform;
            Cube(model.transform, "Belt", new Vector3(0f, 0.92f, 0f), new Vector3(0.54f, 0.08f, 0.32f),
                new Color(0.20f, 0.15f, 0.10f));

            // Arms: pivots at the shoulders.
            rig.ArmL = Pivot(model.transform, "ArmL", new Vector3(-0.34f, 1.44f, 0f));
            rig.ArmR = Pivot(model.transform, "ArmR", new Vector3(0.34f, 1.44f, 0f));
            Cube(rig.ArmL, "Arm", new Vector3(0f, -0.34f, 0f), new Vector3(0.16f, 0.68f, 0.18f), skin);
            Cube(rig.ArmR, "Arm", new Vector3(0f, -0.34f, 0f), new Vector3(0.16f, 0.68f, 0.18f), skin);

            // Head: its own pivot so it can nod; face details make facing readable.
            rig.Head = Pivot(model.transform, "Head", new Vector3(0f, 1.62f, 0f));
            float hs = 0.32f * p.HeadScale;
            Cube(rig.Head, "Skull", new Vector3(0f, 0.17f, 0f), new Vector3(hs, hs, hs), skin);

            // Face variant: nose and eye proportions.
            float noseScale = FaceNoseScale[face];
            float eyeScale = FaceEyeScale[face];
            Cube(rig.Head, "Nose", new Vector3(0f, 0.14f, hs * 0.55f),
                new Vector3(0.07f * noseScale, 0.07f * noseScale, 0.10f * noseScale), skin);
            Color eye = new Color(0.08f, 0.08f, 0.10f);
            Cube(rig.Head, "EyeL", new Vector3(-hs * 0.24f, 0.21f, hs * 0.51f),
                new Vector3(0.05f * eyeScale, 0.05f * eyeScale, 0.02f), eye);
            Cube(rig.Head, "EyeR", new Vector3(hs * 0.24f, 0.21f, hs * 0.51f),
                new Vector3(0.05f * eyeScale, 0.05f * eyeScale, 0.02f), eye);

            // Hair style: 0 court, 1 long, 2 iroquois, 3 chauve.
            if (hairStyle == 0 || hairStyle == 1)
            {
                Cube(rig.Head, "Hair", new Vector3(0f, 0.17f + (hs * 0.55f), 0f),
                    new Vector3(hs + 0.03f, 0.10f, hs + 0.03f), hairColor);
            }

            if (hairStyle == 1) // long: cap + hair falling down the back
            {
                Cube(rig.Head, "HairBack", new Vector3(0f, 0.02f, -hs * 0.55f),
                    new Vector3(hs * 0.9f, 0.52f, 0.10f), hairColor);
            }
            else if (hairStyle == 2) // iroquois: a tall narrow crest
            {
                Cube(rig.Head, "Mohawk", new Vector3(0f, 0.17f + (hs * 0.72f), 0f),
                    new Vector3(0.08f, 0.16f, hs + 0.06f), hairColor);
            }

            // Beard style: 0 aucune, 1 courte, 2 longue, 3 tressée.
            if (beardStyle == 1)
            {
                Cube(rig.Head, "Beard", new Vector3(0f, 0.02f, hs * 0.45f),
                    new Vector3(hs * 0.8f, 0.24f, 0.10f), beardColor);
            }
            else if (beardStyle == 2)
            {
                Cube(rig.Head, "Beard", new Vector3(0f, -0.10f, hs * 0.45f),
                    new Vector3(hs * 0.8f, 0.48f, 0.10f), beardColor);
            }
            else if (beardStyle == 3)
            {
                Cube(rig.Head, "Beard", new Vector3(0f, -0.02f, hs * 0.45f),
                    new Vector3(hs * 0.8f, 0.30f, 0.10f), beardColor);
                Cube(rig.Head, "Braid", new Vector3(0f, -0.30f, hs * 0.45f),
                    new Vector3(0.08f, 0.32f, 0.08f), beardColor);
            }

            if (p.Tusks)
            {
                Color bone = new Color(0.92f, 0.90f, 0.82f);
                Cube(rig.Head, "TuskL", new Vector3(-0.07f, 0.05f, hs * 0.52f), new Vector3(0.04f, 0.11f, 0.04f), bone);
                Cube(rig.Head, "TuskR", new Vector3(0.07f, 0.05f, hs * 0.52f), new Vector3(0.04f, 0.11f, 0.04f), bone);
            }

            if (p.Ears == 1) // pointed elf ears
            {
                Cube(rig.Head, "EarL", new Vector3(-hs * 0.58f, 0.24f, 0f), new Vector3(0.05f, 0.15f, 0.05f), skin);
                Cube(rig.Head, "EarR", new Vector3(hs * 0.58f, 0.24f, 0f), new Vector3(0.05f, 0.15f, 0.05f), skin);
            }
            else if (p.Ears == 2) // large goblin ears
            {
                Cube(rig.Head, "EarL", new Vector3(-hs * 0.68f, 0.19f, 0f), new Vector3(0.14f, 0.10f, 0.05f), skin);
                Cube(rig.Head, "EarR", new Vector3(hs * 0.68f, 0.19f, 0f), new Vector3(0.14f, 0.10f, 0.05f), skin);
            }

            if (p.Crown)
            {
                Cube(rig.Head, "Crown", new Vector3(0f, 0.17f + (hs * 0.68f), 0f),
                    new Vector3(hs * 0.9f, 0.10f, hs * 0.9f), new Color(0.95f, 0.80f, 0.20f));
            }

            // Only relation-tint player tunics; monsters keep their fixed colours.
            if (p.Tunic == null)
            {
                rig.TintTargets = new Renderer[] { torso.GetComponent<Renderer>() };
            }

            rig.HeadHeight = 2.15f * p.Height;
            return rig;
        }

        private static void AttachWeapon(ModelRig rig, byte classId)
        {
            switch (classId)
            {
                case 1: // Warrior: a sword held ready, blade forward.
                {
                    var w = Pivot(rig.ArmR, "Sword", new Vector3(0f, -0.66f, 0.10f));
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
                    orb.transform.SetParent(rig.ArmR, false);
                    orb.transform.localPosition = new Vector3(0f, -0.70f, 0.14f);
                    orb.transform.localScale = new Vector3(0.17f, 0.17f, 0.17f);
                    Paint(orb, new Color(0.30f, 0.90f, 1f));
                    break;
                }

                case 3: // Ranger: a bow in the off-hand.
                {
                    var w = Pivot(rig.ArmL, "Bow", new Vector3(0f, -0.66f, 0.08f));
                    Color wood = new Color(0.45f, 0.30f, 0.15f);
                    var upper = Cube(w, "LimbUp", new Vector3(0f, 0.24f, 0.06f), new Vector3(0.04f, 0.50f, 0.06f), wood);
                    upper.transform.localRotation = Quaternion.Euler(-14f, 0f, 0f);
                    var lower = Cube(w, "LimbDown", new Vector3(0f, -0.24f, 0.06f), new Vector3(0.04f, 0.50f, 0.06f), wood);
                    lower.transform.localRotation = Quaternion.Euler(14f, 0f, 0f);
                    Cube(w, "String", new Vector3(0f, 0f, -0.02f), new Vector3(0.015f, 0.90f, 0.015f),
                        new Color(0.85f, 0.85f, 0.80f));
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

            // Body slung horizontally along +Z (the facing direction).
            var body = Cube(model.transform, "Body", new Vector3(0f, 0.58f, 0f), new Vector3(0.46f, 0.42f, 1.05f), fur);
            rig.Torso = body.transform;

            // Head + snout on a pivot at the front, so it can lunge/bite.
            rig.Head = Pivot(model.transform, "Head", new Vector3(0f, 0.72f, 0.55f));
            Cube(rig.Head, "Skull", new Vector3(0f, 0.05f, 0.12f), new Vector3(0.34f, 0.30f, 0.34f), fur);
            Cube(rig.Head, "Snout", new Vector3(0f, -0.02f, 0.36f), new Vector3(0.18f, 0.16f, 0.28f), dark);
            Cube(rig.Head, "EyeL", new Vector3(-0.10f, 0.12f, 0.28f), new Vector3(0.05f, 0.05f, 0.02f), eyes);
            Cube(rig.Head, "EyeR", new Vector3(0.10f, 0.12f, 0.28f), new Vector3(0.05f, 0.05f, 0.02f), eyes);
            Cube(rig.Head, "EarL", new Vector3(-0.12f, 0.25f, 0.05f), new Vector3(0.08f, 0.14f, 0.05f), dark);
            Cube(rig.Head, "EarR", new Vector3(0.12f, 0.25f, 0.05f), new Vector3(0.08f, 0.14f, 0.05f), dark);

            if (horns)
            {
                Color bone = new Color(0.90f, 0.85f, 0.70f);
                var hl = Cube(rig.Head, "HornL", new Vector3(-0.16f, 0.28f, 0.02f), new Vector3(0.07f, 0.30f, 0.07f), bone);
                hl.transform.localRotation = Quaternion.Euler(-25f, 0f, -20f);
                var hr = Cube(rig.Head, "HornR", new Vector3(0.16f, 0.28f, 0.02f), new Vector3(0.07f, 0.30f, 0.07f), bone);
                hr.transform.localRotation = Quaternion.Euler(-25f, 0f, 20f);
            }

            // Four legs: front pair on the "arm" pivots, hind pair on the "leg" pivots.
            rig.ArmL = Pivot(model.transform, "FrontL", new Vector3(-0.17f, 0.52f, 0.38f));
            rig.ArmR = Pivot(model.transform, "FrontR", new Vector3(0.17f, 0.52f, 0.38f));
            rig.LegL = Pivot(model.transform, "HindL", new Vector3(-0.17f, 0.52f, -0.38f));
            rig.LegR = Pivot(model.transform, "HindR", new Vector3(0.17f, 0.52f, -0.38f));
            Cube(rig.ArmL, "Leg", new Vector3(0f, -0.26f, 0f), new Vector3(0.13f, 0.52f, 0.15f), dark);
            Cube(rig.ArmR, "Leg", new Vector3(0f, -0.26f, 0f), new Vector3(0.13f, 0.52f, 0.15f), dark);
            Cube(rig.LegL, "Leg", new Vector3(0f, -0.26f, 0f), new Vector3(0.13f, 0.52f, 0.15f), dark);
            Cube(rig.LegR, "Leg", new Vector3(0f, -0.26f, 0f), new Vector3(0.13f, 0.52f, 0.15f), dark);

            // Tail, angled up and back, wags while moving.
            rig.Tail = Pivot(model.transform, "Tail", new Vector3(0f, 0.68f, -0.52f));
            var tail = Cube(rig.Tail, "TailMesh", new Vector3(0f, 0.08f, -0.20f), new Vector3(0.10f, 0.10f, 0.45f), dark);
            tail.transform.localRotation = Quaternion.Euler(-20f, 0f, 0f);

            rig.HeadHeight = 1.25f * scale;
            return rig;
        }

        // -------------------------------------------------------------- Corpse

        private static ModelRig BuildCorpse(Transform parent)
        {
            var rig = new ModelRig { HeadHeight = 1.0f };

            var model = new GameObject("Model");
            model.transform.SetParent(parent, false);

            Cube(model.transform, "Mound", new Vector3(0f, 0.08f, 0f), new Vector3(0.90f, 0.16f, 0.90f),
                new Color(0.35f, 0.30f, 0.26f));
            Cube(model.transform, "Stone", new Vector3(0f, 0.38f, -0.25f), new Vector3(0.50f, 0.60f, 0.10f),
                new Color(0.55f, 0.55f, 0.58f));

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

        private static Transform Pivot(Transform parent, string name, Vector3 localPos)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            return go.transform;
        }

        private static GameObject Cube(Transform parent, string name, Vector3 localPos, Vector3 scale, Color color)
        {
            GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
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
