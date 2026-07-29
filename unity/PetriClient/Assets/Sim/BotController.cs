using System.Collections.Generic;

namespace Petri.Core
{
    /// <summary>
    /// Skirmish opponent for the superorganism game. STRICTLY a command source: reads the
    /// world (including deterministically derived scratch), appends Commands (Player
    /// pre-stamped) — never mutates sim state, never touches the sim RNG. Brain, on a
    /// ~10s cadence: tune production weights once; grow the base (military producers,
    /// then armed buildings) by a deterministic scan over own territory; STOP pushes
    /// that lost their advantage; hold everything while one of its own fronts is broken;
    /// otherwise push the front with the best push-vs-hold advantage at the weakest
    /// contacted enemy's heart.
    /// </summary>
    public sealed class BotController
    {
        public const int ThinkPeriod = 200; // ~10 s at 20 ticks/s

        private const int MaxExtraProducers = 3;
        private const int MaxArmedBuildings = 2;

        private readonly byte _player;
        private bool _tunedWeights;

        public BotController(byte player, ulong matchSeed)
        {
            _player = player;
        }

        public void Think(SimWorld w, DefDatabase defs, List<Command> outCommands)
        {
            if (_player >= w.Players.Length || !w.Players[_player].Alive) return;

            if (!_tunedWeights)
            {
                for (int k = 0; k < defs.Units.Length; k++)
                {
                    var ud = defs.Units[k];
                    int weight = ud.IsWorker ? 2 : ud.AttackDamage <= 0 ? 0
                        : ud.ProjectileSpeedCenti > 0 ? 3 : 4;
                    outCommands.Add(new Command { Player = _player, Type = CommandType.SetProductionWeight, A = k, B = weight });
                }
                _tunedWeights = true;
            }

            if (w.TickCount % ThinkPeriod != 0) return;

            ThinkBuild(w, defs, outCommands);
            ThinkFronts(w, defs, outCommands);
        }

        /// <summary>Base growth ladder: extra military producers first, then armed
        /// buildings. Free-spawner producers (only zero-cost units) don't count — the
        /// organism needs units that actually cost and fight.</summary>
        private void ThinkBuild(SimWorld w, DefDatabase defs, List<Command> outCommands)
        {
            var pl = w.Players[_player];
            int producers = 0, armed = 0;
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Building || w.Owner[i] != _player) continue;
                var bd = defs.Buildings[w.DefIndex[i]];
                if (ProducesPaidFighters(defs, bd)) producers++;
                if (bd.AttackDamage > 0) armed++;
            }

            int want = -1;
            if (producers < 1 + MaxExtraProducers) want = PickBuilding(w, defs, needProducer: true);
            else if (armed < MaxArmedBuildings) want = PickBuilding(w, defs, needProducer: false);
            if (want < 0) return;

            var bdef = defs.Buildings[want];
            if (pl.Food < bdef.FoodCost || pl.Minerals < bdef.MineralCost || pl.EvoPoints < bdef.EvoCost) return;
            if (!FindPlacement(w, defs, bdef, out int xCenti, out int yCenti)) return;
            outCommands.Add(new Command
            {
                Player = _player, Type = CommandType.ConstructBuilding,
                A = -1, B = xCenti, C = yCenti, D = want,
            });
        }

        /// <summary>Lowest-index constructible def that fits the role — a producer of
        /// paid fighters, or an armed building.</summary>
        private static int PickBuilding(SimWorld w, DefDatabase defs, bool needProducer)
        {
            for (int b = 0; b < defs.Buildings.Length; b++)
            {
                var bd = defs.Buildings[b];
                if (!bd.Constructible) continue;
                if (needProducer ? ProducesPaidFighters(defs, bd) : bd.AttackDamage > 0) return b;
            }
            return -1;
        }

        private static bool ProducesPaidFighters(DefDatabase defs, BuildingDef bd)
        {
            for (int k = 0; k < bd.ProducesDense.Length; k++)
            {
                var ud = defs.Units[bd.ProducesDense[k]];
                if (ud.FoodCost > 0 && ud.AttackDamage > 0) return true;
            }
            return false;
        }

        /// <summary>Deterministic placement: scan own cells ascending and take the first
        /// whose center is clear of walls, buildings and nodes by the def's radius.</summary>
        private bool FindPlacement(SimWorld w, DefDatabase defs, BuildingDef bdef, out int xCenti, out int yCenti)
        {
            long r = bdef.CollisionRadiusCenti + 20; // small placement margin
            for (int c = 0; c < w.CellCount; c++)
            {
                if (w.Territory[c] != _player || w.TerritoryBlocked[c]) continue;
                w.CellCenterCenti(c, out int cx, out int cy);
                bool clear = true;
                for (int i = 0; i < w.HighWater && clear; i++)
                {
                    if (w.Kind[i] == EntityKind.None) continue;
                    long or2 = w.Kind[i] == EntityKind.Building
                        ? defs.Buildings[w.DefIndex[i]].CollisionRadiusCenti
                        : w.Rules.NodeRadiusCenti;
                    long dx = cx - (long)w.Pos[i].X.Raw * 100 / Fix.OneRaw;
                    long dy = cy - (long)w.Pos[i].Y.Raw * 100 / Fix.OneRaw;
                    long reach = r + or2;
                    if (dx * dx + dy * dy < reach * reach) clear = false;
                }
                if (!clear) continue;
                xCenti = cx;
                yCenti = cy;
                return true;
            }
            xCenti = yCenti = 0;
            return false;
        }

        /// <summary>Front discipline: stop pushes that lost their edge, hold everything
        /// while any own front is broken, else push the best-advantage contested front
        /// toward the weakest contacted enemy's centroid.</summary>
        private void ThinkFronts(SimWorld w, DefDatabase defs, List<Command> outCommands)
        {
            var pl = w.Players[_player];
            const int MF = SimConstants.MaxFronts;

            bool anyBroken = false;
            for (int f = 0; f < pl.FrontCount; f++)
                if (pl.FrontBrokenTicks[f] > 0) { anyBroken = true; break; }

            int bestFront = -1, bestAdv = 0;
            byte bestEnemy = 0;
            for (int f = 0; f < pl.FrontCount; f++)
            {
                int slot = _player * MF + f;
                int nc = w.ScratchContactCount[slot];
                // No contact: any push here is directed EXPANSION (growth spends budget
                // toward the target and clears it on arrival) — leave it be. Only combat
                // pushes get policed for advantage.
                if (nc == 0) continue;
                int push = w.ScratchFrontMelee[slot] * w.Rules.PushNum / w.Rules.PushDen;
                // The WEAKEST contacted enemy front is the one worth probing.
                int minHold = int.MaxValue;
                byte minEnemy = 0;
                for (int e = 0; e < nc; e++)
                {
                    int packed = w.ScratchContact[slot * SimWorld.ContactCap + e];
                    byte q = (byte)(packed >> 8);
                    int g = packed & 0xFF;
                    int hold = w.Players[q].FrontBrokenTicks[g] > 0 ? 0 : w.ScratchFrontHold[q * MF + g];
                    if (hold < minHold) { minHold = hold; minEnemy = q; }
                }
                int adv = push - minHold;

                if (pl.FrontPushX[f] >= 0 && adv <= 0)
                    outCommands.Add(new Command { Player = _player, Type = CommandType.StopFront, A = f });
                else if (adv > bestAdv) { bestAdv = adv; bestFront = f; bestEnemy = minEnemy; }
            }

            if (anyBroken || bestFront < 0) return;
            if (pl.FrontPushX[bestFront] >= 0) return; // already committed there
            outCommands.Add(new Command
            {
                Player = _player, Type = CommandType.PushFront, A = bestFront,
                B = w.ScratchCentXCenti[bestEnemy], C = w.ScratchCentYCenti[bestEnemy],
            });
        }
    }
}
