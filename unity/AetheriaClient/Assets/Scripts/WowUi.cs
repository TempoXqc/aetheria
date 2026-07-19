using UnityEngine;

namespace Aetheria.UnityClient
{
    /// <summary>
    /// The WoW-Classic-flavoured interface kit: deep-red buttons with a gold trim, near-black
    /// parchment panels with double borders, golden headings. Every texture is generated at
    /// runtime (no asset files), 9-sliced through GUIStyle borders so any size looks right.
    /// </summary>
    public static class WowUi
    {
        private static bool _built;
        private static Texture2D _button, _buttonHover, _panel, _field, _slot, _highlight;
        private static GUIStyle _btn, _panelStyle, _fieldStyle, _title, _gold, _goldCenter, _small;

        // ------------------------------------------------------------ Palette
        private static readonly Color GoldLight = new Color(1.00f, 0.82f, 0.10f);
        private static readonly Color Trim = new Color(0.72f, 0.58f, 0.22f);
        private static readonly Color TrimDark = new Color(0.35f, 0.27f, 0.10f);
        private static readonly Color RedTop = new Color(0.64f, 0.10f, 0.08f);
        private static readonly Color RedBottom = new Color(0.36f, 0.04f, 0.04f);
        private static readonly Color PanelBg = new Color(0.055f, 0.05f, 0.06f, 0.94f);
        private static readonly Color FieldBg = new Color(0.02f, 0.02f, 0.025f, 0.98f);

        private static void EnsureBuilt()
        {
            if (_built) { return; }

            _built = true;
            _button = MakeFramed(32, 32, RedTop, RedBottom, Trim, 2);
            _buttonHover = MakeFramed(32, 32, new Color(0.82f, 0.16f, 0.12f), new Color(0.48f, 0.07f, 0.06f), GoldLight, 2);
            _panel = MakeFramed(32, 32, PanelBg, PanelBg, Trim, 2, inner: TrimDark);
            _field = MakeFramed(32, 32, FieldBg, FieldBg, TrimDark, 2);
            _slot = MakeFramed(32, 32, new Color(0.10f, 0.10f, 0.12f, 0.96f), new Color(0.05f, 0.05f, 0.06f, 0.96f), TrimDark, 2);
            _highlight = MakeFramed(32, 32, new Color(0.35f, 0.28f, 0.05f, 0.55f), new Color(0.25f, 0.20f, 0.03f, 0.55f), GoldLight, 2);

            _btn = new GUIStyle(GUI.skin.button)
            {
                richText = true,
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
            };
            _btn.normal.background = _button;
            _btn.normal.textColor = GoldLight;
            _btn.hover.background = _buttonHover;
            _btn.hover.textColor = new Color(1f, 0.95f, 0.6f);
            _btn.border = new RectOffset(6, 6, 6, 6);

            _panelStyle = new GUIStyle(GUI.skin.box) { richText = true };
            _panelStyle.normal.background = _panel;
            _panelStyle.border = new RectOffset(6, 6, 6, 6);

            _fieldStyle = new GUIStyle(GUI.skin.textField) { richText = false };
            _fieldStyle.normal.background = _field;
            _fieldStyle.normal.textColor = Color.white;
            _fieldStyle.border = new RectOffset(4, 4, 4, 4);

            _title = new GUIStyle(GUI.skin.label)
            {
                richText = true,
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                fontSize = 20,
            };
            _title.normal.textColor = GoldLight;

            _gold = new GUIStyle(GUI.skin.label) { richText = true, fontStyle = FontStyle.Bold };
            _gold.normal.textColor = GoldLight;

            _goldCenter = new GUIStyle(_gold) { alignment = TextAnchor.MiddleCenter };

            _small = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 10, wordWrap = true };
            _small.normal.textColor = new Color(0.92f, 0.90f, 0.82f);
        }

        // ------------------------------------------------------------ Widgets

        /// <summary>A WoW red button with gold trim.</summary>
        public static bool Button(Rect r, string label)
        {
            EnsureBuilt();
            return GUI.Button(r, label, _btn);
        }

        /// <summary>A dark bordered panel; optional golden heading centred on its top edge.</summary>
        public static void Panel(Rect r, string title = null)
        {
            EnsureBuilt();
            GUI.Box(r, "", _panelStyle);
            if (!string.IsNullOrEmpty(title))
            {
                GUI.Label(new Rect(r.x, r.y + 8, r.width, 26), title, _title);
            }
        }

        /// <summary>A golden-trim selection highlight (the chosen character/race).</summary>
        public static void Highlight(Rect r)
        {
            EnsureBuilt();
            GUI.Box(r, "", new GUIStyle { normal = { background = _highlight }, border = new RectOffset(4, 4, 4, 4) });
        }

        public static string TextField(Rect r, string value, bool password = false)
        {
            EnsureBuilt();
            return password ? GUI.PasswordField(r, value, '*', 24, _fieldStyle) : GUI.TextField(r, value, 24, _fieldStyle);
        }

        public static void Title(Rect r, string text)
        {
            EnsureBuilt();
            // A soft shadow keeps the gold readable over any backdrop.
            GUIStyle shadow = new GUIStyle(_title);
            shadow.normal.textColor = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(r.x + 1, r.y + 2, r.width, r.height), text, shadow);
            GUI.Label(r, text, _title);
        }

        public static void Gold(Rect r, string text) { EnsureBuilt(); GUI.Label(r, text, _gold); }

        public static void GoldCentered(Rect r, string text) { EnsureBuilt(); GUI.Label(r, text, _goldCenter); }

        public static void Body(Rect r, string text) { EnsureBuilt(); GUI.Label(r, text, _small); }

        /// <summary>An action-bar slot background (dark, thin gold trim).</summary>
        public static void Slot(Rect r)
        {
            EnsureBuilt();
            GUI.Box(r, "", new GUIStyle { normal = { background = _slot }, border = new RectOffset(4, 4, 4, 4) });
        }

        // ----------------------------------------------------------- Portrait

        private static Texture2D _disc, _ring;

        /// <summary>A WoW-style round portrait: coloured disc, gold ring, a big initial.</summary>
        public static void Portrait(Rect r, Color fill, string initial)
        {
            EnsureBuilt();
            if (_disc == null)
            {
                _disc = MakeDisc(48, Color.white, ring: false);
                _ring = MakeDisc(48, Trim, ring: true);
            }

            Color prev = GUI.color;
            GUI.color = fill;
            GUI.DrawTexture(r, _disc);
            GUI.color = prev;
            GUI.DrawTexture(r, _ring);

            var big = new GUIStyle(GUI.skin.label)
            {
                richText = true,
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold,
                fontSize = Mathf.RoundToInt(r.height * 0.42f),
            };
            big.normal.textColor = new Color(1f, 1f, 1f, 0.92f);
            GUI.Label(r, initial, big);
        }

        private static Texture2D MakeDisc(int size, Color color, bool ring)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            float c = (size - 1) / 2f;
            float rOut = c;
            float rIn = c - 2.6f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt(((x - c) * (x - c)) + ((y - c) * (y - c)));
                    Color px;
                    if (ring)
                    {
                        px = d <= rOut && d >= rIn ? color : new Color(0f, 0f, 0f, 0f);
                    }
                    else
                    {
                        px = d <= rOut ? color : new Color(0f, 0f, 0f, 0f);
                    }

                    tex.SetPixel(x, y, px);
                }
            }

            tex.Apply();
            return tex;
        }

        // ----------------------------------------------------------- Textures

        /// <summary>A vertical-gradient tile with an outer frame (and optional inner line).</summary>
        private static Texture2D MakeFramed(int w, int h, Color top, Color bottom, Color frame,
            int frameWidth, Color? inner = null)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            for (int y = 0; y < h; y++)
            {
                float t = h <= 1 ? 0f : y / (float)(h - 1);
                Color row = Color.Lerp(bottom, top, t);
                for (int x = 0; x < w; x++)
                {
                    bool isFrame = x < frameWidth || y < frameWidth || x >= w - frameWidth || y >= h - frameWidth;
                    bool isInner = !isFrame && inner.HasValue &&
                                   (x < frameWidth + 1 || y < frameWidth + 1 ||
                                    x >= w - frameWidth - 1 || y >= h - frameWidth - 1);
                    tex.SetPixel(x, y, isFrame ? frame : isInner ? inner.Value : row);
                }
            }

            tex.Apply();
            return tex;
        }
    }
}
