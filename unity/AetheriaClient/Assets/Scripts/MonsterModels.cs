using System.Collections.Generic;
using UnityEngine;

namespace Aetheria.UnityClient
{
    /// <summary>A spawned real monster: its root, its clip player, and where nameplates anchor.</summary>
    public sealed class MonsterHandle
    {
        public GameObject Root;
        public MonsterAnimator Animator;
        public float HeadHeight;
    }

    /// <summary>
    /// Real animated monsters from the Quaternius "Ultimate Monsters" pack (CC0), dropped into
    /// Resources/Monsters. Every FBX ships a full clip set (Idle / Walk / Run / Punch / HitReact /
    /// Death) on one shared atlas texture. The models are imported as LEGACY animation (see
    /// Editor/MonsterImportFixer) so clips can be played straight from code — no controller asset.
    /// When the pack (or its clips) are missing, callers fall back to the procedural monsters.
    /// </summary>
    public static class MonsterModels
    {
        private static bool _checked;
        private static bool _available;
        private static Texture2D _atlas;

        public static bool Available
        {
            get
            {
                if (!_checked)
                {
                    _checked = true;
                    _available = Resources.Load<GameObject>("Monsters/Orc") != null;
                }

                return _available;
            }
        }

        /// <summary>Which pack model plays which game monster, and at what size.</summary>
        private struct Cast
        {
            public string Model;
            public float Height;
            public Color Tint;
        }

        private static Cast For(byte defId)
        {
            switch (defId)
            {
                case 2: // Dire Wolf: the Dino beast, greyed toward wolf fur
                    return new Cast { Model = "Dino", Height = 1.5f, Tint = new Color(0.68f, 0.64f, 0.62f) };
                case 3: // Goblin King: the skull-crowned orc, towering
                    return new Cast { Model = "Orc_Skull", Height = 2.45f, Tint = Color.white };
                case 4: // Ashmaw the Devourer: the demon, huge
                    return new Cast { Model = "Demon", Height = 3.1f, Tint = Color.white };
                default: // Goblin Grunt (and unknown monsters): the green orc
                    return new Cast { Model = "Orc", Height = 1.55f, Tint = Color.white };
            }
        }

        /// <summary>Spawn the LIVING monster: normalized size, atlas texture, clips bound.</summary>
        public static MonsterHandle Create(Transform parent, byte defId)
        {
            return Spawn(parent, defId, remains: false);
        }

        /// <summary>Spawn the SLAIN monster: it plays its death clip once and stays down.</summary>
        public static MonsterHandle CreateRemains(Transform parent, byte defId)
        {
            return Spawn(parent, defId, remains: true);
        }

        private static MonsterHandle Spawn(Transform parent, byte defId, bool remains)
        {
            if (!Available)
            {
                return null;
            }

            Cast cast = For(defId);
            Object[] assets = Resources.LoadAll("Monsters/" + cast.Model);
            if (assets == null || assets.Length == 0)
            {
                return null;
            }

            GameObject prefab = null;
            var clips = new List<AnimationClip>();
            foreach (Object asset in assets)
            {
                if (prefab == null && asset is GameObject go) { prefab = go; }
                if (asset is AnimationClip clip && clip.legacy) { clips.Add(clip); }
            }

            // No legacy clips = the editor import fixer hasn't run yet: let the caller fall back
            // to the procedural monster rather than showing a frozen statue.
            if (prefab == null || clips.Count == 0)
            {
                return null;
            }

            var pivot = new GameObject("Monster_" + cast.Model);
            pivot.transform.SetParent(parent, false);

            GameObject root = Object.Instantiate(prefab, pivot.transform, false);
            // This pack already exports facing forward (+Z): no flip. (The character packs
            // need one; these don't — flipping them made every monster walk and attack backwards.)
            root.transform.localRotation = Quaternion.identity;

            // Height-normalize by MEASURED bounds (immune to the pack's export scale), then
            // stand the feet exactly on the ground.
            Bounds bounds = MeasureBounds(root);
            if (bounds.size.y > 0.001f)
            {
                float k = cast.Height / bounds.size.y;
                root.transform.localScale = Vector3.one * k;
                bounds = MeasureBounds(root);
                root.transform.localPosition = new Vector3(0f, -bounds.min.y, 0f);
            }

            WireAtlas(root, cast.Tint, remains);

            // The clip player lives on the FBX root — that's what the curves' paths bind to.
            Animation anim = root.GetComponent<Animation>();
            if (anim == null)
            {
                anim = root.AddComponent<Animation>();
            }

            MonsterAnimator driver = pivot.AddComponent<MonsterAnimator>();
            driver.Bind(anim, clips, remains);

            return new MonsterHandle
            {
                Root = pivot,
                Animator = driver,
                HeadHeight = cast.Height + 0.3f,
            };
        }

        private static Bounds MeasureBounds(GameObject root)
        {
            Renderer[] parts = root.GetComponentsInChildren<Renderer>();
            var bounds = new Bounds(root.transform.position, Vector3.zero);
            bool first = true;
            foreach (Renderer part in parts)
            {
                if (part is SkinnedMeshRenderer skinned)
                {
                    skinned.updateWhenOffscreen = true; // animated bounds; never cull mid-swing
                }

                if (first) { bounds = part.bounds; first = false; }
                else { bounds.Encapsulate(part.bounds); }
            }

            return bounds;
        }

        private static void WireAtlas(GameObject root, Color tint, bool remains)
        {
            if (_atlas == null)
            {
                _atlas = Resources.Load<Texture2D>("Monsters/Atlas_Monsters");
                if (_atlas != null)
                {
                    _atlas.filterMode = FilterMode.Point; // a palette atlas: crisp blocks, no bleed
                }
            }

            Color color = remains
                ? new Color(tint.r * 0.45f, tint.g * 0.45f, tint.b * 0.45f) // death pallor
                : tint;

            foreach (Renderer part in root.GetComponentsInChildren<Renderer>())
            {
                foreach (Material mat in part.materials)
                {
                    if (_atlas != null) { mat.mainTexture = _atlas; }
                    mat.color = color;
                }
            }
        }
    }

    /// <summary>
    /// Drives a legacy-clip monster: Idle/Walk/Run picked from measured speed, with Punch,
    /// HitReact and Death as one-shots that briefly own the body. Clip names come from the pack
    /// ("CharacterArmature|Idle", ...), matched by suffix so any Quaternius monster drops in.
    /// </summary>
    public sealed class MonsterAnimator : MonoBehaviour
    {
        private Animation _anim;
        private string _idle, _walk, _run, _attack, _hit, _death;
        private float _lock; // seconds a one-shot still owns the body
        private bool _dead;

        public void Bind(Animation anim, List<AnimationClip> clips, bool remains)
        {
            _anim = anim;
            foreach (AnimationClip clip in clips)
            {
                if (_anim.GetClip(clip.name) == null)
                {
                    _anim.AddClip(clip, clip.name);
                }

                string n = clip.name;
                if (n.EndsWith("|Idle")) { _idle = n; Loop(n); }
                else if (n.EndsWith("|Walk")) { _walk = n; Loop(n); }
                else if (n.EndsWith("|Run")) { _run = n; Loop(n); }
                else if (n.EndsWith("|Punch")) { _attack = n; Once(n); }
                else if (n.EndsWith("|HitReact")) { _hit = n; Once(n); }
                else if (n.EndsWith("|Death")) { _death = n; Clamp(n); }
            }

            if (remains)
            {
                _dead = true;
                if (_death != null) { _anim.Play(_death); } // falls over, stays down
            }
            else if (_idle != null)
            {
                _anim.Play(_idle);
            }
        }

        private void Loop(string clip) { _anim[clip].wrapMode = WrapMode.Loop; }

        private void Once(string clip) { _anim[clip].wrapMode = WrapMode.Once; }

        private void Clamp(string clip) { _anim[clip].wrapMode = WrapMode.ClampForever; }

        /// <summary>Locomotion from measured speed; one-shots keep priority while they play.</summary>
        public void SetSpeed(float speed)
        {
            if (_dead || _anim == null)
            {
                return;
            }

            if (_lock > 0f)
            {
                _lock -= Time.deltaTime;
                return;
            }

            string want = speed > 3.4f && _run != null ? _run
                : speed > 0.25f && _walk != null ? _walk
                : _idle;
            if (want != null && !_anim.IsPlaying(want))
            {
                _anim.CrossFade(want, 0.18f);
            }
        }

        public void PlayAttack()
        {
            if (_dead || _anim == null || _attack == null)
            {
                return;
            }

            _anim.CrossFade(_attack, 0.08f);
            _anim[_attack].time = 0f;
            _lock = _anim[_attack].length * 0.85f;
        }

        public void PlayHit()
        {
            // Never interrupt an attack mid-swing with a flinch.
            if (_dead || _anim == null || _hit == null || _lock > 0f)
            {
                return;
            }

            _anim.CrossFade(_hit, 0.06f);
            _anim[_hit].time = 0f;
            _lock = _anim[_hit].length * 0.8f;
        }
    }
}
