# Petri — deterministic microbial RTS (clean rebuild)

A brand-new game, fully independent of the Colony Wars code elsewhere in this repo: its own
solution, its own data set, its own runner and tests. Nothing here references or is referenced
by the old game.

## Layout

One codebase, two runtimes: the deterministic sim source lives under the Unity project's
`Assets/Sim` and is compiled BOTH by Unity's Mono (the graphical client) and by the headless
`.NET` projects (which source-glob those same files). This is the only copy of the sim.

| Path | What |
|---|---|
| `unity/PetriClient/Assets/Sim` | Engine-free deterministic sim: fixed-point math, defs, world, commands, systems. Compiled by Unity (asmdef `Petri.Sim`, no engine refs) and globbed by the headless build. |
| `unity/PetriClient/Assets/Client` | Unity view layer (asmdef `Petri.Client`): data loader, 20 Hz tick driver, sprite renderer, camera, input→commands, HUD. |
| `unity/PetriClient/Assets/StreamingAssets/Data` | The JSON dataset the Unity client loads (copy of `data/`). |
| `src/Petri.Core` | Headless build: source-globs `Assets/Sim` + `DefLoader` (System.Text.Json, headless-only). |
| `src/Petri.Runner` | Headless CLI: `run-match`, `determinism`. |
| `tests/Petri.Tests` | xunit suite (math, determinism, commands, spawn reuse, data validation). |
| `data/` | Master JSON dataset used by the headless runner/tests. Mirror into StreamingAssets after edits. |

## Running the Unity client

1. Open `unity/PetriClient` in Unity **6000.5.2f1** (Unity generates `.meta`/`Library` on first
   open — commit the new `.meta` files with the next commit; never hand-author one).
2. Press **Play**. The main menu builds itself from code (`MainMenu` via
   `RuntimeInitializeOnLoadMethod`) — no scene wiring. **Skirmish** (map + seed) starts a match;
   matches end with a victory banner back to the menu.
3. Controls: **L-click/drag** select (dbl-click = all of type, **Space** = all military) ·
   **R-click** move/rally · **R-drag** formation curve · **Shift+R-drag** set facing ·
   **[A]** attack-move · **[E]** encircle · **[G]** assimilate · **[L]/[U]** link/unlink ·
   **[O]** pace · **Ctrl+[1-9]/[1-9]** control groups · **[B]** build · **[S]** stop ·
   **arrows** or **middle-drag** pan · **scroll** zoom. The placement matrix (grid) reassigns unit types between
   formation zones per squad.

If mouse/keyboard do nothing, set **Edit ▸ Project Settings ▸ Player ▸ Active Input Handling**
to *Input Manager (Old)* or *Both* — the client uses the legacy `UnityEngine.Input` API.
The view is a pure projection of sim state and never writes back; sprites are generated at
runtime, so authored C&C-style art drops in later by swapping them per def id.

## Iron rules (same discipline as any lockstep RTS)

1. **Fixed-point only** (`Fix`, Q16.16) in sim code — no float/double/`System.Random`/LINQ in ticks.
2. **Everything mutates through Commands** (`CommandLog` → `CommandSystem`). Invalid commands
   reject and change nothing. UI/replays/network peers are all just command sources.
3. **Index-order scans only** — never enumerate a Dictionary/HashSet in tick code.
4. **New persistent state joins `Simulation.StateHash()` AND is reset in `SimWorld.Spawn()`.**
5. Distances/rates in JSON are integer centi-units and ticks (20 ticks/second).

## Implemented so far

- Deterministic tick loop with FNV-1a world fingerprint; replay = re-fed command log.
- Data-driven unit/building defs with tank/damage/speed/range/support role scores.
- Automated weighted production (players set composition, buildings build on their own);
  fresh combat units auto-reinforce the nearest swarm leader with capacity.
- Worker economy: gather from nodes, haul to headquarters.
- Movement, hard-body collision with push-resistance-weighted separation, auto-engage combat,
  HQ-death elimination.
- **Swarm system**: leaders command up to `maxUnitsPerLeader` (30) units, capped at 9 leaders
  per player (matching control groups). Squads are atomic (select any member = the squad;
  orders flow through the leader; the sim rejects member micro). Leader death → Leaderless
  (-25% move/attack) → retreat & auto-rejoin. Super-swarm LINKS: leader→leader trees with
  limb stations (default abreast or freeform-drawn), auto member rebalancing across the
  link, arrive-as-one pacing, and a spatial hierarchy grid with auto control groups.
- **Dynamic formations**: per-squad placement matrix (hashed def→zone assignments; Front /
  Rear / Flanks / Spread / Guard, code-built layouts, spine-centered) set from the command
  grid with live re-forming; Encircle stance wraps the nearest enemy. Freeform drag curves
  position multiple leaders along a drawn front; Shift+R-drag orders facing.
- **Combat**: attack-move posture (releases squads to fight on contact; plain moves never
  chase), squad cohesion damage bonus, DIRECTIONAL damage (front ×1 / side ×1.25 / rear
  ×1.5 vs the victim's simulated facing — units turn at data-driven turn speeds), visual
  projectiles for ranged units, death pops, health bars.
- **Tech paths**: 4 constructible hub add-ons (Lysis / Flagella / Toxin / Capsule), each
  producing a strong+cheap specialist unit and gating 2 purchasable upgrades. Upgrades are
  per-player hashed state (`PlayerState.UpgradeLevels`) that fold a Num/Den into one integer
  floor at the relevant hot path (damage, armor, move, attack-speed, range).
- **Unity client**: main menu (Skirmish/Settings live; Campaign/Multiplayer/Replay stubs),
  20 Hz fixed-tick driver with client-side game speed, pooled runtime sprites (diamonds =
  ranged, facing arrows), full command UI. The view is a pure projection of sim state.

Next up (per design doc): LOGISTICS/SUPPLY LINES (the untouched defining pillar), the
evolution landmark tier tree, spatial partitioning for the 8k-unit target, authored
animated sprites, then the multiplayer/replay layers the architecture already anticipates.

## Verify (from `newgame/`)

```
dotnet build Petri.slnx
dotnet test Petri.slnx
dotnet run --project src/Petri.Runner -- determinism --seed 42 --ticks 6000
dotnet run --project src/Petri.Runner -- run-match --seed 7 --ticks 12000
```

All four must pass before and after any change; `determinism` must print PASS for both the
fresh rerun and the replay.
