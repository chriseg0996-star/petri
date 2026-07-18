using System;
using System.IO;
using Petri.Core;
using Xunit;

namespace Petri.Tests
{
    /// <summary>In-code minimal dataset so sim tests never depend on data files on disk.</summary>
    internal static class TestWorlds
    {
        public static DefDatabase TinyDefs()
        {
            var rules = new Rules { MaxEntities = 1024, StartingFood = 150, StartingWorkers = 2, NodeRadiusCenti = 60 };
            var units = new[]
            {
                new UnitDef
                {
                    Id = "test.leader", MaxHp = 100, MoveSpeedCenti = 210, CollisionRadiusCenti = 35,
                    PushStrength = 2, PushResistance = 3, FoodCost = 120, BuildTimeTicks = 150,
                    IsLeader = true,
                },
                new UnitDef
                {
                    Id = "test.soldier", MaxHp = 60, MoveSpeedCenti = 200, CollisionRadiusCenti = 30,
                    PushStrength = 2, PushResistance = 2, AttackDamage = 5, AttackRangeCenti = 40,
                    AcquireRangeCenti = 800, AttackCooldownTicks = 20, FoodCost = 60, BuildTimeTicks = 100,
                },
                new UnitDef
                {
                    Id = "test.worker", MaxHp = 30, MoveSpeedCenti = 220, CollisionRadiusCenti = 25,
                    PushStrength = 1, PushResistance = 1, FoodCost = 40, BuildTimeTicks = 80,
                    IsWorker = true, CarryCapacity = 10, GatherTicks = 30,
                },
                // Free chaff (FoodCost 0) — MUST sort after test.worker so the hardcoded
                // unit dense indices 0/1/2 above stay valid.
                new UnitDef
                {
                    Id = "test.xmite", MaxHp = 40, MoveSpeedCenti = 240, CollisionRadiusCenti = 18,
                    PushStrength = 1, PushResistance = 1, AttackDamage = 3, AttackRangeCenti = 40,
                    AcquireRangeCenti = 700, AttackCooldownTicks = 20, FoodCost = 0, BuildTimeTicks = 50,
                },
            };
            var buildings = new[]
            {
                // Sorts FIRST (like the real strain.brood-sac) so the bot test genuinely
                // guards against the free-spawner hijacking PickConstructible.
                new BuildingDef
                {
                    Id = "test.broodsac", MaxHp = 200, CollisionRadiusCenti = 50,
                    Constructible = true, FoodCost = 50, BuildTimeTicks = 40,
                    Produces = new[] { "test.xmite" },
                },
                new BuildingDef
                {
                    Id = "test.cache", MaxHp = 200, CollisionRadiusCenti = 60,
                    ProvidesSupply = true, StockCapacity = 50, Constructible = true, FoodCost = 40, BuildTimeTicks = 40,
                },
                new BuildingDef
                {
                    Id = "test.dropoff", MaxHp = 300, CollisionRadiusCenti = 60,
                    IsDropoff = true, Constructible = true, FoodCost = 40, BuildTimeTicks = 40,
                },
                new BuildingDef
                {
                    Id = "test.hq", MaxHp = 500, CollisionRadiusCenti = 100,
                    IsHeadquarters = true, ProvidesSupply = true, StockCapacity = 5000, StartsBuilt = true,
                    Produces = new[] { "test.worker", "test.soldier" },
                },
                new BuildingDef
                {
                    Id = "test.nursery", MaxHp = 300, CollisionRadiusCenti = 80,
                    Constructible = true, FoodCost = 50, BuildTimeTicks = 60,
                    Produces = new[] { "test.soldier", "test.leader" },
                },
                new BuildingDef
                {
                    Id = "test.prong", MaxHp = 200, CollisionRadiusCenti = 50,
                    HubBuilt = true, FoodCost = 80, MineralCost = 50, BuildTimeTicks = 30,
                    Produces = new[] { "test.soldier" },
                },
                new BuildingDef
                {
                    Id = "test.mutagen", MaxHp = 200, CollisionRadiusCenti = 40,
                    Constructible = true, EvoCost = 3, BuildTimeTicks = 40, AttackBonus = 1,
                },
                new BuildingDef
                {
                    Id = "test.turret", MaxHp = 300, CollisionRadiusCenti = 50,
                    Constructible = true, FoodCost = 60, BuildTimeTicks = 40,
                    AttackDamage = 7, AttackRangeCenti = 500, AttackCooldownTicks = 10, ProjectileSpeedCenti = 1400,
                },
            };
            // Test upgrades gated behind test.nursery, all scaling test.soldier — one per fold
            // site (damage, armor, move, attack-speed, range). Pre-sorted by id (ordinal).
            var upgrades = new[]
            {
                new UpgradeDef { Id = "test.up-armor", FoodCost = 50, RequiresBuilding = "test.nursery",
                    Stat = UpgradeStat.Armor, Num = 1, Den = 2, Affects = new[] { "test.soldier" } },
                new UpgradeDef { Id = "test.up-damage", FoodCost = 50, RequiresBuilding = "test.nursery",
                    Stat = UpgradeStat.Damage, Num = 2, Den = 1, Affects = new[] { "test.soldier" } },
                new UpgradeDef { Id = "test.up-haste", FoodCost = 50, RequiresBuilding = "test.nursery",
                    Stat = UpgradeStat.AttackSpeed, Num = 1, Den = 2, Affects = new[] { "test.soldier" } },
                new UpgradeDef { Id = "test.up-reach", FoodCost = 50, RequiresBuilding = "test.nursery",
                    Stat = UpgradeStat.AttackRange, Num = 2, Den = 1, Affects = new[] { "test.soldier" } },
                new UpgradeDef { Id = "test.up-speed", FoodCost = 50, RequiresBuilding = "test.nursery",
                    Stat = UpgradeStat.MoveSpeed, Num = 2, Den = 1, Affects = new[] { "test.soldier" } },
            };
            // Arrays are pre-sorted by id (ordinal) — same contract the loader guarantees.
            return new DefDatabase(rules, units, buildings, upgrades, 1);
        }

        public static MapDef TinyMap() => new MapDef
        {
            Name = "tiny",
            WidthCenti = 4000,
            HeightCenti = 4000,
            // Eight spawns (corners then edge midpoints) so free-for-all player counts are
            // testable; 2-player tests keep using spawns 0 and 1 exactly as before.
            Spawns = new[]
            {
                new MapSpawn { XCenti = 600, YCenti = 600 },
                new MapSpawn { XCenti = 3400, YCenti = 3400 },
                new MapSpawn { XCenti = 3400, YCenti = 600 },
                new MapSpawn { XCenti = 600, YCenti = 3400 },
                new MapSpawn { XCenti = 2000, YCenti = 400 },
                new MapSpawn { XCenti = 2000, YCenti = 3600 },
                new MapSpawn { XCenti = 400, YCenti = 2000 },
                new MapSpawn { XCenti = 3600, YCenti = 2000 },
            },
            Nodes = new[]
            {
                new MapNode { XCenti = 1400, YCenti = 600, Food = 500 },
                new MapNode { XCenti = 2600, YCenti = 3400, Food = 500 },
            },
        };

        /// <summary>A roomy plate with a grid of seats, for large free-for-all tests.</summary>
        public static MapDef BigMap(int seats)
        {
            const int cols = 8, pitch = 3000;
            var spawns = new MapSpawn[seats];
            var nodes = new MapNode[seats];
            for (int i = 0; i < seats; i++)
            {
                int x = 1500 + (i % cols) * pitch, y = 1500 + (i / cols) * pitch;
                spawns[i] = new MapSpawn { XCenti = x, YCenti = y };
                nodes[i] = new MapNode { XCenti = x + 900, YCenti = y, Food = 5000 };
            }
            return new MapDef { Name = "big", WidthCenti = 26000, HeightCenti = 26000, Spawns = spawns, Nodes = nodes };
        }

        public static Simulation NewSim(ulong seed, CommandLog log) =>
            new Simulation(TinyDefs(), TinyMap(), 2, seed, log);
    }

    public class FixMathTests
    {
        [Fact]
        public void ArithmeticIsExact()
        {
            Assert.Equal(Fix.FromInt(3), Fix.FromInt(6) * Fix.Ratio(1, 2));
            Assert.Equal(Fix.One, Fix.Ratio(1, 4) + Fix.Ratio(3, 4));
            Assert.Equal(Fix.FromInt(5), Fix.FromInt(20) / Fix.FromInt(4));
            Assert.Equal(Fix.FromInt(-2), -Fix.FromInt(2));
        }

        [Fact]
        public void SqrtIsExactForPerfectSquares()
        {
            Assert.Equal(Fix.FromInt(3), Fix.Sqrt(Fix.FromInt(9)));
            Assert.Equal(Fix.FromInt(12), Fix.Sqrt(Fix.FromInt(144)));
            Assert.Equal(Fix.Zero, Fix.Sqrt(Fix.Zero));
            Assert.Equal(Fix.Ratio(1, 2), Fix.Sqrt(Fix.Ratio(1, 4)));
        }

        [Fact]
        public void MoveTowardsSnapsExactlyOnArrival()
        {
            var from = new FixVec2(Fix.Zero, Fix.Zero);
            var to = new FixVec2(Fix.FromInt(1), Fix.Zero);
            var mid = FixVec2.MoveTowards(from, to, Fix.Ratio(1, 4), out bool arrived);
            Assert.False(arrived);
            Assert.Equal(Fix.Ratio(1, 4), mid.X);
            var end = FixVec2.MoveTowards(mid, to, Fix.FromInt(2), out arrived);
            Assert.True(arrived);
            Assert.Equal(to, end);
        }
    }

    public class DeterminismTests
    {
        private static ulong RunAndHash(ulong seed, int ticks)
        {
            var sim = TestWorlds.NewSim(seed, new CommandLog());
            for (int t = 0; t < ticks; t++) sim.Tick();
            return sim.StateHash();
        }

        [Fact]
        public void IdenticalRunsAreBitIdentical()
        {
            Assert.Equal(RunAndHash(42, 600), RunAndHash(42, 600));
            Assert.Equal(RunAndHash(7, 600), RunAndHash(7, 600));
        }

        [Fact]
        public void DifferentSeedsDiverge()
        {
            Assert.NotEqual(RunAndHash(42, 600), RunAndHash(43, 600));
        }

        [Fact]
        public void CommandsChangeTheHashDeterministically()
        {
            ulong WithMove()
            {
                var log = new CommandLog();
                var sim = TestWorlds.NewSim(42, log);
                int worker = FindUnit(sim, 0, isWorker: true);
                log.Add(new Command { Tick = 0, Player = 0, Type = CommandType.Move, A = worker, B = 2000, C = 2000 });
                for (int t = 0; t < 200; t++) sim.Tick();
                return sim.StateHash();
            }
            Assert.Equal(WithMove(), WithMove());
            Assert.NotEqual(WithMove(), RunAndHash(42, 200));
        }

        internal static int FindUnit(Simulation sim, byte owner, bool isWorker)
        {
            var w = sim.World;
            for (int i = 0; i < w.HighWater; i++)
                if (w.Kind[i] == EntityKind.Unit && w.Owner[i] == owner && sim.Defs.Units[w.DefIndex[i]].IsWorker == isWorker)
                    return i;
            return -1;
        }
    }

    public class CommandTests
    {
        [Fact]
        public void MoveCommandOnEnemyUnitRejectsAndChangesNothing()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            int worker = DeterminismTests.FindUnit(sim, 0, isWorker: true);
            Assert.True(worker >= 0);
            var cmd = new Command { Tick = 0, Player = 1, Type = CommandType.Move, A = worker, B = 100, C = 100 };
            CommandSystem.Apply(sim.World, sim.Defs, cmd);
            Assert.Equal(1, sim.World.RejectedCommands);
            Assert.False(sim.World.HasMoveOrder[worker]);
        }

        [Fact]
        public void ValidMoveCommandSetsTheOrder()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            int worker = DeterminismTests.FindUnit(sim, 0, isWorker: true);
            var cmd = new Command { Tick = 0, Player = 0, Type = CommandType.Move, A = worker, B = 2000, C = 1000 };
            CommandSystem.Apply(sim.World, sim.Defs, cmd);
            Assert.Equal(0, sim.World.RejectedCommands);
            Assert.True(sim.World.HasMoveOrder[worker]);
            Assert.Equal(Fix.FromInt(20), sim.World.MoveTarget[worker].X);
        }

        [Fact]
        public void ProduceOverridePinsProductionToOneUnit()
        {
            // TinyDefs unit dense order: 0 leader, 1 soldier, 2 worker; HQ produces worker+soldier.
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int hq = WorkerSystem.FindHq(w, sim.Defs, 0);
            Assert.True(hq >= 0);

            // Auto weights would pick the soldier first (2 workers already exist, 0 soldiers).
            // Pin the HQ to workers and it must produce a worker instead.
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetProduceOverride, A = hq, B = 2 });
            Assert.Equal(0, w.RejectedCommands);
            sim.Tick();
            Assert.Equal(2, w.ProduceChoice[hq]);

            // A unit this building cannot produce (the leader) rejects and changes nothing.
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetProduceOverride, A = hq, B = 0 });
            Assert.Equal(1, w.RejectedCommands);
            Assert.Equal(2, w.ProduceOverride[hq]);

            // The enemy cannot retune my buildings.
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 1, Type = CommandType.SetProduceOverride, A = hq, B = 1 });
            Assert.Equal(2, w.RejectedCommands);

            // Back to auto.
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetProduceOverride, A = hq, B = -1 });
            Assert.Equal(2, w.RejectedCommands);
            Assert.Equal(-1, w.ProduceOverride[hq]);
        }

        [Fact]
        public void ProducedWorkersGatherAndGrowTheEconomy()
        {
            // Regression guard: a worker PRODUCED mid-match must gather and deposit like the
            // starting ones. Remove the starting workers so only produced ones exist, then pause
            // production (so nothing is spent) and confirm those workers raise food on their own.
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            for (int i = 0; i < w.HighWater; i++)
                if (w.Kind[i] == EntityKind.Unit && w.Owner[i] == 0 && sim.Defs.Units[w.DefIndex[i]].IsWorker)
                    w.Despawn(i);
            Assert.Equal(0, CountUnits(w, 0, 2));

            int hq = WorkerSystem.FindHq(w, sim.Defs, 0);
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetProduceOverride, A = hq, B = 2 });
            for (int t = 0; t < 400; t++) sim.Tick();               // build a few fresh workers
            Assert.True(CountUnits(w, 0, 2) >= 2, "expected new workers to be produced");

            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetProducePaused, A = hq, B = 1 });
            long foodAfterPause = w.Players[0].Food;
            for (int t = 0; t < 2000; t++) sim.Tick();              // now only gathering happens
            Assert.True(w.Players[0].Food > foodAfterPause, "produced workers must gather and deposit food");
        }

        [Fact]
        public void PauseHaltsProductionAndResumeContinues()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int hq = WorkerSystem.FindHq(w, sim.Defs, 0);
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetProducePaused, A = hq, B = 1 });
            Assert.True(w.ProducePaused[hq]);

            long foodAtPause = w.Players[0].Food;
            for (int t = 0; t < 200; t++) sim.Tick();
            // Paused: no new production started, so the (idle) HQ never spent food on a unit.
            Assert.Equal(-1, w.ProduceChoice[hq]);

            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetProducePaused, A = hq, B = 0 });
            Assert.False(w.ProducePaused[hq]);
            sim.Tick();
            Assert.True(w.ProduceChoice[hq] >= 0, "resumed HQ should start producing again");
        }

        [Fact]
        public void RallyOntoResourcePileMakesWorkersMineIt()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int hq = WorkerSystem.FindHq(w, sim.Defs, 0);
            // Find a node and rally the (worker-pinned) HQ directly onto it.
            int node = -1;
            for (int i = 0; i < w.HighWater; i++) if (w.Kind[i] == EntityKind.Node) { node = i; break; }
            Assert.True(node >= 0);
            int nx = (int)(w.Pos[node].X.Raw * 100 >> Fix.FracBits);
            int ny = (int)(w.Pos[node].Y.Raw * 100 >> Fix.FracBits);
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetProduceOverride, A = hq, B = 2 });
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetRally, A = hq, B = nx, C = ny, D = 1 });

            int before = CountUnits(w, 0, 2);
            for (int t = 0; t < 120 && CountUnits(w, 0, 2) == before; t++) sim.Tick();

            // A freshly produced worker is assigned to mine that exact pile (no move order needed).
            bool found = false;
            for (int i = 0; i < w.HighWater; i++)
                if (w.Kind[i] == EntityKind.Unit && w.Owner[i] == 0 && w.DefIndex[i] == 2 && w.WorkNode[i] == node)
                    found = true;
            Assert.True(found, "expected a produced worker assigned to the rallied pile");
        }

        [Fact]
        public void RallySendsProducedUnitsToThePoint()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int hq = WorkerSystem.FindHq(w, sim.Defs, 0);
            // Pin the HQ to workers and rally it far from the base.
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetProduceOverride, A = hq, B = 2 });
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetRally, A = hq, B = 3000, C = 600, D = 1 });
            Assert.Equal(0, w.RejectedCommands);
            Assert.True(w.HasRally[hq]);

            int before = CountUnits(w, 0, 2);
            for (int t = 0; t < 120 && CountUnits(w, 0, 2) == before; t++) sim.Tick();

            // The newly produced worker is marching to the rally point.
            bool found = false;
            for (int i = 0; i < w.HighWater; i++)
                if (w.Kind[i] == EntityKind.Unit && w.Owner[i] == 0 && w.DefIndex[i] == 2 && w.HasMoveOrder[i]
                    && w.MoveTarget[i].Equals(w.RallyPoint[hq]))
                    found = true;
            Assert.True(found, "expected a produced worker heading to the rally point");
        }

        [Fact]
        public void WorkerConstructsABuilding()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int worker = DeterminismTests.FindUnit(sim, 0, isWorker: true);
            long foodBefore = w.Players[0].Food;

            // Overlapping the HQ rejects and spends nothing.
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.ConstructBuilding, A = worker, B = 600, C = 600, D = sim.Defs.BuildingIndex("test.nursery") });
            Assert.Equal(1, w.RejectedCommands);
            Assert.Equal(foodBefore, w.Players[0].Food);

            // A clear spot is accepted: site spawns, food is paid, the worker is tasked.
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.ConstructBuilding, A = worker, B = 1200, C = 1200, D = sim.Defs.BuildingIndex("test.nursery") });
            Assert.Equal(1, w.RejectedCommands);
            Assert.Equal(foodBefore - 50, w.Players[0].Food);
            int site = w.BuildTask[worker];
            Assert.True(site >= 0);
            Assert.Equal(180, w.ConstructionRemaining[site]); // work units = 3 × 60 build ticks

            for (int t = 0; t < 400 && w.ConstructionRemaining[site] > 0; t++) sim.Tick();
            Assert.Equal(0, w.ConstructionRemaining[site]);
            Assert.Equal(EntityKind.Building, w.Kind[site]); // finished nursery stands
            sim.Tick(); // next tick the worker notices the site is done and leaves the crew
            Assert.Equal(-1, w.BuildTask[worker]);
        }

        [Fact]
        public void ExtraBuildersFollowAoe2Curve()
        {
            // N builders advance a site by N+2 work units per tick (actual time = 3T/(N+2)).
            var defs = TestWorlds.TinyDefs();
            var w = new SimWorld(defs.Rules, 1, defs.Units.Length, defs.Upgrades.Length, Fix.FromInt(40), Fix.FromInt(40), 1);
            int nursery = defs.BuildingIndex("test.nursery");
            var bdef = defs.Buildings[nursery];
            int site = w.Spawn(EntityKind.Building, (short)nursery, 0, new FixVec2(Fix.FromInt(10), Fix.FromInt(10)), bdef.MaxHp);
            int total = bdef.BuildTimeTicks * 3;
            w.ConstructionRemaining[site] = total;

            int b1 = w.Spawn(EntityKind.Unit, 2, 0, w.Pos[site], defs.Units[2].MaxHp); // test.worker
            w.BuildTask[b1] = site;
            WorkerSystem.Tick(w, defs);
            Assert.Equal(total - 3, w.ConstructionRemaining[site]); // solo crew: 3/tick = nominal speed

            int b2 = w.Spawn(EntityKind.Unit, 2, 0, w.Pos[site], defs.Units[2].MaxHp);
            w.BuildTask[b2] = site;
            WorkerSystem.Tick(w, defs);
            Assert.Equal(total - 3 - 4, w.ConstructionRemaining[site]); // two builders: 4/tick
        }

        [Fact]
        public void AbandonedConstructionResumesOnReassign()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int worker = DeterminismTests.FindUnit(sim, 0, isWorker: true);
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.ConstructBuilding, A = worker, B = 1200, C = 1200, D = sim.Defs.BuildingIndex("test.nursery") });
            int site = w.BuildTask[worker];
            Assert.True(site >= 0);
            int total = w.ConstructionRemaining[site];

            // Let the worker walk over and put in some work.
            for (int t = 0; t < 300 && w.ConstructionRemaining[site] == total; t++) sim.Tick();
            int progressed = w.ConstructionRemaining[site];
            Assert.True(progressed < total, "worker never started building");

            // A move order pulls the worker off the crew; the site freezes where it is.
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.Move, A = worker, B = 100, C = 100 });
            Assert.Equal(-1, w.BuildTask[worker]);
            for (int t = 0; t < 20; t++) sim.Tick();
            Assert.Equal(progressed, w.ConstructionRemaining[site]);

            // Reclicking the site (AssignBuild) resumes with progress intact.
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.AssignBuild, A = worker, B = site });
            Assert.Equal(0, w.RejectedCommands);
            Assert.Equal(site, w.BuildTask[worker]);
            for (int t = 0; t < 400 && w.ConstructionRemaining[site] > 0; t++) sim.Tick();
            Assert.Equal(0, w.ConstructionRemaining[site]);
            Assert.Equal(EntityKind.Building, w.Kind[site]);

            // Rejoining a FINISHED building is nonsense and rejects.
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.AssignBuild, A = worker, B = site });
            Assert.Equal(1, w.RejectedCommands);
        }

        internal static int CountUnits(SimWorld w, byte owner, int defIx)
        {
            int n = 0;
            for (int i = 0; i < w.HighWater; i++)
                if (w.Kind[i] == EntityKind.Unit && w.Owner[i] == owner && w.DefIndex[i] == defIx) n++;
            return n;
        }

        [Fact]
        public void InvalidWeightCommandRejects()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            CommandSystem.Apply(sim.World, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetProductionWeight, A = 99, B = 1 });
            CommandSystem.Apply(sim.World, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetProductionWeight, A = 0, B = -5 });
            Assert.Equal(2, sim.World.RejectedCommands);
        }
    }

    public class CollisionBlockingTests
    {
        // A heavy tank (big radius, huge HP) vs a light soldier, overlapping head-on.
        private static SimWorld TwoUnits(DefDatabase defs, int players, short heavyDef, byte ownerA, short lightDef, byte ownerB, out int heavy, out int light)
        {
            var w = new SimWorld(defs.Rules, players, defs.Units.Length, defs.Upgrades.Length,
                Fix.FromInt(40), Fix.FromInt(40), 1);
            heavy = w.Spawn(EntityKind.Unit, heavyDef, ownerA, new FixVec2(Fix.FromInt(20), Fix.FromInt(20)), defs.Units[heavyDef].MaxHp);
            light = w.Spawn(EntityKind.Unit, lightDef, ownerB, new FixVec2(Fix.Ratio(2040, 100), Fix.FromInt(20)), defs.Units[lightDef].MaxHp);
            return w;
        }

        [Fact]
        public void HeavyEnemyBodyBlocksLighterOne()
        {
            // test.leader (r35 x hp100 = 3500) outweighs test.worker (r25 x hp30 = 750) ~4.7:1,
            // well past the 2:1 block ratio, so the leader is an immovable wall.
            var defs = TestWorlds.TinyDefs();
            short leaderDef = (short)defs.UnitIndex("test.leader");
            short workerDef = (short)defs.UnitIndex("test.worker");
            var w = TwoUnits(defs, 2, leaderDef, 0, workerDef, 1, out int heavy, out int light);
            var heavyPos0 = w.Pos[heavy];
            var lightPos0 = w.Pos[light];

            CollisionSystem.Tick(w, defs);

            Assert.Equal(heavyPos0.X.Raw, w.Pos[heavy].X.Raw); // the wall does not budge
            Assert.True(w.Pos[light].X.Raw > lightPos0.X.Raw); // the lighter enemy is pushed away
        }

        [Fact]
        public void WorkerYieldsToFriendlyWarrior()
        {
            var defs = TestWorlds.TinyDefs();
            short soldierDef = (short)defs.UnitIndex("test.soldier");
            short workerDef = (short)defs.UnitIndex("test.worker");
            // Same owner: worker (priority 0) must give way to the soldier (priority 1).
            var w = TwoUnits(defs, 2, soldierDef, 0, workerDef, 0, out int soldier, out int worker);
            var soldierPos0 = w.Pos[soldier];
            var workerPos0 = w.Pos[worker];

            CollisionSystem.Tick(w, defs);

            Assert.Equal(soldierPos0.X.Raw, w.Pos[soldier].X.Raw); // warrior holds ground
            Assert.True(w.Pos[worker].X.Raw > workerPos0.X.Raw);   // worker steps aside fully
        }

        [Fact]
        public void FriendlyHeavyDoesNotWallFriendlyLight()
        {
            // Body-blocking is opposing-team only: two same-team, same-priority soldiers of
            // different weight still both give ground (resistance split), neither is a wall.
            var defs = TestWorlds.TinyDefs();
            short soldierDef = (short)defs.UnitIndex("test.soldier");
            var w = new SimWorld(defs.Rules, 2, defs.Units.Length, defs.Upgrades.Length,
                Fix.FromInt(40), Fix.FromInt(40), 1);
            int a = w.Spawn(EntityKind.Unit, soldierDef, 0, new FixVec2(Fix.FromInt(20), Fix.FromInt(20)), 60);
            int b = w.Spawn(EntityKind.Unit, soldierDef, 0, new FixVec2(Fix.Ratio(2040, 100), Fix.FromInt(20)), 60);
            var a0 = w.Pos[a]; var b0 = w.Pos[b];

            CollisionSystem.Tick(w, defs);

            Assert.True(w.Pos[a].X.Raw < a0.X.Raw); // both move apart
            Assert.True(w.Pos[b].X.Raw > b0.X.Raw);
        }
    }

    public class AttackStructureTests
    {
        [Fact]
        public void AttackStructuresRaiseArmyDamageAndStackAndDropWhenLost()
        {
            var defs = TestWorlds.TinyDefs();
            var w = new SimWorld(defs.Rules, 2, defs.Units.Length, defs.Upgrades.Length,
                Fix.FromInt(40), Fix.FromInt(40), 1);
            short sd = (short)defs.UnitIndex("test.soldier");
            int mut = defs.BuildingIndex("test.mutagen");

            // Attacker in the target's FRONT arc → clean base damage of 5.
            int me = w.Spawn(EntityKind.Unit, sd, 0, new FixVec2(Fix.FromInt(7), Fix.FromInt(5)), 60);
            int foe = w.Spawn(EntityKind.Unit, sd, 1, new FixVec2(Fix.FromInt(6), Fix.FromInt(5)), 60);
            w.RebuildGrid();
            CombatSystem.Tick(w, defs); // populates the derived tally
            Assert.Equal(5, CombatSystem.DamageOf(w, defs, me, foe));

            // One finished mutagen pool → +1 to all of player 0's units.
            int p1 = w.Spawn(EntityKind.Building, (short)mut, 0, new FixVec2(Fix.FromInt(20), Fix.FromInt(20)), 200);
            CombatSystem.Tick(w, defs);
            Assert.Equal(6, CombatSystem.DamageOf(w, defs, me, foe));
            Assert.Equal(0, w.ScratchAttackBonus[1]); // the enemy gets nothing

            // A second pool stacks to +2.
            int p2 = w.Spawn(EntityKind.Building, (short)mut, 0, new FixVec2(Fix.FromInt(30), Fix.FromInt(30)), 200);
            CombatSystem.Tick(w, defs);
            Assert.Equal(7, CombatSystem.DamageOf(w, defs, me, foe));

            // An UNFINISHED pool doesn't count yet.
            int p3 = w.Spawn(EntityKind.Building, (short)mut, 0, new FixVec2(Fix.FromInt(10), Fix.FromInt(30)), 200);
            w.ConstructionRemaining[p3] = 50;
            CombatSystem.Tick(w, defs);
            Assert.Equal(7, CombatSystem.DamageOf(w, defs, me, foe));

            // Lose a standing pool → the bonus drops back to +1.
            w.Despawn(p1);
            CombatSystem.Tick(w, defs);
            Assert.Equal(6, CombatSystem.DamageOf(w, defs, me, foe));
        }
    }

    public class EvoPointTests
    {
        [Fact]
        public void KillsAreTheOnlySourceOfEvoPointsAndTheyGateBuilding()
        {
            var sim = TestWorlds.NewSim(4, new CommandLog());
            var w = sim.World;
            var defs = sim.Defs;
            short sd = (short)defs.UnitIndex("test.soldier");
            int mut = defs.BuildingIndex("test.mutagen");

            Assert.Equal(0, (int)w.Players[0].EvoPoints); // nobody starts with any

            // Gathering nutrients all day earns none — only blood does.
            int worker = DeterminismTests.FindUnit(sim, 0, isWorker: true);
            for (int t = 0; t < 200; t++) sim.Tick();
            Assert.Equal(0, (int)w.Players[0].EvoPoints);

            // Too poor to build: the pool needs 3 evo points.
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.ConstructBuilding,
                A = worker, B = 1500, C = 1500, D = mut });
            Assert.Equal(1, w.RejectedCommands);

            // Land three kills → three points.
            int me = w.Spawn(EntityKind.Unit, sd, 0, new FixVec2(Fix.FromInt(31), Fix.FromInt(30)), 60);
            for (int k = 0; k < 3; k++)
            {
                int foe = w.Spawn(EntityKind.Unit, sd, 1, new FixVec2(Fix.FromInt(30), Fix.FromInt(30)), 1);
                for (int t = 0; t < 60 && w.Kind[foe] == EntityKind.Unit && w.Hp[foe] > 0; t++)
                    CombatSystem.Tick(w, defs);
                w.Despawn(foe);
            }
            Assert.Equal(3, (int)w.Players[0].EvoPoints);

            // Now affordable — and the points are spent.
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.ConstructBuilding,
                A = worker, B = 1500, C = 1500, D = mut });
            Assert.Equal(1, w.RejectedCommands); // no new rejection
            Assert.Equal(0, (int)w.Players[0].EvoPoints);
        }
    }

    public class KillBountyTests
    {
        [Fact]
        public void KillingAnEnemyUnitBanksTenPercentOfItsCost()
        {
            var defs = TestWorlds.TinyDefs();
            var w = new SimWorld(defs.Rules, 2, defs.Units.Length, defs.Upgrades.Length,
                Fix.FromInt(40), Fix.FromInt(40), 1);
            short sd = (short)defs.UnitIndex("test.soldier");
            int cost = defs.Units[sd].FoodCost;              // 60
            int expected = cost * defs.Rules.KillBountyNum / defs.Rules.KillBountyDen; // 10% = 6

            // Attacker in the victim's front arc so damage is the clean base value.
            int me = w.Spawn(EntityKind.Unit, sd, 0, new FixVec2(Fix.Ratio(2050, 100), Fix.FromInt(20)), 60);
            int foe = w.Spawn(EntityKind.Unit, sd, 1, new FixVec2(Fix.FromInt(20), Fix.FromInt(20)), 5); // nearly dead
            w.Players[0].Food = 0;

            // Chip it without killing: no bounty until it actually dies.
            w.Hp[foe] = 500;
            for (int t = 0; t < 10; t++) CombatSystem.Tick(w, defs);
            Assert.True(w.Hp[foe] < 500, "the attacker should be landing hits");
            Assert.Equal(0, (int)w.Players[0].Food);

            // Now let the kill land.
            w.Hp[foe] = 1;
            for (int t = 0; t < 60 && w.Kind[foe] == EntityKind.Unit && w.Hp[foe] > 0; t++)
                CombatSystem.Tick(w, defs);
            Assert.True(w.Hp[foe] <= 0, "the victim should be dead");
            Assert.Equal(expected, (int)w.Players[0].Food);

            // Corpses pay once: further hits before cleanup must not top it up again.
            for (int t = 0; t < 10; t++) CombatSystem.Tick(w, defs);
            Assert.Equal(expected, (int)w.Players[0].Food);
        }

        [Fact]
        public void NoBountyForKillingABuilding()
        {
            var defs = TestWorlds.TinyDefs();
            var w = new SimWorld(defs.Rules, 2, defs.Units.Length, defs.Upgrades.Length,
                Fix.FromInt(40), Fix.FromInt(40), 1);
            short sd = (short)defs.UnitIndex("test.soldier");
            w.Spawn(EntityKind.Unit, sd, 0, new FixVec2(Fix.Ratio(2100, 100), Fix.FromInt(20)), 60);
            int bld = w.Spawn(EntityKind.Building, (short)defs.BuildingIndex("test.nursery"), 1,
                new FixVec2(Fix.FromInt(20), Fix.FromInt(20)), 1);
            w.Players[0].Food = 0;

            for (int t = 0; t < 60 && w.Hp[bld] > 0; t++) CombatSystem.Tick(w, defs);
            Assert.True(w.Hp[bld] <= 0, "the building should be destroyed");
            Assert.Equal(0, (int)w.Players[0].Food); // structures carry no bounty
        }
    }

    public class TeamTests
    {
        // 4 players: 0+1 on team 0, 2+3 on team 1.
        private static Simulation TeamSim(ulong seed) =>
            new Simulation(TestWorlds.TinyDefs(), TestWorlds.TinyMap(), 4, seed, new CommandLog(),
                new byte[] { 0, 0, 1, 1 });

        [Fact]
        public void AlliesAreNotTargetsButRivalTeamsAre()
        {
            var sim = TeamSim(5);
            var w = sim.World;
            var defs = sim.Defs;
            short sd = (short)defs.UnitIndex("test.soldier");

            // Hostility routes through teams, not owners.
            Assert.False(w.AreEnemies(0, 1));                       // same team
            Assert.True(w.AreEnemies(0, 2));                        // across teams
            Assert.False(w.AreEnemies(0, SimWorld.NeutralOwner));   // nodes are never enemies
            Assert.True(w.IsFriendly(0, 1));
            Assert.False(w.IsFriendly(0, 2));

            // An ally standing in reach is never shot; a rival in the same spot is.
            int me = w.Spawn(EntityKind.Unit, sd, 0, new FixVec2(Fix.FromInt(20), Fix.FromInt(20)), 60);
            int ally = w.Spawn(EntityKind.Unit, sd, 1, new FixVec2(Fix.Ratio(2050, 100), Fix.FromInt(20)), 60);
            w.RebuildGrid(); // range queries read the grid; a tick would refresh it for us
            int hpBefore = w.Hp[ally];
            for (int t = 0; t < 60; t++) CombatSystem.Tick(w, defs);
            Assert.Equal(hpBefore, w.Hp[ally]); // allies never trade fire

            int foe = w.Spawn(EntityKind.Unit, sd, 2, new FixVec2(Fix.Ratio(1950, 100), Fix.FromInt(20)), 60);
            w.RebuildGrid();
            for (int t = 0; t < 60; t++) CombatSystem.Tick(w, defs);
            Assert.True(w.Hp[foe] < 60, "a rival team's unit should be attacked");
        }

        [Fact]
        public void VictoryIsLastTeamStandingNotLastPlayer()
        {
            var sim = TeamSim(6);
            var w = sim.World;
            Assert.Equal(2, sim.AliveTeams());
            Assert.Equal(-1, sim.WinningTeam()); // undecided while both teams live

            // Wipe team 1 (players 2 and 3): team 0 wins even though TWO players remain.
            w.Players[2].Alive = false;
            w.Players[3].Alive = false;
            Assert.Equal(1, sim.AliveTeams());
            Assert.Equal(0, sim.WinningTeam());
            Assert.Equal(2, sim.AlivePlayers());  // last-player-standing would still say "no winner"
            Assert.Equal(-1, sim.Winner());
        }
    }

    public class BotTests
    {
        [Fact]
        public void EightPlayerFreeForAllIsDeterministic()
        {
            // 1 human slot + 7 bots, everyone hostile to everyone: the whole 8-way match must
            // still be bit-identical across runs (bots are pure command sources).
            var a = RunFfa(11, 8, 3000, TestWorlds.TinyMap());
            var b = RunFfa(11, 8, 3000, TestWorlds.TinyMap());
            Assert.Equal(a.Hash, b.Hash);
            Assert.Equal(a.Commands, b.Commands);
            Assert.True(a.Commands > 0, "no bot ever issued a command in the free-for-all");
        }

        [Fact]
        [Trait("Category", "Slow")]
        public void ThirtyTwoPlayerFreeForAllIsDeterministic()
        {
            // The largest supported match: 32 seats, 31 bots, all hostile. Still bit-identical.
            var a = RunFfa(12, 32, 1500, TestWorlds.BigMap(32));
            var b = RunFfa(12, 32, 1500, TestWorlds.BigMap(32));
            Assert.Equal(a.Hash, b.Hash);
            Assert.Equal(a.Commands, b.Commands);
            Assert.True(a.Commands > 0, "no bot ever issued a command in the 32-way match");
        }

        private static BotRun RunFfa(ulong seed, int players, int ticks, MapDef map)
        {
            var log = new CommandLog();
            var sim = new Simulation(TestWorlds.TinyDefs(), map, players, seed, log);
            var bots = new BotController[players];
            for (byte p = 1; p < players; p++) bots[p] = new BotController(p, seed); // p0 = the human seat
            var buffer = new System.Collections.Generic.List<Command>();
            int commands = 0;
            for (int t = 0; t < ticks; t++)
            {
                buffer.Clear();
                for (byte p = 1; p < players; p++) bots[p].Think(sim.World, sim.Defs, buffer);
                for (int k = 0; k < buffer.Count; k++)
                {
                    var c = buffer[k];
                    c.Tick = sim.TickCount;
                    log.Add(c);
                    commands++;
                }
                sim.Tick();
                if (sim.AlivePlayers() <= 1) break;
            }
            return new BotRun { Hash = sim.StateHash(), Commands = commands };
        }

        [Fact]
        public void BotMirrorMatchIsDeterministicAndLaunchesWaves()
        {
            var a = RunBotMatch(99, 9000);
            var b = RunBotMatch(99, 9000);
            Assert.Equal(a.Hash, b.Hash);             // bots are pure command sources
            Assert.Equal(a.Attacks, b.Attacks);
            Assert.True(a.Attacks > 0, "bot never launched an attack wave");
            Assert.True(a.Commands > 0, "bot issued no commands at all");
        }

        [Fact]
        public void BotNeverPicksTheFreeSpawnerAsItsMilitaryBuilding()
        {
            // test.broodsac sorts FIRST among buildings and produces a (free) combat unit —
            // without the paid-produce rule the bot would pick chaff sacs as its military
            // expansion. Keep the bot rich so its expansion branch actually fires.
            var log = new CommandLog();
            var sim = TestWorlds.NewSim(7, log);
            var bots = new[] { new BotController(0, 7), new BotController(1, 7) };
            var buffer = new System.Collections.Generic.List<Command>();
            int broodsac = sim.Defs.BuildingIndex("test.broodsac");
            int nursery = sim.Defs.BuildingIndex("test.nursery");
            int constructs = 0;
            for (int t = 0; t < 1200; t++)
            {
                sim.World.Players[0].Food = 5000; // rich: the "second military building" path opens
                sim.World.Players[1].Food = 5000;
                buffer.Clear();
                bots[0].Think(sim.World, sim.Defs, buffer);
                bots[1].Think(sim.World, sim.Defs, buffer);
                for (int k = 0; k < buffer.Count; k++)
                {
                    var c = buffer[k];
                    c.Tick = sim.TickCount;
                    log.Add(c);
                    if (c.Type == CommandType.ConstructBuilding)
                    {
                        constructs++;
                        Assert.NotEqual(broodsac, c.D); // free spawners are never the army pick
                        Assert.Equal(nursery, c.D);     // the real producer is
                    }
                }
                sim.Tick();
                if (sim.AlivePlayers() <= 1) break;
            }
            Assert.True(constructs > 0, "bot never tried to expand at all");
        }

        private struct BotRun { public ulong Hash; public int Attacks; public int Commands; }

        private static BotRun RunBotMatch(ulong seed, int ticks)
        {
            var log = new CommandLog();
            var sim = TestWorlds.NewSim(seed, log);
            var bots = new[] { new BotController(0, seed), new BotController(1, seed) };
            var buffer = new System.Collections.Generic.List<Command>();
            int attacks = 0, commands = 0;
            for (int t = 0; t < ticks; t++)
            {
                buffer.Clear();
                bots[0].Think(sim.World, sim.Defs, buffer);
                bots[1].Think(sim.World, sim.Defs, buffer);
                for (int k = 0; k < buffer.Count; k++)
                {
                    var c = buffer[k];
                    c.Tick = sim.TickCount;
                    log.Add(c);
                    commands++;
                    if (c.Type == CommandType.AttackMove) attacks++;
                }
                sim.Tick();
                if (sim.AlivePlayers() <= 1) break;
            }
            return new BotRun { Hash = sim.StateHash(), Attacks = attacks, Commands = commands };
        }
    }

    public class CacheTierTests
    {
        private static SimWorld NewWorld(DefDatabase defs, int players = 1) =>
            new SimWorld(defs.Rules, players, defs.Units.Length, defs.Upgrades.Length, Fix.FromInt(40), Fix.FromInt(40), 1);

        [Fact]
        public void UpgradeDoublesStockAndHpAndEscalatesCost()
        {
            var defs = TestWorlds.TinyDefs();
            var w = NewWorld(defs);
            int cacheDef = defs.BuildingIndex("test.cache"); // MaxHp 200, StockCapacity 50
            int cache = w.Spawn(EntityKind.Building, (short)cacheDef, 0, new FixVec2(Fix.FromInt(10), Fix.FromInt(10)), 200);
            int hq = w.Spawn(EntityKind.Building, (short)defs.BuildingIndex("test.hq"), 0, new FixVec2(Fix.FromInt(5), Fix.FromInt(5)), 500);
            w.Players[0].Food = 350;

            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.UpgradeCache, A = cache });
            Assert.Equal(0, w.RejectedCommands);
            Assert.Equal(1, (int)w.Tier[cache]);
            Assert.Equal(250, (int)w.Players[0].Food); // tier 1 costs 100
            Assert.Equal(400, w.Hp[cache]);            // max HP doubled, healed on the spot
            Assert.Equal(100, SupplySystem.StockCapOf(w, defs, cache)); // capacity doubled

            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.UpgradeCache, A = cache });
            Assert.Equal(2, (int)w.Tier[cache]);
            Assert.Equal(50, (int)w.Players[0].Food);  // tier 2 costs 200 (doubles per tier)
            Assert.Equal(800, w.Hp[cache]);
            Assert.Equal(200, SupplySystem.StockCapOf(w, defs, cache));

            // Too poor for tier 3 (400f) — rejected, nothing changes.
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.UpgradeCache, A = cache });
            Assert.Equal(1, w.RejectedCommands);
            Assert.Equal(2, (int)w.Tier[cache]);

            // The HQ is not a cache — rejected.
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.UpgradeCache, A = hq });
            Assert.Equal(2, w.RejectedCommands);

            // Tier cap: buy tier 3, then the next attempt rejects even with food on hand.
            w.Players[0].Food = 10000;
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.UpgradeCache, A = cache });
            Assert.Equal(w.Rules.CacheMaxTier, (int)w.Tier[cache]);
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.UpgradeCache, A = cache });
            Assert.Equal(3, w.RejectedCommands);
        }

        [Fact]
        public void TieredCacheHalvesSupplyDrain()
        {
            var defs = TestWorlds.TinyDefs();
            var w = NewWorld(defs);
            w.Spawn(EntityKind.Building, (short)defs.BuildingIndex("test.hq"), 0, new FixVec2(Fix.FromInt(5), Fix.FromInt(5)), 500);
            int cache = w.Spawn(EntityKind.Building, (short)defs.BuildingIndex("test.cache"), 0, new FixVec2(Fix.FromInt(20), Fix.FromInt(5)), 200);
            w.Spawn(EntityKind.Unit, 1, 0, new FixVec2(Fix.FromInt(20), Fix.FromInt(6)), 60); // soldier by the cache

            // Base tier: 1 food per SupplyDrainTicks — a 2x window costs exactly 2.
            w.DepotStock[cache] = 50;
            for (int t = 0; t < w.Rules.SupplyDrainTicks * 2; t++) { SupplySystem.Tick(w, defs); w.TickCount++; }
            Assert.Equal(48, w.DepotStock[cache]);

            // Tier 1: the drain interval doubles — the same window costs exactly 1.
            w.Tier[cache] = 1;
            w.DepotStock[cache] = 50;
            for (int t = 0; t < w.Rules.SupplyDrainTicks * 2; t++) { SupplySystem.Tick(w, defs); w.TickCount++; }
            Assert.Equal(49, w.DepotStock[cache]);
        }

        [Fact]
        public void TieredCacheShootsBackBaseCacheDoesNot()
        {
            var defs = TestWorlds.TinyDefs();
            var w = NewWorld(defs, 2);
            int cache = w.Spawn(EntityKind.Building, (short)defs.BuildingIndex("test.cache"), 0, new FixVec2(Fix.FromInt(10), Fix.FromInt(10)), 200);
            int enemy = w.Spawn(EntityKind.Unit, 1, 1, new FixVec2(Fix.FromInt(12), Fix.FromInt(10)), 60);

            CombatSystem.Tick(w, defs);
            Assert.Equal(60, w.Hp[enemy]); // an unupgraded cache is defenseless

            w.Tier[cache] = 1;
            CombatSystem.Tick(w, defs);
            Assert.Equal(60 - w.Rules.CacheAttackDamage, w.Hp[enemy]);
            Assert.Equal(w.Rules.CacheAttackCooldownTicks, w.AttackCooldown[cache]);
            Assert.Contains(w.AttackEvents, ev => ev.Attacker == cache && ev.Target == enemy);
        }
    }

    public class HubProngTests
    {
        private static SimWorld NewWorld(DefDatabase defs) =>
            new SimWorld(defs.Rules, 1, defs.Units.Length, defs.Upgrades.Length,
                Fix.FromInt(60), Fix.FromInt(60), 1);

        private static int FindBuilding(SimWorld w, byte owner, int def)
        {
            for (int i = 0; i < w.HighWater; i++)
                if (w.Kind[i] == EntityKind.Building && w.Owner[i] == owner && w.DefIndex[i] == def) return i;
            return -1;
        }

        [Fact]
        public void HeadquartersGrowsSelfBuildingProng()
        {
            var defs = TestWorlds.TinyDefs();
            var w = NewWorld(defs);
            int hq = w.Spawn(EntityKind.Building, (short)defs.BuildingIndex("test.hq"), 0, new FixVec2(Fix.FromInt(30), Fix.FromInt(30)), 500);
            int prongDef = defs.BuildingIndex("test.prong");
            w.Players[0].Food = 500;
            w.Players[0].Minerals = 100;

            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.BuildProng, A = hq, B = prongDef });
            Assert.Equal(0, w.RejectedCommands);
            Assert.Equal(420, (int)w.Players[0].Food);     // 500 - 80
            Assert.Equal(50, (int)w.Players[0].Minerals);  // 100 - 50 mineral cost

            int prong = FindBuilding(w, 0, prongDef);
            Assert.True(prong >= 0 && prong != hq);
            Assert.Equal(defs.Buildings[prongDef].BuildTimeTicks * 3, w.ConstructionRemaining[prong]); // 90 work units
            // Placed adjacent to the hub (not overlapping, within a couple of building radii).
            Assert.True((w.Pos[prong] - w.Pos[hq]).LengthSq.Raw > 0);

            // A worker CANNOT be assigned to a hub prong (it self-builds), and a duplicate rejects.
            int worker = w.Spawn(EntityKind.Unit, (short)defs.UnitIndex("test.worker"), 0, w.Pos[prong], 30);
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.AssignBuild, A = worker, B = prong });
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.BuildProng, A = hq, B = prongDef });
            Assert.Equal(2, w.RejectedCommands);

            // Self-builds with NO worker crew, finishing in exactly BuildTimeTicks (rate 3/tick).
            for (int t = 0; t < defs.Buildings[prongDef].BuildTimeTicks && w.ConstructionRemaining[prong] > 0; t++)
                WorkerSystem.Tick(w, defs);
            Assert.Equal(0, w.ConstructionRemaining[prong]);
            Assert.Equal(EntityKind.Building, w.Kind[prong]);
        }

        [Fact]
        public void WorkersDropOffAtFinishedProng()
        {
            var defs = TestWorlds.TinyDefs();
            var w = NewWorld(defs);
            int workerDef = defs.UnitIndex("test.worker");
            w.Spawn(EntityKind.Building, (short)defs.BuildingIndex("test.hq"), 0, new FixVec2(Fix.FromInt(5), Fix.FromInt(5)), 500);        // far corner
            w.Spawn(EntityKind.Building, (short)defs.BuildingIndex("test.prong"), 0, new FixVec2(Fix.FromInt(45), Fix.FromInt(45)), 200);   // finished prong
            int worker = w.Spawn(EntityKind.Unit, (short)workerDef, 0, new FixVec2(Fix.FromInt(45), Fix.FromInt(45)), 30);
            w.Carry[worker] = defs.Units[workerDef].CarryCapacity; // arrives full, next to the prong
            long before = w.Players[0].Food;

            WorkerSystem.Tick(w, defs);

            // Deposited at the prong (the HQ is across the map) — prongs extend the core.
            Assert.Equal(0, w.Carry[worker]);
            Assert.Equal(before + defs.Units[workerDef].CarryCapacity, w.Players[0].Food);
        }

        [Fact]
        public void WorkersMineMineralNodesIntoTheMineralsPool()
        {
            var defs = TestWorlds.TinyDefs();
            var w = NewWorld(defs);
            w.Spawn(EntityKind.Building, (short)defs.BuildingIndex("test.hq"), 0, new FixVec2(Fix.FromInt(10), Fix.FromInt(10)), 500);
            int node = w.Spawn(EntityKind.Node, 0, SimWorld.NeutralOwner, new FixVec2(Fix.FromInt(13), Fix.FromInt(10)), 1);
            w.NodeFood[node] = 100; w.NodeMineral[node] = true;
            int worker = w.Spawn(EntityKind.Unit, (short)defs.UnitIndex("test.worker"), 0, new FixVec2(Fix.FromInt(13), Fix.FromInt(10)), 30);
            w.WorkNode[worker] = node;

            for (int t = 0; t < 400 && w.Players[0].Minerals == 0; t++) WorkerSystem.Tick(w, defs);
            Assert.True(w.Players[0].Minerals > 0, "minerals never banked");
            Assert.Equal(0, w.Players[0].Food); // a mineral haul must not touch the nutrients pool
        }

        [Fact]
        public void ProngRejectsWithoutHqOrFunds()
        {
            var defs = TestWorlds.TinyDefs();
            var w = NewWorld(defs);
            int prongDef = defs.BuildingIndex("test.prong");
            // A non-HQ building can't grow prongs.
            int nursery = w.Spawn(EntityKind.Building, (short)defs.BuildingIndex("test.nursery"), 0, new FixVec2(Fix.FromInt(30), Fix.FromInt(30)), 300);
            w.Players[0].Food = 500;
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.BuildProng, A = nursery, B = prongDef });
            Assert.Equal(1, w.RejectedCommands);

            // HQ present but too poor.
            int hq = w.Spawn(EntityKind.Building, (short)defs.BuildingIndex("test.hq"), 0, new FixVec2(Fix.FromInt(10), Fix.FromInt(10)), 500);
            w.Players[0].Food = 40;
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.BuildProng, A = hq, B = prongDef });
            Assert.Equal(2, w.RejectedCommands);
            Assert.Equal(-1, FindBuilding(w, 0, prongDef));

            // A non-hub-built def is not a valid prong.
            w.Players[0].Food = 500;
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.BuildProng, A = hq, B = defs.BuildingIndex("test.nursery") });
            Assert.Equal(3, w.RejectedCommands);

            // Enough food but no minerals → rejects (prongs are paid in minerals).
            w.Players[0].Food = 500; w.Players[0].Minerals = 0;
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.BuildProng, A = hq, B = prongDef });
            Assert.Equal(4, w.RejectedCommands);
            Assert.Equal(-1, FindBuilding(w, 0, prongDef));
        }
    }

    public class TechUpgradeTests
    {
        private static SimWorld NewWorld(DefDatabase defs, int players = 2) =>
            new SimWorld(defs.Rules, players, defs.Units.Length, defs.Upgrades.Length,
                Fix.FromInt(40), Fix.FromInt(40), 1);

        // Spawn an operational nursery for the player so path upgrades can be bought.
        private static void GiveNursery(SimWorld w, DefDatabase defs, byte owner, int x, int y) =>
            w.Spawn(EntityKind.Building, (short)defs.BuildingIndex("test.nursery"), owner,
                new FixVec2(Fix.FromInt(x), Fix.FromInt(y)), 300);

        [Fact]
        public void BuyUpgradeValidatesBuildingCostAndDoubleBuy()
        {
            var defs = TestWorlds.TinyDefs();
            var w = NewWorld(defs);
            int up = defs.UpgradeIndex("test.up-damage");
            w.Players[0].Food = 40; // below the 50 cost

            // No path building yet → reject.
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.BuyUpgrade, A = up });
            Assert.Equal(1, w.RejectedCommands);

            GiveNursery(w, defs, 0, 15, 15);
            // Building present but too poor → reject.
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.BuyUpgrade, A = up });
            Assert.Equal(2, w.RejectedCommands);
            Assert.Equal(0, (int)w.Players[0].UpgradeLevels[up]);

            // Affordable now → buys, spends, records.
            w.Players[0].Food = 200;
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.BuyUpgrade, A = up });
            Assert.Equal(2, w.RejectedCommands);
            Assert.Equal(150, (int)w.Players[0].Food);
            Assert.Equal(1, (int)w.Players[0].UpgradeLevels[up]);

            // Re-buying the same upgrade rejects (and out-of-range rejects).
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.BuyUpgrade, A = up });
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.BuyUpgrade, A = 999 });
            Assert.Equal(4, w.RejectedCommands);
        }

        [Fact]
        public void DamageAndArmorUpgradesFoldIntoDamage()
        {
            var defs = TestWorlds.TinyDefs();
            var w = NewWorld(defs);
            int sd = defs.UnitIndex("test.soldier");
            // Attacker (P0) in the target's FRONT arc (target faces +x by default) so the arc
            // multiplier is ×1 and we read the clean base damage.
            int atk = w.Spawn(EntityKind.Unit, (short)sd, 0, new FixVec2(Fix.FromInt(7), Fix.FromInt(5)), 60);
            int tgt = w.Spawn(EntityKind.Unit, (short)sd, 1, new FixVec2(Fix.FromInt(6), Fix.FromInt(5)), 60);
            Assert.Equal(5, CombatSystem.DamageOf(w, defs, atk, tgt)); // base test.soldier damage

            GiveNursery(w, defs, 0, 15, 15);
            GiveNursery(w, defs, 1, 25, 25);
            w.Players[0].Food = 200; w.Players[1].Food = 200;

            // Attacker's damage upgrade doubles outgoing.
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.BuyUpgrade, A = defs.UpgradeIndex("test.up-damage") });
            Assert.Equal(10, CombatSystem.DamageOf(w, defs, atk, tgt));

            // Target's armor upgrade halves incoming: 10 × 1/2 = 5 (one floor).
            CommandSystem.Apply(w, defs, new Command { Player = 1, Type = CommandType.BuyUpgrade, A = defs.UpgradeIndex("test.up-armor") });
            Assert.Equal(5, CombatSystem.DamageOf(w, defs, atk, tgt));
        }

        [Fact]
        public void MoveSpeedCooldownAndRangeUpgradesApply()
        {
            var defs = TestWorlds.TinyDefs();
            var w = NewWorld(defs, 1);
            int sd = defs.UnitIndex("test.soldier");
            int u = w.Spawn(EntityKind.Unit, (short)sd, 0, new FixVec2(Fix.FromInt(5), Fix.FromInt(5)), 60);
            GiveNursery(w, defs, 0, 15, 15);
            w.Players[0].Food = 1000;

            int moveCenti = defs.Units[sd].MoveSpeedCenti;
            int baseCd = CombatSystem.CooldownFor(w, defs, u);
            int baseReach = UpgradeSystem.ScaleCenti(w, defs, 0, sd, defs.Units[sd].AttackRangeCenti, UpgradeStat.AttackRange);

            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.BuyUpgrade, A = defs.UpgradeIndex("test.up-speed") });
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.BuyUpgrade, A = defs.UpgradeIndex("test.up-haste") });
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.BuyUpgrade, A = defs.UpgradeIndex("test.up-reach") });
            Assert.Equal(0, w.RejectedCommands);

            // Move folds the ×2 before the single divide (one floor) — matches doubling the centi.
            Assert.Equal(MovementSystem.PerTickStep(moveCenti * 2).Raw, MovementSystem.StepFor(w, defs, u).Raw);
            Assert.Equal(baseCd / 2, CombatSystem.CooldownFor(w, defs, u));               // cooldown ×1/2
            Assert.Equal(baseReach * 2, UpgradeSystem.ScaleCenti(w, defs, 0, sd, defs.Units[sd].AttackRangeCenti, UpgradeStat.AttackRange)); // reach ×2
        }
    }

    public class OrderQueueTests
    {
        private static SimWorld NewWorld(DefDatabase defs) =>
            new SimWorld(defs.Rules, 1, defs.Units.Length, defs.Upgrades.Length, Fix.FromInt(40), Fix.FromInt(40), 1);

        [Fact]
        public void QueuedMovesRunInSequence()
        {
            var defs = TestWorlds.TinyDefs();
            var w = NewWorld(defs);
            int u = w.Spawn(EntityKind.Unit, 1, 0, new FixVec2(Fix.FromInt(5), Fix.FromInt(5)), 30); // test.soldier

            // First order starts immediately; shift-queued ones wait behind it.
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.Move, A = u, B = 700, C = 500 });
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.Move, A = u, B = 700, C = 700, D = 1 });
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.Move, A = u, B = 300, C = 700, D = 1 });
            Assert.Equal(0, w.RejectedCommands);
            Assert.True(w.HasMoveOrder[u]);
            Assert.Equal(Fix.Ratio(700, 100).Raw, w.MoveTarget[u].X.Raw);
            Assert.Equal(2, (int)w.QueueCount[u]);

            for (int t = 0; t < 800 && (w.HasMoveOrder[u] || w.QueueCount[u] > 0); t++)
                MovementSystem.Tick(w, defs);
            Assert.False(w.HasMoveOrder[u]);
            Assert.Equal(0, (int)w.QueueCount[u]);
            Assert.Equal(Fix.Ratio(300, 100).Raw, w.Pos[u].X.Raw); // ended at the LAST waypoint
            Assert.Equal(Fix.Ratio(700, 100).Raw, w.Pos[u].Y.Raw);
        }

        [Fact]
        public void DirectOrderOrStopWipesTheQueue()
        {
            var defs = TestWorlds.TinyDefs();
            var w = NewWorld(defs);
            int u = w.Spawn(EntityKind.Unit, 1, 0, new FixVec2(Fix.FromInt(5), Fix.FromInt(5)), 30);

            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.Move, A = u, B = 700, C = 500 });
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.Move, A = u, B = 700, C = 700, D = 1 });
            Assert.Equal(1, (int)w.QueueCount[u]);

            // A direct (unshifted) order replaces the whole plan.
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.Move, A = u, B = 900, C = 900 });
            Assert.Equal(0, (int)w.QueueCount[u]);
            Assert.Equal(Fix.Ratio(900, 100).Raw, w.MoveTarget[u].X.Raw);

            // Stop scraps the queue too.
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.Move, A = u, B = 700, C = 700, D = 1 });
            Assert.Equal(1, (int)w.QueueCount[u]);
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.Stop, A = u });
            Assert.False(w.HasMoveOrder[u]);
            Assert.Equal(0, (int)w.QueueCount[u]);
        }

        [Fact]
        public void QueueOverflowRejects()
        {
            var defs = TestWorlds.TinyDefs();
            var w = NewWorld(defs);
            int u = w.Spawn(EntityKind.Unit, 1, 0, new FixVec2(Fix.FromInt(5), Fix.FromInt(5)), 30);

            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.Move, A = u, B = 700, C = 500 });
            for (int q = 0; q < SimConstants.MaxOrderQueue; q++)
                CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.Move, A = u, B = 700 + q, C = 700, D = 1 });
            Assert.Equal(0, w.RejectedCommands);
            Assert.Equal(SimConstants.MaxOrderQueue, (int)w.QueueCount[u]);

            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.Move, A = u, B = 999, C = 999, D = 1 });
            Assert.Equal(1, w.RejectedCommands);
            Assert.Equal(SimConstants.MaxOrderQueue, (int)w.QueueCount[u]);
        }

        [Fact]
        public void QueuedLegAfterAttackMoveStarts()
        {
            // An armed loose unit attack-moves (CombatSystem drives it), then a queued plain
            // move must start once the attack-move leg completes.
            var defs = TestWorlds.TinyDefs();
            var w = NewWorld(defs);
            int u = w.Spawn(EntityKind.Unit, 1, 0, new FixVec2(Fix.FromInt(5), Fix.FromInt(5)), 30);

            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.AttackMove, A = u, B = 700, C = 500 });
            Assert.True(w.AttackMove[u]);
            Assert.False(w.HasMoveOrder[u]);
            CommandSystem.Apply(w, defs, new Command { Player = 0, Type = CommandType.Move, A = u, B = 300, C = 500, D = 1 });
            Assert.Equal(1, (int)w.QueueCount[u]);

            for (int t = 0; t < 800 && (w.AttackMove[u] || w.HasMoveOrder[u] || w.QueueCount[u] > 0); t++)
            {
                CombatSystem.Tick(w, defs);
                MovementSystem.Tick(w, defs);
            }
            Assert.Equal(0, (int)w.QueueCount[u]);
            Assert.Equal(Fix.Ratio(300, 100).Raw, w.Pos[u].X.Raw); // finished the queued leg
        }
    }

    public class SpawnReuseTests
    {
        [Fact]
        public void ReusedEntityIndexStartsFullyReset()
        {
            var defs = TestWorlds.TinyDefs();
            var w = new SimWorld(defs.Rules, 2, defs.Units.Length, defs.Upgrades.Length, Fix.FromInt(40), Fix.FromInt(40), 1);
            int e = w.Spawn(EntityKind.Unit, 1, 0, new FixVec2(Fix.FromInt(5), Fix.FromInt(5)), 30);
            w.Carry[e] = 7;
            w.GatherTimer[e] = 9;
            w.WorkNode[e] = 3;
            w.HasMoveOrder[e] = true;
            w.AttackMove[e] = true;
            w.SupplyTicks[e] = 1;
            w.DepotStock[e] = 9;
            w.CaravanCache[e] = 4;
            w.ProduceOverride[e] = 2;
            w.ProducePaused[e] = true;
            w.HasRally[e] = true;
            w.ConstructionRemaining[e] = 9;
            w.BuildTask[e] = 4;
            w.QueueCount[e] = 3;
            w.Tier[e] = 2;
            w.Despawn(e);

            int e2 = w.Spawn(EntityKind.Unit, 0, 1, new FixVec2(Fix.FromInt(1), Fix.FromInt(1)), 60);
            Assert.Equal(e, e2); // lowest-free-index reuse
            Assert.Equal(0, w.Carry[e2]);
            Assert.Equal(0, w.GatherTimer[e2]);
            Assert.Equal(-1, w.WorkNode[e2]);
            Assert.False(w.HasMoveOrder[e2]);
            Assert.False(w.AttackMove[e2]);
            Assert.Equal(w.Rules.SupplyGraceTicks, w.SupplyTicks[e2]);
            Assert.Equal(0, w.DepotStock[e2]);
            Assert.Equal(-1, w.CaravanCache[e2]);
            Assert.Equal(-1, w.ProduceOverride[e2]);
            Assert.False(w.ProducePaused[e2]);
            Assert.False(w.HasRally[e2]);
            Assert.Equal(0, w.ConstructionRemaining[e2]);
            Assert.Equal(-1, w.BuildTask[e2]);
            Assert.Equal(0, (int)w.QueueCount[e2]);
            Assert.Equal(0, (int)w.Tier[e2]);
        }
    }

    public class CombatOrderAndSupplyTests
    {
        private const short SoldierDef = 1; // TinyDefs sorted ids: test.leader, test.soldier, test.worker

        [Fact]
        public void AttackMoveAdvancesThenDivertsToEngage()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int soldier = w.Spawn(EntityKind.Unit, SoldierDef, 0, new FixVec2(Fix.FromInt(5), Fix.FromInt(20)), 60);

            // Attack-move far to the right; with no enemies it should march toward the point.
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.AttackMove, A = soldier, B = 3800, C = 2000 });
            Assert.Equal(0, w.RejectedCommands);
            Assert.True(w.AttackMove[soldier]);
            Assert.False(w.HasMoveOrder[soldier]); // combat owns the movement
            Fix startX = w.Pos[soldier].X;
            for (int t = 0; t < 40; t++) sim.Tick();
            Assert.True(w.Pos[soldier].X > startX, "attack-move unit should advance toward the point");

            // Drop an enemy in its path; it must divert and attack rather than walk past.
            int enemy = w.Spawn(EntityKind.Unit, SoldierDef, 1, new FixVec2(w.Pos[soldier].X + Fix.FromInt(3), w.Pos[soldier].Y), 60);
            int hpBefore = w.Hp[enemy];
            for (int t = 0; t < 120; t++) sim.Tick();
            Assert.True(w.Hp[enemy] < hpBefore, "attack-move unit should engage an enemy in its path");
        }

        [Fact]
        public void UnitsTurnTowardTheirHeadingAtTurnSpeed()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int s = w.Spawn(EntityKind.Unit, SoldierDef, 0, new FixVec2(Fix.FromInt(20), Fix.FromInt(20)), 60);
            Assert.Equal(Fix.One, w.Facing[s].X); // spawns facing +x

            // Order it due +y: facing rotates over ticks instead of snapping.
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.Move, A = s, B = 2000, C = 3800 });
            sim.Tick();
            Assert.True(w.Facing[s].Y > Fix.Zero, "turn should have begun");
            Assert.True(w.Facing[s].X > Fix.Zero, "a 90° turn cannot complete in one tick");

            for (int t = 0; t < 40; t++) sim.Tick();
            Assert.True(w.Facing[s].Y > Fix.Ratio(99, 100), "facing should have converged to +y");

            // The chord-turn helper stays on the unit circle and snaps exactly when close.
            var turned = FixVec2.TurnTowards(new FixVec2(Fix.One, Fix.Zero), new FixVec2(Fix.Zero, Fix.FromInt(5)), Fix.FromInt(3));
            Assert.Equal(Fix.Zero, turned.X);
            Assert.Equal(Fix.One, turned.Y);
        }

        [Fact]
        public void LandedHitsEmitTransientAttackEvents()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int a = w.Spawn(EntityKind.Unit, SoldierDef, 0, new FixVec2(Fix.FromInt(20), Fix.FromInt(20)), 60);
            int b = w.Spawn(EntityKind.Unit, SoldierDef, 1, new FixVec2(Fix.FromInt(20) + Fix.Ratio(1, 2), Fix.FromInt(20)), 60);
            sim.Tick(); // touching, cooldowns start at 0 → both swing this tick
            Assert.Contains(w.AttackEvents, ev => ev.Attacker == a && ev.Target == b);
            Assert.Contains(w.AttackEvents, ev => ev.Attacker == b && ev.Target == a);
            // The feed is transient: a tick with no hits (cooldowns running) clears it.
            sim.Tick();
            Assert.Empty(w.AttackEvents);
        }

        [Fact]
        public void PlainMoveDoesNotChaseEnemies()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int soldier = w.Spawn(EntityKind.Unit, SoldierDef, 0, new FixVec2(Fix.FromInt(5), Fix.FromInt(20)), 60);
            // An enemy sits off to the side; a plain move past it must not divert to chase.
            int enemy = w.Spawn(EntityKind.Unit, SoldierDef, 1, new FixVec2(Fix.FromInt(20), Fix.FromInt(34)), 60);
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.Move, A = soldier, B = 3800, C = 2000 });
            Assert.True(w.HasMoveOrder[soldier]);
            Assert.False(w.AttackMove[soldier]);
            for (int t = 0; t < 60; t++) sim.Tick();
            // It kept marching east; it did not veer up toward the enemy.
            Assert.True(w.Pos[soldier].Y < Fix.FromInt(28), "plain move must not chase the flanking enemy");
        }

        [Fact]
        public void FlankAndRearAttacksDealBonusDamage()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            // Victim faces +x (spawn default). Base damage 5; no squad bonus on either side.
            int victim = w.Spawn(EntityKind.Unit, SoldierDef, 1, new FixVec2(Fix.FromInt(20), Fix.FromInt(20)), 60);
            int baseDmg = sim.Defs.Units[SoldierDef].AttackDamage;

            // Attacker in front (+x of the victim): full front arc, ×1.
            int front = w.Spawn(EntityKind.Unit, SoldierDef, 0, new FixVec2(Fix.FromInt(25), Fix.FromInt(20)), 60);
            Assert.Equal(0, CombatSystem.ArcOf(w, victim, w.Pos[front]));
            Assert.Equal(baseDmg, CombatSystem.DamageOf(w, sim.Defs, front, victim));

            // Attacker to the side (+y): side arc, ×5/4.
            int side = w.Spawn(EntityKind.Unit, SoldierDef, 0, new FixVec2(Fix.FromInt(20), Fix.FromInt(25)), 60);
            Assert.Equal(1, CombatSystem.ArcOf(w, victim, w.Pos[side]));
            Assert.Equal(baseDmg * 5 / 4, CombatSystem.DamageOf(w, sim.Defs, side, victim));

            // Attacker behind (-x): rear arc, ×3/2.
            int rear = w.Spawn(EntityKind.Unit, SoldierDef, 0, new FixVec2(Fix.FromInt(15), Fix.FromInt(20)), 60);
            Assert.Equal(2, CombatSystem.ArcOf(w, victim, w.Pos[rear]));
            Assert.Equal(baseDmg * 3 / 2, CombatSystem.DamageOf(w, sim.Defs, rear, victim));

            // Turning the victim to face the rear attacker flips it to a frontal hit.
            w.Facing[victim] = new FixVec2(-Fix.One, Fix.Zero);
            Assert.Equal(0, CombatSystem.ArcOf(w, victim, w.Pos[rear]));
        }

        [Fact]
        public void SupplyChainConnectsThroughDepotsAndCutsWhenBroken()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            short cacheDef = (short)sim.Defs.BuildingIndex("test.cache");
            // HQ0 sits at (6,6); the chain hops mid (6,26) then far (30,26) — both hops < 30u.
            int mid = w.Spawn(EntityKind.Building, cacheDef, 0, new FixVec2(Fix.FromInt(6), Fix.FromInt(26)), 200);
            int far = w.Spawn(EntityKind.Building, cacheDef, 0, new FixVec2(Fix.FromInt(30), Fix.FromInt(26)), 200);
            w.DepotStock[mid] = 50; // stocked by hand: this test is about CONNECTIVITY
            w.DepotStock[far] = 50;
            int s = w.Spawn(EntityKind.Unit, SoldierDef, 0, new FixVec2(Fix.FromInt(32), Fix.FromInt(28)), 60);

            sim.Tick();
            Assert.True(w.ScratchConnected[mid], "mid cache links to the HQ");
            Assert.True(w.ScratchConnected[far], "far cache links through the mid cache");
            Assert.Equal(w.Rules.SupplyGraceTicks, w.SupplyTicks[s]); // inside the far cache's radius

            // Raid the middle of the line: the far cache goes dark, the soldier starts draining.
            w.Despawn(mid);
            sim.Tick();
            Assert.False(w.ScratchConnected[far], "cutting the chain isolates the forward depot");
            Assert.True(w.SupplyTicks[s] < w.Rules.SupplyGraceTicks, "unit off supply starts draining");
        }

        [Fact]
        public void ArmiesDrainTheirDepotDryAndThenLoseSupply()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            // No workforce, no resupply: this test isolates the DRAIN side. (The starting
            // workers would otherwise caravan food out — the feature under test elsewhere.)
            for (int i = 0; i < w.HighWater; i++)
                if (w.Kind[i] == EntityKind.Unit && w.Owner[i] == 0 && sim.Defs.Units[w.DefIndex[i]].IsWorker)
                    w.Despawn(i);
            int hq0 = WorkerSystem.FindHq(w, sim.Defs, 0);
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetProducePaused, A = hq0, B = 1 });

            short cacheDef = (short)sim.Defs.BuildingIndex("test.cache");
            int cache = w.Spawn(EntityKind.Building, cacheDef, 0, new FixVec2(Fix.FromInt(6), Fix.FromInt(26)), 200);
            w.DepotStock[cache] = 2; // nearly dry forward depot
            int s = w.Spawn(EntityKind.Unit, SoldierDef, 0, new FixVec2(Fix.FromInt(7), Fix.FromInt(27)), 60);

            // Camped in the cache's radius: each drain interval eats 1 stock.
            for (int t = 0; t < w.Rules.SupplyDrainTicks * 3; t++) sim.Tick();
            Assert.Equal(0, w.DepotStock[cache]);
            // Dry depot supplies nothing: the soldier's grace is now running down.
            Assert.True(w.SupplyTicks[s] < w.Rules.SupplyGraceTicks, "dry cache must stop supplying");
        }

        [Fact]
        public void PerEntityDialSetsValidatesAndResets()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int s = w.Spawn(EntityKind.Unit, SoldierDef, 0, new FixVec2(Fix.FromInt(20), Fix.FromInt(20)), 60);
            Assert.Equal(50, (int)w.Dial[s]); // spawn default: mid

            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetDial, A = s, B = 85 });
            Assert.Equal(85, (int)w.Dial[s]);

            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 1, Type = CommandType.SetDial, A = s, B = 10 }); // not yours
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetDial, A = s, B = 101 }); // out of range
            Assert.Equal(2, w.RejectedCommands);
            Assert.Equal(85, (int)w.Dial[s]);
        }

        [Fact]
        public void SupplyPriorityDialGatesCaravanDuty()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            w.Players[0].Food = 500;
            short cacheDef = (short)sim.Defs.BuildingIndex("test.cache");
            int cache = w.Spawn(EntityKind.Building, cacheDef, 0, new FixVec2(Fix.FromInt(6), Fix.FromInt(24)), 200);

            // Dial hard left: every worker keeps the core — the cache never gets stocked.
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetSupplyPriority, A = 0 });
            Assert.Equal(0, w.Players[0].SupplyPriority);
            for (int t = 0; t < 400; t++) sim.Tick();
            Assert.Equal(0, w.DepotStock[cache]);

            // Dial right: caravans start flowing.
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = sim.TickCount, Player = 0, Type = CommandType.SetSupplyPriority, A = 100 });
            for (int t = 0; t < 600 && w.DepotStock[cache] == 0; t++) sim.Tick();
            Assert.True(w.DepotStock[cache] > 0, "at full priority the cache must get stocked");

            // Out-of-range values reject.
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = sim.TickCount, Player = 0, Type = CommandType.SetSupplyPriority, A = 101 });
            Assert.Equal(1, w.RejectedCommands);
        }

        [Fact]
        public void WorkersAutomaticallyCaravanFoodToForwardCaches()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            w.Players[0].Food = 500;
            short cacheDef = (short)sim.Defs.BuildingIndex("test.cache");
            int cache = w.Spawn(EntityKind.Building, cacheDef, 0, new FixVec2(Fix.FromInt(6), Fix.FromInt(24)), 200);
            // Empty, connected, and below capacity → a worker should claim the run, load at
            // the HQ (treasury pays), walk out, and deliver.
            for (int t = 0; t < 600 && w.DepotStock[cache] == 0; t++) sim.Tick();
            Assert.True(w.DepotStock[cache] > 0, "a caravan should have delivered to the cache");
        }

        [Fact]
        public void UnsuppliedUnitsFightAtReducedDamageUntilResupplied()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            // Attacker EAST of the enemy (who faces +x): a frontal hit, so the only damage
            // modifier in play is supply.
            int s = w.Spawn(EntityKind.Unit, SoldierDef, 0, new FixVec2(Fix.FromInt(36), Fix.FromInt(6)), 60);
            int enemy = w.Spawn(EntityKind.Unit, SoldierDef, 1, new FixVec2(Fix.FromInt(35), Fix.FromInt(6)), 600);
            int baseDmg = sim.Defs.Units[SoldierDef].AttackDamage;

            // Far from friendly supply, the reservoir drains...
            w.SupplyTicks[s] = 3;
            SupplySystem.Tick(w, sim.Defs);
            Assert.Equal(2, w.SupplyTicks[s]);
            w.SupplyTicks[s] = 0;
            Assert.Equal(baseDmg * 1 / 2, CombatSystem.DamageOf(w, sim.Defs, s, enemy)); // half effect

            // ...and marching back into HQ supply refills instantly, restoring full damage.
            // (Keep the enemy west of the attacker so the hit stays frontal.)
            w.Pos[s] = new FixVec2(Fix.FromInt(8), Fix.FromInt(8));
            w.Pos[enemy] = new FixVec2(Fix.FromInt(7), Fix.FromInt(8));
            SupplySystem.Tick(w, sim.Defs);
            Assert.Equal(w.Rules.SupplyGraceTicks, w.SupplyTicks[s]);
            Assert.Equal(baseDmg, CombatSystem.DamageOf(w, sim.Defs, s, enemy));
        }

        [Fact]
        public void GenerationAdvancesOnSlotReuse()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int e = w.Spawn(EntityKind.Unit, SoldierDef, 0, new FixVec2(Fix.FromInt(10), Fix.FromInt(10)), 60);
            int g0 = w.Generation[e];
            w.Despawn(e);
            int e2 = w.Spawn(EntityKind.Unit, SoldierDef, 0, new FixVec2(Fix.FromInt(11), Fix.FromInt(10)), 60);
            Assert.Equal(e, e2);                       // same slot reused
            Assert.True(w.Generation[e2] > g0);        // but a newer generation → distinct identity
        }
    }

    /// <summary>Classic per-unit control and the leader's command aura: every unit obeys
    /// direct orders, and friendly units within LeaderAuraRadius of a live same-owner
    /// leader deal LeaderAuraBonus damage (derived scratch, recomputed each tick).</summary>
    public class LeaderAuraTests
    {
        private const short LeaderDef = 0;  // TinyDefs sorted ids: test.leader, test.soldier, test.worker
        private const short SoldierDef = 1;

        [Fact]
        public void AuraGrantsDamageBonusWithinRadius()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int lead = w.Spawn(EntityKind.Unit, LeaderDef, 0, new FixVec2(Fix.FromInt(20), Fix.FromInt(20)), 100);
            int s = w.Spawn(EntityKind.Unit, SoldierDef, 0, new FixVec2(Fix.FromInt(21), Fix.FromInt(20)), 60);
            int bld = WorkerSystem.FindHq(w, sim.Defs, 1); // building target → flat directional factor
            int baseDmg = sim.Defs.Units[SoldierDef].AttackDamage; // 5

            LeaderAuraSystem.Tick(w, sim.Defs);
            Assert.True(w.ScratchLeaderAura[s]);
            Assert.Equal(baseDmg * 5 / 4, CombatSystem.DamageOf(w, sim.Defs, s, bld)); // +25% in the aura
        }

        [Fact]
        public void AuraRespectsRadiusOwnershipAndSelf()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int lead = w.Spawn(EntityKind.Unit, LeaderDef, 0, new FixVec2(Fix.FromInt(20), Fix.FromInt(20)), 100);
            int farOwn = w.Spawn(EntityKind.Unit, SoldierDef, 0, new FixVec2(Fix.FromInt(35), Fix.FromInt(20)), 60);
            int nearFoe = w.Spawn(EntityKind.Unit, SoldierDef, 1, new FixVec2(Fix.FromInt(21), Fix.FromInt(20)), 60);
            int bld = WorkerSystem.FindHq(w, sim.Defs, 1);
            int baseDmg = sim.Defs.Units[SoldierDef].AttackDamage;

            LeaderAuraSystem.Tick(w, sim.Defs);
            Assert.False(w.ScratchLeaderAura[farOwn]);  // 15u away >> 6u aura radius
            Assert.False(w.ScratchLeaderAura[nearFoe]); // enemy leaders never buff you
            Assert.False(w.ScratchLeaderAura[lead]);    // the leader never buffs itself
            Assert.Equal(baseDmg, CombatSystem.DamageOf(w, sim.Defs, farOwn, bld));
        }

        [Fact]
        public void AuraDropsWhenTheLeaderDies()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int lead = w.Spawn(EntityKind.Unit, LeaderDef, 0, new FixVec2(Fix.FromInt(20), Fix.FromInt(20)), 100);
            int s = w.Spawn(EntityKind.Unit, SoldierDef, 0, new FixVec2(Fix.FromInt(21), Fix.FromInt(20)), 60);

            LeaderAuraSystem.Tick(w, sim.Defs);
            Assert.True(w.ScratchLeaderAura[s]);

            w.Despawn(lead);
            LeaderAuraSystem.Tick(w, sim.Defs); // derived scratch: recomputed from scratch
            Assert.False(w.ScratchLeaderAura[s]);
        }

        [Fact]
        public void EveryUnitObeysDirectOrders()
        {
            // Classic control: leaders and soldiers alike take Move/AttackMove/Stop/SetFacing.
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int lead = w.Spawn(EntityKind.Unit, LeaderDef, 0, new FixVec2(Fix.FromInt(20), Fix.FromInt(20)), 100);
            int s = w.Spawn(EntityKind.Unit, SoldierDef, 0, new FixVec2(Fix.FromInt(22), Fix.FromInt(20)), 60);

            foreach (int e in new[] { lead, s })
            {
                CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.Move, A = e, B = 3000, C = 3000 });
                Assert.True(w.HasMoveOrder[e]);
                CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.Stop, A = e });
                Assert.False(w.HasMoveOrder[e]);
                CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetFacing, A = e, B = 0, C = 100 });
                Assert.Equal(Fix.One, w.Facing[e].Y);
            }
            Assert.Equal(0, w.RejectedCommands);

            // A zero facing vector still rejects.
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetFacing, A = s, B = 0, C = 0 });
            Assert.Equal(1, w.RejectedCommands);
        }

        [Fact]
        public void ReservedSwarmCommandTypesReject()
        {
            // The removed swarm-era command ids must land in the default Reject and
            // change nothing — old logs and stale peers may still emit them.
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int s = w.Spawn(EntityKind.Unit, SoldierDef, 0, new FixVec2(Fix.FromInt(20), Fix.FromInt(20)), 60);
            ulong before = sim.StateHash();

            int[] reserved = { 4, 5, 11, 12, 13, 14, 15, 23 };
            foreach (int id in reserved)
                CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = (CommandType)id, A = s, B = 1 });

            Assert.Equal(reserved.Length, w.RejectedCommands);
            w.RejectedCommands = 0; // the counter is hashed; restore it to compare the rest
            Assert.Equal(before, sim.StateHash());
        }

        [Fact]
        public void ProducedUnitsStayLooseAndFollowRally()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            // A leader stands nearby — with auto-assimilate gone it must NOT absorb anyone.
            w.Spawn(EntityKind.Unit, LeaderDef, 0, new FixVec2(Fix.FromInt(8), Fix.FromInt(8)), 100);
            int hq = WorkerSystem.FindHq(w, sim.Defs, 0);
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetProduceOverride, A = hq, B = SoldierDef });
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetRally, A = hq, B = 3000, C = 600, D = 1 });

            int soldier = -1;
            for (int t = 0; t < 300 && soldier < 0; t++)
            {
                sim.Tick();
                for (int i = 0; i < w.HighWater; i++)
                    if (w.Kind[i] == EntityKind.Unit && w.Owner[i] == 0 && w.DefIndex[i] == SoldierDef) { soldier = i; break; }
            }
            Assert.True(soldier >= 0, "expected a soldier within 300 ticks");
            Assert.True(w.HasMoveOrder[soldier], "fresh unit walks to the rally point");
            Assert.Equal(w.RallyPoint[hq], w.MoveTarget[soldier]);
        }
    }

    /// <summary>Def-driven building weapons (spike-battery style): a finished building with
    /// AttackDamage in its def shoots on its own stats; tiered caches keep the rules stats.</summary>
    public class ArmedBuildingTests
    {
        private static SimWorld NewWorld(DefDatabase defs, int players = 2) =>
            new SimWorld(defs.Rules, players, defs.Units.Length, defs.Upgrades.Length, Fix.FromInt(40), Fix.FromInt(40), 1);

        [Fact]
        public void ArmedBuildingShootsOnlyOnceFinished()
        {
            var defs = TestWorlds.TinyDefs();
            var w = NewWorld(defs);
            int turret = w.Spawn(EntityKind.Building, (short)defs.BuildingIndex("test.turret"), 0, new FixVec2(Fix.FromInt(10), Fix.FromInt(10)), 300);
            w.ConstructionRemaining[turret] = 30;
            int enemy = w.Spawn(EntityKind.Unit, 1, 1, new FixVec2(Fix.FromInt(13), Fix.FromInt(10)), 60);

            CombatSystem.Tick(w, defs);
            Assert.Equal(60, w.Hp[enemy]); // a construction site is silent

            w.ConstructionRemaining[turret] = 0;
            CombatSystem.Tick(w, defs);
            Assert.Equal(60 - 7, w.Hp[enemy]); // def-driven damage, not the cache rules
            Assert.Equal(10, w.AttackCooldown[turret]); // def-driven cooldown
            Assert.Contains(w.AttackEvents, ev => ev.Attacker == turret && ev.Target == enemy);
        }

        [Fact]
        public void ArmedBuildingRespectsItsRange()
        {
            var defs = TestWorlds.TinyDefs();
            var w = NewWorld(defs);
            int turret = w.Spawn(EntityKind.Building, (short)defs.BuildingIndex("test.turret"), 0, new FixVec2(Fix.FromInt(10), Fix.FromInt(10)), 300);
            int enemy = w.Spawn(EntityKind.Unit, 1, 1, new FixVec2(Fix.FromInt(20), Fix.FromInt(10)), 60);

            for (int t = 0; t < 30; t++) CombatSystem.Tick(w, defs);
            Assert.Equal(60, w.Hp[enemy]); // 10u away >> 5u reach: never hit
        }
    }

    /// <summary>Expansion drop-offs (isDropoff): workers bank resources at them like at
    /// the HQ, but they are not supply depots.</summary>
    public class DropoffTests
    {
        private static SimWorld NewWorld(DefDatabase defs) =>
            new SimWorld(defs.Rules, 1, defs.Units.Length, defs.Upgrades.Length, Fix.FromInt(60), Fix.FromInt(60), 1);

        [Fact]
        public void DropoffBuildingAcceptsWorkerDeposits()
        {
            var defs = TestWorlds.TinyDefs();
            var w = NewWorld(defs);
            int workerDef = defs.UnitIndex("test.worker");
            w.Spawn(EntityKind.Building, (short)defs.BuildingIndex("test.hq"), 0, new FixVec2(Fix.FromInt(5), Fix.FromInt(5)), 500); // far corner
            int drop = w.Spawn(EntityKind.Building, (short)defs.BuildingIndex("test.dropoff"), 0, new FixVec2(Fix.FromInt(45), Fix.FromInt(45)), 300);
            int worker = w.Spawn(EntityKind.Unit, (short)workerDef, 0, new FixVec2(Fix.FromInt(45), Fix.FromInt(45)), 30);
            w.Carry[worker] = defs.Units[workerDef].CarryCapacity;
            long before = w.Players[0].Food;

            WorkerSystem.Tick(w, defs);

            Assert.Equal(0, w.Carry[worker]); // deposited at the expansion, not across the map
            Assert.Equal(before + defs.Units[workerDef].CarryCapacity, w.Players[0].Food);
            Assert.Equal(0, w.DepotStock[drop]); // not a supply depot: nothing is stocked
        }

        [Fact]
        public void UnfinishedDropoffDoesNotAcceptDeposits()
        {
            var defs = TestWorlds.TinyDefs();
            var w = NewWorld(defs);
            int workerDef = defs.UnitIndex("test.worker");
            w.Spawn(EntityKind.Building, (short)defs.BuildingIndex("test.hq"), 0, new FixVec2(Fix.FromInt(5), Fix.FromInt(5)), 500);
            int drop = w.Spawn(EntityKind.Building, (short)defs.BuildingIndex("test.dropoff"), 0, new FixVec2(Fix.FromInt(45), Fix.FromInt(45)), 300);
            w.ConstructionRemaining[drop] = 30; // still a site
            int worker = w.Spawn(EntityKind.Unit, (short)workerDef, 0, new FixVec2(Fix.FromInt(45), Fix.FromInt(45)), 30);
            w.Carry[worker] = defs.Units[workerDef].CarryCapacity;

            WorkerSystem.Tick(w, defs);

            Assert.Equal(defs.Units[workerDef].CarryCapacity, w.Carry[worker]); // still hauling to the far HQ
        }
    }

    /// <summary>Zero-food units trickle from their producer without touching the bank —
    /// the brood-sac mechanism, running on the unmodified production system.</summary>
    public class FreeSpawnerTests
    {
        [Fact]
        public void FreeUnitsTrickleWithoutDrainingFood()
        {
            var defs = TestWorlds.TinyDefs();
            var w = new SimWorld(defs.Rules, 1, defs.Units.Length, defs.Upgrades.Length, Fix.FromInt(40), Fix.FromInt(40), 1);
            int sac = w.Spawn(EntityKind.Building, (short)defs.BuildingIndex("test.broodsac"), 0, new FixVec2(Fix.FromInt(20), Fix.FromInt(20)), 200);
            int mite = defs.UnitIndex("test.xmite");
            w.Players[0].ProductionWeights[mite] = 1; // standalone worlds start all-zero weights
            w.Players[0].Food = 0; // an empty bank still affords free units

            for (int t = 0; t < 105; t++) ProductionSystem.Tick(w, defs);

            int mites = 0;
            for (int i = 0; i < w.HighWater; i++)
                if (w.Kind[i] == EntityKind.Unit && w.DefIndex[i] == mite) mites++;
            Assert.Equal(2, mites); // one per BuildTimeTicks(50) cycle
            Assert.Equal(0, w.Players[0].Food);
        }
    }

    /// <summary>Immovable terrain walls: map-defined circles that units cannot cross and
    /// buildings cannot stand on — chokepoints without a pathfinder.</summary>
    public class TerrainTests
    {
        private const short SoldierDef = 1; // TinyDefs sorted ids: test.leader, test.soldier, test.worker

        [Fact]
        public void WallsAreImpassableToUnits()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            w.WallPos = new[] { new FixVec2(Fix.FromInt(20), Fix.FromInt(20)) };
            w.WallRadius = new[] { Fix.FromInt(3) };
            int s = w.Spawn(EntityKind.Unit, SoldierDef, 0, new FixVec2(Fix.FromInt(12), Fix.FromInt(20)), 60);

            // Order the soldier straight through the wall's center.
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.Move, A = s, B = 3000, C = 2000 });
            Fix wallR = w.WallRadius[0];
            for (int t = 0; t < 300; t++)
            {
                sim.Tick();
                Fix dSq = (w.Pos[s] - w.WallPos[0]).LengthSq;
                Assert.True(dSq >= wallR * wallR, $"unit inside the wall at tick {t}");
                Assert.True(w.Pos[s].X < Fix.FromInt(20), "unit teleported through the wall");
            }
        }

        [Fact]
        public void BuildingsRefuseToStandOnWalls()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            w.WallPos = new[] { new FixVec2(Fix.FromInt(20), Fix.FromInt(20)) };
            w.WallRadius = new[] { Fix.FromInt(3) };
            int worker = DeterminismTests.FindUnit(sim, 0, isWorker: true);

            int rejectedBefore = w.RejectedCommands;
            CommandSystem.Apply(w, sim.Defs, new Command
            {
                Tick = 0, Player = 0, Type = CommandType.ConstructBuilding,
                A = worker, B = 2000, C = 2000, D = sim.Defs.BuildingIndex("test.nursery"),
            });
            Assert.Equal(rejectedBefore + 1, w.RejectedCommands); // footprint on the wall

            CommandSystem.Apply(w, sim.Defs, new Command
            {
                Tick = 0, Player = 0, Type = CommandType.ConstructBuilding,
                A = worker, B = 3200, C = 2000, D = sim.Defs.BuildingIndex("test.nursery"),
            });
            Assert.Equal(rejectedBefore + 1, w.RejectedCommands); // clear of it: accepted
        }
    }

    /// <summary>Validates the shipped JSON dataset actually loads and is internally consistent.</summary>
    public class DataTests
    {
        private static string FindDataDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "data");
                if (File.Exists(Path.Combine(candidate, "rules.json"))) return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("data directory not found above " + AppContext.BaseDirectory);
        }

        [Fact]
        public void ShippedDatasetLoadsAndRuns()
        {
            string dataDir = FindDataDir();
            var defs = DefLoader.Load(dataDir);
            Assert.Equal(13, defs.Units.Length);
            Assert.Equal(13, defs.Buildings.Length);
            Assert.NotEqual(0UL, defs.DefsHash);

            var map = DefLoader.LoadMap(dataDir, "petri-dish");
            Assert.True(map.Spawns.Length >= 2);
            Assert.True(map.Walls.Length > 0); // shipped maps carry terrain walls

            var sim = new Simulation(defs, map, 2, 42, new CommandLog());
            for (int t = 0; t < 300; t++) sim.Tick();
            Assert.Equal(2, sim.AlivePlayers());

            var sim2 = new Simulation(defs, map, 2, 42, new CommandLog());
            for (int t = 0; t < 300; t++) sim2.Tick();
            Assert.Equal(sim.StateHash(), sim2.StateHash());
        }
    }

}
