using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Petri.Client
{
    /// <summary>
    /// Skirmish configuration and validation, kept apart from drawing. The authoritative values
    /// live in MatchBootstrap's static Pending* fields (the contract the match reads at Start);
    /// this class adds only what the menu needs on top: the map catalogue, the raw seed text
    /// being edited, cached chip/seat labels (so drawing allocates nothing per frame), and the
    /// validation error list. Validation runs on mutation and on Start — never per frame,
    /// because the map check touches the filesystem.
    /// </summary>
    internal sealed class SkirmishSetupState
    {
        public static readonly string[] MapIds = { "petri-dish", "capillary", "agar-plate" };
        public static readonly string[] MapNames = { "Petri Dish", "Capillary", "Agar Plate" };
        private static readonly string[] MapBlurbs =
        {
            "Square arena · seats up to 8 players",
            "Long corridor · seats 2 players",
            "Large ring · seats up to 32 players",
        };
        public static readonly int[] MapMaxPlayers = { 8, 2, 32 }; // must match each map's spawn count

        public const float MinGameSpeed = 0.5f;
        public const float MaxGameSpeed = 4f;
        public static readonly float[] GameSpeedSteps = { 0.5f, 1f, 1.5f, 2f, 3f, 4f };
        public static readonly string[] GameSpeedLabels = { "×0.5", "×1", "×1.5", "×2", "×3", "×4" };

        // Stepping 2..32 one seat at a time would be 30 clicks; offer the useful sizes.
        private static readonly int[] PlayerSteps = { 2, 3, 4, 6, 8, 12, 16, 24, 32 };

        public int MapIx { get; private set; }
        public string MapId => MapIds[MapIx];
        public string MapBlurb => MapBlurbs[MapIx];

        public string SeedText;
        public ulong ParsedSeed { get; private set; } = 7;

        public string[] PlayerChoiceLabels { get; private set; }
        public int PlayerChoiceIx { get; private set; }
        private int[] _playerChoices;

        public string[] SeatLabels { get; private set; }
        public bool IsValid { get; private set; } = true;
        public string ErrorText { get; private set; } = "";

        private readonly List<string> _errors = new List<string>();
        private readonly StringBuilder _sb = new StringBuilder(128);

        public SkirmishSetupState()
        {
            SeedText = MatchBootstrap.PendingSeed.ToString();
            MapIx = System.Array.IndexOf(MapIds, MatchBootstrap.PendingMap);
            if (MapIx < 0) MapIx = 0;
            RefreshPlayerChoices();
            RefreshSeatLabels();
            Validate();
        }

        public int Players => MatchBootstrap.PendingPlayers;

        public bool Bots
        {
            get => MatchBootstrap.PendingBot;
            set => MatchBootstrap.PendingBot = value;
        }

        public bool Fog
        {
            get => MatchBootstrap.PendingFog;
            set => MatchBootstrap.PendingFog = value;
        }

        public string OpponentBlurb => !Bots
            ? "Rival strains grow and defend but have no general — perfect for drilling your swarms."
            : Players > 2
                ? "Every strain for itself: each rival feeds, divides, and attacks whoever it finds first."
                : "The rival strain feeds, divides, and throws attack waves at your colony — hold the line.";

        public void SelectMap(int ix)
        {
            MapIx = Mathf.Clamp(ix, 0, MapIds.Length - 1);
            // A map can't seat more players than it has spawns.
            MatchBootstrap.PendingPlayers =
                Mathf.Min(MatchBootstrap.PendingPlayers, MapMaxPlayers[MapIx]);
            RefreshPlayerChoices();
            RefreshSeatLabels();
            Validate();
        }

        public void SelectPlayerChoice(int ix)
        {
            if (ix < 0 || ix >= _playerChoices.Length) return;
            PlayerChoiceIx = ix;
            MatchBootstrap.PendingPlayers = _playerChoices[ix];
            RefreshSeatLabels();
            Validate();
        }

        public void CycleSeatTeam(int seat)
        {
            if (seat < 0 || seat >= Players) return;
            MatchBootstrap.PendingTeams[seat] = (MatchBootstrap.PendingTeams[seat] + 1) % Players;
            RefreshSeatLabels();
            Validate();
        }

        public void ApplyFreeForAll()
        {
            for (int p = 0; p < MatchBootstrap.MaxPlayers; p++) MatchBootstrap.PendingTeams[p] = p;
            RefreshSeatLabels();
            Validate();
        }

        public void ApplyTwoTeams()
        {
            for (int p = 0; p < MatchBootstrap.MaxPlayers; p++)
                MatchBootstrap.PendingTeams[p] = p < Players / 2 ? 0 : 1;
            RefreshSeatLabels();
            Validate();
        }

        /// <summary>True when every seated player is on their own team.</summary>
        public bool IsFreeForAll
        {
            get
            {
                long mask = 0; // team ids are < MaxPlayers (32), so a bitmask covers them
                for (int p = 0; p < Players; p++)
                {
                    long bit = 1L << MatchBootstrap.PendingTeams[p];
                    if ((mask & bit) != 0) return false;
                    mask |= bit;
                }
                return true;
            }
        }

        /// <summary>True when the seats match the half-and-half two-team preset.</summary>
        public bool IsTwoTeamSplit
        {
            get
            {
                for (int p = 0; p < Players; p++)
                    if (MatchBootstrap.PendingTeams[p] != (p < Players / 2 ? 0 : 1)) return false;
                return true;
            }
        }

        public void RandomizeSeed()
        {
            SeedText = ((uint)Random.Range(1, int.MaxValue)).ToString();
            Validate();
        }

        public void Revalidate() => Validate();

        public bool Validate()
        {
            _errors.Clear();

            if (!ulong.TryParse(SeedText, out ulong seed) || seed == 0)
                _errors.Add("Seed must be a whole number of 1 or more.");
            else
                ParsedSeed = seed;

            if (!File.Exists(MapPath(MapIds[MapIx])))
                _errors.Add("Map data not found: " + MapIds[MapIx] + ".json");

            int maxP = MapMaxPlayers[MapIx];
            if (Players < 2)
                _errors.Add("At least two players are required.");
            else if (Players > maxP)
                _errors.Add(MapNames[MapIx] + " seats at most " + maxP + " players.");

            bool twoSided = false;
            for (int p = 1; p < Players && !twoSided; p++)
                if (MatchBootstrap.PendingTeams[p] != MatchBootstrap.PendingTeams[0]) twoSided = true;
            if (!twoSided)
                _errors.Add("All seats are on one team — assign at least two different teams.");

            MatchBootstrap.GameSpeed =
                Mathf.Clamp(MatchBootstrap.GameSpeed, MinGameSpeed, MaxGameSpeed);

            IsValid = _errors.Count == 0;
            _sb.Length = 0;
            for (int i = 0; i < _errors.Count; i++)
            {
                if (i > 0) _sb.Append('\n');
                _sb.Append(_errors[i]);
            }
            ErrorText = _sb.ToString();
            return IsValid;
        }

        private void RefreshPlayerChoices()
        {
            int maxP = Mathf.Min(MapMaxPlayers[MapIx], MatchBootstrap.MaxPlayers);
            int n = 0;
            for (int i = 0; i < PlayerSteps.Length; i++)
                if (PlayerSteps[i] <= maxP) n++;
            _playerChoices = new int[n];
            PlayerChoiceLabels = new string[n];
            for (int i = 0; i < n; i++)
            {
                _playerChoices[i] = PlayerSteps[i];
                PlayerChoiceLabels[i] = PlayerSteps[i].ToString();
            }
            // Snap the current count down to the nearest available choice.
            int cur = Mathf.Clamp(MatchBootstrap.PendingPlayers, 2, maxP);
            PlayerChoiceIx = 0;
            for (int i = 0; i < n; i++)
                if (_playerChoices[i] <= cur) PlayerChoiceIx = i;
            MatchBootstrap.PendingPlayers = _playerChoices[PlayerChoiceIx];
        }

        private void RefreshSeatLabels()
        {
            int seats = Players;
            if (SeatLabels == null || SeatLabels.Length != seats) SeatLabels = new string[seats];
            for (int p = 0; p < seats; p++)
            {
                _sb.Length = 0;
                if (p == 0) _sb.Append("You");
                else _sb.Append('P').Append(p);
                _sb.Append(" · T").Append(MatchBootstrap.PendingTeams[p] + 1);
                SeatLabels[p] = _sb.ToString();
            }
        }

        private static string MapPath(string id) =>
            Path.Combine(UnityDataLoader.DataDir, "maps", id + ".json");
    }
}
