using System.Collections.Generic;
using UnityEngine;
using Petri.Core;

namespace Petri.Client
{
    /// <summary>
    /// Owns one live match: loads defs+map, creates the deterministic Simulation, and advances
    /// it at a fixed 20 ticks/second independent of render framerate (an accumulator, NOT
    /// FixedUpdate, so the sim clock is ours alone). All player input becomes Commands that are
    /// stamped with the current tick and flushed into the CommandLog just before each Tick —
    /// exactly the path a network peer's commands would take.
    ///
    /// The whole scene is built from code via RuntimeInitializeOnLoadMethod, so pressing Play in
    /// any empty scene runs the game — no scene asset to hand-author.
    /// </summary>
    public sealed class MatchBootstrap : MonoBehaviour
    {
        public const int HumanPlayer = 0;
        public const int MaxPlayers = 32;             // 1 human + up to 31 bots
        public static int PendingPlayers = 2;         // chosen in the skirmish menu (2..map spawns)
        public int PlayerCount { get; private set; }  // locked in for this match at Start
        // Team per player slot, set in the skirmish menu. All-distinct = free-for-all.
        public static readonly int[] PendingTeams = BuildDefaultTeams();

        private static int[] BuildDefaultTeams()
        {
            var t = new int[MaxPlayers];
            for (int p = 0; p < MaxPlayers; p++) t[p] = p; // default: everyone their own team
            return t;
        }

        // Skirmish setup, written by the main menu before the match object is created.
        public static ulong PendingSeed = 7;
        public static string PendingMap = "petri-dish";
        public static bool PendingBot = true;  // rival colony driven by BotController
        public static bool PendingFog = true;  // client-side fog of war
        // Client-side pacing only: scales how fast real time feeds the fixed 20 Hz sim.
        public static float GameSpeed = 1f;

        private const double TickSeconds = 1.0 / SimConstants.TicksPerSecond;
        private const int MaxCatchUpTicks = 5; // avoid a spiral of death after a hitch

        public Simulation Sim { get; private set; }
        public DefDatabase Defs { get; private set; }
        public MapDef Map { get; private set; }
        public InputController Input { get; private set; }
        public HudView Hud { get; private set; }
        public GameView View { get; private set; }
        public ulong SeedValue { get; private set; }
        public int WinnerTeam { get; private set; } = -1; // set once one team is left standing
        /// <summary>True when the decided match was won by the human's team (allies count).</summary>
        public bool HumanWon => WinnerTeam >= 0 && Sim.World.Players[HumanPlayer].Team == WinnerTeam;
        public bool Paused;

        /// <summary>Fraction [0,1] of the way from the last simulated tick to the next — the
        /// view lerps entity positions by this so 20 Hz motion renders smooth at any framerate.</summary>
        public float TickAlpha => Paused ? 1f : Mathf.Clamp01((float)(_accumulator / TickSeconds));

        private CommandLog _log;
        private readonly List<Command> _pending = new List<Command>();
        private double _accumulator;
        private BotController[] _bots; // null when the skirmish opponent is disabled

        private void Start()
        {
            SeedValue = PendingSeed;
            Defs = UnityDataLoader.LoadDefs();
            Map = UnityDataLoader.LoadMap(PendingMap);
            // Free-for-all: every player is hostile to every other. The map's spawn count is
            // the hard ceiling — never ask the sim for more players than it can seat.
            PlayerCount = Mathf.Clamp(PendingPlayers, 2, Mathf.Min(MaxPlayers, Map.Spawns.Length));
            var teams = new byte[PlayerCount];
            for (int p = 0; p < PlayerCount; p++) teams[p] = (byte)PendingTeams[p];
            // A match needs at least two sides; if every seat landed on one team the game would
            // be "won" at tick 0, so fall back to a free-for-all.
            bool twoSided = false;
            for (int p = 0; p < PlayerCount && !twoSided; p++)
                for (int q = p + 1; q < PlayerCount; q++)
                    if (teams[p] != teams[q]) { twoSided = true; break; }
            if (!twoSided) for (int p = 0; p < PlayerCount; p++) teams[p] = (byte)p;
            _log = new CommandLog();
            Sim = new Simulation(Defs, Map, PlayerCount, SeedValue, _log, teams);

            if (PendingBot)
            {
                // Every non-human player gets a bot general. Bots are command sources only:
                // their orders ride the same pending list and log as the player's clicks.
                _bots = new BotController[PlayerCount];
                for (byte p = 1; p < PlayerCount; p++) _bots[p] = new BotController(p, SeedValue);
            }

            var cam = EnsureCamera();
            var rig = cam.GetComponent<CameraRig>();
            if (rig == null) rig = cam.gameObject.AddComponent<CameraRig>();
            rig.Configure(Map);

            var viewGo = new GameObject("GameView");
            viewGo.transform.SetParent(transform);
            View = viewGo.AddComponent<GameView>();
            View.Bind(this);

            Input = gameObject.AddComponent<InputController>();
            Input.Bind(this, cam);

            Hud = gameObject.AddComponent<HudView>();
            Hud.Bind(this);

            Debug.Log($"[Petri] Match started: map={Map.Name} seed={SeedValue} defsHash={Defs.DefsHash:x16} " +
                      $"units={Defs.Units.Length}");
        }

        private void Update()
        {
            if (Paused) return;
            _accumulator += Time.deltaTime * GameSpeed;
            int steps = 0;
            while (_accumulator >= TickSeconds && steps < MaxCatchUpTicks)
            {
                if (_bots != null)
                    for (int p = 1; p < _bots.Length; p++)
                        _bots[p].Think(Sim.World, Defs, _pending); // commands, Player pre-stamped
                FlushPending();
                Sim.Tick();
                DispatchAttackFx();
                _accumulator -= TickSeconds;
                steps++;
            }
            if (steps == MaxCatchUpTicks) _accumulator = 0; // dropped behind; resync

            // The match ends when one TEAM is left standing (a lone player is a team of one).
            if (WinnerTeam < 0 && Sim.AliveTeams() <= 1)
            {
                WinnerTeam = Sim.WinningTeam();
                Paused = true; // freeze the field under the victory banner
            }
        }

        /// <summary>Tear the match down and return to the main menu.</summary>
        public void QuitToMenu()
        {
            Destroy(gameObject); // GameView (child), Input, and Hud die with it
            if (MainMenu.Instance != null) MainMenu.Instance.ShowMenu();
        }

        /// <summary>Queue a command for the current tick. Applied at the next Tick boundary.</summary>
        public void Enqueue(Command c)
        {
            c.Tick = Sim.TickCount;
            c.Player = HumanPlayer;
            _pending.Add(c);
        }

        /// <summary>Feed this tick's landed hits to the view (ranged shots become projectiles).</summary>
        private void DispatchAttackFx()
        {
            if (View == null) return;
            var events = Sim.World.AttackEvents;
            for (int k = 0; k < events.Count; k++)
                View.SpawnAttackFx(events[k].Attacker, events[k].Target);
        }

        private void FlushPending()
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                var c = _pending[i];
                c.Tick = Sim.TickCount; // stamp with the tick actually being simulated
                _log.Add(c);
            }
            _pending.Clear();
        }

        public static Camera EnsureCamera()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera");
                go.tag = "MainCamera";
                cam = go.AddComponent<Camera>();
            }
            cam.orthographic = true;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.10f, 0.12f, 0.10f);
            cam.transform.rotation = Quaternion.identity; // look down +Z at the XY board
            return cam;
        }
    }
}
