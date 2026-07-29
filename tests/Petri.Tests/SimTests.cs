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
                new BuildingDef
                {
                    Id = "test.broodsac", MaxHp = 200, CollisionRadiusCenti = 50,
                    Constructible = true, FoodCost = 50, BuildTimeTicks = 40,
                    Produces = new[] { "test.xmite" },
                },
                new BuildingDef
                {
                    Id = "test.hq", MaxHp = 500, CollisionRadiusCenti = 100,
                    IsHeadquarters = true, StartsBuilt = true,
                    Produces = new[] { "test.worker", "test.soldier" },
                },
                new BuildingDef
                {
                    Id = "test.mutagen", MaxHp = 200, CollisionRadiusCenti = 40,
                    Constructible = true, EvoCost = 3, BuildTimeTicks = 40, AttackBonus = 1,
                },
                new BuildingDef
                {
                    Id = "test.nursery", MaxHp = 300, CollisionRadiusCenti = 80,
                    Constructible = true, FoodCost = 50, BuildTimeTicks = 60,
                    Produces = new[] { "test.soldier", "test.leader" },
                },
                new BuildingDef
                {
                    Id = "test.turret", MaxHp = 300, CollisionRadiusCenti = 50,
                    Constructible = true, FoodCost = 60, BuildTimeTicks = 40,
                    AttackDamage = 7, AttackRangeCenti = 500, AttackCooldownTicks = 10, ProjectileSpeedCenti = 1400,
                },
            };
            // Arrays are pre-sorted by id (ordinal) — same contract the loader guarantees.
            return new DefDatabase(rules, units, buildings, Array.Empty<UpgradeDef>(), 1);
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
            ulong WithPush()
            {
                var log = new CommandLog();
                var sim = TestWorlds.NewSim(42, log);
                log.Add(new Command { Tick = 0, Player = 0, Type = CommandType.PushFront, A = 0, B = 3400, C = 600 });
                for (int t = 0; t < 200; t++) sim.Tick();
                return sim.StateHash();
            }
            Assert.Equal(WithPush(), WithPush());
            Assert.NotEqual(WithPush(), RunAndHash(42, 200));
        }
    }

    /// <summary>C3 cutover: production yields COUNTS in the organism's force, not entities.</summary>
    public class ProductionCountTests
    {
        [Fact]
        public void ProductionAddsSoldierCountsToFronts()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int hq = WorldQuery.FindHq(w, sim.Defs, 0);
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetProduceOverride, A = hq, B = 1 });
            Assert.Equal(0, w.RejectedCommands);

            for (int t = 0; t < 600; t++) sim.Tick();
            int u = w.UnitDefCount, total = 0;
            for (int f = 0; f < SimConstants.MaxFronts; f++) total += w.Players[0].Force[f * u + 1];
            // 150 starting food covers two 60-cost soldiers (100 ticks each) inside 600 ticks.
            Assert.True(total >= 2, $"soldiers should join the force as counts (got {total})");
            // No unit ENTITIES may exist anywhere, ever.
            for (int i = 0; i < w.HighWater; i++) Assert.NotEqual(EntityKind.Unit, w.Kind[i]);
        }

        [Fact]
        public void ProducedWorkersGrowTheWorkerPool()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int hq = WorldQuery.FindHq(w, sim.Defs, 0);
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetProduceOverride, A = hq, B = 2 });
            for (int t = 0; t < 600; t++) sim.Tick();
            Assert.True(w.Players[0].WorkerCount > w.Rules.StartingWorkers,
                $"workers are a count that production grows (got {w.Players[0].WorkerCount})");
        }

        [Fact]
        public void RalliedProducerSendsItsUnitsToThatFront()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int hq = WorldQuery.FindHq(w, sim.Defs, 0);
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetProduceOverride, A = hq, B = 1 });
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.RallyProduction, A = hq, B = 3 });
            Assert.Equal(0, w.RejectedCommands);

            // Tick until the FIRST soldier completes: it must appear on front 3, and a lone
            // count stays put (redeploy only levels spreads greater than one).
            int u = w.UnitDefCount;
            for (int t = 0; t < 600; t++)
            {
                sim.Tick();
                int total = 0;
                for (int f = 0; f < SimConstants.MaxFronts; f++) total += w.Players[0].Force[f * u + 1];
                if (total == 0) continue;
                Assert.Equal(1, w.Players[0].Force[3 * u + 1]);
                return;
            }
            Assert.Fail("no soldier was ever produced");
        }

        [Fact]
        public void PauseHaltsProduction()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int hq = WorldQuery.FindHq(w, sim.Defs, 0);
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetProducePaused, A = hq, B = 1 });
            for (int t = 0; t < 400; t++) sim.Tick();
            int u = w.UnitDefCount, total = 0;
            for (int k = 0; k < w.Players[0].Force.Length; k++) total += w.Players[0].Force[k];
            Assert.Equal(0, total);
            Assert.Equal(w.Rules.StartingWorkers, w.Players[0].WorkerCount);
        }
    }

    /// <summary>C3 cutover: nodes are mined passively, but only once the organism engulfs them.</summary>
    public class HarvestTests
    {
        [Fact]
        public void HarvestDrainsOnlyEngulfedNodes()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int node = -1;
            for (int i = 0; i < w.HighWater; i++) if (w.Kind[i] == EntityKind.Node) { node = i; break; }
            Assert.True(node >= 0);
            int before = w.NodeFood[node];

            // Outside every territory: a harvest beat takes nothing.
            HarvestSystem.Tick(w, sim.Defs);
            Assert.Equal(before, w.NodeFood[node]);

            // Hand-flip the node's cell to player 0: the next beat mines it into Food.
            long foodBefore = w.Players[0].Food;
            w.Territory[w.CellOfPos(w.Pos[node])] = 0;
            HarvestSystem.Tick(w, sim.Defs);
            int taken = before - w.NodeFood[node];
            Assert.True(taken > 0, "engulfed node should be mined");
            Assert.True(taken <= w.Rules.HarvestNodeCapPerBeat);
            Assert.Equal(foodBefore + taken, w.Players[0].Food);
        }
    }

    /// <summary>C3 cutover: buildings are placed from the panel and build THEMSELVES.</summary>
    public class ConstructionTests
    {
        [Fact]
        public void SelfBuildFinishesInBuildTimeTicks()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int nursery = sim.Defs.BuildingIndex("test.nursery");
            CommandSystem.Apply(w, sim.Defs, new Command
            {
                Tick = 0, Player = 0, Type = CommandType.ConstructBuilding,
                A = -1, B = 800, C = 600, D = nursery,
            });
            Assert.Equal(0, w.RejectedCommands);

            int site = -1;
            for (int i = 0; i < w.HighWater; i++)
                if (w.Kind[i] == EntityKind.Building && w.DefIndex[i] == nursery && w.Owner[i] == 0) site = i;
            Assert.True(site >= 0, "the site should exist immediately");
            int ticks = sim.Defs.Buildings[nursery].BuildTimeTicks;
            Assert.Equal(ticks * 3, w.ConstructionRemaining[site]); // 3 work units per tick at HubBuildRate 3

            for (int t = 0; t < ticks; t++) sim.Tick();
            Assert.Equal(0, w.ConstructionRemaining[site]); // grew itself, no workers involved
        }

        [Fact]
        public void PlacementRequiresOwnUnblockedTerritory()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int nursery = sim.Defs.BuildingIndex("test.nursery");
            Command At(int x, int y) => new Command
            {
                Tick = 0, Player = 0, Type = CommandType.ConstructBuilding, A = -1, B = x, C = y, D = nursery,
            };

            CommandSystem.Apply(w, sim.Defs, At(2000, 2000)); // neutral middle
            Assert.Equal(1, w.RejectedCommands);
            CommandSystem.Apply(w, sim.Defs, At(3400, 3400)); // the ENEMY organism
            Assert.Equal(2, w.RejectedCommands);
            CommandSystem.Apply(w, sim.Defs, At(600, 600));   // own ground but on the nucleus
            Assert.Equal(3, w.RejectedCommands);

            // A statically blocked cell (wall terrain) rejects even when owned.
            int cell = w.CellOfCenti(800, 600);
            w.TerritoryBlocked[cell] = true;
            CommandSystem.Apply(w, sim.Defs, At(800, 600));
            Assert.Equal(4, w.RejectedCommands);
            w.TerritoryBlocked[cell] = false;
            CommandSystem.Apply(w, sim.Defs, At(800, 600));   // now legal
            Assert.Equal(4, w.RejectedCommands);
        }
    }

    public class CommandRejectTests
    {
        [Fact]
        public void RetiredCommandIdsAllReject()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            // Every id ever retired from the protocol. These numbers must NEVER be reused:
            // a peer replaying an old log must hit the default Reject, not new behavior.
            int[] retired = { 1, 2, 4, 5, 7, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23 };
            for (int k = 0; k < retired.Length; k++)
            {
                CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = (CommandType)retired[k], A = 0 });
                Assert.Equal(k + 1, w.RejectedCommands);
            }
        }

        [Fact]
        public void InvalidWeightCommandRejects()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetProductionWeight, A = -1, B = 3 });
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetProductionWeight, A = 99, B = 3 });
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetProductionWeight, A = 1, B = -2 });
            Assert.Equal(3, w.RejectedCommands);
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetProductionWeight, A = 1, B = 5 });
            Assert.Equal(3, w.RejectedCommands);
            Assert.Equal(5, w.Players[0].ProductionWeights[1]);
        }
    }

    public class TeamTests
    {
        // 4 players: 0+1 on team 0, 2+3 on team 1.
        private static Simulation TeamSim(ulong seed) =>
            new Simulation(TestWorlds.TinyDefs(), TestWorlds.TinyMap(), 4, seed, new CommandLog(),
                new byte[] { 0, 0, 1, 1 });

        [Fact]
        public void HostilityRoutesThroughTeamsNotOwners()
        {
            var sim = TeamSim(5);
            var w = sim.World;
            Assert.False(w.AreEnemies(0, 1));                       // same team
            Assert.True(w.AreEnemies(0, 2));                        // across teams
            Assert.False(w.AreEnemies(0, SimWorld.NeutralOwner));   // nodes are never enemies
            Assert.True(w.IsFriendly(0, 1));
            Assert.False(w.IsFriendly(0, 2));
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

    public class SpawnReuseTests
    {
        [Fact]
        public void ReusedEntityIndexStartsFullyReset()
        {
            var defs = TestWorlds.TinyDefs();
            var w = new SimWorld(defs.Rules, 2, defs.Units.Length, defs.Upgrades.Length, Fix.FromInt(40), Fix.FromInt(40), 1);
            int e = w.Spawn(EntityKind.Building, 1, 0, new FixVec2(Fix.FromInt(5), Fix.FromInt(5)), 300);
            int gen = w.Generation[e];
            w.ProduceProgress[e] = 5;
            w.ProduceChoice[e] = 1;
            w.ProduceOverride[e] = 2;
            w.ProducePaused[e] = true;
            w.ConstructionRemaining[e] = 9;
            w.NodeFood[e] = 50;
            w.NodeMineral[e] = true;
            w.RallyFront[e] = 3;
            w.Despawn(e);

            int e2 = w.Spawn(EntityKind.Node, 0, 1, new FixVec2(Fix.FromInt(1), Fix.FromInt(1)), 60);
            Assert.Equal(e, e2); // lowest-free-index reuse
            Assert.Equal(0, w.ProduceProgress[e2]);
            Assert.Equal(-1, (int)w.ProduceChoice[e2]);
            Assert.Equal(-1, (int)w.ProduceOverride[e2]);
            Assert.False(w.ProducePaused[e2]);
            Assert.Equal(0, w.ConstructionRemaining[e2]);
            Assert.Equal(0, w.NodeFood[e2]);
            Assert.False(w.NodeMineral[e2]);
            Assert.Equal(-1, (int)w.RallyFront[e2]);
            Assert.Equal(gen + 1, w.Generation[e2]); // generation only ever advances
        }
    }

    /// <summary>Eliminate() must leave a canonical post-mortem state on every peer.</summary>
    public class EliminationTests
    {
        [Fact]
        public void EliminateWipesTerritoryEntitiesAndForce()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            for (int t = 0; t < 100; t++) sim.Tick();
            w.Players[0].Force[1] = 7; // pretend some force existed

            w.Eliminate(0);
            Assert.False(w.Players[0].Alive);
            Assert.Equal(0, w.Players[0].WorkerCount);
            Assert.Equal(0, w.Players[0].OrganismHealth);
            for (int k = 0; k < w.Players[0].Force.Length; k++) Assert.Equal(0, w.Players[0].Force[k]);
            for (int c = 0; c < w.Territory.Length; c++) Assert.NotEqual(0, (int)w.Territory[c]);
            for (int i = 0; i < w.HighWater; i++)
                if (w.Kind[i] != EntityKind.None && w.Owner[i] == 0)
                    Assert.Equal(EntityKind.Node, w.Kind[i]); // nodes are unowned map features
        }

        [Fact]
        public void NucleusDeathEliminatesThePlayer()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int hq = WorldQuery.FindHq(w, sim.Defs, 0);
            w.Hp[hq] = 0;
            sim.Tick(); // CleanupSystem notices the dead nucleus
            Assert.False(w.Players[0].Alive);
            Assert.Equal(EntityKind.None, w.Kind[hq]);
        }
    }

    public class TerritoryTests
    {
        private static int OwnedCount(SimWorld w, byte p)
        {
            int n = 0;
            for (int c = 0; c < w.Territory.Length; c++) if (w.Territory[c] == p) n++;
            return n;
        }

        [Fact]
        public void StartingBlobsAreSeededAndDisjoint()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            Assert.True(w.Territory.Length > 0);
            int p0 = OwnedCount(w, 0), p1 = OwnedCount(w, 1);
            // Both organisms start with a blob (sizes can differ slightly at map edges,
            // where the disc clips against the boundary).
            Assert.True(p0 > 5 && p1 > 5, $"both organisms start with territory (p0={p0}, p1={p1})");
            // The nucleus (spawn) cell belongs to its player.
            int hq = WorldQuery.FindHq(w, sim.Defs, 0);
            Assert.Equal(0, w.Territory[w.CellOfPos(w.Pos[hq])]);
        }

        [Fact]
        public void GrowthClaimsAdjacentNeutralCellsDeterministically()
        {
            var a = TestWorlds.NewSim(42, new CommandLog());
            var b = TestWorlds.NewSim(42, new CommandLog());
            int before = OwnedCount(a.World, 0);
            for (int t = 0; t < 40; t++) { a.Tick(); b.Tick(); }
            Assert.True(OwnedCount(a.World, 0) > before, "organism should grow");
            Assert.Equal(a.World.Territory, b.World.Territory); // bit-identical growth
            for (int c = 0; c < a.World.Territory.Length; c++)
                Assert.False(a.World.TerritoryBlocked[c] && a.World.Territory[c] != SimWorld.NeutralOwner,
                    "blocked cells must never be owned");
        }

        [Fact]
        public void WallsBlockCellsFromOwnership()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            // Statically block a plate of cells (as a map wall would) and grow a long time:
            // ownership must never appear there.
            int c0 = w.CellOfCenti(2000, 2000);
            w.TerritoryBlocked[c0] = true;
            if (w.Territory[c0] != SimWorld.NeutralOwner) w.Territory[c0] = SimWorld.NeutralOwner;
            for (int t = 0; t < 400; t++) sim.Tick();
            Assert.Equal(SimWorld.NeutralOwner, w.Territory[c0]);
        }

        [Fact]
        public void TerritoryIsHashedState()
        {
            var a = TestWorlds.NewSim(42, new CommandLog());
            var b = TestWorlds.NewSim(42, new CommandLog());
            for (int t = 0; t < 600; t++) { a.Tick(); b.Tick(); }
            Assert.Equal(a.StateHash(), b.StateHash());
            // Flip one cell to a DIFFERENT owner: the fingerprint must diverge.
            b.World.Territory[0] = b.World.Territory[0] == 0 ? (byte)1 : (byte)0;
            Assert.NotEqual(a.StateHash(), b.StateHash());
        }
    }

    /// <summary>FrontMath sector classification and the front-state commands.</summary>
    public class FrontGeometryTests
    {
        [Fact]
        public void SectorPartitionCoversAllDirectionsForEveryK()
        {
            foreach (int k in SimConstants.FrontCounts)
            {
                var counts = new int[k];
                // 720 directions around the circle: every one lands in exactly one sector.
                for (int step = 0; step < 720; step++)
                {
                    double a = step * System.Math.PI / 360.0;
                    long dx = (long)(System.Math.Cos(a) * 100000);
                    long dy = (long)(System.Math.Sin(a) * 100000);
                    int s = FrontMath.Sector(k, dx, dy);
                    Assert.InRange(s, 0, k - 1);
                    counts[s]++;
                }
                // Equal wedges: each sector catches 720/k directions, within table rounding.
                for (int s = 0; s < k; s++)
                    Assert.InRange(counts[s], 720 / k - 2, 720 / k + 2);
            }
        }

        [Fact]
        public void SectorZeroIsCenteredEast()
        {
            foreach (int k in SimConstants.FrontCounts)
                Assert.Equal(0, FrontMath.Sector(k, 1000, 0)); // due east for every K
            Assert.Equal(1, FrontMath.Sector(4, 0, 1000));  // north
            Assert.Equal(2, FrontMath.Sector(4, -1000, 0)); // west
            Assert.Equal(3, FrontMath.Sector(4, 0, -1000)); // south
        }

        [Fact]
        public void SectorClassificationIsDeterministicAtBoundaries()
        {
            // Exactly on a boundary ray: the same sector every time, twice.
            foreach (int k in SimConstants.FrontCounts)
            {
                int a = FrontMath.Sector(k, 707, 707);
                int b = FrontMath.Sector(k, 707, 707);
                Assert.Equal(a, b);
                Assert.Equal(0, FrontMath.Sector(k, 0, 0)); // degenerate → sector 0
            }
        }
    }

    public class FrontCommandTests
    {
        [Fact]
        public void SetFrontCountRedistributesPreservingTotals()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            var pl = w.Players[0];
            int u = w.UnitDefCount;
            // Hand-load 10 soldiers on front 0 and 3 on front 2 (K=4 default).
            pl.Force[0 * u + 1] = 10;
            pl.Force[2 * u + 1] = 3;
            pl.FrontDamage[0] = 55;
            pl.FrontPushX[1] = 500; pl.FrontPushY[1] = 500;

            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetFrontCount, A = 6 });
            Assert.Equal(0, w.RejectedCommands);
            Assert.Equal(6, (int)pl.FrontCount);
            int total = 0;
            for (int f = 0; f < SimConstants.MaxFronts; f++) total += pl.Force[f * u + 1];
            Assert.Equal(13, total); // per-def totals preserved
            Assert.Equal(3, pl.Force[0 * u + 1]); // 13/6 = 2 rem 1 → low front gets the extra
            Assert.Equal(2, pl.Force[5 * u + 1]);
            Assert.Equal(0, pl.FrontDamage[0]);   // damage/broken/pushes reset
            Assert.Equal(-1, pl.FrontPushX[1]);

            // Illegal K rejects; same K rejects.
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetFrontCount, A = 7 });
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.SetFrontCount, A = 6 });
            Assert.Equal(2, w.RejectedCommands);
        }

        [Fact]
        public void PushAndStopFrontValidateAndStoreTargets()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            var pl = w.Players[0];

            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.PushFront, A = 2, B = 3000, C = 1000 });
            Assert.Equal(0, w.RejectedCommands);
            Assert.Equal(3000, pl.FrontPushX[2]);
            Assert.Equal(1000, pl.FrontPushY[2]);

            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.StopFront, A = 2 });
            Assert.Equal(-1, pl.FrontPushX[2]);

            // Front index >= K rejects (K = 4 by default).
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.PushFront, A = 4, B = 100, C = 100 });
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.StopFront, A = -1 });
            Assert.Equal(2, w.RejectedCommands);
        }

        [Fact]
        public void RallyProductionValidatesOwnershipAndProducer()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int hq0 = WorldQuery.FindHq(w, sim.Defs, 0);
            int hq1 = WorldQuery.FindHq(w, sim.Defs, 1);

            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.RallyProduction, A = hq0, B = 3 });
            Assert.Equal(0, w.RejectedCommands);
            Assert.Equal(3, (int)w.RallyFront[hq0]);
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.RallyProduction, A = hq0, B = -1 });
            Assert.Equal(-1, (int)w.RallyFront[hq0]);

            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.RallyProduction, A = hq1, B = 0 });  // not yours
            CommandSystem.Apply(w, sim.Defs, new Command { Tick = 0, Player = 0, Type = CommandType.RallyProduction, A = hq0, B = 40 }); // >= MaxFronts
            Assert.Equal(2, w.RejectedCommands);
        }

        [Fact]
        public void RedeployFlowsForceTowardEmptyFrontsAtMoveSpeedRate()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            var pl = w.Players[0];
            int u = w.UnitDefCount;
            pl.Force[0 * u + 1] = 12; // 12 soldiers piled on front 0, K = 4, nothing contested

            FrontSystem.TickGeometry(w, sim.Defs);
            FrontSystem.TickForces(w, sim.Defs);
            // test.soldier moveSpeed 200 → max(1, 200/100) = 2 transfers on the first beat.
            int total = 0, moved = 0;
            for (int f = 0; f < 4; f++) total += pl.Force[f * u + 1];
            moved = total - pl.Force[0 * u + 1];
            Assert.Equal(12, total);
            Assert.Equal(2, moved);

            for (int t = 0; t < 12; t++)
            {
                FrontSystem.TickGeometry(w, sim.Defs);
                FrontSystem.TickForces(w, sim.Defs);
            }
            // Long run: force settles even (3/3/3/3).
            for (int f = 0; f < 4; f++) Assert.Equal(3, pl.Force[f * u + 1]);
        }
    }

    /// <summary>
    /// FRONT COMBAT: two hand-painted island cells in the middle of the dish put one front
    /// of each organism in contact, with force blocks loaded by hand. Only TickGeometry +
    /// TickCombat run, so territory and forces stay exactly where the test put them.
    /// </summary>
    public class FrontCombatTests
    {
        private const int SoldierIx = 1, LeaderIx = 0, XmiteIx = 3;

        /// <summary>Paint (9,9)→p0 and (10,9)→p1, run geometry, and return each side's
        /// contact sector. TinyMap is 20×20 cells; cell centers are (x*200+100, y*200+100).</summary>
        private static (Simulation sim, int s0, int s1, int cellA, int cellB) ContactWorld()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int cellA = 9 * w.TerritoryCellsX + 9, cellB = 9 * w.TerritoryCellsX + 10;
            w.Territory[cellA] = 0;
            w.Territory[cellB] = 1;
            FrontSystem.TickGeometry(w, sim.Defs);
            return (sim, w.ScratchCellSector[cellA], w.ScratchCellSector[cellB], cellA, cellB);
        }

        private static void Beat(Simulation sim)
        {
            FrontSystem.TickGeometry(sim.World, sim.Defs);
            FrontSystem.TickCombat(sim.World, sim.Defs);
        }

        [Fact]
        public void ExchangePoolsDamageAndKillsWhatThePoolCanCover()
        {
            var (sim, s0, s1, _, _) = ContactWorld();
            var w = sim.World;
            int u = w.UnitDefCount;
            var p0 = w.Players[0];
            var p1 = w.Players[1];
            p0.Force[s0 * u + SoldierIx] = 10;             // out = 10×5/4 = 12 per beat
            p1.Force[s1 * u + LeaderIx] = 1;               // 100 HP sponge, no attack
            p1.Force[s1 * u + SoldierIx] = 1;              // 60 HP, out = 5/4 = 1 per beat
            long foodBefore = p0.Food;

            Beat(sim);
            Assert.Equal(12, p1.FrontDamage[s1]);          // pooled, nothing dies yet
            Assert.Equal(1, p0.FrontDamage[s0]);           // the lone soldier fires back
            Assert.True(w.ScratchFrontContested[1 * SimConstants.MaxFronts + s1]);

            // Chip damage kills the first unit the pool can COVER (dense order): at beat 5
            // the pool hits 60 — the soldier falls, the 100 HP leader soaks on.
            for (int b = 0; b < 4; b++) Beat(sim);
            Assert.Equal(1, p1.Force[s1 * u + LeaderIx]);
            Assert.Equal(0, p1.Force[s1 * u + SoldierIx]);
            Assert.Equal(0, p1.FrontDamage[s1]);           // 60 − 60: pool spent exactly
            Assert.Equal(sim.Defs.Rules.EvoPerKill, p0.EvoPoints);
            Assert.Equal(foodBefore + 6, p0.Food);         // bounty: 60 food cost / 10

            // Four more beats: pool 48 < 100 — the leader still stands, front unbroken.
            for (int b = 0; b < 4; b++) Beat(sim);
            Assert.Equal(1, p1.Force[s1 * u + LeaderIx]);
            Assert.Equal(48, p1.FrontDamage[s1]);
            Assert.Equal(0, p1.FrontBrokenTicks[s1]);
        }

        [Fact]
        public void FireOnAnUnmannedFrontScorchesOrganismHealth()
        {
            var (sim, s0, _, _, _) = ContactWorld();
            var w = sim.World;
            w.Players[0].Force[s0 * w.UnitDefCount + SoldierIx] = 10;
            int hpBefore = w.Players[1].OrganismHealth;

            Beat(sim);
            Assert.Equal(hpBefore - 1, w.Players[1].OrganismHealth); // max(1, 12/10)
        }

        [Fact]
        public void ArmedBuildingGarrisonsItsSector()
        {
            var (sim, _, s1, _, cellB) = ContactWorld();
            var w = sim.World;
            const int MF = SimConstants.MaxFronts;
            int rangedBefore = w.ScratchFrontRanged[1 * MF + s1];
            // A finished turret standing on p1's island cell arms that front.
            int turret = sim.Defs.BuildingIndex("test.turret");
            w.CellCenterCenti(cellB, out int bx, out int by);
            w.Spawn(EntityKind.Building, (short)turret, 1, new FixVec2(Fix.Ratio(bx, 100), Fix.Ratio(by, 100)), 300);

            Beat(sim);
            Assert.Equal(rangedBefore + 7, w.ScratchFrontRanged[1 * MF + s1]);
            Assert.Equal(7, w.ScratchFrontHold[1 * MF + s1]); // no force: hold is the turret alone
        }
    }

    public class BreakthroughTests
    {
        private const int SoldierIx = 1, XmiteIx = 3;

        [Fact]
        public void WipedContestedFrontBreaksAndBreakerTakesGroundFast()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int u = w.UnitDefCount;
            // p0 holds one island cell; p1 holds a 3-cell corridor stretching east of it.
            // Row 3 is level with p0's blob centroid, so the whole advance stays inside
            // p0's EAST sector — the push order keeps applying as the frontier moves.
            int cellA = 3 * w.TerritoryCellsX + 9;
            int strip0 = 3 * w.TerritoryCellsX + 10;
            int strip1 = 3 * w.TerritoryCellsX + 11;
            int strip2 = 3 * w.TerritoryCellsX + 12;
            w.Territory[cellA] = 0;
            w.Territory[strip0] = 1;
            w.Territory[strip1] = 1;
            w.Territory[strip2] = 1;
            FrontSystem.TickGeometry(w, sim.Defs);
            int s0 = w.ScratchCellSector[cellA], s1 = w.ScratchCellSector[strip0];
            var p0 = w.Players[0];
            var p1 = w.Players[1];
            // 32 soldiers burst 32×5/4 = 40 damage in ONE beat: exactly the xmite's 40 HP,
            // so the defenders are wiped before any pre-break flip can sever the contact.
            p0.Force[s0 * u + SoldierIx] = 32;
            p1.Force[s1 * u + XmiteIx] = 1;
            w.CellCenterCenti(strip2, out int bx, out int by);
            p0.FrontPushX[s0] = bx;                        // the breaker IS pushing
            p0.FrontPushY[s0] = by;
            int hpBefore = p1.OrganismHealth;

            FrontSystem.TickGeometry(w, sim.Defs);
            FrontSystem.TickCombat(w, sim.Defs);
            Assert.Equal(0, p1.Force[s1 * u + XmiteIx]);   // defenders perished
            Assert.Equal(sim.Defs.Rules.BreakthroughTicks, p1.FrontBrokenTicks[s1]); // window open
            Assert.Equal(0, p1.FrontDamage[s1]);           // residual pool died with the front
            Assert.Equal(0, (int)w.Territory[strip0]);     // ground taken the SAME beat
            Assert.Equal(hpBefore - sim.Defs.Rules.CellLossHealth, p1.OrganismHealth);

            // The corridor is one cell wide, so the frontier advances one cell per beat —
            // by two more beats the whole corridor has fallen through the open window.
            for (int b = 0; b < 2; b++)
            {
                FrontSystem.TickGeometry(w, sim.Defs);
                FrontSystem.TickCombat(w, sim.Defs);
            }
            Assert.Equal(0, (int)w.Territory[strip1]);
            Assert.Equal(0, (int)w.Territory[strip2]);
        }

        [Fact]
        public void NoPushMeansNoTerritoryTakenEvenWithAdvantage()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int u = w.UnitDefCount;
            int cellA = 9 * w.TerritoryCellsX + 9, cellB = 9 * w.TerritoryCellsX + 10;
            w.Territory[cellA] = 0;
            w.Territory[cellB] = 1;
            FrontSystem.TickGeometry(w, sim.Defs);
            w.Players[0].Force[w.ScratchCellSector[cellA] * u + SoldierIx] = 10;

            for (int b = 0; b < 10; b++)
            {
                FrontSystem.TickGeometry(w, sim.Defs);
                FrontSystem.TickCombat(w, sim.Defs);
            }
            Assert.Equal(1, (int)w.Territory[cellB]); // combat rages, but nobody ordered a push
        }
    }

    public class BuildingFlipTests
    {
        private const int SoldierIx = 1;

        [Fact]
        public void StandingBuildingSoaksFlipsUntilItFalls()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int u = w.UnitDefCount;
            int cellA = 9 * w.TerritoryCellsX + 9, cellB = 9 * w.TerritoryCellsX + 10;
            w.Territory[cellA] = 0;
            w.Territory[cellB] = 1;
            FrontSystem.TickGeometry(w, sim.Defs);
            int s0 = w.ScratchCellSector[cellA];
            var p0 = w.Players[0];
            p0.Force[s0 * u + SoldierIx] = 10;
            w.CellCenterCenti(cellB, out int bx, out int by);
            p0.FrontPushX[s0] = bx;
            p0.FrontPushY[s0] = by;
            // A 200 HP brood-sac stands on the contested cell: 2 flips/beat × 40 dmg each.
            int sac = sim.Defs.BuildingIndex("test.broodsac");
            int shield = w.Spawn(EntityKind.Building, (short)sac, 1,
                new FixVec2(Fix.Ratio(bx, 100), Fix.Ratio(by, 100)), 200);

            FrontSystem.TickGeometry(w, sim.Defs);
            FrontSystem.TickCombat(w, sim.Defs);
            Assert.Equal(120, w.Hp[shield]);          // two flips soaked
            Assert.Equal(1, (int)w.Territory[cellB]); // the ground held

            for (int b = 0; b < 2; b++)
            {
                FrontSystem.TickGeometry(w, sim.Defs);
                FrontSystem.TickCombat(w, sim.Defs);
            }
            Assert.True(w.Hp[shield] <= 0);           // 200 HP / 40 = 5 flips to fell it
            Assert.Equal(0, (int)w.Territory[cellB]); // the 6th flip finally took the cell
            CleanupSystem.Tick(w, sim.Defs);
            Assert.Equal(EntityKind.None, w.Kind[shield]);
        }
    }

    public class HealthTests
    {
        [Fact]
        public void OrganismsHatchAtFullHealthForTheirSize()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int expected = HealthSystem.MaxOf(w, sim.Defs, 0);
            Assert.Equal(expected, w.Players[0].OrganismHealth);
            // The formula: base + per-cell × blob + per-building × the finished nucleus.
            int cells = 0;
            for (int c = 0; c < w.Territory.Length; c++) if (w.Territory[c] == 0) cells++;
            Assert.Equal(1000 + 5 * cells + 200, expected);
        }

        [Fact]
        public void HealthRegeneratesTowardTheGrowingMax()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            int max = HealthSystem.MaxOf(w, sim.Defs, 0);
            w.Players[0].OrganismHealth = max - 50;
            HealthSystem.Tick(w, sim.Defs);
            Assert.Equal(max - 45, w.Players[0].OrganismHealth);
            w.Players[0].OrganismHealth = max - 2;
            HealthSystem.Tick(w, sim.Defs);
            Assert.Equal(max, w.Players[0].OrganismHealth); // clamped, never over
        }

        [Fact]
        public void BledToZeroIsElimination()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            w.Players[0].OrganismHealth = -10; // deeper than one beat of regen can save
            HealthSystem.Tick(w, sim.Defs);
            Assert.False(w.Players[0].Alive);
            for (int c = 0; c < w.Territory.Length; c++) Assert.NotEqual(0, (int)w.Territory[c]);
        }
    }

    public class VictoryTests
    {
        [Fact]
        public void ThreeQuartersOfTheDishEndsTheMatch()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            var w = sim.World;
            // Paint 80% of the (wall-free) dish for p0; p1's far-corner blob survives.
            int target = w.OwnableCellCount * 80 / 100;
            for (int c = 0; c < target; c++) w.Territory[c] = 0;
            Assert.True(w.Players[1].Alive);

            VictorySystem.Tick(w, sim.Defs);
            Assert.False(w.Players[1].Alive);
            Assert.True(w.Players[0].Alive);
            Assert.Equal(1, sim.AliveTeams());
            Assert.Equal(w.Players[0].Team, (byte)sim.WinningTeam());
        }

        [Fact]
        public void BelowTheThresholdNothingHappens()
        {
            var sim = TestWorlds.NewSim(42, new CommandLog());
            VictorySystem.Tick(sim.World, sim.Defs);
            Assert.Equal(2, sim.AlivePlayers());
        }
    }

    public class FrontCombatDeterminismTests
    {
        [Fact]
        public void PushCombatMatchesAreBitIdenticalOverTwoThousandTicks()
        {
            ulong Run()
            {
                var log = new CommandLog();
                var sim = TestWorlds.NewSim(42, log);
                var w = sim.World;
                int hq0 = WorldQuery.FindHq(w, sim.Defs, 0);
                int hq1 = WorldQuery.FindHq(w, sim.Defs, 1);
                log.Add(new Command { Tick = 0, Player = 0, Type = CommandType.SetProduceOverride, A = hq0, B = 1 });
                log.Add(new Command { Tick = 0, Player = 1, Type = CommandType.SetProduceOverride, A = hq1, B = 1 });
                log.Add(new Command
                {
                    Tick = 0, Player = 0, Type = CommandType.PushFront,
                    A = FrontMath.Sector(4, 2800, 2800), B = 3400, C = 3400,
                });
                log.Add(new Command
                {
                    Tick = 0, Player = 1, Type = CommandType.PushFront,
                    A = FrontMath.Sector(4, -2800, -2800), B = 600, C = 600,
                });
                for (int t = 0; t < 2000; t++) sim.Tick();
                return sim.StateHash();
            }
            Assert.Equal(Run(), Run());
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
