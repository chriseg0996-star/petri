using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Petri.Core
{
    /// <summary>
    /// Loads the JSON dataset. Files are read in ordinal filename order and defs are dense-
    /// indexed alphabetically by id, so the same data directory always yields the same
    /// DefsHash — peers compare that hash at connect time to refuse mismatched datasets.
    /// Load-time only: nothing here runs during ticks.
    /// </summary>
    public static class DefLoader
    {
        public static DefDatabase Load(string dataDir)
        {
            var rules = ParseRules(File.ReadAllText(Path.Combine(dataDir, "rules.json")));

            var units = new List<UnitDef>();
            foreach (var file in SortedFiles(Path.Combine(dataDir, "units")))
                units.Add(ParseUnit(File.ReadAllText(file)));
            units.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

            var buildings = new List<BuildingDef>();
            foreach (var file in SortedFiles(Path.Combine(dataDir, "buildings")))
                buildings.Add(ParseBuilding(File.ReadAllText(file)));
            buildings.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

            var upgrades = new List<UpgradeDef>();
            foreach (var file in SortedFiles(Path.Combine(dataDir, "upgrades")))
                upgrades.Add(ParseUpgrade(File.ReadAllText(file)));
            upgrades.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

            return new DefDatabase(rules, units.ToArray(), buildings.ToArray(), upgrades.ToArray(), ComputeDefsHash(dataDir));
        }

        public static MapDef LoadMap(string dataDir, string name)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDir, "maps", name + ".json")));
            var root = doc.RootElement;
            var spawns = new List<MapSpawn>();
            foreach (var s in root.GetProperty("spawns").EnumerateArray())
                spawns.Add(new MapSpawn { XCenti = GetInt(s, "x"), YCenti = GetInt(s, "y") });
            var nodes = new List<MapNode>();
            foreach (var n in root.GetProperty("nodes").EnumerateArray())
                nodes.Add(new MapNode
                {
                    XCenti = GetInt(n, "x"), YCenti = GetInt(n, "y"), Food = GetInt(n, "food"),
                    Mineral = GetString(n, "resource", "food") == "minerals",
                });
            return new MapDef
            {
                Name = GetString(root, "name", name),
                WidthCenti = GetInt(root, "widthCenti"),
                HeightCenti = GetInt(root, "heightCenti"),
                Spawns = spawns.ToArray(),
                Nodes = nodes.ToArray(),
            };
        }

        /// <summary>FNV-1a over every data file's relative path + bytes, in sorted order.</summary>
        public static ulong ComputeDefsHash(string dataDir)
        {
            ulong h = 14695981039346656037UL;
            void MixByte(byte b) { h ^= b; h *= 1099511628211UL; }

            var files = new List<string>(Directory.GetFiles(dataDir, "*.json", SearchOption.AllDirectories));
            files.Sort(StringComparer.Ordinal);
            foreach (var file in files)
            {
                string rel = Path.GetRelativePath(dataDir, file).Replace('\\', '/');
                foreach (char c in rel) MixByte((byte)c);
                foreach (byte b in File.ReadAllBytes(file)) MixByte(b);
            }
            return h;
        }

        private static string[] SortedFiles(string dir)
        {
            var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json") : Array.Empty<string>();
            Array.Sort(files, StringComparer.Ordinal);
            return files;
        }

        private static Rules ParseRules(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return new Rules
            {
                MaxEntities = GetInt(r, "maxEntities", 512),
                StartingFood = GetInt(r, "startingFood", 200),
                StartingWorkers = GetInt(r, "startingWorkers", 6),
                NodeRadiusCenti = GetInt(r, "nodeRadiusCenti", 60),
                LeaderAuraBonusNum = GetInt(r, "leaderAuraBonusNum", 5),
                LeaderAuraBonusDen = GetInt(r, "leaderAuraBonusDen", 4),
                LeaderAuraRadiusCenti = GetInt(r, "leaderAuraRadiusCenti", 600),
                FrontArcCosNum = GetInt(r, "frontArcCosNum", 1),
                FrontArcCosDen = GetInt(r, "frontArcCosDen", 2),
                RearArcCosNum = GetInt(r, "rearArcCosNum", 1),
                RearArcCosDen = GetInt(r, "rearArcCosDen", 2),
                SideDamageNum = GetInt(r, "sideDamageNum", 5),
                SideDamageDen = GetInt(r, "sideDamageDen", 4),
                RearDamageNum = GetInt(r, "rearDamageNum", 3),
                RearDamageDen = GetInt(r, "rearDamageDen", 2),
                SupplyRadiusCenti = GetInt(r, "supplyRadiusCenti", 1800),
                SupplyLinkRangeCenti = GetInt(r, "supplyLinkRangeCenti", 3000),
                SupplyGraceTicks = GetInt(r, "supplyGraceTicks", 600),
                UnsuppliedDamageNum = GetInt(r, "unsuppliedDamageNum", 1),
                UnsuppliedDamageDen = GetInt(r, "unsuppliedDamageDen", 2),
                SupplyDrainTicks = GetInt(r, "supplyDrainTicks", 200),
                CacheMaxTier = GetInt(r, "cacheMaxTier", 3),
                CacheUpgradeFoodCost = GetInt(r, "cacheUpgradeFoodCost", 100),
                CacheAttackDamage = GetInt(r, "cacheAttackDamage", 6),
                CacheAttackRangeCenti = GetInt(r, "cacheAttackRangeCenti", 600),
                CacheAttackCooldownTicks = GetInt(r, "cacheAttackCooldownTicks", 30),
                CacheProjectileSpeedCenti = GetInt(r, "cacheProjectileSpeedCenti", 1400),
                UnitVisionRangeCenti = GetInt(r, "unitVisionRangeCenti", 1400),
                BuildingVisionRangeCenti = GetInt(r, "buildingVisionRangeCenti", 2000),
                HubBuildRate = GetInt(r, "hubBuildRate", 3),
                StartingMinerals = GetInt(r, "startingMinerals", 0),
                KillBountyNum = GetInt(r, "killBountyNum", 1),
                KillBountyDen = GetInt(r, "killBountyDen", 10),
                CollisionBlockRatioNum = GetInt(r, "collisionBlockRatioNum", 2),
                CollisionBlockRatioDen = GetInt(r, "collisionBlockRatioDen", 1),
                EvoPerKill = GetInt(r, "evoPerKill", 1),
            };
        }

        private static UnitDef ParseUnit(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return new UnitDef
            {
                Id = GetString(r, "id", ""),
                Description = GetString(r, "description", ""),
                MaxHp = GetInt(r, "maxHp"),
                MoveSpeedCenti = GetInt(r, "moveSpeedCenti"),
                CollisionRadiusCenti = GetInt(r, "collisionRadiusCenti"),
                PushStrength = GetInt(r, "pushStrength", 1),
                PushResistance = GetInt(r, "pushResistance", 1),
                AttackDamage = GetInt(r, "attackDamage"),
                AttackRangeCenti = GetInt(r, "attackRangeCenti"),
                AcquireRangeCenti = GetInt(r, "acquireRangeCenti"),
                AttackCooldownTicks = GetInt(r, "attackCooldownTicks"),
                ProjectileSpeedCenti = GetInt(r, "projectileSpeedCenti"),
                TurnSpeedCenti = GetInt(r, "turnSpeedCenti", 600),
                FoodCost = GetInt(r, "foodCost"),
                BuildTimeTicks = GetInt(r, "buildTimeTicks"),
                IsWorker = GetBool(r, "isWorker"),
                IsLeader = GetBool(r, "isLeader"),
                CarryCapacity = GetInt(r, "carryCapacity"),
                GatherTicks = GetInt(r, "gatherTicks"),
            };
        }

        private static BuildingDef ParseBuilding(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var produces = new List<string>();
            if (r.TryGetProperty("produces", out var arr))
                foreach (var item in arr.EnumerateArray()) produces.Add(item.GetString() ?? "");
            return new BuildingDef
            {
                Id = GetString(r, "id", ""),
                Description = GetString(r, "description", ""),
                MaxHp = GetInt(r, "maxHp"),
                CollisionRadiusCenti = GetInt(r, "collisionRadiusCenti"),
                IsHeadquarters = GetBool(r, "isHeadquarters"),
                ProvidesSupply = GetBool(r, "providesSupply"),
                AttackBonus = GetInt(r, "attackBonus"),
                StockCapacity = GetInt(r, "stockCapacity"),
                StartsBuilt = GetBool(r, "startsBuilt"),
                Constructible = GetBool(r, "constructible"),
                HubBuilt = GetBool(r, "hubBuilt"),
                FoodCost = GetInt(r, "foodCost"),
                MineralCost = GetInt(r, "mineralCost"),
                EvoCost = GetInt(r, "evoCost"),
                BuildTimeTicks = GetInt(r, "buildTimeTicks"),
                Produces = produces.ToArray(),
            };
        }

        private static UpgradeDef ParseUpgrade(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var affects = new List<string>();
            if (r.TryGetProperty("affects", out var arr))
                foreach (var item in arr.EnumerateArray()) affects.Add(item.GetString() ?? "");
            return new UpgradeDef
            {
                Id = GetString(r, "id", ""),
                FoodCost = GetInt(r, "foodCost"),
                RequiresBuilding = GetString(r, "requiresBuilding", ""),
                Stat = ParseStat(GetString(r, "stat", "damage")),
                Num = GetInt(r, "num", 1),
                Den = GetInt(r, "den", 1),
                Affects = affects.ToArray(),
            };
        }

        /// <summary>Upgrade stat name → enum.</summary>
        private static UpgradeStat ParseStat(string stat)
        {
            switch (stat)
            {
                case "moveSpeed": return UpgradeStat.MoveSpeed;
                case "attackSpeed": return UpgradeStat.AttackSpeed;
                case "attackRange": return UpgradeStat.AttackRange;
                case "acquireRange": return UpgradeStat.AcquireRange;
                case "armor": return UpgradeStat.Armor;
                default: return UpgradeStat.Damage;
            }
        }

        private static int GetInt(JsonElement e, string name, int fallback = 0) =>
            e.TryGetProperty(name, out var p) ? p.GetInt32() : fallback;

        private static bool GetBool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var p) && p.GetBoolean();

        private static string GetString(JsonElement e, string name, string fallback) =>
            e.TryGetProperty(name, out var p) ? (p.GetString() ?? fallback) : fallback;
    }
}
