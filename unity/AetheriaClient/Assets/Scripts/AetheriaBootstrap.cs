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
            Tex.Apply(ground, "grass", tileX: 90f, tileY: 90f); // real dirt-and-blades surface

            // Light: warm sun with SOFT SHADOWS, plus a cool ambient fill so shade stays readable.
            var lightGo = new GameObject("Sun");
            Light sun = lightGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.15f;
            sun.color = new Color(1f, 0.96f, 0.88f);
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.75f;
            lightGo.transform.rotation = Quaternion.Euler(55f, 30f, 0f);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.45f, 0.52f);

            // Isometric camera rig.
            Camera cam = Camera.main;
            if (cam == null)
            {
                var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = camGo.AddComponent<Camera>();
                camGo.AddComponent<AudioListener>();
            }

            cam.orthographic = false;               // WoW-style third-person view
            cam.fieldOfView = 55f;
            cam.backgroundColor = new Color(0.35f, 0.55f, 0.80f); // daytime sky
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.gameObject.AddComponent<IsoCameraRig>();

            // The client itself.
            var clientGo = new GameObject("AetheriaClient");
            clientGo.AddComponent<AetheriaClientBehaviour>();
            Object.DontDestroyOnLoad(clientGo);
        }
    }
}
