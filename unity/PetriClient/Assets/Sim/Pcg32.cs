namespace Petri.Core
{
    /// <summary>
    /// PCG-XSH-RR 32-bit deterministic RNG. The only randomness source allowed in sim code
    /// (System.Random is banned). State is part of the world hash.
    /// </summary>
    public struct Pcg32
    {
        private ulong _state;
        private ulong _inc;

        public Pcg32(ulong seed, ulong sequence)
        {
            _state = 0;
            _inc = (sequence << 1) | 1;
            NextUInt();
            _state += seed;
            NextUInt();
        }

        public uint NextUInt()
        {
            ulong old = _state;
            _state = old * 6364136223846793005UL + _inc;
            uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
            int rot = (int)(old >> 59);
            return (xorshifted >> rot) | (xorshifted << (-rot & 31));
        }

        public int NextInt(int maxExclusive) => maxExclusive <= 0 ? 0 : (int)(NextUInt() % (uint)maxExclusive);

        // Exposed so Simulation.StateHash can fold RNG state into the world fingerprint.
        public ulong State => _state;
        public ulong Inc => _inc;
    }
}
