namespace Petri.Core
{
    /// <summary>
    /// Sector classification for superorganism fronts. The organism's border is divided
    /// into K equal angular wedges around its centroid; sector i is CENTERED on the
    /// direction i*360/K degrees CCW from +x, so at K = 4 the fronts face E, N, W, S.
    /// Boundary rays are hardcoded integer (cos, sin)x1000 tables — pure integer cross
    /// products, no trig at runtime, identical on every peer. Shared by sim, bot, AND the
    /// client (engine-free), so everyone classifies a cell into the same front.
    /// </summary>
    public static class FrontMath
    {
        /// <summary>Table index for a legal K, or -1.</summary>
        public static int KIndex(int k)
        {
            for (int i = 0; i < SimConstants.FrontCounts.Length; i++)
                if (SimConstants.FrontCounts[i] == k) return i;
            return -1;
        }

        /// <summary>
        /// Which of the K sectors the direction (dx, dy) falls in. Convention: sector s iff
        /// cross(ray_s, d) >= 0 AND cross(ray_{s+1 mod K}, d) &lt; 0 (wedges are &lt;= 90
        /// degrees for every legal K, so the two-sided test is exact); numeric edge cases
        /// fall back to K-1. (0,0) is sector 0. All math in long.
        /// </summary>
        public static int Sector(int k, long dx, long dy)
        {
            int ki = KIndex(k);
            if (ki < 0) return 0;
            if (dx == 0 && dy == 0) return 0;
            int[] rx = RayX[ki], ry = RayY[ki];
            for (int s = 0; s < k; s++)
            {
                int n = s + 1 == k ? 0 : s + 1;
                long crossA = rx[s] * dy - ry[s] * dx;
                long crossB = rx[n] * dy - ry[n] * dx;
                if (crossA >= 0 && crossB < 0) return s;
            }
            return k - 1;
        }

        // Boundary rays: ray_i at angle (2i-1)*180/K degrees, (cos, sin) x 1000.
        // Generated offline; do not edit by hand-recomputation — regenerate wholesale.
        internal static readonly int[][] RayX =
        {
            new[] { 707, 707, -707, -707 },                                                   // K=4
            new[] { 866, 866, 0, -866, -866, 0 },                                             // K=6
            new[] { 924, 924, 383, -383, -924, -924, -383, 383 },                             // K=8
            new[] { 966, 966, 707, 259, -259, -707, -966, -966, -707, -259, 259, 707 },       // K=12
            new[] { 988, 988, 891, 707, 454, 156, -156, -454, -707, -891,
                    -988, -988, -891, -707, -454, -156, 156, 454, 707, 891 },                 // K=20
            new[] { 997, 997, 972, 924, 853, 760, 649, 522, 383, 233,
                    78, -78, -233, -383, -522, -649, -760, -853, -924, -972,
                    -997, -997, -972, -924, -853, -760, -649, -522, -383, -233,
                    -78, 78, 233, 383, 522, 649, 760, 853, 924, 972 },                        // K=40
        };

        internal static readonly int[][] RayY =
        {
            new[] { -707, 707, 707, -707 },                                                   // K=4
            new[] { -500, 500, 1000, 500, -500, -1000 },                                      // K=6
            new[] { -383, 383, 924, 924, 383, -383, -924, -924 },                             // K=8
            new[] { -259, 259, 707, 966, 966, 707, 259, -259, -707, -966, -966, -707 },       // K=12
            new[] { -156, 156, 454, 707, 891, 988, 988, 891, 707, 454,
                    156, -156, -454, -707, -891, -988, -988, -891, -707, -454 },              // K=20
            new[] { -78, 78, 233, 383, 522, 649, 760, 853, 924, 972,
                    997, 997, 972, 924, 853, 760, 649, 522, 383, 233,
                    78, -78, -233, -383, -522, -649, -760, -853, -924, -972,
                    -997, -997, -972, -924, -853, -760, -649, -522, -383, -233 },             // K=40
        };
    }
}
