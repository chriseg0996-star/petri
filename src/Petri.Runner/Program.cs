using System;
using System.Collections.Generic;
using System.IO;
using Petri.Core;

namespace Petri.Runner
{
    /// <summary>
    /// Headless CLI for the new game. Verbs:
    ///   run-match   --seed N --ticks T [--map petri-dish] [--data DIR]   plays a scripted 2-player match
    ///   determinism --seed N --ticks T [--map petri-dish] [--data DIR]   fresh-rerun + replay bit-identity gate
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 0) { PrintHelp(); return 2; }
            string verb = args[0];
            var opt = ParseFlags(args);
            ulong seed = (ulong)GetLong(opt, "--seed", 42);
            int ticks = (int)GetLong(opt, "--ticks", 6000);
            string dataDir = opt.TryGetValue("--data", out var d) ? d : FindDataDir();
            string mapName = opt.TryGetValue("--map", out var m) ? m : "petri-dish";

            var defs = DefLoader.Load(dataDir);
            var map = DefLoader.LoadMap(dataDir, mapName);
            Console.WriteLine($"map={map.Name} seed={seed} players=2 defsHash={defs.DefsHash:x16}");

            switch (verb)
            {
                case "run-match":
                {
                    var r = RunMatch(defs, map, seed, ticks, verbose: true, replayLog: null);
                    Console.WriteLine($"winner={(r.Winner < 0 ? "none (tick cap reached)" : "P" + r.Winner)} finalTick={r.FinalTick} stateHash={r.Hash:x16} commands={r.Log.Count}");
                    return 0;
                }
                case "determinism":
                {
                    var a = RunMatch(defs, map, seed, ticks, verbose: false, replayLog: null);
                    Console.WriteLine($"run A: hash={a.Hash:x16} finalTick={a.FinalTick} commands={a.Log.Count}");

                    var b = RunMatch(defs, map, seed, ticks, verbose: false, replayLog: null);
                    bool freshOk = Compare("run B (fresh rerun)", a, b);

                    var c = RunMatch(defs, map, seed, ticks, verbose: false, replayLog: a.Log);
                    bool replayOk = Compare("run C (replay of A's log)", a, c);

                    Console.WriteLine(freshOk && replayOk ? "determinism: PASS" : "determinism: FAIL");
                    return freshOk && replayOk ? 0 : 1;
                }
                case "bench":
                {
                    // Perf probe: a real bot match, reporting sim cost as the armies grow.
                    int players = (int)GetLong(opt, "--players", 2);
                    RunBench(defs, map, seed, ticks, players);
                    return 0;
                }
                default:
                    PrintHelp();
                    return 2;
            }
        }

        /// <summary>Drives a bot free-for-all and prints ms/tick against entity count, so the
        /// growth curve (not just the average) is visible.</summary>
        private static void RunBench(DefDatabase defs, MapDef map, ulong seed, int ticks, int players)
        {
            var log = new CommandLog();
            var sim = new Simulation(defs, map, players, seed, log);
            var bots = new BotController[players];
            for (byte p = 0; p < players; p++) bots[p] = new BotController(p, seed);
            var buffer = new List<Command>();
            var sw = new System.Diagnostics.Stopwatch();
            var total = System.Diagnostics.Stopwatch.StartNew();

            Console.WriteLine($"bench: players={players} ticks={ticks}");
            Console.WriteLine("  tick   entities   ms/tick(win)   sim-ticks/sec");
            long windowTicks = 0;
            sw.Start();
            for (int t = 0; t < ticks; t++)
            {
                buffer.Clear();
                for (byte p = 0; p < players; p++) bots[p].Think(sim.World, sim.Defs, buffer);
                for (int k = 0; k < buffer.Count; k++)
                {
                    var c = buffer[k];
                    c.Tick = sim.TickCount;
                    log.Add(c);
                }
                sim.Tick();
                windowTicks++;

                if ((t + 1) % 1000 == 0)
                {
                    sw.Stop();
                    int live = 0;
                    for (int i = 0; i < sim.World.HighWater; i++)
                        if (sim.World.Kind[i] != EntityKind.None) live++;
                    double msPer = sw.Elapsed.TotalMilliseconds / windowTicks;
                    Console.WriteLine($"  {t + 1,5}   {live,8}   {msPer,10:0.00}   {1000.0 / Math.Max(msPer, 0.0001),12:0}");
                    windowTicks = 0;
                    sw.Restart();
                }
                if (sim.AliveTeams() <= 1) break;
            }
            total.Stop();
            Console.WriteLine($"bench: {ticks} ticks in {total.Elapsed.TotalSeconds:0.00}s  (budget is 50 ms/tick at 20 Hz)");
        }

        private sealed class MatchResult
        {
            public ulong Hash;
            public int FinalTick;
            public int Winner;
            public CommandLog Log = new CommandLog();
            public List<ulong> Checkpoints = new List<ulong>(); // every 100 ticks
        }

        private static MatchResult RunMatch(DefDatabase defs, MapDef map, ulong seed, int ticks, bool verbose, CommandLog? replayLog)
        {
            var log = replayLog ?? new CommandLog();
            var sim = new Simulation(defs, map, 2, seed, log);
            var driver = replayLog == null ? new ScriptedDriver(defs) : null;
            var result = new MatchResult { Log = log, Winner = -1 };

            for (int t = 0; t < ticks; t++)
            {
                driver?.Enqueue(sim, log);
                sim.Tick();
                if (sim.TickCount % 100 == 0) result.Checkpoints.Add(sim.StateHash());
                if (verbose && sim.TickCount % 600 == 0) PrintStatus(sim);
                if (sim.AlivePlayers() <= 1) { result.Winner = sim.Winner(); break; }
            }

            result.FinalTick = sim.TickCount;
            result.Hash = sim.StateHash();
            return result;
        }

        private static bool Compare(string label, MatchResult expected, MatchResult actual)
        {
            if (expected.Hash == actual.Hash && expected.FinalTick == actual.FinalTick)
            {
                Console.WriteLine($"{label}: PASS (bit-identical)");
                return true;
            }
            int n = Math.Min(expected.Checkpoints.Count, actual.Checkpoints.Count);
            for (int i = 0; i < n; i++)
            {
                if (expected.Checkpoints[i] != actual.Checkpoints[i])
                {
                    Console.WriteLine($"{label}: FAIL — first divergence at tick {(i + 1) * 100} ({expected.Checkpoints[i]:x16} vs {actual.Checkpoints[i]:x16})");
                    return false;
                }
            }
            Console.WriteLine($"{label}: FAIL — final hash {expected.Hash:x16} vs {actual.Hash:x16} (finalTick {expected.FinalTick} vs {actual.FinalTick})");
            return false;
        }

        private static void PrintStatus(Simulation sim)
        {
            var w = sim.World;
            var parts = new List<string>();
            for (byte p = 0; p < w.Players.Length; p++)
            {
                var pl = w.Players[p];
                int cells = 0;
                for (int c = 0; c < w.Territory.Length; c++) if (w.Territory[c] == p) cells++;
                int force = 0;
                for (int k = 0; k < pl.Force.Length; k++) force += pl.Force[k];
                int pct = w.OwnableCellCount > 0 ? cells * 100 / w.OwnableCellCount : 0;
                parts.Add($"P{p} terr={pct}% food={pl.Food} workers={pl.WorkerCount} force={force} hp={pl.OrganismHealth}");
            }
            int sec = w.TickCount / SimConstants.TicksPerSecond;
            Console.WriteLine($"t={sec / 60:00}:{sec % 60:00} | {string.Join(" | ", parts)}");
        }

        private static Dictionary<string, string> ParseFlags(string[] args)
        {
            var opt = new Dictionary<string, string>();
            for (int i = 1; i < args.Length - 1; i++)
                if (args[i].StartsWith("--")) opt[args[i]] = args[i + 1];
            return opt;
        }

        private static long GetLong(Dictionary<string, string> opt, string key, long fallback) =>
            opt.TryGetValue(key, out var v) && long.TryParse(v, out var n) ? n : fallback;

        private static string FindDataDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "data");
                if (File.Exists(Path.Combine(candidate, "rules.json"))) return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("could not locate the data directory; pass --data <dir>");
        }

        private static void PrintHelp()
        {
            Console.WriteLine("verbs:");
            Console.WriteLine("  run-match   --seed N --ticks T [--map petri-dish] [--data DIR]");
            Console.WriteLine("  determinism --seed N --ticks T [--map petri-dish] [--data DIR]");
        }
    }

    /// <summary>
    /// A scripted command source standing in for players. It reads sim state and issues
    /// commands through the same log a UI or network peer would use: composition weights
    /// and an incubator placement at tick 0, then periodic PushFront orders toward the
    /// enemy nucleus so growth, fronts, and (once implemented) combat all get exercised.
    /// </summary>
    internal sealed class ScriptedDriver
    {
        private readonly int _workerIx;
        private readonly int _soldierIx;
        private readonly int _spitterIx;
        private readonly int _incubatorIx;

        public ScriptedDriver(DefDatabase defs)
        {
            _workerIx = defs.UnitIndex("strain.forager");
            _soldierIx = defs.UnitIndex("strain.predator");
            _spitterIx = defs.UnitIndex("strain.secretor");
            _incubatorIx = defs.BuildingIndex("strain.incubator");
        }

        private static int Centi(Fix v) => (int)(v.Raw * 100 >> Fix.FracBits);

        public void Enqueue(Simulation sim, CommandLog log)
        {
            var w = sim.World;
            int tick = w.TickCount;

            if (tick == 0)
            {
                for (byte p = 0; p < w.Players.Length; p++)
                {
                    log.Add(new Command { Tick = 0, Player = p, Type = CommandType.SetProductionWeight, A = _workerIx, B = 2 });
                    log.Add(new Command { Tick = 0, Player = p, Type = CommandType.SetProductionWeight, A = _soldierIx, B = 4 });
                    log.Add(new Command { Tick = 0, Player = p, Type = CommandType.SetProductionWeight, A = _spitterIx, B = 3 });
                }
                return;
            }

            // Once the starting blob has grown a little, drop an incubator next to the
            // nucleus (buildings self-build inside own territory now).
            if (tick == 200)
            {
                for (byte p = 0; p < w.Players.Length; p++)
                {
                    int myHq = WorldQuery.FindHq(w, sim.Defs, p);
                    if (myHq < 0) continue;
                    int mx = Centi(w.Pos[myHq].X), my = Centi(w.Pos[myHq].Y);
                    int toward = mx < Centi(w.MapWidth) / 2 ? 1 : -1;
                    log.Add(new Command
                    {
                        Tick = tick, Player = p, Type = CommandType.ConstructBuilding, A = -1,
                        B = mx + toward * 400, C = my, D = _incubatorIx,
                    });
                }
                return;
            }

            // Periodic pushes: each player shoves the front facing the enemy nucleus.
            if (tick < 1500 || (tick - 1500) % 1200 != 0) return;
            for (byte p = 0; p < w.Players.Length; p++)
            {
                if (!w.Players[p].Alive) continue;
                byte enemy = (byte)(1 - p);
                int enemyHq = WorldQuery.FindHq(w, sim.Defs, enemy);
                if (enemyHq < 0) continue;
                int ex = Centi(w.Pos[enemyHq].X), ey = Centi(w.Pos[enemyHq].Y);
                // Which of my fronts faces the enemy nucleus? Classify with the shared math
                // against my current centroid (deterministic: reads hashed state only).
                int front = FrontMath.Sector(w.Players[p].FrontCount,
                    ex - w.ScratchCentXCenti[p], ey - w.ScratchCentYCenti[p]);
                log.Add(new Command { Tick = tick, Player = p, Type = CommandType.PushFront, A = front, B = ex, C = ey });
            }
        }
    }
}
