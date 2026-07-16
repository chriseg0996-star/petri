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
                int units = 0;
                int leaders = 0;
                int hqHp = 0;
                for (int i = 0; i < w.HighWater; i++)
                {
                    if (w.Kind[i] == EntityKind.Unit && w.Owner[i] == p)
                    {
                        units++;
                        if (sim.Defs.Units[w.DefIndex[i]].IsLeader) leaders++;
                    }
                    if (w.Kind[i] == EntityKind.Building && w.Owner[i] == p && sim.Defs.Buildings[w.DefIndex[i]].IsHeadquarters) hqHp = w.Hp[i];
                }
                parts.Add($"P{p} food={w.Players[p].Food} units={units} leaders={leaders} hqHP={hqHp}");
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
    /// A scripted command source standing in for players (per project direction: no AI bots
    /// yet). It reads sim state and issues commands through the same log a UI or network
    /// peer would use: composition weights at tick 0, then periodic attack waves at the
    /// enemy headquarters so combat, collision, and elimination all get exercised.
    /// </summary>
    internal sealed class ScriptedDriver
    {
        private readonly int _workerIx;
        private readonly int _soldierIx;
        private readonly int _spitterIx;
        private readonly int _leaderIx;
        private readonly int _incubatorIx;

        public ScriptedDriver(DefDatabase defs)
        {
            _workerIx = defs.UnitIndex("strain.forager");
            _soldierIx = defs.UnitIndex("strain.predator");
            _spitterIx = defs.UnitIndex("strain.secretor");
            _leaderIx = defs.UnitIndex("strain.swarm-leader");
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
                    log.Add(new Command { Tick = 0, Player = p, Type = CommandType.SetProductionWeight, A = _leaderIx, B = 1 });

                    // Combat units now need an incubator (no starting one): each player builds
                    // one toward the enemy so the scripted match still fields an army.
                    int myHq = WorkerSystem.FindHq(w, sim.Defs, p);
                    int foeHq = WorkerSystem.FindHq(w, sim.Defs, (byte)(w.Players.Length - 1 - p));
                    int worker = -1;
                    for (int e = 0; e < w.HighWater; e++)
                        if (w.Kind[e] == EntityKind.Unit && w.Owner[e] == p && sim.Defs.Units[w.DefIndex[e]].IsWorker) { worker = e; break; }
                    if (myHq >= 0 && foeHq >= 0 && worker >= 0)
                    {
                        int mx = Centi(w.Pos[myHq].X), my = Centi(w.Pos[myHq].Y);
                        int fx = Centi(w.Pos[foeHq].X), fy = Centi(w.Pos[foeHq].Y);
                        log.Add(new Command
                        {
                            Tick = 0, Player = p, Type = CommandType.ConstructBuilding, A = worker,
                            B = mx + (fx - mx) / 16, C = my + (fy - my) / 16, D = _incubatorIx,
                        });
                    }
                }
                return;
            }

            if (tick < 1500 || (tick - 1500) % 1200 != 0) return;

            for (byte p = 0; p < w.Players.Length; p++)
            {
                if (!w.Players[p].Alive) continue;
                byte enemy = (byte)(1 - p);
                int enemyHq = WorkerSystem.FindHq(w, sim.Defs, enemy);
                if (enemyHq < 0) continue;
                int hqX = (int)(w.Pos[enemyHq].X.Raw * 100 >> Fix.FracBits);
                int hqY = (int)(w.Pos[enemyHq].Y.Raw * 100 >> Fix.FracBits);
                for (int e = 0; e < w.HighWater; e++)
                {
                    if (w.Kind[e] != EntityKind.Unit || w.Owner[e] != p) continue;
                    var def = sim.Defs.Units[w.DefIndex[e]];
                    if (def.IsWorker) continue;
                    if (def.IsLeader)
                    {
                        // Leaders drive their whole squad: formation-move at the enemy HQ,
                        log.Add(new Command
                        {
                            Tick = tick, Player = p, Type = CommandType.FormationMove,
                            A = e, B = hqX, C = hqY,
                        });
                    }
                    else if (w.Leader[e] < 0 && !w.HasMoveOrder[e])
                    {
                        // Units without a swarm attack-move on their own.
                        log.Add(new Command
                        {
                            Tick = tick, Player = p, Type = CommandType.Move,
                            A = e, B = hqX + (e % 5 - 2) * 40, C = hqY + (e % 3 - 1) * 40,
                        });
                    }
                }
            }
        }
    }
}
