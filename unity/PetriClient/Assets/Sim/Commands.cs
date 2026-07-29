using System;
using System.Collections.Generic;

namespace Petri.Core
{
    public enum CommandType : byte
    {
        // Superorganism-era command set. RESERVED ids — retired commands from earlier
        // designs (unit orders, swarm links, supply, tech): 1 Move, 2 Stop,
        // 4 AssignToLeader, 5 FormationMove, 7 SetRally, 10 AttackMove, 11 SetLimbStation,
        // 12 SetAutoAssimilate, 13 SetMoveAsOne, 14 SetUnitZone, 15 SetStance,
        // 16 SetFacing, 17 SetSupplyPriority, 18 SetDial, 19 AssignBuild, 20 UpgradeCache,
        // 21 BuyUpgrade, 22 BuildProng, 23 SetSiblingOrdinal. Never reuse these numbers:
        // old logs/peers may still emit them, and they must land in the default Reject.
        SetProductionWeight = 3, // A = unit dense index, B = weight (>= 0)
        SetProduceOverride = 6,  // A = building, B = unit dense index to produce exclusively, -1 = auto
        ConstructBuilding = 8,   // A = -1 (unused), B = xCenti, C = yCenti, D = building dense.
                                 //   Placeable only on the player's own territory; self-builds.
        SetProducePaused = 9,    // A = building, B = 1 pause / 0 resume
        // ---- Superorganism commands.
        SetFrontCount = 24,      // A = new K (must be one of SimConstants.FrontCounts);
                                 //   force redistributes evenly, damage/broken/pushes reset
        RallyProduction = 25,    // A = producing building, B = front index or -1 = auto
        PushFront = 26,          // A = front index (< K), B = xCenti, C = yCenti target
        StopFront = 27,          // A = front index (< K): cancel its push
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
        public int E; // reserved — keeps the wire shape stable
        public int F; // reserved
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
                case CommandType.SetProductionWeight:
                {
                    if (c.A < 0 || c.A >= defs.Units.Length || c.B < 0) { Reject(w); return; }
                    w.Players[c.Player].ProductionWeights[c.A] = c.B;
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
                case CommandType.SetProducePaused:
                {
                    if (!IsOwnedBuilding(w, c.A, c.Player)) { Reject(w); return; }
                    w.ProducePaused[c.A] = c.B != 0;
                    return;
                }
                case CommandType.ConstructBuilding:
                {
                    // Place a building INSIDE the organism: every footprint cell must be
                    // owned and unblocked; the site self-builds (no workers involved).
                    if (c.D < 0 || c.D >= defs.Buildings.Length || !defs.Buildings[c.D].Constructible) { Reject(w); return; }
                    var bdef = defs.Buildings[c.D];
                    var player = w.Players[c.Player];
                    if (player.Food < bdef.FoodCost || player.Minerals < bdef.MineralCost
                        || player.EvoPoints < bdef.EvoCost) { Reject(w); return; }

                    var pos = w.ClampToMap(new FixVec2(Fix.Ratio(c.B, 100), Fix.Ratio(c.C, 100)));
                    int xCenti = (int)((long)pos.X.Raw * 100 / Fix.OneRaw);
                    int yCenti = (int)((long)pos.Y.Raw * 100 / Fix.OneRaw);
                    if (!FootprintOnOwnTerritory(w, c.Player, xCenti, yCenti, bdef.CollisionRadiusCenti)) { Reject(w); return; }
                    if (!FootprintClear(w, defs, pos, Fix.Ratio(bdef.CollisionRadiusCenti, 100), Fix.Ratio(10, 100))) { Reject(w); return; }

                    int site = w.Spawn(EntityKind.Building, (short)c.D, c.Player, pos, bdef.MaxHp);
                    if (site < 0) { Reject(w); return; } // world full
                    player.Food -= bdef.FoodCost;
                    player.Minerals -= bdef.MineralCost;
                    player.EvoPoints -= bdef.EvoCost;
                    // Work units = 3 × build ticks; ConstructionSystem advances HubBuildRate
                    // (3) per tick, so a site finishes in exactly BuildTimeTicks.
                    w.ConstructionRemaining[site] = bdef.BuildTimeTicks * 3;
                    return;
                }
                case CommandType.SetFrontCount:
                {
                    // Change the border partition. Per unit def, the total assigned force is
                    // redistributed evenly across the new K (remainder on the low fronts);
                    // damage pools, breakthrough windows, and pushes reset — the line reforms.
                    var pl = w.Players[c.Player];
                    if (FrontMath.KIndex(c.A) < 0 || c.A == pl.FrontCount) { Reject(w); return; }
                    int u = w.UnitDefCount;
                    for (int d = 0; d < u; d++)
                    {
                        int total = 0;
                        for (int f = 0; f < SimConstants.MaxFronts; f++)
                        {
                            total += pl.Force[f * u + d];
                            pl.Force[f * u + d] = 0;
                        }
                        int share = total / c.A, rem = total % c.A;
                        for (int f = 0; f < c.A; f++)
                            pl.Force[f * u + d] = share + (f < rem ? 1 : 0);
                    }
                    for (int f = 0; f < SimConstants.MaxFronts; f++)
                    {
                        pl.FrontDamage[f] = 0;
                        pl.FrontBrokenTicks[f] = 0;
                        pl.FrontPushX[f] = -1;
                        pl.FrontPushY[f] = -1;
                    }
                    pl.FrontCount = (byte)c.A;
                    return;
                }
                case CommandType.RallyProduction:
                {
                    if (!IsOwnedBuilding(w, c.A, c.Player)) { Reject(w); return; }
                    if (defs.Buildings[w.DefIndex[c.A]].ProducesDense.Length == 0) { Reject(w); return; }
                    if (c.B < -1 || c.B >= SimConstants.MaxFronts) { Reject(w); return; }
                    w.RallyFront[c.A] = (short)c.B;
                    return;
                }
                case CommandType.PushFront:
                {
                    var pl = w.Players[c.Player];
                    if (c.A < 0 || c.A >= pl.FrontCount) { Reject(w); return; }
                    var target = w.ClampToMap(new FixVec2(Fix.Ratio(c.B, 100), Fix.Ratio(c.C, 100)));
                    pl.FrontPushX[c.A] = (int)((long)target.X.Raw * 100 / Fix.OneRaw);
                    pl.FrontPushY[c.A] = (int)((long)target.Y.Raw * 100 / Fix.OneRaw);
                    return;
                }
                case CommandType.StopFront:
                {
                    var pl = w.Players[c.Player];
                    if (c.A < 0 || c.A >= pl.FrontCount) { Reject(w); return; }
                    pl.FrontPushX[c.A] = -1;
                    pl.FrontPushY[c.A] = -1;
                    return;
                }
                default:
                    Reject(w);
                    return;
            }
        }

        /// <summary>Every territory cell whose center the footprint covers (plus the cell
        /// under the center itself) must belong to the player and be unblocked.</summary>
        private static bool FootprintOnOwnTerritory(SimWorld w, byte player, int xCenti, int yCenti, int rCenti)
        {
            int center = w.CellOfCenti(xCenti, yCenti);
            if (w.TerritoryBlocked[center] || w.Territory[center] != player) return false;
            int c0x = (xCenti - rCenti) / SimWorld.CellCenti, c1x = (xCenti + rCenti) / SimWorld.CellCenti;
            int c0y = (yCenti - rCenti) / SimWorld.CellCenti, c1y = (yCenti + rCenti) / SimWorld.CellCenti;
            long rSq = (long)rCenti * rCenti;
            for (int cy = c0y; cy <= c1y; cy++)
            {
                if (cy < 0 || cy >= w.TerritoryCellsY) return false; // footprint off-map
                for (int cx = c0x; cx <= c1x; cx++)
                {
                    if (cx < 0 || cx >= w.TerritoryCellsX) return false;
                    int c = cy * w.TerritoryCellsX + cx;
                    w.CellCenterCenti(c, out int ccx, out int ccy);
                    long dx = ccx - xCenti, dy = ccy - yCenti;
                    if (dx * dx + dy * dy > rSq) continue; // cell center outside the footprint
                    if (w.TerritoryBlocked[c] || w.Territory[c] != player) return false;
                }
            }
            return true;
        }

        /// <summary>A building footprint at pos clears every building, resource node, and
        /// terrain wall by gap.</summary>
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
            for (int k = 0; k < w.WallPos.Length; k++)
            {
                Fix minD = newR + w.WallRadius[k] + gap;
                if ((w.WallPos[k] - pos).LengthSq < minD * minD) return false;
            }
            return true;
        }

        private static bool IsOwnedBuilding(SimWorld w, int e, byte player) =>
            e >= 0 && e < w.Capacity && w.Kind[e] == EntityKind.Building && w.Owner[e] == player;

        private static void Reject(SimWorld w) => w.RejectedCommands++;
    }
}
