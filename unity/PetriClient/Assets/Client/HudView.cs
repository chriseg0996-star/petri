using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Petri.Core;

namespace Petri.Client
{
    /// <summary>
    /// Superorganism HUD: a top strip with each colony's vitals, a PERSISTENT bottom panel
    /// (organism info on the left, force table in the middle, the always-available build grid
    /// on the right), and the fog-aware minimap. INTERIM (conversion in progress): front
    /// selection/status and the K split arrows land with the fronts UI. Pure IMGUI so the
    /// project needs no UI assets; buttons act through InputController → sim Commands.
    /// </summary>
    public sealed class HudView : MonoBehaviour
    {
        private const float PanelHeight = 196f;
        private const float ButtonSize = 44f;
        private const float ButtonGap = 4f;
        private const int GridCols = 4;

        private MatchBootstrap _match;
        private GUIStyle _label, _small, _header, _button;
        private Texture2D _white;
        private Rect _panelRect;

        // Minimap: 1 texture cell per world unit, rebuilt on a modest cadence, fog-aware.
        private Texture2D _miniTex;
        private Color32[] _miniPixels;
        private float _miniNext;
        private const float MiniWidth = 176f;
        // Minimap blips reuse GameView's palette so the two views never drift apart.
        private static Color32 MiniOwnerColor(int owner) => (Color32)GameView.OwnerColor(owner);

        // Rich-text hexes for the resource colours, taken straight from GameView so HUD numbers
        // match the nodes on the map: nutrients green, minerals blue, evo points violet.
        private static readonly string NutHex = "#" + ColorUtility.ToHtmlStringRGB(GameView.NutrientColor);
        private static readonly string MinHex = "#" + ColorUtility.ToHtmlStringRGB(GameView.MineralColor);
        private static readonly string EvoHex = "#" + ColorUtility.ToHtmlStringRGB(GameView.EvoColor);

        private static string Nut(object v) => $"<color={NutHex}>{v}</color>";
        private static string Min(object v) => $"<color={MinHex}>{v}</color>";
        private static string Evo(object v) => $"<color={EvoHex}>{v}</color>";
        private readonly StringBuilder _sb = new StringBuilder(256);

        // Per-player vitals recomputed on a cadence, not per IMGUI pass: territory cell
        // counts (a full grid scan) and force totals from the Force block.
        private float _statsNext;
        private int[] _terrCells;
        private int[] _forceTotals;
        private int[] _forcePerDef; // human only, summed across fronts
        private int _healthMax;     // human organism's current health ceiling

        // Hover tooltip for build buttons: set while the mouse is over a building button,
        // drawn as a popup above the panel. Strictly stat-derived gameplay facts.
        private string _tooltip;
        private static readonly StringBuilder _tip = new StringBuilder(256);

        public void Bind(MatchBootstrap match) => _match = match;

        /// <summary>True when the given mouse position (bottom-up screen coords) is over the
        /// command panel or the minimap — InputController uses this so LEFT clicks over the
        /// HUD never select things in the world underneath.</summary>
        public bool IsPointerOver(Vector2 mouseBottomUp)
        {
            var p = new Vector2(mouseBottomUp.x, Screen.height - mouseBottomUp.y);
            if (_panelRect.Contains(p)) return true;
            return _match != null && _match.Map != null && MinimapRect.Contains(p);
        }

        // Every interactive control registers its rect here each OnGUI pass, so right-clicks
        // can tell REAL widgets from the panel's dead background. IMGUI runs several passes
        // per frame; the list rebuilds identically each pass, so reads from Update are stable.
        private readonly List<Rect> _hotRects = new List<Rect>(32);

        private Rect Hot(Rect r)
        {
            _hotRects.Add(r);
            return r;
        }

        /// <summary>True when a RIGHT click at the given mouse position (bottom-up screen
        /// coords) must be swallowed: it's on an actual widget or the minimap. The panel's
        /// dead background lets right-clicks through to the battlefield.</summary>
        public bool BlocksRightClick(Vector2 mouseBottomUp)
        {
            var p = new Vector2(mouseBottomUp.x, Screen.height - mouseBottomUp.y);
            for (int i = 0; i < _hotRects.Count; i++)
                if (_hotRects[i].Contains(p)) return true;
            return _match != null && _match.Map != null && MinimapRect.Contains(p);
        }

        /// <summary>Screen rect of the minimap (GUI coords, top-down), sized to the map's
        /// aspect and parked in the top-right corner.</summary>
        public Rect MinimapRect
        {
            get
            {
                var map = _match.Map;
                float aspect = map.HeightCenti / (float)map.WidthCenti;
                float w = MiniWidth, h = MiniWidth * aspect;
                if (h > MiniWidth) { h = MiniWidth; w = MiniWidth / aspect; }
                return new Rect(Screen.width - w - 10, 10, w, h);
            }
        }

        public bool MinimapContains(Vector2 mouseBottomUp) =>
            _match != null && _match.Map != null
            && MinimapRect.Contains(new Vector2(mouseBottomUp.x, Screen.height - mouseBottomUp.y));

        /// <summary>Map a mouse position over the minimap to world coordinates.</summary>
        public Vector2 MinimapToWorld(Vector2 mouseBottomUp)
        {
            var r = MinimapRect;
            float gy = Screen.height - mouseBottomUp.y;
            float u = Mathf.Clamp01((mouseBottomUp.x - r.x) / r.width);
            float v = Mathf.Clamp01(1f - (gy - r.y) / r.height);
            return new Vector2(u * _match.Map.WidthCenti / 100f, v * _match.Map.HeightCenti / 100f);
        }

        private void OnGUI()
        {
            if (_match == null || _match.Sim == null) return;
            EnsureStyles();
            RefreshStats();
            _hotRects.Clear();
            _tooltip = null;
            DrawPushPath();
            DrawTopBar();
            DrawPanel();
            DrawMinimap();
            DrawTooltip();

            // Quit-to-menu, always available top-center.
            if (GUI.Button(Hot(new Rect(Screen.width * 0.5f - 32, 8, 64, 24)), "Menu", _small))
                _match.QuitToMenu();

            // Victory / defeat banner once the match is decided.
            if (_match.WinnerTeam >= 0)
            {
                var r = new Rect(Screen.width * 0.5f - 220, Screen.height * 0.32f, 440, 130);
                Tint(r, new Color(0.05f, 0.07f, 0.05f, 0.92f));
                var big = _header;
                string text = _match.HumanWon
                    ? "<size=30><color=#b8ff9e>VICTORY</color></size>"
                    : "<size=30><color=#ff8a80>DEFEAT</color></size>";
                var align = big.alignment;
                big.alignment = TextAnchor.MiddleCenter;
                GUI.Label(new Rect(r.x, r.y + 14, r.width, 46), text, big);
                big.alignment = align;
                if (GUI.Button(Hot(new Rect(r.x + r.width * 0.5f - 90, r.y + 74, 180, 36)), "Return to Menu", _button))
                    _match.QuitToMenu();
            }
        }

        private void EnsureStyles()
        {
            if (_label != null) return;
            _label = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 11, richText = true };
            _header = new GUIStyle(GUI.skin.label) { fontSize = 15, richText = true, fontStyle = FontStyle.Bold };
            _button = new GUIStyle(GUI.skin.button)
            {
                fontSize = 9, richText = true, alignment = TextAnchor.MiddleCenter, wordWrap = true,
                padding = new RectOffset(1, 1, 1, 1), clipping = TextClipping.Clip,
            };
            _white = new Texture2D(1, 1);
            _white.SetPixel(0, 0, Color.white);
            _white.Apply();
            var w = _match.Sim.World;
            _terrCells = new int[w.Players.Length];
            _forceTotals = new int[w.Players.Length];
            _forcePerDef = new int[_match.Defs.Units.Length];
        }

        private void RefreshStats()
        {
            if (Time.time < _statsNext) return;
            _statsNext = Time.time + 0.25f;
            var w = _match.Sim.World;
            int u = _match.Defs.Units.Length;

            System.Array.Clear(_terrCells, 0, _terrCells.Length);
            for (int c = 0; c < w.Territory.Length; c++)
            {
                byte o = w.Territory[c];
                if (o < _terrCells.Length) _terrCells[o]++;
            }
            for (int p = 0; p < w.Players.Length; p++)
            {
                int total = 0;
                var force = w.Players[p].Force;
                for (int k = 0; k < force.Length; k++) total += force[k];
                _forceTotals[p] = total;
            }
            var mine = w.Players[MatchBootstrap.HumanPlayer].Force;
            for (int d = 0; d < u; d++)
            {
                int total = 0;
                for (int f = 0; f < SimConstants.MaxFronts; f++) total += mine[f * u + d];
                _forcePerDef[d] = total;
            }
            _healthMax = HealthSystem.MaxOf(w, _match.Defs, MatchBootstrap.HumanPlayer);
        }

        private int TerritoryPercent(int p) =>
            _match.Sim.World.OwnableCellCount > 0
                ? _terrCells[p] * 100 / _match.Sim.World.OwnableCellCount : 0;

        /// <summary>Fog-aware minimap: terrain shading by visibility, nodes once explored,
        /// buildings as blips (enemies only when explored), plus the camera's viewport
        /// rectangle. Left-press pans — handled by InputController via MinimapContains.</summary>
        private void DrawMinimap()
        {
            if (_match.Map == null) return;
            var w = _match.Sim.World;
            int cw = Mathf.Max(1, _match.Map.WidthCenti / 100);
            int ch = Mathf.Max(1, _match.Map.HeightCenti / 100);
            if (_miniTex == null)
            {
                _miniTex = new Texture2D(cw, ch, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
                _miniPixels = new Color32[cw * ch];
            }

            if (Time.time >= _miniNext)
            {
                _miniNext = Time.time + 0.15f;
                var vision = _match.View != null ? _match.View.Vision : null;
                var unseen = new Color32(4, 8, 4, 255);
                var explored = new Color32(16, 26, 14, 255);
                var visible = new Color32(24, 38, 22, 255);
                var nutrientCol = (Color32)GameView.NutrientColor;
                var mineralCol = (Color32)GameView.MineralColor;
                for (int y = 0; y < ch; y++)
                {
                    int row = y * cw;
                    for (int x = 0; x < cw; x++)
                        _miniPixels[row + x] = vision == null ? visible
                            : vision.VisibleCell(x, y) ? visible
                            : vision.ExploredCell(x, y) ? explored : unseen;
                }

                void Plot(int px, int py, Color32 c, int size)
                {
                    for (int dy = 0; dy < size; dy++)
                        for (int dx = 0; dx < size; dx++)
                        {
                            int qx = Mathf.Clamp(px + dx, 0, cw - 1), qy = Mathf.Clamp(py + dy, 0, ch - 1);
                            _miniPixels[qy * cw + qx] = c;
                        }
                }

                // Territory wash: each owned 2u cell tints its four 1u minimap pixels, fog
                // permitting — the organisms' shapes ARE the game, so they lead here too.
                for (int c = 0; c < w.Territory.Length; c++)
                {
                    byte o = w.Territory[c];
                    if (o >= w.Players.Length) continue;
                    int cx = c % w.TerritoryCellsX * 2, cy = c / w.TerritoryCellsX * 2;
                    var oc = MiniOwnerColor(o);
                    var dim = new Color32((byte)(oc.r / 3), (byte)(oc.g / 3), (byte)(oc.b / 3), 255);
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int qx = cx + dx, qy = cy + dy;
                            if (qx >= cw || qy >= ch) continue;
                            if (vision != null && !vision.VisibleCell(qx, qy)
                                && !vision.ExploredCell(qx, qy)) continue;
                            _miniPixels[qy * cw + qx] = dim;
                        }
                }

                // Terrain walls: static geometry, always drawn (like the terrain shading).
                var wallCol = new Color32(74, 82, 72, 255);
                for (int k = 0; k < w.WallPos.Length; k++)
                {
                    int wx = (int)(w.WallPos[k].X.Raw / (float)Fix.OneRaw);
                    int wy = (int)(w.WallPos[k].Y.Raw / (float)Fix.OneRaw);
                    int wr = Mathf.Max(1, (int)(w.WallRadius[k].Raw / (float)Fix.OneRaw));
                    for (int dy = -wr; dy <= wr; dy++)
                        for (int dx = -wr; dx <= wr; dx++)
                        {
                            if (dx * dx + dy * dy > wr * wr) continue;
                            int qx = wx + dx, qy = wy + dy;
                            if (qx < 0 || qx >= cw || qy < 0 || qy >= ch) continue;
                            _miniPixels[qy * cw + qx] = wallCol;
                        }
                }

                for (int i = 0; i < w.HighWater; i++)
                {
                    if (w.Kind[i] == EntityKind.None) continue;
                    int px = Mathf.Clamp((int)(w.Pos[i].X.Raw / (float)Fix.OneRaw), 0, cw - 1);
                    int py = Mathf.Clamp((int)(w.Pos[i].Y.Raw / (float)Fix.OneRaw), 0, ch - 1);
                    bool mine = w.IsFriendly(MatchBootstrap.HumanPlayer, w.Owner[i]); // own + allies
                    if (w.Kind[i] == EntityKind.Node)
                    {
                        if (vision == null || vision.ExploredCell(px, py))
                            Plot(px, py, w.NodeMineral[i] ? mineralCol : nutrientCol, 2);
                    }
                    else if (w.Kind[i] == EntityKind.Building)
                    {
                        if (!mine && vision != null && !vision.ExploredCell(px, py)) continue;
                        Plot(px - 1, py - 1, MiniOwnerColor(w.Owner[i]), 3);
                    }
                }
                _miniTex.SetPixels32(_miniPixels);
                _miniTex.Apply(false);
            }

            var r = MinimapRect;
            Tint(new Rect(r.x - 2, r.y - 2, r.width + 4, r.height + 4), new Color(0.28f, 0.33f, 0.28f, 0.95f));
            GUI.DrawTexture(r, _miniTex);

            // Camera viewport rectangle (world y up ↔ GUI y down).
            var cam = Camera.main;
            if (cam != null)
            {
                float mw = _match.Map.WidthCenti / 100f, mh = _match.Map.HeightCenti / 100f;
                float halfH = cam.orthographicSize, halfW = halfH * cam.aspect;
                float x0 = Mathf.Clamp01((cam.transform.position.x - halfW) / mw);
                float x1 = Mathf.Clamp01((cam.transform.position.x + halfW) / mw);
                float y0 = Mathf.Clamp01((cam.transform.position.y - halfH) / mh);
                float y1 = Mathf.Clamp01((cam.transform.position.y + halfH) / mh);
                float gx0 = r.x + x0 * r.width, gx1 = r.x + x1 * r.width;
                float gy0 = r.y + (1f - y1) * r.height, gy1 = r.y + (1f - y0) * r.height;
                var vc = new Color(1f, 1f, 1f, 0.65f);
                Tint(new Rect(gx0, gy0, gx1 - gx0, 1f), vc);
                Tint(new Rect(gx0, gy1 - 1f, gx1 - gx0, 1f), vc);
                Tint(new Rect(gx0, gy0, 1f, gy1 - gy0), vc);
                Tint(new Rect(gx1 - 1f, gy0, 1f, gy1 - gy0), vc);
            }
        }

        private void DrawTopBar()
        {
            var w = _match.Sim.World;
            _sb.Length = 0;
            int sec = w.TickCount / SimConstants.TicksPerSecond;
            _sb.Append($"<b>PETRI</b>   t={sec / 60:00}:{sec % 60:00}\n");

            for (int p = 0; p < w.Players.Length; p++)
            {
                var pl = w.Players[p];
                string who = p == MatchBootstrap.HumanPlayer ? "You"
                    : w.IsFriendly(MatchBootstrap.HumanPlayer, (byte)p) ? "<color=#9fe0a0>Ally</color>" : "Foe";
                string alive = pl.Alive ? "" : "  <color=#ff6666>[eliminated]</color>";
                _sb.Append($"P{p} {who} T{pl.Team + 1}  {Nut("nutrients=" + pl.Food)}  {Min("minerals=" + pl.Minerals)}  {Evo("evo=" + pl.EvoPoints)}  " +
                           $"workers={pl.WorkerCount}  force={_forceTotals[p]}  hp={pl.OrganismHealth}  terr={TerritoryPercent(p)}%{alive}\n");
            }
            GUI.Label(new Rect(12, 8, 1100, 30 + w.Players.Length * 17), _sb.ToString(), _label);

            string hint;
            if (_match.Input != null && _match.Input.PlacingBuilding >= 0)
                hint = $"<b>Placing {PrettyName(_match.Defs.Buildings[_match.Input.PlacingBuilding].Id)}</b> — left-click to place (own territory only) · right-click / Esc to cancel";
            else if (_match.Input != null && _match.Input.SelectedFront >= 0)
                hint = $"<b>Front {_match.Input.SelectedFront + 1} selected</b> — right-click-drag a path to PUSH it · [S] stop · Esc deselect";
            else
                hint = "L-click your border to select a FRONT · L-click buildings/nodes to inspect · R-click with a producer selected to rally it · win at 75% of the dish";
            GUI.Label(new Rect(12, Screen.height - 24, 1800, 22), hint, _small);
        }

        // Dotted gold trail under the cursor while sketching a push path; the bright head
        // marks where the front is being sent.
        private void DrawPushPath()
        {
            var input = _match.Input;
            if (input == null || !input.RightDragging || input.RightPathScreen.Count == 0) return;
            const float step = 10f;
            float carry = 0f;
            var path = input.RightPathScreen;
            DrawDot(path[0], 5f);
            for (int i = 1; i < path.Count; i++)
            {
                Vector2 a = path[i - 1], b = path[i];
                float seg = Vector2.Distance(a, b);
                if (seg < 1e-3f) continue;
                for (float d = step - carry; d < seg; d += step)
                    DrawDot(Vector2.Lerp(a, b, d / seg), 5f);
                carry = (carry + seg) % step;
            }
            DrawDot(path[path.Count - 1], 8f);
        }

        private void DrawDot(Vector2 p, float sz) =>
            Tint(new Rect(p.x - sz * 0.5f, Screen.height - p.y - sz * 0.5f, sz, sz),
                new Color(1f, 0.95f, 0.4f, sz > 6f ? 0.95f : 0.7f));

        /// <summary>The persistent bottom panel: organism/selection card on the left, force
        /// table in the middle, build grid (always) plus the selected producer's production
        /// controls on the right.</summary>
        private void DrawPanel()
        {
            var w = _match.Sim.World;
            var defs = _match.Defs;
            var input = _match.Input;
            _panelRect = new Rect(8, Screen.height - PanelHeight - 30, Screen.width - 16, PanelHeight);
            Tint(_panelRect, new Color(0.05f, 0.07f, 0.05f, 0.85f));

            var cardRect = new Rect(_panelRect.x + 12, _panelRect.y + 8, 330, PanelHeight - 16);
            var tableRect = new Rect(_panelRect.x + 360, _panelRect.y + 8, 280, PanelHeight - 16);

            int primary = input != null ? input.PrimarySelected() : -1;
            if (primary >= 0 && w.Kind[primary] == EntityKind.Node) DrawNodeCard(w, primary, cardRect);
            else if (primary >= 0 && w.Kind[primary] == EntityKind.Building) DrawBuildingCard(w, defs, primary, cardRect);
            else DrawOrganismCard(w, cardRect);

            DrawForceTable(defs, tableRect);
            DrawFrontArrows(w);
            DrawBuildGrid(w, defs, input, primary);
        }

        /// <summary>The front-split control: ▲ splits the border into more fronts, ▼ merges
        /// back down, stepping K through 4·6·8·12·20·40. Force redistributes evenly.</summary>
        private void DrawFrontArrows(SimWorld w)
        {
            var pl = w.Players[MatchBootstrap.HumanPlayer];
            float ax = _panelRect.x + 660, ay = _panelRect.y + 8;
            GUI.Label(new Rect(ax, ay, 90, 18), "<b>Fronts</b>", _small);

            int ix = System.Array.IndexOf(SimConstants.FrontCounts, (int)pl.FrontCount);
            var oldBg = GUI.backgroundColor;
            GUI.enabled = ix < SimConstants.FrontCounts.Length - 1;
            if (GUI.Button(Hot(new Rect(ax, ay + 20, 44, 30)), "▲", _button) && GUI.enabled)
                _match.Enqueue(new Command { Type = CommandType.SetFrontCount, A = SimConstants.FrontCounts[ix + 1] });
            GUI.enabled = true;

            var align = _header.alignment;
            _header.alignment = TextAnchor.MiddleCenter;
            GUI.Label(new Rect(ax, ay + 52, 44, 24), $"<b>{pl.FrontCount}</b>", _header);
            _header.alignment = align;

            GUI.enabled = ix > 0;
            if (GUI.Button(Hot(new Rect(ax, ay + 78, 44, 30)), "▼", _button) && GUI.enabled)
                _match.Enqueue(new Command { Type = CommandType.SetFrontCount, A = SimConstants.FrontCounts[ix - 1] });
            GUI.enabled = true;
            GUI.backgroundColor = oldBg;
        }

        /// <summary>Default left card: the superorganism's vitals.</summary>
        private void DrawOrganismCard(SimWorld w, Rect r)
        {
            var pl = w.Players[MatchBootstrap.HumanPlayer];
            float y = r.y;
            GUI.Label(new Rect(r.x, y, r.width, 22), "Superorganism", _header);
            y += 24;

            // The organism's lifebar: its ceiling grows with territory and buildings.
            float frac = _healthMax > 0 ? Mathf.Clamp01(pl.OrganismHealth / (float)_healthMax) : 0f;
            Tint(new Rect(r.x, y, 300, 12), new Color(0.25f, 0.05f, 0.05f, 0.9f));
            Tint(new Rect(r.x, y, 300 * frac, 12),
                Color.Lerp(new Color(0.95f, 0.25f, 0.15f), new Color(0.2f, 0.85f, 0.35f), frac));
            GUI.Label(new Rect(r.x + 306, y - 4, 140, 20), $"{pl.OrganismHealth} / {_healthMax}", _small);
            y += 18;

            _sb.Length = 0;
            _sb.Append($"Territory <b>{TerritoryPercent(MatchBootstrap.HumanPlayer)}%</b> of the dish (win at 75%)\n");
            _sb.Append($"Workers <b>{pl.WorkerCount}</b> — each speeds growth and harvest\n");
            _sb.Append($"Fronts <b>{pl.FrontCount}</b> border sections\n");

            int sel = _match.Input != null ? _match.Input.SelectedFront : -1;
            if (sel >= 0)
            {
                int u = _match.Defs.Units.Length, force = 0;
                for (int d = 0; d < u; d++) force += pl.Force[sel * u + d];
                string state = pl.FrontBrokenTicks[sel] > 0 ? "<color=#ff8a80><b>BROKEN</b></color>"
                    : pl.FrontPushX[sel] >= 0 ? "<color=#ffd94a><b>PUSHING</b></color>" : "holding";
                _sb.Append($"<b>Front {sel + 1}</b> — force <b>{force}</b> · {state}\n");
            }
            else
            {
                _sb.Append("<color=#9fb0a4><i>Click your border to select a front; right-click-drag to push it.</i></color>");
            }
            GUI.Label(new Rect(r.x, y, r.width, r.height - (y - r.y)), _sb.ToString(), _label);
        }

        /// <summary>Middle column: what the organism's force is made of, by unit type, with
        /// a second column for the selected front's share.</summary>
        private void DrawForceTable(DefDatabase defs, Rect r)
        {
            var pl = _match.Sim.World.Players[MatchBootstrap.HumanPlayer];
            int u = defs.Units.Length;
            int sel = _match.Input != null ? _match.Input.SelectedFront : -1;
            _sb.Length = 0;
            _sb.Append($"<b>Force</b>  ({_forceTotals[MatchBootstrap.HumanPlayer]})");
            if (sel >= 0) _sb.Append($"   <color=#ffd94a>front {sel + 1}</color>");
            _sb.Append('\n');
            _sb.Append($"{pl.WorkerCount} × Workers\n");
            for (int d = 0; d < _forcePerDef.Length; d++)
            {
                if (_forcePerDef[d] == 0) continue;
                _sb.Append($"{_forcePerDef[d]} × {PrettyName(defs.Units[d].Id)}");
                if (sel >= 0) _sb.Append($"   <color=#ffd94a>{pl.Force[sel * u + d]}</color>");
                _sb.Append('\n');
            }
            GUI.Label(r, _sb.ToString(), _label);
        }

        private void DrawNodeCard(SimWorld w, int e, Rect r)
        {
            bool mineral = w.NodeMineral[e];
            float y = r.y;
            GUI.Label(new Rect(r.x, y, r.width, 22),
                mineral ? Min("Mineral Pool") : Nut("Nutrient Pool"), _header);
            y += 26;

            _sb.Length = 0;
            string label = mineral ? Min("Minerals remaining: <b>" + w.NodeFood[e] + "</b>")
                                   : Nut("Nutrients remaining: <b>" + w.NodeFood[e] + "</b>");
            _sb.Append(label).Append('\n');
            int cell = w.CellOfPos(w.Pos[e]);
            bool inside = w.Territory[cell] == MatchBootstrap.HumanPlayer;
            _sb.Append(inside
                ? "<color=#b8ff9e>Inside your organism — harvested passively.</color>\n"
                : "Grow your organism over this node to harvest it.\n");
            _sb.Append("<color=#9fb0a4><i>Harvest rate scales with your worker count.</i></color>");
            GUI.Label(new Rect(r.x, y, r.width + 260, r.height - (y - r.y)), _sb.ToString(), _label);
        }

        private void DrawBuildingCard(SimWorld w, DefDatabase defs, int e, Rect r)
        {
            var def = defs.Buildings[w.DefIndex[e]];
            float y = r.y;

            GUI.Label(new Rect(r.x, y, r.width, 22), PrettyName(def.Id), _header);
            y += 24;

            float frac = Mathf.Clamp01(w.Hp[e] / (float)def.MaxHp);
            Tint(new Rect(r.x, y, 300, 12), new Color(0.25f, 0.05f, 0.05f, 0.9f));
            Tint(new Rect(r.x, y, 300 * frac, 12), new Color(0.15f, 0.75f, 0.25f, 0.95f));
            GUI.Label(new Rect(r.x + 306, y - 4, 120, 20), $"{w.Hp[e]} / {def.MaxHp}", _small);
            y += 18;

            _sb.Length = 0;
            if (!string.IsNullOrEmpty(def.Description))
                _sb.Append($"<color=#9fb0a4><i>{def.Description}</i></color>\n");
            if (def.IsHeadquarters)
                _sb.Append("<b>Nucleus</b> — lose it and you are eliminated\n");

            if (w.ConstructionRemaining[e] > 0)
            {
                int total = def.BuildTimeTicks * 3; // sites track work units = 3 × build ticks
                int done = total > 0 ? 100 * (total - w.ConstructionRemaining[e]) / total : 0;
                _sb.Append($"<color=#ffd966><b>Growing</b>  {done}%</color>\n");
                GUI.Label(new Rect(r.x, y, r.width, r.height - (y - r.y)), _sb.ToString(), _label);
                return;
            }

            if (def.ProducesDense.Length > 0)
            {
                if (w.ProducePaused[e])
                    _sb.Append("Production <color=#ffcf66><b>PAUSED</b></color>\n");
                else if (w.ProduceChoice[e] >= 0)
                {
                    var udef = defs.Units[w.ProduceChoice[e]];
                    int pct = udef.BuildTimeTicks > 0 ? 100 * w.ProduceProgress[e] / udef.BuildTimeTicks : 0;
                    _sb.Append($"Producing <b>{PrettyName(udef.Id)}</b>  {pct}%\n");
                }
                else _sb.Append($"Production <b>idle</b> (waiting for {Nut("nutrients")})\n");

                _sb.Append(w.ProduceOverride[e] >= 0
                    ? $"Mode: <b>Only {PrettyName(defs.Units[w.ProduceOverride[e]].Id)}</b>\n"
                    : "Mode: <b>Auto</b> (composition weights)\n");
                _sb.Append(w.RallyFront[e] >= 0
                    ? $"Rally: <b>front {w.RallyFront[e] + 1}</b> (right-click to move, [R] to clear)\n"
                    : "Rally: <b>auto</b> — right-click a sector to pin its output there\n");
            }
            else _sb.Append("Produces nothing\n");

            GUI.Label(new Rect(r.x, y, r.width, r.height - (y - r.y)), _sb.ToString(), _label);
        }

        /// <summary>Right side of the panel: the ALWAYS-available build grid, plus production
        /// controls for the selected producer building to its left.</summary>
        private void DrawBuildGrid(SimWorld w, DefDatabase defs, InputController input, int primary)
        {
            float gridW = GridCols * ButtonSize + (GridCols - 1) * ButtonGap;
            float gx = _panelRect.xMax - gridW - 12;
            float gy = _panelRect.y + 8;
            var oldBg = GUI.backgroundColor;
            var hi = new Color(1f, 0.9f, 0.35f);

            GUI.Label(new Rect(gx, gy - 2, gridW, 18), "<b>Build</b>", _small);
            gy += 18;
            int slot = 0;
            for (int b = 0; b < defs.Buildings.Length; b++)
            {
                if (!defs.Buildings[b].Constructible) continue;
                int col = slot % GridCols, row = slot / GridCols;
                var rect = new Rect(gx + col * (ButtonSize + ButtonGap), gy + row * (ButtonSize + ButtonGap), ButtonSize, ButtonSize);
                GUI.backgroundColor = input != null && input.PlacingBuilding == b ? hi : oldBg;
                if (GUI.Button(Hot(rect), $"{ShortName(defs.Buildings[b].Id)}\n{CostLabel(defs.Buildings[b])}", _button) && input != null)
                    input.BeginPlacement(b);
                TipIfHovered(rect, defs.Buildings[b]);
                slot++;
            }
            GUI.backgroundColor = oldBg;

            // Production controls for the selected producer, in their own column to the left.
            if (primary < 0 || w.Kind[primary] != EntityKind.Building || input == null) return;
            var bdef = defs.Buildings[w.DefIndex[primary]];
            if (bdef.ProducesDense.Length == 0 || w.ConstructionRemaining[primary] > 0) return;

            float px = gx - gridW - 28;
            float py = _panelRect.y + 8;
            GUI.Label(new Rect(px, py - 2, gridW, 18), "<b>Produce</b>", _small);
            py += 18;
            int over = w.ProduceOverride[primary];
            slot = 0;

            Rect Next()
            {
                int col = slot % GridCols, row = slot / GridCols;
                slot++;
                return new Rect(px + col * (ButtonSize + ButtonGap), py + row * (ButtonSize + ButtonGap), ButtonSize, ButtonSize);
            }

            GUI.backgroundColor = over < 0 ? hi : oldBg;
            if (GUI.Button(Hot(Next()), "Auto", _button)) input.ApplyProduceOverride(-1);
            for (int k = 0; k < bdef.ProducesDense.Length; k++)
            {
                int unitIx = bdef.ProducesDense[k];
                var udef = defs.Units[unitIx];
                GUI.backgroundColor = over == unitIx ? hi : oldBg;
                if (GUI.Button(Hot(Next()), $"{ShortName(udef.Id)}\n{Nut(udef.FoodCost + "n")}", _button))
                    input.ApplyProduceOverride(unitIx);
            }
            GUI.backgroundColor = w.ProducePaused[primary] ? hi : oldBg;
            if (GUI.Button(Hot(Next()), w.ProducePaused[primary] ? "Resume" : "Pause", _button))
                input.ToggleProducePaused(primary);
            GUI.backgroundColor = oldBg;
        }

        /// <summary>Remember a gameplay tooltip while the mouse hovers the given rect.</summary>
        private void TipIfHovered(Rect r, BuildingDef bd)
        {
            if (r.Contains(Event.current.mousePosition)) _tooltip = BuildingTooltip(bd);
        }

        /// <summary>Strictly stat-derived gameplay summary of a building — every line comes
        /// from def numbers or a hard rule, no flavour.</summary>
        private string BuildingTooltip(BuildingDef bd)
        {
            _tip.Length = 0;
            _tip.Append($"<b>{PrettyName(bd.Id)}</b>   {CostLabel(bd)} · {bd.MaxHp} HP · ~{bd.BuildTimeTicks / SimConstants.TicksPerSecond}s\n");
            if (bd.IsHeadquarters)
                _tip.Append("Your nucleus — lose it and you are eliminated.\n");
            if (bd.ProducesDense.Length > 0)
            {
                _tip.Append("Produces: ");
                for (int k = 0; k < bd.ProducesDense.Length; k++)
                {
                    var ud = _match.Defs.Units[bd.ProducesDense[k]];
                    if (k > 0) _tip.Append(", ");
                    _tip.Append(PrettyName(ud.Id));
                    if (ud.FoodCost == 0) _tip.Append(" (free)");
                }
                _tip.Append(" — finished units join your force on a front.\n");
            }
            if (bd.AttackDamage > 0)
                _tip.Append($"Adds {bd.AttackDamage} defensive fire to its front sector.\n");
            if (bd.AttackBonus > 0)
                _tip.Append($"+{bd.AttackBonus} attack to your whole force while it stands (stacks per copy).\n");
            _tip.Append("Own territory only; grows itself; dies if its ground is lost (soaking the flip first).");
            return _tip.ToString();
        }

        /// <summary>The popup itself: a small box above the panel, right side (over the build
        /// grid the mouse is on).</summary>
        private void DrawTooltip()
        {
            if (_tooltip == null) return;
            const float width = 420f;
            float height = _label.CalcHeight(new GUIContent(_tooltip), width - 16f) + 12f;
            var r = new Rect(_panelRect.xMax - width - 8, _panelRect.y - height - 6, width, height);
            Tint(r, new Color(0.05f, 0.07f, 0.05f, 0.94f));
            Tint(new Rect(r.x, r.y, r.width, 1f), new Color(0.34f, 0.4f, 0.34f, 1f)); // hairline top
            GUI.Label(new Rect(r.x + 8, r.y + 6, r.width - 16, r.height - 12), _tooltip, _label);
        }

        /// <summary>Price tag for a build button: only the resources this def actually asks for
        /// (n = nutrients, m = minerals, e = evolutionary points).</summary>
        private static string CostLabel(BuildingDef def)
        {
            _cost.Length = 0;
            if (def.FoodCost > 0) _cost.Append(Nut(def.FoodCost + "n"));
            if (def.MineralCost > 0) { if (_cost.Length > 0) _cost.Append(' '); _cost.Append(Min(def.MineralCost + "m")); }
            if (def.EvoCost > 0) { if (_cost.Length > 0) _cost.Append(' '); _cost.Append(Evo(def.EvoCost + "e")); }
            if (_cost.Length == 0) _cost.Append("free");
            return _cost.ToString();
        }
        private static readonly StringBuilder _cost = new StringBuilder(24);

        private void Tint(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = old;
        }

        /// <summary>Grid-button-sized name: the last word of the pretty name
        /// ("strain.spike-battery" → "Battery", "strain.incubator" → "Incubator").</summary>
        public static string ShortName(string id)
        {
            string pretty = PrettyName(id);
            int space = pretty.LastIndexOf(' ');
            return space >= 0 ? pretty.Substring(space + 1) : pretty;
        }

        /// <summary>"strain.spike-battery" → "Spike Battery".</summary>
        public static string PrettyName(string id)
        {
            int dot = id.LastIndexOf('.');
            string s = dot >= 0 ? id.Substring(dot + 1) : id;
            var parts = s.Split('-');
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Length > 0)
                    parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
            return string.Join(" ", parts);
        }
    }
}
