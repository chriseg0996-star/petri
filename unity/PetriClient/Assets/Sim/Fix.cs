using System;

namespace Petri.Core
{
    /// <summary>
    /// Q16.16 fixed-point number stored in a long. The ONLY fractional math type allowed in
    /// sim code — floats/doubles are banned for cross-runtime bit-identity (lockstep multiplayer).
    /// </summary>
    public readonly struct Fix : IEquatable<Fix>, IComparable<Fix>
    {
        public const int FracBits = 16;
        public const long OneRaw = 1L << FracBits;

        public readonly long Raw;

        private Fix(long raw) { Raw = raw; }

        public static readonly Fix Zero = new Fix(0);
        public static readonly Fix One = new Fix(OneRaw);

        public static Fix FromRaw(long raw) => new Fix(raw);
        public static Fix FromInt(int value) => new Fix((long)value << FracBits);

        /// <summary>Exact rational — the canonical way to express fractional constants (one floor).</summary>
        public static Fix Ratio(long numerator, long denominator) => new Fix((numerator << FracBits) / denominator);

        public int FloorToInt() => (int)(Raw >> FracBits);

        public static Fix operator +(Fix a, Fix b) => new Fix(a.Raw + b.Raw);
        public static Fix operator -(Fix a, Fix b) => new Fix(a.Raw - b.Raw);
        public static Fix operator -(Fix a) => new Fix(-a.Raw);
        public static Fix operator *(Fix a, Fix b) => new Fix((a.Raw * b.Raw) >> FracBits);
        public static Fix operator /(Fix a, Fix b) => new Fix((a.Raw << FracBits) / b.Raw);

        public static bool operator ==(Fix a, Fix b) => a.Raw == b.Raw;
        public static bool operator !=(Fix a, Fix b) => a.Raw != b.Raw;
        public static bool operator <(Fix a, Fix b) => a.Raw < b.Raw;
        public static bool operator >(Fix a, Fix b) => a.Raw > b.Raw;
        public static bool operator <=(Fix a, Fix b) => a.Raw <= b.Raw;
        public static bool operator >=(Fix a, Fix b) => a.Raw >= b.Raw;

        public static Fix Min(Fix a, Fix b) => a.Raw < b.Raw ? a : b;
        public static Fix Max(Fix a, Fix b) => a.Raw > b.Raw ? a : b;
        public static Fix Abs(Fix v) => v.Raw < 0 ? new Fix(-v.Raw) : v;
        public static Fix Clamp(Fix v, Fix min, Fix max) => v.Raw < min.Raw ? min : (v.Raw > max.Raw ? max : v);

        /// <summary>Bitwise integer square root — exact, no floating point.</summary>
        public static Fix Sqrt(Fix value)
        {
            if (value.Raw <= 0) return Zero;
            ulong x = (ulong)value.Raw << FracBits;
            ulong result = 0;
            ulong bit = 1UL << 62;
            while (bit > x) bit >>= 2;
            while (bit != 0)
            {
                if (x >= result + bit) { x -= result + bit; result = (result >> 1) + bit; }
                else result >>= 1;
                bit >>= 2;
            }
            return new Fix((long)result);
        }

        public bool Equals(Fix other) => Raw == other.Raw;
        public override bool Equals(object obj) => obj is Fix f && f.Raw == Raw;
        public override int GetHashCode() => Raw.GetHashCode();
        public int CompareTo(Fix other) => Raw.CompareTo(other.Raw);

        // Debug/display only — never feeds back into sim state.
        public override string ToString() => (Raw / (double)OneRaw).ToString("0.####");
    }

    /// <summary>2D fixed-point vector.</summary>
    public readonly struct FixVec2 : IEquatable<FixVec2>
    {
        public readonly Fix X;
        public readonly Fix Y;

        public FixVec2(Fix x, Fix y) { X = x; Y = y; }

        public static readonly FixVec2 Zero = new FixVec2(Fix.Zero, Fix.Zero);

        public static FixVec2 operator +(FixVec2 a, FixVec2 b) => new FixVec2(a.X + b.X, a.Y + b.Y);
        public static FixVec2 operator -(FixVec2 a, FixVec2 b) => new FixVec2(a.X - b.X, a.Y - b.Y);
        public static FixVec2 operator *(FixVec2 v, Fix s) => new FixVec2(v.X * s, v.Y * s);

        public Fix LengthSq => X * X + Y * Y;
        public Fix Length => Fix.Sqrt(LengthSq);

        /// <summary>Step from 'from' toward 'to' by at most maxStep; snaps exactly on arrival.</summary>
        public static FixVec2 MoveTowards(FixVec2 from, FixVec2 to, Fix maxStep, out bool arrived)
        {
            FixVec2 delta = to - from;
            Fix len = delta.Length;
            if (len <= maxStep) { arrived = true; return to; }
            arrived = false;
            return from + new FixVec2(delta.X * maxStep / len, delta.Y * maxStep / len);
        }

        /// <summary>
        /// Rotate the unit vector 'facing' toward 'desired' (any length) by at most maxChord —
        /// chord length on the unit circle per step (1.0 ≈ 60°), so turn rate is data-tunable.
        /// Trig-free and deterministic: step along the chord, renormalize back onto the circle;
        /// snaps exactly when close. Exactly-opposite headings break symmetry via a fixed
        /// perpendicular. Returns 'facing' unchanged when 'desired' is zero.
        /// </summary>
        public static FixVec2 TurnTowards(FixVec2 facing, FixVec2 desired, Fix maxChord)
        {
            Fix dLen = desired.Length;
            if (dLen.Raw == 0) return facing;
            var d = new FixVec2(desired.X / dLen, desired.Y / dLen);
            FixVec2 delta = d - facing;
            Fix len = delta.Length;
            if (len <= maxChord) return d;
            FixVec2 v = facing + new FixVec2(delta.X * maxChord / len, delta.Y * maxChord / len);
            Fix vLen = v.Length;
            if (vLen.Raw == 0) return new FixVec2(-facing.Y, facing.X); // 180°: deterministic side
            return new FixVec2(v.X / vLen, v.Y / vLen);
        }

        public bool Equals(FixVec2 other) => X == other.X && Y == other.Y;
        public override bool Equals(object obj) => obj is FixVec2 v && Equals(v);
        public override int GetHashCode() => X.GetHashCode() * 397 ^ Y.GetHashCode();
        public override string ToString() => "(" + X + ", " + Y + ")";
    }
}
