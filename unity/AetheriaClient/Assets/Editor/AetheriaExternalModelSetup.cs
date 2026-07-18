using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Aetheria.UnityClient.EditorTools
{
    /// <summary>
    /// One-click Mixamo integration. Drop your Mixamo FBX files into
    /// Assets/Resources/ExternalModels/, then run the menu item below: every model is switched to
    /// a HUMANOID rig, cycle clips (idle/walk/run) are set to loop, and a CharacterAnimator
    /// controller is generated with Idle ⇄ Walk driven by Speed, plus Attack and Jump triggers.
    /// The game then uses these characters automatically (see ExternalCharacters.cs).
    /// </summary>
    public static class AetheriaExternalModelSetup
    {
        private const string Root = "Assets/Resources/ExternalModels";
        private const string ControllerPath = Root + "/CharacterAnimator.controller";

        [MenuItem("Aetheria/Configurer les modèles externes (Mixamo)")]
        public static void Configure()
        {
            if (!AssetDatabase.IsValidFolder(Root))
            {
                EditorUtility.DisplayDialog("Aetheria",
                    "Dossier introuvable : " + Root + "\nDépose d'abord tes FBX Mixamo dedans.", "OK");
                return;
            }

            // 1) Every FBX becomes a Humanoid rig; loopable clips loop.
            string[] guids = AssetDatabase.FindAssets("t:Model", new[] { Root });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer == null)
                {
                    continue;
                }

                importer.animationType = ModelImporterAnimationType.Human;
                importer.importAnimation = true;

                ModelImporterClipAnimation[] clips = importer.defaultClipAnimations;
                foreach (ModelImporterClipAnimation clip in clips)
                {
                    string n = clip.name.ToLowerInvariant();
                    clip.loopTime = n.Contains("idle") || n.Contains("walk") || n.Contains("run");
                }

                if (clips.Length > 0)
                {
                    importer.clipAnimations = clips;
                }

                importer.SaveAndReimport();
            }

            // 2) Collect every animation clip found in the folder.
            var allClips = new List<AnimationClip>();
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (asset is AnimationClip clip && !clip.name.StartsWith("__preview"))
                    {
                        allClips.Add(clip);
                    }
                }
            }

            AnimationClip Find(params string[] keys)
            {
                foreach (string key in keys)
                {
                    AnimationClip hit = allClips.FirstOrDefault(
                        c => c.name.ToLowerInvariant().Contains(key));
                    if (hit != null)
                    {
                        return hit;
                    }
                }

                return null;
            }

            AnimationClip idle = Find("idle");
            AnimationClip walk = Find("walk", "run");
            AnimationClip attack = Find("attack", "slash", "punch", "swing");
            AnimationClip jump = Find("jump");

            if (idle == null || walk == null)
            {
                EditorUtility.DisplayDialog("Aetheria",
                    "Il manque au minimum une animation 'Idle' et une 'Walk' (ou 'Run').\n" +
                    "Clips trouvés : " + string.Join(", ", allClips.Select(c => c.name)), "OK");
                return;
            }

            // 3) Build the animator controller.
            AssetDatabase.DeleteAsset(ControllerPath);
            AnimatorController ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);
            ctrl.AddParameter("Attack", AnimatorControllerParameterType.Trigger);
            ctrl.AddParameter("Jump", AnimatorControllerParameterType.Trigger);

            AnimatorStateMachine sm = ctrl.layers[0].stateMachine;
            AnimatorState stIdle = sm.AddState("Idle");
            stIdle.motion = idle;
            AnimatorState stWalk = sm.AddState("Walk");
            stWalk.motion = walk;
            sm.defaultState = stIdle;

            AnimatorStateTransition toWalk = stIdle.AddTransition(stWalk);
            toWalk.AddCondition(AnimatorConditionMode.Greater, 0.5f, "Speed");
            toWalk.hasExitTime = false;
            toWalk.duration = 0.15f;

            AnimatorStateTransition toIdle = stWalk.AddTransition(stIdle);
            toIdle.AddCondition(AnimatorConditionMode.Less, 0.5f, "Speed");
            toIdle.hasExitTime = false;
            toIdle.duration = 0.15f;

            if (attack != null)
            {
                AnimatorState stAttack = sm.AddState("Attack");
                stAttack.motion = attack;
                AnimatorStateTransition any = sm.AddAnyStateTransition(stAttack);
                any.AddCondition(AnimatorConditionMode.If, 0f, "Attack");
                any.hasExitTime = false;
                any.duration = 0.05f;
                AnimatorStateTransition back = stAttack.AddTransition(stIdle);
                back.hasExitTime = true;
                back.exitTime = 0.9f;
                back.duration = 0.1f;
            }

            if (jump != null)
            {
                AnimatorState stJump = sm.AddState("Jump");
                stJump.motion = jump;
                AnimatorStateTransition any = sm.AddAnyStateTransition(stJump);
                any.AddCondition(AnimatorConditionMode.If, 0f, "Jump");
                any.hasExitTime = false;
                any.duration = 0.05f;
                AnimatorStateTransition back = stJump.AddTransition(stIdle);
                back.hasExitTime = true;
                back.exitTime = 0.9f;
                back.duration = 0.1f;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorUtility.DisplayDialog("Aetheria",
                "Modèles externes configurés !\n" +
                $"Clips : idle='{idle.name}', walk='{walk.name}', " +
                $"attack='{(attack != null ? attack.name : "—")}', jump='{(jump != null ? jump.name : "—")}'.\n" +
                "Lance le jeu : les personnages Mixamo remplacent les personnages procéduraux.", "OK");
        }
    }
}
