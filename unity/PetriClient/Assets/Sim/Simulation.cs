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
            World.AttackEvents.Clear(); // last tick's view feed
            while (_cursor < _log.Count && _log[_cursor].Tick <= World.TickCount)
            {
                CommandSystem.Apply(World, Defs, _log[_cursor]);
                _cursor++;
            }
            ProductionSystem.Tick(World, Defs);
            SwarmSystem.Tick(World, Defs);
            WorkerSystem.Tick(World, Defs);
            MovementSystem.Tick(World, Defs);
            CollisionSystem.Tick(World, Defs);
            SupplySystem.Tick(World, Defs);
            CombatSystem.Tick(World, Defs);
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

            for (int p = 0; p < w.Players.Length; p++)
            {
                var pl = w.Players[p];
                Mix(pl.Alive ? 1UL : 0UL);
                Mix(pl.Team);
                Mix((ulong)pl.Food);
                Mix((ulong)pl.Minerals);
                Mix((ulong)pl.EvoPoints);
                Mix((ulong)pl.SupplyPriority);
                for (int k = 0; k < pl.ProductionWeights.Length; k++) Mix((ulong)pl.ProductionWeights[k]);
                for (int k = 0; k < pl.UpgradeLevels.Length; k++) Mix(pl.UpgradeLevels[k]);
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
                Mix((ulong)w.MoveTarget[i].X.Raw);
                Mix((ulong)w.MoveTarget[i].Y.Raw);
                Mix(w.HasMoveOrder[i] ? 1UL : 0UL);
                Mix(w.AttackMove[i] ? 1UL : 0UL);
                Mix(w.QueueCount[i]);
                for (int q = 0; q < w.QueueCount[i]; q++)
                {
                    int slot = i * SimConstants.MaxOrderQueue + q;
                    Mix(w.QueueKind[slot]);
                    Mix((ulong)w.QueuePos[slot].X.Raw);
                    Mix((ulong)w.QueuePos[slot].Y.Raw);
                }
                Mix((ulong)w.AttackCooldown[i]);
                Mix((ulong)w.Carry[i]);
                Mix((ulong)w.GatherTimer[i]);
                Mix((ulong)w.WorkNode[i]);
                Mix((ulong)w.ProduceProgress[i]);
                Mix((ulong)w.ProduceChoice[i]);
                Mix((ulong)w.ProduceOverride[i]);
                Mix(w.ProducePaused[i] ? 1UL : 0UL);
                Mix(w.HasRally[i] ? 1UL : 0UL);
                Mix((ulong)w.RallyPoint[i].X.Raw);
                Mix((ulong)w.RallyPoint[i].Y.Raw);
                Mix((ulong)w.ConstructionRemaining[i]);
                Mix((ulong)w.BuildTask[i]);
                Mix((ulong)w.NodeFood[i]);
                Mix(w.NodeMineral[i] ? 1UL : 0UL);
                Mix(w.CarryMineral[i] ? 1UL : 0UL);
                Mix((ulong)w.Leader[i]);
                Mix(w.SiblingOrdinal[i]);
                Mix(w.Leaderless[i] ? 1UL : 0UL);
                Mix(w.Settled[i] ? 1UL : 0UL);
                Mix(w.SeekingSwarm[i] ? 1UL : 0UL);
                Mix(w.MoveAsOne[i] ? 1UL : 0UL);
                Mix((ulong)w.SupplyTicks[i]);
                Mix(w.Tier[i]);
                Mix((ulong)w.DepotStock[i]);
                Mix((ulong)w.CaravanCache[i]);
                Mix(w.Dial[i]);
                Mix(w.Stance[i] ? 1UL : 0UL);
                for (int u = 0; u < w.UnitDefCount; u++) Mix(w.ZoneMatrix[i * w.UnitDefCount + u]);
                Mix((ulong)w.Facing[i].X.Raw);
                Mix((ulong)w.Facing[i].Y.Raw);
                Mix(w.FacingHeld[i] ? 1UL : 0UL);
                Mix(w.HasLimbStation[i] ? 1UL : 0UL);
                Mix((ulong)w.LimbStation[i].X.Raw);
                Mix((ulong)w.LimbStation[i].Y.Raw);
                Mix(w.AutoAssimilate[i] ? 1UL : 0UL);
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
                Fix.Ratio(map.WidthCenti, 100), Fix.Ratio(map.HeightCenti, 100), seed, defs.BuildDefaultZones());

            // Largest possible entity radius — pads grid range queries so no pair is missed.
            int maxCenti = defs.Rules.NodeRadiusCenti;
            foreach (var u in defs.Units) if (u.CollisionRadiusCenti > maxCenti) maxCenti = u.CollisionRadiusCenti;
            foreach (var b in defs.Buildings) if (b.CollisionRadiusCenti > maxCenti) maxCenti = b.CollisionRadiusCenti;
            w.MaxInteractRadius = Fix.Ratio(maxCenti + 10, 100);
            Fix centerX = w.MapWidth * Fix.Ratio(1, 2);

            for (byte p = 0; p < playerCount; p++)
            {
                var player = w.Players[p];
                // No team list (or a short one) = free-for-all: each player is their own team.
                player.Team = teams != null && p < teams.Length ? teams[p] : p;
                player.Food = defs.Rules.StartingFood;
                player.Minerals = defs.Rules.StartingMinerals;
                for (int k = 0; k < defs.Units.Length; k++) player.ProductionWeights[k] = 1;

                var spawn = new FixVec2(Fix.Ratio(map.Spawns[p].XCenti, 100), Fix.Ratio(map.Spawns[p].YCenti, 100));
                // Lay starting buildings in a row marching toward map center so they never
                // overlap regardless of which spawn corner the player holds.
                int towardCenter = spawn.X < centerX ? 1 : -1;
                int placed = 0;
                int hq = -1;
                for (int b = 0; b < defs.Buildings.Length; b++)
                {
                    if (!defs.Buildings[b].StartsBuilt) continue;
                    var pos = w.ClampToMap(spawn + new FixVec2(Fix.FromInt(3 * placed * towardCenter), Fix.Zero));
                    int e = w.Spawn(EntityKind.Building, (short)b, p, pos, defs.Buildings[b].MaxHp);
                    w.DepotStock[e] = defs.Buildings[b].StockCapacity; // starting depots open full
                    if (defs.Buildings[b].IsHeadquarters && hq < 0) hq = e;
                    placed++;
                }

                if (hq >= 0)
                {
                    var hqDef = defs.Buildings[w.DefIndex[hq]];
                    for (int k = 0; k < defs.Rules.StartingWorkers; k++)
                    {
                        int workerDef = FirstWorkerDef(defs);
                        if (workerDef < 0) break;
                        Fix dist = Fix.Ratio(hqDef.CollisionRadiusCenti + defs.Units[workerDef].CollisionRadiusCenti + 30, 100);
                        var pos = w.ClampToMap(w.Pos[hq] + SimWorld.RingDir(k) * dist);
                        w.Spawn(EntityKind.Unit, (short)workerDef, p, pos, defs.Units[workerDef].MaxHp);
                    }
                }
            }

            foreach (var node in map.Nodes)
            {
                var pos = new FixVec2(Fix.Ratio(node.XCenti, 100), Fix.Ratio(node.YCenti, 100));
                int e = w.Spawn(EntityKind.Node, 0, SimWorld.NeutralOwner, pos, 1);
                if (e >= 0) { w.NodeFood[e] = node.Food; w.NodeMineral[e] = node.Mineral; }
            }

            return w;
        }

        private static int FirstWorkerDef(DefDatabase defs)
        {
            for (int k = 0; k < defs.Units.Length; k++)
                if (defs.Units[k].IsWorker) return k;
            return -1;
        }
    }
}
