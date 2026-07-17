using System.Collections.Generic;
using UnityEngine;
using Petri.Core;

namespace Petri.Client
{
    /// <summary>
    /// Renders the sim as readable top-down sprites. The view is a pure projection of sim state
    /// — it never writes back. Sprites are generated at runtime (disc, ring, square, square
    /// outline) so the project needs no art assets; authored C&C-style sprites drop in later by
    /// swapping these per def id. Entities, selection outlines, order pings, rally markers, and
    /// the build-placement ghost are all pooled/reused to keep per-frame allocations at zero.
    /// </summary>
    public sealed class GameView : MonoBehaviour
    {
        // The first eight are hand-picked and deliberately avoid green (which reads as a
        // neutral node); the rest are spread by golden-angle hue so 32 seats stay tellable
        // apart. Single source of truth — the HUD/minimap read this via OwnerColor().
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

        private static readonly Color NeutralColor = NutrientColor;
        private static readonly Color SelectionColor = Color.white;
        public static readonly Color MovePing = new Color(0.4f, 1f, 0.5f);
        public static readonly Color RallyPing = new Color(1f, 0.85f, 0.3f);
        public static readonly Color AttackPing = new Color(1f, 0.35f, 0.3f);

        private const float PingSeconds = 0.6f;

        private struct PingFx { public Vector3 Pos; public float Start; public Color Color; }
        private struct PopFx { public Vector3 Pos; public float Start; public Color Color; public float Size; }
        private struct ProjectileFx { public Vector3 From, To; public float Start, Duration; public Color Color; }
        private const float PopSeconds = 0.4f;

        private MatchBootstrap _match;
        private Sprite _disc, _ring, _thinRing, _square, _squareOutline, _arrow, _diamond;
        private Sprite[] _digits; // runtime 3x5 pixel numerals for tier badges

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
        private readonly List<ProjectileFx> _projectiles = new List<ProjectileFx>();
        private SpriteRenderer _ghost;

        // Hit feedback: victims blink white briefly when damage lands (fed by AttackEvents).
        private float[] _blinkUntil;

        // Render interpolation: the sim moves at 20 Hz, so we lerp each entity between its
        // previous-tick and current-tick position/facing by MatchBootstrap.TickAlpha. Purely
        // cosmetic — the sim is never read for these; hashes are untouched.
        private Vector2[] _ipPrevPos, _ipCurPos, _ipPrevFace, _ipCurFace;
        private int[] _ipGen;
        private int _ipTick = int.MinValue;
        private float[] _selFade; // per-slot selection-outline fade (eased 0..1)

        // Last rendered frame's snapshot, for death detection: a slot that held a unit or
        // building and is now empty (or holds a different generation) just died there.
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
                // 1 pixel = 1 world unit; pivot at the map origin so position (0,0) lines up.
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
            _blinkUntil = new float[cap];
            _ipPrevPos = new Vector2[cap];
            _ipCurPos = new Vector2[cap];
            _ipPrevFace = new Vector2[cap];
            _ipCurFace = new Vector2[cap];
            _ipGen = new int[cap];
            _selFade = new float[cap];
        }

        /// <summary>On each new sim tick, roll current→previous and capture the new positions/
        /// facings. Fresh occupants (generation change) or a multi-tick catch-up snap instead of
        /// streaking across the map.</summary>
        private void RollInterp(SimWorld w)
        {
            bool bigGap = w.TickCount - _ipTick > 1;
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] == EntityKind.None) { _ipGen[i] = -1; continue; }
                var p = new Vector2(ToF(w.Pos[i].X), ToF(w.Pos[i].Y));
                var f = new Vector2(w.Facing[i].X.Raw / (float)Fix.OneRaw, w.Facing[i].Y.Raw / (float)Fix.OneRaw);
                bool fresh = bigGap || w.Generation[i] != _ipGen[i];
                if (fresh) _selFade[i] = 0f; // don't inherit the prior occupant's selection glow
                _ipPrevPos[i] = fresh ? p : _ipCurPos[i];
                _ipCurPos[i] = p;
                _ipPrevFace[i] = fresh ? f : _ipCurFace[i];
                _ipCurFace[i] = f;
                _ipGen[i] = w.Generation[i];
            }
            _ipTick = w.TickCount;
        }

        /// <summary>
        /// Pack every runtime-generated shape into ONE atlas texture. Sprites that share a
        /// texture share a material, so Unity can batch thousands of entities into a handful
        /// of draw calls — with a texture each, every sprite was its own draw call and frame
        /// time collapsed as armies grew.
        /// </summary>
        private void BuildSpriteAtlas()
        {
            var texs = new Texture2D[17];
            texs[0] = MakeDiscTex(64);
            texs[1] = MakeRingTex(64, 0.80f);
            texs[2] = MakeSquareTex(8);
            texs[3] = MakeSquareOutlineTex(64, 6);
            texs[4] = MakeArrowTex(32);
            texs[5] = MakeDiamondTex(64);
            // High-res hairline ring: supply footprints blow a sprite up to ~36 world units,
            // where the chunky 80% ring became a fat translucent donut. A 256px, 97%-inner
            // ring stays a crisp thin circle at that scale.
            texs[6] = MakeRingTex(256, 0.970f);
            for (int d = 0; d < 10; d++) texs[7 + d] = MakeDigitTex(d);

            var atlas = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
            // Padding keeps bilinear sampling from bleeding neighbours into each other.
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

            foreach (var t in texs) Destroy(t); // the atlas owns the pixels now
        }

        /// <summary>Sprite for one atlas cell. pixelsPerUnit = the cell's pixel height, so a
        /// transform scale of 1 is still exactly one world unit tall (as before atlasing).</summary>
        private static Sprite FromAtlas(Texture2D atlas, Rect uvRect)
        {
            var px = new Rect(uvRect.x * atlas.width, uvRect.y * atlas.height,
                              uvRect.width * atlas.width, uvRect.height * atlas.height);
            return Sprite.Create(atlas, px, new Vector2(0.5f, 0.5f), Mathf.Max(1f, px.height));
        }

        /// <summary>Show a brief expanding ping at a world position (order feedback).</summary>
        public void Ping(Vector3 worldPos, Color color) =>
            _pings.Add(new PingFx { Pos = worldPos, Start = Time.time, Color = color });

        /// <summary>A hit landed this tick: ranged attackers (projectileSpeedCenti &gt; 0 in the
        /// unit's def) fly a small dot from shooter to victim at that speed. Melee gets nothing
        /// (yet). Purely cosmetic — the sim's damage already applied.</summary>
        public void SpawnAttackFx(int attacker, int target)
        {
            var w = _match.Sim.World;
            // Fights entirely inside the fog stay silent.
            if (Vision != null
                && !Vision.VisibleAt(ToF(w.Pos[attacker].X), ToF(w.Pos[attacker].Y))
                && !Vision.VisibleAt(ToF(w.Pos[target].X), ToF(w.Pos[target].Y))) return;
            _blinkUntil[target] = Time.time + 0.12f; // white damage blink on the victim
            // Tiered caches shoot too — their projectile speed comes from rules, not a unit def.
            float speed = w.Kind[attacker] == EntityKind.Building
                ? w.Rules.CacheProjectileSpeedCenti / 100f
                : _match.Defs.Units[w.DefIndex[attacker]].ProjectileSpeedCenti / 100f;
            if (speed <= 0f) return;
            var from = new Vector3(ToF(w.Pos[attacker].X), ToF(w.Pos[attacker].Y), 0f);
            var to = new Vector3(ToF(w.Pos[target].X), ToF(w.Pos[target].Y), 0f);
            float dist = Vector3.Distance(from, to);
            if (dist < 0.05f) return;
            _projectiles.Add(new ProjectileFx
            {
                From = from, To = to, Start = Time.time, Duration = dist / speed,
                Color = Color.Lerp(OwnerColors[w.Owner[attacker] % OwnerColors.Length], Color.white, 0.55f),
            });
        }

        private void LateUpdate()
        {
            if (_match == null || _match.Sim == null) return;
            var w = _match.Sim.World;
            var defs = _match.Defs;

            UpdateFog(w, defs);

            if (w.TickCount != _ipTick) RollInterp(w);
            float alpha = _match.TickAlpha;

            // Screen density: when zoomed out, per-unit trimmings are a few pixels across and
            // unreadable — skipping them there roughly halves the renderer count exactly when
            // the most entities are on screen.
            var cam = Camera.main;
            float pxPerUnit = cam != null && cam.orthographicSize > 0.01f
                ? Screen.height / (2f * cam.orthographicSize) : 100f;

            int body = 0, overlay = 0;
            var input = _match.Input;
            var selected = input != null ? input.Selected : null;

            // ---- Death pops: anything that vanished (or whose slot was regenerated) since the
            // last rendered frame bursts at its final position (only where the player can see).
            for (int i = 0; i < _prevHigh; i++)
            {
                if (_prevKind[i] != EntityKind.Unit && _prevKind[i] != EntityKind.Building) continue;
                if (w.Kind[i] != EntityKind.None && w.Generation[i] == _prevGen[i]) continue;
                _blinkUntil[i] = 0f; // don't blink the slot's next occupant
                if (Vision != null && !Vision.VisibleAt(_prevPos[i].x, _prevPos[i].y)) continue;
                var c = Color.Lerp(OwnerColors[_prevOwner[i] % OwnerColors.Length], Color.white, 0.45f);
                _pops.Add(new PopFx { Pos = _prevPos[i], Start = Time.time, Color = c, Size = _prevSize[i] });
            }

            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] == EntityKind.None) continue;

                // Smoothed render position: lerp last-tick → this-tick by the frame's alpha.
                Vector2 ip = Vector2.Lerp(_ipPrevPos[i], _ipCurPos[i], alpha);
                float x = ip.x, y = ip.y;

                // Fog culling: your own and ALLIED entities always show (teams share vision);
                // enemy units exist on screen only while actually seen; enemy buildings and
                // neutral nodes appear once explored (static — safe to remember).
                if (Vision != null && !w.IsFriendly(MatchBootstrap.HumanPlayer, w.Owner[i]))
                {
                    if (w.Kind[i] == EntityKind.Unit && w.Owner[i] != SimWorld.NeutralOwner)
                    { if (!Vision.VisibleAt(x, y)) continue; }
                    else if (!Vision.ExploredAt(x, y)) continue;
                }
                float radius = ToF(CollisionSystem.RadiusOf(w, defs, i));
                float diameter = Mathf.Max(0.2f, radius * 2f);
                bool isBuilding = w.Kind[i] == EntityKind.Building;
                float onScreenPx = diameter * pxPerUnit;
                bool detail = onScreenPx >= 10f; // facing arrows / tier badges
                bool bars = onScreenPx >= 7f;    // health bars

                var sr = Rent(_bodies, ref body);
                sr.transform.position = new Vector3(x, y, 0f);

                switch (w.Kind[i])
                {
                    case EntityKind.Building:
                        sr.sprite = _square;
                        sr.transform.localScale = new Vector3(diameter, diameter, 1f);
                        var bc = Dim(OwnerColors[w.Owner[i] % OwnerColors.Length], 0.75f);
                        // Tiered caches read brighter per tier — an armed depot looks the part.
                        if (w.Tier[i] > 0) bc = Color.Lerp(bc, Color.white, 0.18f * w.Tier[i]);
                        if (w.ConstructionRemaining[i] > 0) bc.a = 0.45f; // translucent site
                        if (Time.time < _blinkUntil[i]) bc = Color.Lerp(bc, Color.white, 0.75f); // hit blink
                        sr.color = bc;
                        sr.sortingOrder = 0;

                        // Tier badge: an upgraded cache wears its tier number (matching the
                        // HUD card's numbering, where an unupgraded cache is tier 1).
                        if (w.Tier[i] > 0 && detail)
                        {
                            var dg = Rent(_overlays, ref overlay);
                            dg.sprite = _digits[Mathf.Min(9, w.Tier[i] + 1)];
                            dg.transform.position = new Vector3(x, y, 0f);
                            float dh = diameter * 0.55f;
                            dg.transform.localScale = new Vector3(dh, dh, 1f);
                            dg.color = new Color(0.05f, 0.08f, 0.05f, 0.95f);
                            dg.sortingOrder = 2;
                        }

                        // Supply state for the player's own network. A healthy depot draws its
                        // reach as a clean hairline circle plus a soft inner glow; a dry or cut
                        // one drops the footprint (it supplies nothing) and wears a pulsing
                        // badge instead, so problems catch the eye rather than the whole map
                        // being covered in fat rings.
                        if (w.Owner[i] == MatchBootstrap.HumanPlayer && w.ConstructionRemaining[i] == 0
                            && defs.Buildings[w.DefIndex[i]].ProvidesSupply)
                        {
                            bool connected = w.ScratchConnected[i];
                            bool dry = w.DepotStock[i] == 0;
                            if (connected && !dry)
                            {
                                float sd = w.Rules.SupplyRadiusCenti / 100f * 2f;
                                var glow = Rent(_overlays, ref overlay);   // barely-there fill
                                glow.sprite = _disc;
                                glow.transform.position = new Vector3(x, y, 0f);
                                glow.transform.localScale = new Vector3(sd, sd, 1f);
                                glow.color = new Color(0.40f, 0.95f, 0.55f, 0.045f);
                                glow.sortingOrder = 1;

                                var edge = Rent(_overlays, ref overlay);   // crisp reach outline
                                edge.sprite = _thinRing;
                                edge.transform.position = new Vector3(x, y, 0f);
                                edge.transform.localScale = new Vector3(sd, sd, 1f);
                                edge.color = new Color(0.45f, 1f, 0.6f, 0.32f);
                                edge.sortingOrder = 2;
                            }
                            else
                            {
                                // Gentle 1 Hz pulse: amber = stocked-out, red = chain cut.
                                float pulse = 0.55f + 0.25f * Mathf.Sin(Time.time * 6f);
                                var badge = Rent(_overlays, ref overlay);
                                badge.sprite = _ring;
                                badge.transform.position = new Vector3(x, y, 0f);
                                float bd2 = diameter * 1.5f;
                                badge.transform.localScale = new Vector3(bd2, bd2, 1f);
                                badge.color = !connected
                                    ? new Color(1f, 0.30f, 0.25f, pulse)
                                    : new Color(1f, 0.75f, 0.20f, pulse);
                                badge.sortingOrder = 2;
                            }
                        }
                        break;
                    case EntityKind.Node:
                        sr.sprite = _disc;
                        sr.transform.localScale = new Vector3(diameter, diameter, 1f);
                        sr.color = w.NodeMineral[i] ? MineralColor : NutrientColor;
                        sr.sortingOrder = 1;
                        break;
                    default: // Unit
                        var ud = defs.Units[w.DefIndex[i]];
                        Color c = OwnerColors[w.Owner[i] % OwnerColors.Length];
                        if (ud.IsLeader) { c = Color.Lerp(c, Color.white, 0.5f); diameter *= 1.25f; }
                        else if (ud.IsWorker) c = Dim(c, 0.7f);
                        if (Time.time < _blinkUntil[i]) c = Color.Lerp(c, Color.white, 0.75f); // hit blink
                        // Ranged units (they fire projectiles) read as diamonds; melee as discs.
                        sr.sprite = ud.ProjectileSpeedCenti > 0 ? _diamond : _disc;
                        sr.transform.localScale = new Vector3(diameter, diameter, 1f);
                        sr.color = c;
                        sr.sortingOrder = ud.IsLeader ? 4 : 3;

                        // A selected own leader shows its command-aura reach — units inside
                        // the ring fight harder, so the player can shape the line around it.
                        if (ud.IsLeader && w.Owner[i] == MatchBootstrap.HumanPlayer
                            && selected != null && selected.Contains(i))
                        {
                            float ad = w.Rules.LeaderAuraRadiusCenti / 100f * 2f;
                            var aura = Rent(_overlays, ref overlay);
                            aura.sprite = _thinRing;
                            aura.transform.position = new Vector3(x, y, 0f);
                            aura.transform.localScale = new Vector3(ad, ad, 1f);
                            aura.color = new Color(0.65f, 0.85f, 1f, 0.35f);
                            aura.sortingOrder = 2;
                        }

                        if (!detail) break; // zoomed out: the arrow would be sub-pixel noise
                        // Facing arrow: a small dark wedge riding the front edge of the body,
                        // oriented along the unit's simulated facing (interpolated for smoothness).
                        Vector2 fdir = Vector2.Lerp(_ipPrevFace[i], _ipCurFace[i], alpha);
                        float fxd = fdir.x, fyd = fdir.y;
                        var ar = Rent(_overlays, ref overlay);
                        ar.sprite = _arrow;
                        float aSize = diameter * 0.62f;
                        ar.transform.position = new Vector3(x + fxd * radius * 0.5f, y + fyd * radius * 0.5f, 0f);
                        ar.transform.localScale = new Vector3(aSize, aSize, 1f);
                        ar.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(fyd, fxd) * Mathf.Rad2Deg);
                        // Red arrow = out of supply (fighting at reduced effect).
                        ar.color = w.SupplyTicks[i] == 0
                            ? new Color(1f, 0.25f, 0.2f, 0.95f)
                            : new Color(0.05f, 0.08f, 0.05f, 0.8f);
                        ar.sortingOrder = ud.IsLeader ? 5 : 4;
                        break;
                }

                // ---- Health bar above damaged units/buildings; hidden at full health.
                if (bars && w.Kind[i] != EntityKind.Node)
                {
                    int maxHp = isBuilding ? defs.Buildings[w.DefIndex[i]].MaxHp << w.Tier[i] : defs.Units[w.DefIndex[i]].MaxHp;
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

                // Selection outline eases in on select and out on deselect (no hard pop).
                bool isSel = selected != null && selected.Contains(i);
                _selFade[i] = Mathf.MoveTowards(_selFade[i], isSel ? 1f : 0f, Time.deltaTime / 0.10f);
                if (_selFade[i] > 0.01f)
                {
                    // White outline sized to the entity's own sprite — a small proportional
                    // margin so it hugs just outside the graphic, consistent whether it's a
                    // tiny worker or a big leader/building (no fixed pad that dwarfs small units).
                    var so = Rent(_overlays, ref overlay);
                    so.transform.position = new Vector3(x, y, 0f);
                    so.sprite = isBuilding ? _squareOutline : _ring;
                    // Settle from a touch larger to its resting size as it fades in.
                    float rest = isBuilding ? 1.06f : 1.15f;
                    float sel = diameter * Mathf.Lerp(rest + 0.14f, rest, _selFade[i]);
                    so.transform.localScale = new Vector3(sel, sel, 1f);
                    var selc = SelectionColor; selc.a = _selFade[i];
                    so.color = selc;
                    so.sortingOrder = 6;
                }

                if (isSel)
                {
                    // Rally flag for selected own buildings.
                    if (isBuilding && w.HasRally[i] && w.Owner[i] == MatchBootstrap.HumanPlayer)
                    {
                        var rp = Rent(_overlays, ref overlay);
                        rp.transform.position = new Vector3(ToF(w.RallyPoint[i].X), ToF(w.RallyPoint[i].Y), 0f);
                        rp.sprite = _ring;
                        rp.transform.localScale = new Vector3(0.5f, 0.5f, 1f);
                        rp.color = RallyPing;
                        rp.sortingOrder = 6;
                    }
                }
            }

            // ---- Command lines: for each selected own unit, the path it will take — the
            // active leg plus every shift-queued leg, green for moves, red for attack-moves,
            // a dot per waypoint.
            if (selected != null)
            {
                foreach (int i in selected)
                {
                    if (i >= w.HighWater || w.Kind[i] != EntityKind.Unit) continue;
                    if (w.Owner[i] != MatchBootstrap.HumanPlayer) continue;
                    var from = new Vector3(ToF(w.Pos[i].X), ToF(w.Pos[i].Y), 0f);
                    if (w.HasMoveOrder[i] || w.AttackMove[i])
                    {
                        var to = new Vector3(ToF(w.MoveTarget[i].X), ToF(w.MoveTarget[i].Y), 0f);
                        DrawOrderLine(from, to, w.AttackMove[i], ref overlay);
                        from = to;
                    }
                    int qb = i * SimConstants.MaxOrderQueue;
                    for (int q = 0; q < w.QueueCount[i]; q++)
                    {
                        bool attack = w.QueueKind[qb + q] == SimConstants.OrderAttackMove;
                        var to = new Vector3(ToF(w.QueuePos[qb + q].X), ToF(w.QueuePos[qb + q].Y), 0f);
                        DrawOrderLine(from, to, attack, ref overlay);
                        var dot = Rent(_overlays, ref overlay);
                        dot.sprite = _ring;
                        dot.transform.position = to;
                        dot.transform.localScale = new Vector3(0.3f, 0.3f, 1f);
                        var dc = attack ? AttackPing : MovePing;
                        dc.a = 0.8f;
                        dot.color = dc;
                        dot.sortingOrder = 2;
                        from = to;
                    }
                }
            }

            // ---- Order pings: expand and fade, then expire.
            for (int p = _pings.Count - 1; p >= 0; p--)
            {
                float t = (Time.time - _pings[p].Start) / PingSeconds;
                if (t >= 1f) { _pings.RemoveAt(p); continue; }
                var pr = Rent(_overlays, ref overlay);
                pr.transform.position = _pings[p].Pos;
                float s = Mathf.Lerp(0.25f, 1.6f, t);
                pr.transform.localScale = new Vector3(s, s, 1f);
                var pc = _pings[p].Color;
                pc.a = 1f - t;
                pr.sprite = _ring;
                pr.color = pc;
                pr.sortingOrder = 7;
            }

            // ---- Projectiles: small dots flying shooter → victim at the def's speed.
            for (int p = _projectiles.Count - 1; p >= 0; p--)
            {
                float t = (Time.time - _projectiles[p].Start) / Mathf.Max(0.02f, _projectiles[p].Duration);
                if (t >= 1f) { _projectiles.RemoveAt(p); continue; }
                var pr = Rent(_overlays, ref overlay);
                pr.transform.position = Vector3.Lerp(_projectiles[p].From, _projectiles[p].To, t);
                pr.transform.localScale = new Vector3(0.14f, 0.14f, 1f);
                pr.sprite = _disc;
                pr.color = _projectiles[p].Color;
                pr.sortingOrder = 5;
            }

            // ---- Death pops: a bright burst that swells and fades where something died.
            for (int p = _pops.Count - 1; p >= 0; p--)
            {
                float t = (Time.time - _pops[p].Start) / PopSeconds;
                if (t >= 1f) { _pops.RemoveAt(p); continue; }
                var pr = Rent(_overlays, ref overlay);
                pr.transform.position = _pops[p].Pos;
                float s = _pops[p].Size * Mathf.Lerp(1f, 2.2f, t);
                pr.transform.localScale = new Vector3(s, s, 1f);
                var pc = _pops[p].Color;
                pc.a = 0.85f * (1f - t) * (1f - t); // ease out
                pr.sprite = _disc;
                pr.color = pc;
                pr.sortingOrder = 8;
            }

            DrawGhost(w, defs, input);

            for (int i = body; i < _bodies.Count; i++) _bodies[i].enabled = false;
            for (int i = overlay; i < _overlays.Count; i++) _overlays[i].enabled = false;

            // ---- Snapshot this frame for next frame's death detection.
            for (int i = 0; i < w.HighWater; i++)
            {
                _prevKind[i] = w.Kind[i];
                _prevGen[i] = w.Generation[i];
                _prevOwner[i] = w.Owner[i];
                _prevPos[i] = new Vector3(ToF(w.Pos[i].X), ToF(w.Pos[i].Y), 0f);
                _prevSize[i] = Mathf.Max(0.25f, ToF(CollisionSystem.RadiusOf(w, defs, i)) * 2f);
            }
            _prevHigh = w.HighWater;
        }

        /// <summary>Rebuild visibility and repaint the fog blanket on a modest cadence —
        /// derived view state only, never fed back into the sim.</summary>
        private void UpdateFog(SimWorld w, DefDatabase defs)
        {
            if (Vision == null || Time.time < _fogNext) return;
            _fogNext = Time.time + FogInterval;
            Vision.Rebuild(w, defs, MatchBootstrap.HumanPlayer);
            for (int y = 0; y < Vision.CellsY; y++)
            {
                int row = y * Vision.CellsX;
                for (int x = 0; x < Vision.CellsX; x++)
                    _fogPixels[row + x] = Vision.VisibleCell(x, y) ? FogVisible
                        : Vision.ExploredCell(x, y) ? FogExplored : FogUnseen;
            }
            _fogTex.SetPixels32(_fogPixels);
            _fogTex.Apply(false);
        }

        /// <summary>Translucent footprint following the mouse while placing a building —
        /// green when the spot is buildable, red when blocked.</summary>
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
            _ghost.transform.position = new Vector3(wp.x, wp.y, 0f);
            _ghost.transform.localScale = new Vector3(d, d, 1f);
            bool ok = PlacementLooksClear(w, defs, wp, bdef);
            _ghost.color = ok ? new Color(0.3f, 1f, 0.4f, 0.4f) : new Color(1f, 0.25f, 0.2f, 0.45f);
        }

        // Client-side preview of the sim's placement rule (the command revalidates anyway).
        private static bool PlacementLooksClear(SimWorld w, DefDatabase defs, Vector3 pos, BuildingDef bdef)
        {
            float newR = bdef.CollisionRadiusCenti / 100f;
            for (int i = 0; i < w.HighWater; i++)
            {
                if (w.Kind[i] != EntityKind.Building && w.Kind[i] != EntityKind.Node) continue;
                float otherR = w.Kind[i] == EntityKind.Building
                    ? defs.Buildings[w.DefIndex[i]].CollisionRadiusCenti / 100f
                    : w.Rules.NodeRadiusCenti / 100f;
                float dx = ToF(w.Pos[i].X) - pos.x, dy = ToF(w.Pos[i].Y) - pos.y;
                float minD = newR + otherR + 0.1f;
                if (dx * dx + dy * dy < minD * minD) return false;
            }
            return true;
        }

        /// <summary>One leg of a selected unit's command path: a thin stretched bar from a to b.</summary>
        private void DrawOrderLine(Vector3 a, Vector3 b, bool attack, ref int overlay)
        {
            var d = b - a;
            float len = d.magnitude;
            if (len < 0.05f) return;
            var sr = Rent(_overlays, ref overlay);
            sr.sprite = _square;
            sr.transform.position = (a + b) * 0.5f;
            sr.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
            sr.transform.localScale = new Vector3(len, 0.07f, 1f);
            var c = attack ? AttackPing : MovePing;
            c.a = 0.45f;
            sr.color = c;
            sr.sortingOrder = 2; // under unit bodies so the swarm stays readable
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
            sr.transform.rotation = Quaternion.identity; // arrows rotate; everything else must not inherit it
            cursor++;
            return sr;
        }

        private static float ToF(Fix f) => f.Raw / (float)Fix.OneRaw;
        private static Color Dim(Color c, float k) => new Color(c.r * k, c.g * k, c.b * k, c.a);

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

        // Solid diamond (square rotated 45°) — the silhouette for ranged units.
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

        // Solid triangle pointing +x, for facing indicators.
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

        // 3x5 pixel numerals (row strings, top to bottom), scaled up point-filtered so tier
        // badges stay crisp at any zoom — same no-art-assets philosophy as the shapes.
        private static readonly string[] DigitPatterns =
        {
            "111101101101111", // 0
            "010110010010111", // 1
            "111001111100111", // 2
            "111001111001111", // 3
            "101101111001001", // 4
            "111100111001111", // 5
            "111100111101111", // 6
            "111001001001001", // 7
            "111101111101111", // 8
            "111101111001111", // 9
        };

        private static Texture2D MakeDigitTex(int digit)
        {
            const int px = 8; // texture pixels per font pixel
            var tex = new Texture2D(3 * px, 5 * px, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
            string pat = DigitPatterns[digit];
            for (int r = 0; r < 5; r++)
                for (int c = 0; c < 3; c++)
                {
                    var col = pat[r * 3 + c] == '1' ? Color.white : Color.clear;
                    for (int yy = 0; yy < px; yy++)
                        for (int xx = 0; xx < px; xx++)
                            tex.SetPixel(c * px + xx, (4 - r) * px + yy, col); // row 0 is the top
                }
            tex.Apply();
            return tex; // atlased by BuildSpriteAtlas; ppu = cell height keeps it 1 unit tall
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

        private static Texture2D NewTex(int size) => new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };

    }
}
