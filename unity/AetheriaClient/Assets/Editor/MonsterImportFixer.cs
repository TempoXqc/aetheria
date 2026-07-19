using UnityEditor;
using UnityEngine;

namespace Aetheria.UnityClient.EditorTools
{
    /// <summary>
    /// The Ultimate Monsters FBX ship their animation clips (Idle/Walk/Run/Punch/HitReact/Death)
    /// embedded, and the client plays them from code with the LEGACY Animation component —
    /// no controller asset. Unity imports FBX animation as Generic by default, so this fixer
    /// switches every model under Resources/Monsters to Legacy import, for future imports
    /// (postprocessor) and anything already imported (startup sweep). Zero manual clicking.
    /// </summary>
    public sealed class MonsterImportFixer : AssetPostprocessor
    {
        private const string Folder = "Assets/Resources/Monsters";

        private void OnPreprocessModel()
        {
            if (assetPath.Replace('\\', '/').StartsWith(Folder))
            {
                var importer = (ModelImporter)assetImporter;
                importer.animationType = ModelImporterAnimationType.Legacy;
            }
        }

        [InitializeOnLoadMethod]
        private static void FixAlreadyImported()
        {
            // Delay so the AssetDatabase is fully ready when the editor loads.
            EditorApplication.delayCall += () =>
            {
                string[] guids = AssetDatabase.FindAssets("t:Model", new[] { Folder });
                int fixedCount = 0;
                foreach (string guid in guids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                    if (importer != null && importer.animationType != ModelImporterAnimationType.Legacy)
                    {
                        importer.animationType = ModelImporterAnimationType.Legacy;
                        importer.SaveAndReimport();
                        fixedCount++;
                    }
                }

                if (fixedCount > 0)
                {
                    Debug.Log("[Aetheria] Import Legacy activé sur " + fixedCount +
                              " monstre(s) (requis pour jouer leurs clips d'animation).");
                }
            };
        }
    }
}
