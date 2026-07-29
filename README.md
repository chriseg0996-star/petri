# Petri — deterministic territorial superorganism RTS

Each player IS one bacterial superorganism on a petri dish. It starts as a small blob
around its nucleus, grows outward on its own, and passively mines the resource nodes it
engulfs. Units are produced from buildings but are never individual entities — they join
the organism's **Force** as counts assigned to **fronts** (equal angular sections of the
border), where they hold ground, trade fire, and push. Win by holding **75% of the dish**
or by bleeding every rival organism's health to zero (losing your nucleus also kills you).

## How it plays

- **Growth**: the organism claims adjacent free cells every beat; workers (a pooled count)
  speed both growth and the passive harvest of engulfed nutrient/mineral nodes.
- **Fronts**: the border splits into K equal sectors around the organism's centroid,
  K ∈ {4, 6, 8, 12, 20, 40}, stepped by the ▲/▼ panel buttons (force redistributes evenly).
  New units auto-assign to the emptiest front unless their producer is rallied; force
  re-deploys between fronts at each unit type's move speed.
- **Combat** (the heart of the game): where borders touch, fronts exchange fire from a
  role triangle — **melee attack = push power, ranged attack = defensive fire, HP = hold,
  move speed = redeploy rate**; armed buildings garrison their sector. Damage pools per
  defending front and kills pay the killer evolution points + a food bounty. Only a
  **pushing** front takes cells, and only where its push beats the defenders' hold — probe
  for the weak sector, don't slam the strong one.
- **Breakthrough**: a contested front whose defenders are wiped BREAKS for 10 seconds —
  the units assigned to it have perished, and enemies flip cells through the gap at ×4
  while it reforms.
- **Health**: ONE living value per organism, swelling in lockstep with its growing
  ceiling — every new cell and finished building adds its worth on the spot, war or
  peace, so expanding always strengthens you. The slow regenerative knit stops while any
  front is engaged, and combat bleeds the body: attrition under fire even on manned
  fronts, scorching on unmanned ones, and 3 per torn-away cell. Zero = elimination.
- **Buildings**: placed from the persistent bottom panel, only inside your own territory,
  and they build themselves. A building on a contested cell soaks enemy flips (40 damage
  each) until it falls — territory lost is buildings lost.

## Layout

One codebase, two runtimes: the deterministic sim source lives under the Unity project's
`Assets/Sim` and is compiled BOTH by Unity's Mono (the graphical client) and by the headless
`.NET` projects (which source-glob those same files). This is the only copy of the sim.

| Path | What |
|---|---|
| `unity/PetriClient/Assets/Sim` | Engine-free deterministic sim: fixed-point math, defs, territory grid, front math, commands, systems, bot. Compiled by Unity (asmdef `Petri.Sim`, no engine refs) and globbed by the headless build. |
| `unity/PetriClient/Assets/Client` | Unity view layer (asmdef `Petri.Client`): data loader, 20 Hz tick driver, territory/fog renderer, camera, input→commands, HUD. |
| `unity/PetriClient/Assets/StreamingAssets/Data` | The JSON dataset the Unity client loads (byte-identical copy of `data/`). |
| `src/Petri.Core` | Headless build: source-globs `Assets/Sim` + `DefLoader` (System.Text.Json, headless-only). |
| `src/Petri.Runner` | Headless CLI: `run-match`, `determinism`. |
| `tests/Petri.Tests` | xunit suite (math, determinism, territory, fronts, combat, breakthrough, health, victory, bot, data validation). |
| `data/` | Master JSON dataset used by the headless runner/tests. Mirror into StreamingAssets after edits (copy, never retype). |

## Running the Unity client

1. Open `unity/PetriClient` in Unity **6000.5.2f1** (Unity generates `.meta`/`Library` on first
   open — commit the new `.meta` files with the next commit; never hand-author one).
2. Press **Play**. The main menu builds itself from code (`MainMenu` via
   `RuntimeInitializeOnLoadMethod`) — no scene wiring. **Skirmish** (map + seed) starts a match;
   matches end with a victory banner back to the menu.
3. Controls: **R-click** where you want to attack — the front facing the click pushes
   there (select a front first with **L-click** on your border to steer a specific one;
   the wedge lifts white; labels ride the border — gold = pushing, red pulse = broken) ·
   **R-click-drag** sketches a push path; release orders the push at its end ·
   **[S]** stop the selected front's push ·
   **L-click** buildings/nodes to inspect · with a producer selected, **R-click** a sector
   to rally its output there, **[R]** back to auto · build from the persistent bottom
   panel (ghost green = legal, own territory only) · **▲/▼** split/merge fronts ·
   **Esc** deselect/cancel · **arrows**/**middle-drag** pan · **scroll** zoom ·
   minimap **L-press** pans.

If mouse/keyboard do nothing, set **Edit ▸ Project Settings ▸ Player ▸ Active Input Handling**
to *Input Manager (Old)* or *Both* — the client uses the legacy `UnityEngine.Input` API.
The view is a pure projection of sim state and never writes back; sprites and the territory
overlay are generated at runtime.

## Iron rules (same discipline as any lockstep RTS)

1. **Fixed-point / integer only** in sim code — no float/double/`System.Random`/LINQ in ticks.
   Territory, fronts, and combat are pure integer math (hardcoded sector ray tables,
   cross-product classification, single-floor formulas).
2. **Everything mutates through Commands** (`CommandLog` → `CommandSystem`). Invalid commands
   reject and change nothing. UI/replays/network peers/the bot are all just command sources.
   Retired command ids (1, 2, 4, 5, 7, 10, 11-23) reject forever and are never reused.
3. **Index-order scans only** — never enumerate a Dictionary/HashSet in tick code; ordered
   candidate lists use capped per-world scratch buffers with stable insertion sorts.
4. **New persistent state joins `Simulation.StateHash()` AND is reset** (`SimWorld.Spawn()`
   for entity fields, `Eliminate()` for the per-player superorganism block).
5. Distances/rates in JSON are integer centi-units and ticks (20 ticks/second); the
   territory grid is 2u cells, hashed per cell.

## Implemented

- Territory grid with seeded start blobs, beat-driven isotropic growth, and push-directed
  expansion; walls block cells (all three shipped maps are symmetric).
- Front partition via shared integer ray tables (`FrontMath`) — sim, bot, and client all
  classify with the same cross-product convention.
- Front combat: contact discovery, frozen role-triangle stats, damage pools, kill bounties,
  breakthrough windows, building flip-soak, organism health, dual victory paths
  (75% territory / health elimination / nucleus capture).
- Production as counts with weights, overrides, pause, and per-front rally; passive
  worker-scaled harvest; self-building construction from the panel.
- Skirmish bot: economy weights, build ladder, and disciplined front pushes at the weakest
  contacted sector (stops losing pushes, holds during its own breakthroughs).
- Unity client: runtime territory overlay with contested shimmer, fog of war stamped from
  territory, fronts UI, persistent build/info HUD, minimap with territory wash.
- Deterministic tick loop with FNV-1a world fingerprint; replay = re-fed command log.

Deferred (data retained, systems gated off): supply lines, tech prongs, the 8 upgrades,
per-entity dial. `burrow-node` and `sentinel-spire` are `constructible: false` until the
supply layer returns.

## Verify (from the repo root)

```
dotnet build Petri.slnx
dotnet test Petri.slnx
dotnet run --project src/Petri.Runner -- determinism --seed 42 --ticks 8000
dotnet run --project src/Petri.Runner -- run-match --seed 7 --ticks 20000
```

All four must pass before and after any change; `determinism` must print PASS for both the
fresh rerun and the replay, and `run-match` must end with a winner. Balance reference:
scripted matches decide around tick 5500 (petri-dish), 5800 (capillary), 6300 (agar-plate).
