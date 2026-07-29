namespace Petri.Core
{
    /// <summary>
    /// Orchestrates one deterministic match: pulls due commands from the log, runs the
    /// systems in fixed order, and fingerprints the whole world with StateHash. Two
    /// simulations built from the same defs, map, seed, and command log are bit-identical
    /// forever — that property IS the multiplayer model.
    /// </summary>
    public sealed class Simulation
    {
        public readonly SimWorld World;
        public readonly DefDatabase Defs;
        private readonly CommandLog _log;
        private int _cursor;

        /// <summary>Teams (optional): one entry per player; players sharing a value are allies.
        /// Null (or short) means every player is on their own team — a free-for-all.</summary>
        public Simulation(DefDatabase defs, MapDef map, int playerCount, ulong seed, CommandLog log, byte[] teams = null)
        {
            Defs = defs;
            _log = log;
            World = MatchSetup.Create(defs, map, playerCount, seed, teams);
        }

        public int TickCount => World.TickCount;

        public void Tick()
        {
            while (_cursor < _log.Count && _log[_cursor].Tick <= World.TickCount)
            {
                CommandSystem.Apply(World, Defs, _log[_cursor]);
                _cursor++;
            }
            ProductionSystem.Tick(World, Defs);
            ConstructionSystem.Tick(World, Defs);
            if (World.TickCount % Defs.Rules.SlowBeatTicks == 0)
                HarvestSystem.Tick(World, Defs);
            if (World.TickCount % Defs.Rules.GrowthBeatTicks == 0)
            {
                // Geometry first: growth's push pass and combat both read the sectors.
                FrontSystem.TickGeometry(World, Defs);
                GrowthSystem.Tick(World, Defs);
                FrontSystem.TickForces(World, Defs);
                FrontSystem.TickCombat(World, Defs);
            }
            if (World.TickCount % Defs.Rules.SlowBeatTicks == 0)
            {
                HealthSystem.Tick(World, Defs);
                VictorySystem.Tick(World, Defs);
            }
            CleanupSystem.Tick(World, Defs);
            World.TickCount++;
        }

        public int AlivePlayers()
        {
            int n = 0;
            for (int p = 0; p < World.Players.Length; p++)
                if (World.Players[p].Alive) n++;
            return n;
        }

        /// <summary>Last player standing, or -1 while the match is undecided.</summary>
        public int Winner()
        {
            if (AlivePlayers() != 1) return -1;
            for (int p = 0; p < World.Players.Length; p++)
                if (World.Players[p].Alive) return p;
            return -1;
        }

        /// <summary>How many distinct teams still have a living player. Index-order scan.</summary>
        public int AliveTeams()
        {
            int n = 0;
            for (int p = 0; p < World.Players.Length; p++)
            {
                if (!World.Players[p].Alive) continue;
                bool counted = false;
                for (int q = 0; q < p; q++)
                    if (World.Players[q].Alive && World.Players[q].Team == World.Players[p].Team) { counted = true; break; }
                if (!counted) n++;
            }
            return n;
        }

        /// <summary>The last team standing, or -1 while the match is undecided.</summary>
        public int WinningTeam()
        {
            if (AliveTeams() != 1) return -1;
            for (int p = 0; p < World.Players.Length; p++)
                if (World.Players[p].Alive) return World.Players[p].Team;
            return -1;
        }

        /// <summary>
        /// FNV-1a fingerprint of ALL persistent state. Every hashed field must also be reset
        /// in SimWorld.Spawn; scratch buffers stay out.
        /// </summary>
        public ulong StateHash()
        {
            var w = World;
            ulong h = 14695981039346656037UL;
            void Mix(ulong v) { h ^= v; h *= 1099511628211UL; }

            Mix((ulong)w.TickCount);
            Mix(w.Rng.State);
            Mix(w.Rng.Inc);
            Mix((ulong)w.RejectedCommands);
            Mix((ulong)w.MapWidth.Raw);
            Mix((ulong)w.MapHeight.Raw);

            // The territory map IS the game state: every cell, index order.
            for (int c = 0; c < w.Territory.Length; c++) Mix(w.Territory[c]);

            for (int p = 0; p < w.Players.Length; p++)
            {
                var pl = w.Players[p];
                Mix(pl.Alive ? 1UL : 0UL);
                Mix(pl.Team);
                Mix((ulong)pl.Food);
                Mix((ulong)pl.Minerals);
                Mix((ulong)pl.EvoPoints);
                for (int k = 0; k < pl.ProductionWeights.Length; k++) Mix((ulong)pl.ProductionWeights[k]);
                for (int k = 0; k < pl.UpgradeLevels.Length; k++) Mix(pl.UpgradeLevels[k]);
                // Superorganism block.
                Mix((ulong)pl.WorkerCount);
                Mix(pl.FrontCount);
                Mix((ulong)pl.OrganismHealth);
                for (int k = 0; k < pl.Force.Length; k++) Mix((ulong)pl.Force[k]);
                for (int k = 0; k < SimConstants.MaxFronts; k++)
                {
                    Mix((ulong)pl.FrontDamage[k]);
                    Mix((ulong)pl.FrontBrokenTicks[k]);
                    Mix((ulong)pl.FrontPushX[k]);
                    Mix((ulong)pl.FrontPushY[k]);
                }
            }

            for (int i = 0; i < w.HighWater; i++)
            {
                Mix((ulong)w.Kind[i]);
                if (w.Kind[i] == EntityKind.None) continue;
                Mix((ulong)i);
                Mix((ulong)w.DefIndex[i]);
                Mix(w.Owner[i]);
                Mix((ulong)w.Pos[i].X.Raw);
                Mix((ulong)w.Pos[i].Y.Raw);
                Mix((ulong)w.Hp[i]);
                Mix((ulong)w.ProduceProgress[i]);
                Mix((ulong)w.ProduceChoice[i]);
                Mix((ulong)w.ProduceOverride[i]);
                Mix(w.ProducePaused[i] ? 1UL : 0UL);
                Mix((ulong)w.ConstructionRemaining[i]);
                Mix((ulong)w.NodeFood[i]);
                Mix(w.NodeMineral[i] ? 1UL : 0UL);
                Mix((ulong)w.RallyFront[i]);
                Mix((ulong)w.Generation[i]);
            }
            return h;
        }
    }

    /// <summary>Builds the tick-0 world from a map def: HQ + starting buildings, workers, nodes.</summary>
    public static class MatchSetup
    {
        public static SimWorld Create(DefDatabase defs, MapDef map, int playerCount, ulong seed, byte[] teams = null)
        {
            if (playerCount > map.Spawns.Length)
                throw new System.ArgumentException("map " + map.Name + " has only " + map.Spawns.Length + " spawns");

            var w = new SimWorld(defs.Rules, playerCount, defs.Units.Length, defs.Upgrades.Length,
                Fix.Ratio(map.WidthCenti, 100), Fix.Ratio(map.HeightCenti, 100), seed);
            Fix centerX = w.MapWidth * Fix.Ratio(1, 2);

            // Immovable terrain straight from the map: chokepoints and flanking routes.
            w.WallPos = new FixVec2[map.Walls.Length];
            w.WallRadius = new Fix[map.Walls.Length];
            for (int k = 0; k < map.Walls.Length; k++)
            {
                w.WallPos[k] = w.ClampToMap(new FixVec2(Fix.Ratio(map.Walls[k].XCenti, 100), Fix.Ratio(map.Walls[k].YCenti, 100)));
                w.WallRadius[k] = Fix.Ratio(map.Walls[k].RadiusCenti, 100);
            }

            // ---- TERRITORY grid: 2u cells; blocked where a wall covers the cell center.
            w.TerritoryCellsX = System.Math.Max(1, map.WidthCenti / SimWorld.CellCenti);
            w.TerritoryCellsY = System.Math.Max(1, map.HeightCenti / SimWorld.CellCenti);
            int cells = w.TerritoryCellsX * w.TerritoryCellsY;
            w.Territory = new byte[cells];
            w.TerritoryBlocked = new bool[cells];
            w.ScratchCellSector = new byte[cells];
            for (int c = 0; c < cells; c++)
            {
                w.Territory[c] = SimWorld.NeutralOwner;
                w.CellCenterCenti(c, out int ccx, out int ccy);
                for (int k = 0; k < map.Walls.Length; k++)
                {
                    long dx = ccx - map.Walls[k].XCenti, dy = ccy - map.Walls[k].YCenti;
                    long r = map.Walls[k].RadiusCenti;
                    if (dx * dx + dy * dy <= r * r) { w.TerritoryBlocked[c] = true; break; }
                }
                if (!w.TerritoryBlocked[c]) w.OwnableCellCount++;
            }

            // Each organism starts as a small blob around its spawn (nucleus) cell.
            for (byte p = 0; p < playerCount; p++)
            {
                int spawnCell = w.CellOfCenti(map.Spawns[p].XCenti, map.Spawns[p].YCenti);
                int scx = spawnCell % w.TerritoryCellsX, scy = spawnCell / w.TerritoryCellsX;
                int r0 = defs.Rules.StartRadiusCells;
                for (int cy = scy - r0; cy <= scy + r0; cy++)
                {
                    if (cy < 0 || cy >= w.TerritoryCellsY) continue;
                    for (int cx = scx - r0; cx <= scx + r0; cx++)
                    {
                        if (cx < 0 || cx >= w.TerritoryCellsX) continue;
                        if ((cx - scx) * (cx - scx) + (cy - scy) * (cy - scy) > r0 * r0) continue;
                        int c = cy * w.TerritoryCellsX + cx;
                        if (!w.TerritoryBlocked[c] && w.Territory[c] == SimWorld.NeutralOwner)
                            w.Territory[c] = p;
                    }
                }
            }

            for (byte p = 0; p < playerCount; p++)
            {
                var player = w.Players[p];
                // No team list (or a short one) = free-for-all: each player is their own team.
                player.Team = teams != null && p < teams.Length ? teams[p] : p;
                player.Food = defs.Rules.StartingFood;
                player.Minerals = defs.Rules.StartingMinerals;
                player.WorkerCount = defs.Rules.StartingWorkers;
                for (int k = 0; k < defs.Units.Length; k++) player.ProductionWeights[k] = 1;

                var spawn = new FixVec2(Fix.Ratio(map.Spawns[p].XCenti, 100), Fix.Ratio(map.Spawns[p].YCenti, 100));
                // Starting buildings (the nucleus) in a row marching toward map center so
                // they never overlap regardless of which spawn corner the player holds.
                int towardCenter = spawn.X < centerX ? 1 : -1;
                int placed = 0;
                for (int b = 0; b < defs.Buildings.Length; b++)
                {
                    if (!defs.Buildings[b].StartsBuilt) continue;
                    var pos = w.ClampToMap(spawn + new FixVec2(Fix.FromInt(3 * placed * towardCenter), Fix.Zero));
                    w.Spawn(EntityKind.Building, (short)b, p, pos, defs.Buildings[b].MaxHp);
                    placed++;
                }
            }

            foreach (var node in map.Nodes)
            {
                var pos = new FixVec2(Fix.Ratio(node.XCenti, 100), Fix.Ratio(node.YCenti, 100));
                int e = w.Spawn(EntityKind.Node, 0, SimWorld.NeutralOwner, pos, 1);
                if (e >= 0) { w.NodeFood[e] = node.Food; w.NodeMineral[e] = node.Mineral; }
            }

            // Organisms hatch at full health for their starting size.
            for (byte p = 0; p < playerCount; p++)
                w.Players[p].OrganismHealth = HealthSystem.MaxOf(w, defs, p);

            return w;
        }
    }
}
