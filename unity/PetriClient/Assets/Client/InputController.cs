using System.Collections.Generic;
using UnityEngine;
using Petri.Core;

namespace Petri.Client
{
    /// <summary>
    /// Translates mouse/keyboard into Commands — the only way the human touches the sim.
    ///   Left click         : select the unit under the cursor, or your building if none.
    ///   Left drag          : box-select your units (Shift adds to selection).
    ///   Right click        : selected leaders formation-move their whole squad to the point;
    ///                        other selected units get a plain move order.
    ///   1..9               : units selected → switch tactical formation (leaders re-form in
    ///                        place); building selected → choose its production ([1] = Auto).
    ///   S                  : stop all selected units.
    /// Clicks over the HUD command panel are ignored (HudView.IsPointerOver), AoE2-style.
    /// Selection holds entity indices and is pruned each frame as entities die/reuse slots.
    /// </summary>
    public sealed class InputController : MonoBehaviour
    {
        public readonly HashSet<int> Selected = new HashSet<int>();
        public int PlacingBuilding { get; private set; } = -1; // building dense ix while placing, -1 off
        public bool AttackArmed;                               // [A] pressed: next click is attack-move
        public bool IsDragging { get; private set; }
        public Vector2 DragStartScreen { get; private set; }
        public Vector2 DragNowScreen { get; private set; }
        public bool RightDragging { get; private set; }        // drawing a formation line
        public Vector2 RightDragStartScreen { get; private set; }
        public Vector2 RightDragNowScreen { get; private set; }
        public bool FacingDragging { get; private set; }       // Shift+R-drag: aiming a facing
        public Vector2 FacingDragStartScreen { get; private set; }
        public Vector2 FacingDragNowScreen { get; private set; }

        private MatchBootstrap _match;
        private Camera _cam;
        private const float ClickPixels = 6f;
        private const float FormationDragPixels = 24f;
        private const float PathSamplePixels = 9f;
        private const float DoubleClickSeconds = 0.35f;
        private float _lastClickTime = -10f;
        private int _lastClickDef = -1;
        private EntityKind _lastClickKind;
        private readonly List<int> _lineUnits = new List<int>();
        private readonly List<int> _lineLeaders = new List<int>();
        private readonly List<Vector2> _rightPath = new List<Vector2>();  // screen-space drawn curve
        private readonly List<Vector2> _worldPath = new List<Vector2>();  // world-space, built on release
        private readonly List<float> _cum = new List<float>();            // cumulative arc-length

        /// <summary>The screen-space curve being drawn with a right-drag (for the HUD preview).</summary>
        public IReadOnlyList<Vector2> RightPathScreen => _rightPath;

        // Control groups 1..9 store (slot, generation) so recall survives entity-index reuse:
        // a dead unit's slot taken by a new one won't be wrongly re-selected.
        private readonly List<(int idx, int gen)>[] _groups = new List<(int, int)>[10];

        public void Bind(MatchBootstrap match, Camera cam)
        {
            _match = match;
            _cam = cam;
            for (int n = 1; n <= 9; n++) _groups[n] = new List<(int, int)>();
        }

        public bool GroupPopulated(int n) => n >= 1 && n <= 9 && _groups[n].Count > 0;

        /// <summary>Save the current selection (own units/buildings) as control group N.</summary>
        public void AssignControlGroup(int n)
        {
            if (n < 1 || n > 9) return;
            var w = _match.Sim.World;
            var g = _groups[n];
            g.Clear();
            for (int i = 0; i < w.HighWater; i++) // index order: stable snapshot
            {
                if (!Selected.Contains(i)) continue;
                if (w.Owner[i] != MatchBootstrap.HumanPlayer) continue;
                if (w.Kind[i] == EntityKind.Unit || w.Kind[i] == EntityKind.Building)
                    g.Add((i, w.Generation[i]));
            }
        }

        /// <summary>Recall control group N; only members still alive with a matching generation.</summary>
        public void SelectControlGroup(int n, bool add)
        {
            if (n < 1 || n > 9) return;
            var w = _match.Sim.World;
            if (!add) Selected.Clear();
            AttackArmed = false;
            PlacingBuilding = -1;
            foreach (var m in _groups[n])
                if (m.idx < w.HighWater && w.Generation[m.idx] == m.gen
                    && w.Owner[m.idx] == MatchBootstrap.HumanPlayer
                    && (w.Kind[m.idx] == EntityKind.Unit || w.Kind[m.idx] == EntityKind.Building))
                    Selected.Add(m.idx);
        }

        private void Update()
        {
            if (_match == null || _match.Sim == null) return;
            var w = _match.Sim.World;
            PruneDead(w);
            HandleHotkeys();

            // Left clicks respect the whole panel (selection safety); right clicks pass
            // through the panel's dead background and are only swallowed by real widgets
            // and the minimap — so the bottom strip of the battlefield stays orderable.
            bool overHud = _match.Hud != null && _match.Hud.IsPointerOver(Input.mousePosition);
            bool blocksRight = _match.Hud != null && _match.Hud.BlocksRightClick(Input.mousePosition);

            // Build-placement mode swallows the mouse: left places, right/Esc cancels.
            if (PlacingBuilding >= 0)
            {
                if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1)) PlacingBuilding = -1;
                else if (Input.GetMouseButtonDown(0) && !overHud) PlaceBuilding(w);
                return;
            }

            // Attack-move armed by [A]: the next left-click is an attack-move to that point.
            // With shift held the click queues AND stays armed, so a whole strike route can
            // be laid down in one pass.
            if (AttackArmed)
            {
                if (Input.GetKeyDown(KeyCode.Escape) || Input.GetMouseButtonDown(1)) AttackArmed = false;
                else if (Input.GetMouseButtonDown(0) && !overHud)
                {
                    bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                    IssueAttackMove(w, WorldAt(Input.mousePosition), shift);
                    AttackArmed = shift;
                }
                return;
            }

            // ---- Minimap: left-press (or drag) pans the camera there; right-click orders a
            // move at that map point (shift queues). Active world drags keep priority so a
            // box-select released over the minimap still completes.
            if (_match.Hud != null && !IsDragging && !RightDragging && !FacingDragging
                && _match.Hud.MinimapContains(Input.mousePosition))
            {
                if (Input.GetMouseButton(0))
                {
                    var mp = _match.Hud.MinimapToWorld(Input.mousePosition);
                    var p = _cam.transform.position;
                    _cam.transform.position = new Vector3(mp.x, mp.y, p.z);
                    return;
                }
                if (Input.GetMouseButtonDown(1) && Selected.Count > 0)
                {
                    var mp = _match.Hud.MinimapToWorld(Input.mousePosition);
                    bool shiftQ = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                    IssueMoveOrdersAt(w, new Vector3(mp.x, mp.y, 0f), shiftQ);
                    return;
                }
            }

            // ---- Left mouse: select (click) or box-select (drag).
            if (Input.GetMouseButtonDown(0) && !overHud)
            {
                IsDragging = true;
                DragStartScreen = DragNowScreen = Input.mousePosition;
            }
            else if (Input.GetMouseButton(0) && IsDragging)
            {
                DragNowScreen = Input.mousePosition;
            }
            else if (Input.GetMouseButtonUp(0) && IsDragging)
            {
                IsDragging = false;
                if (!Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift)) Selected.Clear();
                if (Vector2.Distance(DragStartScreen, Input.mousePosition) <= ClickPixels)
                    SelectNearest(w);
                else
                    SelectBox(w);
            }

            // ---- Shift+Right drag: aim a facing — on release everything selected turns to it.
            if (Input.GetMouseButtonDown(1) && Selected.Count > 0 && !blocksRight
                && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
            {
                FacingDragging = true;
                FacingDragStartScreen = FacingDragNowScreen = Input.mousePosition;
            }
            else if (Input.GetMouseButton(1) && FacingDragging)
            {
                FacingDragNowScreen = Input.mousePosition;
            }
            else if (Input.GetMouseButtonUp(1) && FacingDragging)
            {
                FacingDragging = false;
                if (!IssueFacing(w))
                {
                    // Too short to read a direction: it was a shift+right-CLICK — queue the
                    // order behind whatever the selection is already doing.
                    int primary = PrimarySelected();
                    if (primary >= 0 && w.Kind[primary] == EntityKind.Building) IssueRally(w);
                    else IssueMoveOrders(w, true);
                }
            }
            // ---- Right mouse: click = move/rally/attack; drag = BAR-style formation curve.
            else if (Input.GetMouseButtonDown(1) && Selected.Count > 0 && !blocksRight)
            {
                RightDragging = true;
                RightDragStartScreen = RightDragNowScreen = Input.mousePosition;
                _rightPath.Clear();
                _rightPath.Add(Input.mousePosition);
            }
            else if (Input.GetMouseButton(1) && RightDragging)
            {
                RightDragNowScreen = Input.mousePosition;
                if (Vector2.Distance(_rightPath[_rightPath.Count - 1], Input.mousePosition) >= PathSamplePixels)
                    _rightPath.Add(Input.mousePosition);
            }
            else if (Input.GetMouseButtonUp(1) && RightDragging)
            {
                RightDragging = false;
                if ((Vector2)Input.mousePosition != _rightPath[_rightPath.Count - 1]) _rightPath.Add(Input.mousePosition);
                int primary = PrimarySelected();
                bool dragged = ScreenPathLength(_rightPath) > FormationDragPixels;
                if (dragged && primary >= 0 && w.Kind[primary] == EntityKind.Unit)
                    IssueFormationPath(w);
                else if (primary >= 0 && w.Kind[primary] == EntityKind.Building)
                    IssueRally(w);
                else
                    IssueMoveOrders(w);
            }
        }

        /// <summary>Turn every selected unit to face along the dragged arrow (world-space
        /// direction). The facing holds while a unit stands idle.
        /// Returns false when the drag was too short to read a direction (a plain shift-click).</summary>
        private bool IssueFacing(SimWorld w)
        {
            Vector3 a = WorldAt(FacingDragStartScreen);
            Vector3 b = WorldAt(FacingDragNowScreen);
            var dir = new Vector2(b.x - a.x, b.y - a.y);
            if (dir.sqrMagnitude < 0.25f) return false; // too short a drag to read a direction
            dir.Normalize();
            int cx = Mathf.RoundToInt(dir.x * 100f), cy = Mathf.RoundToInt(dir.y * 100f);
            bool any = false;
            foreach (int e in Selected)
            {
                if (w.Kind[e] != EntityKind.Unit) continue;
                _match.Enqueue(new Command { Type = CommandType.SetFacing, A = e, B = cx, C = cy });
                any = true;
            }
            if (any) _match.View.Ping(new Vector3(b.x, b.y, 0f), GameView.MovePing);
            return true;
        }

        private static float ScreenPathLength(List<Vector2> pts)
        {
            float len = 0f;
            for (int i = 1; i < pts.Count; i++) len += Vector2.Distance(pts[i - 1], pts[i]);
            return len;
        }

        private void HandleHotkeys()
        {
            var w = _match.Sim.World;

            // Control groups on 1..9 are GLOBAL and take priority: Ctrl+N assigns the current
            // selection, N recalls it (Shift+N adds to the current selection).
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            for (int n = 1; n <= 9; n++)
            {
                if (!Input.GetKeyDown(KeyCode.Alpha0 + n)) continue;
                if (ctrl) AssignControlGroup(n);
                else SelectControlGroup(n, shift);
                return;
            }

            // Space selects all your military (non-worker) units map-wide.
            if (Input.GetKeyDown(KeyCode.Space)) { SelectAllMilitary(); return; }

            int primary = PrimarySelected();
            if (primary >= 0 && w.Kind[primary] == EntityKind.Building)
            {
                if (Input.GetKeyDown(KeyCode.P)) ToggleProducePaused(primary);
                if (Input.GetKeyDown(KeyCode.R)) ClearRally();
                if (Input.GetKeyDown(KeyCode.U)) UpgradeCache(primary);
                return;
            }

            // Worker mode: [B] starts placing the first constructible building.
            if (primary >= 0 && w.Kind[primary] == EntityKind.Unit
                && _match.Defs.Units[w.DefIndex[primary]].IsWorker && Input.GetKeyDown(KeyCode.B))
            {
                for (int b = 0; b < _match.Defs.Buildings.Length; b++)
                    if (_match.Defs.Buildings[b].Constructible) { BeginPlacement(b); break; }
            }

            if (Input.GetKeyDown(KeyCode.A) && HasArmedSelected(w)) AttackArmed = true; // attack-move
            if (Input.GetKeyDown(KeyCode.S)) StopSelected();
        }

        /// <summary>Select every military (non-worker) unit you own — soldiers, spitters, and
        /// aura leaders — across the whole map.</summary>
        public void SelectAllMilitary()
        {
            var w = _match.Sim.World;
            Selected.Clear();
            AttackArmed = false;
            PlacingBuilding = -1;
            for (int i = 0; i < w.HighWater; i++)
                if (w.Kind[i] == EntityKind.Unit && w.Owner[i] == MatchBootstrap.HumanPlayer
                    && !_match.Defs.Units[w.DefIndex[i]].IsWorker)
                    Selected.Add(i);
        }

        private bool HasArmedSelected(SimWorld w)
        {
            foreach (int e in Selected)
                if (w.Kind[e] == EntityKind.Unit && _match.Defs.Units[w.DefIndex[e]].AttackDamage > 0)
                    return true;
            return false;
        }

        private Vector3 WorldAt(Vector2 screen) =>
            _cam.ScreenToWorldPoint(new Vector3(screen.x, screen.y, -_cam.transform.position.z));

        /// <summary>Enter build-placement mode (ghost follows the mouse until click/cancel).</summary>
        public void BeginPlacement(int buildingIx)
        {
            if (buildingIx < 0 || buildingIx >= _match.Defs.Buildings.Length) return;
            if (!_match.Defs.Buildings[buildingIx].Constructible) return;
            PlacingBuilding = buildingIx;
        }

        private void PlaceBuilding(SimWorld w)
        {
            Vector3 wp = _cam.ScreenToWorldPoint(new Vector3(Input.mousePosition.x, Input.mousePosition.y, -_cam.transform.position.z));
            // The nearest selected worker takes the job.
            int builder = -1;
            float bestSq = float.MaxValue;
            foreach (int e in Selected)
            {
                if (w.Kind[e] != EntityKind.Unit || !_match.Defs.Units[w.DefIndex[e]].IsWorker) continue;
                float dx = w.Pos[e].X.Raw / (float)Fix.OneRaw - wp.x;
                float dy = w.Pos[e].Y.Raw / (float)Fix.OneRaw - wp.y;
                float dsq = dx * dx + dy * dy;
                if (dsq < bestSq) { bestSq = dsq; builder = e; }
            }
            if (builder >= 0)
            {
                _match.Enqueue(new Command
                {
                    Type = CommandType.ConstructBuilding, A = builder,
                    B = Mathf.RoundToInt(wp.x * 100f), C = Mathf.RoundToInt(wp.y * 100f),
                    D = PlacingBuilding,
                });
                _match.View.Ping(new Vector3(wp.x, wp.y, 0f), GameView.MovePing);
            }
            PlacingBuilding = -1;
        }

        private void IssueRally(SimWorld w)
        {
            Vector3 wp = _cam.ScreenToWorldPoint(new Vector3(Input.mousePosition.x, Input.mousePosition.y, -_cam.transform.position.z));
            int cx = Mathf.RoundToInt(wp.x * 100f), cy = Mathf.RoundToInt(wp.y * 100f);
            bool any = false;
            foreach (int e in Selected)
            {
                if (w.Kind[e] != EntityKind.Building) continue;
                _match.Enqueue(new Command { Type = CommandType.SetRally, A = e, B = cx, C = cy, D = 1 });
                any = true;
            }
            if (any) _match.View.Ping(new Vector3(wp.x, wp.y, 0f), GameView.RallyPing);
        }

        /// <summary>Toggle production pause on all selected buildings (following the primary).</summary>
        public void ToggleProducePaused(int primary)
        {
            var w = _match.Sim.World;
            if (primary < 0 || w.Kind[primary] != EntityKind.Building) return;
            bool pause = !w.ProducePaused[primary];
            foreach (int e in Selected)
                if (w.Kind[e] == EntityKind.Building)
                    _match.Enqueue(new Command { Type = CommandType.SetProducePaused, A = e, B = pause ? 1 : 0 });
        }

        /// <summary>Buy the next tier on every selected supply cache (the sim validates
        /// eligibility, tier cap, and cost per cache).</summary>
        public void UpgradeCache(int primary)
        {
            var w = _match.Sim.World;
            if (primary < 0 || w.Kind[primary] != EntityKind.Building) return;
            foreach (int e in Selected)
            {
                if (w.Kind[e] != EntityKind.Building) continue;
                var bdef = _match.Defs.Buildings[w.DefIndex[e]];
                if (!bdef.ProvidesSupply || bdef.IsHeadquarters) continue;
                if (w.Tier[e] >= w.Rules.CacheMaxTier || w.ConstructionRemaining[e] > 0) continue;
                _match.Enqueue(new Command { Type = CommandType.UpgradeCache, A = e });
            }
        }

        /// <summary>Purchase a tech-path upgrade (the sim validates cost, path building, and
        /// that it isn't already owned).</summary>
        public void BuyUpgrade(int upgradeIx)
        {
            if (upgradeIx < 0 || upgradeIx >= _match.Defs.Upgrades.Length) return;
            _match.Enqueue(new Command { Type = CommandType.BuyUpgrade, A = upgradeIx });
        }

        /// <summary>Grow a tech-path prong off the selected headquarters (the sim auto-places
        /// it, self-builds it, and validates cost / duplicate / room).</summary>
        public void BuildProng(int hq, int buildingIx)
        {
            if (hq < 0 || buildingIx < 0 || buildingIx >= _match.Defs.Buildings.Length) return;
            _match.Enqueue(new Command { Type = CommandType.BuildProng, A = hq, B = buildingIx });
        }

        /// <summary>Set the tuning dial on every selected own unit/building (future modifiers).</summary>
        public void ApplyDial(int value)
        {
            var w = _match.Sim.World;
            foreach (int e in Selected)
                if (w.Kind[e] == EntityKind.Unit || w.Kind[e] == EntityKind.Building)
                    _match.Enqueue(new Command { Type = CommandType.SetDial, A = e, B = value });
        }

        /// <summary>Remove the rally point from every selected building.</summary>
        public void ClearRally()
        {
            var w = _match.Sim.World;
            foreach (int e in Selected)
            {
                if (w.Kind[e] != EntityKind.Building || !w.HasRally[e]) continue;
                _match.Enqueue(new Command { Type = CommandType.SetRally, A = e, D = 0 });
            }
        }

        /// <summary>The stable "primary" the HUD card and hotkey context follow: the lowest
        /// selected entity index.</summary>
        public int PrimarySelected()
        {
            int primary = -1;
            foreach (int e in Selected)
                if (primary < 0 || e < primary) primary = e;
            return primary;
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

        /// <summary>Stop every selected unit (halts move orders; gatherers resume on their own).</summary>
        public void StopSelected()
        {
            var w = _match.Sim.World;
            foreach (int e in Selected)
            {
                if (w.Kind[e] != EntityKind.Unit) continue;
                _match.Enqueue(new Command { Type = CommandType.Stop, A = e });
            }
        }

        private void IssueMoveOrders(SimWorld w, bool queue = false) =>
            IssueMoveOrdersAt(w, WorldAt(Input.mousePosition), queue);

        private void IssueMoveOrdersAt(SimWorld w, Vector3 wp, bool queue)
        {
            // Right-clicking on an enemy is an attack order (attack-move to it).
            int enemy = FindEnemyAt(w, wp);
            if (enemy >= 0) { IssueAttackMove(w, new Vector3(w.Pos[enemy].X.Raw / (float)Fix.OneRaw, w.Pos[enemy].Y.Raw / (float)Fix.OneRaw, 0f), queue); return; }

            // Right-clicking one of our own unfinished sites puts selected workers on the crew
            // (resume an abandoned build / add extra hands); everyone else moves normally.
            int site = FindOwnSiteAt(w, wp);

            int cx = Mathf.RoundToInt(wp.x * 100f), cy = Mathf.RoundToInt(wp.y * 100f);
            bool any = false;
            _lineUnits.Clear();
            foreach (int e in Selected)
            {
                if (w.Kind[e] != EntityKind.Unit || w.Owner[e] != MatchBootstrap.HumanPlayer) continue;
                if (site >= 0 && _match.Defs.Units[w.DefIndex[e]].IsWorker)
                {
                    _match.Enqueue(new Command { Type = CommandType.AssignBuild, A = e, B = site });
                    any = true;
                    continue;
                }
                _lineUnits.Add(e);
            }

            if (_lineUnits.Count == 1)
            {
                _match.Enqueue(new Command { Type = CommandType.Move, A = _lineUnits[0], B = cx, C = cy, D = queue ? 1 : 0 });
                any = true;
            }
            else if (_lineUnits.Count >= 2)
            {
                // SPREAD MOVE: a compact grid of one slot per unit, CENTERED on the click,
                // instead of piling everyone onto the same point to jitter apart. Integer
                // math only — identical clicks yield identical command payloads anywhere.
                int spacingCenti = 40;
                foreach (int e in _lineUnits)
                    spacingCenti = Mathf.Max(spacingCenti, 2 * _match.Defs.Units[w.DefIndex[e]].CollisionRadiusCenti + 40);
                int n = _lineUnits.Count;
                int cols = 1;
                while (cols * cols < n) cols++;
                int rows = (n + cols - 1) / cols;

                // Slots fill north-to-south; hand them out northernmost-first (west-to-east,
                // entity index breaks ties) so units roughly keep their relative positions
                // and don't cross. Pure integer sort keys from the Fix raws.
                _lineUnits.Sort((a, b) =>
                {
                    if (w.Pos[a].Y.Raw != w.Pos[b].Y.Raw) return w.Pos[b].Y.Raw.CompareTo(w.Pos[a].Y.Raw);
                    if (w.Pos[a].X.Raw != w.Pos[b].X.Raw) return w.Pos[a].X.Raw.CompareTo(w.Pos[b].X.Raw);
                    return a.CompareTo(b);
                });

                for (int k = 0; k < n; k++)
                {
                    int row = k / cols, col = k % cols;
                    int rowLen = Mathf.Min(cols, n - row * cols); // short last row stays centered
                    int offX = (2 * col - (rowLen - 1)) * spacingCenti / 2;
                    int offY = ((rows - 1) - 2 * row) * spacingCenti / 2;
                    _match.Enqueue(new Command { Type = CommandType.Move, A = _lineUnits[k], B = cx + offX, C = cy + offY, D = queue ? 1 : 0 });
                    if (k < 24) // destination ghosts, capped so huge armies don't spam
                        _match.View.Ping(new Vector3((cx + offX) / 100f, (cy + offY) / 100f, 0f), GameView.MovePing);
                }
                return;
            }
            if (any) _match.View.Ping(new Vector3(wp.x, wp.y, 0f), GameView.MovePing);
        }

        /// <summary>Attack-move every selected unit toward the point (armed units engage en route).</summary>
        private void IssueAttackMove(SimWorld w, Vector3 wp, bool queue = false)
        {
            int cx = Mathf.RoundToInt(wp.x * 100f), cy = Mathf.RoundToInt(wp.y * 100f);
            bool any = false;
            foreach (int e in Selected)
            {
                if (w.Kind[e] != EntityKind.Unit) continue;
                _match.Enqueue(new Command { Type = CommandType.AttackMove, A = e, B = cx, C = cy, D = queue ? 1 : 0 });
                any = true;
            }
            if (any) _match.View.Ping(new Vector3(wp.x, wp.y, 0f), GameView.AttackPing);
        }

        private int FindEnemyAt(SimWorld w, Vector3 wp)
        {
            int best = -1;
            float bestSq = float.MaxValue;
            var vision = _match.View != null ? _match.View.Vision : null;
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Unit && w.Kind[i] != EntityKind.Building) continue;
                if (!w.AreEnemies(MatchBootstrap.HumanPlayer, w.Owner[i])) continue; // never click-attack an ally
                float dx0 = w.Pos[i].X.Raw / (float)Fix.OneRaw, dy0 = w.Pos[i].Y.Raw / (float)Fix.OneRaw;
                // You can't click-attack what the fog hides.
                if (vision != null && !vision.VisibleAt(dx0, dy0)) continue;
                float r = CollisionRadius(w, i) + 0.4f;
                float dx = dx0 - wp.x;
                float dy = dy0 - wp.y;
                float dsq = dx * dx + dy * dy;
                if (dsq <= r * r && dsq < bestSq) { bestSq = dsq; best = i; }
            }
            return best;
        }

        /// <summary>A friendly building still under construction at the clicked point, or -1.</summary>
        private int FindOwnSiteAt(SimWorld w, Vector3 wp)
        {
            int best = -1;
            float bestSq = float.MaxValue;
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Building || w.Owner[i] != MatchBootstrap.HumanPlayer) continue;
                if (w.ConstructionRemaining[i] <= 0) continue;
                if (!_match.Defs.Buildings[w.DefIndex[i]].Constructible) continue; // hub prongs self-build
                float r = CollisionRadius(w, i) + 0.4f;
                float dx = w.Pos[i].X.Raw / (float)Fix.OneRaw - wp.x;
                float dy = w.Pos[i].Y.Raw / (float)Fix.OneRaw - wp.y;
                float dsq = dx * dx + dy * dy;
                if (dsq <= r * r && dsq < bestSq) { bestSq = dsq; best = i; }
            }
            return best;
        }

        private float CollisionRadius(SimWorld w, int e) =>
            w.Kind[e] == EntityKind.Building
                ? _match.Defs.Buildings[w.DefIndex[e]].CollisionRadiusCenti / 100f
                : _match.Defs.Units[w.DefIndex[e]].CollisionRadiusCenti / 100f;

        /// <summary>
        /// Drawn-line RANK FORMATION: the stroke is the formation's center line — the block
        /// of selected units forms up straddling it, following its curve. Line length sets
        /// the frontage (units per rank); a longer stroke flattens the block down to a
        /// minimum of two ranks, a shorter one deepens it. Melee holds the front ranks,
        /// ranged stands behind, unarmed brings up the rear. Leaders stand ON the line
        /// itself, spread evenly along it, so their auras cover the whole block.
        /// </summary>
        private void IssueFormationPath(SimWorld w)
        {
            // Screen curve → world polyline (drop near-duplicate points).
            _worldPath.Clear();
            for (int i = 0; i < _rightPath.Count; i++)
            {
                Vector3 wp = WorldAt(_rightPath[i]);
                var p = new Vector2(wp.x, wp.y);
                if (_worldPath.Count == 0 || Vector2.Distance(_worldPath[_worldPath.Count - 1], p) > 0.05f) _worldPath.Add(p);
            }
            if (_worldPath.Count < 2) { IssueMoveOrders(w); return; }

            // Cumulative arc-length along the polyline.
            _cum.Clear();
            _cum.Add(0f);
            for (int i = 1; i < _worldPath.Count; i++) _cum.Add(_cum[i - 1] + Vector2.Distance(_worldPath[i - 1], _worldPath[i]));
            float total = _cum[_cum.Count - 1];
            if (total < 0.1f) { IssueMoveOrders(w); return; }

            // Movers: every selected own unit. Leaders split off — they spread along the
            // line itself instead of joining the rank fill.
            _lineUnits.Clear();
            _lineLeaders.Clear();
            foreach (int e in Selected)
            {
                if (w.Kind[e] != EntityKind.Unit || w.Owner[e] != MatchBootstrap.HumanPlayer) continue;
                if (_match.Defs.Units[w.DefIndex[e]].IsLeader) _lineLeaders.Add(e);
                else _lineUnits.Add(e);
            }
            int n = _lineUnits.Count;
            if (n + _lineLeaders.Count == 0) return;

            if (n + _lineLeaders.Count == 1)
            {
                Vector2 solo = SampleAt(total * 0.5f, out _);
                _match.Enqueue(new Command
                {
                    Type = CommandType.Move, A = n == 1 ? _lineUnits[0] : _lineLeaders[0],
                    B = Mathf.RoundToInt(solo.x * 100f), C = Mathf.RoundToInt(solo.y * 100f),
                });
                _match.View.Ping(new Vector3(solo.x, solo.y, 0f), GameView.MovePing);
                return;
            }

            // The formation's FRONT: perpendicular to the drawn line's chord, pointing away
            // from the group — the side the army is advancing toward.
            Vector2 centroid = Vector2.zero;
            foreach (int e in _lineUnits) centroid += UnitPos(w, e);
            foreach (int e in _lineLeaders) centroid += UnitPos(w, e);
            centroid /= n + _lineLeaders.Count;

            // Leaders take the line itself: spread evenly by arc-length (midpoint slots),
            // handed out in arc order so they don't cross, blanketing the block in auras.
            if (_lineLeaders.Count > 0)
            {
                _lineLeaders.Sort((a, b) =>
                {
                    float sa = NearestArcLength(UnitPos(w, a)), sb = NearestArcLength(UnitPos(w, b));
                    return sa != sb ? sa.CompareTo(sb) : a.CompareTo(b);
                });
                int L = _lineLeaders.Count;
                for (int j = 0; j < L; j++)
                {
                    Vector2 pos = SampleAt(total * (2 * j + 1) / (2f * L), out _);
                    _match.Enqueue(new Command
                    {
                        Type = CommandType.Move, A = _lineLeaders[j],
                        B = Mathf.RoundToInt(pos.x * 100f), C = Mathf.RoundToInt(pos.y * 100f),
                    });
                    _match.View.Ping(new Vector3(pos.x, pos.y, 0f), GameView.MovePing);
                }
            }
            if (n == 0) return; // leaders only: the line placement above is the whole order
            Vector2 chord = _worldPath[_worldPath.Count - 1] - _worldPath[0];
            Vector2 refFront = new Vector2(-chord.y, chord.x);
            if (refFront.sqrMagnitude < 1e-6f) refFront = Vector2.up;
            Vector2 mid = SampleAt(total * 0.5f, out _);
            if (Vector2.Dot(refFront, mid - centroid) < 0f) refFront = -refFront;

            // RANK FORMATION centered on the drawn line: the line's length sets how many
            // units stand abreast, so a longer stroke flattens the block down to a minimum
            // of two ranks; a short stroke deepens it. Melee fills the front ranks, ranged
            // stands behind, unarmed units bring up the rear.
            float spacing = 0.4f;
            foreach (int e in _lineUnits)
                spacing = Mathf.Max(spacing, 2f * _match.Defs.Units[w.DefIndex[e]].CollisionRadiusCenti / 100f + 0.4f);
            int perRowCap = Mathf.Max(1, Mathf.FloorToInt(total / spacing) + 1);
            int rows = Mathf.Max(2, Mathf.CeilToInt(n / (float)perRowCap));
            int perRow = Mathf.CeilToInt(n / (float)rows);

            // Fill order decides the ranks: melee first (front), then ranged, then unarmed.
            // Within a class, arc-length order keeps paths from crossing.
            _lineUnits.Sort((a, b) =>
            {
                int ca = FormationRank(w, a), cb = FormationRank(w, b);
                if (ca != cb) return ca.CompareTo(cb);
                float sa = NearestArcLength(UnitPos(w, a)), sb = NearestArcLength(UnitPos(w, b));
                if (sa != sb) return sa.CompareTo(sb);
                return a.CompareTo(b);
            });

            for (int k = 0; k < n; k++)
            {
                int row = k / perRow, idx = k % perRow;
                int rowLen = Mathf.Min(perRow, n - row * perRow);
                float s = total * (2 * idx + 1) / (2f * rowLen); // midpoint sampling centers every rank
                Vector2 slot = SampleAt(s, out Vector2 tan);
                Vector2 perp = new Vector2(-tan.y, tan.x);
                if (Vector2.Dot(perp, refFront) < 0f) perp = -perp;
                perp.Normalize();
                // Ranks straddle the drawn line: front ranks ahead of it, rear ranks behind.
                float frontOff = ((rows - 1) * 0.5f - row) * spacing;
                Vector2 pos = slot + perp * frontOff;
                _match.Enqueue(new Command
                {
                    Type = CommandType.Move, A = _lineUnits[k],
                    B = Mathf.RoundToInt(pos.x * 100f), C = Mathf.RoundToInt(pos.y * 100f),
                });
                if (k < 24) // destination ghosts, capped so huge armies don't spam
                    _match.View.Ping(new Vector3(pos.x, pos.y, 0f), GameView.MovePing);
            }
        }

        /// <summary>Which rank block a unit belongs to in a drawn formation: 0 = melee
        /// (front), 1 = ranged (they fire projectiles), 2 = unarmed (rear).</summary>
        private int FormationRank(SimWorld w, int e)
        {
            var def = _match.Defs.Units[w.DefIndex[e]];
            if (def.AttackDamage <= 0) return 2;
            return def.ProjectileSpeedCenti > 0 ? 1 : 0;
        }

        private Vector2 UnitPos(SimWorld w, int e) =>
            new Vector2(w.Pos[e].X.Raw / (float)Fix.OneRaw, w.Pos[e].Y.Raw / (float)Fix.OneRaw);

        /// <summary>Position (and local tangent) at arc-length s along the world polyline.</summary>
        private Vector2 SampleAt(float s, out Vector2 tangent)
        {
            int last = _worldPath.Count - 1;
            if (s <= 0f) { tangent = (_worldPath[1] - _worldPath[0]).normalized; return _worldPath[0]; }
            if (s >= _cum[last]) { tangent = (_worldPath[last] - _worldPath[last - 1]).normalized; return _worldPath[last]; }
            for (int i = 1; i <= last; i++)
            {
                if (_cum[i] < s) continue;
                float segLen = _cum[i] - _cum[i - 1];
                float t = segLen > 1e-6f ? (s - _cum[i - 1]) / segLen : 0f;
                tangent = (_worldPath[i] - _worldPath[i - 1]).normalized;
                return Vector2.Lerp(_worldPath[i - 1], _worldPath[i], t);
            }
            tangent = (_worldPath[last] - _worldPath[last - 1]).normalized;
            return _worldPath[last];
        }

        /// <summary>Arc-length of the point on the polyline nearest to p (for crossing-free slotting).</summary>
        private float NearestArcLength(Vector2 p)
        {
            float bestD = float.MaxValue, bestS = 0f;
            for (int i = 1; i < _worldPath.Count; i++)
            {
                Vector2 a = _worldPath[i - 1], b = _worldPath[i], ab = b - a;
                float len2 = ab.sqrMagnitude;
                float t = len2 > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2) : 0f;
                Vector2 proj = a + ab * t;
                float d = (p - proj).sqrMagnitude;
                if (d < bestD) { bestD = d; bestS = _cum[i - 1] + t * (_cum[i] - _cum[i - 1]); }
            }
            return bestS;
        }

        private void SelectNearest(SimWorld w)
        {
            Vector3 wp = _cam.ScreenToWorldPoint(new Vector3(Input.mousePosition.x, Input.mousePosition.y, -_cam.transform.position.z));
            const float grace = 0.08f; // a hair of forgiveness at sprite edges
            int bestUnit = -1, bestBuilding = -1, bestNode = -1;
            float bestUnitSq = float.MaxValue, bestBuildingSq = float.MaxValue, bestNodeSq = float.MaxValue;
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
                    continue;
                }
                if (w.Owner[i] != MatchBootstrap.HumanPlayer) continue;
                if (w.Kind[i] == EntityKind.Unit)
                {
                    // Hitbox = the drawn circle (leaders render 25% larger).
                    var ud = _match.Defs.Units[w.DefIndex[i]];
                    float r = ud.CollisionRadiusCenti / 100f * (ud.IsLeader ? 1.25f : 1f) + grace;
                    if (dsq <= r * r && dsq < bestUnitSq) { bestUnitSq = dsq; bestUnit = i; }
                }
                else if (w.Kind[i] == EntityKind.Building)
                {
                    // Hitbox = the drawn SQUARE footprint, construction sites included.
                    float r = _match.Defs.Buildings[w.DefIndex[i]].CollisionRadiusCenti / 100f + grace;
                    if (Mathf.Abs(dx) <= r && Mathf.Abs(dy) <= r && dsq < bestBuildingSq) { bestBuildingSq = dsq; bestBuilding = i; }
                }
            }
            // Hit priority mirrors draw order: units on top, then buildings, then piles.
            // A unit only wins if the click actually landed on its sprite — a worker standing
            // beside a construction site no longer steals the click.
            if (bestUnit >= 0)
            {
                Selected.Add(bestUnit);
                // Double-click on the same unit type → select every one of them you own.
                int def = w.DefIndex[bestUnit];
                if (Time.unscaledTime - _lastClickTime <= DoubleClickSeconds
                    && _lastClickDef == def && _lastClickKind == EntityKind.Unit)
                {
                    for (int i = 0; i < w.HighWater; i++)
                        if (w.Kind[i] == EntityKind.Unit && w.Owner[i] == MatchBootstrap.HumanPlayer && w.DefIndex[i] == def)
                            Selected.Add(i);
                    _lastClickDef = -1; // triple-click doesn't re-trigger
                }
                else { _lastClickDef = def; _lastClickKind = EntityKind.Unit; }
                _lastClickTime = Time.unscaledTime;
            }
            else if (bestBuilding >= 0)
            {
                Selected.Add(bestBuilding);
                // Double-click on the same building type → select every one of them you own.
                int def = w.DefIndex[bestBuilding];
                if (Time.unscaledTime - _lastClickTime <= DoubleClickSeconds
                    && _lastClickDef == def && _lastClickKind == EntityKind.Building)
                {
                    for (int i = 0; i < w.HighWater; i++)
                        if (w.Kind[i] == EntityKind.Building && w.Owner[i] == MatchBootstrap.HumanPlayer && w.DefIndex[i] == def)
                            Selected.Add(i);
                    _lastClickDef = -1;
                }
                else { _lastClickDef = def; _lastClickKind = EntityKind.Building; }
                _lastClickTime = Time.unscaledTime;
            }
            else if (bestNode >= 0) { Selected.Clear(); Selected.Add(bestNode); _lastClickDef = -1; }
            else _lastClickDef = -1;
        }

        private void SelectBox(SimWorld w)
        {
            var min = Vector2.Min(DragStartScreen, Input.mousePosition);
            var max = Vector2.Max(DragStartScreen, Input.mousePosition);
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Unit || w.Owner[i] != MatchBootstrap.HumanPlayer) continue;
                Vector3 sp = _cam.WorldToScreenPoint(new Vector3(w.Pos[i].X.Raw / (float)Fix.OneRaw, w.Pos[i].Y.Raw / (float)Fix.OneRaw, 0f));
                if (sp.x >= min.x && sp.x <= max.x && sp.y >= min.y && sp.y <= max.y) Selected.Add(i);
            }
        }

        private void PruneDead(SimWorld w)
        {
            if (Selected.Count == 0) return;
            _scratch.Clear();
            foreach (int e in Selected)
            {
                if (e < 0 || e >= w.HighWater) continue;
                if (w.Kind[e] == EntityKind.Node) _scratch.Add(e); // neutral pile, keep while it lasts
                else if (w.Owner[e] == MatchBootstrap.HumanPlayer
                    && (w.Kind[e] == EntityKind.Unit || w.Kind[e] == EntityKind.Building))
                    _scratch.Add(e);
            }
            if (_scratch.Count != Selected.Count)
            {
                Selected.Clear();
                foreach (int e in _scratch) Selected.Add(e);
            }
        }


        private readonly List<int> _scratch = new List<int>();
    }
}
