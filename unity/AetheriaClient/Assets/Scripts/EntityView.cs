using Aetheria.Shared.Combat;
using Aetheria.Shared.Items;
using Aetheria.Shared.Protocol;
using UnityEngine;

namespace Aetheria.UnityClient
{
    /// <summary>
    /// The visual for one networked entity: a procedurally built low-poly model (see
    /// <see cref="CharacterModelBuilder"/>) that smoothly interpolates toward the server's
    /// authoritative position and is animated in code — walk cycle from measured velocity,
    /// attack swings triggered by combat events, an idle breathing bob, and a hit flash.
    /// </summary>
    public sealed class EntityView : MonoBehaviour
    {
        private const float LerpSpeed = 12f;
        private const float AttackDuration = 0.6f; // a full, weighty WoW-pace swing
        private const float HitFlashDuration = 0.18f;
        private const float JumpDuration = 0.6f;
        private const float JumpHeight = 1.1f;

        private Vector3 _targetPosition;
        private Quaternion _targetRotation = Quaternion.identity;
        private bool _hasTarget;

        private Transform _body; // rotated child, so facing doesn't fight the position lerp
        private ModelRig _rig;

        // Animation state.
        private Vector3 _lastPosition;
        private float _smoothedSpeed;
        private float _walkPhase;
        private float _attackTimer;
        private float _flashTimer;
        private float _jumpTimer;
        private bool _wasJumpFlag;
        private float _torsoBaseY;

        // External (Mixamo) character: when present, its Animator replaces procedural animation.
        private Animator _extAnimator;
        private float _extHeadHeight;

        // Colour bookkeeping: base colour per renderer, so the hit flash can restore them.
        private Renderer[] _parts = new Renderer[0];
        private Color[] _baseColors = new Color[0];
        private Color _lastTint;
        private bool _hasTint;
        private bool _colorsDirty;

        public int EntityId { get; private set; }
        public EntityKind Kind { get; private set; }
        public int Health { get; private set; }
        public int MaxHealth { get; private set; }
        public bool IsBoss { get; private set; }
        public string DisplayName { get; private set; } = "";
        public int Level { get; private set; } = 1;
        public Faction Faction { get; private set; }

        /// <summary>Ability being incanted (0 = none) and its progress, for nameplate cast bars.</summary>
        public byte CastAbilityId { get; private set; }
        public float CastFraction { get; private set; }

        // The gear this model was built with — a change means the LOOK changed: rebuild.
        private readonly byte[] _builtEquipment = new byte[EquipSlots.Count];

        private void RememberEquipment(EntitySnapshot snapshot)
        {
            for (int i = 0; i < _builtEquipment.Length; i++)
            {
                _builtEquipment[i] = snapshot.EquippedIn((EquipSlot)i);
            }
        }

        private bool EquipmentChanged(EntitySnapshot snapshot)
        {
            for (int i = 0; i < _builtEquipment.Length; i++)
            {
                if (snapshot.EquippedIn((EquipSlot)i) != _builtEquipment[i]) { return true; }
            }

            return false;
        }

        /// <summary>Where nameplates and prompts should anchor, above the model's head.</summary>
        public float HeadHeight
        {
            get { return _extAnimator != null ? _extHeadHeight : _rig != null ? _rig.HeadHeight : 2f; }
        }

        public static EntityView Create(EntitySnapshot snapshot)
        {
            var go = new GameObject(snapshot.Kind + "#" + snapshot.Id);
            var view = go.AddComponent<EntityView>();
            view.EntityId = snapshot.Id;
            view.Kind = snapshot.Kind;

            var body = new GameObject("Body");
            body.transform.SetParent(go.transform, false);
            view._body = body.transform;

            // Players: prefer an artist-made rigged character (Mixamo) when one is installed.
            if (snapshot.Kind == EntityKind.Player && ExternalCharacters.Available)
            {
                ExternalCharacterHandle ext = ExternalCharacters.Create(
                    body.transform, snapshot.RaceId, (byte)snapshot.Gender);
                if (ext != null)
                {
                    view._extAnimator = ext.Animator;
                    view._extHeadHeight = ext.HeadHeight;
                }
            }

            if (view._extAnimator == null)
            {
                view._rig = CharacterModelBuilder.Build(body.transform, snapshot);
            }

            view.RememberEquipment(snapshot);
            view.CaptureParts();
            return view;
        }

        private void CaptureParts()
        {
            _parts = _body.GetComponentsInChildren<Renderer>();
            _baseColors = new Color[_parts.Length];
            for (int i = 0; i < _parts.Length; i++)
            {
                _baseColors[i] = _parts[i].material.color;
            }

            _hasTint = false; // relation tint re-applies on the next snapshot
        }

        /// <summary>Tear the procedural model down and rebuild it (gear changed = new look).</summary>
        private void RebuildModel(EntitySnapshot snapshot)
        {
            for (int i = _body.childCount - 1; i >= 0; i--)
            {
                Transform old = _body.GetChild(i);
                old.SetParent(null, false); // detach FIRST so CaptureParts can't see the dying parts
                Object.Destroy(old.gameObject);
            }

            _rig = CharacterModelBuilder.Build(_body, snapshot);
            RememberEquipment(snapshot);
            _torsoBaseY = 0f;
            CaptureParts();
        }

        /// <summary>Apply the latest authoritative snapshot for this entity.</summary>
        public void ApplySnapshot(EntitySnapshot snapshot, Faction viewerFaction, bool isSelf)
        {
            Health = snapshot.Health;
            MaxHealth = snapshot.MaxHealth;
            DisplayName = snapshot.Name;
            Level = snapshot.Level;
            Faction = snapshot.Faction;
            IsBoss = Kind == EntityKind.Monster && snapshot.MaxHealth >= 300;
            CastAbilityId = snapshot.CastAbilityId;
            CastFraction = snapshot.CastProgress / 255f;

            // Gear changed → the silhouette changes with it (visible loot!).
            if (Kind == EntityKind.Player && _extAnimator == null && EquipmentChanged(snapshot))
            {
                RebuildModel(snapshot);
            }

            // Server plane (X, Y) maps onto Unity ground plane (X, Z); models stand on y=0.
            _targetPosition = new Vector3(snapshot.Position.X, 0f, snapshot.Position.Y);
            if (!_hasTarget)
            {
                transform.position = _targetPosition; // first sight: snap, don't glide across the map
                _lastPosition = _targetPosition;
                _hasTarget = true;
            }

            // Facing: server plane angle (0 = +X) → Unity yaw on the ground plane.
            if (Kind == EntityKind.Player || Kind == EntityKind.Monster)
            {
                var facingDir = new Vector3(
                    Mathf.Cos(snapshot.FacingRadians), 0f, Mathf.Sin(snapshot.FacingRadians));
                if (facingDir.sqrMagnitude > 0.001f)
                {
                    _targetRotation = Quaternion.LookRotation(facingDir, Vector3.up);
                }
            }

            // Jump relay: when the server flags this entity as jumping, play the hop (the local
            // player already triggered it on key-press; TriggerJump ignores double-starts).
            if (snapshot.IsJumping && !_wasJumpFlag)
            {
                TriggerJump();
            }

            _wasJumpFlag = snapshot.IsJumping;

            // Relation tint on the tunic: you are green, allies blue, the enemy faction red.
            if (Kind == EntityKind.Player && _rig != null && _rig.TintTargets.Length > 0)
            {
                Color tint = isSelf
                    ? new Color(0.25f, 0.9f, 0.35f)
                    : snapshot.Faction == viewerFaction
                        ? new Color(0.30f, 0.55f, 1f)
                        : new Color(1f, 0.25f, 0.25f);

                if (!_hasTint || tint != _lastTint)
                {
                    _lastTint = tint;
                    _hasTint = true;
                    for (int i = 0; i < _rig.TintTargets.Length; i++)
                    {
                        Renderer target = _rig.TintTargets[i];
                        for (int j = 0; j < _parts.Length; j++)
                        {
                            if (ReferenceEquals(_parts[j], target)) { _baseColors[j] = tint; }
                        }
                    }

                    _colorsDirty = true;
                }
            }
        }

        /// <summary>This entity just landed a hit: play the attack swing.</summary>
        public void TriggerAttack()
        {
            if (_extAnimator != null)
            {
                _extAnimator.SetTrigger("Attack");
                return;
            }

            _attackTimer = AttackDuration;
        }

        /// <summary>This entity just took a hit: flash its model red for a moment.</summary>
        public void TriggerHit()
        {
            _flashTimer = HitFlashDuration;
        }

        private GameObject _selectionRing;

        /// <summary>Show/hide the WoW-style selection ring at this entity's feet.</summary>
        public void SetSelected(bool selected)
        {
            if (selected && _selectionRing == null)
            {
                _selectionRing = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                _selectionRing.name = "SelectionRing";
                Object.Destroy(_selectionRing.GetComponent<Collider>());
                _selectionRing.transform.SetParent(transform, false);
                _selectionRing.transform.localPosition = new Vector3(0f, 0.03f, 0f);
                _selectionRing.transform.localScale = new Vector3(1.5f, 0.015f, 1.5f);
                var renderer = _selectionRing.GetComponent<Renderer>();
                if (renderer != null)
                {
                    renderer.material.color = new Color(0.95f, 0.35f, 0.2f);
                }
            }

            if (_selectionRing != null)
            {
                _selectionRing.SetActive(selected);
            }
        }

        /// <summary>Start the cosmetic jump arc (no-op while one is already in the air).</summary>
        public void TriggerJump()
        {
            if (_extAnimator != null)
            {
                _extAnimator.SetTrigger("Jump"); // the clip carries the motion; no manual arc
                return;
            }

            if (_jumpTimer <= 0f)
            {
                _jumpTimer = JumpDuration;
            }
        }

        private void Update()
        {
            float dt = Time.deltaTime;

            if (_hasTarget)
            {
                transform.position = Vector3.Lerp(transform.position, _targetPosition, dt * LerpSpeed);
            }

            if (_body != null && (Kind == EntityKind.Player || Kind == EntityKind.Monster))
            {
                _body.rotation = Quaternion.Slerp(_body.rotation, _targetRotation, dt * 14f);
            }

            // Jump arc: a clean parabola lifted through the body child (position lerp stays flat).
            if (_body != null)
            {
                float jumpY = 0f;
                if (_jumpTimer > 0f)
                {
                    _jumpTimer -= dt;
                    float t = 1f - Mathf.Clamp01(_jumpTimer / JumpDuration); // 0 → 1
                    jumpY = JumpHeight * 4f * t * (1f - t);
                }

                Vector3 lp = _body.localPosition;
                _body.localPosition = new Vector3(lp.x, jumpY, lp.z);
            }

            // Measure how fast the view is actually moving to drive the walk cycle.
            if (dt > 0f)
            {
                Vector3 delta = transform.position - _lastPosition;
                delta.y = 0f;
                float speed = delta.magnitude / dt;
                _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, speed, dt * 10f);
                _lastPosition = transform.position;
            }

            // External characters: their Animator does the acting — feed it the measured speed.
            if (_extAnimator != null)
            {
                _extAnimator.SetFloat("Speed", _smoothedSpeed);
            }
            else
            {
                Animate(dt);
            }

            ApplyColors();
        }

        // ------------------------------------------------------------- Animation

        private void Animate(float dt)
        {
            if (_rig == null || (Kind != EntityKind.Player && Kind != EntityKind.Monster))
            {
                return; // corpses, remains and NPCs hold still
            }

            // Walk cycle: stride frequency and swing amplitude scale with measured speed.
            float stride = Mathf.Clamp01(_smoothedSpeed / 5f);
            _walkPhase += _smoothedSpeed * 2.4f * dt;
            float swing = Mathf.Sin(_walkPhase) * 38f * stride;

            if (_rig.Quadruped)
            {
                // Diagonal pairs, like a trot.
                SetPivotX(_rig.ArmL, swing);
                SetPivotX(_rig.LegR, swing);
                SetPivotX(_rig.ArmR, -swing);
                SetPivotX(_rig.LegL, -swing);

                if (_rig.Tail != null)
                {
                    float wag = Mathf.Sin(Time.time * 4f) * (8f + (14f * stride));
                    _rig.Tail.localRotation = Quaternion.Euler(0f, wag, 0f);
                }

                // Bite: the head lunges forward and down during an attack.
                if (_rig.Head != null)
                {
                    float bite = AttackCurve() * 28f;
                    _rig.Head.localRotation = Quaternion.Euler(bite, 0f, 0f);
                }
            }
            else
            {
                // Legs swing opposite each other; arms counter-swing.
                SetPivotX(_rig.LegL, swing);
                SetPivotX(_rig.LegR, -swing);
                SetPivotX(_rig.ArmL, -swing * 0.7f);

                // The weapon arm: walk counter-swing, overridden by the attack swing.
                float atk = AttackCurve();
                float armAngle = atk > 0f
                    ? Mathf.Lerp(20f, -105f, atk) // wind up slightly, then strike down/forward
                    : swing * 0.7f;
                SetPivotX(_rig.ArmR, armAngle);

                // Idle breathing: a subtle torso pulse when standing still.
                if (_rig.Torso != null)
                {
                    if (_torsoBaseY <= 0f) { _torsoBaseY = _rig.Torso.localScale.y; }
                    float breathe = 1f + (Mathf.Sin(Time.time * 2f) * 0.015f * (1f - stride));
                    Vector3 s = _rig.Torso.localScale;
                    _rig.Torso.localScale = new Vector3(s.x, _torsoBaseY * breathe, s.z);
                }
            }

            if (_attackTimer > 0f)
            {
                _attackTimer -= dt;
            }
        }

        /// <summary>0 when not attacking; rises to 1 mid-swing and returns to 0.</summary>
        private float AttackCurve()
        {
            if (_attackTimer <= 0f)
            {
                return 0f;
            }

            float t = 1f - (_attackTimer / AttackDuration); // 0 → 1 over the swing
            return Mathf.Sin(t * Mathf.PI);
        }

        private static void SetPivotX(Transform pivot, float degrees)
        {
            if (pivot != null)
            {
                pivot.localRotation = Quaternion.Euler(degrees, 0f, 0f);
            }
        }

        // --------------------------------------------------------------- Colours

        private void ApplyColors()
        {
            if (_flashTimer > 0f)
            {
                _flashTimer -= Time.deltaTime;
                float k = Mathf.Clamp01(_flashTimer / HitFlashDuration) * 0.75f;
                for (int i = 0; i < _parts.Length; i++)
                {
                    if (_parts[i] != null)
                    {
                        _parts[i].material.color = Color.Lerp(_baseColors[i], Color.red, k);
                    }
                }

                _colorsDirty = true; // restore base colours once the flash fades out
            }
            else if (_colorsDirty)
            {
                for (int i = 0; i < _parts.Length; i++)
                {
                    if (_parts[i] != null)
                    {
                        _parts[i].material.color = _baseColors[i];
                    }
                }

                _colorsDirty = false;
            }
        }
    }
}
