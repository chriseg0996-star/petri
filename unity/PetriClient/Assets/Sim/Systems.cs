using System;

namespace Petri.Core
{
    /// <summary>
    /// Automated production: buildings continuously produce whichever producible unit is
    /// furthest below its weight share (players set composition, not build orders). Newly
    /// completed combat units auto-reinforce the nearest swarm leader with capacity.
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
                        if (player.Food >= defs.Units[w.ProduceOverride[i]].FoodCost
                            && UnderLeaderCap(w, defs, counts, w.Owner[i], w.ProduceOverride[i]))
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
                            if (!UnderLeaderCap(w, defs, counts, w.Owner[i], cand)) continue;
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
                    if (e >= 0)
                    {
                        if (w.HasRally[i])
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
                                // Combat units join a swarm leader near the rally point if one
                                // has room (and the building assimilates); otherwise walk there —
                                // still SEEKING, so they join a swarm that gains room later.
                                int lead = udef.IsLeader || !w.AutoAssimilate[i] ? -1
                                    : SwarmSystem.NearestLeaderWithCapacityNearPoint(w, defs, w.Owner[i],
                                        w.RallyPoint[i], Fix.Ratio(w.Rules.SwarmJoinRadiusCenti, 100));
                                if (lead >= 0) w.Leader[e] = lead;
                                else
                                {
                                    w.MoveTarget[e] = w.RallyPoint[i];
                                    w.HasMoveOrder[e] = true;
                                    if (!udef.IsLeader && w.AutoAssimilate[i]) w.SeekingSwarm[e] = true;
                                }
                            }
                        }
                        else if (!udef.IsWorker && !udef.IsLeader && w.AutoAssimilate[i])
                        {
                            // Fresh combat units reinforce the nearest swarm with room (never
                            // workers or leaders themselves). If every leader is full right now,
                            // KEEP SEEKING — they join the next leader that appears or frees up,
                            // instead of staying loose forever.
                            int lead = SwarmSystem.NearestLeaderWithCapacityFresh(w, defs, e);
                            if (lead >= 0) w.Leader[e] = lead;
                            else w.SeekingSwarm[e] = true;
                        }
                    }
                    w.ProduceChoice[i] = -1;
                    w.ProduceProgress[i] = 0;
                }
            }
        }

        /// <summary>Leaders are capped per player (Rules.MaxLeadersPerPlayer, matching the nine
        /// control groups). counts[] already includes live units AND queued production, so a
        /// leader mid-build blocks a tenth from being queued.</summary>
        private static bool UnderLeaderCap(SimWorld w, DefDatabase defs, int[] counts, byte owner, int cand)
        {
            if (!defs.Units[cand].IsLeader) return true;
            int leaders = 0;
            for (int k = 0; k < defs.Units.Length; k++)
                if (defs.Units[k].IsLeader) leaders += counts[owner * defs.Units.Length + k];
            return leaders < w.Rules.MaxLeadersPerPlayer;
        }
    }

    /// <summary>
    /// Swarm leaders and tactical formations. Each leader commands up to
    /// Rules.MaxUnitsPerLeader units. Squad members hold data-driven formation slots laid
    /// out in the leader's facing frame; role scores decide which band a unit belongs to.
    /// When a leader dies its squad goes Leaderless (-25% move/attack via rules rationals),
    /// retreats to the nearest leader with capacity, and auto-joins inside the join radius.
    /// </summary>
    public static class SwarmSystem
    {
        public static void Tick(SimWorld w, DefDatabase defs)
        {
            w.RebuildGrid(); // posture release and encircle anchoring query the spatial grid

            // Pass 1: validate leader links (a dead leader's index may already be reused —
            // possibly by an enemy or a non-leader — so re-check everything), count squads.
            // squad[] counts only non-leader members: leader→leader links are limbs of a
            // larger swarm and don't consume member capacity. A leader whose prime died just
            // unlinks — commanders never suffer the leaderless penalty.
            int[] squad = w.ScratchSquadCount;
            Array.Clear(squad, 0, w.HighWater);
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Unit) continue;
                int lead = w.Leader[i];
                if (lead < 0) continue;
                bool selfLeader = defs.Units[w.DefIndex[i]].IsLeader;
                if (w.Kind[lead] != EntityKind.Unit || !defs.Units[w.DefIndex[lead]].IsLeader || w.Owner[lead] != w.Owner[i])
                {
                    w.Leader[i] = -1;
                    w.Settled[i] = false;
                    w.AttackMove[i] = false; // stale posture must not keep driving it
                    if (!selfLeader) w.Leaderless[i] = true;
                }
                else if (!selfLeader) squad[lead]++;
            }

            // Pass 1b: "arrive as one" pacing. Each swarm tree whose root leader has MoveAsOne
            // travels at its slowest unit's speed, so the body arrives together instead of
            // stringing out. Derived scratch only — recomputed every tick from hashed state;
            // MovementSystem applies the cap to formation travel (combat chasing stays full
            // speed: fights are fought at each unit's own pace).
            Array.Clear(w.ScratchStepCap, 0, w.HighWater);
            Array.Clear(w.ScratchMinStep, 0, w.HighWater);
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Unit) continue;
                int root = i, guard = 0;
                while (w.Leader[root] >= 0 && guard++ <= w.HighWater) root = w.Leader[root];
                w.ScratchRoot[i] = root;
                long step = MovementSystem.PerTickStep(defs.Units[w.DefIndex[i]].MoveSpeedCenti).Raw;
                if (w.ScratchMinStep[root] == 0 || step < w.ScratchMinStep[root]) w.ScratchMinStep[root] = step;
            }
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Unit) continue;
                int root = w.ScratchRoot[i];
                w.ScratchStepCap[i] = w.MoveAsOne[root] ? w.ScratchMinStep[root] : 0;
            }

            // Pass 1c: swarm-link load balancing. Members redistribute evenly across the
            // squads of a linked swarm — one transfer per tree per tick, from the fullest
            // squad to the emptiest (the member nearest the recipient leader moves), so
            // rebalancing reads as an organic trickle rather than a teleport. HOOK: when
            // formation limb-layout defs land, this is where per-limb role priorities bias
            // WHO transfers WHERE (vanguard limbs pull tanks, rearguard limbs pull ranged).
            for (int root = 0; root < w.HighWater; root++)
            {
                if (w.Kind[root] != EntityKind.Unit || !defs.Units[w.DefIndex[root]].IsLeader) continue;
                if (w.Leader[root] >= 0) continue; // tree tops only

                int fullest = -1, emptiest = -1;
                for (int L = 0; L < w.HighWater; L++)
                {
                    if (w.Kind[L] != EntityKind.Unit || !defs.Units[w.DefIndex[L]].IsLeader) continue;
                    if (w.ScratchRoot[L] != root) continue; // this tree (includes the root)
                    if (fullest < 0 || squad[L] > squad[fullest]) fullest = L;
                    if (emptiest < 0 || squad[L] < squad[emptiest]) emptiest = L;
                }
                if (fullest < 0 || emptiest < 0 || fullest == emptiest) continue;
                if (squad[fullest] - squad[emptiest] <= 1) continue; // balanced enough

                int pick = -1;
                Fix bestSq = Fix.Zero;
                for (int i = 0; i < w.HighWater; i++)
                {
                    if (!IsMemberOf(w, defs, i, fullest)) continue;
                    Fix dsq = (w.Pos[emptiest] - w.Pos[i]).LengthSq;
                    if (pick < 0 || dsq < bestSq) { pick = i; bestSq = dsq; }
                }
                if (pick < 0) continue;
                w.Leader[pick] = emptiest;
                w.Settled[pick] = false;
                squad[fullest]--;
                squad[emptiest]++;
            }

            // Pass 2: leaderless units retreat to the nearest friendly leader with capacity
            // and join once inside the join radius. SEEKING units (produced by an
            // auto-assimilate building when every leader was full) also join a capacity
            // leader that comes within the join radius — but they wait in place rather than
            // marching across the map, so rally points and stockpiles stay meaningful.
            //
            // COMMITTED units — attack-moving or with an enemy in acquire range — never
            // abandon their engagement to go find a leader. They keep fighting where they
            // are and only get absorbed when a leader comes to THEM (cohesion range).
            Fix joinR = Fix.Ratio(w.Rules.SwarmJoinRadiusCenti, 100);
            Fix recruitR = Fix.Ratio(w.Rules.SquadCohesionRadiusCenti, 100);
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Unit) continue;
                bool leaderless = w.Leaderless[i];
                if (!leaderless && !w.SeekingSwarm[i]) continue;
                bool committed = w.AttackMove[i] || CombatSystem.EnemyInAcquireRange(w, defs, i);
                int lead = NearestLeaderWithCapacity(w, defs, i, squad);
                if (lead < 0) continue; // no leader anywhere: stand and fight (or keep waiting)
                Fix limit = committed ? recruitR : joinR;
                if ((w.Pos[lead] - w.Pos[i]).LengthSq <= limit * limit)
                {
                    // NOTE: joining does NOT clear the leaderless penalty — a survivor stays
                    // -25% until it physically reaches its new leader (cohesion range, pass 3b).
                    // Losing a leader hurts for the whole retreat, not for one tick.
                    w.Leader[i] = lead;
                    w.SeekingSwarm[i] = false;
                    w.Settled[i] = false; // walk to a slot first, then rest
                    w.AttackMove[i] = false; // the leader drives it from here (posture flows down)
                    w.QueueCount[i] = 0;     // any leftover personal plan dies with autonomy
                    squad[lead]++;
                    w.HasMoveOrder[i] = false;
                    w.MoveTarget[i] = w.Pos[i];
                }
                else if (leaderless && !committed)
                {
                    w.MoveTarget[i] = w.Pos[lead];
                    w.HasMoveOrder[i] = true;
                }
            }

            // Pass 3a: linked sub-leaders are limbs of a larger swarm — each holds a station
            // abreast of its prime (alternating right/left) and inherits the prime's facing,
            // so one order to the prime moves the whole super-formation as one body.
            Fix slack = Fix.Ratio(60, 100);
            Fix regroupR = Fix.Ratio(w.Rules.RegroupRadiusCenti, 100);
            Fix linkSpacing = Fix.Ratio(w.Rules.LinkSpacingCenti, 100);
            int[] linkK = w.ScratchLinkCount;
            Array.Clear(linkK, 0, w.HighWater);
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Unit || !defs.Units[w.DefIndex[i]].IsLeader) continue;
                int prime = w.Leader[i];
                if (prime < 0) continue; // pass 1 guarantees any remaining link is valid
                // Attack posture flows down the tree: an attack-moving super-swarm releases
                // every squad in it to fight on contact.
                w.AttackMove[i] = w.AttackMove[prime];
                // A limb in attack posture with an enemy in reach fights instead of holding
                // station (no standing order → CombatSystem chases and attacks).
                if (w.AttackMove[i] && CombatSystem.EnemyInAcquireRange(w, defs, i))
                {
                    w.HasMoveOrder[i] = false;
                    w.Settled[i] = false;
                    continue;
                }
                int k = linkK[prime]++;
                FixVec2 pf = w.Facing[prime];
                if (pf.LengthSq == Fix.Zero) pf = new FixVec2(Fix.One, Fix.Zero);
                w.Facing[i] = pf; // the limb faces with the spine
                var pPerp = new FixVec2(-pf.Y, pf.X);

                // While the prime is marching, stations anchor at its DESTINATION — limbs path
                // straight to their final spots instead of chasing the spine mid-route (which
                // made sub-swarms take long indirect paths). When the prime stands, stations
                // ride its actual position so the body drifts and turns with it.
                bool primeMoving = w.HasMoveOrder[prime] || w.AttackMove[prime];
                FixVec2 anchor = primeMoving ? w.MoveTarget[prime] : w.Pos[prime];

                FixVec2 station;
                if (w.HasLimbStation[i])
                {
                    // Freeform station drawn by the player: a fixed offset in the prime's
                    // facing frame, so the drawn shape travels and turns with the spine.
                    station = w.ClampToMap(anchor + pf * w.LimbStation[i].X + pPerp * w.LimbStation[i].Y);
                }
                else
                {
                    Fix mag = linkSpacing * Fix.FromInt((k >> 1) + 1);
                    Fix side = (k & 1) == 0 ? mag : -mag;
                    station = w.ClampToMap(anchor + pPerp * side);
                }

                // Rest-until-the-spine-moves: a settled limb ignores small drift; it wakes when
                // the prime gets an order or the limb is knocked far off station.
                Fix stationSq = (station - w.Pos[i]).LengthSq;
                if (primeMoving || stationSq > regroupR * regroupR) w.Settled[i] = false;
                if (w.Settled[i]) { w.HasMoveOrder[i] = false; continue; }

                if (stationSq > slack * slack)
                {
                    w.MoveTarget[i] = station;
                    w.HasMoveOrder[i] = true;
                }
                else
                {
                    w.HasMoveOrder[i] = false;
                    if (!primeMoving) w.Settled[i] = true;
                }
            }

            // Pass 3b: steer squad members onto formation slots. The slot pattern is centered
            // on the leader — the body forms AROUND the spine, not in front of it — by
            // subtracting the mean slot offset. Within the slack radius the order is released
            // so auto-engagement (CombatSystem) takes over.
            for (int lead = 0; lead < w.HighWater; lead++)
            {
                if (w.Kind[lead] != EntityKind.Unit || squad[lead] == 0) continue;
                if (!defs.Units[w.DefIndex[lead]].IsLeader) continue;
                int U = w.UnitDefCount;
                FixVec2 f = w.Facing[lead];
                if (f.LengthSq == Fix.Zero) f = new FixVec2(Fix.One, Fix.Zero);
                var perp = new FixVec2(-f.Y, f.X);

                // Encircle stance: this squad wraps the nearest enemy in anchor range. Non-Guard
                // members ring IT (Rear holds the near face as a firing block); the Guard zone
                // always stays on the leader. No target in range → normal zone layout.
                int anchorEnemy = -1;
                FixVec2 fe = f, pe = perp, anchorPos = w.Pos[lead];
                if (w.Stance[lead])
                {
                    anchorEnemy = NearestEnemy(w, w.Owner[lead], w.Pos[lead], Fix.Ratio(w.Rules.EnemyAnchorRangeCenti, 100));
                    if (anchorEnemy >= 0)
                    {
                        anchorPos = w.Pos[anchorEnemy];
                        FixVec2 d = anchorPos - w.Pos[lead];
                        Fix len = d.Length;
                        if (len.Raw > 0)
                        {
                            fe = new FixVec2(d.X / len, d.Y / len);
                            pe = new FixVec2(-fe.Y, fe.X);
                        }
                    }
                }

                int[] bandN = w.ScratchBandCount;   // per-zone member counts
                int[] bandK = w.ScratchBandCursor;  // per-zone slot cursors
                int spread = 0, ringK = 0;          // Spread interleave + encircle-ring cursors

                // The placement matrix decides each member's zone; Spread interleaves its
                // members alternately through Front and Rear. Cursors reset per scan so the
                // mean and placement scans resolve identical slots.
                int ZoneOfMember(int i)
                {
                    int z = w.ZoneMatrix[lead * U + w.DefIndex[i]];
                    if (z == SimConstants.ZoneSpread)
                    {
                        z = (spread & 1) == 0 ? SimConstants.ZoneFront : SimConstants.ZoneRear;
                        spread++;
                    }
                    return z;
                }

                Array.Clear(bandN, 0, SimConstants.ZoneCount);
                Array.Clear(bandK, 0, SimConstants.ZoneCount);
                for (int i = 0; i < w.HighWater; i++)
                    if (IsMemberOf(w, defs, i, lead))
                        bandN[ZoneOfMember(i)]++;

                // Zone slot math (layout tuned via rules zone* keys; identical k-order across scans).
                var rules = w.Rules;
                void Offsets(int zone, int k, out Fix fwd, out Fix perpOff)
                {
                    switch (zone)
                    {
                        case SimConstants.ZoneFront:
                        case SimConstants.ZoneRear:
                        {
                            // Ranked block: Front marches ahead of the spine, Rear behind it.
                            bool front = zone == SimConstants.ZoneFront;
                            int perRow = rules.ZoneRowWidth > 0 ? rules.ZoneRowWidth : 6;
                            int rank = k / perRow, file = k % perRow;
                            int rowCount = System.Math.Min(perRow, bandN[zone] - rank * perRow);
                            fwd = Fix.Ratio(front ? rules.ZoneFrontForwardCenti : rules.ZoneRearForwardCenti, 100)
                                - Fix.Ratio(rules.ZoneRankSpacingCenti, 100) * Fix.FromInt(rank);
                            perpOff = Fix.FromRaw(Fix.Ratio(rules.ZoneSpacingCenti, 100).Raw * (2 * file - (rowCount - 1)) / 2);
                            return;
                        }
                        case SimConstants.ZoneFlanks:
                        {
                            // Mirrored at both ends of the line, pairs stepping outward.
                            Fix mag = Fix.Ratio(rules.ZoneFlankSideCenti, 100)
                                + Fix.Ratio(rules.ZoneSpacingCenti, 100) * Fix.FromInt(k >> 1);
                            fwd = Fix.Ratio(60, 100);
                            perpOff = (k & 1) == 0 ? mag : -mag;
                            return;
                        }
                        default: // ZoneGuard — shells ringing the leader.
                        {
                            FixVec2 dir = SimWorld.RingDir(k);
                            Fix radius = Fix.Ratio(rules.ZoneGuardRadiusCenti, 100)
                                + Fix.Ratio(rules.ZoneGuardGapCenti, 100) * Fix.FromInt(k >> 3);
                            fwd = dir.X * radius;
                            perpOff = dir.Y * radius;
                            return;
                        }
                    }
                }

                // Encircle slots, anchored on the enemy in the squad→enemy frame.
                FixVec2 EncircleSlot(int zone, int k)
                {
                    if (zone == SimConstants.ZoneRear)
                    {
                        int perRow = rules.ZoneRowWidth > 0 ? rules.ZoneRowWidth : 6;
                        int rank = k / perRow, file = k % perRow;
                        int rowCount = System.Math.Min(perRow, bandN[zone] - rank * perRow);
                        Fix bf = Fix.Ratio(-170, 100) - Fix.Ratio(rules.ZoneRankSpacingCenti, 100) * Fix.FromInt(rank);
                        Fix bp = Fix.FromRaw(Fix.Ratio(rules.ZoneSpacingCenti, 100).Raw * (2 * file - (rowCount - 1)) / 2);
                        return w.ClampToMap(anchorPos + fe * bf + pe * bp);
                    }
                    FixVec2 dir = SimWorld.RingDir(k);
                    Fix radius = Fix.Ratio(220, 100) + Fix.Ratio(90, 100) * Fix.FromInt(k >> 3);
                    return w.ClampToMap(anchorPos + fe * (dir.X * radius) + pe * (dir.Y * radius));
                }

                // Scan A: mean slot offset (the centroid of the body relative to the leader).
                // Enemy-anchored members (encircling) stay out of the centering math.
                Fix sumFwd = Fix.Zero, sumPerp = Fix.Zero;
                int meanCount = 0;
                spread = 0;
                ringK = 0;
                for (int i = 0; i < w.HighWater; i++)
                {
                    if (!IsMemberOf(w, defs, i, lead)) continue;
                    int z = ZoneOfMember(i);
                    if (anchorEnemy >= 0 && z != SimConstants.ZoneGuard)
                    {
                        if (z == SimConstants.ZoneRear) bandK[z]++; else ringK++;
                        continue;
                    }
                    Offsets(z, bandK[z]++, out Fix fwd, out Fix po);
                    sumFwd += fwd;
                    sumPerp += po;
                    meanCount++;
                }
                Fix meanFwd = meanCount > 0 ? Fix.FromRaw(sumFwd.Raw / meanCount) : Fix.Zero;
                Fix meanPerp = meanCount > 0 ? Fix.FromRaw(sumPerp.Raw / meanCount) : Fix.Zero;

                // Scan B: place, re-centered so the leader sits at the body's centroid.
                // Members REST once they arrive (or physically touch the leader) while the
                // leader is standing — no re-steering against collision jostle — and wake when
                // the leader moves again or they get knocked far off their slot.
                bool leadMoving = w.HasMoveOrder[lead] || w.AttackMove[lead];
                bool attackPosture = w.AttackMove[lead];
                Fix leadR = Fix.Ratio(defs.Units[w.DefIndex[lead]].CollisionRadiusCenti, 100);
                Array.Clear(bandK, 0, SimConstants.ZoneCount);
                spread = 0;
                ringK = 0;
                Fix cohesionR = Fix.Ratio(w.Rules.SquadCohesionRadiusCenti, 100);
                for (int i = 0; i < w.HighWater; i++)
                {
                    if (!IsMemberOf(w, defs, i, lead)) continue;
                    int z = ZoneOfMember(i);
                    bool encircling = anchorEnemy >= 0 && z != SimConstants.ZoneGuard;
                    int k = encircling && z != SimConstants.ZoneRear ? ringK++ : bandK[z]++;

                    // A rejoined survivor sheds the leaderless penalty only on ARRIVAL —
                    // once it's back within cohesion range of its new leader.
                    if (w.Leaderless[i] && (w.Pos[lead] - w.Pos[i]).LengthSq <= cohesionR * cohesionR)
                        w.Leaderless[i] = false;

                    // Attack posture overrides formation-keeping on contact: a member with an
                    // enemy in acquire range is released so CombatSystem chases and fights.
                    // (A plain move keeps formation strictly — units march past enemies.)
                    if (attackPosture && CombatSystem.EnemyInAcquireRange(w, defs, i))
                    {
                        w.HasMoveOrder[i] = false;
                        w.Settled[i] = false;
                        continue;
                    }

                    FixVec2 slot;
                    if (encircling)
                    {
                        slot = EncircleSlot(z, k);
                    }
                    else
                    {
                        Offsets(z, k, out Fix fwd, out Fix po);
                        slot = w.ClampToMap(w.Pos[lead] + f * (fwd - meanFwd) + perp * (po - meanPerp));
                    }

                    Fix slotSq = (slot - w.Pos[i]).LengthSq;
                    if (leadMoving || slotSq > regroupR * regroupR) w.Settled[i] = false;
                    if (w.Settled[i]) { w.HasMoveOrder[i] = false; continue; }

                    if (slotSq > slack * slack)
                    {
                        // Touching the standing leader counts as arrived — rest on contact.
                        Fix touch = leadR + Fix.Ratio(defs.Units[w.DefIndex[i]].CollisionRadiusCenti + 10, 100);
                        if (!leadMoving && (w.Pos[lead] - w.Pos[i]).LengthSq <= touch * touch)
                        {
                            w.HasMoveOrder[i] = false;
                            w.Settled[i] = true;
                        }
                        else
                        {
                            w.MoveTarget[i] = slot;
                            w.HasMoveOrder[i] = true;
                        }
                    }
                    else
                    {
                        w.HasMoveOrder[i] = false;
                        if (!leadMoving) w.Settled[i] = true;
                    }
                }
            }
        }

        /// <summary>A non-leader unit in lead's squad (sub-leaders are limbs, not members).</summary>
        private static bool IsMemberOf(SimWorld w, DefDatabase defs, int i, int lead) =>
            w.Kind[i] == EntityKind.Unit && w.Leader[i] == lead && !defs.Units[w.DefIndex[i]].IsLeader;

        /// <summary>Nearest enemy unit/building within maxR of a point (lowest index on ties) —
        /// the target an enemy-anchored formation acts on.</summary>
        private static int NearestEnemy(SimWorld w, byte owner, FixVec2 from, Fix maxR)
        {
            int best = -1;
            Fix bestSq = Fix.Zero;
            Fix maxSq = maxR * maxR;
            int cx0 = w.GridClampX((int)((from.X - maxR).Raw >> SimWorld.GridShift));
            int cx1 = w.GridClampX((int)((from.X + maxR).Raw >> SimWorld.GridShift));
            int cy0 = w.GridClampY((int)((from.Y - maxR).Raw >> SimWorld.GridShift));
            int cy1 = w.GridClampY((int)((from.Y + maxR).Raw >> SimWorld.GridShift));
            for (int cy = cy0; cy <= cy1; cy++)
            for (int cx = cx0; cx <= cx1; cx++)
            for (int j = w.GridHead[cy * w.GridCellsX + cx]; j >= 0; j = w.GridNext[j])
            {
                if (w.Kind[j] != EntityKind.Unit && w.Kind[j] != EntityKind.Building) continue;
                if (!w.AreEnemies(owner, w.Owner[j])) continue; // allies and neutrals aren't targets
                Fix dsq = (w.Pos[j] - from).LengthSq;
                if (dsq > maxSq) continue;
                if (best < 0 || dsq < bestSq || (dsq == bestSq && j < best)) { best = j; bestSq = dsq; }
            }
            return best;
        }

        public static int NearestLeaderWithCapacity(SimWorld w, DefDatabase defs, int self, int[] squadCounts)
        {
            int best = -1;
            Fix bestSq = Fix.Zero;
            for (int i = 0; i < w.HighWater; i++)
            {
                if (i == self || w.Kind[i] != EntityKind.Unit || w.Owner[i] != w.Owner[self]) continue;
                if (!defs.Units[w.DefIndex[i]].IsLeader) continue;
                if (squadCounts[i] >= w.Rules.MaxUnitsPerLeader) continue;
                Fix dsq = (w.Pos[i] - w.Pos[self]).LengthSq;
                if (best < 0 || dsq < bestSq) { best = i; bestSq = dsq; }
            }
            return best;
        }

        /// <summary>Recomputes squad counts before searching — for rare paths (production).</summary>
        public static int NearestLeaderWithCapacityFresh(SimWorld w, DefDatabase defs, int self)
        {
            int[] squad = RecountSquads(w, defs);
            return NearestLeaderWithCapacity(w, defs, self, squad);
        }

        /// <summary>Nearest friendly leader with room within maxDist of a point (rally-to-swarm).</summary>
        public static int NearestLeaderWithCapacityNearPoint(SimWorld w, DefDatabase defs, byte owner, FixVec2 point, Fix maxDist)
        {
            int[] squad = RecountSquads(w, defs);
            int best = -1;
            Fix bestSq = Fix.Zero;
            Fix maxSq = maxDist * maxDist;
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Unit || w.Owner[i] != owner) continue;
                if (!defs.Units[w.DefIndex[i]].IsLeader) continue;
                if (squad[i] >= w.Rules.MaxUnitsPerLeader) continue;
                Fix dsq = (w.Pos[i] - point).LengthSq;
                if (dsq > maxSq) continue;
                if (best < 0 || dsq < bestSq) { best = i; bestSq = dsq; }
            }
            return best;
        }

        private static int[] RecountSquads(SimWorld w, DefDatabase defs)
        {
            // Members only — linked sub-leaders (limbs) must NOT consume member capacity,
            // matching pass 1 and the AssignToLeader command. Counting them made primes read
            // as full and silently blocked production auto-assimilation.
            int[] squad = w.ScratchSquadCount;
            Array.Clear(squad, 0, w.HighWater);
            for (int i = 0; i < w.HighWater; i++)
                if (w.Kind[i] == EntityKind.Unit && w.Leader[i] >= 0 && w.Leader[i] < w.HighWater
                    && !defs.Units[w.DefIndex[i]].IsLeader)
                    squad[w.Leader[i]]++;
            return squad;
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
                        if (bd.IsHeadquarters || bd.HubBuilt) w.ScratchDropoffs[w.ScratchDropoffCount++] = i;
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
        /// Turn a unit's body facing toward a direction at its def's turn speed. Leaders only
        /// turn when their facing isn't HELD — an explicitly ordered front (drag-line or
        /// Shift+R-drag) survives the march; otherwise the whole squad orients along its
        /// travel direction. Facing is hashed state and drives directional damage.
        /// </summary>
        public static void FaceToward(SimWorld w, DefDatabase defs, int i, FixVec2 toward)
        {
            var def = defs.Units[w.DefIndex[i]];
            // Leaders keep an explicitly ordered front; linked limbs inherit their prime's
            // facing (pass 3a) — neither turns toward mere travel.
            if (def.IsLeader && (w.FacingHeld[i] || w.Leader[i] >= 0)) return;
            Fix chord = Fix.Ratio(def.TurnSpeedCenti, 100 * SimConstants.TicksPerSecond);
            w.Facing[i] = FixVec2.TurnTowards(w.Facing[i], toward, chord);
        }

        /// <summary>Per-tick step for a specific unit, with the leaderless penalty folded in
        /// as a single rational so there is still only one floor.</summary>
        public static Fix StepFor(SimWorld w, DefDatabase defs, int e)
        {
            var def = defs.Units[w.DefIndex[e]];
            long num = 1, den = 1;
            if (w.Leaderless[e]) { num *= w.Rules.LeaderlessPenaltyNum; den *= w.Rules.LeaderlessPenaltyDen; }
            UpgradeSystem.Fold(w, defs, w.Owner[e], w.DefIndex[e], UpgradeStat.MoveSpeed, ref num, ref den);
            // Combine every rational factor, then divide once (one floor).
            return Fix.Ratio((long)def.MoveSpeedCenti * num, 100L * SimConstants.TicksPerSecond * den);
        }

        public static void Tick(SimWorld w, DefDatabase defs)
        {
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Unit || !w.HasMoveOrder[i]) continue;
                Fix step = StepFor(w, defs, i);
                long cap = w.ScratchStepCap[i];
                if (cap > 0 && cap < step.Raw) step = Fix.FromRaw(cap); // arrive-as-one pacing
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
    /// order. Leaderless units attack 25% slower (longer cooldown, one integer floor).
    /// </summary>
    public static class CombatSystem
    {
        public static int CooldownFor(SimWorld w, DefDatabase defs, int e)
        {
            var def = defs.Units[w.DefIndex[e]];
            long num = 1, den = 1;
            // Leaderless attacks are SLOWER: cooldown ×Den/Num (penalty rational inverted).
            if (w.Leaderless[e]) { num *= w.Rules.LeaderlessPenaltyDen; den *= w.Rules.LeaderlessPenaltyNum; }
            // Attack-speed upgrades shorten the cooldown (their Num<Den).
            UpgradeSystem.Fold(w, defs, w.Owner[e], w.DefIndex[e], UpgradeStat.AttackSpeed, ref num, ref den);
            return (int)((long)def.AttackCooldownTicks * num / den);
        }

        /// <summary>Any enemy unit/building within this unit's acquire reach? Used by the swarm
        /// system to release squad members from formation-keeping when their squad is in
        /// attack posture — the same range combat targeting uses, so a released unit always
        /// has something to fight.</summary>
        public static bool EnemyInAcquireRange(SimWorld w, DefDatabase defs, int e)
        {
            var def = defs.Units[w.DefIndex[e]];
            if (def.AttackDamage <= 0) return false;
            Fix selfR = Fix.Ratio(def.CollisionRadiusCenti, 100);
            Fix acquire = Fix.Ratio(UpgradeSystem.ScaleCenti(w, defs, w.Owner[e], w.DefIndex[e], def.AcquireRangeCenti, UpgradeStat.AcquireRange), 100);
            Fix reach = acquire + selfR + w.MaxInteractRadius;
            int cx0 = w.GridClampX((int)((w.Pos[e].X - reach).Raw >> SimWorld.GridShift));
            int cx1 = w.GridClampX((int)((w.Pos[e].X + reach).Raw >> SimWorld.GridShift));
            int cy0 = w.GridClampY((int)((w.Pos[e].Y - reach).Raw >> SimWorld.GridShift));
            int cy1 = w.GridClampY((int)((w.Pos[e].Y + reach).Raw >> SimWorld.GridShift));
            for (int cy = cy0; cy <= cy1; cy++)
            for (int cx = cx0; cx <= cx1; cx++)
            for (int j = w.GridHead[cy * w.GridCellsX + cx]; j >= 0; j = w.GridNext[j])
            {
                if (j == e || !w.AreEnemies(w.Owner[e], w.Owner[j])) continue;
                if (w.Kind[j] != EntityKind.Unit && w.Kind[j] != EntityKind.Building) continue;
                Fix maxD = acquire + selfR + CollisionSystem.RadiusOf(w, defs, j);
                if ((w.Pos[j] - w.Pos[e]).LengthSq <= maxD * maxD) return true;
            }
            return false;
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
        /// Effective attack damage against a specific target. Two rational factors combined
        /// with ONE integer floor (iron rule): the squad bonus (in a squad AND within cohesion
        /// range of the leader), and the directional multiplier — unit victims struck from the
        /// side or rear take extra damage based on THEIR facing vs the attacker's position.
        /// Buildings have no facing and take flat damage. Turn speed + formation facing now
        /// decide fights: a pincer's flanks genuinely hit harder.
        /// </summary>
        public static int DamageOf(SimWorld w, DefDatabase defs, int e, int target)
        {
            // Base attack, plus the owner's standing attack structures. Added BEFORE the
            // rationals, so it is a genuine stat boost that squad/arc bonuses scale too.
            // Unarmed units never reach here (the combat loop skips AttackDamage <= 0), so
            // this can't hand workers a weapon.
            int dmg = defs.Units[w.DefIndex[e]].AttackDamage;
            if (w.Owner[e] < w.ScratchAttackBonus.Length) dmg += w.ScratchAttackBonus[w.Owner[e]];
            long num = 1, den = 1;

            int lead = w.Leader[e];
            if (lead >= 0)
            {
                Fix r = Fix.Ratio(w.Rules.SquadCohesionRadiusCenti, 100);
                if ((w.Pos[lead] - w.Pos[e]).LengthSq <= r * r)
                {
                    num *= w.Rules.SquadDamageBonusNum;
                    den *= w.Rules.SquadDamageBonusDen;
                }
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
                    // No enemy in range. An attack-moving unit (not a squad member — its leader
                    // drives it) advances toward its destination; when it arrives the order ends.
                    if (w.AttackMove[i] && !w.HasMoveOrder[i] && w.Leader[i] < 0)
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

            // ---- TIERED CACHE DEFENSE: an upgraded supply cache (Tier >= 1) fires a ranged
            // shot at the nearest enemy in range. Static defense with flat rules-driven damage
            // — no arcs, squad bonuses, or supply modifiers.
            Fix cacheRange = Fix.Ratio(w.Rules.CacheAttackRangeCenti, 100);
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Building || w.Tier[i] == 0 || w.ConstructionRemaining[i] > 0) continue;
                if (w.AttackCooldown[i] > 0) { w.AttackCooldown[i]--; continue; }

                Fix selfR = Fix.Ratio(defs.Buildings[w.DefIndex[i]].CollisionRadiusCenti, 100);
                int target = -1;
                Fix bestSq = Fix.Zero;
                Fix reach0 = cacheRange + selfR + w.MaxInteractRadius;
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
                    Fix maxD = cacheRange + selfR + CollisionSystem.RadiusOf(w, defs, j);
                    Fix dsq = (w.Pos[j] - w.Pos[i]).LengthSq;
                    if (dsq > maxD * maxD) continue;
                    if (target < 0 || dsq < bestSq || (dsq == bestSq && j < target)) { target = j; bestSq = dsq; }
                }
                if (target < 0) continue;
                Hit(w, defs, i, target, w.Rules.CacheAttackDamage); // an armed cache earns bounty too
                w.AttackCooldown[i] = w.Rules.CacheAttackCooldownTicks;
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
            int[] queue = w.ScratchRoot; // reused scratch; SwarmSystem is done with it this tick
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
