using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Petri.Core;

namespace Petri.Client
{
    /// <summary>
    /// Loads the JSON dataset from StreamingAssets into the SAME def objects the deterministic
    /// sim consumes. Unity does not ship System.Text.Json (the headless DefLoader's parser), so
    /// this mirror uses UnityEngine.JsonUtility with [Serializable] DTOs. The DefsHash is
    /// computed with the identical FNV-over-sorted-bytes algorithm as the headless loader so a
    /// Unity client and a headless peer agree on dataset identity once networking lands.
    /// </summary>
    public static class UnityDataLoader
    {
#pragma warning disable 0649 // DTO fields are assigned by JsonUtility via reflection
        [Serializable] private class RulesDto
        {
            public int maxEntities, startingFood, startingWorkers, nodeRadiusCenti;
            public int leaderAuraBonusNum, leaderAuraBonusDen, leaderAuraRadiusCenti;
            public int frontArcCosNum, frontArcCosDen, rearArcCosNum, rearArcCosDen;
            public int sideDamageNum, sideDamageDen, rearDamageNum, rearDamageDen;
            public int supplyRadiusCenti, supplyLinkRangeCenti, supplyGraceTicks, unsuppliedDamageNum, unsuppliedDamageDen, supplyDrainTicks;
            public int cacheMaxTier, cacheUpgradeFoodCost, cacheAttackDamage, cacheAttackRangeCenti, cacheAttackCooldownTicks, cacheProjectileSpeedCenti;
            public int unitVisionRangeCenti, buildingVisionRangeCenti;
            public int hubBuildRate;
            public int startingMinerals;
            public int killBountyNum, killBountyDen;
            public int collisionBlockRatioNum, collisionBlockRatioDen;
            public int evoPerKill;
        }

        [Serializable] private class UnitDto
        {
            public string id;
            public string description;
            public int maxHp, moveSpeedCenti, collisionRadiusCenti, pushStrength, pushResistance;
            public int attackDamage, attackRangeCenti, acquireRangeCenti, attackCooldownTicks, projectileSpeedCenti, turnSpeedCenti;
            public int foodCost, buildTimeTicks, carryCapacity, gatherTicks;
            public bool isWorker, isLeader;
        }

        [Serializable] private class BuildingDto
        {
            public string id;
            public string description;
            public int maxHp, collisionRadiusCenti, foodCost, mineralCost, evoCost, buildTimeTicks;
            public bool isHeadquarters, providesSupply, startsBuilt, constructible, hubBuilt, isDropoff;
            public int attackBonus;
            public int attackDamage, attackRangeCenti, attackCooldownTicks, projectileSpeedCenti;
            public int stockCapacity;
            public string[] produces;
        }

        [Serializable] private class UpgradeDto
        {
            public string id;
            public int foodCost, num, den;
            public string requiresBuilding, stat;
            public string[] affects;
        }

        [Serializable] private class SpawnDto { public int x, y; }
        [Serializable] private class NodeDto { public int x, y, food; public string resource; }
        [Serializable] private class WallDto { public int x, y, r; }
        [Serializable] private class MapDto
        {
            public string name;
            public int widthCenti, heightCenti;
            public SpawnDto[] spawns;
            public NodeDto[] nodes;
            public WallDto[] walls;
        }
#pragma warning restore 0649

        public static string DataDir => Path.Combine(Application.streamingAssetsPath, "Data");

        public static DefDatabase LoadDefs()
        {
            string dataDir = DataDir;

            var rules = ToRules(ReadJson<RulesDto>(Path.Combine(dataDir, "rules.json")));

            var units = new List<UnitDef>();
            foreach (var file in SortedJson(Path.Combine(dataDir, "units")))
                units.Add(ToUnit(ReadJson<UnitDto>(file)));
            units.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

            var buildings = new List<BuildingDef>();
            foreach (var file in SortedJson(Path.Combine(dataDir, "buildings")))
                buildings.Add(ToBuilding(ReadJson<BuildingDto>(file)));
            buildings.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

            var upgrades = new List<UpgradeDef>();
            foreach (var file in SortedJson(Path.Combine(dataDir, "upgrades")))
                upgrades.Add(ToUpgrade(ReadJson<UpgradeDto>(file)));
            upgrades.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

            return new DefDatabase(rules, units.ToArray(), buildings.ToArray(), upgrades.ToArray(), ComputeDefsHash(dataDir));
        }

        public static MapDef LoadMap(string mapName)
        {
            var dto = ReadJson<MapDto>(Path.Combine(DataDir, "maps", mapName + ".json"));
            var spawns = new MapSpawn[dto.spawns != null ? dto.spawns.Length : 0];
            for (int i = 0; i < spawns.Length; i++) spawns[i] = new MapSpawn { XCenti = dto.spawns[i].x, YCenti = dto.spawns[i].y };
            var nodes = new MapNode[dto.nodes != null ? dto.nodes.Length : 0];
            for (int i = 0; i < nodes.Length; i++) nodes[i] = new MapNode
            {
                XCenti = dto.nodes[i].x, YCenti = dto.nodes[i].y, Food = dto.nodes[i].food,
                Mineral = dto.nodes[i].resource == "minerals",
            };
            var walls = new MapWall[dto.walls != null ? dto.walls.Length : 0];
            for (int i = 0; i < walls.Length; i++) walls[i] = new MapWall
            {
                XCenti = dto.walls[i].x, YCenti = dto.walls[i].y, RadiusCenti = dto.walls[i].r,
            };
            return new MapDef
            {
                Name = string.IsNullOrEmpty(dto.name) ? mapName : dto.name,
                WidthCenti = dto.widthCenti,
                HeightCenti = dto.heightCenti,
                Spawns = spawns,
                Nodes = nodes,
                Walls = walls,
            };
        }

        private static Rules ToRules(RulesDto d) => new Rules
        {
            MaxEntities = d.maxEntities > 0 ? d.maxEntities : 512,
            StartingFood = d.startingFood,
            StartingWorkers = d.startingWorkers,
            NodeRadiusCenti = d.nodeRadiusCenti > 0 ? d.nodeRadiusCenti : 60,
            LeaderAuraBonusNum = d.leaderAuraBonusNum > 0 ? d.leaderAuraBonusNum : 5,
            LeaderAuraBonusDen = d.leaderAuraBonusDen > 0 ? d.leaderAuraBonusDen : 4,
            LeaderAuraRadiusCenti = d.leaderAuraRadiusCenti > 0 ? d.leaderAuraRadiusCenti : 600,
            FrontArcCosNum = d.frontArcCosNum > 0 ? d.frontArcCosNum : 1,
            FrontArcCosDen = d.frontArcCosDen > 0 ? d.frontArcCosDen : 2,
            RearArcCosNum = d.rearArcCosNum > 0 ? d.rearArcCosNum : 1,
            RearArcCosDen = d.rearArcCosDen > 0 ? d.rearArcCosDen : 2,
            SideDamageNum = d.sideDamageNum > 0 ? d.sideDamageNum : 5,
            SideDamageDen = d.sideDamageDen > 0 ? d.sideDamageDen : 4,
            RearDamageNum = d.rearDamageNum > 0 ? d.rearDamageNum : 3,
            RearDamageDen = d.rearDamageDen > 0 ? d.rearDamageDen : 2,
            SupplyRadiusCenti = d.supplyRadiusCenti > 0 ? d.supplyRadiusCenti : 1800,
            SupplyLinkRangeCenti = d.supplyLinkRangeCenti > 0 ? d.supplyLinkRangeCenti : 3000,
            SupplyGraceTicks = d.supplyGraceTicks > 0 ? d.supplyGraceTicks : 600,
            UnsuppliedDamageNum = d.unsuppliedDamageNum > 0 ? d.unsuppliedDamageNum : 1,
            UnsuppliedDamageDen = d.unsuppliedDamageDen > 0 ? d.unsuppliedDamageDen : 2,
            SupplyDrainTicks = d.supplyDrainTicks > 0 ? d.supplyDrainTicks : 200,
            CacheMaxTier = d.cacheMaxTier > 0 ? d.cacheMaxTier : 3,
            CacheUpgradeFoodCost = d.cacheUpgradeFoodCost > 0 ? d.cacheUpgradeFoodCost : 100,
            CacheAttackDamage = d.cacheAttackDamage > 0 ? d.cacheAttackDamage : 6,
            CacheAttackRangeCenti = d.cacheAttackRangeCenti > 0 ? d.cacheAttackRangeCenti : 600,
            CacheAttackCooldownTicks = d.cacheAttackCooldownTicks > 0 ? d.cacheAttackCooldownTicks : 30,
            CacheProjectileSpeedCenti = d.cacheProjectileSpeedCenti > 0 ? d.cacheProjectileSpeedCenti : 1400,
            UnitVisionRangeCenti = d.unitVisionRangeCenti > 0 ? d.unitVisionRangeCenti : 1400,
            BuildingVisionRangeCenti = d.buildingVisionRangeCenti > 0 ? d.buildingVisionRangeCenti : 2000,
            HubBuildRate = d.hubBuildRate > 0 ? d.hubBuildRate : 3,
            StartingMinerals = d.startingMinerals,
            KillBountyNum = d.killBountyNum > 0 ? d.killBountyNum : 1,
            KillBountyDen = d.killBountyDen > 0 ? d.killBountyDen : 10,
            CollisionBlockRatioNum = d.collisionBlockRatioNum > 0 ? d.collisionBlockRatioNum : 2,
            CollisionBlockRatioDen = d.collisionBlockRatioDen > 0 ? d.collisionBlockRatioDen : 1,
            EvoPerKill = d.evoPerKill > 0 ? d.evoPerKill : 1,
        };

        private static UnitDef ToUnit(UnitDto d) => new UnitDef
        {
            Id = d.id, Description = d.description ?? "", MaxHp = d.maxHp, MoveSpeedCenti = d.moveSpeedCenti, CollisionRadiusCenti = d.collisionRadiusCenti,
            PushStrength = d.pushStrength, PushResistance = d.pushResistance, AttackDamage = d.attackDamage,
            AttackRangeCenti = d.attackRangeCenti, AcquireRangeCenti = d.acquireRangeCenti, AttackCooldownTicks = d.attackCooldownTicks,
            ProjectileSpeedCenti = d.projectileSpeedCenti,
            TurnSpeedCenti = d.turnSpeedCenti > 0 ? d.turnSpeedCenti : 600,
            FoodCost = d.foodCost, BuildTimeTicks = d.buildTimeTicks, IsWorker = d.isWorker, IsLeader = d.isLeader,
            CarryCapacity = d.carryCapacity, GatherTicks = d.gatherTicks,
        };

        private static BuildingDef ToBuilding(BuildingDto d) => new BuildingDef
        {
            Id = d.id, Description = d.description ?? "", MaxHp = d.maxHp, CollisionRadiusCenti = d.collisionRadiusCenti,
            IsHeadquarters = d.isHeadquarters, ProvidesSupply = d.providesSupply, IsDropoff = d.isDropoff,
            AttackDamage = d.attackDamage, AttackRangeCenti = d.attackRangeCenti,
            AttackCooldownTicks = d.attackCooldownTicks, ProjectileSpeedCenti = d.projectileSpeedCenti,
            AttackBonus = d.attackBonus, StockCapacity = d.stockCapacity, StartsBuilt = d.startsBuilt,
            Constructible = d.constructible, HubBuilt = d.hubBuilt, FoodCost = d.foodCost, MineralCost = d.mineralCost, EvoCost = d.evoCost, BuildTimeTicks = d.buildTimeTicks,
            Produces = d.produces ?? Array.Empty<string>(),
        };

        private static UpgradeDef ToUpgrade(UpgradeDto d) => new UpgradeDef
        {
            Id = d.id, FoodCost = d.foodCost,
            RequiresBuilding = d.requiresBuilding ?? "",
            Stat = ParseStat(d.stat), Num = d.num, Den = d.den,
            Affects = d.affects ?? Array.Empty<string>(),
        };

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

        private static T ReadJson<T>(string path) => JsonUtility.FromJson<T>(File.ReadAllText(path));

        private static List<string> SortedJson(string dir)
        {
            var list = new List<string>();
            if (Directory.Exists(dir)) list.AddRange(Directory.GetFiles(dir, "*.json"));
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        // Identical to DefLoader.ComputeDefsHash: FNV-1a over each file's relative path + bytes,
        // files sorted by ordinal relative path. Skips Unity's generated .meta sidecars.
        private static ulong ComputeDefsHash(string dataDir)
        {
            ulong h = 14695981039346656037UL;
            void MixByte(byte b) { h ^= b; h *= 1099511628211UL; }

            var files = new List<string>(Directory.GetFiles(dataDir, "*.json", SearchOption.AllDirectories));
            files.Sort(StringComparer.Ordinal);
            foreach (var file in files)
            {
                string rel = GetRelativePath(dataDir, file).Replace('\\', '/');
                foreach (char c in rel) MixByte((byte)c);
                foreach (byte b in File.ReadAllBytes(file)) MixByte(b);
            }
            return h;
        }

        private static string GetRelativePath(string baseDir, string full)
        {
            var baseUri = new Uri(AppendSep(baseDir));
            var fullUri = new Uri(full);
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fullUri).ToString());
        }

        private static string AppendSep(string p) =>
            p.EndsWith(Path.DirectorySeparatorChar.ToString()) ? p : p + Path.DirectorySeparatorChar;
    }
}
