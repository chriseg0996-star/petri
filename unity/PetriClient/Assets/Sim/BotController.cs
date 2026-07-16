using System.Collections.Generic;

namespace Petri.Core
{
    /// <summary>
    /// A dumb, deterministic skirmish opponent. STRICTLY a command source: it reads the world
    /// and appends Commands (Player pre-stamped) to a list the host feeds into the CommandLog
    /// — it never mutates sim state and never touches the sim's RNG (it carries its own Pcg32
    /// stream), so bot matches replay and hash-verify exactly like human ones.
    ///
    /// The plan is econ + attack waves: tune production toward soldiers, put up military
    /// buildings as food allows, let auto-assimilation grow squads around produced leaders,
    /// and fling any full-enough squad (or a leaderless mob) at the enemy headquarters.
    ///
    /// VISION-HONEST: the bot keeps its own exploration memory (1-unit cells, integer math
    /// only — this file lives under the sim's Fix64 rules) stamped from its units' and
    /// buildings' vision ranges, exactly the information a fogged human would have. It may
    /// only target an enemy HQ whose cell it has explored; until then it sends a scout
    /// through the map's corners to find one.
    /// </summary>
    public sealed class BotController
    {
        public const int ThinkPeriod = 25;     // decide ~every 1.25s, on the bot's own beat
        public const int WaveSquadSize = 10;   // squad members before a leader wave launches
        public const int WaveLooseSize = 10;   // loose fighters before a leaderless mob rushes
        public const int WavePatienceTicks = 2400; // ~2 min without a wave → attack with whatever
        private const int MinWaveSize = 4;     // even an impatient wave needs a few bodies
        private const int BuildReserve = 60;   // food kept on hand after placing a building
        public const int WorkerCap = 10;       // workers wanted before production pins to combat

        public const int AlarmTicks = 600;     // war footing after taking damage (~30 s)

        private readonly byte _player;
        private Pcg32 _rng;
        private bool _tunedWeights;
        private int _lastWaveTick;
        private long _prevHpSum;               // own total HP last think — a drop means damage
        private int _alarmUntil;

        // Exploration memory (what this bot has SEEN, ever) on 1-unit cells. Integer math
        // throughout — cell coords are Fix raws shifted down, radii are centi/100.
        private bool[] _explored;
        private int _visW, _visH;

        public BotController(byte player, ulong matchSeed)
        {
            _player = player;
            // Own stream, decorrelated per player; NEVER the sim's RNG (rule: bots are outside
            // the sim — a bot must not perturb state except through commands).
            _rng = new Pcg32(matchSeed ^ 0xB07B075EEDUL, 0xB07AA000UL + player);
        }

        /// <summary>Read the world, append this tick's decisions (if any). Call every tick;
        /// the bot acts only on its think beat. All scans are index-order (determinism).</summary>
        public void Think(SimWorld w, DefDatabase defs, List<Command> outCommands)
        {
            if (w.TickCount % ThinkPeriod != 0) return;
            if (_player >= w.Players.Length || !w.Players[_player].Alive) return;

            StampVision(w, defs); // refresh exploration memory BEFORE reading the world

            // ---- Census. Enemy intel is fog-gated: an enemy HQ counts only once the bot has
            // actually explored its cell (buildings are static, so memory stays valid).
            int hq = -1, militaryProducers = 0, leaderProducers = 0, freeWorker = -1, looseCombat = 0, leaderCount = 0;
            int firstScoutable = -1, firstIdleLeader = -1, workerCount = 0;
            long hpSum = 0;
            bool enemyHqFound = false;
            FixVec2 enemyHq = default(FixVec2);
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] == EntityKind.Building)
                {
                    var bd = defs.Buildings[w.DefIndex[i]];
                    if (w.Owner[i] == _player)
                    {
                        hpSum += w.Hp[i];
                        if (bd.IsHeadquarters && hq < 0) hq = i;
                        // Construction sites count too — don't order a second while one rises.
                        if (ProducesCombat(defs, bd)) militaryProducers++;
                        if (ProducesLeader(defs, bd)) leaderProducers++;
                    }
                    else if (w.AreEnemies(_player, w.Owner[i]) && bd.IsHeadquarters && !enemyHqFound
                             && ExploredAt(w.Pos[i]))
                    {
                        // Allied cores are never a target — only a hostile team's.
                        enemyHq = w.Pos[i];
                        enemyHqFound = true;
                    }
                }
                else if (w.Kind[i] == EntityKind.Unit && w.Owner[i] == _player)
                {
                    hpSum += w.Hp[i];
                    var ud = defs.Units[w.DefIndex[i]];
                    if (ud.IsWorker)
                    {
                        workerCount++;
                        if (freeWorker < 0 && w.BuildTask[i] < 0) freeWorker = i;
                    }
                    else if (ud.IsLeader)
                    {
                        if (w.Leader[i] < 0)
                        {
                            leaderCount++;
                            if (firstIdleLeader < 0 && !w.AttackMove[i]) firstIdleLeader = i;
                        }
                    }
                    else if (w.Leader[i] < 0 && !w.AttackMove[i])
                    {
                        looseCombat++;
                        if (firstScoutable < 0) firstScoutable = i;
                    }
                }
            }
            if (hq < 0) return;

            // ---- One-time production plan: soldiers first, a trickle of workers and leaders.
            if (!_tunedWeights)
            {
                for (int k = 0; k < defs.Units.Length; k++)
                {
                    var ud = defs.Units[k];
                    int weight = ud.IsWorker ? 2 : ud.IsLeader ? 1 : 4;
                    outCommands.Add(new Command { Player = _player, Type = CommandType.SetProductionWeight, A = k, B = weight });
                }
                _tunedWeights = true;
            }

            // ---- Alarm: our total HP dropped since the last think — something is hitting
            // us (an honest signal: a player watches their own units bleed). On war footing
            // the bot stops saving and pumps combat units instead.
            if (_prevHpSum > 0 && hpSum < _prevHpSum) _alarmUntil = w.TickCount + AlarmTicks;
            _prevHpSum = hpSum;
            bool underAttack = w.TickCount < _alarmUntil;

            // ---- Expansion. A leader-capable building is the army's spine — put one up
            // first (falling back to any combat producer); a second combat building when
            // rich. Production always buys the best AFFORDABLE unit, so a hand-to-mouth
            // bank never climbs on its own: while short of a building's price, the bot
            // SAVES by pausing its production tills.
            long food = w.Players[_player].Food;
            bool saving = false;
            int buildPick = leaderProducers == 0 ? PickConstructible(defs, true) : -1;
            if (buildPick < 0 && food > 1500 && militaryProducers < 2) buildPick = PickConstructible(defs, false);
            if (buildPick >= 0 && freeWorker >= 0 && !underAttack)
            {
                var bdef = defs.Buildings[buildPick];
                FixVec2 spot;
                if (FindSpot(w, defs, hq, bdef, out spot))
                {
                    if (food >= bdef.FoodCost + BuildReserve)
                        outCommands.Add(new Command
                        {
                            Player = _player, Type = CommandType.ConstructBuilding, A = freeWorker,
                            B = CentiOf(spot.X), C = CentiOf(spot.Y), D = buildPick,
                        });
                    else saving = true;
                }
            }

            // ---- Producer management. Under attack everything pins to combat units and
            // nothing pauses — defense first. In peacetime the first leader gets pinned
            // (everything else pauses so its price can bank), and with the worker pool
            // full, producers pin to combat so the till stops buying cheap workers.
            bool needLeader = !underAttack && leaderCount == 0 && workerCount >= 4 && leaderProducers > 0;
            for (int b = 0; b < w.HighWater; b++)
            {
                if (w.Kind[b] != EntityKind.Building || w.Owner[b] != _player || w.ConstructionRemaining[b] > 0) continue;
                var bd = defs.Buildings[w.DefIndex[b]];
                if (bd.ProducesDense.Length == 0) continue;
                int leaderCand = -1, combatCand = -1;
                for (int k = 0; k < bd.ProducesDense.Length; k++)
                {
                    var ud = defs.Units[bd.ProducesDense[k]];
                    if (ud.IsLeader) { if (leaderCand < 0) leaderCand = bd.ProducesDense[k]; }
                    else if (!ud.IsWorker && combatCand < 0) combatCand = bd.ProducesDense[k];
                }
                int desired = underAttack && combatCand >= 0 ? combatCand
                    : needLeader && leaderCand >= 0 ? leaderCand
                    : workerCount >= WorkerCap && combatCand >= 0 ? combatCand : -1;
                if (w.ProduceOverride[b] != desired)
                    outCommands.Add(new Command { Player = _player, Type = CommandType.SetProduceOverride, A = b, B = desired });
                bool pause = !underAttack && (saving || (needLeader && leaderCand < 0));
                if (w.ProducePaused[b] != pause)
                    outCommands.Add(new Command { Player = _player, Type = CommandType.SetProducePaused, A = b, B = pause ? 1 : 0 });
            }

            // ---- No known enemy HQ yet: send a scout through the map's far reaches — a
            // loose fighter if one exists, else an idle squad (a leader scout takes its
            // whole formation along, which doubles as the first push).
            if (!enemyHqFound)
            {
                Scout(w, firstScoutable >= 0 ? firstScoutable : firstIdleLeader, outCommands);
                return;
            }

            // ---- Attack waves at the enemy headquarters. Full squads launch on their own;
            // after WavePatienceTicks without any wave the bot loses patience and throws
            // whatever it has (keeps small economies aggressive instead of turtling forever).
            int tx = CentiOf(enemyHq.X), ty = CentiOf(enemyHq.Y);
            bool impatient = w.TickCount - _lastWaveTick >= WavePatienceTicks;
            int needSquad = impatient ? MinWaveSize : WaveSquadSize;
            bool launched = false;

            for (int lead = 0; lead < w.HighWater; lead++)
            {
                if (w.Kind[lead] != EntityKind.Unit || w.Owner[lead] != _player) continue;
                if (!defs.Units[w.DefIndex[lead]].IsLeader || w.Leader[lead] >= 0) continue;
                if (w.AttackMove[lead]) continue; // already committed to a wave
                int squad = 0;
                for (int m = 0; m < w.HighWater; m++)
                    if (w.Kind[m] == EntityKind.Unit && w.Leader[m] == lead && !defs.Units[w.DefIndex[m]].IsLeader) squad++;
                if (squad < needSquad) continue;
                outCommands.Add(new Command
                {
                    Player = _player, Type = CommandType.AttackMove, A = lead,
                    B = tx + _rng.NextInt(401) - 200, C = ty + _rng.NextInt(401) - 200,
                });
                launched = true;
            }

            // No leaders on the field: once a pile of loose fighters accumulates, mob rush.
            int needLoose = impatient ? MinWaveSize : WaveLooseSize;
            if (leaderCount == 0 && looseCombat >= needLoose)
            {
                for (int u = 0; u < w.HighWater; u++)
                {
                    if (w.Kind[u] != EntityKind.Unit || w.Owner[u] != _player) continue;
                    var ud = defs.Units[w.DefIndex[u]];
                    if (ud.IsWorker || ud.IsLeader || w.Leader[u] >= 0 || w.AttackMove[u]) continue;
                    outCommands.Add(new Command
                    {
                        Player = _player, Type = CommandType.AttackMove, A = u,
                        B = tx + _rng.NextInt(401) - 200, C = ty + _rng.NextInt(401) - 200,
                    });
                    launched = true;
                }
            }
            if (launched) _lastWaveTick = w.TickCount;
        }

        /// <summary>Stamp exploration memory from every own unit's/building's vision circle.
        /// Pure integers: cell = Fix raw >> FracBits, radius = centi / 100.</summary>
        private void StampVision(SimWorld w, DefDatabase defs)
        {
            if (_explored == null)
            {
                _visW = System.Math.Max(1, (int)(w.MapWidth.Raw >> Fix.FracBits));
                _visH = System.Math.Max(1, (int)(w.MapHeight.Raw >> Fix.FracBits));
                _explored = new bool[_visW * _visH];
            }
            for (int i = 0; i < w.HighWater; i++)
            {
                bool unit = w.Kind[i] == EntityKind.Unit;
                if (!unit && w.Kind[i] != EntityKind.Building) continue;
                if (w.Owner[i] != _player) continue;
                int r = (unit ? w.Rules.UnitVisionRangeCenti : w.Rules.BuildingVisionRangeCenti) / 100;
                int cx = (int)(w.Pos[i].X.Raw >> Fix.FracBits);
                int cy = (int)(w.Pos[i].Y.Raw >> Fix.FracBits);
                int x0 = ClampCell(cx - r, _visW), x1 = ClampCell(cx + r, _visW);
                int y0 = ClampCell(cy - r, _visH), y1 = ClampCell(cy + r, _visH);
                for (int y = y0; y <= y1; y++)
                {
                    int dy = y - cy, row = y * _visW;
                    for (int x = x0; x <= x1; x++)
                    {
                        int dx = x - cx;
                        if (dx * dx + dy * dy <= r * r) _explored[row + x] = true;
                    }
                }
            }
        }

        private bool ExploredAt(FixVec2 pos)
        {
            if (_explored == null) return false;
            int x = ClampCell((int)(pos.X.Raw >> Fix.FracBits), _visW);
            int y = ClampCell((int)(pos.Y.Raw >> Fix.FracBits), _visH);
            return _explored[y * _visW + x];
        }

        private static int ClampCell(int v, int max) => v < 0 ? 0 : (v >= max ? max - 1 : v);

        /// <summary>March a loose fighter through the map's corners (then center) until the
        /// enemy is found — the first unexplored waypoint wins; a scout already under orders
        /// is left to finish its leg.</summary>
        private void Scout(SimWorld w, int scout, List<Command> outCommands)
        {
            if (scout < 0 || _explored == null) return;
            if (w.HasMoveOrder[scout]) return; // mid-leg; let it walk

            // Corner sweep, offset from the edges by an eighth of the map, center last.
            int ox = _visW / 8, oy = _visH / 8;
            int[] px = { _visW - ox, ox, _visW - ox, ox, _visW / 2 };
            int[] py = { _visH - oy, _visH - oy, oy, oy, _visH / 2 };
            for (int k = 0; k < px.Length; k++)
            {
                if (_explored[ClampCell(py[k], _visH) * _visW + ClampCell(px[k], _visW)]) continue;
                outCommands.Add(new Command
                {
                    Player = _player, Type = CommandType.Move, A = scout,
                    B = px[k] * 100 + 50, C = py[k] * 100 + 50,
                });
                return;
            }
        }

        /// <summary>First constructible building that can produce a leader (leaderCapable)
        /// or any combat unit — the bot's expansion shopping list.</summary>
        private static int PickConstructible(DefDatabase defs, bool leaderCapable)
        {
            for (int b = 0; b < defs.Buildings.Length; b++)
            {
                var bd = defs.Buildings[b];
                if (!bd.Constructible) continue;
                if (leaderCapable ? ProducesLeader(defs, bd) : ProducesCombat(defs, bd)) return b;
            }
            return -1;
        }

        /// <summary>A clear spot ringed around the HQ, pre-checked against the sim's own
        /// overlap rule so the ConstructBuilding command lands instead of rejecting.</summary>
        private bool FindSpot(SimWorld w, DefDatabase defs, int hq, BuildingDef bdef, out FixVec2 spot)
        {
            Fix hqR = Fix.Ratio(defs.Buildings[w.DefIndex[hq]].CollisionRadiusCenti, 100);
            Fix newR = Fix.Ratio(bdef.CollisionRadiusCenti, 100);
            int start = _rng.NextInt(8);
            for (int a = 0; a < 16; a++)
            {
                Fix dist = hqR + newR + Fix.Ratio(150 + (a / 8) * 250, 100);
                var pos = w.ClampToMap(w.Pos[hq] + SimWorld.RingDir(start + a) * dist);
                if (SpotClear(w, defs, pos, newR)) { spot = pos; return true; }
            }
            spot = default(FixVec2);
            return false;
        }

        // Mirror of the ConstructBuilding footprint rule (with a little extra margin).
        private static bool SpotClear(SimWorld w, DefDatabase defs, FixVec2 pos, Fix newR)
        {
            Fix gap = Fix.Ratio(20, 100);
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Building && w.Kind[i] != EntityKind.Node) continue;
                Fix otherR = w.Kind[i] == EntityKind.Building
                    ? Fix.Ratio(defs.Buildings[w.DefIndex[i]].CollisionRadiusCenti, 100)
                    : Fix.Ratio(w.Rules.NodeRadiusCenti, 100);
                Fix minD = newR + otherR + gap;
                if ((w.Pos[i] - pos).LengthSq < minD * minD) return false;
            }
            return true;
        }

        private static bool ProducesCombat(DefDatabase defs, BuildingDef bd)
        {
            for (int k = 0; k < bd.ProducesDense.Length; k++)
                if (!defs.Units[bd.ProducesDense[k]].IsWorker) return true;
            return false;
        }

        private static bool ProducesLeader(DefDatabase defs, BuildingDef bd)
        {
            for (int k = 0; k < bd.ProducesDense.Length; k++)
                if (defs.Units[bd.ProducesDense[k]].IsLeader) return true;
            return false;
        }

        private static int CentiOf(Fix v) => (int)((long)v.Raw * 100 / Fix.OneRaw);
    }
}
