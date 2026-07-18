using UnityEngine;

namespace Aetheria.UnityClient
{
    /// <summary>A spawned external (Mixamo) character: its root and its Animator.</summary>
    public sealed class ExternalCharacterHandle
    {
        public GameObject Root;
        public Animator Animator;
        public float HeadHeight;
    }

    /// <summary>
    /// Runtime loader for artist-made rigged characters (Mixamo & co) dropped into
    /// Assets/Resources/ExternalModels/ and configured via the "Aetheria" editor menu. When a
    /// model and the generated CharacterAnimator are present, players are rendered with REAL
    /// animated characters; otherwise the game falls back to the procedural models seamlessly.
    ///
    /// Recognised model names (first found wins, per race/gender then generic):
    ///   Human_Male, Human_Female, Orc_Male, … , Character (shared fallback for everyone)
    /// </summary>
    public static class ExternalCharacters
    {
        private static RuntimeAnimatorController _controller;
        private static bool _controllerChecked;

        private static readonly System.Collections.Generic.Dictionary<string, GameObject> ModelCache =
            new System.Collections.Generic.Dictionary<string, GameObject>();

        private static readonly string[] RaceNames = { "", "Human", "Orc", "Elf", "Dwarf" };

        public static bool Available
        {
            get
            {
                EnsureController();
                return _controller != null && FindModel(1, 0) != null;
            }
        }

        /// <summary>Instantiate the best-matching external character under a parent transform.</summary>
        public static ExternalCharacterHandle Create(Transform parent, byte raceId, byte gender)
        {
            EnsureController();
            GameObject model = FindModel(raceId, gender);
            if (model == null || _controller == null)
            {
                return null;
            }

            GameObject root = Object.Instantiate(model, parent, false);
            root.name = "ExternalCharacter";

            // Normalise to game scale (~1.85 units tall) whatever the FBX export scale was.
            float height = MeasureHeight(root);
            if (height > 0.01f)
            {
                float k = 1.85f / height;
                root.transform.localScale = root.transform.localScale * k;
            }

            Animator animator = root.GetComponent<Animator>();
            if (animator == null)
            {
                animator = root.AddComponent<Animator>();
            }

            animator.runtimeAnimatorController = _controller;
            animator.applyRootMotion = false; // the SERVER moves bodies; clips only pose them

            return new ExternalCharacterHandle
            {
                Root = root,
                Animator = animator,
                HeadHeight = 2.0f,
            };
        }

        private static void EnsureController()
        {
            if (_controllerChecked)
            {
                return;
            }

            _controllerChecked = true;
            _controller = Resources.Load<RuntimeAnimatorController>("ExternalModels/CharacterAnimator");
        }

        private static GameObject FindModel(byte raceId, byte gender)
        {
            string race = raceId < RaceNames.Length ? RaceNames[raceId] : "Human";
            string sex = gender == 1 ? "Female" : "Male";
            string[] candidates = { race + "_" + sex, race, "Character" };

            foreach (string name in candidates)
            {
                GameObject cached;
                if (ModelCache.TryGetValue(name, out cached))
                {
                    if (cached != null) { return cached; }
                    continue; // known missing
                }

                GameObject loaded = Resources.Load<GameObject>("ExternalModels/" + name);
                ModelCache[name] = loaded;
                if (loaded != null)
                {
                    return loaded;
                }
            }

            return null;
        }

        private static float MeasureHeight(GameObject root)
        {
            Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                return 0f;
            }

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            return bounds.size.y;
        }
    }
}
