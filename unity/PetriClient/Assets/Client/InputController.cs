using System.Collections.Generic;
using UnityEngine;
using Petri.Core;

namespace Petri.Client
{
    /// <summary>
    /// Translates mouse/keyboard into Commands for the superorganism game — the only way
    /// the human touches the sim. INTERIM (conversion in progress): left-click selects
    /// buildings/nodes, the build flow places structures inside your territory, minimap
    /// left-press pans. Front selection and push-drag orders land with the fronts UI.
    /// Clicks over the HUD panel are ignored (HudView.IsPointerOver).
    /// </summary>
    public sealed class InputController : MonoBehaviour
    {
        public readonly HashSet<int> Selected = new HashSet<int>();
        public int PlacingBuilding { get; private set; } = -1; // building dense ix while placing, -1 off

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

            // Left click: select the building/node under the cursor.
            if (Input.GetMouseButtonDown(0) && !overHud)
            {
                if (!Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift)) Selected.Clear();
                SelectNearest(w);
            }
        }

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

        private void SelectNearest(SimWorld w)
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
            if (bestBuilding >= 0) Selected.Add(bestBuilding);
            else if (bestNode >= 0) { Selected.Clear(); Selected.Add(bestNode); }
        }

        private readonly List<int> _scratch = new List<int>();

        private void PruneDead(SimWorld w)
        {
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
