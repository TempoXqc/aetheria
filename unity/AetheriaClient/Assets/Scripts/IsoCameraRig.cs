using UnityEngine;

namespace Aetheria.UnityClient
{
    /// <summary>
    /// Classic isometric rig: fixed 3/4 angle, orthographic projection, smoothly following a target.
    /// Mouse wheel zooms. The camera never rotates — readability first, like the genre demands.
    /// </summary>
    public sealed class IsoCameraRig : MonoBehaviour
    {
        private const float Pitch = 35f;
        private const float Yaw = 45f;
        private const float Distance = 40f;
        private const float FollowLerp = 8f;
        private const float MinZoom = 6f;
        private const float MaxZoom = 24f;

        private Camera _camera;
        private Vector3 _focus;

        /// <summary>World point the camera should keep centred (the player).</summary>
        public Vector3 Target { get; set; }

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            transform.rotation = Quaternion.Euler(Pitch, Yaw, 0f);
            _focus = Target;
            SnapToFocus();
        }

        private void LateUpdate()
        {
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (_camera != null && Mathf.Abs(scroll) > 0.0001f)
            {
                _camera.orthographicSize =
                    Mathf.Clamp(_camera.orthographicSize - (scroll * 6f), MinZoom, MaxZoom);
            }

            _focus = Vector3.Lerp(_focus, Target, Time.deltaTime * FollowLerp);
            SnapToFocus();
        }

        private void SnapToFocus()
        {
            transform.position = _focus - (transform.forward * Distance);
        }
    }
}
