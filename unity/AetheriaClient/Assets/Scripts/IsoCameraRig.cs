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

        private void LateUpdate()
        {
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.0001f)
            {
                _distance = Mathf.Clamp(_distance - (scroll * 5f), MinZoom, MaxZoom);
            }

            // Mouselook: the camera turns ONLY while the right button is held.
            if (Input.GetMouseButton(1))
            {
                Yaw += Input.GetAxis("Mouse X") * Sensitivity;
                Pitch = Mathf.Clamp(Pitch - (Input.GetAxis("Mouse Y") * Sensitivity), MinPitch, MaxPitch);
            }

            _focus = Vector3.Lerp(_focus, Target, Time.deltaTime * FollowLerp);
            Place();
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
