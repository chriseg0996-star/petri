using UnityEngine;

namespace Petri.Client
{
    /// <summary>
    /// Age-of-Empires-style main menu, built at runtime like the rest of the client (IMGUI,
    /// no scene assets). Campaign / Multiplayer / Replay are visible but disabled stubs;
    /// SKIRMISH is the working path (map + seed setup → match) since it's how the game is
    /// tested, and Settings holds the client-side knobs (game speed, camera pan).
    /// The menu owns the app flow: it boots on Play, launches matches, and matches return
    /// here via MatchBootstrap.QuitToMenu.
    /// </summary>
    public sealed class MainMenu : MonoBehaviour
    {
        public static MainMenu Instance { get; private set; }

        private enum Panel { Root, Skirmish, Settings }

        private static readonly string[] Maps = { "petri-dish", "capillary", "agar-plate" };
        private static readonly string[] MapLabels =
        {
            "Petri Dish  (square, up to 8p)",
            "Capillary  (long, 2p)",
            "Agar Plate  (large ring, up to 32p)",
        };
        private static readonly int[] MapMaxPlayers = { 8, 2, 32 }; // must match each map's spawn count

        private Panel _panel = Panel.Root;
        private bool _visible = true;
        private string _seedText = "7";
        private int _mapIx;
        private GUIStyle _title, _tagline, _button, _buttonSub, _label, _heading;
        private Texture2D _white;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (Instance != null) return;
            var go = new GameObject("PetriMenu");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<MainMenu>();
            MatchBootstrap.EnsureCamera(); // dark backdrop behind the menu
            MatchBootstrap.GameSpeed = PlayerPrefs.GetFloat("skitter.gameSpeed", 1f);
            CameraRig.PanSpeedMult = PlayerPrefs.GetFloat("skitter.panSpeed", 1f);
        }

        public void ShowMenu()
        {
            _visible = true;
            _panel = Panel.Root;
        }

        private void OnGUI()
        {
            if (!_visible) return;
            EnsureStyles();

            // Full-screen backdrop.
            var old = GUI.color;
            GUI.color = new Color(0.05f, 0.07f, 0.05f, 1f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _white);
            GUI.color = old;

            float cx = Screen.width * 0.5f;
            GUI.Label(new Rect(0, Screen.height * 0.12f, Screen.width, 80), "PETRI", _title);
            GUI.Label(new Rect(0, Screen.height * 0.12f + 74, Screen.width, 30),
                "a deterministic microbial RTS", _tagline);

            switch (_panel)
            {
                case Panel.Root: DrawRoot(cx); break;
                case Panel.Skirmish: DrawSkirmish(cx); break;
                case Panel.Settings: DrawSettings(cx); break;
            }
        }

        private void DrawRoot(float cx)
        {
            float w = 340f, h = 52f, gap = 12f;
            float y = Screen.height * 0.34f;
            Rect Next() { var r = new Rect(cx - w * 0.5f, y, w, h); y += h + gap; return r; }

            GUI.enabled = false;
            GUI.Button(Next(), "Campaign\n<size=10><i>coming soon</i></size>", _buttonSub);
            GUI.enabled = true;

            if (GUI.Button(Next(), "Skirmish", _button)) _panel = Panel.Skirmish;

            GUI.enabled = false;
            GUI.Button(Next(), "Multiplayer\n<size=10><i>coming soon</i></size>", _buttonSub);
            GUI.Button(Next(), "Replay\n<size=10><i>coming soon</i></size>", _buttonSub);
            GUI.enabled = true;

            if (GUI.Button(Next(), "Settings", _button)) _panel = Panel.Settings;
            if (GUI.Button(Next(), "Exit", _button)) Application.Quit();
        }

        // Stepping 2..32 one seat at a time would be 30 clicks; jump through useful sizes.
        private static readonly int[] PlayerSteps = { 2, 3, 4, 6, 8, 12, 16, 24, 32 };

        private static int NextPlayerStep(int current, int maxP)
        {
            for (int i = 0; i < PlayerSteps.Length; i++)
                if (PlayerSteps[i] > current && PlayerSteps[i] <= maxP) return PlayerSteps[i];
            return 2; // wrap
        }

        private void DrawSkirmish(float cx)
        {
            float w = 420f;
            float x = cx - w * 0.5f;
            float y = Screen.height * 0.28f; // the team grid makes this panel tall

            GUI.Label(new Rect(x, y, w, 28), "SKIRMISH", _heading);
            y += 34;

            // Start (and Back) ride at the TOP: with up to 32 team buttons the settings below
            // run long, and anything under them can fall off the bottom of the screen.
            if (GUI.Button(new Rect(x, y, w - 126, 46), "<b>Start Match</b>", _button)) StartSkirmish();
            if (GUI.Button(new Rect(x + w - 120, y, 120, 46), "Back", _buttonSub)) _panel = Panel.Root;
            y += 56;

            GUI.Label(new Rect(x, y, 120, 26), "Map", _label);
            if (GUI.Button(new Rect(x + 130, y, w - 130, 26), MapLabels[_mapIx] + "  ▸", _buttonSub))
            {
                _mapIx = (_mapIx + 1) % Maps.Length;
                // A map can't seat more players than it has spawns.
                MatchBootstrap.PendingPlayers = Mathf.Min(MatchBootstrap.PendingPlayers, MapMaxPlayers[_mapIx]);
            }
            y += 34;

            GUI.Label(new Rect(x, y, 120, 26), "Players", _label);
            int maxP = MapMaxPlayers[_mapIx];
            int bots = MatchBootstrap.PendingPlayers - 1;
            string pLabel = maxP <= 2
                ? "2  —  you + 1 bot  (map seats 2)"
                : $"{MatchBootstrap.PendingPlayers}  —  you + {bots} bot{(bots == 1 ? "" : "s")}  ▸";
            if (GUI.Button(new Rect(x + 130, y, w - 130, 26), pLabel, _buttonSub) && maxP > 2)
                MatchBootstrap.PendingPlayers = NextPlayerStep(MatchBootstrap.PendingPlayers, maxP);
            y += 34;

            // ---- Teams: one button per seat, click to cycle its team. Same team = allies.
            GUI.Label(new Rect(x, y, 120, 26), "Teams", _label);
            if (GUI.Button(new Rect(x + 130, y, 90, 26), "Free-for-all", _buttonSub))
                for (int p = 0; p < MatchBootstrap.MaxPlayers; p++) MatchBootstrap.PendingTeams[p] = p;
            if (GUI.Button(new Rect(x + 226, y, 90, 26), "Split 2", _buttonSub))
                for (int p = 0; p < MatchBootstrap.MaxPlayers; p++)
                    MatchBootstrap.PendingTeams[p] = p < MatchBootstrap.PendingPlayers / 2 ? 0 : 1;
            y += 30;

            // Two roomy columns for small games; four compact ones once the grid gets long.
            int seats = MatchBootstrap.PendingPlayers;
            int cols = seats <= 8 ? 2 : 4;
            float rowH = seats <= 8 ? 28f : 22f;
            float cellW = w / cols - 4f;
            for (int p = 0; p < seats; p++)
            {
                var cell = new Rect(x + (p % cols) * (w / cols), y + (p / cols) * rowH, cellW, rowH - 2f);
                string label = cols == 2
                    ? $"P{p} {(p == 0 ? "You" : "Bot")}  —  Team {MatchBootstrap.PendingTeams[p] + 1}"
                    : $"{(p == 0 ? "You" : "P" + p)} T{MatchBootstrap.PendingTeams[p] + 1}";
                if (GUI.Button(cell, label, _buttonSub))
                    MatchBootstrap.PendingTeams[p] = (MatchBootstrap.PendingTeams[p] + 1) % seats;
            }
            y += ((seats + cols - 1) / cols) * rowH + 8;

            GUI.Label(new Rect(x, y, 120, 26), "Seed", _label);
            _seedText = GUI.TextField(new Rect(x + 130, y, w - 220, 26), _seedText, 12);
            if (GUI.Button(new Rect(x + w - 82, y, 82, 26), "Random", _buttonSub))
                _seedText = ((uint)Random.Range(1, int.MaxValue)).ToString();
            y += 34;

            GUI.Label(new Rect(x, y, 120, 26), "Opponent", _label);
            if (GUI.Button(new Rect(x + 130, y, w - 130, 26),
                MatchBootstrap.PendingBot ? "Computer — econ + attack waves  ▸" : "None — sandbox  ▸", _buttonSub))
                MatchBootstrap.PendingBot = !MatchBootstrap.PendingBot;
            y += 34;

            GUI.Label(new Rect(x, y, 120, 26), "Fog of War", _label);
            if (GUI.Button(new Rect(x + 130, y, w - 130, 26),
                MatchBootstrap.PendingFog ? "On  ▸" : "Off — all-seeing  ▸", _buttonSub))
                MatchBootstrap.PendingFog = !MatchBootstrap.PendingFog;
            y += 34;

            GUI.Label(new Rect(x, y, w, 44), !MatchBootstrap.PendingBot
                ? "<i>Rival strains grow and defend but have no general — perfect for drilling your swarms.</i>"
                : MatchBootstrap.PendingPlayers > 2
                    ? "<i>Every strain for itself: each rival feeds, divides, and throws attack waves at whoever it finds first.</i>"
                    : "<i>The rival strain feeds, divides, and throws attack waves at your colony — hold the line.</i>", _label);
        }

        private void DrawSettings(float cx)
        {
            float w = 420f;
            float x = cx - w * 0.5f;
            float y = Screen.height * 0.34f;

            GUI.Label(new Rect(x, y, w, 28), "SETTINGS", _heading);
            y += 44;

            GUI.Label(new Rect(x, y, 200, 24), $"Game speed  ×{MatchBootstrap.GameSpeed:0.0}", _label);
            float gs = GUI.HorizontalSlider(new Rect(x + 210, y + 6, w - 210, 16), MatchBootstrap.GameSpeed, 0.5f, 4f);
            gs = Mathf.Round(gs * 2f) / 2f; // half-step detents
            if (!Mathf.Approximately(gs, MatchBootstrap.GameSpeed))
            {
                MatchBootstrap.GameSpeed = gs;
                PlayerPrefs.SetFloat("skitter.gameSpeed", gs);
            }
            y += 34;

            GUI.Label(new Rect(x, y, 200, 24), $"Camera pan  ×{CameraRig.PanSpeedMult:0.0}", _label);
            float ps = GUI.HorizontalSlider(new Rect(x + 210, y + 6, w - 210, 16), CameraRig.PanSpeedMult, 0.5f, 3f);
            ps = Mathf.Round(ps * 2f) / 2f;
            if (!Mathf.Approximately(ps, CameraRig.PanSpeedMult))
            {
                CameraRig.PanSpeedMult = ps;
                PlayerPrefs.SetFloat("skitter.panSpeed", ps);
            }
            y += 44;

            GUI.Label(new Rect(x, y, w, 24), "<i>Game speed paces real time only — the sim stays deterministic.</i>", _label);
            y += 40;
            if (GUI.Button(new Rect(x, y, 120, 34), "Back", _buttonSub)) _panel = Panel.Root;
        }

        private void StartSkirmish()
        {
            if (!ulong.TryParse(_seedText, out ulong seed) || seed == 0) seed = 7;
            MatchBootstrap.PendingSeed = seed;
            MatchBootstrap.PendingMap = Maps[_mapIx];
            _visible = false;
            new GameObject("PetriMatch").AddComponent<MatchBootstrap>();
        }

        private void EnsureStyles()
        {
            if (_title != null) return;
            _white = new Texture2D(1, 1);
            _white.SetPixel(0, 0, Color.white);
            _white.Apply();
            _title = new GUIStyle(GUI.skin.label)
            {
                fontSize = 64, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
            };
            _title.normal.textColor = new Color(0.85f, 0.95f, 0.7f);
            _tagline = new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.MiddleCenter, richText = true };
            _tagline.normal.textColor = new Color(0.6f, 0.7f, 0.55f);
            _button = new GUIStyle(GUI.skin.button) { fontSize = 18, richText = true };
            _buttonSub = new GUIStyle(GUI.skin.button) { fontSize = 13, richText = true, alignment = TextAnchor.MiddleCenter };
            _label = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true, wordWrap = true };
            _heading = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };
            _heading.normal.textColor = new Color(0.85f, 0.95f, 0.7f);
        }
    }
}
