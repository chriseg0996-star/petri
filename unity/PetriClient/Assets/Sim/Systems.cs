using System;

namespace Petri.Core
{
    /// <summary>
    /// Automated production: buildings continuously produce whichever producible unit is
    /// furthest below its weight share (players set composition, not build orders). Units
    /// are COUNTS: a completed worker joins the owner's worker pool; a completed combat
    /// unit joins the Force of the building's rally front, or the emptiest front. Costs
    /// debit at selection. Deterministic: dense-index scans, cross-multiplied comparisons.
    /// </summary>
    public static class ProductionSystem
    {
        public static void Tick(SimWorld w, DefDatabase defs)
        {
            int u = defs.Units.Length;
            int[] counts = w.ScratchUnitCounts;
            Array.Clear(counts, 0, counts.Length);
            for (int p = 0; p < w.Players.Length; p++)
            {
                var pl = w.Players[p];
                for (int d = 0; d < u; d++)
                {
                    int total = 0;
                    for (int f = 0; f < SimConstants.MaxFronts; f++) total += pl.Force[f * u + d];
                    if (defs.Units[d].IsWorker) total += pl.WorkerCount;
                    counts[p * u + d] = total;
                }
            }
            for (int i = 0; i < w.HighWater; i++)
                if (w.Kind[i] == EntityKind.Building && w.ProduceChoice[i] >= 0)
                    counts[w.Owner[i] * u + w.ProduceChoice[i]]++;

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
                        if (player.Food >= defs.Units[w.ProduceOverride[i]].FoodCost)
                            best = w.ProduceOverride[i];
                    }
                    else
                    {
                        // Auto: pick the affordable candidate maximizing weight/(count+1);
                        // cross-multiplied so there is no division floor at all.
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
                    int d = w.ProduceChoice[i];
                    if (udef.IsWorker)
                    {
                        player.WorkerCount++;
                    }
                    else
                    {
                        // Rally front if set and valid, else the emptiest front (lowest wins).
                        int k = player.FrontCount;
                        int front = w.RallyFront[i] >= 0 && w.RallyFront[i] < k ? w.RallyFront[i] : -1;
                        if (front < 0)
                        {
                            front = 0;
                            int bestTotal = int.MaxValue;
                            for (int f = 0; f < k; f++)
                            {
                                int total = 0;
                                for (int dd = 0; dd < u; dd++) total += player.Force[f * u + dd];
                                if (total < bestTotal) { bestTotal = total; front = f; }
                            }
                        }
                        player.Force[front * u + d]++;
                    }
                    w.ProduceChoice[i] = -1;
                    w.ProduceProgress[i] = 0;
                }
            }
        }
    }

    /// <summary>Construction sites self-build at Rules.HubBuildRate work units per tick
    /// (sites carry 3× BuildTimeTicks work units, so rate 3 finishes in BuildTimeTicks).</summary>
    public static class ConstructionSystem
    {
        public static void Tick(SimWorld w, DefDatabase defs)
        {
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Building || w.ConstructionRemaining[i] <= 0) continue;
                w.ConstructionRemaining[i] -= w.Rules.HubBuildRate;
                if (w.ConstructionRemaining[i] < 0) w.ConstructionRemaining[i] = 0;
            }
        }
    }

    /// <summary>
    /// HARVEST: every slow beat each organism passively mines the nodes inside its
    /// territory. The rate scales with the worker pool and drains nodes ascending by
    /// index, capped per node per beat so fields deplete gradually. Dry nodes despawn.
    /// </summary>
    public static class HarvestSystem
    {
        public static void Tick(SimWorld w, DefDatabase defs)
        {
            for (int p = 0; p < w.Players.Length; p++)
            {
                var pl = w.Players[p];
                if (!pl.Alive) continue;
                int rate = w.Rules.HarvestBasePerBeat + pl.WorkerCount * w.Rules.HarvestPerWorker;
                for (int i = 0; i < w.HighWater && rate > 0; i++)
                {
                    if (w.Kind[i] != EntityKind.Node || w.NodeFood[i] <= 0) continue;
                    if (w.Territory[w.CellOfPos(w.Pos[i])] != p) continue; // engulfed nodes only
                    int take = w.NodeFood[i];
                    if (take > rate) take = rate;
                    if (take > w.Rules.HarvestNodeCapPerBeat) take = w.Rules.HarvestNodeCapPerBeat;
                    w.NodeFood[i] -= take;
                    if (w.NodeMineral[i]) pl.Minerals += take; else pl.Food += take;
                    rate -= take;
                    if (w.NodeFood[i] <= 0) w.Despawn(i); // pile consumed
                }
            }
        }
    }

    /// <summary>
    /// TERRITORY GROWTH: on each growth beat every living organism claims neutral, ownable
    /// cells 4-adjacent to its territory. Fronts with a PUSH order spend budget first,
    /// claiming the candidate cells nearest their push target (directed expansion); the
    /// remaining budget grows EVENLY — every claim takes the frontier cell nearest the
    /// organism's centroid, so the body rounds outward in rings from its starting point
    /// instead of following the cell scan order. Deterministic: integer math, argmin with
    /// (distance, index) tie-break.
    /// </summary>
    public static class GrowthSystem
    {
        public static void Tick(SimWorld w, DefDatabase defs)
        {
            int cells = w.CellCount;
            if (cells == 0) return;
            int cx0 = w.TerritoryCellsX;

            for (byte p = 0; p < w.Players.Length; p++)
            {
                var pl = w.Players[p];
                if (!pl.Alive) continue;
                int budget = w.Rules.GrowthBasePerBeat + pl.WorkerCount / w.Rules.GrowthWorkerDivisor;

                // PUSH PASS: each pushing front grabs the claimable cells nearest its
                // target first. Sector data comes from the geometry pass that ran just
                // before growth this beat.
                for (int s = 0; s < pl.FrontCount && budget > 0; s++)
                {
                    if (pl.FrontPushX[s] < 0) continue;
                    int n = 0;
                    for (int c = 0; c < cells && n < SimWorld.CandidateCap; c++)
                    {
                        if (w.Territory[c] != SimWorld.NeutralOwner || w.TerritoryBlocked[c]) continue;
                        int x = c % cx0, y = c / cx0;
                        // Adjacent to own territory, and the adjacent OWN cell is in sector s.
                        bool adj = (x > 0 && w.Territory[c - 1] == p && w.ScratchCellSector[c - 1] == s)
                            || (x < cx0 - 1 && w.Territory[c + 1] == p && w.ScratchCellSector[c + 1] == s)
                            || (y > 0 && w.Territory[c - cx0] == p && w.ScratchCellSector[c - cx0] == s)
                            || (y < w.TerritoryCellsY - 1 && w.Territory[c + cx0] == p && w.ScratchCellSector[c + cx0] == s);
                        if (!adj) continue;
                        w.CellCenterCenti(c, out int ccx, out int ccy);
                        long dx = ccx - pl.FrontPushX[s], dy = ccy - pl.FrontPushY[s];
                        w.ScratchCandCell[n] = c;
                        w.ScratchCandDist[n] = dx * dx + dy * dy;
                        n++;
                    }
                    SortCandidates(w, n);
                    for (int i = 0; i < n && budget > 0; i++)
                    {
                        w.Territory[w.ScratchCandCell[i]] = p;
                        budget--;
                    }
                    // Push completes when the target cell itself joins the organism.
                    int targetCell = w.CellOfCenti(pl.FrontPushX[s], pl.FrontPushY[s]);
                    if (w.Territory[targetCell] == p)
                    {
                        pl.FrontPushX[s] = -1;
                        pl.FrontPushY[s] = -1;
                    }
                }

                // EVEN PASS: each remaining claim takes the frontier cell NEAREST the
                // organism's centroid (geometry ran just before growth this beat), so
                // the body rounds outward evenly and fills concavities first. Claims
                // cascade within the beat — a claimed cell's neighbors join the
                // frontier immediately, so chokepoints don't stall the budget.
                int centX = w.ScratchCentXCenti[p], centY = w.ScratchCentYCenti[p];
                while (budget > 0)
                {
                    int best = -1;
                    long bestD = long.MaxValue;
                    for (int c = 0; c < cells; c++)
                    {
                        if (w.Territory[c] != SimWorld.NeutralOwner || w.TerritoryBlocked[c]) continue;
                        int x = c % cx0, y = c / cx0;
                        bool adj = (x > 0 && w.Territory[c - 1] == p)
                            || (x < cx0 - 1 && w.Territory[c + 1] == p)
                            || (y > 0 && w.Territory[c - cx0] == p)
                            || (y < w.TerritoryCellsY - 1 && w.Territory[c + cx0] == p);
                        if (!adj) continue;
                        w.CellCenterCenti(c, out int ccx, out int ccy);
                        long dx = ccx - centX, dy = ccy - centY;
                        long d = dx * dx + dy * dy;
                        if (d < bestD) { bestD = d; best = c; }
                    }
                    if (best < 0) break; // nowhere left to grow
                    w.Territory[best] = p;
                    budget--;
                }
            }
        }

        /// <summary>Insertion-sort the world's candidate workspace by (dist, cellIndex):
        /// small n, no allocation, stable across platforms.</summary>
        public static void SortCandidates(SimWorld w, int n)
        {
            for (int i = 1; i < n; i++)
            {
                int cc = w.ScratchCandCell[i];
                long dd = w.ScratchCandDist[i];
                int j = i - 1;
                while (j >= 0 && (w.ScratchCandDist[j] > dd || (w.ScratchCandDist[j] == dd && w.ScratchCandCell[j] > cc)))
                {
                    w.ScratchCandCell[j + 1] = w.ScratchCandCell[j];
                    w.ScratchCandDist[j + 1] = w.ScratchCandDist[j];
                    j--;
                }
                w.ScratchCandCell[j + 1] = cc;
                w.ScratchCandDist[j + 1] = dd;
            }
        }
    }

    /// <summary>
    /// FRONTS: each organism's border divides into K equal angular sectors around its
    /// centroid (FrontMath). TickGeometry recomputes centroids, per-cell sectors, and
    /// contested flags into scratch (growth and combat both read them); TickForces then
    /// REDEPLOYS force — per unit def, up to moveSpeed/RedeploySpeedDivisor transfers flow
    /// from the fullest front toward the emptiest eligible front (contested fronts first),
    /// so fast units answer a probe quicker than slow ones. Combat lands in its own pass.
    /// </summary>
    public static class FrontSystem
    {
        public static void TickGeometry(SimWorld w, DefDatabase defs)
        {
            int cells = w.CellCount;
            if (cells == 0) return;
            int cx0 = w.TerritoryCellsX;
            int players = w.Players.Length;
            Array.Clear(w.ScratchFrontContested, 0, w.ScratchFrontContested.Length);
            Array.Clear(w.ScratchCentSumX, 0, players);
            Array.Clear(w.ScratchCentSumY, 0, players);
            Array.Clear(w.ScratchCentCount, 0, players);

            for (int c = 0; c < cells; c++)
            {
                byte o = w.Territory[c];
                if (o >= players) continue;
                w.ScratchCentSumX[o] += c % cx0;
                w.ScratchCentSumY[o] += c / cx0;
                w.ScratchCentCount[o]++;
            }
            for (int p = 0; p < players; p++)
            {
                int n = w.ScratchCentCount[p];
                if (n == 0) continue;
                w.ScratchCentXCenti[p] = (int)(w.ScratchCentSumX[p] * SimWorld.CellCenti / n) + SimWorld.CellCenti / 2;
                w.ScratchCentYCenti[p] = (int)(w.ScratchCentSumY[p] * SimWorld.CellCenti / n) + SimWorld.CellCenti / 2;
            }

            for (int c = 0; c < cells; c++)
            {
                byte o = w.Territory[c];
                if (o >= players) { w.ScratchCellSector[c] = 0; continue; }
                int x = c % cx0, y = c / cx0;
                w.CellCenterCenti(c, out int ccx, out int ccy);
                int sector = FrontMath.Sector(w.Players[o].FrontCount,
                    ccx - w.ScratchCentXCenti[o], ccy - w.ScratchCentYCenti[o]);
                w.ScratchCellSector[c] = (byte)sector;

                bool enemyAdj =
                    (x > 0 && IsEnemyCell(w, o, w.Territory[c - 1]))
                    || (x < cx0 - 1 && IsEnemyCell(w, o, w.Territory[c + 1]))
                    || (y > 0 && IsEnemyCell(w, o, w.Territory[c - cx0]))
                    || (y < w.TerritoryCellsY - 1 && IsEnemyCell(w, o, w.Territory[c + cx0]));
                if (enemyAdj) w.ScratchFrontContested[o * SimConstants.MaxFronts + sector] = true;
            }
        }

        public static void TickForces(SimWorld w, DefDatabase defs)
        {
            int u = w.UnitDefCount;
            for (int p = 0; p < w.Players.Length; p++)
            {
                var pl = w.Players[p];
                if (!pl.Alive) continue;
                int k = pl.FrontCount;
                bool anyContested = false;
                for (int f = 0; f < k; f++)
                    if (w.ScratchFrontContested[p * SimConstants.MaxFronts + f]) { anyContested = true; break; }

                for (int d = 0; d < u; d++)
                {
                    int moves = Math.Max(1, defs.Units[d].MoveSpeedCenti / w.Rules.RedeploySpeedDivisor);
                    for (int m = 0; m < moves; m++)
                    {
                        int src = 0, dst = -1;
                        for (int f = 1; f < k; f++)
                            if (pl.Force[f * u + d] > pl.Force[src * u + d]) src = f;
                        for (int f = 0; f < k; f++)
                        {
                            if (anyContested && !w.ScratchFrontContested[p * SimConstants.MaxFronts + f]) continue;
                            if (dst < 0 || pl.Force[f * u + d] < pl.Force[dst * u + d]) dst = f;
                        }
                        if (dst < 0 || src == dst || pl.Force[src * u + d] - pl.Force[dst * u + d] < 2) break;
                        pl.Force[src * u + d]--;
                        pl.Force[dst * u + d]++;
                    }
                }
            }
        }

        /// <summary>
        /// FRONT COMBAT, each growth beat: (1) breakthrough windows tick down; (2) contacts —
        /// the distinct enemy fronts each front touches — build from one ascending cell scan;
        /// (3) per-front stats FREEZE from the force block (role triangle: melee attack =
        /// push power, ranged attack = defensive fire, HP = hold) plus finished buildings;
        /// (4) fronts in contact EXCHANGE fire in (player, front, entry) order — damage pools
        /// per defending front and kills consume it cheapest unit first, paying the killer
        /// evo + bounty; a front whose defenders all perish while contested BREAKS open;
        /// (5) PUSHING fronts convert their push-vs-hold advantage into cell flips, nearest
        /// the push target first — buildings soak flips, broken defenders multiply them.
        /// </summary>
        public static void TickCombat(SimWorld w, DefDatabase defs)
        {
            int cells = w.CellCount;
            if (cells == 0) return;
            int cx0 = w.TerritoryCellsX;
            int players = w.Players.Length;
            int u = w.UnitDefCount;
            const int MF = SimConstants.MaxFronts;

            for (int p = 0; p < players; p++)
            {
                var pl = w.Players[p];
                for (int f = 0; f < MF; f++)
                {
                    pl.FrontBrokenTicks[f] -= w.Rules.GrowthBeatTicks;
                    if (pl.FrontBrokenTicks[f] < 0) pl.FrontBrokenTicks[f] = 0;
                }
            }

            // ---- CONTACTS: check east and north neighbors so each adjacent pair is seen once.
            Array.Clear(w.ScratchContactCount, 0, w.ScratchContactCount.Length);
            for (int c = 0; c < cells; c++)
            {
                if (w.Territory[c] >= players) continue;
                int x = c % cx0, y = c / cx0;
                if (x < cx0 - 1) MaybeContact(w, c, c + 1);
                if (y < w.TerritoryCellsY - 1) MaybeContact(w, c, c + cx0);
            }

            // ---- FROZEN STATS (everything below reads these, nothing rewrites them).
            for (byte p = 0; p < players; p++)
            {
                var pl = w.Players[p];
                int bonus = 0;
                for (int i = 0; i < w.HighWater; i++)
                    if (w.Kind[i] == EntityKind.Building && w.Owner[i] == p && w.ConstructionRemaining[i] == 0)
                        bonus += defs.Buildings[w.DefIndex[i]].AttackBonus;
                for (int f = 0; f < MF; f++)
                {
                    int melee = 0, ranged = 0;
                    long holdHp = 0;
                    if (pl.Alive && f < pl.FrontCount)
                    {
                        for (int d = 0; d < u; d++)
                        {
                            int cnt = pl.Force[f * u + d];
                            if (cnt == 0) continue;
                            var ud = defs.Units[d];
                            holdHp += (long)cnt * ud.MaxHp;
                            if (ud.AttackDamage <= 0) continue;
                            int dmg = cnt * (ud.AttackDamage + bonus);
                            if (ud.ProjectileSpeedCenti > 0) ranged += dmg; else melee += dmg;
                        }
                    }
                    w.ScratchFrontMelee[p * MF + f] = melee;
                    w.ScratchFrontRanged[p * MF + f] = ranged;
                    w.ScratchFrontHold[p * MF + f] = (int)(holdHp / w.Rules.HoldHpDivisor);
                }
                if (!pl.Alive) continue;
                // Armed buildings garrison their own sector: defensive fire plus hold.
                for (int i = 0; i < w.HighWater; i++)
                {
                    if (w.Kind[i] != EntityKind.Building || w.Owner[i] != p || w.ConstructionRemaining[i] > 0) continue;
                    int ad = defs.Buildings[w.DefIndex[i]].AttackDamage;
                    if (ad <= 0) continue;
                    int px = (int)((long)w.Pos[i].X.Raw * 100 / Fix.OneRaw);
                    int py = (int)((long)w.Pos[i].Y.Raw * 100 / Fix.OneRaw);
                    int s = FrontMath.Sector(pl.FrontCount, px - w.ScratchCentXCenti[p], py - w.ScratchCentYCenti[p]);
                    w.ScratchFrontRanged[p * MF + s] += ad;
                    w.ScratchFrontHold[p * MF + s] += ad;
                }
            }

            // ---- EXCHANGE.
            for (byte p = 0; p < players; p++)
            {
                var pl = w.Players[p];
                if (!pl.Alive) continue;
                for (int f = 0; f < pl.FrontCount; f++)
                {
                    int nc = w.ScratchContactCount[p * MF + f];
                    if (nc == 0) continue;
                    int output = (w.ScratchFrontMelee[p * MF + f] + w.ScratchFrontRanged[p * MF + f])
                        / w.Rules.CombatExchangeDivisor;
                    if (output <= 0) continue;
                    int share = output / nc, rem = output % nc;
                    for (int e = 0; e < nc; e++)
                    {
                        int packed = w.ScratchContact[(p * MF + f) * SimWorld.ContactCap + e];
                        byte q = (byte)(packed >> 8);
                        int g = packed & 0xFF;
                        int dmg = share + (e == 0 ? rem : 0);
                        if (dmg <= 0) continue;
                        var foe = w.Players[q];
                        if (!foe.Alive) continue;
                        int foeTotal = 0;
                        for (int d = 0; d < u; d++) foeTotal += foe.Force[g * u + d];
                        if (foeTotal == 0)
                        {
                            // Nothing mans this stretch: the fire scorches the organism itself.
                            int hpLoss = dmg / w.Rules.OverflowDamageDivisor;
                            foe.OrganismHealth -= hpLoss < 1 ? 1 : hpLoss;
                            continue;
                        }
                        // A manned front still bleeds the ORGANISM under sustained fire —
                        // engagement always inflicts casualties on the body itself.
                        int attrition = dmg / w.Rules.OverflowDamageDivisor;
                        foe.OrganismHealth -= attrition < 1 ? 1 : attrition;
                        foe.FrontDamage[g] += dmg;
                        for (int d = 0; d < u && foe.FrontDamage[g] > 0; d++)
                        {
                            var ud = defs.Units[d];
                            if (ud.MaxHp <= 0) continue;
                            while (foe.Force[g * u + d] > 0 && foe.FrontDamage[g] >= ud.MaxHp)
                            {
                                foe.FrontDamage[g] -= ud.MaxHp;
                                foe.Force[g * u + d]--;
                                foeTotal--;
                                pl.EvoPoints += w.Rules.EvoPerKill;
                                pl.Food += ud.FoodCost * w.Rules.KillBountyNum / w.Rules.KillBountyDen;
                            }
                        }
                        if (foeTotal == 0 && w.ScratchFrontContested[q * MF + g] && foe.FrontBrokenTicks[g] == 0)
                        {
                            // BREAKTHROUGH: this front's defenders just perished. The line is
                            // open for a while; residual damage dissipates with the dead.
                            foe.FrontBrokenTicks[g] = w.Rules.BreakthroughTicks;
                            foe.FrontDamage[g] = 0;
                        }
                    }
                }
            }

            // ---- FLIPS: only pushing fronts take ground.
            for (byte p = 0; p < players; p++)
            {
                var pl = w.Players[p];
                if (!pl.Alive) continue;
                for (int f = 0; f < pl.FrontCount; f++)
                {
                    if (pl.FrontPushX[f] < 0) continue;
                    int nc = w.ScratchContactCount[p * MF + f];
                    if (nc == 0) continue;
                    int push = w.ScratchFrontMelee[p * MF + f] * w.Rules.PushNum / w.Rules.PushDen;
                    for (int e = 0; e < nc; e++)
                    {
                        int packed = w.ScratchContact[(p * MF + f) * SimWorld.ContactCap + e];
                        byte q = (byte)(packed >> 8);
                        int g = packed & 0xFF;
                        var foe = w.Players[q];
                        if (!foe.Alive) continue;
                        bool broken = foe.FrontBrokenTicks[g] > 0;
                        int hold = broken ? 0 : w.ScratchFrontHold[q * MF + g];
                        int adv = push - hold;
                        if (adv <= 0) continue;
                        int flips = 1 + adv / w.Rules.FlipAdvantageDivisor;
                        if (flips > w.Rules.FlipCapPerBeat) flips = w.Rules.FlipCapPerBeat;
                        if (broken) flips *= w.Rules.BreakthroughFlipMult;
                        FlipCells(w, p, pl, f, q, g, flips);
                    }
                }
            }
        }

        /// <summary>Take up to <paramref name="flips"/> enemy cells from front g of player q,
        /// nearest the pusher's target first. A standing building soaks a flip as damage
        /// instead of losing the cell; every flipped cell costs the loser organism health.</summary>
        private static void FlipCells(SimWorld w, byte p, PlayerState pl, int f, byte q, int g, int flips)
        {
            int cells = w.CellCount, cx0 = w.TerritoryCellsX;
            int n = 0;
            for (int c = 0; c < cells && n < SimWorld.CandidateCap; c++)
            {
                if (w.Territory[c] != q || w.ScratchCellSector[c] != g) continue;
                int x = c % cx0, y = c / cx0;
                bool adj = (x > 0 && w.Territory[c - 1] == p)
                    || (x < cx0 - 1 && w.Territory[c + 1] == p)
                    || (y > 0 && w.Territory[c - cx0] == p)
                    || (y < w.TerritoryCellsY - 1 && w.Territory[c + cx0] == p);
                if (!adj) continue;
                w.CellCenterCenti(c, out int ccx, out int ccy);
                long dx = ccx - pl.FrontPushX[f], dy = ccy - pl.FrontPushY[f];
                w.ScratchCandCell[n] = c;
                w.ScratchCandDist[n] = dx * dx + dy * dy;
                n++;
            }
            GrowthSystem.SortCandidates(w, n);
            var foe = w.Players[q];
            for (int i = 0; i < n && flips > 0; )
            {
                int c = w.ScratchCandCell[i];
                int shield = BuildingOnCell(w, c);
                if (shield >= 0)
                {
                    // A standing building holds the cell: this beat's remaining flips keep
                    // pounding it (no advance) until it falls; Cleanup despawns it at 0.
                    w.Hp[shield] -= w.Rules.BuildingFlipDamage;
                    flips--;
                    continue;
                }
                w.Territory[c] = p;
                foe.OrganismHealth -= w.Rules.CellLossHealth;
                flips--;
                i++;
            }
        }

        private static int BuildingOnCell(SimWorld w, int cell)
        {
            for (int i = 0; i < w.HighWater; i++)
                if (w.Kind[i] == EntityKind.Building && w.Hp[i] > 0 && w.CellOfPos(w.Pos[i]) == cell)
                    return i;
            return -1;
        }

        private static void MaybeContact(SimWorld w, int c1, int c2)
        {
            byte a = w.Territory[c1], b = w.Territory[c2];
            if (a >= w.Players.Length || b >= w.Players.Length || !w.AreEnemies(a, b)) return;
            AddContact(w, a, w.ScratchCellSector[c1], b, w.ScratchCellSector[c2]);
            AddContact(w, b, w.ScratchCellSector[c2], a, w.ScratchCellSector[c1]);
        }

        private static void AddContact(SimWorld w, byte p, int f, byte q, int g)
        {
            int slot = p * SimConstants.MaxFronts + f;
            int baseIx = slot * SimWorld.ContactCap;
            int n = w.ScratchContactCount[slot];
            short packed = (short)((q << 8) | g);
            for (int e = 0; e < n; e++)
                if (w.ScratchContact[baseIx + e] == packed) return;
            if (n >= SimWorld.ContactCap) return; // cap reached: extra distinct pairs fold away
            w.ScratchContact[baseIx + n] = packed;
            w.ScratchContactCount[slot] = (byte)(n + 1);
        }

        private static bool IsEnemyCell(SimWorld w, byte owner, byte other) =>
            other < w.Players.Length && w.AreEnemies(owner, other);
    }

    /// <summary>
    /// ORGANISM HEALTH, each slow beat: ONE living value. The body swells in lockstep
    /// with its growing ceiling — every new cell and finished building adds its worth to
    /// health directly, war or peace, so expanding is always strengthening. The slow
    /// regenerative knit, though, stops while ANY front is engaged: wounds taken in
    /// combat (attrition, unmanned-front fire, torn-away cells) only close after
    /// disengaging. Zero = elimination.
    /// </summary>
    public static class HealthSystem
    {
        public static int MaxOf(SimWorld w, DefDatabase defs, byte p)
        {
            int cells = 0;
            for (int c = 0; c < w.Territory.Length; c++) if (w.Territory[c] == p) cells++;
            int buildings = 0;
            for (int i = 0; i < w.HighWater; i++)
                if (w.Kind[i] == EntityKind.Building && w.Owner[i] == p && w.ConstructionRemaining[i] == 0)
                    buildings++;
            return w.Rules.HealthBase + w.Rules.HealthPerCell * cells + w.Rules.HealthPerBuilding * buildings;
        }

        public static void Tick(SimWorld w, DefDatabase defs)
        {
            for (byte p = 0; p < w.Players.Length; p++)
            {
                var pl = w.Players[p];
                if (!pl.Alive) continue;
                int max = MaxOf(w, defs, p);
                int delta = max - pl.OrganismHealthMax;
                pl.OrganismHealthMax = max;

                bool engaged = false;
                for (int f = 0; f < pl.FrontCount && !engaged; f++)
                    engaged = w.ScratchFrontContested[p * SimConstants.MaxFronts + f];
                if (delta > 0) pl.OrganismHealth += delta;   // growth swells the body, war or peace
                if (!engaged) pl.OrganismHealth += w.Rules.HealthRegenPerBeat;
                if (pl.OrganismHealth > max) pl.OrganismHealth = max;
                if (pl.OrganismHealth <= 0) w.Eliminate(p);
            }
        }
    }

    /// <summary>
    /// TERRITORY VICTORY, each slow beat: the first team (ascending player order) holding
    /// the winning share of the ownable cells eliminates everyone else — AliveTeams and the
    /// client's banner then report the win through the existing path.
    /// </summary>
    public static class VictorySystem
    {
        public static void Tick(SimWorld w, DefDatabase defs)
        {
            if (w.OwnableCellCount == 0) return;
            for (int p = 0; p < w.Players.Length; p++)
            {
                var pl = w.Players[p];
                if (!pl.Alive) continue;
                bool firstOfTeam = true;
                for (int q = 0; q < p; q++)
                    if (w.Players[q].Alive && w.Players[q].Team == pl.Team) { firstOfTeam = false; break; }
                if (!firstOfTeam) continue;
                long teamCells = 0;
                for (int c = 0; c < w.Territory.Length; c++)
                {
                    byte o = w.Territory[c];
                    if (o < w.Players.Length && w.Players[o].Alive && w.Players[o].Team == pl.Team) teamCells++;
                }
                if (teamCells * 100 < (long)w.OwnableCellCount * w.Rules.TerritoryWinPercent) continue;
                for (byte q = 0; q < w.Players.Length; q++)
                    if (w.Players[q].Alive && w.Players[q].Team != pl.Team) w.Eliminate(q);
                return;
            }
        }
    }

    /// <summary>Dead buildings despawn; a dead nucleus (headquarters) eliminates its owner
    /// — the organism cannot live without its core.</summary>
    public static class CleanupSystem
    {
        public static void Tick(SimWorld w, DefDatabase defs)
        {
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Building || w.Hp[i] > 0) continue;
                bool nucleus = defs.Buildings[w.DefIndex[i]].IsHeadquarters;
                byte owner = w.Owner[i];
                w.Despawn(i);
                if (nucleus && owner < w.Players.Length && w.Players[owner].Alive)
                    w.Eliminate(owner);
            }
        }
    }

    /// <summary>Read-only world queries shared by tests, the runner, and the bot.</summary>
    public static class WorldQuery
    {
        public static int FindHq(SimWorld w, DefDatabase defs, byte owner)
        {
            for (int i = 0; i < w.HighWater; i++)
                if (w.Kind[i] == EntityKind.Building && w.Owner[i] == owner
                    && defs.Buildings[w.DefIndex[i]].IsHeadquarters) return i;
            return -1;
        }
    }
}
