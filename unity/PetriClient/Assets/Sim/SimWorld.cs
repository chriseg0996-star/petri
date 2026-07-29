namespace Petri.Core
{
    public enum EntityKind : byte
    {
        None = 0,
        Unit = 1,     // retired kind: no unit entities exist post-conversion (value reserved)
        Building = 2,
        Node = 3,     // neutral resource node
    }

    public sealed class PlayerState
    {
        public bool Alive;
        public byte Team;       // players sharing a team are allies; different teams are enemies
        public long Food;
        public long Minerals;   // secondary resource, harvested from mineral nodes
        public long EvoPoints;  // third resource — earned ONLY by front-combat kills
        public int[] ProductionWeights = System.Array.Empty<int>(); // per unit dense index
        public byte[] UpgradeLevels = System.Array.Empty<byte>();   // retained data; systems deferred

        // ---- SUPERORGANISM state (all hashed; zeroed on elimination).
        public int WorkerCount;            // workers are a count, not entities
        public byte FrontCount;            // K, one of SimConstants.FrontCounts
        public int OrganismHealth;         // the organism's shared health pool
        public int[] Force = System.Array.Empty<int>();          // [MaxFronts * unitDefCount]
        public int[] FrontDamage = System.Array.Empty<int>();    // [MaxFronts] accumulated damage
        public int[] FrontBrokenTicks = System.Array.Empty<int>(); // [MaxFronts] breakthrough window
        public int[] FrontPushX = System.Array.Empty<int>();     // [MaxFronts] push target centi, -1 none
        public int[] FrontPushY = System.Array.Empty<int>();
    }

    /// <summary>
    /// The entire mutable match state. Everything here (except the Scratch* buffers and the
    /// static terrain) is hashed by Simulation.StateHash and MUST be re-initialized in Spawn
    /// or canonicalized by Eliminate — un-reset state desyncs lockstep peers.
    /// </summary>
    public sealed class SimWorld
    {
        public const byte NeutralOwner = 255;

        public readonly Rules Rules;
        public int TickCount;
        public Pcg32 Rng;
        public int RejectedCommands;
        public Fix MapWidth;
        public Fix MapHeight;
        public readonly int UnitDefCount;

        public readonly PlayerState[] Players;

        // Entities: buildings and resource nodes only (units are per-front counts).
        public readonly EntityKind[] Kind;
        public readonly short[] DefIndex;
        public readonly byte[] Owner;
        public readonly FixVec2[] Pos;
        public readonly int[] Hp;
        public readonly int[] ProduceProgress;  // building: ticks into current production
        public readonly short[] ProduceChoice;  // building: unit dense index in production, -1 idle
        public readonly short[] ProduceOverride; // building: player-forced unit choice, -1 = auto (weights)
        public readonly bool[] ProducePaused;   // building: production halted by the player
        public readonly int[] ConstructionRemaining; // building: work units left, 0 = operational
        public readonly int[] NodeFood;         // node: resource amount remaining (food OR minerals)
        public readonly bool[] NodeMineral;     // node: true = yields minerals, false = nutrients
        public readonly short[] RallyFront;     // building: front its produced units join, -1 = auto
        public readonly int[] Generation;       // per-slot version, bumped each Spawn — (index,gen)
                                                //   is a stable identity the UI uses
        public int HighWater;

        // Derived per-tick scratch (NOT hashed, never carries state across ticks).
        public readonly int[] ScratchUnitCounts;
        public readonly bool[] ScratchFrontContested; // [player * MaxFronts]: sector borders an enemy this beat
        public byte[] ScratchCellSector = System.Array.Empty<byte>(); // owned cell → sector under its owner
        public readonly long[] ScratchCentSumX, ScratchCentSumY; // per-player centroid accumulation
        public readonly int[] ScratchCentCount;
        public readonly int[] ScratchCentXCenti, ScratchCentYCenti; // per-player centroid (centi)
        // FRONT COMBAT scratch, rebuilt each combat beat. Contacts are the distinct enemy
        // fronts a front touches, packed (enemyPlayer << 8) | enemyFront, capped per front.
        public const int ContactCap = 16;
        public readonly short[] ScratchContact;       // [player * MaxFronts * ContactCap]
        public readonly byte[] ScratchContactCount;   // [player * MaxFronts]
        public readonly int[] ScratchFrontMelee;      // frozen pre-beat stats [player * MaxFronts]
        public readonly int[] ScratchFrontRanged;
        public readonly int[] ScratchFrontHold;

        // Immovable terrain from the map (walls/rocks). Static for the whole match and
        // identical on every peer (map data, covered by DefsHash) — deliberately NOT hashed
        // and never mutated. Territory can never claim blocked cells; buildings refuse to
        // stand on walls.
        public FixVec2[] WallPos = System.Array.Empty<FixVec2>();
        public Fix[] WallRadius = System.Array.Empty<Fix>();

        // ---- TERRITORY: the superorganism map. One cell = 2 world units (200 centi).
        // Territory[c] = owning player, 255 = neutral — HASHED per cell (the game state IS
        // the territory). TerritoryBlocked derives from walls at setup: static, unhashed
        // (map data, covered by DefsHash); blocked cells can never be owned.
        public const int CellCenti = 200;
        public byte[] Territory = System.Array.Empty<byte>();
        public bool[] TerritoryBlocked = System.Array.Empty<bool>();
        public int TerritoryCellsX, TerritoryCellsY;
        public int OwnableCellCount;

        public int CellCount => TerritoryCellsX * TerritoryCellsY;

        public int CellOfCenti(int xCenti, int yCenti)
        {
            int cx = xCenti / CellCenti, cy = yCenti / CellCenti;
            if (cx < 0) cx = 0; else if (cx >= TerritoryCellsX) cx = TerritoryCellsX - 1;
            if (cy < 0) cy = 0; else if (cy >= TerritoryCellsY) cy = TerritoryCellsY - 1;
            return cy * TerritoryCellsX + cx;
        }

        public int CellOfPos(FixVec2 p) =>
            CellOfCenti((int)((long)p.X.Raw * 100 / Fix.OneRaw), (int)((long)p.Y.Raw * 100 / Fix.OneRaw));

        public void CellCenterCenti(int c, out int xCenti, out int yCenti)
        {
            int cx = c % TerritoryCellsX, cy = c / TerritoryCellsX;
            xCenti = cx * CellCenti + CellCenti / 2;
            yCenti = cy * CellCenti + CellCenti / 2;
        }

        public int Capacity => Kind.Length;

        public SimWorld(Rules rules, int playerCount, int unitDefCount, int upgradeCount, Fix mapWidth, Fix mapHeight, ulong seed)
        {
            Rules = rules;
            MapWidth = mapWidth;
            MapHeight = mapHeight;
            UnitDefCount = unitDefCount;
            Rng = new Pcg32(seed, 0x5EEDCAFE);
            int cap = rules.MaxEntities;
            Kind = new EntityKind[cap];
            DefIndex = new short[cap];
            Owner = new byte[cap];
            Pos = new FixVec2[cap];
            Hp = new int[cap];
            ProduceProgress = new int[cap];
            ProduceChoice = new short[cap];
            ProduceOverride = new short[cap];
            ProducePaused = new bool[cap];
            ConstructionRemaining = new int[cap];
            NodeFood = new int[cap];
            NodeMineral = new bool[cap];
            RallyFront = new short[cap];
            Generation = new int[cap];
            ScratchUnitCounts = new int[playerCount * unitDefCount];
            ScratchFrontContested = new bool[playerCount * SimConstants.MaxFronts];
            ScratchCentSumX = new long[playerCount];
            ScratchCentSumY = new long[playerCount];
            ScratchCentCount = new int[playerCount];
            ScratchCentXCenti = new int[playerCount];
            ScratchCentYCenti = new int[playerCount];
            ScratchContact = new short[playerCount * SimConstants.MaxFronts * ContactCap];
            ScratchContactCount = new byte[playerCount * SimConstants.MaxFronts];
            ScratchFrontMelee = new int[playerCount * SimConstants.MaxFronts];
            ScratchFrontRanged = new int[playerCount * SimConstants.MaxFronts];
            ScratchFrontHold = new int[playerCount * SimConstants.MaxFronts];

            Players = new PlayerState[playerCount];
            for (int p = 0; p < playerCount; p++)
            {
                Players[p] = new PlayerState
                {
                    Alive = true,
                    Team = (byte)p, // default: everyone on their own team (free-for-all)
                    ProductionWeights = new int[unitDefCount],
                    UpgradeLevels = new byte[upgradeCount],
                    FrontCount = (byte)rules.DefaultFrontCount,
                    Force = new int[SimConstants.MaxFronts * unitDefCount],
                    FrontDamage = new int[SimConstants.MaxFronts],
                    FrontBrokenTicks = new int[SimConstants.MaxFronts],
                    FrontPushX = new int[SimConstants.MaxFronts],
                    FrontPushY = new int[SimConstants.MaxFronts],
                };
                for (int f = 0; f < SimConstants.MaxFronts; f++)
                {
                    Players[p].FrontPushX[f] = -1;
                    Players[p].FrontPushY[f] = -1;
                }
            }
        }

        /// <summary>Lowest-free-index spawn; resets EVERY per-entity field (iron rule).</summary>
        public int Spawn(EntityKind kind, short defIndex, byte owner, FixVec2 pos, int hp)
        {
            for (int i = 0; i < Kind.Length; i++)
            {
                if (Kind[i] != EntityKind.None) continue;
                Kind[i] = kind;
                DefIndex[i] = defIndex;
                Owner[i] = owner;
                Pos[i] = pos;
                Hp[i] = hp;
                ProduceProgress[i] = 0;
                ProduceChoice[i] = -1;
                ProduceOverride[i] = -1;
                ProducePaused[i] = false;
                ConstructionRemaining[i] = 0;
                NodeFood[i] = 0;
                NodeMineral[i] = false;
                RallyFront[i] = -1;
                Generation[i]++; // new occupant of this slot — never reset, only advances
                if (i >= HighWater) HighWater = i + 1;
                return i;
            }
            return -1; // world full — callers must treat as a no-op, never throw mid-tick
        }

        public void Despawn(int i) => Kind[i] = EntityKind.None;

        /// <summary>
        /// Remove a player from the game with a CANONICAL post-elimination state: every
        /// owned entity despawns, every owned cell reverts to neutral, and the whole
        /// superorganism block zeroes — identical on every peer regardless of how the
        /// elimination happened (health, nucleus loss, or territory victory).
        /// </summary>
        public void Eliminate(byte p)
        {
            var pl = Players[p];
            pl.Alive = false;
            for (int i = 0; i < HighWater; i++)
                if (Kind[i] != EntityKind.None && Kind[i] != EntityKind.Node && Owner[i] == p)
                    Despawn(i);
            for (int c = 0; c < Territory.Length; c++)
                if (Territory[c] == p) Territory[c] = NeutralOwner;
            pl.WorkerCount = 0;
            pl.OrganismHealth = 0;
            for (int k = 0; k < pl.Force.Length; k++) pl.Force[k] = 0;
            for (int f = 0; f < SimConstants.MaxFronts; f++)
            {
                pl.FrontDamage[f] = 0;
                pl.FrontBrokenTicks[f] = 0;
                pl.FrontPushX[f] = -1;
                pl.FrontPushY[f] = -1;
            }
        }

        /// <summary>
        /// THE hostility rule: two owners are enemies only when both are real players on
        /// DIFFERENT teams. Same player, same team, or anything neutral (resource nodes) is
        /// never a target. Every targeting decision in the sim routes through this.
        /// </summary>
        public bool AreEnemies(byte a, byte b)
        {
            if (a == NeutralOwner || b == NeutralOwner || a == b) return false;
            if (a >= Players.Length || b >= Players.Length) return false;
            return Players[a].Team != Players[b].Team;
        }

        /// <summary>Friendly to the given player: itself or an ally (never neutral).</summary>
        public bool IsFriendly(byte self, byte other) =>
            other != NeutralOwner && self != NeutralOwner && !AreEnemies(self, other);

        public FixVec2 ClampToMap(FixVec2 p) =>
            new FixVec2(Fix.Clamp(p.X, Fix.Zero, MapWidth), Fix.Clamp(p.Y, Fix.Zero, MapHeight));
    }
}
