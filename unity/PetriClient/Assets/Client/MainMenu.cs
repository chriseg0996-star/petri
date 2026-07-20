using UnityEngine;

namespace Petri.Client
{
    /// <summary>
    /// The game's frontend, built entirely at runtime (IMGUI, no scene assets) so pressing Play
    /// in any empty scene boots straight into it. Two-level structure: a main page with the
    /// primary actions, and dedicated Skirmish / Settings / Credits sub-pages. Multiplayer and
    /// Replays are intentional, visibly-disabled COMING SOON tiles. The menu owns the app flow:
    /// it boots on Play, launches matches (MatchBootstrap), and matches return here via
    /// QuitToMenu → ShowMenu.
    ///
    /// Responsibilities are split so a later UI Toolkit port only replaces the Draw* layer:
    ///   - navigation state: MenuPage / _page
    ///   - configuration state + validation: SkirmishSetupState (wraps MatchBootstrap.Pending*)
    ///   - styles / textures: MenuStyles (cached; rebuilt only when the UI scale changes)
    ///   - drawing: the Draw* methods (all layout in 1280x720 reference units × Scale)
    ///   - match launching: StartSkirmish
    /// </summary>
    public sealed class MainMenu : MonoBehaviour
    {
        public static MainMenu Instance { get; private set; }

        private enum MenuPage { Main, Skirmish, Settings, Credits }

        private const string TaglineText = "A deterministic microbial real-time strategy game";

        // PlayerPrefs keys (the "skitter." prefix predates the Petri rename; kept so existing
        // saved values keep working).
        private const string PrefGameSpeed = "skitter.gameSpeed";
        private const string PrefPanSpeed = "skitter.panSpeed";
        private const string PrefZoomSpeed = "skitter.zoomSpeed";
        private const string PrefFogDefault = "skitter.fogDefault";

        // Settings chips: shared multiplier steps for camera pan / zoom sensitivity.
        private static readonly float[] MultSteps = { 0.5f, 1f, 1.5f, 2f, 2.5f, 3f };
        private static readonly string[] MultLabels = { "×0.5", "×1", "×1.5", "×2", "×2.5", "×3" };
        private static readonly string[] OnOffLabels = { "On", "Off" };
        private static readonly string[] OpponentLabels = { "Bots", "Sandbox" };

        private MenuPage _page = MenuPage.Main;
        private bool _visible = true;
        private readonly SkirmishSetupState _setup = new SkirmishSetupState();
        private MenuStyles _s;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (Instance != null) return;
            var go = new GameObject("PetriMenu");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<MainMenu>();
            MatchBootstrap.EnsureCamera(); // dark backdrop behind the menu
            MatchBootstrap.GameSpeed = PlayerPrefs.GetFloat(PrefGameSpeed, 1f);
            MatchBootstrap.PendingFog = PlayerPrefs.GetInt(PrefFogDefault, 1) != 0;
            CameraRig.PanSpeedMult = PlayerPrefs.GetFloat(PrefPanSpeed, 1f);
            CameraRig.ZoomSpeedMult = PlayerPrefs.GetFloat(PrefZoomSpeed, 1f);
        }

        public void ShowMenu()
        {
            _visible = true;
            GoTo(MenuPage.Main);
        }

        private void GoTo(MenuPage page)
        {
            _page = page;
            // Revalidate on entry: the error list must be current before Start is drawn.
            if (page == MenuPage.Skirmish) _setup.Revalidate();
        }

        private void OnGUI()
        {
            if (!_visible) return;
            if (_s == null) _s = new MenuStyles();
            _s.Ensure();

            DrawBackground();
            HandleEscape();

            switch (_page)
            {
                case MenuPage.Main: DrawMainPage(); break;
                case MenuPage.Skirmish: DrawSkirmishPage(); break;
                case MenuPage.Settings: DrawSettingsPage(); break;
                case MenuPage.Credits: DrawCreditsPage(); break;
            }
        }

        /// <summary>Reference units (1280x720 design) → pixels.</summary>
        private float S(float v) => v * _s.Scale;

        private void HandleEscape()
        {
            var e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape && _page != MenuPage.Main)
            {
                GoTo(MenuPage.Main);
                e.Use();
            }
        }

        // ---- Background & headers --------------------------------------------------

        private void DrawBackground()
        {
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _s.BgGradient,
                ScaleMode.StretchToFill);
            // Soft green bloom behind the upper third — light through agar.
            float gw = Mathf.Max(Screen.width * 0.9f, S(900));
            GUI.DrawTexture(new Rect((Screen.width - gw) * 0.5f, Screen.height * 0.06f - gw * 0.30f,
                gw, gw * 0.75f), _s.Glow, ScaleMode.StretchToFill);
        }

        private void DrawHeader()
        {
            GUI.Label(new Rect(0, Screen.height * 0.12f, Screen.width, S(72)), "PETRI", _s.Title);
            GUI.Label(new Rect(0, Screen.height * 0.12f + S(70), Screen.width, S(24)),
                TaglineText, _s.Tagline);
        }

        private void DrawPageHeader(string title)
        {
            GUI.Label(new Rect(0, S(24), Screen.width, S(36)), title, _s.PageTitle);
            float uw = S(64);
            GUI.DrawTexture(new Rect((Screen.width - uw) * 0.5f, S(62), uw, S(3)), _s.AccentBar,
                ScaleMode.StretchToFill);
        }

        // ---- Shared widgets ----------------------------------------------------------

        private bool DrawPrimaryButton(Rect r, string label) => GUI.Button(r, label, _s.Primary);

        private bool DrawSecondaryButton(Rect r, string label) => GUI.Button(r, label, _s.Secondary);

        private void DrawComingSoonButton(Rect r, string label)
        {
            GUI.Label(r, GUIContent.none, _s.DisabledTile);
            GUI.Label(new Rect(r.x, r.y + r.height * 0.08f, r.width, r.height * 0.55f),
                label, _s.DisabledTitle);
            GUI.Label(new Rect(r.x, r.y + r.height * 0.55f, r.width, r.height * 0.38f),
                "COMING SOON", _s.ComingSoon);
        }

        /// <summary>Row of equal-width selectable chips; returns the (possibly new) selection.</summary>
        private int DrawChips(Rect r, string[] labels, int selected)
        {
            float gap = S(6);
            float w = (r.width - gap * (labels.Length - 1)) / labels.Length;
            int result = selected;
            for (int i = 0; i < labels.Length; i++)
            {
                var cr = new Rect(r.x + i * (w + gap), r.y, w, r.height);
                if (GUI.Button(cr, labels[i], i == selected ? _s.ChipOn : _s.Chip) && i != selected)
                    result = i;
            }
            return result;
        }

        private static int NearestIx(float[] steps, float value)
        {
            int best = 0;
            for (int i = 1; i < steps.Length; i++)
                if (Mathf.Abs(steps[i] - value) < Mathf.Abs(steps[best] - value)) best = i;
            return best;
        }

        // ---- Main page -----------------------------------------------------------------

        private void DrawMainPage()
        {
            DrawHeader();

            float w = S(MenuStyles.MenuButtonW);
            float x = (Screen.width - w) * 0.5f;
            float y = Mathf.Max(Screen.height * 0.36f, Screen.height * 0.12f + S(120));
            float gap = S(10);

            if (DrawPrimaryButton(new Rect(x, y, w, S(56)), "SKIRMISH")) GoTo(MenuPage.Skirmish);
            y += S(56) + gap;

            DrawComingSoonButton(new Rect(x, y, w, S(46)), "MULTIPLAYER");
            y += S(46) + gap;
            DrawComingSoonButton(new Rect(x, y, w, S(46)), "REPLAYS");
            y += S(46) + gap;

            if (DrawSecondaryButton(new Rect(x, y, w, S(46)), "SETTINGS")) GoTo(MenuPage.Settings);
            y += S(46) + gap;
            if (DrawSecondaryButton(new Rect(x, y, w, S(46)), "CREDITS")) GoTo(MenuPage.Credits);
            y += S(46) + gap;
            if (DrawSecondaryButton(new Rect(x, y, w, S(46)), "QUIT")) Application.Quit();
        }

        // ---- Skirmish setup ---------------------------------------------------------------

        private void DrawSkirmishPage()
        {
            DrawPageHeader("SKIRMISH SETUP");

            float pw = Mathf.Min(S(700), Screen.width - S(40));
            var panel = new Rect((Screen.width - pw) * 0.5f, S(80), pw, Screen.height - S(80) - S(20));
            GUI.Label(panel, GUIContent.none, _s.Panel);

            float pad = S(24);
            float x = panel.x + pad;
            float w = panel.width - pad * 2f;
            float y = panel.y + S(18);
            float labelW = S(MenuStyles.LabelW);
            float rowH = S(30);
            float rowGap = S(8);

            Rect Control(float cy) => new Rect(x + labelW, cy, w - labelW, rowH);
            void RowLabel(string t, float cy) =>
                GUI.Label(new Rect(x, cy, labelW, rowH), t, _s.SectionLabel);

            // Map.
            RowLabel("Map", y);
            int newMap = DrawChips(Control(y), SkirmishSetupState.MapNames, _setup.MapIx);
            if (newMap != _setup.MapIx) _setup.SelectMap(newMap);
            y += rowH + S(2);
            GUI.Label(new Rect(x + labelW, y, w - labelW, S(18)), _setup.MapBlurb, _s.Dim);
            y += S(18) + rowGap;

            // Player count (choices are pre-filtered to the map's spawn count).
            RowLabel("Players", y);
            int newP = DrawChips(Control(y), _setup.PlayerChoiceLabels, _setup.PlayerChoiceIx);
            if (newP != _setup.PlayerChoiceIx) _setup.SelectPlayerChoice(newP);
            y += rowH + rowGap;

            // Opponent.
            RowLabel("Opponent", y);
            int botIx = DrawChips(Control(y), OpponentLabels, _setup.Bots ? 0 : 1);
            _setup.Bots = botIx == 0;
            y += rowH + S(2);
            GUI.Label(new Rect(x + labelW, y, w - labelW, S(18)), _setup.OpponentBlurb, _s.Dim);
            y += S(18) + rowGap;

            // Fog of war.
            RowLabel("Fog of War", y);
            int fogIx = DrawChips(Control(y), OnOffLabels, _setup.Fog ? 0 : 1);
            _setup.Fog = fogIx == 0;
            y += rowH + rowGap;

            // Game speed (client pacing only; the sim stays deterministic).
            RowLabel("Game Speed", y);
            int spIx = DrawChips(Control(y), SkirmishSetupState.GameSpeedLabels,
                NearestIx(SkirmishSetupState.GameSpeedSteps, MatchBootstrap.GameSpeed));
            SetGameSpeed(SkirmishSetupState.GameSpeedSteps[spIx]);
            y += rowH + rowGap;

            // Seed.
            RowLabel("Seed", y);
            string seed = GUI.TextField(new Rect(x + labelW, y, w - labelW - S(110), rowH),
                _setup.SeedText, 20, _s.Field);
            if (seed != _setup.SeedText)
            {
                _setup.SeedText = seed;
                _setup.Revalidate();
            }
            if (GUI.Button(new Rect(x + w - S(100), y, S(100), rowH), "Random", _s.Chip))
                _setup.RandomizeSeed();
            y += rowH + rowGap;

            // Teams: presets, then one cycling chip per seat (same team = allies).
            RowLabel("Teams", y);
            if (GUI.Button(new Rect(x + labelW, y, S(130), rowH), "Free-for-all",
                    _setup.IsFreeForAll ? _s.ChipOn : _s.Chip))
                _setup.ApplyFreeForAll();
            if (GUI.Button(new Rect(x + labelW + S(136), y, S(130), rowH), "Two teams",
                    _setup.IsTwoTeamSplit ? _s.ChipOn : _s.Chip))
                _setup.ApplyTwoTeams();
            y += rowH + S(6);

            int seats = _setup.Players;
            int cols = seats <= 8 ? 2 : 4;
            float cellH = seats <= 8 ? S(26) : S(21);
            float cellGap = S(4);
            float cellW = (w - labelW - cellGap * (cols - 1)) / cols;
            for (int p = 0; p < seats; p++)
            {
                var cell = new Rect(x + labelW + (p % cols) * (cellW + cellGap),
                    y + (p / cols) * (cellH + S(3)), cellW, cellH);
                if (GUI.Button(cell, _setup.SeatLabels[p], _s.SeatChip)) _setup.CycleSeatTeam(p);
            }
            y += Mathf.Ceil(seats / (float)cols) * (cellH + S(3));

            // Action row pinned to the panel bottom; errors sit beside the buttons so they
            // never collide with a tall 32-seat team grid.
            float actionH = S(48);
            float ay = panel.yMax - S(18) - actionH;
            if (DrawSecondaryButton(new Rect(x, ay, S(130), actionH), "BACK")) GoTo(MenuPage.Main);
            var startRect = new Rect(x + w - S(270), ay, S(270), actionH);
            if (_setup.IsValid)
            {
                if (GUI.Button(startRect, "START MATCH", _s.Start)) StartSkirmish();
            }
            else
            {
                GUI.Label(startRect, "START MATCH", _s.StartOff);
                GUI.Label(new Rect(x + S(140), ay - S(4), w - S(140) - S(280), actionH + S(8)),
                    _setup.ErrorText, _s.Error);
            }
        }

        // ---- Settings -------------------------------------------------------------------------

        private void DrawSettingsPage()
        {
            DrawPageHeader("SETTINGS");

            float pw = Mathf.Min(S(600), Screen.width - S(40));
            float ph = S(330);
            var panel = new Rect((Screen.width - pw) * 0.5f, S(96), pw, ph);
            GUI.Label(panel, GUIContent.none, _s.Panel);

            float pad = S(24);
            float x = panel.x + pad;
            float w = panel.width - pad * 2f;
            float y = panel.y + S(20);
            float labelW = S(170);
            float rowH = S(30);
            float rowGap = S(12);

            Rect Control(float cy) => new Rect(x + labelW, cy, w - labelW, rowH);
            void RowLabel(string t, float cy) =>
                GUI.Label(new Rect(x, cy, labelW, rowH), t, _s.SectionLabel);

            RowLabel("Game speed", y);
            int spIx = DrawChips(Control(y), SkirmishSetupState.GameSpeedLabels,
                NearestIx(SkirmishSetupState.GameSpeedSteps, MatchBootstrap.GameSpeed));
            SetGameSpeed(SkirmishSetupState.GameSpeedSteps[spIx]);
            y += rowH + rowGap;

            RowLabel("Fog of war default", y);
            int fogIx = DrawChips(Control(y), OnOffLabels, MatchBootstrap.PendingFog ? 0 : 1);
            bool fog = fogIx == 0;
            if (fog != MatchBootstrap.PendingFog)
            {
                MatchBootstrap.PendingFog = fog;
                PlayerPrefs.SetInt(PrefFogDefault, fog ? 1 : 0);
            }
            y += rowH + rowGap;

            RowLabel("Camera pan speed", y);
            int panIx = DrawChips(Control(y), MultLabels, NearestIx(MultSteps, CameraRig.PanSpeedMult));
            if (!Mathf.Approximately(MultSteps[panIx], CameraRig.PanSpeedMult))
            {
                CameraRig.PanSpeedMult = MultSteps[panIx];
                PlayerPrefs.SetFloat(PrefPanSpeed, CameraRig.PanSpeedMult);
            }
            y += rowH + rowGap;

            RowLabel("Zoom sensitivity", y);
            int zoomIx = DrawChips(Control(y), MultLabels, NearestIx(MultSteps, CameraRig.ZoomSpeedMult));
            if (!Mathf.Approximately(MultSteps[zoomIx], CameraRig.ZoomSpeedMult))
            {
                CameraRig.ZoomSpeedMult = MultSteps[zoomIx];
                PlayerPrefs.SetFloat(PrefZoomSpeed, CameraRig.ZoomSpeedMult);
            }
            y += rowH + rowGap;

            GUI.Label(new Rect(x, y, w, S(20)),
                "Game speed paces real time only — the simulation stays deterministic.", _s.Dim);

            float actionH = S(42);
            float ay = panel.yMax - S(18) - actionH;
            if (DrawSecondaryButton(new Rect(x, ay, S(130), actionH), "BACK")) GoTo(MenuPage.Main);
            if (DrawSecondaryButton(new Rect(x + w - S(200), ay, S(200), actionH), "RESET TO DEFAULTS"))
                ResetSettings();
        }

        private static void SetGameSpeed(float v)
        {
            if (Mathf.Approximately(v, MatchBootstrap.GameSpeed)) return;
            MatchBootstrap.GameSpeed = v;
            PlayerPrefs.SetFloat(PrefGameSpeed, v);
        }

        private static void ResetSettings()
        {
            MatchBootstrap.GameSpeed = 1f;
            MatchBootstrap.PendingFog = true;
            CameraRig.PanSpeedMult = 1f;
            CameraRig.ZoomSpeedMult = 1f;
            PlayerPrefs.SetFloat(PrefGameSpeed, 1f);
            PlayerPrefs.SetInt(PrefFogDefault, 1);
            PlayerPrefs.SetFloat(PrefPanSpeed, 1f);
            PlayerPrefs.SetFloat(PrefZoomSpeed, 1f);
        }

        // ---- Credits ------------------------------------------------------------------------------

        private void DrawCreditsPage()
        {
            DrawPageHeader("CREDITS");

            float pw = Mathf.Min(S(520), Screen.width - S(40));
            float ph = S(280);
            var panel = new Rect((Screen.width - pw) * 0.5f, S(110), pw, ph);
            GUI.Label(panel, GUIContent.none, _s.Panel);

            GUI.Label(new Rect(panel.x, panel.y + S(36), panel.width, S(44)), "PETRI", _s.PageTitle);
            GUI.Label(new Rect(panel.x, panel.y + S(92), panel.width, S(26)),
                "Created by Christopher Godines Hernández", _s.Body);
            GUI.Label(new Rect(panel.x, panel.y + S(124), panel.width, S(22)),
                "Deterministic microbial RTS", _s.Tagline);

            float bw = S(130);
            if (DrawSecondaryButton(new Rect(panel.x + (panel.width - bw) * 0.5f,
                    panel.yMax - S(60), bw, S(42)), "BACK"))
                GoTo(MenuPage.Main);
        }

        // ---- Match launch ---------------------------------------------------------------------------

        private void StartSkirmish()
        {
            if (!_setup.Validate()) return; // re-check right before launch
            MatchBootstrap.PendingSeed = _setup.ParsedSeed;
            MatchBootstrap.PendingMap = _setup.MapId;
            _visible = false;
            new GameObject("PetriMatch").AddComponent<MatchBootstrap>();
        }
    }
}
