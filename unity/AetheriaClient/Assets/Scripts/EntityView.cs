using Aetheria.Shared.Combat;
using Aetheria.Shared.Protocol;
using UnityEngine;

namespace Aetheria.UnityClient
{
    /// <summary>
    /// The visual for one networked entity: a primitive (capsule for characters, block for corpses)
    /// that smoothly interpolates toward the server's authoritative position. The server ticks at
    /// 20 Hz; interpolation makes 20 Hz look continuous.
    /// </summary>
    public sealed class EntityView : MonoBehaviour
    {
        private const float LerpSpeed = 12f;

        private Renderer _renderer;
        private Vector3 _targetPosition;
        private Quaternion _targetRotation = Quaternion.identity;
        private bool _hasTarget;

        public int EntityId { get; private set; }
        public EntityKind Kind { get; private set; }
        public int Health { get; private set; }
        public int MaxHealth { get; private set; }
        public bool IsBoss { get; private set; }

        private Transform _body; // rotated child, so facing doesn't fight the position lerp

        public static EntityView Create(int entityId, EntityKind kind)
        {
            var go = new GameObject(kind + "#" + entityId);
            var view = go.AddComponent<EntityView>();
            view.EntityId = entityId;
            view.Kind = kind;

            PrimitiveType shape = kind == EntityKind.Corpse ? PrimitiveType.Cube : PrimitiveType.Capsule;
            GameObject body = GameObject.CreatePrimitive(shape);
            body.name = "Body";
            Object.Destroy(body.GetComponent<Collider>()); // visuals only; the server owns physics/hits
            body.transform.SetParent(go.transform, false);
            view._body = body.transform;
            view._renderer = body.GetComponent<Renderer>();

            if (kind == EntityKind.Corpse)
            {
                body.transform.localScale = new Vector3(0.9f, 0.25f, 0.9f);
            }
            else
            {
                // A small "nose" so the facing direction is readable at a glance.
                GameObject nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
                nose.name = "Nose";
                Object.Destroy(nose.GetComponent<Collider>());
                nose.transform.SetParent(body.transform, false);
                nose.transform.localScale = new Vector3(0.22f, 0.22f, 0.45f);
                nose.transform.localPosition = new Vector3(0f, 0.35f, 0.5f);
            }

            return view;
        }

        /// <summary>Apply the latest authoritative snapshot for this entity.</summary>
        public void ApplySnapshot(EntitySnapshot snapshot, Faction viewerFaction, bool isSelf)
        {
            Health = snapshot.Health;
            MaxHealth = snapshot.MaxHealth;

            // Server plane (X, Y) maps onto Unity ground plane (X, Z).
            float y = Kind == EntityKind.Corpse ? 0.15f : 1f;
            _targetPosition = new Vector3(snapshot.Position.X, y, snapshot.Position.Y);
            if (!_hasTarget)
            {
                transform.position = _targetPosition; // first sight: snap, don't glide across the map
                _hasTarget = true;
            }

            // Facing: server plane angle (0 = +X) → Unity yaw on the ground plane.
            if (Kind != EntityKind.Corpse)
            {
                var facingDir = new Vector3(
                    Mathf.Cos(snapshot.FacingRadians), 0f, Mathf.Sin(snapshot.FacingRadians));
                if (facingDir.sqrMagnitude > 0.001f)
                {
                    _targetRotation = Quaternion.LookRotation(facingDir, Vector3.up);
                }
            }

            // Raid-scale monsters read as bosses: bulk them up.
            IsBoss = Kind == EntityKind.Monster && snapshot.MaxHealth >= 300;
            if (Kind == EntityKind.Monster)
            {
                float s = IsBoss ? 2.2f : 1f;
                transform.localScale = new Vector3(s, s, s);
            }

            if (_renderer != null)
            {
                _renderer.material.color = PickColor(snapshot, viewerFaction, isSelf);
            }
        }

        private Color PickColor(EntitySnapshot snapshot, Faction viewerFaction, bool isSelf)
        {
            if (isSelf)
            {
                return new Color(0.25f, 0.9f, 0.35f); // you: green
            }

            switch (Kind)
            {
                case EntityKind.Player:
                    return snapshot.Faction == viewerFaction
                        ? new Color(0.30f, 0.55f, 1f)   // ally: blue
                        : new Color(1f, 0.25f, 0.25f);  // enemy faction: red
                case EntityKind.Monster:
                    return IsBoss
                        ? new Color(0.65f, 0.10f, 0.55f) // boss: violet
                        : new Color(1f, 0.62f, 0.15f);   // monster: orange
                case EntityKind.Corpse:
                    return new Color(0.45f, 0.42f, 0.40f);
                default:
                    return Color.white;
            }
        }

        private void Update()
        {
            if (_hasTarget)
            {
                transform.position = Vector3.Lerp(transform.position, _targetPosition, Time.deltaTime * LerpSpeed);
            }

            if (_body != null && Kind != EntityKind.Corpse)
            {
                _body.rotation = Quaternion.Slerp(_body.rotation, _targetRotation, Time.deltaTime * 14f);
            }
        }
    }
}
