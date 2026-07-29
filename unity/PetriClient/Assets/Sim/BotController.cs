using System.Collections.Generic;

namespace Petri.Core
{
    /// <summary>
    /// Skirmish opponent for the superorganism game. STRICTLY a command source: reads the
    /// world, appends Commands (Player pre-stamped) — never mutates sim state, never
    /// touches the sim RNG. INTERIM STUB during the conversion: tunes production weights
    /// once and lets the organism grow on its own; the full grow/build/push brain lands
    /// with the combat pass.
    /// </summary>
    public sealed class BotController
    {
        public const int ThinkPeriod = 25;

        private readonly byte _player;
        private Pcg32 _rng;
        private bool _tunedWeights;

        public BotController(byte player, ulong matchSeed)
        {
            _player = player;
            _rng = new Pcg32(matchSeed ^ 0xB07B075EEDUL, 0xB07AA000UL + player);
        }

        public void Think(SimWorld w, DefDatabase defs, List<Command> outCommands)
        {
            if (w.TickCount % ThinkPeriod != 0) return;
            if (_player >= w.Players.Length || !w.Players[_player].Alive) return;

            if (!_tunedWeights)
            {
                for (int k = 0; k < defs.Units.Length; k++)
                {
                    var ud = defs.Units[k];
                    int weight = ud.IsWorker ? 2 : ud.ProjectileSpeedCenti > 0 ? 3 : 4;
                    outCommands.Add(new Command { Player = _player, Type = CommandType.SetProductionWeight, A = k, B = weight });
                }
                _tunedWeights = true;
            }
        }
    }
}
