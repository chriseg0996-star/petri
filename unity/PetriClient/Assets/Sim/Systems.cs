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
    /// remaining budget spreads isotropically via a rotating-offset scan. Deterministic:
    /// integer math, index-order scans, offsets derived from TickCount only.
    /// </summary>
    public static class GrowthSystem
    {
        private const int PushCandidateCap = 128;
        private static readonly int[] _candCell = new int[PushCandidateCap];
        private static readonly long[] _candDist = new long[PushCandidateCap];

        public static void Tick(SimWorld w, DefDatabase defs)
        {
            int cells = w.CellCount;
            if (cells == 0) return;
            int cx0 = w.TerritoryCellsX;
            int start = (int)(((long)(w.TickCount / w.Rules.GrowthBeatTicks) * 7919) % cells);

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
                    for (int c = 0; c < cells && n < PushCandidateCap; c++)
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
                        _candCell[n] = c;
                        _candDist[n] = dx * dx + dy * dy;
                        n++;
                    }
                    // Insertion sort by (dist, cellIndex): small n, no allocation, stable.
                    for (int i = 1; i < n; i++)
                    {
                        int cc = _candCell[i]; long dd = _candDist[i];
                        int j = i - 1;
                        while (j >= 0 && (_candDist[j] > dd || (_candDist[j] == dd && _candCell[j] > cc)))
                        {
                            _candCell[j + 1] = _candCell[j];
                            _candDist[j + 1] = _candDist[j];
                            j--;
                        }
                        _candCell[j + 1] = cc; _candDist[j + 1] = dd;
                    }
                    for (int i = 0; i < n && budget > 0; i++)
                    {
                        w.Territory[_candCell[i]] = p;
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

                // ISOTROPIC PASS: whatever budget remains spreads in all directions.
                for (int n = 0; n < cells && budget > 0; n++)
                {
                    int c = start + n;
                    if (c >= cells) c -= cells;
                    if (w.Territory[c] != SimWorld.NeutralOwner || w.TerritoryBlocked[c]) continue;
                    int x = c % cx0, y = c / cx0;
                    bool adj = (x > 0 && w.Territory[c - 1] == p)
                        || (x < cx0 - 1 && w.Territory[c + 1] == p)
                        || (y > 0 && w.Territory[c - cx0] == p)
                        || (y < w.TerritoryCellsY - 1 && w.Territory[c + cx0] == p);
                    if (!adj) continue;
                    w.Territory[c] = p;
                    budget--;
                }
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

        private static bool IsEnemyCell(SimWorld w, byte owner, byte other) =>
            other < w.Players.Length && w.AreEnemies(owner, other);
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
