using UnityEngine;

namespace Aetheria.UnityClient
{
    /// <summary>
    /// WoW-style third-person camera. The camera owns its own yaw/pitch and only moves when YOU
    /// move it: hold the RIGHT mouse button and drag to look around (the character turns with the
    /// camera, exactly like WoW's mouselook); mouse wheel zooms. It never chases the cursor —
    /// no more self-spinning view. (The class keeps its historical name.)
    /// </summary>
    public sealed class IsoCameraRig : MonoBehaviour
    {
        private const float FollowLerp = 10f;   // position smoothing
        private const float MinZoom = 4f;
        private const float MaxZoom = 20f;
        private const float MinPitch = 8f;
        private const float MaxPitch = 65f;
        private const float Sensitivity = 3.2f; // degrees per mouse-axis unit while right-dragging

        private Camera _camera;
        private Vector3 _focus;
        private float _distance = 11f;

        /// <summary>World point the camera keeps centred (the player).</summary>
        public Vector3 Target { get; set; }

        /// <summary>Camera yaw in degrees. The character's facing is derived from this.</summary>
        public float Yaw { get; private set; } = 45f;

        /// <summary>Camera pitch in degrees (right-drag up/down).</summary>
        public float Pitch { get; private set; } = 26f;

        /// <summary>The camera yaw expressed as a server-plane facing angle (radians, 0 = +X).</summary>
        public float FacingRadians
        {
            get
            {
                float yawRad = Yaw * Mathf.Deg2Rad;
                // Unity forward for this yaw is (sin, 0, cos); server plane maps (X, Y) = (x, z).
                return Mathf.Atan2(Mathf.Cos(yawRad), Mathf.Sin(yawRad));
            }
        }

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            if (_camera != null)
            {
                _camera.orthographic = false;
                _camera.fieldOfView = 55f;
            }

            _focus = Target;
            Place();
        }

        /// <summary>Set by the UI each frame: while true, mouse drags belong to a window
        /// (portrait spin, item drag) and must NOT steer the camera.</summary>
        public bool SuppressDrag;

        /// <summary>True while either mouse button is steering the camera.</summary>
        public bool Dragging
        {
            get { return !SuppressDrag && (Input.GetMouseButton(0) || Input.GetMouseButton(1)); }
        }

        private void LateUpdate()
        {
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.0001f)
            {
                _distance = Mathf.Clamp(_distance - (scroll * 5f), MinZoom, MaxZoom);
            }

            // WoW mouselook: RIGHT drag turns camera AND character (the behaviour reads our yaw);
            // LEFT drag orbits the camera freely WITHOUT touching the character's direction.
            if (Dragging)
            {
                Yaw += Input.GetAxis("Mouse X") * Sensitivity;
                Pitch = Mathf.Clamp(Pitch - (Input.GetAxis("Mouse Y") * Sensitivity), MinPitch, MaxPitch);
            }

            _focus = Vector3.Lerp(_focus, Target, Time.deltaTime * FollowLerp);
            Place();
        }

        /// <summary>
        /// Softly swing the camera back behind the character (called while running with no drag,
        /// like WoW's camera-follow). Never fights an active drag.
        /// </summary>
        public void RecenterBehind(float facingRadians, float dt)
        {
            if (Dragging)
            {
                return;
            }

            var dir = new Vector3(Mathf.Cos(facingRadians), 0f, Mathf.Sin(facingRadians));
            float desired = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            Yaw = Mathf.LerpAngle(Yaw, desired, dt * 2.2f);
        }

        private void Place()
        {
            Quaternion rot = Quaternion.Euler(Pitch, Yaw, 0f);
            Vector3 eye = _focus + new Vector3(0f, 1.6f, 0f) - (rot * Vector3.forward * _distance);
            transform.position = eye;
            transform.rotation = rot;
        }
    }
}
