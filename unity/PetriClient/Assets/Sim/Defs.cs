using System;
using System.Collections.Generic;

namespace Petri.Core
{
    public static class SimConstants
    {
        public const int TicksPerSecond = 20;

        // Formation zones: where a unit type stands within its squad. Players assign each
        // unit def to a zone per squad (the placement matrix); layouts are built in code.
        public const int ZoneFront = 0;   // concentrated ranks ahead of the leader
        public const int ZoneRear = 1;    // ranks behind the leader
        public const int ZoneFlanks = 2;  // mirrored at both ends of the line
        public const int ZoneSpread = 3;  // interleaved evenly through front and rear
        public const int ZoneGuard = 4;   // ring around the leader
        public const int ZoneCount = 5;

        // Shift-queued orders: waypoints held per unit, started one by one as each order
        // completes. Kinds mirror the command that was queued.
        public const int MaxOrderQueue = 16;
        public const byte OrderMove = 1;
        public const byte OrderAttackMove = 2;
        public const byte OrderFormationMove = 3;
    }

    /// <summary>Global match rules, loaded from data/rules.json. Integers only.</summary>
    public sealed class Rules
    {
        public int MaxEntities = 512;
        public int StartingFood = 200;
        public int StartingWorkers = 6;
        public int NodeRadiusCenti = 60;
        public int MaxUnitsPerLeader = 15;
        public int MaxLeadersPerPlayer = 9;    // battalion + squad leaders combined
        public int MaxSquadsPerBattalion = 9;  // prime + limbs; single digits address every squad
        public int SwarmJoinRadiusCenti = 400;
        // Settled members knocked farther than this off their slot wake up and regroup
        // (deliberately separate from the join radius, which can be map-wide).
        public int RegroupRadiusCenti = 400;
        // Leaderless units move and attack at Num/Den speed (3/4 = -25%).
        public int LeaderlessPenaltyNum = 3;
        public int LeaderlessPenaltyDen = 4;
        // Units in a squad (under a living leader) deal Num/Den damage (5/4 = +25%)...
        public int SquadDamageBonusNum = 5;
        public int SquadDamageBonusDen = 4;
        // ...but only while within cohesion range of their leader (formation-keeping matters).
        public int SquadCohesionRadiusCenti = 600;
        // Directional combat: units hit from outside their front arc take extra damage.
        // Arc boundaries are cosines as rationals (1/2 = ±60° cones front and rear).
        public int FrontArcCosNum = 1;
        public int FrontArcCosDen = 2;
        public int RearArcCosNum = 1;
        public int RearArcCosDen = 2;
        public int SideDamageNum = 5;  // 5/4 = +25% from the flanks
        public int SideDamageDen = 4;
        public int RearDamageNum = 3;  // 3/2 = +50% from behind
        public int RearDamageDen = 2;
        // Station spacing between a prime leader and its linked sub-leaders (super-swarm limbs).
        public int LinkSpacingCenti = 500;
        // How far a squad scans for the enemy that enemy-anchored formations (Encircle) act on.
        public int EnemyAnchorRangeCenti = 1500;
        // Formation zone layout (all tunable data): row block offsets/widths, flank stations,
        // and the guard ring around the leader.
        public int ZoneFrontForwardCenti = 160;
        public int ZoneRearForwardCenti = -110;
        public int ZoneRowWidth = 6;
        public int ZoneSpacingCenti = 85;
        public int ZoneRankSpacingCenti = 75;
        public int ZoneFlankSideCenti = 300;
        public int ZoneGuardRadiusCenti = 130;
        public int ZoneGuardGapCenti = 85;
        // LOGISTICS: supply buildings project supplyRadius while CONNECTED to an HQ through a
        // chain of supply buildings each within linkRange of the next — the chain is the
        // raidable supply line. Units carry a grace reservoir that drains outside supply;
        // at zero they fight at Num/Den damage until resupplied.
        public int SupplyRadiusCenti = 1800;
        public int SupplyLinkRangeCenti = 3000;
        public int SupplyGraceTicks = 600;
        public int UnsuppliedDamageNum = 1;
        public int UnsuppliedDamageDen = 2;
        // Each supplied unit consumes 1 food from its covering depot every this many ticks.
        public int SupplyDrainTicks = 200;
        // CACHE TIERS: each upgrade of a supply cache doubles its stock capacity and max HP
        // and halves per-unit drain (the interval doubles); Tier >= 1 also arms the cache
        // with a ranged shot. Upgrade cost doubles per tier already bought.
        public int CacheMaxTier = 3;
        public int CacheUpgradeFoodCost = 100;
        public int CacheAttackDamage = 6;
        public int CacheAttackRangeCenti = 600;
        public int CacheAttackCooldownTicks = 30;
        public int CacheProjectileSpeedCenti = 1400; // visual-only: shot dot speed
        // VISION (fog of war): how far units/buildings see. The fog itself is client-side
        // (derived from positions, never fed back into the sim), but the radii are shared
        // data so future scouting features and honest bots read the same numbers.
        public int UnitVisionRangeCenti = 1400;
        public int BuildingVisionRangeCenti = 2000;
        // Hub-built prongs (tech-path buildings) self-construct at this many work units/tick
        // (construction is 3× BuildTimeTicks work units, so rate 3 finishes in BuildTimeTicks).
        public int HubBuildRate = 3;
        // Minerals: the secondary resource, mined from mineral nodes; spent on evolution prongs.
        public int StartingMinerals = 0;
        // KILL BOUNTY: whoever lands the killing blow on an enemy unit banks this fraction of
        // the victim's nutrient cost (default 1/10 = 10%). One integer floor at the award.
        public int KillBountyNum = 1;
        public int KillBountyDen = 10;
        // EVOLUTIONARY POINTS: banked per enemy unit slain and by no other means — the only
        // resource you cannot gather, so evolution is paid for in blood.
        public int EvoPerKill = 1;
        // BODY BLOCKING (opposing teams only): a unit's shove-weight is radius × maxHp. When one
        // body outweighs the other by this ratio it is immovable to it and the lighter unit takes
        // the whole separation — big tough units wall smaller enemies out instead of sliding.
        public int CollisionBlockRatioNum = 2;
        public int CollisionBlockRatioDen = 1;
    }

    /// <summary>
    /// A unit archetype. All distances are centi-units (1/100 of a world unit) and all rates
    /// are integer ticks, so defs stay pure-integer JSON. Role scores drive data-driven
    /// formation band assignment (tank/damage/speed/range/support).
    /// </summary>
    public sealed class UnitDef
    {
        public string Id = "";
        public string Description = ""; // flavour/role blurb shown on the unit card
        public int DenseIndex;
        public int MaxHp;
        public int MoveSpeedCenti;        // centi-units per second
        public int CollisionRadiusCenti;
        public int PushStrength;
        public int PushResistance;
        public int AttackDamage;
        public int AttackRangeCenti;
        public int AcquireRangeCenti;
        public int AttackCooldownTicks;
        public int ProjectileSpeedCenti; // visual-only: >0 = ranged shot dot at this speed (u/100 per s)
        public int TurnSpeedCenti = 600; // facing turn rate: unit-circle chord per second /100 (100 ≈ 60°/s)
        public int FoodCost;
        public int BuildTimeTicks;
        public bool IsWorker;
        public bool IsLeader;             // swarm leader: commands a squad, backbone of formations
        public byte DefaultZone = SimConstants.ZoneFront; // where fresh squads place this type
        public int CarryCapacity;
        public int GatherTicks;
        public int TankScore;
        public int DamageScore;
        public int SpeedScore;
        public int RangeScore;
        public int SupportScore;
    }

    public sealed class BuildingDef
    {
        public string Id = "";
        public string Description = ""; // flavour/role blurb shown on the building card
        public int DenseIndex;
        public int MaxHp;
        public int CollisionRadiusCenti;
        public bool IsHeadquarters;   // resource drop-off + defeat condition + supply chain root
        public bool ProvidesSupply;   // projects supply and relays the chain (when connected)
        public int AttackBonus;       // while this stands FINISHED, every unit its owner fields
                                      //   hits this much harder — stacks across copies, and is
                                      //   lost the moment the structure falls
        public int StockCapacity;     // food this depot can hold; supply flows only while stocked
        public bool StartsBuilt;      // placed for every player at match start
        public bool Constructible;    // workers may place this via ConstructBuilding
        public bool HubBuilt;         // built as a prong off the headquarters (BuildProng),
                                      //   auto-placed and self-building — no worker involved
        public int FoodCost;          // nutrients paid when placed
        public int MineralCost;       // minerals paid when placed (evolution prongs cost these)
        public int EvoCost;           // evolutionary points paid when placed (earned only by killing)
        public int BuildTimeTicks;    // worker-ticks of construction to complete
        public string[] Produces = Array.Empty<string>();
        public int[] ProducesDense = Array.Empty<int>(); // resolved at load
    }

    /// <summary>Which stat a tech-path upgrade scales, applied as a Num/Den rational at the
    /// relevant hot path (one integer floor, per the iron rules).</summary>
    public enum UpgradeStat : byte
    {
        Damage = 0,       // attacker: outgoing damage ×Num/Den
        MoveSpeed = 1,    // move step ×Num/Den
        AttackSpeed = 2,  // attack cooldown ×Num/Den (Num<Den = faster)
        AttackRange = 3,  // attack reach ×Num/Den
        AcquireRange = 4, // target-acquisition reach ×Num/Den
        Armor = 5,        // target: incoming damage ×Num/Den (Num<Den = tougher)
    }

    /// <summary>
    /// A purchasable tech-path upgrade. Bought once per player (PlayerState.UpgradeLevels),
    /// gated behind its path building, and scales one Stat for the listed units by Num/Den.
    /// </summary>
    public sealed class UpgradeDef
    {
        public string Id = "";
        public int DenseIndex;
        public int FoodCost;
        public string RequiresBuilding = "";      // path building that must stand to buy this
        public int RequiresBuildingDense = -1;    // resolved at load
        public UpgradeStat Stat;
        public int Num = 1;
        public int Den = 1;
        public string[] Affects = Array.Empty<string>(); // unit ids this upgrade scales
        public bool[] AffectsUnit = Array.Empty<bool>(); // resolved: indexed by unit dense index
    }

    public sealed class MapSpawn { public int XCenti; public int YCenti; }
    public sealed class MapNode { public int XCenti; public int YCenti; public int Food; public bool Mineral; }

    public sealed class MapDef
    {
        public string Name = "";
        public int WidthCenti;
        public int HeightCenti;
        public MapSpawn[] Spawns = Array.Empty<MapSpawn>();
        public MapNode[] Nodes = Array.Empty<MapNode>();
    }

    /// <summary>
    /// The loaded dataset. Def arrays are sorted by id (ordinal) — DenseIndex is that order,
    /// and all sim iteration goes over the dense arrays. The dictionaries are load-time
    /// lookups ONLY; never enumerate them in tick code.
    /// </summary>
    public sealed class DefDatabase
    {
        public readonly Rules Rules;
        public readonly UnitDef[] Units;
        public readonly BuildingDef[] Buildings;
        public readonly UpgradeDef[] Upgrades;
        public readonly ulong DefsHash;

        private readonly Dictionary<string, int> _unitIndex = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _buildingIndex = new Dictionary<string, int>();
        private readonly Dictionary<string, int> _upgradeIndex = new Dictionary<string, int>();

        public DefDatabase(Rules rules, UnitDef[] unitsSortedById, BuildingDef[] buildingsSortedById,
            UpgradeDef[] upgradesSortedById, ulong defsHash)
        {
            Rules = rules;
            Units = unitsSortedById;
            Buildings = buildingsSortedById;
            Upgrades = upgradesSortedById;
            DefsHash = defsHash;
            for (int i = 0; i < Units.Length; i++) { Units[i].DenseIndex = i; _unitIndex[Units[i].Id] = i; }
            for (int i = 0; i < Buildings.Length; i++) { Buildings[i].DenseIndex = i; _buildingIndex[Buildings[i].Id] = i; }

            foreach (var b in Buildings)
            {
                b.ProducesDense = new int[b.Produces.Length];
                for (int i = 0; i < b.Produces.Length; i++) b.ProducesDense[i] = UnitIndex(b.Produces[i]);
            }

            for (int i = 0; i < Upgrades.Length; i++)
            {
                var up = Upgrades[i];
                up.DenseIndex = i;
                _upgradeIndex[up.Id] = i;
                up.RequiresBuildingDense = string.IsNullOrEmpty(up.RequiresBuilding) ? -1 : BuildingIndex(up.RequiresBuilding);
                up.AffectsUnit = new bool[Units.Length];
                for (int k = 0; k < up.Affects.Length; k++) up.AffectsUnit[UnitIndex(up.Affects[k])] = true;
            }
        }

        /// <summary>Per-unit-def default zones, the template every fresh leader's placement
        /// matrix starts from.</summary>
        public byte[] BuildDefaultZones()
        {
            var zones = new byte[Units.Length];
            for (int u = 0; u < Units.Length; u++) zones[u] = Units[u].DefaultZone;
            return zones;
        }

        public int UnitIndex(string id) => _unitIndex.TryGetValue(id, out int i) ? i : throw new KeyNotFoundException("unknown unit def: " + id);
        public int BuildingIndex(string id) => _buildingIndex.TryGetValue(id, out int i) ? i : throw new KeyNotFoundException("unknown building def: " + id);
        public int UpgradeIndex(string id) => _upgradeIndex.TryGetValue(id, out int i) ? i : throw new KeyNotFoundException("unknown upgrade def: " + id);
    }
}
