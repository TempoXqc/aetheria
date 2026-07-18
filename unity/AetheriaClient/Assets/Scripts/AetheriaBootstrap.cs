using UnityEngine;

namespace Aetheria.UnityClient
{
    /// <summary>
    /// Zero-setup entry point: press Play in ANY scene (even a brand-new empty one) and this builds
    /// the whole client — network behaviour, isometric camera rig, light, and ground. No prefabs,
    /// no scene wiring, nothing to configure.
    /// </summary>
    public static class AetheriaBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (Object.FindObjectOfType<AetheriaClientBehaviour>() != null)
            {
                return; // A hand-placed client already exists; respect it.
            }

            // Ground plane (the server simulates on a flat plane; X/Z here maps to server X/Y).
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.localScale = new Vector3(40f, 1f, 40f); // 400x400 units
            ground.GetComponent<Renderer>().material.color = new Color(0.22f, 0.30f, 0.20f);

            // Light.
            var lightGo = new GameObject("Sun");
            Light sun = lightGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.1f;
            lightGo.transform.rotation = Quaternion.Euler(55f, 30f, 0f);

            // Isometric camera rig.
            Camera cam = Camera.main;
            if (cam == null)
            {
                var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = camGo.AddComponent<Camera>();
                camGo.AddComponent<AudioListener>();
            }

            cam.orthographic = true;
            cam.orthographicSize = 12f;
            cam.backgroundColor = new Color(0.09f, 0.10f, 0.13f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.gameObject.AddComponent<IsoCameraRig>();

            // The client itself.
            var clientGo = new GameObject("AetheriaClient");
            clientGo.AddComponent<AetheriaClientBehaviour>();
            Object.DontDestroyOnLoad(clientGo);
        }
    }
}
