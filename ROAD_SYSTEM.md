# Bame Plastic — Endless Road System

The road the bus drives on is an **endless, procedurally-streamed, pooled-tile** system. It generates a
random, varied 4-lane road (with footpaths, ground shoulders, lane markings, corners and U-turns) that
streams seamlessly around the bus forever, with effectively zero per-frame cost and no visible loading.

This document is the single source of truth for how it works, why it's built this way, and the sharp
edges to respect if you change it.

---

## TL;DR — how to use it

1. **Menu: `Bame Plastic ▸ Create Tiled Road (Fast)`** — drops a `TiledRoad` GameObject with everything
   wired, builds a preview in the Scene view, and **seats the BusPlayer on the left lane** before Play.
2. Make sure **BusPlayer** is in the scene. Press **Play** → the road streams forever.
3. To change the layout, re-run the menu item, or use the `TiledRoadStreamer` gear-menu →
   **Rebuild Preview + Seat Bus**. Set a non-zero `seed` to pin a layout.

That's it. No runtime placement, no setup, no per-tile lag.

---

## Architecture

The road is a **chain of fixed-length tiles**. Each tile is its own GameObject with its own mesh and
collider, **built once** and **pooled**. Streaming = build a tile at the front, recycle a tile from the
back. Steady-state cost is *at most one tile's worth of work per recycle* — never a whole-road rebuild.

```
TiledRoad (GameObject)
├── RoadZone                 — cross-section widths (lanes/median/footpath/ground) + lane queries
├── TiledRoadStreamer        — the brain: procedural walk, tile pool, streaming, spawn, origin-shift
├── SplineStopSpawner        — Phase C: bus stops + waiting crowds on the left footpath
└── Tiles (child)
    ├── RoadTile             — one section: own mesh + collider, built once
    ├── RoadTile
    └── ...
FloatingOrigin (GameObject)  — keeps the world near 0,0,0 so far-from-origin float jitter never happens
```

### Components

**`RoadZone`** (`Assets/Scripts/Generation/RoadZone.cs`)
The DATA layer / single source of truth for the cross-section. All metres, road runs along local +Z,
cross-section laid along X centred on the median at X=0. Bangladesh is **left-hand traffic** so your
forward lanes are on **-X**.
- Fields: `lanesPerDirection`, `laneWidth`, `medianWidth`, `footpathWidth`, `groundWidth`, `leftHandTraffic`.
- Derived halves: `MedianHalf → DriveHalf → RoadHalf → GroundHalf` (outer edges, cumulative).
- Queries gameplay uses (never guess geometry — ask RoadZone):
  - `LaneCenterX(lane, forward)` / `LaneCenterWorld(...)` — where traffic/rivals drive.
  - `RandomFootpathLocal/World(leftSide)` — where stops/crowd/stalls go.
  - `RandomGroundLocal(leftSide)` — where buildings/props go (the ground shoulder).
  - `IsDrivable(localX)` — for "is the bus on the road" / AI.
- Gizmo: a thin cross-section ruler (green ground, grey footpath, dark lanes, yellow median).

**`TiledRoadStreamer`** (`Assets/Scripts/Generation/TiledRoadStreamer.cs`)
The orchestrator. Owns the procedural walk, the tile pool, streaming, the bus spawn, and the
floating-origin response. Key responsibilities:
- **Procedural walk** — advances a centreline one tile at a time, mixing straights / gentle curves /
  hard corners / U-turns (see *Variety* below).
- **Tile pool** — `_pool` of `RoadTile`s; `_live` is the ordered chain, `_live[0]` = front (newest,
  leading edge), `_live[Count-1]` = back (oldest, behind the bus).
- **Streaming** — keep `tilesAhead` tiles in front of the bus, recycle tiles `> tilesBehind` chain-positions
  behind it.
- **Bus spawn** — seats the bus on the left lane at the **middle** tile (edit mode + runtime).
- **Origin shift** — shifts every cached world position when `FloatingOrigin` recenters.
- Exposes `OnForwardAdvanced` (Phase C content hook), `Zone`, `TryGetLeadFrame`, `TryGetSpawnPose`.

**`RoadTile`** (`Assets/Scripts/Generation/RoadTile.cs`)
One pooled section. Built once per (re)use from a short run of centreline samples. Owns:
- A reused `Mesh` (4 submeshes: 0 road, 1 footpath/curb/median, 2 ground+slab, 3 lane markings).
- A `MeshCollider` cooked **off the main thread** (`Physics.BakeMesh` in a Task) into a separate bake mesh,
  assigned next frame — so streaming a tile never hitches. Synchronous cook in edit mode.
- Two loft paths: a proven **managed** loft (default) and an opt-in **Burst** path (`RoadLoftJob`).

**`RoadLoftJob`** (`Assets/Scripts/Generation/RoadLoftJob.cs`)
Burst-compiled, zero-GC vertex/index generation writing directly into a `Mesh.MeshData`. Opt-in via
`TiledRoadStreamer.useBurst`. The managed path in `RoadTile.BuildManaged` is the proven reference and
default; Burst is for extra headroom once you've confirmed the road looks right.

**`SplineStopSpawner`** (`Assets/Scripts/Gameplay/SplineStopSpawner.cs`)
Phase C bus stops. Hooks `OnForwardAdvanced`; every few sections drops a stop + waiting crowd on the left
footpath (borrowed from `PassengerPool`), runs the two-phase pickup (gather at the curb → board when the
bus is slow & close), recycles stops behind the bus. Reuses the existing `Passenger`/`BusPassengers`
boarding machinery untouched. Auto-creates a `PassengerPool` if one isn't in the scene.

**`FloatingOrigin`** (`Assets/Scripts/Environment/FloatingOrigin.cs`)
When the bus passes `threshold` (default 1000 m) from world origin, shifts **every root object** by
`-busPos` so the bus snaps back near 0,0,0. Velocities are unaffected (motion is seamless); the shift is
invisible because the whole world moves together.

---

## Variety — straights, curves, corners, U-turns

The walk has two layers:
- **Baseline curving** — `maxTurnRate` (°/30 m), occasionally re-rolled (`behaviourChangeChance`,
  `straightChance`, `wobble`). Gives gentle, ever-changing curves.
- **Major turns** — `MaybeStartSharpTurn` rolls once per tile (`sharpTurnChance`, `sharpTurnCooldown`) to
  commit to a hard sustained turn that holds `sharpTurnRate` until it has swept a target angle
  (70°…`sharpTurnMaxSweep`). 90° = a corner, 180° = a U-turn.

**Turn rate is degrees per 30 m of road** (a fixed reference distance), so the road curves the same amount
regardless of `tileLength` or `ringsPerTile`.

⚠️ **U-turn radius vs road width:** a U-turn's turn radius must exceed the road half-width or the two legs
overlap themselves. At `sharpTurnRate ≈ 35` the radius (~49 m) clears the ~20 m half-width of a default
40 m-wide road. Push `sharpTurnRate` higher for tighter corners only if you also reduce road width or cap
`sharpTurnMaxSweep` to ~120 (corners, no full U-turns).

---

## Seamless joins (why there are no gaps or bumps)

- **Position continuity:** each tile's FIRST ring reuses the previous tile's LAST sample point + forward
  (`_carryPt` / `_carryFwd`). Adjacent tiles therefore share an identical boundary ring — same position
  AND same frame — so the join is watertight.
- **Normal continuity:** the top surface + lane markings are forced to flat-up normals after
  `RecalculateNormals` (both managed and Burst). Without this, each tile's edge ring gets a slightly
  different normal on curves → a visible lighting crease at the seam.
- **No end caps:** tiles do NOT build transverse end caps. Adjacent tiles abut, so a cap at each end would
  be a redundant vertical wall at the seam — and over the road (which dips below curb height) it pokes up
  as a thin protruding line. The road is continuous; only the two despawning extremes are open, off-screen.

---

## Streaming logic (the part that took the most iteration)

The bus's position is tracked as a **chain index** (`_busTileIndex`), NOT by world heading and NOT by
global nearest-distance. This was hard-won — see *Bug history* below. Rules:

- **Track:** a **bounded-window nearest search** (`±trackSearchRadius` tiles around the last index). It
  can't stall in a local minimum on a curve, and being bounded it can't jump to a U-turn's far leg.
- **Extend:** while fewer than `tilesAhead` tiles are in front of the bus, `AppendTileAhead`. New tiles
  always attach to `_carryPt` (the front tile's end), so **the chain can never split at build**.
- **Recycle:** drop back tiles that are BOTH far in chain order (`> tilesBehind`) AND physically far from
  the bus (`> tileLength*(tilesBehind+1)`). The physical guard is a safety net: even if the tracked index
  briefly drifts, a tile near the bus is never dropped, so the bus can't be stranded.

**Diagnostic gizmo** (select `TiledRoad` in Play): chain drawn green (front) → red (back), white connectors
between tiles (a long white line = a real split), magenta sphere on the tile the streamer thinks the bus
is on, with a line to the actual bus.

---

## Bus spawn (seated in the editor)

The reliable answer after several "spawn in the sky" regressions: **seat the bus at edit time**, not at
runtime.
- `RebuildEditorPreview` (the create menu + the gear-menu **Rebuild Preview + Seat Bus**) builds the tiles
  AND calls `PlaceBusInEditor`, which puts BusPlayer on the **left lane at the middle tile** in the Scene
  view. So before Play, the bus is already correctly seated.
- Runtime-created meshes don't survive the edit→play domain reload, so the road IS rebuilt at runtime — but
  **deterministically** so it reproduces the exact preview: `seed > 0` pins it; `seed == 0` uses a
  persisted `_autoSeed` (random-looking but stable). The centreline always builds at **world Y=0**
  regardless of the object's transform Y, so the road never sits elevated.
- Spawn pose is **geometric** (Y = lane-surface centreline Y) — no raycast (raycasts depended on the async
  collider bake / could hit the bus). Runtime `TryPlaceBus` re-asserts the same pose and **waits** until
  the bus is fully initialised (`BusController.Start` detaches the sphere → `sphere.transform.parent == null`)
  before teleporting, or the rig desyncs.

---

## Floating origin (required here)

The bus drives unbounded (unlike the old chunk treadmill where content moved past a near-stationary bus),
so far-from-origin float imprecision (jitter, unstable sphere physics, shimmering seams) WILL happen
without recentring.

`FloatingOrigin.Shift(delta)`:
1. Move **every** root object by `delta` in ONE uniform pass — road, stops, pool, bus root, AND the
   detached physics sphere (its own root at runtime). Do NOT shift the bus "separately" on top of the
   generic pass — that double-shifts it (to -2×threshold) or desyncs the sphere into the sky.
2. Re-sync the sphere's Rigidbody position to its transform and flush interpolation (so the interpolated
   bus doesn't smear from the old far pose for one frame).
3. Notify systems that cache WORLD positions so they don't snap back to the pre-shift spot:
   `TiledRoadStreamer.OnOriginShifted` (shifts `_lead.head`, **`_carryPt`**, and every span endpoint),
   `SplineStopSpawner.OnOriginShifted` (shifts cached stop positions).
4. `Physics.SyncTransforms()` once (one controlled broadphase update; skipping it stalls the next
   FixedUpdate's raycasts → a "game paused" spike when the big road colliders teleport).

⚠️ **Every cached world position must be shifted.** Forgetting `_carryPt` made the next tile spawn at the
pre-shift spot ~1 km away — the "parallel road far away" bug. If you add new cached world state, shift it
here too.

---

## Performance notes

- **Pooled, build-once:** no whole-road rebuild; steady state builds ≤ `maxTilesPerFrame` tiles/frame.
- **Off-thread collider bake:** `Physics.BakeMesh` on a worker into a separate frozen mesh (never the live
  mesh the main thread mutates — that caused a "degenerate triangle" race). One bake in flight at a time.
- **Reused meshes:** the render mesh is reused per tile (`MarkDynamic` + `Clear`), not reallocated.
- **Zero-GC option:** flip `useBurst` for the Burst loft path once the road looks right.
- New tiles appear `tilesAhead` tiles in front of the bus, so a one-frame collider delay is invisible.

---

## Tunables (on `TiledRoadStreamer`)

| Field | Default | Effect |
|---|---|---|
| `tileLength` | 60 | section length (m); bigger = fewer joins, smoother long curves |
| `ringsPerTile` | 12 | mesh density along a tile; keep ≈ tileLength/5 for smooth curves |
| `tilesAhead` / `tilesBehind` | 6 / 3 | how much road in front of / behind the bus |
| `maxTilesPerFrame` | 3 | catch-up budget; tiles are cheap |
| `maxTurnRate` | 14 | baseline curviness (°/30 m) |
| `behaviourChangeChance` / `straightChance` / `wobble` | 0.4 / 0.45 / 3 | baseline walk variety |
| `sharpTurnChance` / `sharpTurnRate` / `sharpTurnMaxSweep` / `sharpTurnCooldown` | 0.12 / 35 / 170 / 220 | major turns & U-turns |
| `trackSearchRadius` | 4 | bus-index search window; raise if very fast |
| `seed` | 0 | 0 = stable auto-seed; set to pin a layout |
| `useBurst` | false | Burst loft path |
| widths / curbHeight / roadThickness / marking* | — | cross-section + paint (most live on RoadZone) |

---

## Bug history (so we don't repeat it)

| Symptom | Cause | Fix |
|---|---|---|
| Bus spawns in the sky (intermittent) | Runtime teleport raced async collider bake / bus init order; raycast spawn hit the bus or read a stale Y | Seat the bus in **edit mode**; geometric spawn Y; build centreline at world Y=0; wait for sphere detach before teleport |
| Bus flung to sky / to -2×threshold on recenter | Bus shifted twice (generic pass + separate `ShiftBy`) or sphere desynced | One uniform shift of all roots; re-sync sphere + flush interpolation; removed `ShiftBy` |
| "Game paused" spike on recenter | Implicit physics sync mid-FixedUpdate when big colliders teleported | `Physics.SyncTransforms()` once after the shift |
| Visible/bumpy seam on the road only | Per-tile transverse end caps protruded over the road at joins | Removed end caps (both managed + Burst) |
| Seam lighting crease on curves | Each tile recalculated boundary normals independently | Force top + marking normals to flat-up |
| Road too straight / dull | Turn scaled per ring-step (tiny) | Turn rate is °/30 m; added sharp turns / U-turns |
| Gap, then road building far away | Heading-based "ahead/behind" broke on curves | Chain-index streaming |
| Parallel road far away (after a while / a U-turn) | Single-step hill-climb tracker stalled in a curve's local minimum → recycled wrong tiles | Bounded-window tracker + physical recycle guard |
| Big connector gap after ~driving a while | `OnOriginShifted` shifted everything **except `_carryPt`**, so the next tile spawned at the pre-shift spot | Shift `_carryPt` too |
| "Degenerate triangle" collider error | Worker baked the live mesh while the main thread cleared it | Bake a separate frozen mesh; one bake in flight; degenerate guard |

---

## Phase C hooks (next work)

- **Buildings/props:** scatter on the ground shoulders via `RoadZone.RandomGroundLocal`, pooled, off
  `OnForwardAdvanced`, recycled like the stops.
- **Traffic / rivals:** drive in lanes via `RoadZone.LaneCenterX`; hook into the existing
  `TrafficSpawner` / `RivalBus` / `Obstacle` (bus health on collision).

Both hang off the same `OnForwardAdvanced` + `RoadZone` offsets the stops already use.
