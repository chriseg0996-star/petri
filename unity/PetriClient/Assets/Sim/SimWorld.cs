namespace Petri.Core
{
    public enum EntityKind : byte
    {
        None = 0,
        Unit = 1,
        Building = 2,
        Node = 3, // neutral resource node
    }

    /// <summary>A landed hit this tick (attacker → target). Transient view-feed: cleared at
    /// the start of every tick, never hashed — a pure function of the tick, identical on all
    /// peers, consumed by the client to spawn projectile/impact effects.</summary>
    public struct AttackEvent
    {
        public int Attacker;
        public int Target;
    }

    public sealed class PlayerState
    {
        public bool Alive;
        public byte Team;       // players sharing a team are allies; different teams are enemies
        public long Food;
        public long Minerals;   // secondary resource, mined from mineral nodes; spent on prongs
        public long EvoPoints;  // third resource — earned ONLY by killing enemy units, never mined
        public int[] ProductionWeights = System.Array.Empty<int>(); // per unit dense index
        public int SupplyPriority = 50; // 0 = all workers keep the core; 100 = max caravans to the front
        public byte[] UpgradeLevels = System.Array.Empty<byte>();   // per upgrade dense index, 0/1
    }

    /// <summary>
    /// The entire mutable match state, structure-of-arrays, fixed capacity. Everything here
    /// (except the Scratch* buffers) is hashed by Simulation.StateHash and MUST be
    /// re-initialized in Spawn — un-reset state leaks across entity-index reuse and
    /// desyncs lockstep peers.
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

        public readonly EntityKind[] Kind;
        public readonly short[] DefIndex;
        public readonly byte[] Owner;
        public readonly FixVec2[] Pos;
        public readonly int[] Hp;
        public readonly FixVec2[] MoveTarget;
        public readonly bool[] HasMoveOrder;
        public readonly bool[] AttackMove;      // unit: move to MoveTarget but divert to engage enemies
        public readonly byte[] QueueCount;      // unit: shift-queued orders waiting behind the active one
        public readonly byte[] QueueKind;       // [entity * MaxOrderQueue + q]: SimConstants.Order*
        public readonly FixVec2[] QueuePos;     // [entity * MaxOrderQueue + q]: queued destination
        public readonly int[] AttackCooldown;
        public readonly int[] Carry;            // worker: food carried
        public readonly int[] GatherTimer;      // worker: ticks into current gather cycle
        public readonly int[] WorkNode;         // worker: entity index of assigned node, -1 none
        public readonly int[] ProduceProgress;  // building: ticks into current production
        public readonly short[] ProduceChoice;  // building: unit dense index in production, -1 idle
        public readonly short[] ProduceOverride; // building: player-forced unit choice, -1 = auto (weights)
        public readonly bool[] ProducePaused;   // building: production halted by the player
        public readonly bool[] HasRally;        // building: produced units go to the rally point
        public readonly FixVec2[] RallyPoint;   // building: rally target (valid when HasRally)
        public readonly int[] ConstructionRemaining; // building: worker-ticks left, 0 = operational
        public readonly int[] BuildTask;        // worker: entity index of construction site, -1 none
        public readonly int[] NodeFood;         // node: resource amount remaining (food OR minerals)
        public readonly bool[] NodeMineral;     // node: true = yields minerals, false = nutrients
        public readonly bool[] CarryMineral;    // worker: the carried load is minerals (routes deposit)
        public readonly int[] SupplyTicks;      // unit: grace reservoir; refills inside friendly
                                                //   supply, drains outside; 0 = unsupplied debuff
        public readonly byte[] Tier;            // supply cache: upgrade level (0 = base) — doubles
                                                //   stock/HP, halves drain, >= 1 arms the cache
        public readonly int[] DepotStock;       // supply building: food on hand; dry = supplies nothing
        public readonly int[] CaravanCache;     // worker: cache it's hauling food to, -1 none
        public readonly byte[] Dial;            // per-entity 0..100 tuning dial (UI slider); reserved
                                                //   for future per-entity modifiers, hashed already
        public readonly FixVec2[] Facing;       // unit body facing (directional damage reads it)
        public readonly int[] Generation;       // per-slot version, bumped each Spawn — (index,gen)
                                                //   is a stable identity the UI uses for control groups
        public int HighWater;

        // Transient per-tick view feed (NOT hashed; cleared each tick by Simulation.Tick).
        public readonly System.Collections.Generic.List<AttackEvent> AttackEvents =
            new System.Collections.Generic.List<AttackEvent>(64);

        // Spatial hash grid (derived, rebuilt within each tick; NOT hashed): 4-unit cells,
        // intrusive linked lists via GridHead/GridNext. Powers collision and combat range
        // queries — the difference between O(n²) and O(n·k) on the road to 8k units.
        public const int GridShift = Fix.FracBits + 2; // 4-unit cells (power of two)
        public readonly int GridCellsX;
        public readonly int GridCellsY;
        public readonly int[] GridHead;
        public readonly int[] GridNext;
        public Fix MaxInteractRadius = Fix.FromInt(2); // largest entity radius (set by MatchSetup)

        // Derived per-tick scratch (NOT hashed, never carries state across ticks).
        public readonly int[] ScratchUnitCounts;
        public readonly bool[] ScratchConnected; // per-building: linked to an HQ this tick
        public readonly int[] ScratchBuilders;  // per-site: workers in build reach this tick
        // Per-tick index lists (ascending, so scans keep their lowest-index tie-breaks) that
        // keep hot loops off the full entity range — the difference between O(units × entities)
        // and O(units × depots) as armies grow.
        public readonly int[] ScratchDepots;    // connected supply buildings
        public readonly int[] ScratchNodes;     // live resource nodes
        public readonly int[] ScratchDropoffs;  // finished HQs + hub prongs (any owner)
        public readonly int[] ScratchWorkers;   // worker units (any owner)
        public readonly int[] ScratchCaches;    // finished non-HQ supply buildings
        // Per-player flat attack bonus from standing AttackBonus structures. Derived: recomputed
        // every combat tick from hashed building state, so it stays out of StateHash.
        public readonly int[] ScratchAttackBonus;
        public int ScratchNodeCount, ScratchDropoffCount, ScratchWorkerCount, ScratchCacheCount;
        public readonly int[] ScratchQueue;     // BFS queue for SupplySystem; leader list for LeaderAuraSystem
        public readonly bool[] ScratchLeaderAura; // unit: inside a friendly leader's aura this tick

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
            MoveTarget = new FixVec2[cap];
            HasMoveOrder = new bool[cap];
            AttackMove = new bool[cap];
            QueueCount = new byte[cap];
            QueueKind = new byte[cap * SimConstants.MaxOrderQueue];
            QueuePos = new FixVec2[cap * SimConstants.MaxOrderQueue];
            AttackCooldown = new int[cap];
            Carry = new int[cap];
            GatherTimer = new int[cap];
            WorkNode = new int[cap];
            ProduceProgress = new int[cap];
            ProduceChoice = new short[cap];
            ProduceOverride = new short[cap];
            ProducePaused = new bool[cap];
            HasRally = new bool[cap];
            RallyPoint = new FixVec2[cap];
            ConstructionRemaining = new int[cap];
            BuildTask = new int[cap];
            NodeFood = new int[cap];
            NodeMineral = new bool[cap];
            CarryMineral = new bool[cap];
            SupplyTicks = new int[cap];
            Tier = new byte[cap];
            DepotStock = new int[cap];
            CaravanCache = new int[cap];
            Dial = new byte[cap];
            Facing = new FixVec2[cap];
            Generation = new int[cap];
            ScratchUnitCounts = new int[playerCount * unitDefCount];
            GridCellsX = System.Math.Max(1, (int)(mapWidth.Raw >> GridShift) + 1);
            GridCellsY = System.Math.Max(1, (int)(mapHeight.Raw >> GridShift) + 1);
            GridHead = new int[GridCellsX * GridCellsY];
            GridNext = new int[cap];
            for (int c = 0; c < GridHead.Length; c++) GridHead[c] = -1; // empty until first rebuild
            for (int i = 0; i < cap; i++) GridNext[i] = -1;

            ScratchConnected = new bool[cap];
            ScratchBuilders = new int[cap];
            ScratchDepots = new int[cap];
            ScratchNodes = new int[cap];
            ScratchDropoffs = new int[cap];
            ScratchWorkers = new int[cap];
            ScratchCaches = new int[cap];
            ScratchAttackBonus = new int[playerCount];
            ScratchQueue = new int[cap];
            ScratchLeaderAura = new bool[cap];

            Players = new PlayerState[playerCount];
            for (int p = 0; p < playerCount; p++)
                Players[p] = new PlayerState
                {
                    Alive = true,
                    Team = (byte)p, // default: everyone on their own team (free-for-all)
                    ProductionWeights = new int[unitDefCount],
                    UpgradeLevels = new byte[upgradeCount],
                };
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
                MoveTarget[i] = pos;
                HasMoveOrder[i] = false;
                AttackMove[i] = false;
                QueueCount[i] = 0; // queue entries beyond the count are never read or hashed
                AttackCooldown[i] = 0;
                Carry[i] = 0;
                GatherTimer[i] = 0;
                WorkNode[i] = -1;
                ProduceProgress[i] = 0;
                ProduceChoice[i] = -1;
                ProduceOverride[i] = -1;
                ProducePaused[i] = false;
                HasRally[i] = false;
                RallyPoint[i] = pos;
                ConstructionRemaining[i] = 0;
                BuildTask[i] = -1;
                NodeFood[i] = 0;
                NodeMineral[i] = false;
                CarryMineral[i] = false;
                SupplyTicks[i] = Rules.SupplyGraceTicks; // fresh units start fully supplied
                Tier[i] = 0;
                DepotStock[i] = 0; // depots start empty (MatchSetup fills starting buildings)
                CaravanCache[i] = -1;
                Dial[i] = 50;
                Facing[i] = new FixVec2(Fix.One, Fix.Zero);
                Generation[i]++; // new occupant of this slot — never reset, only advances
                if (i >= HighWater) HighWater = i + 1;
                return i;
            }
            return -1; // world full — callers must treat as a no-op, never throw mid-tick
        }

        public void Despawn(int i) => Kind[i] = EntityKind.None;

        /// <summary>Re-bucket every live entity. Called by systems whose queries need fresh
        /// positions; deterministic (ascending-index insertion, fixed cell order).</summary>
        public void RebuildGrid()
        {
            for (int c = 0; c < GridHead.Length; c++) GridHead[c] = -1;
            for (int i = 0; i < HighWater; i++)
            {
                if (Kind[i] == EntityKind.None) continue;
                int cell = GridCellOf(Pos[i]);
                GridNext[i] = GridHead[cell];
                GridHead[cell] = i;
            }
        }

        public int GridCellOf(FixVec2 p) =>
            GridClampY((int)(p.Y.Raw >> GridShift)) * GridCellsX + GridClampX((int)(p.X.Raw >> GridShift));

        public int GridClampX(int cx) => cx < 0 ? 0 : (cx >= GridCellsX ? GridCellsX - 1 : cx);
        public int GridClampY(int cy) => cy < 0 ? 0 : (cy >= GridCellsY ? GridCellsY - 1 : cy);

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

        // Deterministic 8-direction ring used for spawn placement and zero-distance separation.
        private static readonly FixVec2[] Ring =
        {
            new FixVec2(Fix.One, Fix.Zero),
            new FixVec2(Fix.Ratio(7, 10), Fix.Ratio(7, 10)),
            new FixVec2(Fix.Zero, Fix.One),
            new FixVec2(Fix.Ratio(-7, 10), Fix.Ratio(7, 10)),
            new FixVec2(-Fix.One, Fix.Zero),
            new FixVec2(Fix.Ratio(-7, 10), Fix.Ratio(-7, 10)),
            new FixVec2(Fix.Zero, -Fix.One),
            new FixVec2(Fix.Ratio(7, 10), Fix.Ratio(-7, 10)),
        };

        public static FixVec2 RingDir(int index) => Ring[index & 7];
    }
}
