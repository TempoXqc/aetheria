using UnityEditor;
using UnityEngine;

namespace Aetheria.UnityClient.EditorTools
{
    /// <summary>
    /// The Quaternius base-body meshes go through RUNTIME TRIANGLE SURGERY (PruneToHead keeps
    /// only the head), which requires CPU-readable meshes — but Unity imports FBX with
    /// Read/Write disabled by default. This fixer flips isReadable on for every model under
    /// Resources/Quaternius, both for future imports (postprocessor) and for anything already
    /// imported (startup sweep). Zero manual clicking.
    /// </summary>
    public sealed class QuaterniusImportFixer : AssetPostprocessor
    {
        private const string Folder = "Assets/Resources/Quaternius";

        private void OnPreprocessModel()
        {
            if (assetPath.Replace('\\', '/').StartsWith(Folder))
            {
                var importer = (ModelImporter)assetImporter;
                importer.isReadable = true;
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
                    if (importer != null && !importer.isReadable)
                    {
                        importer.isReadable = true;
                        importer.SaveAndReimport();
                        fixedCount++;
                    }
                }

                if (fixedCount > 0)
                {
                    Debug.Log("[Aetheria] Read/Write activé sur " + fixedCount +
                              " modèle(s) Quaternius (requis pour la découpe de tête).");
                }
            };
        }
    }
}
