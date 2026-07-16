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
        private readonly List<int> _lineUnits = new List<int>();
        private readonly List<Vector2> _slotPos = new List<Vector2>();
        private readonly List<Vector2> _slotFront = new List<Vector2>();
        private readonly List<Vector2> _rightPath = new List<Vector2>();  // screen-space drawn curve
        private readonly List<Vector2> _worldPath = new List<Vector2>();  // world-space, built on release
        private readonly List<float> _cum = new List<float>();            // cumulative arc-length

        /// <summary>The screen-space curve being drawn with a right-drag (for the HUD preview).</summary>
        public IReadOnlyList<Vector2> RightPathScreen => _rightPath;

        // Control groups 1..9 store (slot, generation) so recall survives entity-index reuse:
        // a dead unit's slot taken by a new one won't be wrongly re-selected.
        private readonly List<(int idx, int gen)>[] _groups = new List<(int, int)>[10];
        // Squad groups: auto-assigned to each squad of a swarm link. Recall selects that squad
        // ALONE (leader + members, no whole-swarm expansion). Manual Ctrl+N overrides the slot.
        private readonly (int idx, int gen)[] _squadGroup = new (int, int)[10];
        private readonly List<int> _tmpLeaders = new List<int>();
        private readonly List<int> _tmpCount = new List<int>();

        public void Bind(MatchBootstrap match, Camera cam)
        {
            _match = match;
            _cam = cam;
            for (int n = 1; n <= 9; n++) { _groups[n] = new List<(int, int)>(); _squadGroup[n] = (-1, 0); }
        }

        public bool GroupPopulated(int n) => n >= 1 && n <= 9 && (_groups[n].Count > 0 || SquadGroupValid(n));

        private bool SquadGroupValid(int n)
        {
            var (idx, gen) = _squadGroup[n];
            if (idx < 0) return false;
            var w = _match.Sim.World;
            return idx < w.HighWater && w.Generation[idx] == gen && w.Kind[idx] == EntityKind.Unit
                && w.Owner[idx] == MatchBootstrap.HumanPlayer && _match.Defs.Units[w.DefIndex[idx]].IsLeader;
        }

        /// <summary>The control group auto-assigned to this squad's leader, or 0.</summary>
        public int GroupOf(int leader)
        {
            for (int n = 1; n <= 9; n++)
                if (SquadGroupValid(n) && _squadGroup[n].idx == leader) return n;
            return 0;
        }

        /// <summary>Give every listed squad leader a control group, using free slots 1..9 in
        /// order. Called by the swarm-link grid, so a link's squads are numbered the moment
        /// the link is first shown. Existing assignments (and manual groups) are untouched.</summary>
        public void EnsureSquadGroups(List<int> leaders)
        {
            var w = _match.Sim.World;
            for (int k = 0; k < leaders.Count; k++)
            {
                int lead = leaders[k];
                if (GroupOf(lead) > 0) continue;
                for (int n = 1; n <= 9; n++)
                {
                    if (_groups[n].Count > 0 || SquadGroupValid(n)) continue;
                    _squadGroup[n] = (lead, w.Generation[lead]);
                    break;
                }
            }
        }

        /// <summary>Save the current selection (own units/buildings) as control group N.
        /// Manual assignment overrides any auto squad group in that slot.</summary>
        public void AssignControlGroup(int n)
        {
            if (n < 1 || n > 9) return;
            var w = _match.Sim.World;
            _squadGroup[n] = (-1, 0);
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

            // Auto squad group: recall selects that squad alone (no whole-swarm expansion),
            // exactly like drilling in via the hierarchy grid.
            if (SquadGroupValid(n))
            {
                int lead = _squadGroup[n].idx;
                if (!add) { SelectSquadOnly(lead); return; }
                Selected.Add(lead);
                for (int i = 0; i < w.HighWater; i++)
                    if (w.Kind[i] == EntityKind.Unit && w.Leader[i] == lead && !_match.Defs.Units[w.DefIndex[i]].IsLeader)
                        Selected.Add(i);
                return;
            }

            if (!add) Selected.Clear();
            AttackArmed = false;
            PlacingBuilding = -1;
            foreach (var m in _groups[n])
                if (m.idx < w.HighWater && w.Generation[m.idx] == m.gen
                    && w.Owner[m.idx] == MatchBootstrap.HumanPlayer
                    && (w.Kind[m.idx] == EntityKind.Unit || w.Kind[m.idx] == EntityKind.Building))
                    Selected.Add(m.idx);
            ExpandSelectionToSquads(w); // pick up units that reinforced the squad since assignment
        }

        /// <summary>
        /// Squads (and linked super-swarms) are atomic: if the selection touches any member,
        /// limb, or leader, the entire body joins the selection — up the tree to the prime and
        /// back down through every linked squad. Iterates to a fixpoint (the tree is shallow).
        /// </summary>
        private void ExpandSelectionToSquads(SimWorld w)
        {
            if (Selected.Count == 0) return;
            int before;
            do
            {
                before = Selected.Count;
                _scratch.Clear();
                foreach (int e in Selected)
                    if (w.Kind[e] == EntityKind.Unit && w.Leader[e] >= 0)
                        _scratch.Add(w.Leader[e]);           // up: member/limb → its leader/prime
                foreach (int lead in _scratch) Selected.Add(lead);
                for (int i = 0; i < w.HighWater; i++)        // down: leader → members and limbs
                    if (w.Kind[i] == EntityKind.Unit && w.Leader[i] >= 0 && Selected.Contains(w.Leader[i]))
                        Selected.Add(i);
            } while (Selected.Count != before);
        }

        /// <summary>
        /// Assimilate the selected combat units into swarms: assign each to a leader, balancing
        /// squad sizes and respecting the per-leader cap. Uses the selected leaders if any, else
        /// the player's existing leaders. The sim revalidates every assignment.
        /// </summary>
        public void AssimilateSelected()
        {
            var w = _match.Sim.World;
            _tmpLeaders.Clear();
            foreach (int e in Selected)
                if (w.Kind[e] == EntityKind.Unit && _match.Defs.Units[w.DefIndex[e]].IsLeader) _tmpLeaders.Add(e);
            if (_tmpLeaders.Count == 0)
                for (int i = 0; i < w.HighWater; i++)
                    if (w.Kind[i] == EntityKind.Unit && w.Owner[i] == MatchBootstrap.HumanPlayer && _match.Defs.Units[w.DefIndex[i]].IsLeader)
                        _tmpLeaders.Add(i);
            if (_tmpLeaders.Count == 0) return; // no leaders to assimilate onto

            // Current squad sizes for each candidate leader (members only; limbs don't count).
            _tmpCount.Clear();
            for (int li = 0; li < _tmpLeaders.Count; li++)
            {
                int c = 0;
                for (int i = 0; i < w.HighWater; i++)
                    if (w.Kind[i] == EntityKind.Unit && w.Leader[i] == _tmpLeaders[li]
                        && !_match.Defs.Units[w.DefIndex[i]].IsLeader) c++;
                _tmpCount.Add(c);
            }

            int cap = w.Rules.MaxUnitsPerLeader;
            for (int i = 0; i < w.HighWater; i++) // index order for a deterministic fill
            {
                if (!Selected.Contains(i)) continue;
                if (w.Kind[i] != EntityKind.Unit || w.Owner[i] != MatchBootstrap.HumanPlayer) continue;
                var def = _match.Defs.Units[w.DefIndex[i]];
                if (def.IsWorker || def.IsLeader || w.Leader[i] >= 0) continue; // already in a squad, or ineligible

                int best = -1;
                for (int li = 0; li < _tmpLeaders.Count; li++)
                    if (_tmpCount[li] < cap && (best < 0 || _tmpCount[li] < _tmpCount[best])) best = li;
                if (best < 0) break; // every squad full

                _match.Enqueue(new Command { Type = CommandType.AssignToLeader, A = i, B = _tmpLeaders[best] });
                _tmpCount[best]++;
            }
        }

        private void Update()
        {
            if (_match == null || _match.Sim == null) return;
            var w = _match.Sim.World;
            PruneDead(w);
            HandleHotkeys();

            bool overHud = _match.Hud != null && _match.Hud.IsPointerOver(Input.mousePosition);

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
                ExpandSelectionToSquads(w); // touching any squad member selects the whole squad
            }

            // ---- Shift+Right drag: aim a facing — on release everything selected turns to it.
            if (Input.GetMouseButtonDown(1) && Selected.Count > 0 && !overHud
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
            else if (Input.GetMouseButtonDown(1) && Selected.Count > 0 && !overHud)
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

        /// <summary>Turn every selected leader and loose unit to face along the dragged arrow
        /// (world-space direction). Squad members are excluded — their leader's front rules.
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
                if (w.Kind[e] != EntityKind.Unit || w.Leader[e] >= 0) continue;
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
                if (Input.GetKeyDown(KeyCode.T)) ToggleAutoAssimilate(primary);
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
            if (Input.GetKeyDown(KeyCode.G)) AssimilateSelected();                       // group into swarms
            if (Input.GetKeyDown(KeyCode.L)) LinkSelected();                             // link squads into a super-swarm
            if (Input.GetKeyDown(KeyCode.U)) UnlinkSelected();                           // break links
            if (Input.GetKeyDown(KeyCode.E)) ToggleEncircle();                           // encircle stance
            if (Input.GetKeyDown(KeyCode.O)) ToggleMoveAsOne();                          // arrive-as-one pacing
            if (Input.GetKeyDown(KeyCode.S)) StopSelected();
        }

        /// <summary>Set a unit type's formation zone on every selected leader (the placement
        /// matrix: front/rear/flanks/spread/guard).</summary>
        public void SetUnitZone(int unitDefIx, int zone)
        {
            var w = _match.Sim.World;
            foreach (int e in Selected)
                if (w.Kind[e] == EntityKind.Unit && _match.Defs.Units[w.DefIndex[e]].IsLeader)
                    _match.Enqueue(new Command { Type = CommandType.SetUnitZone, A = e, B = unitDefIx, C = zone });
        }

        /// <summary>Flip Encircle stance on every selected leader (following the primary):
        /// the squad wraps the nearest enemy instead of holding its formation shape.</summary>
        public void ToggleEncircle()
        {
            var w = _match.Sim.World;
            int primary = PrimarySelected();
            if (primary < 0 || w.Kind[primary] != EntityKind.Unit || !_match.Defs.Units[w.DefIndex[primary]].IsLeader) return;
            bool on = !w.Stance[primary];
            foreach (int e in Selected)
                if (w.Kind[e] == EntityKind.Unit && _match.Defs.Units[w.DefIndex[e]].IsLeader)
                    _match.Enqueue(new Command { Type = CommandType.SetStance, A = e, B = on ? 1 : 0 });
        }

        /// <summary>Flip arrive-as-one pacing on every selected leader (following the primary).</summary>
        public void ToggleMoveAsOne()
        {
            var w = _match.Sim.World;
            int primary = PrimarySelected();
            if (primary < 0 || w.Kind[primary] != EntityKind.Unit || !_match.Defs.Units[w.DefIndex[primary]].IsLeader) return;
            bool on = !w.MoveAsOne[primary];
            foreach (int e in Selected)
                if (w.Kind[e] == EntityKind.Unit && _match.Defs.Units[w.DefIndex[e]].IsLeader)
                    _match.Enqueue(new Command { Type = CommandType.SetMoveAsOne, A = e, B = on ? 1 : 0 });
        }

        /// <summary>Select exactly one squad — its leader plus direct members — WITHOUT the
        /// usual whole-swarm expansion. This is how the swarm-link hierarchy grid drills into
        /// an individual squad of a linked swarm.</summary>
        public void SelectSquadOnly(int leader)
        {
            var w = _match.Sim.World;
            if (leader < 0 || leader >= w.HighWater || w.Kind[leader] != EntityKind.Unit) return;
            if (w.Owner[leader] != MatchBootstrap.HumanPlayer || !_match.Defs.Units[w.DefIndex[leader]].IsLeader) return;
            Selected.Clear();
            AttackArmed = false;
            PlacingBuilding = -1;
            Selected.Add(leader);
            for (int i = 0; i < w.HighWater; i++)
                if (w.Kind[i] == EntityKind.Unit && w.Leader[i] == leader && !_match.Defs.Units[w.DefIndex[i]].IsLeader)
                    Selected.Add(i);
        }

        /// <summary>Select the whole swarm (full link tree) that contains the given unit.</summary>
        public void SelectSwarm(int anyMember)
        {
            var w = _match.Sim.World;
            if (anyMember < 0 || anyMember >= w.HighWater || w.Kind[anyMember] != EntityKind.Unit) return;
            Selected.Clear();
            AttackArmed = false;
            Selected.Add(anyMember);
            ExpandSelectionToSquads(w);
        }

        /// <summary>Select every military (non-worker) unit you own — soldiers, spitters, and
        /// swarm leaders — across the whole map.</summary>
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

        /// <summary>Toggle whether selected buildings' new combat units join the nearest swarm
        /// automatically or stay loose (following the primary's current state).</summary>
        public void ToggleAutoAssimilate(int primary)
        {
            var w = _match.Sim.World;
            if (primary < 0 || w.Kind[primary] != EntityKind.Building) return;
            bool on = !w.AutoAssimilate[primary];
            foreach (int e in Selected)
                if (w.Kind[e] == EntityKind.Building)
                    _match.Enqueue(new Command { Type = CommandType.SetAutoAssimilate, A = e, B = on ? 1 : 0 });
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

        /// <summary>The stable "primary" the HUD card and hotkey context follow: a selected
        /// PRIME leader if any (a super-swarm presents as its spine), else any leader, else
        /// lowest index.</summary>
        public int PrimarySelected()
        {
            var w = _match.Sim.World;
            int primary = -1, primaryLeader = -1, primaryPrime = -1;
            foreach (int e in Selected)
            {
                if (primary < 0 || e < primary) primary = e;
                if (w.Kind[e] != EntityKind.Unit || !_match.Defs.Units[w.DefIndex[e]].IsLeader) continue;
                if (primaryLeader < 0 || e < primaryLeader) primaryLeader = e;
                if (w.Leader[e] < 0 && (primaryPrime < 0 || e < primaryPrime)) primaryPrime = e;
            }
            if (primaryPrime >= 0) return primaryPrime;
            return primaryLeader >= 0 ? primaryLeader : primary;
        }

        /// <summary>Link every other selected leader under the primary (prime) leader — their
        /// squads become limbs of one super-swarm. The sim refuses cycles.</summary>
        public void LinkSelected()
        {
            var w = _match.Sim.World;
            int prime = PrimarySelected();
            if (prime < 0 || w.Kind[prime] != EntityKind.Unit || !_match.Defs.Units[w.DefIndex[prime]].IsLeader) return;
            foreach (int e in Selected)
            {
                if (e == prime || w.Kind[e] != EntityKind.Unit) continue;
                if (!_match.Defs.Units[w.DefIndex[e]].IsLeader) continue;
                if (w.Leader[e] == prime) continue; // already a limb of this prime
                _match.Enqueue(new Command { Type = CommandType.AssignToLeader, A = e, B = prime });
            }
        }

        /// <summary>Break every selected leader out of its super-swarm (squads stay intact).</summary>
        public void UnlinkSelected()
        {
            var w = _match.Sim.World;
            foreach (int e in Selected)
            {
                if (w.Kind[e] != EntityKind.Unit || !_match.Defs.Units[w.DefIndex[e]].IsLeader) continue;
                if (w.Leader[e] < 0) continue;
                _match.Enqueue(new Command { Type = CommandType.AssignToLeader, A = e, B = -1 });
            }
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

        /// <summary>Stop every selected unit (halts move orders; gatherers resume on their own).
        /// Squad members halt with their leader — stopping it anchors the whole squad.</summary>
        public void StopSelected()
        {
            var w = _match.Sim.World;
            foreach (int e in Selected)
            {
                if (w.Kind[e] != EntityKind.Unit || w.Leader[e] >= 0) continue;
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
            foreach (int e in Selected)
            {
                if (w.Kind[e] != EntityKind.Unit) continue;
                if (site >= 0 && w.Leader[e] < 0 && _match.Defs.Units[w.DefIndex[e]].IsWorker)
                {
                    _match.Enqueue(new Command { Type = CommandType.AssignBuild, A = e, B = site });
                    any = true;
                    continue;
                }
                if (w.Leader[e] >= 0)
                {
                    // A limb squad commanded WITHOUT its prime (drilled in via the hierarchy
                    // grid): the click repositions its persistent station in the swarm. When
                    // the prime is also selected it drives, and limbs/members are skipped.
                    if (_match.Defs.Units[w.DefIndex[e]].IsLeader && !AncestorSelected(w, e))
                    {
                        IssueLimbStation(w, e, wp);
                        any = true;
                    }
                    continue;
                }
                if (_match.Defs.Units[w.DefIndex[e]].IsLeader)
                    _match.Enqueue(new Command { Type = CommandType.FormationMove, A = e, B = cx, C = cy, D = queue ? 1 : 0 });
                else
                    _match.Enqueue(new Command { Type = CommandType.Move, A = e, B = cx, C = cy, D = queue ? 1 : 0 });
                any = true;
            }
            if (any) _match.View.Ping(new Vector3(wp.x, wp.y, 0f), GameView.MovePing);
        }

        private bool AncestorSelected(SimWorld w, int e)
        {
            int cur = w.Leader[e], guard = 0;
            while (cur >= 0 && guard++ < 64)
            {
                if (Selected.Contains(cur)) return true;
                cur = w.Leader[cur];
            }
            return false;
        }

        /// <summary>Set a limb's station so it stands at the clicked point — expressed relative
        /// to its prime's anchor and facing, the same frame the swarm system steers by.</summary>
        private void IssueLimbStation(SimWorld w, int limb, Vector3 wp)
        {
            int prime = w.Leader[limb];
            bool primeMoving = w.HasMoveOrder[prime] || w.AttackMove[prime];
            var anchorFix = primeMoving ? w.MoveTarget[prime] : w.Pos[prime];
            var anchor = new Vector2(anchorFix.X.Raw / (float)Fix.OneRaw, anchorFix.Y.Raw / (float)Fix.OneRaw);
            var f = new Vector2(w.Facing[prime].X.Raw / (float)Fix.OneRaw, w.Facing[prime].Y.Raw / (float)Fix.OneRaw);
            if (f.sqrMagnitude < 1e-4f) f = Vector2.right;
            Vector2 off = new Vector2(wp.x, wp.y) - anchor;
            float fwd = Vector2.Dot(off, f);
            float side = Vector2.Dot(off, new Vector2(-f.y, f.x));
            _match.Enqueue(new Command
            {
                Type = CommandType.SetLimbStation, A = limb,
                B = Mathf.RoundToInt(fwd * 100f), C = Mathf.RoundToInt(side * 100f),
            });
            _match.View.Ping(new Vector3(wp.x, wp.y, 0f), GameView.RallyPing);
        }

        /// <summary>Attack-move every selected unit toward the point (armed units engage en route).</summary>
        private void IssueAttackMove(SimWorld w, Vector3 wp, bool queue = false)
        {
            int cx = Mathf.RoundToInt(wp.x * 100f), cy = Mathf.RoundToInt(wp.y * 100f);
            bool any = false;
            foreach (int e in Selected)
            {
                if (w.Kind[e] != EntityKind.Unit) continue;
                if (w.Leader[e] >= 0) continue; // the leader carries the squad's attack order
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
        /// Beyond-All-Reason-style formation curve: the selected leaders (or, if none, units) are
        /// spread evenly by arc-length along the ACTUAL path drawn with the mouse — following its
        /// curve, not a straight chord — each facing perpendicular to the local curve direction,
        /// on a coherent front (away from the group). Leaders form up via FormationMove with an
        /// explicit facing; loose units get plain moves to their slot.
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

            // Collect movers: ALL selected leaders — primes anchor to the curve, linked limbs
            // get freeform stations so the drawn shape sticks to the super-swarm and travels
            // with the spine. Fall back to loose units when no leaders are selected. Squad
            // members are never individual movers — they hold formation on their leader.
            _lineUnits.Clear();
            foreach (int e in Selected)
                if (w.Kind[e] == EntityKind.Unit && _match.Defs.Units[w.DefIndex[e]].IsLeader) _lineUnits.Add(e);
            bool leaders = _lineUnits.Count > 0;
            if (!leaders)
                foreach (int e in Selected)
                    if (w.Kind[e] == EntityKind.Unit && w.Leader[e] < 0) _lineUnits.Add(e);
            if (_lineUnits.Count == 0) return;

            Vector2 centroid = Vector2.zero;
            foreach (int e in _lineUnits) centroid += UnitPos(w, e);
            centroid /= _lineUnits.Count;

            // A single reference front (perpendicular to the overall chord, away from the group)
            // keeps the whole line facing coherently even as it curves.
            Vector2 chord = _worldPath[_worldPath.Count - 1] - _worldPath[0];
            Vector2 refFront = new Vector2(-chord.y, chord.x);
            if (refFront.sqrMagnitude < 1e-6f) refFront = Vector2.up;
            Vector2 mid = SampleAt(total * 0.5f, out _);
            if (Vector2.Dot(refFront, mid - centroid) < 0f) refFront = -refFront;

            // Assign movers to slots in arc-length order of their current position → no crossing.
            _lineUnits.Sort((p, q) => NearestArcLength(UnitPos(w, p)).CompareTo(NearestArcLength(UnitPos(w, q))));

            // First pass: compute every mover's slot and local front along the curve.
            int n = _lineUnits.Count;
            _slotPos.Clear();
            _slotFront.Clear();
            for (int k = 0; k < n; k++)
            {
                float s = n == 1 ? total * 0.5f : total * (k / (float)(n - 1));
                Vector2 slot = SampleAt(s, out Vector2 tan);
                Vector2 perp = new Vector2(-tan.y, tan.x);
                if (Vector2.Dot(perp, refFront) < 0f) perp = -perp; // align to the coherent front
                _slotPos.Add(slot);
                _slotFront.Add(perp.normalized);
            }

            // Second pass: primes/loose leaders anchor to their slots; linked limbs instead get
            // a FREEFORM STATION — their slot expressed relative to their prime's slot in the
            // prime's new facing frame — so the drawn shape persists and travels with the spine.
            for (int k = 0; k < n; k++)
            {
                int e = _lineUnits[k];
                Vector2 slot = _slotPos[k];
                int cx = Mathf.RoundToInt(slot.x * 100f), cy = Mathf.RoundToInt(slot.y * 100f);

                if (!leaders)
                {
                    _match.Enqueue(new Command { Type = CommandType.Move, A = e, B = cx, C = cy });
                    _match.View.Ping(new Vector3(slot.x, slot.y, 0f), GameView.MovePing);
                    continue;
                }

                int primeIx = w.Leader[e] >= 0 ? _lineUnits.IndexOf(w.Leader[e]) : -1;
                if (primeIx >= 0)
                {
                    Vector2 f = _slotFront[primeIx];
                    Vector2 off = slot - _slotPos[primeIx];
                    float fwd = Vector2.Dot(off, f);
                    float side = Vector2.Dot(off, new Vector2(-f.y, f.x));
                    _match.Enqueue(new Command
                    {
                        Type = CommandType.SetLimbStation, A = e,
                        B = Mathf.RoundToInt(fwd * 100f), C = Mathf.RoundToInt(side * 100f),
                    });
                }
                else
                {
                    _match.Enqueue(new Command
                    {
                        Type = CommandType.FormationMove, A = e, B = cx, C = cy,
                        E = Mathf.RoundToInt(_slotFront[k].x * 100f), F = Mathf.RoundToInt(_slotFront[k].y * 100f),
                    });
                }
                _match.View.Ping(new Vector3(slot.x, slot.y, 0f), GameView.MovePing);
            }
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
                if (Time.unscaledTime - _lastClickTime <= DoubleClickSeconds && _lastClickDef == def)
                {
                    for (int i = 0; i < w.HighWater; i++)
                        if (w.Kind[i] == EntityKind.Unit && w.Owner[i] == MatchBootstrap.HumanPlayer && w.DefIndex[i] == def)
                            Selected.Add(i);
                    _lastClickDef = -1; // triple-click doesn't re-trigger
                }
                else _lastClickDef = def;
                _lastClickTime = Time.unscaledTime;
            }
            else if (bestBuilding >= 0) { Selected.Add(bestBuilding); _lastClickDef = -1; }
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
