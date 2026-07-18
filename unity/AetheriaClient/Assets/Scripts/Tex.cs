using System.Collections.Generic;
using UnityEngine;

namespace Aetheria.UnityClient
{
    /// <summary>
    /// Procedurally generated tileable textures (Assets/Resources/Textures/*.png), applied on top
    /// of the primitive models: grass and dirt on the ground, stone on menhirs and rocks, bark and
    /// leaves on trees, planks on fences, straw on haystacks, fur on wolves. Every call degrades
    /// gracefully — if a texture is missing the material simply keeps its flat colour.
    /// </summary>
    public static class Tex
    {
        private static readonly Dictionary<string, Texture2D> Cache = new Dictionary<string, Texture2D>();

        public static Texture2D Get(string name)
        {
            Texture2D found;
            if (!Cache.TryGetValue(name, out found))
            {
                found = Resources.Load<Texture2D>("Textures/" + name);
                Cache[name] = found; // null is cached too: missing files cost one lookup only
            }

            return found;
        }

        /// <summary>
        /// Put a texture on the object's material. The albedo colour becomes a light tint so the
        /// texture's own colours carry the look (tint defaults to near-white).
        /// </summary>
        public static void Apply(GameObject go, string name, float tileX = 1f, float tileY = 1f,
            Color? tint = null)
        {
            if (go == null) { return; }

            Texture2D texture = Get(name);
            if (texture == null) { return; }

            var renderer = go.GetComponent<Renderer>();
            if (renderer == null) { return; }

            Material m = renderer.material;
            m.mainTexture = texture;
            m.mainTextureScale = new Vector2(tileX, tileY);
            m.color = tint ?? new Color(0.96f, 0.96f, 0.96f);
        }
    }
}
