using Aetheria.Shared.Combat;
using Aetheria.Shared.Math;
using Aetheria.Shared.Protocol;
using UnityEngine;

namespace Aetheria.UnityClient
{
    /// <summary>
    /// Live-rendered portraits for the character creation screen: each race+gender is built once
    /// with the REAL model builder, photographed by a one-shot camera into a small RenderTexture,
    /// then torn down — WoW's race buttons and gender silhouettes without shipping a single image,
    /// and they always match the actual in-game models. Cached for the whole session.
    /// </summary>
    public static class RacePortraits
    {
        private static readonly Vector3 Studio = new Vector3(760f, -120f, 760f);

        private static readonly System.Collections.Generic.Dictionary<int, RenderTexture> Cache =
            new System.Collections.Generic.Dictionary<int, RenderTexture>();

        /// <summary>Head-and-shoulders portrait (the race button).</summary>
        public static Texture Head(byte raceId, Gender gender) => Shot(raceId, gender, true);

        /// <summary>Full-body portrait (the gender buttons).</summary>
        public static Texture Body(byte raceId, Gender gender) => Shot(raceId, gender, false);

        private static Texture Shot(byte raceId, Gender gender, bool head)
        {
            int key = raceId | ((byte)gender << 8) | (head ? 1 << 16 : 0);
            RenderTexture cached;
            if (Cache.TryGetValue(key, out cached) && cached != null)
            {
                return cached;
            }

            // A tiny photo studio, far from everything: model + light + one-shot camera.
            var root = new GameObject("PortraitStudio");
            root.transform.position = Studio;

            var snap = new EntitySnapshot(0, EntityKind.Player, Faction.Alliance, Vec2.Zero,
                1, 1, 0, 0, 0f, 1, "", raceId, 1, gender);
            var model = new GameObject("Model");
            model.transform.SetParent(root.transform, false);
            ModelRig rig = CharacterModelBuilder.Build(model.transform, snap);
            float headY = rig != null && rig.HeadHeight > 0.5f ? rig.HeadHeight : 2.0f;

            var lightGo = new GameObject("Key");
            lightGo.transform.SetParent(root.transform, false);
            lightGo.transform.localPosition = new Vector3(0.9f, headY + 0.8f, 1.6f);
            Light light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = 8f;
            light.intensity = 1.6f;

            var rt = new RenderTexture(head ? 96 : 112, head ? 96 : 168, 16);
            var camGo = new GameObject("Shot");
            camGo.transform.SetParent(root.transform, false);
            Vector3 eye = head
                ? new Vector3(0f, headY * 0.96f, headY * 0.62f)   // close on the face
                : new Vector3(0f, headY * 0.55f, headY * 1.55f);  // whole silhouette
            Vector3 aim = head ? new Vector3(0f, headY * 0.92f, 0f) : new Vector3(0f, headY * 0.48f, 0f);
            camGo.transform.localPosition = eye;
            camGo.transform.localRotation = Quaternion.LookRotation(aim - eye, Vector3.up);
            Camera cam = camGo.AddComponent<Camera>();
            cam.enabled = false; // one shot, not every frame
            cam.fieldOfView = head ? 26f : 34f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.07f, 0.07f, 0.10f);
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = null;

            Object.Destroy(root);
            Cache[key] = rt;
            return rt;
        }
    }
}
