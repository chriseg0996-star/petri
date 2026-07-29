using System.Collections.Generic;
using UnityEngine;
using Petri.Core;

namespace Petri.Client
{
    /// <summary>
    /// Renders the superorganism world: buildings, resource nodes, terrain walls, fog, and
    /// transient effects (pings, death pops, the build ghost). The territory overlay itself
    /// lands with the territory-rendering pass; entities never move, so there is no render
    /// interpolation. The view is a pure projection of sim state and never writes back.
    /// </summary>
    public sealed class GameView : MonoBehaviour
    {
        private static readonly Color[] OwnerColors = BuildPalette(32);

        /// <summary>Colour for an owner slot (wraps, so any owner index is safe).</summary>
        public static Color OwnerColor(int owner) => OwnerColors[owner % OwnerColors.Length];

        private static Color[] BuildPalette(int n)
        {
            var seeds = new[]
            {
                new Color(0.35f, 0.75f, 1.00f), // P0 cyan
                new Color(1.00f, 0.42f, 0.36f), // P1 red
                new Color(1.00f, 0.88f, 0.35f), // P2 yellow
                new Color(0.75f, 0.50f, 1.00f), // P3 purple
                new Color(1.00f, 0.58f, 0.20f), // P4 orange
                new Color(1.00f, 0.55f, 0.85f), // P5 pink
                new Color(0.25f, 0.85f, 0.80f), // P6 teal
                new Color(0.82f, 0.84f, 0.88f), // P7 silver
            };
            var arr = new Color[n];
            for (int i = 0; i < n; i++)
            {
                if (i < seeds.Length) { arr[i] = seeds[i]; continue; }
                float hue = (i * 0.6180339887f) % 1f;                 // golden angle: maximal spread
                float sat = (i % 2) == 0 ? 0.55f : 0.80f;             // alternate for extra contrast
                arr[i] = Color.HSVToRGB(hue, sat, (i % 3) == 0 ? 0.85f : 1f);
            }
            return arr;
        }

        // Resource identity colours — ONE source of truth. The nodes on the map, the minimap
        // blips and every HUD readout all key off these, so a number's colour always tells you
        // which resource it is.
        public static readonly Color NutrientColor = new Color(0.62f, 0.88f, 0.48f); // green
        public static readonly Color MineralColor = new Color(0.54f, 0.71f, 1.00f);  // steel blue
        public static readonly Color EvoColor = new Color(0.84f, 0.61f, 1.00f);      // violet

        private static readonly Color SelectionColor = Color.white;
        public static readonly Color MovePing = new Color(0.4f, 1f, 0.5f);
        public static readonly Color RallyPing = new Color(1f, 0.85f, 0.3f);
        public static readonly Color AttackPing = new Color(1f, 0.35f, 0.3f);

        private const float PingSeconds = 0.6f;
        private const float PopSeconds = 0.4f;

        private struct PingFx { public Vector3 Pos; public float Start; public Color Color; }
        private struct PopFx { public Vector3 Pos; public float Start; public Color Color; public float Size; }

        private MatchBootstrap _match;
        private Sprite _disc, _ring, _thinRing, _square, _squareOutline, _arrow, _diamond;
        private Sprite _triangle, _pentagon, _hexagon, _octagon, _star, _kite, _cross,
            _crescent, _bullseye, _diamondOutline;
        private Sprite[] _digits;
        private Sprite[] _buildingShape; // per-def silhouettes so buildings read at a glance

        // Territory overlay: one texture pixel per 2u territory cell, under everything.
        private Texture2D _terrTex;
        private SpriteRenderer _terrRenderer;
        private Color32[] _terrPixels;
        private float _terrNext;
        private const float TerritoryInterval = 0.12f;

        // Per-front label anchors: the mean of the human organism's border cells in each
        // sector, refreshed with the territory texture. Labels draw every frame from these.
        private readonly float[] _frontLabelX = new float[SimConstants.MaxFronts];
        private readonly float[] _frontLabelY = new float[SimConstants.MaxFronts];
        private readonly int[] _frontLabelN = new int[SimConstants.MaxFronts];

        // Fog of war (client-side; null when disabled in the skirmish setup).
        public VisionMap Vision { get; private set; }
        private Texture2D _fogTex;
        private SpriteRenderer _fogRenderer;
        private Color32[] _fogPixels;
        private float _fogNext;
        private const float FogInterval = 0.12f;
        private static readonly Color32 FogUnseen = new Color32(5, 8, 5, 243);
        private static readonly Color32 FogExplored = new Color32(5, 8, 5, 120);
        private static readonly Color32 FogVisible = new Color32(0, 0, 0, 0);

        private readonly List<SpriteRenderer> _bodies = new List<SpriteRenderer>();
        private readonly List<SpriteRenderer> _overlays = new List<SpriteRenderer>();
        private readonly List<PingFx> _pings = new List<PingFx>();
        private readonly List<PopFx> _pops = new List<PopFx>();
        private SpriteRenderer _ghost;
        private float[] _selFade;

        // Last rendered frame's snapshot, for death pops: a slot that held a building and is
        // now empty (or a different generation) just died there.
        private EntityKind[] _prevKind;
        private int[] _prevGen;
        private byte[] _prevOwner;
        private Vector3[] _prevPos;
        private float[] _prevSize;
        private int _prevHigh;

        public void Bind(MatchBootstrap match)
        {
            _match = match;
            BuildSpriteAtlas();

            // Terrain walls never move: one persistent renderer each (dark body + rim).
            var world = match.Sim.World;
            for (int k = 0; k < world.WallPos.Length; k++)
            {
                float d = world.WallRadius[k].Raw / (float)Fix.OneRaw * 2f;
                var pos = new Vector3(world.WallPos[k].X.Raw / (float)Fix.OneRaw,
                                      world.WallPos[k].Y.Raw / (float)Fix.OneRaw, 0f);
                var go = new GameObject("wall" + k);
                go.transform.SetParent(transform);
                go.transform.position = pos;
                go.transform.localScale = new Vector3(d, d, 1f);
                var body = go.AddComponent<SpriteRenderer>();
                body.sprite = _disc;
                body.color = new Color(0.20f, 0.23f, 0.20f, 1f);
                body.sortingOrder = 0;
                var rimGo = new GameObject("wallrim" + k);
                rimGo.transform.SetParent(transform);
                rimGo.transform.position = pos;
                rimGo.transform.localScale = new Vector3(d, d, 1f);
                var rim = rimGo.AddComponent<SpriteRenderer>();
                rim.sprite = _ring;
                rim.color = new Color(0.34f, 0.38f, 0.33f, 0.9f);
                rim.sortingOrder = 1;
            }

            // The organisms themselves: a low texture where each pixel is one 2u territory
            // cell, rendered UNDER walls and entities. Bilinear filtering rounds the cell
            // edges into soft organic borders for free.
            _terrTex = new Texture2D(world.TerritoryCellsX, world.TerritoryCellsY, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
            };
            _terrPixels = new Color32[world.TerritoryCellsX * world.TerritoryCellsY];
            var terrGo = new GameObject("territory");
            terrGo.transform.SetParent(transform);
            _terrRenderer = terrGo.AddComponent<SpriteRenderer>();
            _terrRenderer.sprite = Sprite.Create(_terrTex,
                new Rect(0, 0, world.TerritoryCellsX, world.TerritoryCellsY), Vector2.zero,
                100f / SimWorld.CellCenti); // 1 px per cell → cell-size world units per px
            _terrRenderer.transform.position = Vector3.zero;
            _terrRenderer.sortingOrder = -2;

            if (MatchBootstrap.PendingFog)
            {
                Vision = new VisionMap();
                Vision.Configure(match.Map);
                _fogTex = new Texture2D(Vision.CellsX, Vision.CellsY, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
                };
                _fogPixels = new Color32[Vision.CellsX * Vision.CellsY];
                var fogGo = new GameObject("fog");
                fogGo.transform.SetParent(transform);
                _fogRenderer = fogGo.AddComponent<SpriteRenderer>();
                _fogRenderer.sprite = Sprite.Create(_fogTex, new Rect(0, 0, Vision.CellsX, Vision.CellsY), Vector2.zero, 1f);
                _fogRenderer.transform.position = Vector3.zero;
                _fogRenderer.sortingOrder = 20; // blankets everything in the world
            }

            int cap = match.Sim.World.Capacity;
            _prevKind = new EntityKind[cap];
            _prevGen = new int[cap];
            _prevOwner = new byte[cap];
            _prevPos = new Vector3[cap];
            _prevSize = new float[cap];
            _selFade = new float[cap];
        }

        /// <summary>Pack every runtime-generated shape into ONE atlas texture so sprites
        /// share a material and batch.</summary>
        private void BuildSpriteAtlas()
        {
            var texs = new Texture2D[27];
            texs[0] = MakeDiscTex(64);
            texs[1] = MakeRingTex(64, 0.80f);
            texs[2] = MakeSquareTex(8);
            texs[3] = MakeSquareOutlineTex(64, 6);
            texs[4] = MakeArrowTex(32);
            texs[5] = MakeDiamondTex(64);
            texs[6] = MakeRingTex(256, 0.970f); // hairline ring for large radii
            for (int d = 0; d < 10; d++) texs[7 + d] = MakeDigitTex(d);
            texs[17] = MakeRegularPolyTex(64, 3, 90f);
            texs[18] = MakeRegularPolyTex(64, 5, 90f);
            texs[19] = MakeRegularPolyTex(64, 6, 0f);
            texs[20] = MakeRegularPolyTex(64, 8, 22.5f);
            texs[21] = MakeStarTex(64, 5, 0.45f);
            texs[22] = MakeKiteTex(64);
            texs[23] = MakeCrossTex(64, 0.36f);
            texs[24] = MakeCrescentTex(64);
            texs[25] = MakeBullseyeTex(64);
            texs[26] = MakeDiamondOutlineTex(64, 0.55f);

            var atlas = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            Rect[] uv = atlas.PackTextures(texs, 4, 1024);
            atlas.Apply();

            _disc = FromAtlas(atlas, uv[0]);
            _ring = FromAtlas(atlas, uv[1]);
            _square = FromAtlas(atlas, uv[2]);
            _squareOutline = FromAtlas(atlas, uv[3]);
            _arrow = FromAtlas(atlas, uv[4]);
            _diamond = FromAtlas(atlas, uv[5]);
            _thinRing = FromAtlas(atlas, uv[6]);
            _digits = new Sprite[10];
            for (int d = 0; d < 10; d++) _digits[d] = FromAtlas(atlas, uv[7 + d]);
            _triangle = FromAtlas(atlas, uv[17]);
            _pentagon = FromAtlas(atlas, uv[18]);
            _hexagon = FromAtlas(atlas, uv[19]);
            _octagon = FromAtlas(atlas, uv[20]);
            _star = FromAtlas(atlas, uv[21]);
            _kite = FromAtlas(atlas, uv[22]);
            _cross = FromAtlas(atlas, uv[23]);
            _crescent = FromAtlas(atlas, uv[24]);
            _bullseye = FromAtlas(atlas, uv[25]);
            _diamondOutline = FromAtlas(atlas, uv[26]);

            foreach (var t in texs) Destroy(t);

            var defs = _match.Defs;
            _buildingShape = new Sprite[defs.Buildings.Length];
            for (int b = 0; b < defs.Buildings.Length; b++)
            {
                _buildingShape[b] = defs.Buildings[b].Id switch
                {
                    "strain.nucleoid" => _octagon,
                    "strain.incubator" => _square,
                    "strain.mutagen-pool" => _ring,
                    "strain.sentinel-spire" => _triangle,
                    "strain.lysis-chamber" => _pentagon,
                    "strain.flagella-bay" => _kite,
                    "strain.toxin-gland" => _cross,
                    "strain.capsule-foundry" => _hexagon,
                    "strain.spike-battery" => _star,
                    "strain.burrow-node" => _bullseye,
                    "strain.chitin-rampart" => _squareOutline,
                    "strain.brood-sac" => _crescent,
                    "strain.plasmid-reliquary" => _diamondOutline,
                    _ => _square,
                };
            }
        }

        private static Sprite FromAtlas(Texture2D atlas, Rect uvRect)
        {
            var px = new Rect(uvRect.x * atlas.width, uvRect.y * atlas.height,
                              uvRect.width * atlas.width, uvRect.height * atlas.height);
            return Sprite.Create(atlas, px, new Vector2(0.5f, 0.5f), Mathf.Max(1f, px.height));
        }

        /// <summary>Show a brief expanding ping at a world position (order feedback).</summary>
        public void Ping(Vector3 worldPos, Color color) =>
            _pings.Add(new PingFx { Pos = worldPos, Start = Time.time, Color = color });

        private void LateUpdate()
        {
            if (_match == null || _match.Sim == null) return;
            var w = _match.Sim.World;
            var defs = _match.Defs;

            UpdateFog(w, defs);
            UpdateTerritory(w);

            int body = 0, overlay = 0;
            var input = _match.Input;
            var selected = input != null ? input.Selected : null;

            // ---- Death pops: buildings that vanished since the last frame burst.
            for (int i = 0; i < _prevHigh; i++)
            {
                if (_prevKind[i] != EntityKind.Building) continue;
                bool gone = i >= w.HighWater || w.Kind[i] != _prevKind[i] || w.Generation[i] != _prevGen[i];
                if (!gone) continue;
                if (Vision != null && !Vision.VisibleAt(_prevPos[i].x, _prevPos[i].y)) continue;
                _pops.Add(new PopFx
                {
                    Pos = _prevPos[i], Start = Time.time,
                    Color = OwnerColor(_prevOwner[i]), Size = _prevSize[i],
                });
            }

            // ---- Entities: buildings and nodes.
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] == EntityKind.None) continue;
                float x = w.Pos[i].X.Raw / (float)Fix.OneRaw;
                float y = w.Pos[i].Y.Raw / (float)Fix.OneRaw;

                // Fog culling: friendly always shows; enemy buildings and neutral nodes
                // appear once explored (static — safe to remember).
                if (Vision != null && !w.IsFriendly(MatchBootstrap.HumanPlayer, w.Owner[i]))
                {
                    if (!Vision.ExploredAt(x, y)) continue;
                }

                float radius = w.Kind[i] == EntityKind.Building
                    ? defs.Buildings[w.DefIndex[i]].CollisionRadiusCenti / 100f
                    : w.Rules.NodeRadiusCenti / 100f;
                float diameter = Mathf.Max(0.2f, radius * 2f);

                var sr = Rent(_bodies, ref body);
                sr.transform.position = new Vector3(x, y, 0f);
                sr.transform.localScale = new Vector3(diameter, diameter, 1f);

                if (w.Kind[i] == EntityKind.Node)
                {
                    sr.sprite = _disc;
                    sr.color = w.NodeMineral[i] ? MineralColor : NutrientColor;
                    sr.sortingOrder = 1;
                }
                else
                {
                    sr.sprite = _buildingShape[w.DefIndex[i]];
                    var bc = Dim(OwnerColor(w.Owner[i]), 0.75f);
                    if (w.ConstructionRemaining[i] > 0) bc.a = 0.45f; // translucent site
                    sr.color = bc;
                    sr.sortingOrder = 2;

                    // The NUCLEUS is the organism's heart — crown it with a bright star so
                    // both cores read instantly at any zoom.
                    if (defs.Buildings[w.DefIndex[i]].IsHeadquarters)
                    {
                        var star = Rent(_overlays, ref overlay);
                        star.sprite = _star;
                        star.transform.position = new Vector3(x, y, 0f);
                        float ss = diameter * 0.62f;
                        star.transform.localScale = new Vector3(ss, ss, 1f);
                        star.color = Color.Lerp(OwnerColor(w.Owner[i]), Color.white, 0.65f);
                        star.sortingOrder = 3;
                    }
                }

                // Health bar for damaged buildings.
                if (w.Kind[i] == EntityKind.Building)
                {
                    int maxHp = defs.Buildings[w.DefIndex[i]].MaxHp;
                    if (maxHp > 0 && w.Hp[i] > 0 && w.Hp[i] < maxHp)
                    {
                        float frac = w.Hp[i] / (float)maxHp;
                        float barW = Mathf.Max(0.55f, diameter);
                        const float barH = 0.10f;
                        float barY = y + diameter * 0.5f + 0.18f;

                        var bg = Rent(_overlays, ref overlay);
                        bg.sprite = _square;
                        bg.transform.position = new Vector3(x, barY, 0f);
                        bg.transform.localScale = new Vector3(barW, barH, 1f);
                        bg.color = new Color(0.16f, 0.04f, 0.04f, 0.9f);
                        bg.sortingOrder = 9;

                        var fill = Rent(_overlays, ref overlay);
                        fill.sprite = _square;
                        fill.transform.position = new Vector3(x - barW * 0.5f + barW * frac * 0.5f, barY, 0f);
                        fill.transform.localScale = new Vector3(barW * frac, barH * 0.75f, 1f);
                        fill.color = Color.Lerp(new Color(0.95f, 0.25f, 0.15f), new Color(0.25f, 0.9f, 0.3f), frac);
                        fill.sortingOrder = 10;
                    }
                }

                // Selection outline eases in on select and out on deselect.
                bool isSel = selected != null && selected.Contains(i);
                _selFade[i] = Mathf.MoveTowards(_selFade[i], isSel ? 1f : 0f, Time.deltaTime / 0.10f);
                if (_selFade[i] > 0.01f)
                {
                    var so = Rent(_overlays, ref overlay);
                    so.transform.position = new Vector3(x, y, 0f);
                    so.sprite = w.Kind[i] == EntityKind.Building ? _squareOutline : _ring;
                    float rest = w.Kind[i] == EntityKind.Building ? 1.06f : 1.15f;
                    float sel = diameter * Mathf.Lerp(rest + 0.14f, rest, _selFade[i]);
                    so.transform.localScale = new Vector3(sel, sel, 1f);
                    var selc = SelectionColor; selc.a = _selFade[i];
                    so.color = selc;
                    so.sortingOrder = 6;
                }
            }

            // ---- Pings.
            for (int k = _pings.Count - 1; k >= 0; k--)
            {
                float t = (Time.time - _pings[k].Start) / PingSeconds;
                if (t >= 1f) { _pings.RemoveAt(k); continue; }
                var pr = Rent(_overlays, ref overlay);
                pr.sprite = _ring;
                pr.transform.position = _pings[k].Pos;
                float s = Mathf.Lerp(0.25f, 1.6f, t);
                pr.transform.localScale = new Vector3(s, s, 1f);
                var c = _pings[k].Color; c.a = 1f - t;
                pr.color = c;
                pr.sortingOrder = 7;
            }

            // ---- Death pops.
            for (int k = _pops.Count - 1; k >= 0; k--)
            {
                float t = (Time.time - _pops[k].Start) / PopSeconds;
                if (t >= 1f) { _pops.RemoveAt(k); continue; }
                var pr = Rent(_overlays, ref overlay);
                pr.sprite = _ring;
                pr.transform.position = _pops[k].Pos;
                float s = _pops[k].Size * Mathf.Lerp(1f, 2.2f, t);
                pr.transform.localScale = new Vector3(s, s, 1f);
                var c = _pops[k].Color; c.a = 0.85f * (1f - t) * (1f - t);
                pr.color = c;
                pr.sortingOrder = 8;
            }

            DrawFrontMarkers(w, ref overlay);
            DrawGhost(w, defs, input);

            // Snapshot for next frame's death detection; disable the pool tails.
            for (int i = 0; i < w.HighWater; i++)
            {
                _prevKind[i] = w.Kind[i];
                _prevGen[i] = w.Generation[i];
                _prevOwner[i] = w.Owner[i];
                _prevPos[i] = new Vector3(w.Pos[i].X.Raw / (float)Fix.OneRaw, w.Pos[i].Y.Raw / (float)Fix.OneRaw, 0f);
                _prevSize[i] = w.Kind[i] == EntityKind.Building
                    ? defs.Buildings[w.DefIndex[i]].CollisionRadiusCenti / 100f * 2f : 0.5f;
            }
            _prevHigh = w.HighWater;
            for (int i = body; i < _bodies.Count; i++) _bodies[i].enabled = false;
            for (int i = overlay; i < _overlays.Count; i++) _overlays[i].enabled = false;
        }

        /// <summary>Rebuild the territory overlay on a cadence: owner-tinted fill, brighter
        /// borders, and a shimmer where a border touches an enemy (a live front). Fog-gated:
        /// unexplored ground stays blank, explored-but-dark ground renders dimmed.</summary>
        private void UpdateTerritory(SimWorld w)
        {
            if (_terrTex == null || Time.time < _terrNext) return;
            _terrNext = Time.time + TerritoryInterval;
            int cw = w.TerritoryCellsX, ch = w.TerritoryCellsY;
            int players = w.Players.Length;
            int selFront = _match.Input != null ? _match.Input.SelectedFront : -1;
            // The contested pulse: one shared phase, re-sampled each rebuild (~8 Hz shimmer).
            float pulse = Mathf.Lerp(0.55f, 0.95f, 0.5f + 0.5f * Mathf.Sin(Time.time * 5f));
            var clear = new Color32(0, 0, 0, 0);
            System.Array.Clear(_frontLabelX, 0, _frontLabelX.Length);
            System.Array.Clear(_frontLabelY, 0, _frontLabelY.Length);
            System.Array.Clear(_frontLabelN, 0, _frontLabelN.Length);

            for (int y = 0; y < ch; y++)
            {
                int row = y * cw;
                for (int x = 0; x < cw; x++)
                {
                    int c = row + x;
                    byte o = w.Territory[c];
                    if (o >= players) { _terrPixels[c] = clear; continue; }
                    float wx = (x * SimWorld.CellCenti + SimWorld.CellCenti / 2) / 100f;
                    float wy = (y * SimWorld.CellCenti + SimWorld.CellCenti / 2) / 100f;
                    if (Vision != null && !Vision.ExploredAt(wx, wy)) { _terrPixels[c] = clear; continue; }

                    bool border = false, contested = false;
                    if (x > 0 && Check(w, o, w.Territory[c - 1], ref contested)) border = true;
                    if (x < cw - 1 && Check(w, o, w.Territory[c + 1], ref contested)) border = true;
                    if (y > 0 && Check(w, o, w.Territory[c - cw], ref contested)) border = true;
                    if (y < ch - 1 && Check(w, o, w.Territory[c + cw], ref contested)) border = true;

                    var col = OwnerColor(o);
                    float a = contested ? pulse : border ? 0.8f : 0.35f;
                    if (o == MatchBootstrap.HumanPlayer)
                    {
                        if (border)
                        {
                            // Border cells anchor this sector's front label.
                            int s = w.ScratchCellSector[c];
                            _frontLabelX[s] += wx;
                            _frontLabelY[s] += wy;
                            _frontLabelN[s]++;
                        }
                        if (selFront >= 0 && w.ScratchCellSector[c] == selFront)
                        {
                            // The SELECTED front's whole wedge lifts toward white.
                            col = Color.Lerp(col, Color.white, 0.45f);
                            if (a < 0.5f) a = 0.5f;
                        }
                    }
                    if (Vision != null && !Vision.VisibleAt(wx, wy)) a *= 0.6f; // remembered, not seen
                    _terrPixels[c] = new Color32((byte)(col.r * 255f), (byte)(col.g * 255f),
                        (byte)(col.b * 255f), (byte)(a * 255f));
                }
            }
            _terrTex.SetPixels32(_terrPixels);
            _terrTex.Apply(false);

            static bool Check(SimWorld w, byte owner, byte other, ref bool contested)
            {
                if (other == owner) return false;
                if (other < w.Players.Length && w.AreEnemies(owner, other)) contested = true;
                return true;
            }
        }

        /// <summary>Front number labels on the border plus push-target rings, drawn from the
        /// overlay pool every frame. White = selected, gold = pushing, red pulse = broken.</summary>
        private void DrawFrontMarkers(SimWorld w, ref int overlay)
        {
            var pl = w.Players[MatchBootstrap.HumanPlayer];
            if (!pl.Alive) return;
            int selFront = _match.Input != null ? _match.Input.SelectedFront : -1;

            for (int f = 0; f < pl.FrontCount; f++)
            {
                bool pushing = pl.FrontPushX[f] >= 0;
                bool broken = pl.FrontBrokenTicks[f] > 0;
                Color col = f == selFront ? Color.white
                    : broken ? AttackPing
                    : pushing ? RallyPing
                    : new Color(0.75f, 0.8f, 0.75f, 0.65f);
                if (broken) col.a = Mathf.Lerp(0.4f, 1f, 0.5f + 0.5f * Mathf.Sin(Time.time * 8f));

                if (_frontLabelN[f] > 0)
                {
                    float lx = _frontLabelX[f] / _frontLabelN[f];
                    float ly = _frontLabelY[f] / _frontLabelN[f];
                    // Human-facing fronts are 1-based; two digit sprites cover up to 40.
                    int label = f + 1;
                    if (label >= 10)
                    {
                        DrawDigit(label / 10, lx - 0.45f, ly, col, ref overlay);
                        DrawDigit(label % 10, lx + 0.45f, ly, col, ref overlay);
                    }
                    else DrawDigit(label, lx, ly, col, ref overlay);
                }

                if (pushing)
                {
                    var ring = Rent(_overlays, ref overlay);
                    ring.sprite = _thinRing;
                    ring.transform.position = new Vector3(pl.FrontPushX[f] / 100f, pl.FrontPushY[f] / 100f, 0f);
                    float s = 1.8f + 0.25f * Mathf.Sin(Time.time * 4f);
                    ring.transform.localScale = new Vector3(s, s, 1f);
                    ring.color = f == selFront ? Color.white : RallyPing;
                    ring.sortingOrder = 5;
                }
            }
        }

        private void DrawDigit(int digit, float x, float y, Color col, ref int overlay)
        {
            var d = Rent(_overlays, ref overlay);
            d.sprite = _digits[digit];
            d.transform.position = new Vector3(x, y, 0f);
            d.transform.localScale = new Vector3(1.3f, 1.3f, 1f);
            d.color = col;
            d.sortingOrder = 5;
        }

        private void UpdateFog(SimWorld w, DefDatabase defs)
        {
            if (Vision == null || Time.time < _fogNext) return;
            _fogNext = Time.time + FogInterval;
            Vision.Rebuild(w, defs, (byte)MatchBootstrap.HumanPlayer);
            int cw = Vision.CellsX, ch = Vision.CellsY;
            for (int y = 0; y < ch; y++)
            {
                int row = y * cw;
                for (int x = 0; x < cw; x++)
                    _fogPixels[row + x] = Vision.VisibleCell(x, y) ? FogVisible
                        : Vision.ExploredCell(x, y) ? FogExplored : FogUnseen;
            }
            _fogTex.SetPixels32(_fogPixels);
            _fogTex.Apply(false);
        }

        private void DrawGhost(SimWorld w, DefDatabase defs, InputController input)
        {
            bool placing = input != null && input.PlacingBuilding >= 0;
            if (_ghost == null)
            {
                var go = new GameObject("ghost");
                go.transform.SetParent(transform);
                _ghost = go.AddComponent<SpriteRenderer>();
                _ghost.sprite = _square;
                _ghost.sortingOrder = 8;
            }
            _ghost.enabled = placing;
            if (!placing) return;

            var cam = Camera.main;
            Vector3 wp = cam.ScreenToWorldPoint(new Vector3(Input.mousePosition.x, Input.mousePosition.y, -cam.transform.position.z));
            var bdef = defs.Buildings[input.PlacingBuilding];
            float d = bdef.CollisionRadiusCenti / 100f * 2f;
            _ghost.sprite = _buildingShape[input.PlacingBuilding];
            _ghost.transform.position = new Vector3(wp.x, wp.y, 0f);
            _ghost.transform.localScale = new Vector3(d, d, 1f);
            // Rough validity preview: the cell under the cursor must be yours.
            int cell = w.CellOfCenti(Mathf.RoundToInt(wp.x * 100f), Mathf.RoundToInt(wp.y * 100f));
            bool ok = cell >= 0 && cell < w.Territory.Length
                && w.Territory[cell] == MatchBootstrap.HumanPlayer && !w.TerritoryBlocked[cell];
            _ghost.color = ok ? new Color(0.3f, 1f, 0.4f, 0.4f) : new Color(1f, 0.25f, 0.2f, 0.45f);
        }

        private SpriteRenderer Rent(List<SpriteRenderer> pool, ref int cursor)
        {
            SpriteRenderer sr;
            if (cursor < pool.Count) sr = pool[cursor];
            else
            {
                var go = new GameObject("spr");
                go.transform.SetParent(transform);
                sr = go.AddComponent<SpriteRenderer>();
                pool.Add(sr);
            }
            sr.enabled = true;
            sr.transform.rotation = Quaternion.identity;
            cursor++;
            return sr;
        }

        private static Color Dim(Color c, float f) => new Color(c.r * f, c.g * f, c.b * f, c.a);

        // ---- Runtime-generated shape textures.

        private static Texture2D MakeDiscTex(int size)
        {
            var tex = NewTex(size);
            float cx = (size - 1) * 0.5f, r = size * 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cx) * (y - cx));
                    tex.SetPixel(x, y, d <= r ? Color.white : Color.clear);
                }
            tex.Apply();
            return tex;
        }

        private static Texture2D MakeRingTex(int size, float innerFrac)
        {
            var tex = NewTex(size);
            float cx = (size - 1) * 0.5f, outer = size * 0.5f, inner = outer * innerFrac;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cx) * (y - cx));
                    tex.SetPixel(x, y, d <= outer && d >= inner ? Color.white : Color.clear);
                }
            tex.Apply();
            return tex;
        }

        private static Texture2D MakeSquareTex(int size)
        {
            var tex = NewTex(size);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    tex.SetPixel(x, y, Color.white);
            tex.Apply();
            return tex;
        }

        private static Texture2D MakeDiamondTex(int size)
        {
            var tex = NewTex(size);
            float c = (size - 1) * 0.5f, r = size * 0.5f - 1f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    tex.SetPixel(x, y, Mathf.Abs(x - c) + Mathf.Abs(y - c) <= r ? Color.white : Color.clear);
            tex.Apply();
            return tex;
        }

        private static Texture2D MakeArrowTex(int size)
        {
            var tex = NewTex(size);
            float tip = size - 3f, back = 3f, mid = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool inside = x >= back && x <= tip
                        && Mathf.Abs(y - mid) <= (tip - x) * 0.5f;
                    tex.SetPixel(x, y, inside ? Color.white : Color.clear);
                }
            tex.Apply();
            return tex;
        }

        private static readonly string[] DigitPatterns =
        {
            "111101101101111", "010110010010111", "111001111100111", "111001111001111",
            "101101111001001", "111100111001111", "111100111101111", "111001001001001",
            "111101111101111", "111101111001111",
        };

        private static Texture2D MakeDigitTex(int digit)
        {
            const int px = 8;
            var tex = new Texture2D(3 * px, 5 * px, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            string pat = DigitPatterns[digit];
            for (int r = 0; r < 5; r++)
                for (int c = 0; c < 3; c++)
                {
                    var col = pat[r * 3 + c] == '1' ? Color.white : Color.clear;
                    for (int yy = 0; yy < px; yy++)
                        for (int xx = 0; xx < px; xx++)
                            tex.SetPixel(c * px + xx, (4 - r) * px + yy, col);
                }
            tex.Apply();
            return tex;
        }

        private static Texture2D MakeSquareOutlineTex(int size, int border)
        {
            var tex = NewTex(size);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool edge = x < border || y < border || x >= size - border || y >= size - border;
                    tex.SetPixel(x, y, edge ? Color.white : Color.clear);
                }
            tex.Apply();
            return tex;
        }

        private static Texture2D MakePolyTex(int size, Vector2[] verts)
        {
            var tex = NewTex(size);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector2((x + 0.5f) / size, (y + 0.5f) / size);
                    tex.SetPixel(x, y, InsidePoly(p, verts) ? Color.white : Color.clear);
                }
            tex.Apply();
            return tex;
        }

        private static bool InsidePoly(Vector2 p, Vector2[] v)
        {
            bool inside = false;
            for (int i = 0, j = v.Length - 1; i < v.Length; j = i++)
                if (v[i].y > p.y != v[j].y > p.y
                    && p.x < (v[j].x - v[i].x) * (p.y - v[i].y) / (v[j].y - v[i].y) + v[i].x)
                    inside = !inside;
            return inside;
        }

        private static Texture2D MakeRegularPolyTex(int size, int sides, float rotDeg)
        {
            var v = new Vector2[sides];
            for (int i = 0; i < sides; i++)
            {
                float a = (rotDeg + i * 360f / sides) * Mathf.Deg2Rad;
                v[i] = new Vector2(0.5f + 0.48f * Mathf.Cos(a), 0.5f + 0.48f * Mathf.Sin(a));
            }
            return MakePolyTex(size, v);
        }

        private static Texture2D MakeStarTex(int size, int points, float innerFrac)
        {
            var v = new Vector2[points * 2];
            for (int i = 0; i < points * 2; i++)
            {
                float r = (i & 1) == 0 ? 0.48f : 0.48f * innerFrac;
                float a = (90f + i * 180f / points) * Mathf.Deg2Rad;
                v[i] = new Vector2(0.5f + r * Mathf.Cos(a), 0.5f + r * Mathf.Sin(a));
            }
            return MakePolyTex(size, v);
        }

        private static Texture2D MakeKiteTex(int size) =>
            MakePolyTex(size, new[]
            {
                new Vector2(0.5f, 0.98f), new Vector2(0.78f, 0.34f),
                new Vector2(0.5f, 0.02f), new Vector2(0.22f, 0.34f),
            });

        private static Texture2D MakeCrossTex(int size, float armFrac)
        {
            var tex = NewTex(size);
            float c = (size - 1) * 0.5f, arm = size * armFrac * 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    tex.SetPixel(x, y, Mathf.Abs(x - c) <= arm || Mathf.Abs(y - c) <= arm
                        ? Color.white : Color.clear);
            tex.Apply();
            return tex;
        }

        private static Texture2D MakeCrescentTex(int size)
        {
            var tex = NewTex(size);
            float c = (size - 1) * 0.5f, r = size * 0.5f - 1f;
            float cutX = c + size * 0.28f, cutR = size * 0.40f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    float dc = Mathf.Sqrt((x - cutX) * (x - cutX) + (y - c) * (y - c));
                    tex.SetPixel(x, y, d <= r && dc >= cutR ? Color.white : Color.clear);
                }
            tex.Apply();
            return tex;
        }

        private static Texture2D MakeBullseyeTex(int size)
        {
            var tex = NewTex(size);
            float c = (size - 1) * 0.5f, r = size * 0.5f - 1f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    bool inside = d <= r * 0.40f || (d >= r * 0.74f && d <= r);
                    tex.SetPixel(x, y, inside ? Color.white : Color.clear);
                }
            tex.Apply();
            return tex;
        }

        private static Texture2D MakeDiamondOutlineTex(int size, float innerFrac)
        {
            var tex = NewTex(size);
            float c = (size - 1) * 0.5f, r = size * 0.5f - 1f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float m = Mathf.Abs(x - c) + Mathf.Abs(y - c);
                    tex.SetPixel(x, y, m <= r && m >= r * innerFrac ? Color.white : Color.clear);
                }
            tex.Apply();
            return tex;
        }

        private static Texture2D NewTex(int size) => new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
    }
}
