using System.Collections.Generic;
using UnityEngine;
using Petri.Core;

namespace Petri.Client
{
    /// <summary>
    /// Translates mouse/keyboard into Commands for the superorganism game — the only way
    /// the human touches the sim. Left-click selects a building/node, or — on open ground —
    /// the FRONT whose sector the click falls in (the organism's border section). With a
    /// front selected, right-click-drag sketches a push path and release orders PushFront
    /// at the end point; [S] stops the push. With a producer selected, right-click rallies
    /// its production to the clicked sector ([R] back to auto). The build flow places
    /// structures inside your territory. Clicks over the HUD are ignored.
    /// </summary>
    public sealed class InputController : MonoBehaviour
    {
        public readonly HashSet<int> Selected = new HashSet<int>();
        public int PlacingBuilding { get; private set; } = -1; // building dense ix while placing, -1 off
        public int SelectedFront { get; private set; } = -1;   // front (sector) index, -1 none

        // Live right-drag push path, screen coords (bottom-up) — HudView draws the dots.
        public bool RightDragging { get; private set; }
        public readonly List<Vector2> RightPathScreen = new List<Vector2>();

        private MatchBootstrap _match;
        private Camera _cam;

        public void Bind(MatchBootstrap match, Camera cam)
        {
            _match = match;
            _cam = cam;
        }

        private void Update()
        {
            if (_match == null || _match.Sim == null) return;
            var w = _match.Sim.World;
            PruneDead(w);

            bool overHud = _match.Hud != null && _match.Hud.IsPointerOver(Input.mousePosition);

            // Build-placement mode swallows the mouse: left places, right/Esc cancels.
            if (PlacingBuilding >= 0)
            {
                if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1)) PlacingBuilding = -1;
                else if (Input.GetMouseButtonDown(0) && !overHud) PlaceBuilding();
                return;
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Selected.Clear();
                SelectedFront = -1;
            }

            // Minimap: left-press (or drag) pans the camera there.
            if (_match.Hud != null && _match.Hud.MinimapContains(Input.mousePosition))
            {
                if (Input.GetMouseButton(0))
                {
                    var mp = _match.Hud.MinimapToWorld(Input.mousePosition);
                    var p = _cam.transform.position;
                    _cam.transform.position = new Vector3(mp.x, mp.y, p.z);
                }
                return;
            }

            // Left click: a building/node under the cursor wins; open ground selects the
            // FRONT the click's direction falls in.
            if (Input.GetMouseButtonDown(0) && !overHud)
            {
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                if (!shift) Selected.Clear();
                if (SelectNearest(w))
                {
                    SelectedFront = -1;
                }
                else if (!shift)
                {
                    SelectedFront = FrontAt(WorldAt(Input.mousePosition));
                }
            }

            HandleRightMouse(w, overHud);
            HandleKeys(w);
        }

        private void HandleRightMouse(SimWorld w, bool overHud)
        {
            bool blocked = _match.Hud != null && _match.Hud.BlocksRightClick(Input.mousePosition);

            if (Input.GetMouseButtonDown(1) && !overHud && !blocked)
            {
                if (AnySelectedProducer(w))
                {
                    // Rally the selected producers' output to the clicked sector.
                    int front = FrontAt(WorldAt(Input.mousePosition));
                    if (front >= 0)
                    {
                        foreach (int e in Selected)
                            if (IsProducer(w, e))
                                _match.Enqueue(new Command { Type = CommandType.RallyProduction, A = e, B = front });
                        _match.View.Ping(WorldAt(Input.mousePosition), GameView.RallyPing);
                    }
                }
                else if (SelectedFront >= 0)
                {
                    RightDragging = true;
                    RightPathScreen.Clear();
                    RightPathScreen.Add(Input.mousePosition);
                }
            }

            if (RightDragging && Input.GetMouseButton(1))
            {
                Vector2 now = Input.mousePosition;
                if (RightPathScreen.Count == 0
                    || (now - RightPathScreen[RightPathScreen.Count - 1]).sqrMagnitude > 36f)
                    RightPathScreen.Add(now);
            }

            if (RightDragging && Input.GetMouseButtonUp(1))
            {
                RightDragging = false;
                if (SelectedFront >= 0 && RightPathScreen.Count > 0)
                {
                    // The sketched path ENDS where the push should reach; the sim drives
                    // the front toward that point beat by beat.
                    Vector3 end = WorldAt(RightPathScreen[RightPathScreen.Count - 1]);
                    _match.Enqueue(new Command
                    {
                        Type = CommandType.PushFront, A = SelectedFront,
                        B = Mathf.RoundToInt(end.x * 100f), C = Mathf.RoundToInt(end.y * 100f),
                    });
                    _match.View.Ping(end, GameView.AttackPing);
                }
                RightPathScreen.Clear();
            }
        }

        private void HandleKeys(SimWorld w)
        {
            if (Input.GetKeyDown(KeyCode.S) && SelectedFront >= 0)
            {
                _match.Enqueue(new Command { Type = CommandType.StopFront, A = SelectedFront });
            }
            if (Input.GetKeyDown(KeyCode.R))
            {
                foreach (int e in Selected)
                    if (IsProducer(w, e))
                        _match.Enqueue(new Command { Type = CommandType.RallyProduction, A = e, B = -1 });
            }
        }

        /// <summary>The front (sector) of the human organism a world point falls in, from
        /// the SAME ray tables the sim classifies with. -1 before the organism exists.</summary>
        public int FrontAt(Vector3 worldPos)
        {
            var w = _match.Sim.World;
            const int p = MatchBootstrap.HumanPlayer;
            if (!w.Players[p].Alive || w.ScratchCentCount[p] == 0) return -1;
            long dx = Mathf.RoundToInt(worldPos.x * 100f) - w.ScratchCentXCenti[p];
            long dy = Mathf.RoundToInt(worldPos.y * 100f) - w.ScratchCentYCenti[p];
            return FrontMath.Sector(w.Players[p].FrontCount, dx, dy);
        }

        private bool AnySelectedProducer(SimWorld w)
        {
            foreach (int e in Selected)
                if (IsProducer(w, e)) return true;
            return false;
        }

        private bool IsProducer(SimWorld w, int e) =>
            w.Kind[e] == EntityKind.Building && w.Owner[e] == MatchBootstrap.HumanPlayer
            && _match.Defs.Buildings[w.DefIndex[e]].ProducesDense.Length > 0;

        /// <summary>Enter build-placement mode (ghost follows the mouse until click/cancel).</summary>
        public void BeginPlacement(int buildingIx)
        {
            if (buildingIx < 0 || buildingIx >= _match.Defs.Buildings.Length) return;
            if (!_match.Defs.Buildings[buildingIx].Constructible) return;
            PlacingBuilding = buildingIx;
        }

        private void PlaceBuilding()
        {
            Vector3 wp = WorldAt(Input.mousePosition);
            _match.Enqueue(new Command
            {
                Type = CommandType.ConstructBuilding, A = -1,
                B = Mathf.RoundToInt(wp.x * 100f), C = Mathf.RoundToInt(wp.y * 100f),
                D = PlacingBuilding,
            });
            _match.View.Ping(new Vector3(wp.x, wp.y, 0f), GameView.MovePing);
            PlacingBuilding = -1;
        }

        /// <summary>Pin every selected building that can produce the unit to it (-1 = back to auto).</summary>
        public void ApplyProduceOverride(int unitIx)
        {
            var w = _match.Sim.World;
            foreach (int e in Selected)
            {
                if (w.Kind[e] != EntityKind.Building) continue;
                if (unitIx >= 0)
                {
                    var bdef = _match.Defs.Buildings[w.DefIndex[e]];
                    bool producible = false;
                    for (int i = 0; i < bdef.ProducesDense.Length; i++)
                        if (bdef.ProducesDense[i] == unitIx) { producible = true; break; }
                    if (!producible) continue;
                }
                _match.Enqueue(new Command { Type = CommandType.SetProduceOverride, A = e, B = unitIx });
            }
        }

        /// <summary>Toggle production pause on selected buildings (following the primary).</summary>
        public void ToggleProducePaused(int primary)
        {
            var w = _match.Sim.World;
            if (primary < 0 || w.Kind[primary] != EntityKind.Building) return;
            bool pause = !w.ProducePaused[primary];
            foreach (int e in Selected)
                if (w.Kind[e] == EntityKind.Building)
                    _match.Enqueue(new Command { Type = CommandType.SetProducePaused, A = e, B = pause ? 1 : 0 });
        }

        public int PrimarySelected()
        {
            int primary = -1;
            foreach (int e in Selected)
                if (primary < 0 || e < primary) primary = e;
            return primary;
        }

        private Vector3 WorldAt(Vector2 screen) =>
            _cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -_cam.transform.position.z));

        /// <summary>Select the building/node under the cursor; true when something was hit.</summary>
        private bool SelectNearest(SimWorld w)
        {
            Vector3 wp = WorldAt(Input.mousePosition);
            const float grace = 0.08f;
            int bestBuilding = -1, bestNode = -1;
            float bestBuildingSq = float.MaxValue, bestNodeSq = float.MaxValue;
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] == EntityKind.None) continue;
                float dx = w.Pos[i].X.Raw / (float)Fix.OneRaw - wp.x;
                float dy = w.Pos[i].Y.Raw / (float)Fix.OneRaw - wp.y;
                float dsq = dx * dx + dy * dy;
                if (w.Kind[i] == EntityKind.Node)
                {
                    float r = w.Rules.NodeRadiusCenti / 100f + grace;
                    if (dsq <= r * r && dsq < bestNodeSq) { bestNodeSq = dsq; bestNode = i; }
                }
                else if (w.Kind[i] == EntityKind.Building && w.Owner[i] == MatchBootstrap.HumanPlayer)
                {
                    float r = _match.Defs.Buildings[w.DefIndex[i]].CollisionRadiusCenti / 100f + grace;
                    if (Mathf.Abs(dx) <= r && Mathf.Abs(dy) <= r && dsq < bestBuildingSq) { bestBuildingSq = dsq; bestBuilding = i; }
                }
            }
            if (bestBuilding >= 0) { Selected.Add(bestBuilding); return true; }
            if (bestNode >= 0) { Selected.Clear(); Selected.Add(bestNode); return true; }
            return false;
        }

        private readonly List<int> _scratch = new List<int>();

        private void PruneDead(SimWorld w)
        {
            if (SelectedFront >= 0 && SelectedFront >= w.Players[MatchBootstrap.HumanPlayer].FrontCount)
                SelectedFront = -1; // K was stepped down under the selection
            if (Selected.Count == 0) return;
            _scratch.Clear();
            foreach (int e in Selected)
            {
                if (e < 0 || e >= w.HighWater) continue;
                if (w.Kind[e] == EntityKind.Node || (w.Kind[e] == EntityKind.Building && w.Owner[e] == MatchBootstrap.HumanPlayer))
                    _scratch.Add(e);
            }
            if (_scratch.Count != Selected.Count)
            {
                Selected.Clear();
                foreach (int e in _scratch) Selected.Add(e);
            }
        }
    }
}
