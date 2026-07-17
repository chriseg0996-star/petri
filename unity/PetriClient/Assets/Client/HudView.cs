using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Petri.Core;

namespace Petri.Client
{
    /// <summary>
    /// AoE2-style HUD: a top resource/army strip, the live drag-select rectangle, and — when
    /// units are selected — a bottom command panel with a unit stat card (HP bar, combat stats,
    /// role scores, squad status), a selection summary, and a hotkeyed command grid (one button
    /// per formation def, plus Stop). The grid is data-driven: adding a formation JSON adds a
    /// button. Pure IMGUI so the project needs no UI assets; buttons act through
    /// InputController, which turns them into sim Commands.
    /// </summary>
    public sealed class HudView : MonoBehaviour
    {
        // 5x4 command grid: compact footprint, room for the formation roster + action row.
        private const float PanelHeight = 196f;
        private const float ButtonSize = 36f;
        private const float ButtonGap = 4f;
        private const int GridCols = 5;
        private const int GridRows = 4;

        private MatchBootstrap _match;
        private GUIStyle _label, _small, _header, _button;
        private Texture2D _white;
        private Rect _panelRect;
        private bool _panelVisible;

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
        private int[] _selCounts;
        private int[] _selBuildingCounts;
        private readonly StringBuilder _sb = new StringBuilder(256);

        public void Bind(MatchBootstrap match) => _match = match;

        /// <summary>True when the given mouse position (bottom-up screen coords) is over the
        /// command panel or the minimap — InputController uses this so HUD clicks never
        /// select or order units in the world underneath.</summary>
        public bool IsPointerOver(Vector2 mouseBottomUp)
        {
            var p = new Vector2(mouseBottomUp.x, Screen.height - mouseBottomUp.y);
            if (_panelVisible && _panelRect.Contains(p)) return true;
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
            DrawDragBox();
            DrawTopBar();
            DrawCommandPanel();
            DrawMinimap();

            // Quit-to-menu, always available top-right.
            if (GUI.Button(new Rect(Screen.width * 0.5f - 32, 8, 64, 24), "Menu", _small))
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
                if (GUI.Button(new Rect(r.x + r.width * 0.5f - 90, r.y + 74, 180, 36), "Return to Menu", _button))
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
            _selCounts = new int[_match.Defs.Units.Length];
            _selBuildingCounts = new int[_match.Defs.Buildings.Length];
        }

        /// <summary>Fog-aware minimap: terrain shading by visibility, nodes once explored,
        /// buildings as 2x2 blips (enemies only when explored), units as dots (enemies only
        /// while visible), plus the camera's viewport rectangle. Left-drag pans, right-click
        /// orders — handled by InputController via MinimapContains/MinimapToWorld.</summary>
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
                    else
                    {
                        if (!mine && vision != null && !vision.VisibleCell(px, py)) continue;
                        Plot(px, py, MiniOwnerColor(w.Owner[i]), 1);
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

        private void DrawDragBox()
        {
            var input = _match.Input;
            if (input == null) return;
            if (input.IsDragging)
            {
                var a = input.DragStartScreen;
                var b = input.DragNowScreen;
                float y0 = Screen.height - Mathf.Max(a.y, b.y), y1 = Screen.height - Mathf.Min(a.y, b.y);
                Tint(new Rect(Mathf.Min(a.x, b.x), y0, Mathf.Abs(a.x - b.x), y1 - y0), new Color(1f, 1f, 1f, 0.15f));
            }
            if (input.RightDragging)
                DrawFormationPath(input.RightPathScreen);
            if (input.FacingDragging)
                DrawFacingArrow(input.FacingDragStartScreen, input.FacingDragNowScreen);
        }

        // Straight dotted arrow (cyan) from the anchor toward the cursor: the direction the
        // selection will face on release. The bright head marks the pointing end.
        private void DrawFacingArrow(Vector2 a, Vector2 b)
        {
            var c = new Color(0.45f, 0.9f, 1f, 0.8f);
            int dots = 12;
            for (int i = 0; i <= dots; i++)
            {
                Vector2 p = Vector2.Lerp(a, b, i / (float)dots);
                float sz = i == dots ? 10f : 5f;
                Tint(new Rect(p.x - sz * 0.5f, Screen.height - p.y - sz * 0.5f, sz, sz),
                    i == dots ? new Color(0.6f, 1f, 1f, 0.95f) : c);
            }
        }

        /// <summary>Compact strip for large matches: your own resources, then one row per team
        /// (living seats and total units) so a 32-way game still reads at a glance. Appends to
        /// _sb and returns the number of lines added.</summary>
        private int AppendTeamRollup(SimWorld w)
        {
            var me = w.Players[MatchBootstrap.HumanPlayer];
            int myUnits = 0;
            for (int i = 0; i < w.HighWater; i++)
                if (w.Kind[i] == EntityKind.Unit && w.Owner[i] == MatchBootstrap.HumanPlayer) myUnits++;
            _sb.Append($"You  P{MatchBootstrap.HumanPlayer} T{me.Team + 1}  {Nut("nutrients=" + me.Food)}  {Min("minerals=" + me.Minerals)}  {Evo("evo=" + me.EvoPoints)}  units={myUnits}\n");

            // Index-order team roll-up (teams are small ints — no dictionaries in view code either).
            int lines = 1;
            for (int t = 0; t < MatchBootstrap.MaxPlayers; t++)
            {
                int seats = 0, aliveSeats = 0, units = 0;
                for (int p = 0; p < w.Players.Length; p++)
                {
                    if (w.Players[p].Team != t) continue;
                    seats++;
                    if (w.Players[p].Alive) aliveSeats++;
                }
                if (seats == 0) continue;
                for (int i = 0; i < w.HighWater; i++)
                    if (w.Kind[i] == EntityKind.Unit && w.Owner[i] < w.Players.Length
                        && w.Players[w.Owner[i]].Team == t) units++;
                string tag = t == me.Team ? "<color=#9fe0a0>(yours)</color>" : "";
                string dead = aliveSeats == 0 ? "  <color=#ff6666>[wiped]</color>" : "";
                _sb.Append($"T{t + 1} {tag}  seats={aliveSeats}/{seats}  units={units}{dead}\n");
                lines++;
            }
            return lines;
        }

        private void DrawTopBar()
        {
            var w = _match.Sim.World;
            _sb.Length = 0;
            int sec = w.TickCount / SimConstants.TicksPerSecond;
            _sb.Append($"<b>PETRI</b>   t={sec / 60:00}:{sec % 60:00}\n");

            // Big matches: a per-player line for all 32 seats would swallow the screen, so show
            // your own line and roll everyone else up per team.
            int barLines;
            if (w.Players.Length > 6)
            {
                barLines = 1 + AppendTeamRollup(w);
            }
            else
            {
                for (int p = 0; p < w.Players.Length; p++)
                {
                    int units = 0, leaders = 0, hqHp = 0;
                    for (int i = 0; i < w.HighWater; i++)
                    {
                        if (w.Kind[i] == EntityKind.Unit && w.Owner[i] == p)
                        {
                            units++;
                            if (_match.Defs.Units[w.DefIndex[i]].IsLeader) leaders++;
                        }
                        if (w.Kind[i] == EntityKind.Building && w.Owner[i] == p && _match.Defs.Buildings[w.DefIndex[i]].IsHeadquarters) hqHp = w.Hp[i];
                    }
                    string who = p == MatchBootstrap.HumanPlayer ? "You"
                        : w.IsFriendly(MatchBootstrap.HumanPlayer, (byte)p) ? "<color=#9fe0a0>Ally</color>" : "Foe";
                    string alive = w.Players[p].Alive ? "" : "  <color=#ff6666>[defeated]</color>";
                    _sb.Append($"P{p} {who} T{w.Players[p].Team + 1}  {Nut("nutrients=" + w.Players[p].Food)}  {Min("minerals=" + w.Players[p].Minerals)}  {Evo("evo=" + w.Players[p].EvoPoints)}  units={units}  leaders={leaders}  coreHP={hqHp}{alive}\n");
                }
                barLines = w.Players.Length;
            }
            GUI.Label(new Rect(12, 8, 900, 30 + barLines * 17), _sb.ToString(), _label);

            // Group tray: slot 1 is the army key (always live — the hierarchy drill);
            // 2-9 light up when a manual group is stored. Ctrl+N assigns, N recalls.
            if (_match.Input != null)
            {
                _sb.Length = 0;
                _sb.Append("Groups ");
                for (int n = 1; n <= 9; n++)
                    _sb.Append(_match.Input.GroupPopulated(n) ? $"<b><color=#ffd94a>{n}</color></b> " : $"<color=#666666>{n}</color> ");
                GUI.Label(new Rect(12, 8 + 18 * (barLines + 1), 900, 20), _sb.ToString(), _small);
            }

            string hint;
            if (_match.Input != null && _match.Input.PlacingBuilding >= 0)
                hint = $"<b>Placing {PrettyName(_match.Defs.Buildings[_match.Input.PlacingBuilding].Id)}</b> — left-click to place · right-click / Esc to cancel";
            else if (_match.Input != null && _match.Input.AttackArmed)
                hint = "<color=#ff8a80><b>ATTACK-MOVE</b> — left-click a target point · right-click / Esc to cancel</color>";
            else
                hint = "L-click select · R-click move / attack · R-drag line move · Shift+R-drag face · [A] attack-move · Ctrl+[1-9]/[1-9] groups · [Space] all military · [B] build · [S] stop";
            GUI.Label(new Rect(12, Screen.height - 24, 1800, 22), hint, _small);
        }

        private void DrawCommandPanel()
        {
            var input = _match.Input;
            _panelVisible = input != null && input.Selected.Count > 0;
            if (!_panelVisible) { _panelRect = default; return; }

            var w = _match.Sim.World;
            var defs = _match.Defs;
            _panelRect = new Rect(8, Screen.height - PanelHeight - 30, Screen.width - 16, PanelHeight);
            Tint(_panelRect, new Color(0.05f, 0.07f, 0.05f, 0.85f));

            // ---- Gather selection: per-type counts and the primary entity (lowest index = stable).
            System.Array.Clear(_selCounts, 0, _selCounts.Length);
            System.Array.Clear(_selBuildingCounts, 0, _selBuildingCounts.Length);
            foreach (int e in input.Selected)
            {
                if (w.Kind[e] == EntityKind.Unit) _selCounts[w.DefIndex[e]]++;
                else if (w.Kind[e] == EntityKind.Building) _selBuildingCounts[w.DefIndex[e]]++;
            }
            int primary = input.PrimarySelected();
            if (primary < 0) return;

            var cardRect = new Rect(_panelRect.x + 12, _panelRect.y + 8, 330, PanelHeight - 16);
            var summaryRect = new Rect(_panelRect.x + 360, _panelRect.y + 8, 260, PanelHeight - 16);
            if (w.Kind[primary] == EntityKind.Node)
            {
                DrawNodeCard(w, primary, cardRect);
            }
            else if (w.Kind[primary] == EntityKind.Building)
            {
                DrawBuildingCard(w, defs, primary, cardRect);
                DrawSelectionSummary(defs, input.Selected.Count, summaryRect);
                DrawProductionGrid(w, defs, input, primary);
            }
            else
            {
                DrawUnitCard(w, defs, primary, cardRect);
                DrawSelectionSummary(defs, input.Selected.Count, summaryRect);
                DrawCommandGrid(w, defs, input, primary);
            }
        }

        private void DrawUnitCard(SimWorld w, DefDatabase defs, int e, Rect r)
        {
            var def = defs.Units[w.DefIndex[e]];
            float y = r.y;

            GUI.Label(new Rect(r.x, y, r.width, 22), PrettyName(def.Id), _header);
            y += 24;

            // HP bar
            float frac = Mathf.Clamp01(w.Hp[e] / (float)def.MaxHp);
            Tint(new Rect(r.x, y, 300, 12), new Color(0.25f, 0.05f, 0.05f, 0.9f));
            Tint(new Rect(r.x, y, 300 * frac, 12), new Color(0.15f, 0.75f, 0.25f, 0.95f));
            GUI.Label(new Rect(r.x + 306, y - 4, 120, 20), $"{w.Hp[e]} / {def.MaxHp}", _small);
            y += 18;

            _sb.Length = 0;
            if (!string.IsNullOrEmpty(def.Description))
                _sb.Append($"<color=#9fb0a4><i>{def.Description}</i></color>\n");
            if (def.AttackDamage > 0)
            {
                float rate = def.AttackCooldownTicks > 0 ? SimConstants.TicksPerSecond / (float)def.AttackCooldownTicks : 0f;
                int bonus = w.Owner[e] < w.ScratchAttackBonus.Length ? w.ScratchAttackBonus[w.Owner[e]] : 0;
                string atk = bonus > 0
                    ? $"{def.AttackDamage + bonus} <color=#9fe0a0>(+{bonus})</color>"
                    : $"{def.AttackDamage}";
                _sb.Append($"Attack <b>{atk}</b>   Range <b>{def.AttackRangeCenti / 100f:0.0}</b>   Rate <b>{rate:0.0}/s</b>\n");
            }
            else _sb.Append("No attack\n");
            _sb.Append($"Speed <b>{def.MoveSpeedCenti / 100f:0.0}</b>   Size <b>{def.CollisionRadiusCenti / 100f:0.00}</b>   Push <b>{def.PushStrength}/{def.PushResistance}</b>\n");

            if (def.IsWorker)
            {
                // The load takes the colour of whatever the worker is actually hauling.
                string what = w.CarryMineral[e] ? Min("minerals") : Nut("nutrients");
                string amount = w.CarryMineral[e] ? Min("<b>" + w.Carry[e] + "</b>") : Nut("<b>" + w.Carry[e] + "</b>");
                _sb.Append($"Carrying {amount} / {def.CarryCapacity} {what}\n");
            }

            int auraPct = (w.Rules.LeaderAuraBonusNum * 100 / w.Rules.LeaderAuraBonusDen) - 100;
            if (def.IsLeader)
                _sb.Append($"<color=#9fd0ff><b>Command aura</b> — friendly units within {w.Rules.LeaderAuraRadiusCenti / 100f:0.0} hit +{auraPct}% harder</color>\n");
            else if (w.ScratchLeaderAura[e])
                _sb.Append($"<color=#9fe0a0>In a leader's aura  +{auraPct}% dmg</color>\n");

            if (w.SupplyTicks[e] == 0)
                _sb.Append("<color=#ff7766><b>OUT OF SUPPLY</b> — half damage; return to a supply line</color>\n");
            else if (w.SupplyTicks[e] < w.Rules.SupplyGraceTicks)
                _sb.Append($"<color=#ffd27f>Supply running out: {w.SupplyTicks[e] / SimConstants.TicksPerSecond}s</color>\n");

            GUI.Label(new Rect(r.x, y, r.width, r.height - (y - r.y)), _sb.ToString(), _label);
        }

        private void DrawSelectionSummary(DefDatabase defs, int total, Rect r)
        {
            _sb.Length = 0;
            _sb.Append($"<b>Selection</b>  ({total})\n");
            for (int d = 0; d < _selCounts.Length; d++)
                if (_selCounts[d] > 0)
                    _sb.Append($"{_selCounts[d]} × {PrettyName(defs.Units[d].Id)}\n");
            for (int d = 0; d < _selBuildingCounts.Length; d++)
                if (_selBuildingCounts[d] > 0)
                    _sb.Append($"{_selBuildingCounts[d]} × {PrettyName(defs.Buildings[d].Id)}\n");
            GUI.Label(r, _sb.ToString(), _label);
        }

        private void DrawNodeCard(SimWorld w, int e, Rect r)
        {
            bool mineral = w.NodeMineral[e];
            float y = r.y;
            GUI.Label(new Rect(r.x, y, r.width, 22),
                mineral ? Min("Mineral Pool") : Nut("Nutrient Pool"), _header);
            y += 26;

            int workers = 0;
            for (int i = 0; i < w.HighWater; i++)
                if (w.Kind[i] == EntityKind.Unit && w.WorkNode[i] == e) workers++;

            _sb.Length = 0;
            string label = mineral ? Min("Minerals remaining: <b>" + w.NodeFood[e] + "</b>")
                                   : Nut("Nutrients remaining: <b>" + w.NodeFood[e] + "</b>");
            _sb.Append(label).Append('\n');
            _sb.Append($"Harvesters here: <b>{workers}</b>\n");
            _sb.Append(mineral
                ? Min("Rally workers here to mine minerals (for evolution prongs).")
                : "<color=#bfe6a8>Rally a building here so new workers mine it.</color>");
            GUI.Label(new Rect(r.x, y, r.width + 260, r.height - (y - r.y)), _sb.ToString(), _label);
        }

        private void DrawBuildingCard(SimWorld w, DefDatabase defs, int e, Rect r)
        {
            var def = defs.Buildings[w.DefIndex[e]];
            float y = r.y;

            GUI.Label(new Rect(r.x, y, r.width, 22), PrettyName(def.Id), _header);
            y += 24;

            int maxHp = def.MaxHp << w.Tier[e]; // cache tiers double the shell
            float frac = Mathf.Clamp01(w.Hp[e] / (float)maxHp);
            Tint(new Rect(r.x, y, 300, 12), new Color(0.25f, 0.05f, 0.05f, 0.9f));
            Tint(new Rect(r.x, y, 300 * frac, 12), new Color(0.15f, 0.75f, 0.25f, 0.95f));
            GUI.Label(new Rect(r.x + 306, y - 4, 120, 20), $"{w.Hp[e]} / {maxHp}", _small);
            y += 18;

            _sb.Length = 0;
            if (!string.IsNullOrEmpty(def.Description))
                _sb.Append($"<color=#9fb0a4><i>{def.Description}</i></color>\n");
            if (def.IsHeadquarters)
                _sb.Append($"<b>Colony core</b> — {Nut("nutrient")} drop-off; lose it and you are eliminated\n");
            else if (def.HubBuilt)
                _sb.Append($"<b>Core extension</b> — a tech-path prong; workers drop off {Nut("nutrients")} here\n");
            if (def.AttackBonus > 0 && w.ConstructionRemaining[e] == 0)
            {
                int total = w.Owner[e] < w.ScratchAttackBonus.Length ? w.ScratchAttackBonus[w.Owner[e]] : def.AttackBonus;
                _sb.Append($"<color=#9fe0a0><b>+{def.AttackBonus} attack</b> to all your units — colony total <b>+{total}</b></color>\n");
            }

            if (def.ProvidesSupply && w.ConstructionRemaining[e] == 0)
            {
                _sb.Append(w.ScratchConnected[e]
                    ? "Supply: <color=#b8ff9e><b>connected</b></color>"
                    : "Supply: <color=#ff7766><b>CUT OFF</b></color> — no chain back to an HQ");
                _sb.Append("   ").Append(Nut($"Stock <b>{w.DepotStock[e]}</b> / {SupplySystem.StockCapOf(w, defs, e)}")).Append('\n');
                if (!def.IsHeadquarters)
                    _sb.Append(w.Tier[e] > 0
                        ? $"Tier <b>{w.Tier[e] + 1}</b> — armed, {1 << w.Tier[e]}x stock, 1/{1 << w.Tier[e]} drain\n"
                        : "Tier <b>1</b> — upgrade to arm it and stretch supplies\n");
                if (w.DepotStock[e] == 0)
                    _sb.Append("<color=#ffcf66><b>DRY</b> — supplies nothing until workers restock it</color>\n");
            }

            if (w.ConstructionRemaining[e] > 0)
            {
                int total = def.BuildTimeTicks * 3; // sites track work units = 3 × build ticks
                int done = total > 0 ? 100 * (total - w.ConstructionRemaining[e]) / total : 0;
                _sb.Append($"<color=#ffd966><b>Under construction</b>  {done}%</color>\n");
                GUI.Label(new Rect(r.x, y, r.width, r.height - (y - r.y)), _sb.ToString(), _label);
                return;
            }

            _sb.Append(w.HasRally[e]
                ? "Rally: <b>set</b> (right-click to move it, [R] to clear)\n"
                : "Rally: none (right-click the map to set)\n");

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
            }
            else _sb.Append("Produces nothing\n");

            GUI.Label(new Rect(r.x, y, r.width, r.height - (y - r.y)), _sb.ToString(), _label);
        }

        private void DrawProductionGrid(SimWorld w, DefDatabase defs, InputController input, int primary)
        {
            var bdef = defs.Buildings[w.DefIndex[primary]];
            bool produces = bdef.ProducesDense.Length > 0;
            bool cache = bdef.ProvidesSupply && !bdef.IsHeadquarters && w.ConstructionRemaining[primary] == 0;
            if (!produces && !w.HasRally[primary] && !cache) return;

            float gridW = GridCols * ButtonSize + (GridCols - 1) * ButtonGap;
            float gx = _panelRect.xMax - gridW - 12;
            float gy = _panelRect.y + 8;
            gy += DrawEntityDial(w, input, primary, gx, gy, gridW);
            GUI.Label(new Rect(gx, gy - 2, gridW, 18), produces ? "<b>Produce</b>" : "<b>Spire</b>", _small);
            gy += 18;

            int over = w.ProduceOverride[primary];
            var oldBg = GUI.backgroundColor;
            var hi = new Color(1f, 0.9f, 0.35f);
            int slot = 0;

            Rect Next()
            {
                int col = slot % GridCols, row = slot / GridCols;
                slot++;
                return new Rect(gx + col * (ButtonSize + ButtonGap), gy + row * (ButtonSize + ButtonGap), ButtonSize, ButtonSize);
            }

            if (produces)
            {
                GUI.backgroundColor = over < 0 ? hi : oldBg;
                if (GUI.Button(Next(), "Auto\n<b>[1]</b>", _button)) input.ApplyProduceOverride(-1);

                for (int k = 0; k < bdef.ProducesDense.Length; k++)
                {
                    int unitIx = bdef.ProducesDense[k];
                    var udef = defs.Units[unitIx];
                    GUI.backgroundColor = over == unitIx ? hi : oldBg;
                    if (GUI.Button(Next(), $"{ShortName(udef.Id)}\n{Nut(udef.FoodCost + "n")} <b>[{k + 2}]</b>", _button))
                        input.ApplyProduceOverride(unitIx);
                }

                GUI.backgroundColor = w.ProducePaused[primary] ? hi : oldBg;
                string pauseLabel = w.ProducePaused[primary] ? "Resume\n<b>[P]</b>" : "Pause\n<b>[P]</b>";
                if (GUI.Button(Next(), pauseLabel, _button)) input.ToggleProducePaused(primary);
            }

            // The headquarters grows tech-path prongs (hub-built buildings), one of each.
            if (bdef.IsHeadquarters)
            {
                for (int b = 0; b < defs.Buildings.Length; b++)
                {
                    var pd = defs.Buildings[b];
                    if (!pd.HubBuilt) continue;
                    bool have = PlayerHasBuilding(w, b);
                    string name = PrettyName(pd.Id).Split(' ')[0]; // "Lysis Chamber" → "Lysis"
                    GUI.backgroundColor = have ? hi : oldBg;
                    if (GUI.Button(Next(), have ? $"{name}\n<b>✓</b>" : $"{name}\n{Min(pd.MineralCost + "m")}", _button) && !have)
                        input.BuildProng(primary, b);
                }
            }

            // Tech-path upgrades gated behind THIS building — buy once each.
            var upLevels = w.Players[MatchBootstrap.HumanPlayer].UpgradeLevels;
            for (int u = 0; u < defs.Upgrades.Length; u++)
            {
                var up = defs.Upgrades[u];
                if (up.RequiresBuildingDense != w.DefIndex[primary]) continue;
                bool owned = u < upLevels.Length && upLevels[u] != 0;
                GUI.backgroundColor = owned ? hi : oldBg;
                if (GUI.Button(Next(), owned ? $"{ShortName(up.Id)}\n<b>✓</b>" : $"{ShortName(up.Id)}\n{Nut(up.FoodCost + "n")}", _button) && !owned)
                    input.BuyUpgrade(u);
            }
            GUI.backgroundColor = oldBg;

            if (cache && w.Tier[primary] < w.Rules.CacheMaxTier)
            {
                GUI.backgroundColor = oldBg;
                int cost = w.Rules.CacheUpgradeFoodCost << w.Tier[primary];
                if (GUI.Button(Next(), $"Upgrade\n{Nut(cost + "n")} <b>[U]</b>", _button)) input.UpgradeCache(primary);
            }

            if (w.HasRally[primary])
            {
                GUI.backgroundColor = oldBg;
                if (GUI.Button(Next(), "No Rally\n<b>[R]</b>", _button)) input.ClearRally();
            }
            GUI.backgroundColor = oldBg;
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

        /// <summary>Does the human already own a building of this def (built or growing)?</summary>
        private static bool PlayerHasBuilding(SimWorld w, int buildingDense)
        {
            for (int i = 0; i < w.HighWater; i++)
                if (w.Kind[i] == EntityKind.Building && w.Owner[i] == MatchBootstrap.HumanPlayer && w.DefIndex[i] == buildingDense)
                    return true;
            return false;
        }

        private void DrawCommandGrid(SimWorld w, DefDatabase defs, InputController input, int primary)
        {
            if (defs.Units[w.DefIndex[primary]].IsWorker)
            {
                DrawWorkerGrid(w, defs, input, primary);
                return;
            }

            float gridW = GridCols * ButtonSize + (GridCols - 1) * ButtonGap;
            float gx = _panelRect.xMax - gridW - 12;
            float gy = _panelRect.y + 6;

            gy += DrawEntityDial(w, input, primary, gx, gy, gridW);

            float actY = gy + 4;
            int slot = 0;
            Rect Act()
            {
                var r = new Rect(gx + slot * (ButtonSize + ButtonGap), actY, ButtonSize, ButtonSize * 0.9f);
                slot++;
                return r;
            }
            if (GUI.Button(Act(), "Stop\n<b>[S]</b>", _button)) input.StopSelected();
        }

        /// <summary>
        /// The dial row at the top of every selection's grid column. Selecting the colony core
        /// (HQ) binds it to the supply-priority dial (core-keeping vs front-line caravans);
        /// every other unit/building shows its own per-entity dial — hashed sim state reserved
        /// for coming modifiers. Returns the row height consumed.
        /// </summary>
        private float DrawEntityDial(SimWorld w, InputController input, int primary, float gx, float gy, float width)
        {
            bool core = w.Kind[primary] == EntityKind.Building
                && _match.Defs.Buildings[w.DefIndex[primary]].IsHeadquarters;
            if (core)
            {
                var me = w.Players[MatchBootstrap.HumanPlayer];
                GUI.Label(new Rect(gx, gy, 34, 16), "Core", _small);
                float v = GUI.HorizontalSlider(new Rect(gx + 36, gy + 4, width - 128, 12), me.SupplyPriority, 0f, 100f);
                GUI.Label(new Rect(gx + width - 88, gy, 88, 16), $"Front <b>{me.SupplyPriority}%</b>", _small);
                int snap = Mathf.RoundToInt(v / 5f) * 5;
                if (snap != me.SupplyPriority)
                    _match.Enqueue(new Command { Type = CommandType.SetSupplyPriority, A = snap });
            }
            else
            {
                int cur = w.Dial[primary];
                GUI.Label(new Rect(gx, gy, 34, 16), "Mod", _small);
                float v = GUI.HorizontalSlider(new Rect(gx + 36, gy + 4, width - 128, 12), cur, 0f, 100f);
                GUI.Label(new Rect(gx + width - 88, gy, 88, 16), $"<b>{cur}%</b> <i>rsvd</i>", _small);
                int snap = Mathf.RoundToInt(v / 5f) * 5;
                if (snap != cur) input.ApplyDial(snap);
            }
            return 18f;
        }

        private void DrawWorkerGrid(SimWorld w, DefDatabase defs, InputController input, int primary)
        {
            float gridW = GridCols * ButtonSize + (GridCols - 1) * ButtonGap;
            float gx = _panelRect.xMax - gridW - 12;
            float gy = _panelRect.y + 8;
            gy += DrawEntityDial(w, input, primary, gx, gy, gridW);

            GUI.Label(new Rect(gx, gy - 2, gridW, 18), "<b>Build</b>", _small);
            gy += 18;

            var oldBg = GUI.backgroundColor;
            int slot = 0;
            for (int b = 0; b < defs.Buildings.Length; b++)
            {
                if (!defs.Buildings[b].Constructible) continue;
                int col = slot % GridCols, row = slot / GridCols;
                var rect = new Rect(gx + col * (ButtonSize + ButtonGap), gy + row * (ButtonSize + ButtonGap), ButtonSize, ButtonSize);
                GUI.backgroundColor = input.PlacingBuilding == b ? new Color(1f, 0.9f, 0.35f) : oldBg;
                string key = slot == 0 ? " <b>[B]</b>" : "";
                if (GUI.Button(rect, $"{ShortName(defs.Buildings[b].Id)}\n{CostLabel(defs.Buildings[b])}{key}", _button))
                    input.BeginPlacement(b);
                slot++;
            }

            // Stop rides along so workers can be halted from the same grid.
            var stopRect = new Rect(gx + slot % GridCols * (ButtonSize + ButtonGap),
                gy + slot / GridCols * (ButtonSize + ButtonGap), ButtonSize, ButtonSize);
            GUI.backgroundColor = oldBg;
            if (GUI.Button(stopRect, "Stop\n<b>[S]</b>", _button))
                input.StopSelected();
        }

        // Dotted trail tracing the actual drawn curve = where the formation front will stand
        // (screen space, y flipped for GUI). A brighter dot at the head marks the drag end.
        private void DrawFormationPath(IReadOnlyList<Vector2> path)
        {
            if (path == null || path.Count == 0) return;
            const float step = 10f; // screen px between dots, so long/short drags read the same
            float carry = 0f;
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

        private void Tint(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = old;
        }

        /// <summary>Grid-button-sized name: the last word of the pretty name
        /// ("strain.swarm-leader" → "Leader", "strain.incubator" → "Incubator").</summary>
        public static string ShortName(string id)
        {
            string pretty = PrettyName(id);
            int space = pretty.LastIndexOf(' ');
            return space >= 0 ? pretty.Substring(space + 1) : pretty;
        }

        /// <summary>"strain.swarm-leader" → "Swarm Leader".</summary>
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
