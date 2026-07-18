using System;

namespace Petri.Core
{
    /// <summary>
    /// Automated production: buildings continuously produce whichever producible unit is
    /// furthest below its weight share (players set composition, not build orders). Newly
    /// completed units walk to the rally point, or stand by the building without one.
    /// Deterministic: dense-index scans, integer cross-multiplied comparisons, one RNG draw
    /// per completed unit for the spawn direction.
    /// </summary>
    public static class ProductionSystem
    {
        public static void Tick(SimWorld w, DefDatabase defs)
        {
            int u = defs.Units.Length;
            int[] counts = w.ScratchUnitCounts;
            Array.Clear(counts, 0, counts.Length);
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] == EntityKind.Unit) counts[w.Owner[i] * u + w.DefIndex[i]]++;
                else if (w.Kind[i] == EntityKind.Building && w.ProduceChoice[i] >= 0) counts[w.Owner[i] * u + w.ProduceChoice[i]]++;
            }

            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Building) continue;
                if (w.ConstructionRemaining[i] > 0) continue; // site not finished yet
                if (w.ProducePaused[i]) continue;             // player halted this building
                var bdef = defs.Buildings[w.DefIndex[i]];
                if (bdef.ProducesDense.Length == 0) continue;
                var player = w.Players[w.Owner[i]];
                if (!player.Alive) continue;

                if (w.ProduceChoice[i] < 0)
                {
                    int best = -1;
                    if (w.ProduceOverride[i] >= 0)
                    {
                        // Player pinned this building to one unit: produce exactly that whenever
                        // affordable, bypassing composition weights entirely.
                        if (player.Food >= defs.Units[w.ProduceOverride[i]].FoodCost)
                            best = w.ProduceOverride[i];
                    }
                    else
                    {
                        // Auto: pick the affordable candidate maximizing weight/(count+1); compare
                        // with cross-multiplication so there is no division floor at all.
                        foreach (int cand in bdef.ProducesDense)
                        {
                            int weight = player.ProductionWeights[cand];
                            if (weight <= 0 || player.Food < defs.Units[cand].FoodCost) continue;
                            if (best < 0 ||
                                (long)weight * (counts[w.Owner[i] * u + best] + 1) >
                                (long)player.ProductionWeights[best] * (counts[w.Owner[i] * u + cand] + 1))
                                best = cand;
                        }
                    }
                    if (best < 0) continue;
                    player.Food -= defs.Units[best].FoodCost;
                    w.ProduceChoice[i] = (short)best;
                    w.ProduceProgress[i] = 0;
                    counts[w.Owner[i] * u + best]++;
                }
                else
                {
                    var udef = defs.Units[w.ProduceChoice[i]];
                    if (++w.ProduceProgress[i] < udef.BuildTimeTicks) continue;
                    Fix dist = Fix.Ratio(bdef.CollisionRadiusCenti + udef.CollisionRadiusCenti + 20, 100);
                    FixVec2 pos = w.ClampToMap(w.Pos[i] + SimWorld.RingDir(w.Rng.NextInt(8)) * dist);
                    int e = w.Spawn(EntityKind.Unit, w.ProduceChoice[i], w.Owner[i], pos, udef.MaxHp);
                    if (e >= 0 && w.HasRally[i])
                    {
                        if (udef.IsWorker)
                        {
                            // Rallied onto a resource pile → the worker mines that pile
                            // (WorkerSystem walks it over). Rallied elsewhere → walk there,
                            // then it gathers the nearest pile on its own.
                            int node = WorkerSystem.NodeAtPoint(w, w.RallyPoint[i]);
                            if (node >= 0) w.WorkNode[e] = node;
                            else { w.MoveTarget[e] = w.RallyPoint[i]; w.HasMoveOrder[e] = true; }
                        }
                        else
                        {
                            w.MoveTarget[e] = w.RallyPoint[i];
                            w.HasMoveOrder[e] = true;
                        }
                    }
                    w.ProduceChoice[i] = -1;
                    w.ProduceProgress[i] = 0;
                }
            }
        }
    }

    /// <summary>
    /// The leader's command aura: friendly units standing within Rules.LeaderAuraRadiusCenti
    /// of a live, same-owner leader deal Rules.LeaderAuraBonus damage. Derived per-tick
    /// scratch (like ScratchAttackBonus): recomputed from hashed positions after movement,
    /// never hashed itself, order-independent (a pure within-radius predicate).
    /// </summary>
    public static class LeaderAuraSystem
    {
        public static void Tick(SimWorld w, DefDatabase defs)
        {
            Array.Clear(w.ScratchLeaderAura, 0, w.HighWater);
            int n = 0;
            int[] leaders = w.ScratchQueue; // reused scratch; SupplySystem is done with it
            for (int i = 0; i < w.HighWater; i++) // ascending index
                if (w.Kind[i] == EntityKind.Unit && w.Hp[i] > 0 && defs.Units[w.DefIndex[i]].IsLeader)
                    leaders[n++] = i;
            if (n == 0) return;
            Fix r = Fix.Ratio(w.Rules.LeaderAuraRadiusCenti, 100);
            Fix rSq = r * r;
            for (int u = 0; u < w.HighWater; u++)
            {
                if (w.Kind[u] != EntityKind.Unit) continue;
                for (int k = 0; k < n; k++)
                {
                    int L = leaders[k];
                    if (L == u || w.Owner[L] != w.Owner[u]) continue; // same owner; never self
                    if ((w.Pos[L] - w.Pos[u]).LengthSq <= rSq) { w.ScratchLeaderAura[u] = true; break; }
                }
            }
        }
    }

    /// <summary>
    /// Worker automation: idle workers pick the nearest node (lowest index wins ties),
    /// gather, and haul to their headquarters. A player move order suspends gathering.
    /// </summary>
    public static class WorkerSystem
    {
        public static void Tick(SimWorld w, DefDatabase defs)
        {
            for (int i = 0; i < w.ScratchBuilders.Length; i++) w.ScratchBuilders[i] = 0;

            // Candidate lists, built ONCE per tick in ascending index order so every scan below
            // keeps its lowest-index tie-break while looking at tens of entries instead of the
            // whole entity range. Membership conditions that can change mid-tick (a node running
            // dry, a depot filling) are still re-checked live at each use.
            w.ScratchNodeCount = w.ScratchDropoffCount = w.ScratchWorkerCount = w.ScratchCacheCount = 0;
            for (int i = 0; i < w.HighWater; i++)
            {
                switch (w.Kind[i])
                {
                    case EntityKind.Node:
                        w.ScratchNodes[w.ScratchNodeCount++] = i;
                        break;
                    case EntityKind.Unit:
                        if (defs.Units[w.DefIndex[i]].IsWorker) w.ScratchWorkers[w.ScratchWorkerCount++] = i;
                        break;
                    case EntityKind.Building:
                        if (w.ConstructionRemaining[i] > 0) break;
                        var bd = defs.Buildings[w.DefIndex[i]];
                        // Drop-offs: HQs, hub prongs, and dedicated expansion drop-offs.
                        // Forward drop-offs also become caravan LOAD points (the treasury
                        // is global, so loading anywhere is equivalent) — intentional.
                        if (bd.IsHeadquarters || bd.HubBuilt || bd.IsDropoff) w.ScratchDropoffs[w.ScratchDropoffCount++] = i;
                        if (bd.ProvidesSupply && !bd.IsHeadquarters) w.ScratchCaches[w.ScratchCacheCount++] = i;
                        break;
                }
            }

            Fix slack = Fix.Ratio(20, 100); // arrival tolerance beyond touching edges
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Unit) continue;
                var def = defs.Units[w.DefIndex[i]];
                if (!def.IsWorker || w.HasMoveOrder[i]) continue;

                Fix step = MovementSystem.PerTickStep(def.MoveSpeedCenti);
                Fix selfR = Fix.Ratio(def.CollisionRadiusCenti, 100);

                // Construction duty outranks gathering: walk to the assigned site and build.
                int task = w.BuildTask[i];
                if (task >= 0)
                {
                    if (w.Kind[task] != EntityKind.Building || w.ConstructionRemaining[task] <= 0
                        || w.Owner[task] != w.Owner[i])
                    {
                        w.BuildTask[i] = -1; // site finished or destroyed; resume gathering
                    }
                    else
                    {
                        Fix reach = selfR + Fix.Ratio(defs.Buildings[w.DefIndex[task]].CollisionRadiusCenti, 100) + slack;
                        if ((w.Pos[task] - w.Pos[i]).LengthSq <= reach * reach)
                        {
                            w.ScratchBuilders[task]++; // progress applied in the crew pass below
                        }
                        else
                        {
                            MovementSystem.FaceToward(w, defs, i, w.Pos[task] - w.Pos[i]);
                            w.Pos[i] = FixVec2.MoveTowards(w.Pos[i], w.Pos[task], step, out _);
                        }
                        continue;
                    }
                }

                // CARAVAN duty: keep connected forward caches stocked. A worker claims a needy
                // cache, loads food from the treasury AT the HQ, and physically hauls it out —
                // a raidable supply run (kill the carrier, lose the food). Automatic, like
                // gathering; ranks between construction and mining.
                if (w.CaravanCache[i] >= 0 && !CaravanTargetValid(w, defs, i, w.CaravanCache[i]))
                    w.CaravanCache[i] = -1;
                if (w.CaravanCache[i] < 0 && w.Carry[i] == 0)
                    w.CaravanCache[i] = ClaimCaravan(w, defs, i);
                if (w.CaravanCache[i] >= 0)
                {
                    int cache = w.CaravanCache[i];
                    if (w.Carry[i] == 0)
                    {
                        // Load at the nearest drop-off (food leaves the treasury at the core
                        // or any of its prongs).
                        int loadHq = FindNearestDropoff(w, defs, w.Owner[i], w.Pos[i]);
                        if (loadHq < 0) { w.CaravanCache[i] = -1; continue; }
                        Fix hqReach = selfR + Fix.Ratio(defs.Buildings[w.DefIndex[loadHq]].CollisionRadiusCenti, 100) + slack;
                        if ((w.Pos[loadHq] - w.Pos[i]).LengthSq <= hqReach * hqReach)
                        {
                            var pl = w.Players[w.Owner[i]];
                            int deficit = SupplySystem.StockCapOf(w, defs, cache) - w.DepotStock[cache];
                            int load = System.Math.Min(def.CarryCapacity, System.Math.Min((int)System.Math.Min(pl.Food, int.MaxValue), deficit));
                            if (load <= 0) { w.CaravanCache[i] = -1; continue; }
                            pl.Food -= load;
                            w.Carry[i] = load;
                            w.CarryMineral[i] = false; // caravans haul nutrients from the treasury
                        }
                        else
                        {
                            MovementSystem.FaceToward(w, defs, i, w.Pos[loadHq] - w.Pos[i]);
                            w.Pos[i] = FixVec2.MoveTowards(w.Pos[i], w.Pos[loadHq], step, out _);
                        }
                    }
                    else
                    {
                        // Deliver to the cache; leftovers ride home through the normal deposit.
                        Fix cReach = selfR + Fix.Ratio(defs.Buildings[w.DefIndex[cache]].CollisionRadiusCenti, 100) + slack;
                        if ((w.Pos[cache] - w.Pos[i]).LengthSq <= cReach * cReach)
                        {
                            int room = SupplySystem.StockCapOf(w, defs, cache) - w.DepotStock[cache];
                            int xfer = System.Math.Min(w.Carry[i], room);
                            w.DepotStock[cache] += xfer;
                            w.Carry[i] -= xfer;
                            w.CaravanCache[i] = -1;
                        }
                        else
                        {
                            MovementSystem.FaceToward(w, defs, i, w.Pos[cache] - w.Pos[i]);
                            w.Pos[i] = FixVec2.MoveTowards(w.Pos[i], w.Pos[cache], step, out _);
                        }
                    }
                    continue;
                }

                if (w.Carry[i] >= def.CarryCapacity)
                {
                    int drop = FindNearestDropoff(w, defs, w.Owner[i], w.Pos[i]);
                    if (drop < 0) continue;
                    Fix reach = selfR + Fix.Ratio(defs.Buildings[w.DefIndex[drop]].CollisionRadiusCenti, 100) + slack;
                    if ((w.Pos[drop] - w.Pos[i]).LengthSq <= reach * reach)
                    {
                        if (w.CarryMineral[i])
                        {
                            w.Players[w.Owner[i]].Minerals += w.Carry[i]; // minerals bank separately
                        }
                        else
                        {
                            w.Players[w.Owner[i]].Food += w.Carry[i];
                            // Delivering nutrients to a supply drop-off (the core) also tops up its
                            // depot for free — base supply rides the normal economy. Prongs aren't depots.
                            if (defs.Buildings[w.DefIndex[drop]].ProvidesSupply)
                                w.DepotStock[drop] = System.Math.Min(SupplySystem.StockCapOf(w, defs, drop),
                                    w.DepotStock[drop] + w.Carry[i]);
                        }
                        w.Carry[i] = 0;
                        w.CarryMineral[i] = false;
                    }
                    else
                    {
                        MovementSystem.FaceToward(w, defs, i, w.Pos[drop] - w.Pos[i]);
                        w.Pos[i] = FixVec2.MoveTowards(w.Pos[i], w.Pos[drop], step, out _);
                    }
                }
                else
                {
                    int node = w.WorkNode[i];
                    if (node < 0 || w.Kind[node] != EntityKind.Node || w.NodeFood[node] <= 0)
                    {
                        node = FindNearestNode(w, i);
                        w.WorkNode[i] = node;
                        w.GatherTimer[i] = 0;
                    }
                    if (node < 0)
                    {
                        // Nothing left to mine anywhere: haul home whatever is carried.
                        if (w.Carry[i] > 0) w.Carry[i] = def.CarryCapacity; // force deposit path next tick
                        continue;
                    }
                    Fix reach = selfR + Fix.Ratio(w.Rules.NodeRadiusCenti, 100) + slack;
                    if ((w.Pos[node] - w.Pos[i]).LengthSq <= reach * reach)
                    {
                        if (++w.GatherTimer[i] >= def.GatherTicks)
                        {
                            w.GatherTimer[i] = 0;
                            int take = Math.Min(def.CarryCapacity - w.Carry[i], w.NodeFood[node]);
                            w.Carry[i] += take;
                            w.CarryMineral[i] = w.NodeMineral[node]; // the load takes the node's kind
                            w.NodeFood[node] -= take;
                            if (w.NodeFood[node] <= 0) w.Despawn(node);
                        }
                    }
                    else
                    {
                        MovementSystem.FaceToward(w, defs, i, w.Pos[node] - w.Pos[i]);
                        w.Pos[i] = FixVec2.MoveTowards(w.Pos[i], w.Pos[node], step, out _);
                    }
                }
            }

            // Site progress. Worker-built sites advance with their crew: N builders in reach
            // add N+2 work units/tick out of 3 × BuildTimeTicks (AoE2 curve, actual = 3T/(N+2)).
            // Hub-built PRONGS have no crew — the headquarters grows them at Rules.HubBuildRate.
            for (int b = 0; b < w.HighWater; b++)
            {
                if (w.Kind[b] != EntityKind.Building || w.ConstructionRemaining[b] <= 0) continue;
                int rate = defs.Buildings[w.DefIndex[b]].HubBuilt ? w.Rules.HubBuildRate
                    : (w.ScratchBuilders[b] == 0 ? 0 : w.ScratchBuilders[b] + 2);
                if (rate == 0) continue;
                w.ConstructionRemaining[b] -= rate;
                if (w.ConstructionRemaining[b] < 0) w.ConstructionRemaining[b] = 0;
            }
        }

        /// <summary>A cache is a valid caravan destination while it stands, is finished,
        /// connected to the chain (last tick's BFS), and below capacity.</summary>
        private static bool CaravanTargetValid(SimWorld w, DefDatabase defs, int worker, int cache)
        {
            if (cache < 0 || cache >= w.HighWater) return false;
            if (w.Kind[cache] != EntityKind.Building || w.Owner[cache] != w.Owner[worker]) return false;
            if (w.ConstructionRemaining[cache] > 0 || !w.ScratchConnected[cache]) return false;
            var bdef = defs.Buildings[w.DefIndex[cache]];
            return bdef.ProvidesSupply && !bdef.IsHeadquarters && w.DepotStock[cache] < SupplySystem.StockCapOf(w, defs, cache);
        }

        /// <summary>Pick the nearest needy cache, capped at ceil(deficit/carry) carriers per
        /// cache so the whole worker pool doesn't stampede one depot. Index-order deterministic.</summary>
        private static int ClaimCaravan(SimWorld w, DefDatabase defs, int worker)
        {
            int carry = defs.Units[w.DefIndex[worker]].CarryCapacity;
            if (carry <= 0 || w.Players[w.Owner[worker]].Food <= 0) return -1;

            // The supply-priority dial caps how much of the worker pool runs caravans:
            // 0 = everyone keeps the core (no caravans), 100 = the whole pool may haul.
            // Pool counts read live (claims made earlier this tick must be visible) but only
            // over this tick's worker list rather than every entity.
            int poolSize = 0, poolHauling = 0;
            for (int k = 0; k < w.ScratchWorkerCount; k++)
            {
                int o = w.ScratchWorkers[k];
                if (w.Owner[o] != w.Owner[worker]) continue;
                poolSize++;
                if (w.CaravanCache[o] >= 0) poolHauling++;
            }
            if (poolHauling >= poolSize * w.Players[w.Owner[worker]].SupplyPriority / 100) return -1;

            int best = -1;
            Fix bestSq = Fix.Zero;
            for (int ck = 0; ck < w.ScratchCacheCount; ck++) // candidate caches, ascending
            {
                int c = w.ScratchCaches[ck];
                if (!CaravanTargetValid(w, defs, worker, c)) continue;
                int deficit = SupplySystem.StockCapOf(w, defs, c) - w.DepotStock[c];
                if (deficit < carry) continue; // not worth a trip yet
                int carriers = 0;
                for (int k = 0; k < w.ScratchWorkerCount; k++)
                    if (w.CaravanCache[w.ScratchWorkers[k]] == c) carriers++;
                if (carriers >= (deficit + carry - 1) / carry) continue; // enough en route
                Fix dsq = (w.Pos[c] - w.Pos[worker]).LengthSq;
                if (best < 0 || dsq < bestSq) { best = c; bestSq = dsq; }
            }
            return best;
        }

        public static int FindHq(SimWorld w, DefDatabase defs, byte player)
        {
            for (int i = 0; i < w.HighWater; i++)
                if (w.Kind[i] == EntityKind.Building && w.Owner[i] == player && defs.Buildings[w.DefIndex[i]].IsHeadquarters)
                    return i;
            return -1;
        }

        /// <summary>Nearest finished drop-off the player owns — the headquarters OR any hub-built
        /// prong (they extend the core). Workers deposit at the closest one, so the ring of
        /// prongs around the hub gives haulers more edges to touch instead of blocking the core.
        /// Lowest index breaks ties; deterministic.</summary>
        public static int FindNearestDropoff(SimWorld w, DefDatabase defs, byte player, FixVec2 from)
        {
            int best = -1;
            Fix bestSq = Fix.Zero;
            for (int k = 0; k < w.ScratchDropoffCount; k++) // this tick's drop-off list, ascending
            {
                int i = w.ScratchDropoffs[k];
                if (w.Owner[i] != player) continue;
                Fix dsq = (w.Pos[i] - from).LengthSq;
                if (best < 0 || dsq < bestSq || (dsq == bestSq && i < best)) { best = i; bestSq = dsq; }
            }
            return best;
        }

        private static int FindNearestNode(SimWorld w, int self)
        {
            int best = -1;
            Fix bestSq = Fix.Zero;
            for (int k = 0; k < w.ScratchNodeCount; k++) // this tick's node list, ascending
            {
                int i = w.ScratchNodes[k];
                if (w.Kind[i] != EntityKind.Node || w.NodeFood[i] <= 0) continue; // may have run dry
                Fix dsq = (w.Pos[i] - w.Pos[self]).LengthSq;
                if (best < 0 || dsq < bestSq) { best = i; bestSq = dsq; }
            }
            return best;
        }

        /// <summary>Node whose footprint contains the point (nearest wins), or -1. Used to route
        /// a building's rally-to-resource-pile onto a specific node.</summary>
        public static int NodeAtPoint(SimWorld w, FixVec2 point)
        {
            Fix r = Fix.Ratio(w.Rules.NodeRadiusCenti, 100);
            Fix rSq = r * r;
            int best = -1;
            Fix bestSq = Fix.Zero;
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Node || w.NodeFood[i] <= 0) continue;
                Fix dsq = (w.Pos[i] - point).LengthSq;
                if (dsq > rSq) continue;
                if (best < 0 || dsq < bestSq) { best = i; bestSq = dsq; }
            }
            return best;
        }
    }

    /// <summary>
    /// Tech-path upgrades. Purchased upgrades scale one stat for the listed units by Num/Den;
    /// this folds every applicable (bought, matching, affecting) upgrade for a player+unit into
    /// a running Num/Den so the caller applies exactly ONE integer floor (iron rule). Index-order
    /// scan over the upgrade defs — deterministic.
    /// </summary>
    public static class UpgradeSystem
    {
        public static void Fold(SimWorld w, DefDatabase defs, byte owner, int unitDef, UpgradeStat stat, ref long num, ref long den)
        {
            if (owner >= w.Players.Length) return;
            var levels = w.Players[owner].UpgradeLevels;
            for (int u = 0; u < defs.Upgrades.Length; u++)
            {
                if (u >= levels.Length || levels[u] == 0) continue;
                var up = defs.Upgrades[u];
                if (up.Stat != stat || !up.AffectsUnit[unitDef]) continue;
                num *= up.Num;
                den *= up.Den;
            }
        }

        /// <summary>A base centi value scaled by a stat's upgrades, one floor. Used for ranges.</summary>
        public static int ScaleCenti(SimWorld w, DefDatabase defs, byte owner, int unitDef, int baseCenti, UpgradeStat stat)
        {
            long num = 1, den = 1;
            Fold(w, defs, owner, unitDef, stat, ref num, ref den);
            return den == 1 && num == 1 ? baseCenti : (int)((long)baseCenti * num / den);
        }
    }

    public static class MovementSystem
    {
        /// <summary>centi-units/second → world units per tick, one floor.</summary>
        public static Fix PerTickStep(int moveSpeedCenti) => Fix.Ratio(moveSpeedCenti, 100 * SimConstants.TicksPerSecond);

        /// <summary>
        /// Turn a unit's body facing toward a direction at its def's turn speed. Only runs
        /// while a unit moves, builds, or fights — an idle unit keeps its facing (so an
        /// ordered SetFacing holds until the next activity). Facing is hashed state and
        /// drives directional damage.
        /// </summary>
        public static void FaceToward(SimWorld w, DefDatabase defs, int i, FixVec2 toward)
        {
            var def = defs.Units[w.DefIndex[i]];
            Fix chord = Fix.Ratio(def.TurnSpeedCenti, 100 * SimConstants.TicksPerSecond);
            w.Facing[i] = FixVec2.TurnTowards(w.Facing[i], toward, chord);
        }

        /// <summary>Per-tick step for a specific unit with upgrade rationals folded in
        /// as a single division (one floor).</summary>
        public static Fix StepFor(SimWorld w, DefDatabase defs, int e)
        {
            var def = defs.Units[w.DefIndex[e]];
            long num = 1, den = 1;
            UpgradeSystem.Fold(w, defs, w.Owner[e], w.DefIndex[e], UpgradeStat.MoveSpeed, ref num, ref den);
            return Fix.Ratio((long)def.MoveSpeedCenti * num, 100L * SimConstants.TicksPerSecond * den);
        }

        public static void Tick(SimWorld w, DefDatabase defs)
        {
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Unit || !w.HasMoveOrder[i]) continue;
                Fix step = StepFor(w, defs, i);
                FaceToward(w, defs, i, w.MoveTarget[i] - w.Pos[i]);
                w.Pos[i] = FixVec2.MoveTowards(w.Pos[i], w.MoveTarget[i], step, out bool arrived);
                if (arrived)
                {
                    w.HasMoveOrder[i] = false;
                    CommandSystem.AdvanceQueue(w, defs, i); // start the next shift-queued leg
                }
            }
        }
    }

    /// <summary>
    /// Hard-body separation: overlapping entities push apart along their center line. How the
    /// overlap is split depends on the pair:
    ///  • Opposing-team units use BODY WEIGHT (radius × maxHp): the heavier gives ground in
    ///    inverse proportion, and once one outweighs the other past Rules.CollisionBlockRatio
    ///    it becomes an immovable wall — big, tough units body-block smaller enemies.
    ///  • Friendly units yield by PRIORITY, not weight: workers slip through warriors, so a
    ///    hauler is never pinned by its own army (the worker takes the whole push).
    ///  • Same-priority friendlies and everything else split by push resistance, as before.
    /// Buildings and nodes are immobile. Pair order is i&lt;j dense-index — deterministic.
    /// </summary>
    public static class CollisionSystem
    {
        /// <summary>Shove-weight for opposing-team blocking: bigger and tougher = harder to move.
        /// Uses centi-radius so it stays integer (a Fix radius would floor small units to 0).</summary>
        private static long BodyWeight(SimWorld w, DefDatabase defs, int e)
        {
            var def = defs.Units[w.DefIndex[e]];
            return (long)def.CollisionRadiusCenti * System.Math.Max(1, def.MaxHp);
        }

        /// <summary>Movement priority among FRIENDLY units — lower yields (takes the push).
        /// Workers (0) get out of the way of warriors (1); leaders hold ground most (2).</summary>
        private static int MovePriority(DefDatabase defs, int unitDef)
        {
            var def = defs.Units[unitDef];
            if (def.IsWorker) return 0;
            return def.IsLeader ? 2 : 1;
        }

        public static void Tick(SimWorld w, DefDatabase defs)
        {
            w.RebuildGrid(); // positions moved since the last bucketing
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] == EntityKind.None) continue;
                bool mobileI = w.Kind[i] == EntityKind.Unit;
                Fix ri = RadiusOf(w, defs, i);

                // Only neighbors within ri + the largest possible radius can overlap i.
                Fix reach = ri + w.MaxInteractRadius;
                int cx0 = w.GridClampX((int)((w.Pos[i].X - reach).Raw >> SimWorld.GridShift));
                int cx1 = w.GridClampX((int)((w.Pos[i].X + reach).Raw >> SimWorld.GridShift));
                int cy0 = w.GridClampY((int)((w.Pos[i].Y - reach).Raw >> SimWorld.GridShift));
                int cy1 = w.GridClampY((int)((w.Pos[i].Y + reach).Raw >> SimWorld.GridShift));
                for (int cy = cy0; cy <= cy1; cy++)
                for (int cx = cx0; cx <= cx1; cx++)
                for (int j = w.GridHead[cy * w.GridCellsX + cx]; j >= 0; j = w.GridNext[j])
                {
                    if (j <= i) continue; // each pair once, i < j
                    bool mobileJ = w.Kind[j] == EntityKind.Unit;
                    if (!mobileI && !mobileJ) continue;
                    Fix rj = RadiusOf(w, defs, j);
                    Fix rSum = ri + rj;
                    FixVec2 d = w.Pos[j] - w.Pos[i];
                    Fix distSq = d.LengthSq;
                    if (distSq >= rSum * rSum) continue;

                    Fix dist = Fix.Sqrt(distSq);
                    FixVec2 dir = dist.Raw == 0
                        ? SimWorld.RingDir(i + j) // exactly coincident: deterministic split axis
                        : new FixVec2(d.X / dist, d.Y / dist);
                    Fix overlap = rSum - dist;

                    if (mobileI && mobileJ)
                    {
                        // shareI = fraction of the overlap that entity i moves (j moves the rest).
                        Fix shareI;
                        if (w.AreEnemies(w.Owner[i], w.Owner[j]))
                        {
                            // Body blocking: the heavier body gives less ground, and past the
                            // block ratio the lighter one takes the whole push (a hard wall).
                            long wi = BodyWeight(w, defs, i), wj = BodyWeight(w, defs, j);
                            var r = w.Rules;
                            if (wi * r.CollisionBlockRatioDen >= wj * r.CollisionBlockRatioNum) shareI = Fix.Zero;      // i walls j
                            else if (wj * r.CollisionBlockRatioDen >= wi * r.CollisionBlockRatioNum) shareI = overlap;  // j walls i
                            else shareI = overlap * Fix.Ratio(wj, wi + wj); // inverse-weight split
                        }
                        else
                        {
                            // Friendly: the lower-priority unit yields. Workers step aside for
                            // warriors; equal priority falls back to push-resistance.
                            int pi = MovePriority(defs, w.DefIndex[i]), pj = MovePriority(defs, w.DefIndex[j]);
                            if (pi < pj) shareI = overlap;      // i is lower priority → i moves fully
                            else if (pj < pi) shareI = Fix.Zero; // j yields
                            else
                            {
                                int resI = defs.Units[w.DefIndex[i]].PushResistance;
                                int resJ = defs.Units[w.DefIndex[j]].PushResistance;
                                int total = resI + resJ;
                                if (total <= 0) total = 1;
                                shareI = overlap * Fix.Ratio(resJ, total);
                            }
                        }
                        w.Pos[i] = w.ClampToMap(w.Pos[i] - dir * shareI);
                        w.Pos[j] = w.ClampToMap(w.Pos[j] + dir * (overlap - shareI));
                    }
                    else if (mobileI) w.Pos[i] = w.ClampToMap(w.Pos[i] - dir * overlap);
                    else w.Pos[j] = w.ClampToMap(w.Pos[j] + dir * overlap);
                }

                // Terrain walls are absolutely immovable: any overlapping unit takes the
                // whole separation. Straight-line movers slide along the arc, so walls
                // shape chokepoints and flanking routes without a pathfinder.
                if (mobileI)
                {
                    for (int k = 0; k < w.WallPos.Length; k++)
                    {
                        Fix rSum = ri + w.WallRadius[k];
                        FixVec2 d = w.Pos[i] - w.WallPos[k];
                        Fix distSq = d.LengthSq;
                        if (distSq >= rSum * rSum) continue;
                        Fix dist = Fix.Sqrt(distSq);
                        FixVec2 dir = dist.Raw == 0
                            ? SimWorld.RingDir(i + k) // dead center: deterministic exit axis
                            : new FixVec2(d.X / dist, d.Y / dist);
                        w.Pos[i] = w.ClampToMap(w.WallPos[k] + dir * rSum);
                    }
                }
            }
        }

        public static Fix RadiusOf(SimWorld w, DefDatabase defs, int e)
        {
            switch (w.Kind[e])
            {
                case EntityKind.Unit: return Fix.Ratio(defs.Units[w.DefIndex[e]].CollisionRadiusCenti, 100);
                case EntityKind.Building: return Fix.Ratio(defs.Buildings[w.DefIndex[e]].CollisionRadiusCenti, 100);
                case EntityKind.Node: return Fix.Ratio(w.Rules.NodeRadiusCenti, 100);
                default: return Fix.Zero;
            }
        }
    }

    /// <summary>
    /// Auto-engagement combat: armed units acquire the nearest enemy in range (lowest index
    /// wins ties), attack when in reach, and chase only when the unit has no standing move
    /// order.
    /// </summary>
    public static class CombatSystem
    {
        public static int CooldownFor(SimWorld w, DefDatabase defs, int e)
        {
            var def = defs.Units[w.DefIndex[e]];
            long num = 1, den = 1;
            // Attack-speed upgrades shorten the cooldown (their Num<Den).
            UpgradeSystem.Fold(w, defs, w.Owner[e], w.DefIndex[e], UpgradeStat.AttackSpeed, ref num, ref den);
            return (int)((long)def.AttackCooldownTicks * num / den);
        }

        /// <summary>
        /// Which arc of the victim's body a position lies in: 0 = front, 1 = side, 2 = rear.
        /// Compares dot(facing, toAttacker) against the rules' arc cosines squared — no sqrt,
        /// pure integer math. Facing is maintained unit-length by TurnTowards.
        /// </summary>
        public static int ArcOf(SimWorld w, int victim, FixVec2 from)
        {
            FixVec2 d = from - w.Pos[victim];
            Fix d2 = d.LengthSq;
            if (d2.Raw == 0) return 0; // coincident: call it frontal
            FixVec2 f = w.Facing[victim];
            Fix dot = f.X * d.X + f.Y * d.Y;
            Fix dot2 = dot * dot;
            var r = w.Rules;
            if (dot >= Fix.Zero)
            {
                if (dot2 * Fix.FromInt(r.FrontArcCosDen * r.FrontArcCosDen)
                    >= d2 * Fix.FromInt(r.FrontArcCosNum * r.FrontArcCosNum)) return 0;
            }
            else
            {
                if (dot2 * Fix.FromInt(r.RearArcCosDen * r.RearArcCosDen)
                    >= d2 * Fix.FromInt(r.RearArcCosNum * r.RearArcCosNum)) return 2;
            }
            return 1;
        }

        /// <summary>
        /// Effective attack damage against a specific target. Rational factors combined
        /// with ONE integer floor (iron rule): the leader aura bonus (standing within a
        /// friendly leader's aura), and the directional multiplier — unit victims struck
        /// from the side or rear take extra damage based on THEIR facing vs the attacker's
        /// position. Buildings have no facing and take flat damage. Turn speed and facing
        /// decide fights: a pincer's flanks genuinely hit harder.
        /// </summary>
        public static int DamageOf(SimWorld w, DefDatabase defs, int e, int target)
        {
            // Base attack, plus the owner's standing attack structures. Added BEFORE the
            // rationals, so it is a genuine stat boost that aura/arc bonuses scale too.
            // Unarmed units never reach here (the combat loop skips AttackDamage <= 0), so
            // this can't hand workers a weapon.
            int dmg = defs.Units[w.DefIndex[e]].AttackDamage;
            if (w.Owner[e] < w.ScratchAttackBonus.Length) dmg += w.ScratchAttackBonus[w.Owner[e]];
            long num = 1, den = 1;

            if (w.ScratchLeaderAura[e])
            {
                num *= w.Rules.LeaderAuraBonusNum;
                den *= w.Rules.LeaderAuraBonusDen;
            }

            if (w.Kind[target] == EntityKind.Unit)
            {
                int arc = ArcOf(w, target, w.Pos[e]);
                if (arc == 1) { num *= w.Rules.SideDamageNum; den *= w.Rules.SideDamageDen; }
                else if (arc == 2) { num *= w.Rules.RearDamageNum; den *= w.Rules.RearDamageDen; }
            }

            // Logistics bites here: an unsupplied attacker fights at reduced effect.
            if (w.SupplyTicks[e] == 0)
            {
                num *= w.Rules.UnsuppliedDamageNum;
                den *= w.Rules.UnsuppliedDamageDen;
            }

            // Tech paths: attacker's damage upgrades and the target's armor (incoming-damage
            // reduction) both fold into the same one-floor expression.
            UpgradeSystem.Fold(w, defs, w.Owner[e], w.DefIndex[e], UpgradeStat.Damage, ref num, ref den);
            if (w.Kind[target] == EntityKind.Unit)
                UpgradeSystem.Fold(w, defs, w.Owner[target], w.DefIndex[target], UpgradeStat.Armor, ref num, ref den);

            return (int)(dmg * num / den);
        }

        /// <summary>
        /// Apply damage and pay the kill bounty. The victim's owner-cost share is banked by the
        /// attacker's player ONLY on the blow that takes it from alive to dead — later hits on
        /// the same corpse this tick (it isn't despawned until CleanupSystem) must not pay again.
        /// Damage itself is applied unconditionally, exactly as before.
        /// </summary>
        private static void Hit(SimWorld w, DefDatabase defs, int attacker, int target, int dmg)
        {
            bool wasAlive = w.Hp[target] > 0;
            w.Hp[target] -= dmg;
            if (!wasAlive || w.Hp[target] > 0) return;
            if (w.Kind[target] != EntityKind.Unit) return; // buildings carry no bounty
            byte killer = w.Owner[attacker];
            if (killer >= w.Players.Length) return;
            int bounty = (int)((long)defs.Units[w.DefIndex[target]].FoodCost
                * w.Rules.KillBountyNum / w.Rules.KillBountyDen); // one floor
            w.Players[killer].Food += bounty;
            w.Players[killer].EvoPoints += w.Rules.EvoPerKill; // evolution is paid for in blood
        }

        /// <summary>Tally each player's flat attack bonus from their standing AttackBonus
        /// structures. Derived from hashed state every tick — build one and the whole army hits
        /// harder; lose it and the bonus goes with it. Index-order scan.</summary>
        private static void TallyAttackBonus(SimWorld w, DefDatabase defs)
        {
            for (int p = 0; p < w.ScratchAttackBonus.Length; p++) w.ScratchAttackBonus[p] = 0;
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Building || w.ConstructionRemaining[i] > 0) continue;
                int bonus = defs.Buildings[w.DefIndex[i]].AttackBonus;
                if (bonus == 0) continue;
                byte owner = w.Owner[i];
                if (owner < w.ScratchAttackBonus.Length) w.ScratchAttackBonus[owner] += bonus;
            }
        }

        public static void Tick(SimWorld w, DefDatabase defs)
        {
            w.RebuildGrid(); // movement + collision have shuffled positions this tick
            TallyAttackBonus(w, defs);
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Unit) continue;
                if (w.AttackCooldown[i] > 0) w.AttackCooldown[i]--;
                var def = defs.Units[w.DefIndex[i]];
                if (def.AttackDamage <= 0) continue;

                Fix selfR = Fix.Ratio(def.CollisionRadiusCenti, 100);
                // Tech-path range upgrades widen acquire/attack reach for the owner's units.
                Fix acquire = Fix.Ratio(UpgradeSystem.ScaleCenti(w, defs, w.Owner[i], w.DefIndex[i], def.AcquireRangeCenti, UpgradeStat.AcquireRange), 100);
                int atkRangeCenti = UpgradeSystem.ScaleCenti(w, defs, w.Owner[i], w.DefIndex[i], def.AttackRangeCenti, UpgradeStat.AttackRange);
                int target = -1;
                Fix bestSq = Fix.Zero;
                Fix reach0 = acquire + selfR + w.MaxInteractRadius;
                int cx0 = w.GridClampX((int)((w.Pos[i].X - reach0).Raw >> SimWorld.GridShift));
                int cx1 = w.GridClampX((int)((w.Pos[i].X + reach0).Raw >> SimWorld.GridShift));
                int cy0 = w.GridClampY((int)((w.Pos[i].Y - reach0).Raw >> SimWorld.GridShift));
                int cy1 = w.GridClampY((int)((w.Pos[i].Y + reach0).Raw >> SimWorld.GridShift));
                for (int cy = cy0; cy <= cy1; cy++)
                for (int cx = cx0; cx <= cx1; cx++)
                for (int j = w.GridHead[cy * w.GridCellsX + cx]; j >= 0; j = w.GridNext[j])
                {
                    if (j == i || !w.AreEnemies(w.Owner[i], w.Owner[j])) continue;
                    if (w.Kind[j] != EntityKind.Unit && w.Kind[j] != EntityKind.Building) continue;
                    Fix maxD = acquire + selfR + CollisionSystem.RadiusOf(w, defs, j);
                    Fix dsq = (w.Pos[j] - w.Pos[i]).LengthSq;
                    if (dsq > maxD * maxD) continue;
                    // Order-independent choice: nearest wins, lowest index breaks ties — the
                    // grid's iteration order must never leak into the result.
                    if (target < 0 || dsq < bestSq || (dsq == bestSq && j < target)) { target = j; bestSq = dsq; }
                }
                if (target < 0)
                {
                    // No enemy in range. An attack-moving unit advances toward its
                    // destination; when it arrives the order ends.
                    if (w.AttackMove[i] && !w.HasMoveOrder[i])
                    {
                        MovementSystem.FaceToward(w, defs, i, w.MoveTarget[i] - w.Pos[i]);
                        w.Pos[i] = FixVec2.MoveTowards(w.Pos[i], w.MoveTarget[i], MovementSystem.StepFor(w, defs, i), out bool arrived);
                        if (arrived)
                        {
                            w.AttackMove[i] = false;
                            CommandSystem.AdvanceQueue(w, defs, i); // start the next shift-queued leg
                        }
                    }
                    continue;
                }

                Fix reach = Fix.Ratio(atkRangeCenti, 100) + selfR + CollisionSystem.RadiusOf(w, defs, target);
                if (bestSq <= reach * reach)
                {
                    MovementSystem.FaceToward(w, defs, i, w.Pos[target] - w.Pos[i]); // square up to the target
                    if (w.AttackCooldown[i] == 0)
                    {
                        Hit(w, defs, i, target, DamageOf(w, defs, i, target));
                        w.AttackCooldown[i] = CooldownFor(w, defs, i);
                        w.AttackEvents.Add(new AttackEvent { Attacker = i, Target = target });
                    }
                }
                else if (!w.HasMoveOrder[i])
                {
                    // Idle and attack-moving units chase acquired enemies (both have no standing
                    // move order); a plain-move unit keeps walking and never diverts.
                    MovementSystem.FaceToward(w, defs, i, w.Pos[target] - w.Pos[i]);
                    w.Pos[i] = FixVec2.MoveTowards(w.Pos[i], w.Pos[target], MovementSystem.StepFor(w, defs, i), out _);
                }
            }

            // ---- STATIC DEFENSE: a finished building fires a ranged shot if it was upgraded
            // to Tier >= 1 (flat rules-driven cache stats, exactly as before) OR its def
            // carries its own AttackDamage (spike-battery style, def-driven stats; def stats
            // win if both apply). No arcs, aura bonuses, or supply modifiers.
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Building || w.ConstructionRemaining[i] > 0) continue;
                var bdef = defs.Buildings[w.DefIndex[i]];
                bool defArmed = bdef.AttackDamage > 0;
                if (!defArmed && w.Tier[i] == 0) continue;
                if (w.AttackCooldown[i] > 0) { w.AttackCooldown[i]--; continue; }

                int dmg = defArmed ? bdef.AttackDamage : w.Rules.CacheAttackDamage;
                Fix range = Fix.Ratio(defArmed ? bdef.AttackRangeCenti : w.Rules.CacheAttackRangeCenti, 100);
                Fix selfR = Fix.Ratio(bdef.CollisionRadiusCenti, 100);
                int target = -1;
                Fix bestSq = Fix.Zero;
                Fix reach0 = range + selfR + w.MaxInteractRadius;
                int cx0 = w.GridClampX((int)((w.Pos[i].X - reach0).Raw >> SimWorld.GridShift));
                int cx1 = w.GridClampX((int)((w.Pos[i].X + reach0).Raw >> SimWorld.GridShift));
                int cy0 = w.GridClampY((int)((w.Pos[i].Y - reach0).Raw >> SimWorld.GridShift));
                int cy1 = w.GridClampY((int)((w.Pos[i].Y + reach0).Raw >> SimWorld.GridShift));
                for (int cy = cy0; cy <= cy1; cy++)
                for (int cx = cx0; cx <= cx1; cx++)
                for (int j = w.GridHead[cy * w.GridCellsX + cx]; j >= 0; j = w.GridNext[j])
                {
                    if (!w.AreEnemies(w.Owner[i], w.Owner[j])) continue;
                    if (w.Kind[j] != EntityKind.Unit && w.Kind[j] != EntityKind.Building) continue;
                    Fix maxD = range + selfR + CollisionSystem.RadiusOf(w, defs, j);
                    Fix dsq = (w.Pos[j] - w.Pos[i]).LengthSq;
                    if (dsq > maxD * maxD) continue;
                    if (target < 0 || dsq < bestSq || (dsq == bestSq && j < target)) { target = j; bestSq = dsq; }
                }
                if (target < 0) continue;
                Hit(w, defs, i, target, dmg); // armed structures earn bounty too
                w.AttackCooldown[i] = defArmed ? bdef.AttackCooldownTicks : w.Rules.CacheAttackCooldownTicks;
                w.AttackEvents.Add(new AttackEvent { Attacker = i, Target = target });
            }
        }
    }

    /// <summary>
    /// LOGISTICS. Supply flows from headquarters through a CHAIN of supply buildings (each
    /// within Rules.SupplyLinkRangeCenti of the next); a connected building projects
    /// Rules.SupplyRadiusCenti of supply. The chain is the supply line: destroy a link and
    /// everything beyond it goes dark. Units carry a grace reservoir (SupplyGraceTicks) that
    /// refills inside friendly supply and drains outside; at zero they fight at
    /// UnsuppliedDamageNum/Den damage until resupplied. Construction sites don't supply.
    /// </summary>
    public static class SupplySystem
    {
        public static void Tick(SimWorld w, DefDatabase defs)
        {
            // BFS the supply chain per player from its headquarters, over supply buildings.
            bool[] connected = w.ScratchConnected;
            Array.Clear(connected, 0, w.HighWater);
            int[] queue = w.ScratchQueue; // reused scratch (LeaderAuraSystem borrows it later in the tick)
            int head = 0, tail = 0;
            Fix link = Fix.Ratio(w.Rules.SupplyLinkRangeCenti, 100);
            Fix linkSq = link * link;

            // Gather the finished supply buildings ONCE (ascending index), so the BFS relaxes
            // over those instead of re-walking every entity for each link.
            int[] supply = w.ScratchDepots;
            int supplyCount = 0;
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Building || w.ConstructionRemaining[i] > 0) continue;
                if (!defs.Buildings[w.DefIndex[i]].ProvidesSupply) continue;
                supply[supplyCount++] = i;
                if (defs.Buildings[w.DefIndex[i]].IsHeadquarters) { connected[i] = true; queue[tail++] = i; }
            }
            while (head < tail)
            {
                int b = queue[head++];
                for (int k = 0; k < supplyCount; k++)
                {
                    int j = supply[k];
                    if (connected[j]) continue;
                    if (w.Owner[j] != w.Owner[b]) continue;
                    if ((w.Pos[j] - w.Pos[b]).LengthSq > linkSq) continue;
                    connected[j] = true;
                    queue[tail++] = j;
                }
            }

            // Units: supplied only by a connected depot WITH STOCK covering them (nearest one,
            // lowest index on ties). Each supplied unit eats 1 food from its depot every
            // SupplyDrainTicks, staggered by entity index so consumption is smooth. A dry
            // depot supplies nothing — the army's grace runs down and the debuff lands.
            Fix radius = Fix.Ratio(w.Rules.SupplyRadiusCenti, 100);
            Fix radiusSq = radius * radius;
            // Narrow the per-unit search to CONNECTED depots (ascending, so the lowest-index
            // tie-break is unchanged). Stock is still re-checked live inside the loop, because
            // units drain depots as we go and a depot going dry mid-tick must stop supplying.
            int depotCount = 0;
            for (int k = 0; k < supplyCount; k++)
                if (connected[supply[k]]) supply[depotCount++] = supply[k];

            for (int u = 0; u < w.HighWater; u++)
            {
                if (w.Kind[u] != EntityKind.Unit) continue;
                int depot = -1;
                Fix bestSq = Fix.Zero;
                for (int k = 0; k < depotCount; k++)
                {
                    int b = supply[k];
                    if (w.Owner[b] != w.Owner[u]) continue;
                    if (w.DepotStock[b] <= 0) continue;
                    Fix dsq = (w.Pos[b] - w.Pos[u]).LengthSq;
                    if (dsq > radiusSq) continue;
                    if (depot < 0 || dsq < bestSq || (dsq == bestSq && b < depot)) { depot = b; bestSq = dsq; }
                }
                if (depot >= 0)
                {
                    w.SupplyTicks[u] = w.Rules.SupplyGraceTicks;
                    // Tiered depots feed troops more efficiently: each tier doubles the
                    // interval between bites, halving food consumed per unit.
                    if ((w.TickCount + u) % (w.Rules.SupplyDrainTicks << w.Tier[depot]) == 0)
                        w.DepotStock[depot]--;
                }
                else if (w.SupplyTicks[u] > 0) w.SupplyTicks[u]--;
            }
        }

        /// <summary>Stock capacity with the cache's tier applied (each tier doubles it).</summary>
        public static int StockCapOf(SimWorld w, DefDatabase defs, int e) =>
            defs.Buildings[w.DefIndex[e]].StockCapacity << w.Tier[e];
    }

    /// <summary>Removes dead entities; a dead headquarters eliminates its player.</summary>
    public static class CleanupSystem
    {
        public static void Tick(SimWorld w, DefDatabase defs)
        {
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] == EntityKind.Unit && w.Hp[i] <= 0)
                {
                    w.Despawn(i);
                }
                else if (w.Kind[i] == EntityKind.Building && w.Hp[i] <= 0)
                {
                    byte p = w.Owner[i];
                    bool wasHq = defs.Buildings[w.DefIndex[i]].IsHeadquarters;
                    w.Despawn(i);
                    if (wasHq)
                    {
                        w.Players[p].Alive = false;
                        for (int j = 0; j < w.HighWater; j++)
                            if (w.Kind[j] != EntityKind.None && w.Kind[j] != EntityKind.Node && w.Owner[j] == p)
                                w.Despawn(j);
                    }
                }
            }
        }
    }
}
