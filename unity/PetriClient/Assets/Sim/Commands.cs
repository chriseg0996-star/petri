using System;
using System.Collections.Generic;

namespace Petri.Core
{
    public enum CommandType : byte
    {
        // Move/AttackMove/Stop REJECT on squad members (Leader >= 0): a squad acts as one
        // body driven by its leader — players command formations, not individual soldiers.
        // Move/AttackMove/FormationMove: D = 1 queues the order behind the active one
        // (shift-click); D = 0 replaces everything, wiping the queue.
        Move = 1,                // A = entity, B = xCenti, C = yCenti, D = queue flag
        Stop = 2,                // A = entity
        SetProductionWeight = 3, // A = unit dense index, B = weight (>= 0)
        AssignToLeader = 4,      // A = unit, B = leader entity
        FormationMove = 5,       // A = leader, B = xCenti, C = yCenti, D = queue flag
        SetProduceOverride = 6,  // A = building, B = unit dense index to produce exclusively, -1 = auto
        SetRally = 7,            // A = building, B = xCenti, C = yCenti, D = 1 set / 0 clear
        ConstructBuilding = 8,   // A = worker, B = xCenti, C = yCenti, D = building dense index
        SetProducePaused = 9,    // A = building, B = 1 pause / 0 resume
        AttackMove = 10,         // A = unit, B = xCenti, C = yCenti (advance, engaging enemies)
        SetLimbStation = 11,     // A = linked leader, B = forwardCenti, C = sideCenti (prime frame)
        SetAutoAssimilate = 12,  // A = building, B = 1 join nearest swarm / 0 stay loose
        SetMoveAsOne = 13,       // A = leader, B = 1 pace swarm to slowest unit / 0 own speeds
        SetUnitZone = 14,        // A = leader, B = unit def index, C = zone (SimConstants.Zone*)
        SetStance = 15,          // A = leader, B = 1 Encircle (wrap nearest enemy) / 0 formation
        SetFacing = 16,          // A = unit, B/C = direction vector (centi); leaders hold it as
                                 //   the formation front until the next move order
        SetSupplyPriority = 17,  // A = 0..100: core-focused workers vs caravans to the front
        SetDial = 18,            // A = entity, B = 0..100: per-entity tuning dial (future modifiers)
        AssignBuild = 19,        // A = worker, B = construction site: (re)join the build crew
        UpgradeCache = 20,       // A = supply cache: buy the next tier (2x stock/HP, 1/2 drain, armed)
        BuyUpgrade = 21,         // A = upgrade dense index: purchase a tech-path upgrade
        BuildProng = 22,         // A = headquarters entity, B = hub-built building dense index
    }

    /// <summary>
    /// The ONLY way anything (UI, replays, network peers, scripts) mutates the sim.
    /// Coordinates ride as centi-unit ints so commands serialize as plain integers.
    /// </summary>
    public struct Command
    {
        public int Tick;
        public byte Player;
        public CommandType Type;
        public int A;
        public int B;
        public int C;
        public int D;
        public int E; // optional facing x (centi) for FormationMove line drags
        public int F; // optional facing y (centi)
    }

    /// <summary>
    /// Append-only, tick-ordered command list. In multiplayer every peer must hold the
    /// identical log; a replay is just a saved log re-fed to a fresh simulation.
    /// </summary>
    public sealed class CommandLog
    {
        private readonly List<Command> _commands = new List<Command>();

        public int Count => _commands.Count;
        public Command this[int index] => _commands[index];

        public void Add(Command c)
        {
            if (_commands.Count > 0 && c.Tick < _commands[_commands.Count - 1].Tick)
                throw new InvalidOperationException("commands must be appended in tick order");
            _commands.Add(c);
        }
    }

    /// <summary>
    /// Validates and applies commands. Invalid commands reject (counter bumps, nothing else
    /// changes) — they never throw and never partially apply, because a malicious or stale
    /// network peer must not be able to corrupt or desync the sim.
    /// </summary>
    public static class CommandSystem
    {
        public static void Apply(SimWorld w, DefDatabase defs, Command c)
        {
            if (c.Player >= w.Players.Length || !w.Players[c.Player].Alive) { Reject(w); return; }

            switch (c.Type)
            {
                case CommandType.Move:
                {
                    if (!IsOwnedUnit(w, c.A, c.Player) || w.Leader[c.A] >= 0) { Reject(w); return; }
                    var target = w.ClampToMap(new FixVec2(Fix.Ratio(c.B, 100), Fix.Ratio(c.C, 100)));
                    if (TryQueue(w, c, SimConstants.OrderMove, target)) return;
                    w.QueueCount[c.A] = 0; // a direct order replaces the whole plan
                    ApplyMove(w, c.A, target);
                    return;
                }
                case CommandType.AttackMove:
                {
                    if (!IsOwnedUnit(w, c.A, c.Player) || w.Leader[c.A] >= 0) { Reject(w); return; }
                    var target = w.ClampToMap(new FixVec2(Fix.Ratio(c.B, 100), Fix.Ratio(c.C, 100)));
                    if (TryQueue(w, c, SimConstants.OrderAttackMove, target)) return;
                    w.QueueCount[c.A] = 0;
                    ApplyAttackMove(w, defs, c.A, target);
                    return;
                }
                case CommandType.Stop:
                {
                    if (!IsOwnedUnit(w, c.A, c.Player) || w.Leader[c.A] >= 0) { Reject(w); return; }
                    w.HasMoveOrder[c.A] = false;
                    w.AttackMove[c.A] = false;
                    w.QueueCount[c.A] = 0; // stop scraps the queued plan too
                    w.SeekingSwarm[c.A] = false; // the player took manual control
                    w.FacingHeld[c.A] = false;
                    w.BuildTask[c.A] = -1;
                    w.MoveTarget[c.A] = w.Pos[c.A];
                    return;
                }
                case CommandType.SetProductionWeight:
                {
                    if (c.A < 0 || c.A >= defs.Units.Length || c.B < 0) { Reject(w); return; }
                    w.Players[c.Player].ProductionWeights[c.A] = c.B;
                    return;
                }
                case CommandType.AssignToLeader:
                {
                    if (!IsOwnedUnit(w, c.A, c.Player)) { Reject(w); return; }
                    var unitDef = defs.Units[w.DefIndex[c.A]];
                    if (unitDef.IsWorker) { Reject(w); return; }

                    // B == -1: a leader unlinks its squad from the larger swarm.
                    if (c.B == -1)
                    {
                        if (!unitDef.IsLeader || w.Leader[c.A] < 0) { Reject(w); return; }
                        w.Leader[c.A] = -1;
                        w.HasLimbStation[c.A] = false;
                        w.Settled[c.A] = false;
                        w.AttackMove[c.A] = false;
                        return;
                    }

                    if (!IsOwnedUnit(w, c.B, c.Player) || c.A == c.B) { Reject(w); return; }
                    if (!defs.Units[w.DefIndex[c.B]].IsLeader) { Reject(w); return; }

                    if (unitDef.IsLeader)
                    {
                        // Leader→leader link: the squad becomes a limb of B's swarm (a tree).
                        // Links don't consume member capacity, but cycles must be impossible.
                        int cur = c.B, guard = 0;
                        while (cur >= 0 && guard++ <= w.HighWater)
                        {
                            if (cur == c.A) { Reject(w); return; }
                            cur = w.Leader[cur];
                        }
                        w.Leader[c.A] = c.B;
                        w.Leaderless[c.A] = false;
                        w.HasLimbStation[c.A] = false; // fresh links start at default stations
                        w.Settled[c.A] = false;
                        w.AttackMove[c.A] = false; // posture now flows from the prime
                        w.QueueCount[c.A] = 0;     // the prime's plan rules now
                        return;
                    }

                    // Member join: capacity counts only non-leader members.
                    int squad = 0;
                    for (int i = 0; i < w.HighWater; i++)
                        if (w.Kind[i] == EntityKind.Unit && w.Leader[i] == c.B && !defs.Units[w.DefIndex[i]].IsLeader) squad++;
                    if (squad >= w.Rules.MaxUnitsPerLeader) { Reject(w); return; }
                    w.Leader[c.A] = c.B;
                    // Leaderless is NOT cleared here — a penalized survivor recovers only when
                    // it physically reaches its new leader (SwarmSystem pass 3b).
                    w.SeekingSwarm[c.A] = false;
                    w.Settled[c.A] = false;
                    w.AttackMove[c.A] = false; // the leader drives the squad from here
                    w.QueueCount[c.A] = 0;
                    return;
                }
                case CommandType.SetProduceOverride:
                {
                    if (!IsOwnedBuilding(w, c.A, c.Player)) { Reject(w); return; }
                    if (c.B != -1)
                    {
                        var bdef = defs.Buildings[w.DefIndex[c.A]];
                        bool producible = false;
                        for (int i = 0; i < bdef.ProducesDense.Length; i++)
                            if (bdef.ProducesDense[i] == c.B) { producible = true; break; }
                        if (!producible) { Reject(w); return; }
                    }
                    w.ProduceOverride[c.A] = (short)c.B;
                    return;
                }
                case CommandType.SetLimbStation:
                {
                    // A freeform station for a linked squad: where this limb rides relative to
                    // its prime (forward/side in the prime's facing frame). This is how a drawn
                    // formation line gives a super-swarm a persistent shape.
                    if (!IsOwnedUnit(w, c.A, c.Player) || !defs.Units[w.DefIndex[c.A]].IsLeader) { Reject(w); return; }
                    if (w.Leader[c.A] < 0) { Reject(w); return; } // only linked limbs have stations
                    w.LimbStation[c.A] = new FixVec2(Fix.Ratio(c.B, 100), Fix.Ratio(c.C, 100));
                    w.HasLimbStation[c.A] = true;
                    w.Settled[c.A] = false; // wake the limb so it walks to its new station
                    return;
                }
                case CommandType.SetMoveAsOne:
                {
                    if (!IsOwnedUnit(w, c.A, c.Player) || !defs.Units[w.DefIndex[c.A]].IsLeader) { Reject(w); return; }
                    w.MoveAsOne[c.A] = c.B != 0;
                    return;
                }
                case CommandType.SetAutoAssimilate:
                {
                    if (!IsOwnedBuilding(w, c.A, c.Player)) { Reject(w); return; }
                    w.AutoAssimilate[c.A] = c.B != 0;
                    return;
                }
                case CommandType.SetProducePaused:
                {
                    if (!IsOwnedBuilding(w, c.A, c.Player)) { Reject(w); return; }
                    w.ProducePaused[c.A] = c.B != 0;
                    return;
                }
                case CommandType.SetRally:
                {
                    if (!IsOwnedBuilding(w, c.A, c.Player)) { Reject(w); return; }
                    if (c.D == 0)
                    {
                        w.HasRally[c.A] = false;
                        w.RallyPoint[c.A] = w.Pos[c.A];
                    }
                    else
                    {
                        w.HasRally[c.A] = true;
                        w.RallyPoint[c.A] = w.ClampToMap(new FixVec2(Fix.Ratio(c.B, 100), Fix.Ratio(c.C, 100)));
                    }
                    return;
                }
                case CommandType.ConstructBuilding:
                {
                    if (!IsOwnedUnit(w, c.A, c.Player) || !defs.Units[w.DefIndex[c.A]].IsWorker) { Reject(w); return; }
                    if (c.D < 0 || c.D >= defs.Buildings.Length || !defs.Buildings[c.D].Constructible) { Reject(w); return; }
                    var bdef = defs.Buildings[c.D];
                    var player = w.Players[c.Player];
                    // Must afford every resource the def asks for — nutrients, minerals, and
                    // evolutionary points (which can only have come from kills).
                    if (player.Food < bdef.FoodCost || player.Minerals < bdef.MineralCost
                        || player.EvoPoints < bdef.EvoCost) { Reject(w); return; }

                    var pos = w.ClampToMap(new FixVec2(Fix.Ratio(c.B, 100), Fix.Ratio(c.C, 100)));
                    // The footprint must not overlap any building or resource node (units get
                    // pushed aside by collision once the immobile site exists).
                    if (!FootprintClear(w, defs, pos, Fix.Ratio(bdef.CollisionRadiusCenti, 100), Fix.Ratio(10, 100))) { Reject(w); return; }

                    int site = w.Spawn(EntityKind.Building, (short)c.D, c.Player, pos, bdef.MaxHp);
                    if (site < 0) { Reject(w); return; } // world full
                    player.Food -= bdef.FoodCost;
                    player.Minerals -= bdef.MineralCost;
                    player.EvoPoints -= bdef.EvoCost;
                    // Construction is tracked in WORK UNITS = 3 × build ticks: N builders
                    // contribute N+2 units per tick (the AoE2 curve — one builder finishes in
                    // exactly BuildTimeTicks, extras help with diminishing returns).
                    w.ConstructionRemaining[site] = bdef.BuildTimeTicks * 3;
                    w.BuildTask[c.A] = site;
                    w.HasMoveOrder[c.A] = false; // WorkerSystem walks the builder over
                    w.WorkNode[c.A] = -1;
                    w.GatherTimer[c.A] = 0;
                    w.QueueCount[c.A] = 0;
                    return;
                }
                case CommandType.AssignBuild:
                {
                    // Put a worker (back) on an existing construction site — resume an
                    // abandoned build or add extra hands to the crew. Hub-built prongs
                    // self-construct off the headquarters and refuse worker crews.
                    if (!IsOwnedUnit(w, c.A, c.Player) || !defs.Units[w.DefIndex[c.A]].IsWorker) { Reject(w); return; }
                    if (!IsOwnedBuilding(w, c.B, c.Player) || w.ConstructionRemaining[c.B] <= 0) { Reject(w); return; }
                    if (!defs.Buildings[w.DefIndex[c.B]].Constructible) { Reject(w); return; }
                    w.BuildTask[c.A] = c.B;
                    w.HasMoveOrder[c.A] = false;
                    w.WorkNode[c.A] = -1;
                    w.GatherTimer[c.A] = 0;
                    w.CaravanCache[c.A] = -1;
                    w.QueueCount[c.A] = 0;
                    return;
                }
                case CommandType.FormationMove:
                {
                    if (!IsOwnedUnit(w, c.A, c.Player) || !defs.Units[w.DefIndex[c.A]].IsLeader) { Reject(w); return; }
                    var target = w.ClampToMap(new FixVec2(Fix.Ratio(c.B, 100), Fix.Ratio(c.C, 100)));
                    if (TryQueue(w, c, SimConstants.OrderFormationMove, target)) return;
                    w.QueueCount[c.A] = 0;
                    ApplyFormationMove(w, c.A, target, new FixVec2(Fix.Ratio(c.E, 100), Fix.Ratio(c.F, 100)));
                    return;
                }
                case CommandType.SetUnitZone:
                {
                    // The placement matrix: which zone of this squad's formation the given unit
                    // type stands in (front/rear/flanks/spread/guard).
                    if (!IsOwnedUnit(w, c.A, c.Player) || !defs.Units[w.DefIndex[c.A]].IsLeader) { Reject(w); return; }
                    if (c.B < 0 || c.B >= defs.Units.Length) { Reject(w); return; }
                    if (c.C < 0 || c.C >= SimConstants.ZoneCount) { Reject(w); return; }
                    w.ZoneMatrix[c.A * w.UnitDefCount + c.B] = (byte)c.C;
                    WakeSquad(w, c.A); // re-form NOW, not on the next march (rested members
                                       // ignore small slot shifts; Spread also reshuffles others)
                    return;
                }
                case CommandType.SetStance:
                {
                    if (!IsOwnedUnit(w, c.A, c.Player) || !defs.Units[w.DefIndex[c.A]].IsLeader) { Reject(w); return; }
                    w.Stance[c.A] = c.B != 0;
                    WakeSquad(w, c.A); // stance flips reposition the whole squad immediately
                    return;
                }
                case CommandType.SetFacing:
                {
                    // Face a direction on demand (Shift+R-drag). Squad members are excluded —
                    // their body facing is driven by movement and combat.
                    if (!IsOwnedUnit(w, c.A, c.Player) || w.Leader[c.A] >= 0) { Reject(w); return; }
                    var dir = new FixVec2(Fix.Ratio(c.B, 100), Fix.Ratio(c.C, 100));
                    if (dir.LengthSq.Raw == 0) { Reject(w); return; }
                    Fix dLen = dir.Length;
                    w.Facing[c.A] = new FixVec2(dir.X / dLen, dir.Y / dLen);
                    if (defs.Units[w.DefIndex[c.A]].IsLeader)
                    {
                        w.FacingHeld[c.A] = true; // the ordered front survives future marches
                        WakeSquad(w, c.A);        // and the squad rotates onto it right now
                    }
                    return;
                }
                case CommandType.SetSupplyPriority:
                {
                    // The logistics dial: what share of the worker pool may run caravans.
                    if (c.A < 0 || c.A > 100) { Reject(w); return; }
                    w.Players[c.Player].SupplyPriority = c.A;
                    return;
                }
                case CommandType.UpgradeCache:
                {
                    // Tier up a finished supply cache. Each tier doubles stock capacity and
                    // max HP, halves per-unit drain, and (from tier 1) arms the cache with a
                    // ranged shot. Cost doubles per tier already bought.
                    if (!IsOwnedBuilding(w, c.A, c.Player) || w.ConstructionRemaining[c.A] > 0) { Reject(w); return; }
                    var cdef = defs.Buildings[w.DefIndex[c.A]];
                    if (!cdef.ProvidesSupply || cdef.IsHeadquarters) { Reject(w); return; }
                    if (w.Tier[c.A] >= w.Rules.CacheMaxTier) { Reject(w); return; }
                    int cost = w.Rules.CacheUpgradeFoodCost << w.Tier[c.A];
                    var cplayer = w.Players[c.Player];
                    if (cplayer.Food < cost) { Reject(w); return; }
                    cplayer.Food -= cost;
                    w.Tier[c.A]++;
                    // Doubling max HP grants the new half on the spot (the shell thickens).
                    w.Hp[c.A] += cdef.MaxHp << (w.Tier[c.A] - 1);
                    return;
                }
                case CommandType.BuildProng:
                {
                    // The headquarters grows a tech-path prong: auto-placed adjacent, self-
                    // building (no worker). One of each path per player.
                    if (!IsOwnedBuilding(w, c.A, c.Player) || w.ConstructionRemaining[c.A] > 0) { Reject(w); return; }
                    if (!defs.Buildings[w.DefIndex[c.A]].IsHeadquarters) { Reject(w); return; }
                    if (c.B < 0 || c.B >= defs.Buildings.Length || !defs.Buildings[c.B].HubBuilt) { Reject(w); return; }
                    var pdef = defs.Buildings[c.B];
                    var pplayer = w.Players[c.Player];
                    // Evolution prongs are paid in minerals (plus any nutrient cost).
                    if (pplayer.Food < pdef.FoodCost || pplayer.Minerals < pdef.MineralCost) { Reject(w); return; }
                    // Already own (or growing) this prong → reject.
                    for (int i = 0; i < w.HighWater; i++)
                        if (w.Kind[i] == EntityKind.Building && w.Owner[i] == c.Player && w.DefIndex[i] == c.B) { Reject(w); return; }
                    if (!FindHubSlot(w, defs, c.A, pdef, out var ppos)) { Reject(w); return; } // no room around the hub
                    int prong = w.Spawn(EntityKind.Building, (short)c.B, c.Player, ppos, pdef.MaxHp);
                    if (prong < 0) { Reject(w); return; } // world full
                    pplayer.Food -= pdef.FoodCost;
                    pplayer.Minerals -= pdef.MineralCost;
                    w.ConstructionRemaining[prong] = pdef.BuildTimeTicks * 3; // self-builds via HubBuilt pass
                    return;
                }
                case CommandType.BuyUpgrade:
                {
                    // Purchase a tech-path upgrade once: it must exist, be unbought, its path
                    // building must stand operational, and the player must afford it.
                    if (c.A < 0 || c.A >= defs.Upgrades.Length) { Reject(w); return; }
                    var player = w.Players[c.Player];
                    if (player.UpgradeLevels[c.A] != 0) { Reject(w); return; } // already owned
                    var up = defs.Upgrades[c.A];
                    if (player.Food < up.FoodCost) { Reject(w); return; }
                    if (!HasOperationalBuilding(w, up.RequiresBuildingDense, c.Player)) { Reject(w); return; }
                    player.Food -= up.FoodCost;
                    player.UpgradeLevels[c.A] = 1;
                    return;
                }
                case CommandType.SetDial:
                {
                    // Generic per-entity dial (0..100). No gameplay effect bound yet — it is
                    // hashed state reserved for coming per-entity modifiers, settable on any
                    // of the player's own units or buildings.
                    if (!IsOwnedUnit(w, c.A, c.Player) && !IsOwnedBuilding(w, c.A, c.Player)) { Reject(w); return; }
                    if (c.B < 0 || c.B > 100) { Reject(w); return; }
                    w.Dial[c.A] = (byte)c.B;
                    return;
                }
                default:
                    Reject(w);
                    return;
            }
        }

        /// <summary>Shift-queue path: if the command carries the queue flag (D=1) and the unit
        /// is already busy (active order or a non-empty queue), append it and report handled.
        /// A queued order on an idle unit falls through and starts immediately.</summary>
        private static bool TryQueue(SimWorld w, Command c, byte kind, FixVec2 target)
        {
            if (c.D == 0) return false;
            if (!w.HasMoveOrder[c.A] && !w.AttackMove[c.A] && w.QueueCount[c.A] == 0) return false;
            if (w.QueueCount[c.A] >= SimConstants.MaxOrderQueue) { Reject(w); return true; }
            int slot = c.A * SimConstants.MaxOrderQueue + w.QueueCount[c.A];
            w.QueueKind[slot] = kind;
            w.QueuePos[slot] = target;
            w.QueueCount[c.A]++;
            return true;
        }

        /// <summary>Pop and start the next queued order. Systems call this when the active
        /// order completes; a no-op for empty queues and squad-bound units.</summary>
        internal static void AdvanceQueue(SimWorld w, DefDatabase defs, int e)
        {
            if (w.QueueCount[e] == 0 || w.Leader[e] >= 0) return;
            int qb = e * SimConstants.MaxOrderQueue;
            byte kind = w.QueueKind[qb];
            var target = w.QueuePos[qb];
            int left = --w.QueueCount[e];
            for (int q = 0; q < left; q++)
            {
                w.QueueKind[qb + q] = w.QueueKind[qb + q + 1];
                w.QueuePos[qb + q] = w.QueuePos[qb + q + 1];
            }
            switch (kind)
            {
                case SimConstants.OrderAttackMove: ApplyAttackMove(w, defs, e, target); break;
                case SimConstants.OrderFormationMove: ApplyFormationMove(w, e, target, FixVec2.Zero); break;
                default: ApplyMove(w, e, target); break;
            }
        }

        private static void ApplyMove(SimWorld w, int e, FixVec2 target)
        {
            w.MoveTarget[e] = target;
            w.HasMoveOrder[e] = true;
            w.AttackMove[e] = false;
            w.SeekingSwarm[e] = false; // the player took manual control
            w.WorkNode[e] = -1;   // player order overrides gather assignment
            w.GatherTimer[e] = 0;
            w.BuildTask[e] = -1;  // pulled off the construction crew
        }

        private static void ApplyAttackMove(SimWorld w, DefDatabase defs, int e, FixVec2 target)
        {
            w.MoveTarget[e] = target;
            w.SeekingSwarm[e] = false; // the player took manual control
            w.WorkNode[e] = -1;
            w.GatherTimer[e] = 0;
            w.BuildTask[e] = -1;
            var amDef = defs.Units[w.DefIndex[e]];
            if (amDef.IsLeader)
            {
                // Leaders march the spine straight to the point (no personal chasing)
                // while carrying ATTACK POSTURE: their squad — and every linked limb —
                // releases to fight on contact. Armed or not: the squad does the fighting.
                w.HasMoveOrder[e] = true;
                w.AttackMove[e] = true;
                w.FacingHeld[e] = false;
                var delta = target - w.Pos[e];
                if (delta.LengthSq > Fix.Zero)
                {
                    Fix len = delta.Length;
                    w.Facing[e] = new FixVec2(delta.X / len, delta.Y / len);
                }
            }
            else if (amDef.AttackDamage <= 0)
            {
                // Unarmed (workers): nothing to attack with — just move there.
                w.HasMoveOrder[e] = true;
                w.AttackMove[e] = false;
            }
            else
            {
                // Armed loose unit: CombatSystem owns the movement (advance, divert to engage).
                w.HasMoveOrder[e] = false;
                w.AttackMove[e] = true;
            }
        }

        private static void ApplyFormationMove(SimWorld w, int e, FixVec2 target, FixVec2 facing)
        {
            // Explicit facing wins — a click-drag line sets a shared front for all
            // leaders; otherwise face the direction of travel.
            bool explicitFacing = facing.LengthSq > Fix.Zero;
            if (!explicitFacing) facing = target - w.Pos[e];
            if (facing.LengthSq > Fix.Zero)
            {
                Fix len = facing.Length;
                w.Facing[e] = new FixVec2(facing.X / len, facing.Y / len);
            }
            w.MoveTarget[e] = target;
            w.HasMoveOrder[e] = true;
            w.AttackMove[e] = false;
            // Explicit facing pins the front for the whole march; otherwise the leader
            // is free to turn toward its travel direction.
            w.FacingHeld[e] = explicitFacing;
        }

        /// <summary>A building footprint at pos clears every building and resource node by gap.</summary>
        private static bool FootprintClear(SimWorld w, DefDatabase defs, FixVec2 pos, Fix newR, Fix gap)
        {
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Building && w.Kind[i] != EntityKind.Node) continue;
                Fix otherR = w.Kind[i] == EntityKind.Building
                    ? Fix.Ratio(defs.Buildings[w.DefIndex[i]].CollisionRadiusCenti, 100)
                    : Fix.Ratio(w.Rules.NodeRadiusCenti, 100);
                Fix minD = newR + otherR + gap;
                if ((w.Pos[i] - pos).LengthSq < minD * minD) return false;
            }
            return true;
        }

        // The four cardinal directions (N, E, S, W) prongs fan out along.
        private static readonly FixVec2[] Cardinals =
        {
            new FixVec2(Fix.Zero, Fix.FromInt(1)),   // N
            new FixVec2(Fix.FromInt(1), Fix.Zero),   // E
            new FixVec2(Fix.Zero, Fix.FromInt(-1)),  // S
            new FixVec2(Fix.FromInt(-1), Fix.Zero),  // W
        };

        /// <summary>A clear slot for a prong on one of the four cardinal directions around the
        /// headquarters — each new prong takes the first free cardinal, so they space out to
        /// N/E/S/W. Widens the ring only if a cardinal is blocked by terrain. Deterministic.</summary>
        private static bool FindHubSlot(SimWorld w, DefDatabase defs, int hq, BuildingDef pdef, out FixVec2 pos)
        {
            Fix hqR = Fix.Ratio(defs.Buildings[w.DefIndex[hq]].CollisionRadiusCenti, 100);
            Fix newR = Fix.Ratio(pdef.CollisionRadiusCenti, 100);
            Fix gap = Fix.Ratio(40, 100);
            for (int ring = 0; ring < 3; ring++)
            {
                Fix dist = hqR + newR + Fix.Ratio(40 + ring * 200, 100);
                for (int d = 0; d < Cardinals.Length; d++)
                {
                    var p = w.ClampToMap(w.Pos[hq] + Cardinals[d] * dist);
                    if (FootprintClear(w, defs, p, newR, gap)) { pos = p; return true; }
                }
            }
            pos = default(FixVec2);
            return false;
        }

        /// <summary>True if the player owns at least one finished building of the given def.</summary>
        private static bool HasOperationalBuilding(SimWorld w, int buildingDense, byte player)
        {
            if (buildingDense < 0) return false;
            for (int i = 0; i < w.HighWater; i++)
                if (w.Kind[i] == EntityKind.Building && w.Owner[i] == player
                    && w.DefIndex[i] == buildingDense && w.ConstructionRemaining[i] == 0)
                    return true;
            return false;
        }

        private static bool IsOwnedUnit(SimWorld w, int e, byte player) =>
            e >= 0 && e < w.Capacity && w.Kind[e] == EntityKind.Unit && w.Owner[e] == player;

        private static bool IsOwnedBuilding(SimWorld w, int e, byte player) =>
            e >= 0 && e < w.Capacity && w.Kind[e] == EntityKind.Building && w.Owner[e] == player;

        /// <summary>Wake every rested member of a leader's squad so a layout change (zone or
        /// stance) takes effect on the spot instead of on the next march.</summary>
        private static void WakeSquad(SimWorld w, int lead)
        {
            for (int i = 0; i < w.HighWater; i++)
                if (w.Kind[i] == EntityKind.Unit && w.Leader[i] == lead)
                    w.Settled[i] = false;
        }

        private static void Reject(SimWorld w) => w.RejectedCommands++;
    }
}
