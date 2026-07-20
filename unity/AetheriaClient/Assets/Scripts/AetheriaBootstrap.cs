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
        /// <summary>The one sun. The day/night cycle drives it; nothing else casts shadows.</summary>
        public static Light SunLight;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            // An MMO client must keep talking to the server even when its window loses focus —
            // otherwise testing with two windows "kicks" whichever client isn't focused.
            Application.runInBackground = true;

            if (Object.FindObjectOfType<AetheriaClientBehaviour>() != null)
            {
                return; // A hand-placed client already exists; respect it.
            }

            // HAND-BUILT WORLD: put an object named "HandmadeWorld" in the scene (or a Unity
            // Terrain) and the game will NOT generate its ground or decor — your sculpted
            // terrain, trees and buildings stay exactly as you placed them in the editor.
            bool handmade = GameObject.Find("HandmadeWorld") != null
                || Object.FindObjectOfType<Terrain>() != null;

            if (!handmade && !ForestMap.Available)
            {
                // Ground plane (the server simulates on a flat plane; X/Z maps to server X/Y).
                // With the Fantasy Forest pack imported, the REAL terrain replaces this plane
                // (built with the zone decor) — a flat slab underneath would poke through dips.
                GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ground.name = "Ground";
                ground.transform.localScale = new Vector3(40f, 1f, 40f); // 400x400 units
                ground.GetComponent<Renderer>().material.color = new Color(0.30f, 0.34f, 0.18f); // Northshire green-gold
                Tex.Apply(ground, "grass", tileX: 90f, tileY: 90f); // real dirt-and-blades surface
            }

            // Light: a LOW golden sun with soft shadows and a warm ambient — the late-afternoon
            // Elwynn feel, instead of a flat noon.
            var lightGo = new GameObject("Sun");
            Light sun = lightGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.22f;
            sun.color = new Color(1f, 0.90f, 0.72f);
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.8f;
            lightGo.transform.rotation = Quaternion.Euler(38f, 40f, 0f);
            SunLight = sun;

            // ONE shadow only: a default scene often ships its own Directional Light — disable
            // every directional light that isn't our sun, and strip shadows from the rest.
            foreach (Light other in Object.FindObjectsOfType<Light>())
            {
                if (other == sun) { continue; }
                if (other.type == LightType.Directional)
                {
                    other.gameObject.SetActive(false);
                }
                else
                {
                    other.shadows = LightShadows.None;
                }
            }

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.48f, 0.44f, 0.40f);

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
