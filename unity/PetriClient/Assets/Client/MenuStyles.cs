using System.Collections.Generic;
using UnityEngine;

namespace Petri.Client
{
    /// <summary>
    /// Centralized palette, dimensions, textures, and GUIStyles for the menu. Everything is
    /// cached: textures are generated once (procedural rounded rects and gradients — never per
    /// frame), styles are rebuilt only when the UI scale bucket changes. Layout code elsewhere
    /// works in 1280x720 reference units multiplied by <see cref="Scale"/>, so the menu holds
    /// together from 1280x720 through 1440p and ultrawide.
    /// </summary>
    internal sealed class MenuStyles
    {
        // ---- Palette: restrained microbial-laboratory look. -----------------------
        private static readonly Color BgTop = new Color(0.045f, 0.080f, 0.058f);   // near-black green
        private static readonly Color BgBottom = new Color(0.014f, 0.030f, 0.022f);
        private static readonly Color GlowGreen = new Color(0.35f, 0.80f, 0.45f, 0.055f);
        private static readonly Color PanelFill = new Color(0.070f, 0.100f, 0.078f, 0.97f);
        private static readonly Color PanelEdge = new Color(0.200f, 0.290f, 0.220f);
        private static readonly Color Accent = new Color(0.560f, 0.860f, 0.500f);   // soft green, primary
        private static readonly Color AccentDim = new Color(0.300f, 0.480f, 0.290f);
        private static readonly Color Cyan = new Color(0.450f, 0.780f, 0.740f);     // muted cyan, secondary
        private static readonly Color TextMain = new Color(0.910f, 0.930f, 0.880f); // off-white
        private static readonly Color TextDim = new Color(0.520f, 0.570f, 0.520f);  // disabled / secondary
        private static readonly Color ErrorRed = new Color(0.940f, 0.450f, 0.400f); // invalid input only
        // Amber (0.90, 0.72, 0.35) is reserved for warnings; nothing warns yet.

        // ---- Shared dimensions in 1280x720 reference units. ------------------------
        public const float MenuButtonW = 400f;
        public const float LabelW = 150f;

        /// <summary>Current UI scale: min(w/1280, h/720) clamped and bucketed to 0.25 steps.</summary>
        public float Scale { get; private set; } = 1f;

        public GUIStyle Title, Tagline, PageTitle, SectionLabel, Body, Dim, Error,
            Primary, Secondary, Start, StartOff, DisabledTitle, ComingSoon,
            Chip, ChipOn, SeatChip, Field, Panel, DisabledTile;

        public Texture2D BgGradient, Glow, AccentBar;

        private Texture2D _panelTex, _primTex, _primHover, _primDown,
            _secTex, _secHover, _secDown,
            _startTex, _startHover, _startDown, _startOffTex,
            _disabledTex, _chipTex, _chipHover, _chipOnTex, _fieldTex;

        private readonly List<Texture2D> _owned = new List<Texture2D>();
        private float _builtScale = -1f;
        private bool _texturesBuilt;

        /// <summary>Call once per OnGUI before drawing; cheap when nothing changed.</summary>
        public void Ensure()
        {
            if (!_texturesBuilt)
            {
                BuildTextures();
                _texturesBuilt = true;
            }
            float raw = Mathf.Min(Screen.width / 1280f, Screen.height / 720f);
            float s = Mathf.Clamp(Mathf.Round(raw * 4f) * 0.25f, 0.75f, 2.5f);
            if (Mathf.Approximately(_builtScale, s)) return;
            Scale = s;
            BuildStyles();
            _builtScale = s;
        }

        // ---- Textures (built once; radius is baked at texture scale and nine-sliced). ----

        private void BuildTextures()
        {
            BgGradient = VerticalGradient(BgTop, BgBottom);
            Glow = RadialGlow(GlowGreen);
            AccentBar = Solid(Accent);

            _panelTex = Rounded(PanelFill, PanelEdge);

            _primTex = Rounded(new Color(0.130f, 0.230f, 0.140f), AccentDim);
            _primHover = Rounded(new Color(0.170f, 0.300f, 0.180f), Accent);
            _primDown = Rounded(new Color(0.100f, 0.170f, 0.110f), AccentDim);

            _secTex = Rounded(new Color(0.105f, 0.140f, 0.115f), new Color(0.235f, 0.300f, 0.250f));
            _secHover = Rounded(new Color(0.135f, 0.180f, 0.150f), new Color(0.360f, 0.560f, 0.530f));
            _secDown = Rounded(new Color(0.085f, 0.115f, 0.095f), new Color(0.235f, 0.300f, 0.250f));

            _startTex = Rounded(Accent, new Color(0.680f, 0.950f, 0.620f));
            _startHover = Rounded(new Color(0.630f, 0.930f, 0.570f), new Color(0.760f, 0.980f, 0.700f));
            _startDown = Rounded(new Color(0.450f, 0.720f, 0.400f), AccentDim);
            _startOffTex = Rounded(new Color(0.110f, 0.140f, 0.120f), new Color(0.200f, 0.240f, 0.200f));

            _disabledTex = Rounded(new Color(0.075f, 0.100f, 0.082f), new Color(0.150f, 0.195f, 0.160f));

            _chipTex = Rounded(new Color(0.095f, 0.130f, 0.105f), new Color(0.210f, 0.270f, 0.220f));
            _chipHover = Rounded(new Color(0.125f, 0.170f, 0.140f), new Color(0.300f, 0.400f, 0.340f));
            _chipOnTex = Rounded(new Color(0.095f, 0.185f, 0.175f), Cyan);

            _fieldTex = Rounded(new Color(0.050f, 0.075f, 0.058f), new Color(0.220f, 0.300f, 0.240f));
        }

        private void BuildStyles()
        {
            int P(float v) => Mathf.RoundToInt(v * Scale);

            GUIStyle Text(int size, Color c, TextAnchor anchor, FontStyle fs, bool wrap)
            {
                var st = new GUIStyle
                {
                    fontSize = size, alignment = anchor, fontStyle = fs, wordWrap = wrap,
                };
                st.normal.textColor = c;
                return st;
            }

            GUIStyle Btn(Texture2D normal, Texture2D hover, Texture2D down,
                Color text, Color textHover, int size, FontStyle fs)
            {
                var st = new GUIStyle
                {
                    fontSize = size, fontStyle = fs, alignment = TextAnchor.MiddleCenter,
                    border = new RectOffset(10, 10, 10, 10), clipping = TextClipping.Clip,
                };
                st.normal.background = normal;
                st.normal.textColor = text;
                st.hover.background = hover;
                st.hover.textColor = textHover;
                st.active.background = down;
                st.active.textColor = text;
                st.focused.background = normal;
                st.focused.textColor = text;
                return st;
            }

            Title = Text(P(58), new Color(0.850f, 0.960f, 0.740f), TextAnchor.MiddleCenter, FontStyle.Bold, false);
            Tagline = Text(P(15), new Color(0.420f, 0.620f, 0.580f), TextAnchor.MiddleCenter, FontStyle.Normal, false);
            PageTitle = Text(P(26), TextMain, TextAnchor.MiddleCenter, FontStyle.Bold, false);
            SectionLabel = Text(P(14), TextDim, TextAnchor.MiddleLeft, FontStyle.Normal, false);
            Body = Text(P(16), TextMain, TextAnchor.MiddleCenter, FontStyle.Normal, false);
            Dim = Text(P(12), TextDim, TextAnchor.MiddleLeft, FontStyle.Italic, true);
            Error = Text(P(12), ErrorRed, TextAnchor.MiddleLeft, FontStyle.Normal, true);

            Primary = Btn(_primTex, _primHover, _primDown,
                new Color(0.780f, 0.950f, 0.700f), new Color(0.880f, 1f, 0.800f), P(18), FontStyle.Bold);
            Secondary = Btn(_secTex, _secHover, _secDown, TextMain, TextMain, P(15), FontStyle.Normal);
            Start = Btn(_startTex, _startHover, _startDown,
                new Color(0.040f, 0.090f, 0.050f), new Color(0.040f, 0.090f, 0.050f), P(19), FontStyle.Bold);
            StartOff = Btn(_startOffTex, _startOffTex, _startOffTex, TextDim, TextDim, P(19), FontStyle.Bold);

            DisabledTitle = Text(P(15), TextDim, TextAnchor.MiddleCenter, FontStyle.Normal, false);
            ComingSoon = Text(P(10), new Color(0.400f, 0.550f, 0.520f), TextAnchor.MiddleCenter, FontStyle.Normal, false);

            Chip = Btn(_chipTex, _chipHover, _chipTex,
                new Color(0.760f, 0.800f, 0.750f), TextMain, P(13), FontStyle.Normal);
            ChipOn = Btn(_chipOnTex, _chipOnTex, _chipOnTex,
                new Color(0.850f, 0.970f, 0.940f), new Color(0.850f, 0.970f, 0.940f), P(13), FontStyle.Bold);
            SeatChip = Btn(_chipTex, _chipHover, _chipTex,
                new Color(0.760f, 0.800f, 0.750f), TextMain, P(11), FontStyle.Normal);

            Field = new GUIStyle
            {
                fontSize = P(14), alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip,
                border = new RectOffset(8, 8, 8, 8),
                padding = new RectOffset(P(10), P(10), 0, 0),
            };
            Field.normal.background = _fieldTex;
            Field.normal.textColor = TextMain;
            Field.hover.background = _fieldTex;
            Field.hover.textColor = TextMain;
            Field.focused.background = _fieldTex;
            Field.focused.textColor = TextMain;

            Panel = new GUIStyle { border = new RectOffset(12, 12, 12, 12) };
            Panel.normal.background = _panelTex;

            DisabledTile = new GUIStyle { border = new RectOffset(10, 10, 10, 10) };
            DisabledTile.normal.background = _disabledTex;
        }

        // ---- Procedural texture helpers. -------------------------------------------

        private Texture2D Track(Texture2D tex)
        {
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            _owned.Add(tex);
            return tex;
        }

        private Texture2D Solid(Color c)
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, c);
            tex.Apply(false, false);
            return Track(tex);
        }

        /// <summary>Rounded-rect with a 1px-antialiased edge and a border ring; nine-sliced
        /// by the styles, so one 32px texture serves every button size.</summary>
        private Texture2D Rounded(Color fill, Color border)
        {
            const int size = 32;
            const float radius = 8f;
            const float borderW = 2f;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Signed distance to the rounded-rect edge (negative inside).
                    float dx = Mathf.Max(Mathf.Abs(x + 0.5f - size * 0.5f) - (size * 0.5f - radius), 0f);
                    float dy = Mathf.Max(Mathf.Abs(y + 0.5f - size * 0.5f) - (size * 0.5f - radius), 0f);
                    float dist = Mathf.Sqrt(dx * dx + dy * dy) - radius;
                    float coverage = Mathf.Clamp01(0.5f - dist);
                    Color c = Color.Lerp(fill, border, Mathf.Clamp01(dist + borderW + 0.5f));
                    c.a *= coverage;
                    px[y * size + x] = c;
                }
            }
            tex.SetPixels(px);
            tex.Apply(false, true);
            return Track(tex);
        }

        private Texture2D VerticalGradient(Color top, Color bottom)
        {
            const int steps = 128;
            var tex = new Texture2D(1, steps, TextureFormat.RGBA32, false);
            for (int i = 0; i < steps; i++)
                tex.SetPixel(0, i, Color.Lerp(bottom, top, i / (float)(steps - 1)));
            tex.Apply(false, true);
            return Track(tex);
        }

        private Texture2D RadialGlow(Color c)
        {
            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color[size * size];
            float half = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x - half) * (x - half) + (y - half) * (y - half)) / half;
                    float a = Mathf.Pow(Mathf.Clamp01(1f - d), 2f);
                    px[y * size + x] = new Color(c.r, c.g, c.b, c.a * a);
                }
            }
            tex.SetPixels(px);
            tex.Apply(false, true);
            return Track(tex);
        }
    }
}
