using UnityEngine;

namespace Aetheria.UnityClient
{
    /// <summary>
    /// WoW-style third-person chase camera: perspective projection, hovering behind the character's
    /// shoulder, smoothly following both position and facing. Mouse wheel zooms in and out. (The
    /// class keeps its historical name — it started life as an isometric rig.)
    /// </summary>
    public sealed class IsoCameraRig : MonoBehaviour
    {
        private const float Pitch = 26f;          // downward tilt, WoW-like
        private const float FollowLerp = 8f;      // position smoothing
        private const float YawLerp = 4f;         // rotation smoothing (mouse-driven facing swings a lot)
        private const float MinZoom = 4f;
        private const float MaxZoom = 20f;

        private Camera _camera;
        private Vector3 _focus;
        private float _yaw;                       // smoothed camera yaw, degrees
        private float _distance = 11f;

        /// <summary>World point the camera should keep centred (the player).</summary>
        public Vector3 Target { get; set; }

        /// <summary>The character's facing on the server plane, radians (0 = +X). Camera trails it.</summary>
        public float TargetFacingRadians { get; set; }

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            if (_camera != null)
            {
                _camera.orthographic = false;
                _camera.fieldOfView = 55f;
            }

            _focus = Target;
            _yaw = DesiredYaw();
            Place();
        }

        private void LateUpdate()
        {
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.0001f)
            {
                _distance = Mathf.Clamp(_distance - (scroll * 5f), MinZoom, MaxZoom);
            }

            _focus = Vector3.Lerp(_focus, Target, Time.deltaTime * FollowLerp);
            _yaw = Mathf.LerpAngle(_yaw, DesiredYaw(), Time.deltaTime * YawLerp);
            Place();
        }

        /// <summary>Behind the character: camera yaw = the facing direction's yaw.</summary>
        private float DesiredYaw()
        {
            var dir = new Vector3(Mathf.Cos(TargetFacingRadians), 0f, Mathf.Sin(TargetFacingRadians));
            return Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        }

        private void Place()
        {
            Quaternion rot = Quaternion.Euler(Pitch, _yaw, 0f);
            Vector3 eye = _focus + new Vector3(0f, 1.6f, 0f) - (rot * Vector3.forward * _distance);
            transform.position = eye;
            transform.rotation = rot;
        }
    }
}
