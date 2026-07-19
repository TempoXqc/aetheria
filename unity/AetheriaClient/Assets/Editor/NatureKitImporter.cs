using System.IO;
using UnityEditor;
using UnityEngine;

namespace Aetheria.UnityClient.EditorTools
{
    /// <summary>
    /// When the free "Nature Starter Kit 2" asset (or any folder whose name starts with
    /// "NatureStarterKit") is imported into the project, this copies its PREFABS into
    /// Resources/NatureKit so the runtime world dressing can load them by name — trees, bushes,
    /// grass and rocks with real textures. Runs once at editor load; safe to re-run (it only
    /// copies what's missing). Zero manual clicking.
    /// </summary>
    public static class NatureKitImporter
    {
        private const string TargetFolder = "Assets/Resources/NatureKit";

        [InitializeOnLoadMethod]
        private static void CopyKitPrefabs()
        {
            EditorApplication.delayCall += () =>
            {
                // Find the kit wherever the user dropped it.
                string kitRoot = null;
                foreach (string dir in Directory.GetDirectories("Assets"))
                {
                    string name = Path.GetFileName(dir);
                    if (name.StartsWith("NatureStarterKit"))
                    {
                        kitRoot = dir.Replace('\\', '/');
                        break;
                    }
                }

                if (kitRoot == null)
                {
                    return; // kit not imported: nothing to do
                }

                string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { kitRoot });
                if (guids.Length == 0)
                {
                    return;
                }

                if (!AssetDatabase.IsValidFolder(TargetFolder))
                {
                    AssetDatabase.CreateFolder("Assets/Resources", "NatureKit");
                }

                int copied = 0;
                foreach (string guid in guids)
                {
                    string source = AssetDatabase.GUIDToAssetPath(guid);
                    string dest = TargetFolder + "/" + Path.GetFileName(source);
                    if (AssetDatabase.LoadAssetAtPath<GameObject>(dest) == null)
                    {
                        if (AssetDatabase.CopyAsset(source, dest)) { copied++; }
                    }
                }

                if (copied > 0)
                {
                    AssetDatabase.Refresh();
                    Debug.Log("[Aetheria] Nature Starter Kit détecté : " + copied +
                              " prefab(s) copié(s) dans Resources/NatureKit — le monde va s'en servir.");
                }
            };
        }
    }
}
