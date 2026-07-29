using Petri.Core;

namespace Petri.Client
{
    /// <summary>
    /// Client-side fog of war for the human player. DERIVED data only: rebuilt from sim
    /// positions on a view cadence and never fed back into the sim, so determinism is
    /// untouched. Two layers over a 1-world-unit cell grid: current VISIBILITY (any own
    /// unit/building within its vision range, radii from rules.json) and persistent
    /// EXPLORATION memory (view-side — what this client has seen, not the sim's business).
    /// </summary>
    public sealed class VisionMap
    {
        public int CellsX { get; private set; }
        public int CellsY { get; private set; }

        private byte[] _visible;
        private byte[] _explored;

        public void Configure(MapDef map)
        {
            CellsX = System.Math.Max(1, map.WidthCenti / 100);
            CellsY = System.Math.Max(1, map.HeightCenti / 100);
            _visible = new byte[CellsX * CellsY];
            _explored = new byte[CellsX * CellsY];
        }

        /// <summary>Recompute current visibility: the organism SEES from its territory —
        /// every cell the player's team owns stamps a small vision circle (the border
        /// effectively projects a short sight margin) — plus team buildings.</summary>
        public void Rebuild(SimWorld w, DefDatabase defs, byte player)
        {
            System.Array.Clear(_visible, 0, _visible.Length);
            float margin = w.Rules.UnitVisionRangeCenti / 100f * 0.5f;
            for (int c = 0; c < w.Territory.Length; c++)
            {
                byte o = w.Territory[c];
                if (o >= w.Players.Length || !w.IsFriendly(player, o)) continue;
                w.CellCenterCenti(c, out int ccx, out int ccy);
                StampCircle(ccx / 100f, ccy / 100f, margin);
            }
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Building || !w.IsFriendly(player, w.Owner[i])) continue;
                StampCircle(w.Pos[i].X.Raw / (float)Fix.OneRaw, w.Pos[i].Y.Raw / (float)Fix.OneRaw,
                    w.Rules.BuildingVisionRangeCenti / 100f);
            }
        }

        private void StampCircle(float cx, float cy, float r)
        {
            int x0 = ClampX((int)(cx - r)), x1 = ClampX((int)(cx + r));
            int y0 = ClampY((int)(cy - r)), y1 = ClampY((int)(cy + r));
            float rsq = r * r;
            for (int y = y0; y <= y1; y++)
            {
                float dy = y + 0.5f - cy;
                int row = y * CellsX;
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x + 0.5f - cx;
                    if (dx * dx + dy * dy > rsq) continue;
                    _visible[row + x] = 1;
                    _explored[row + x] = 1;
                }
            }
        }

        public bool VisibleCell(int x, int y) => _visible[y * CellsX + x] != 0;
        public bool ExploredCell(int x, int y) => _explored[y * CellsX + x] != 0;

        public bool VisibleAt(float x, float y) => _visible[ClampY((int)y) * CellsX + ClampX((int)x)] != 0;
        public bool ExploredAt(float x, float y) => _explored[ClampY((int)y) * CellsX + ClampX((int)x)] != 0;

        private int ClampX(int x) => x < 0 ? 0 : (x >= CellsX ? CellsX - 1 : x);
        private int ClampY(int y) => y < 0 ? 0 : (y >= CellsY ? CellsY - 1 : y);
    }
}
