# Feature: Basement / Below-First-Floor Building (INVESTIGATION — SHELVED)

**Status:** 🔬 INVESTIGATED & SHELVED (2026-06-08). Not started; no code. Verdict:
a **true, playable basement is NOT feasible from a server-side mod** — the data
layer is reachable, but the things that make a basement usable (terrain, camera)
are **client-side** and outside a server mod's reach. Documented here so we can
revisit if a client-side companion (BCH) ever takes it on, or if the engine adds
terrain/camera support. Adjacent server-side options that ARE feasible are listed
at the bottom.

## The question

Can a V Rising **dedicated-server** BepInEx/IL2CPP plugin let players build
*below* the ground/first floor of their castle — dig out a basement, build
downward?

## Verdict (short)

No, not in a way that is playable. The castle building **data model** supports
vertical stacking and could be coaxed into describing a sub-ground floor, but the
result would render **buried inside solid terrain** with the camera looking at the
surface — and terrain geometry + camera are **client-rendered, non-replicated**
state a server mod cannot touch.

## Why — two layers, only one of which the server owns

V Rising castle building splits into a **data layer** (server-authoritative) and a
**presentation layer** (client-only).

### Data layer — the server CAN manipulate this
- Pieces sit on a 2D tile grid: `ProjectM.TilePosition { Tile (int2),
  TileRotation, CompressedHeight }`. `ObjectSpawnService.ApplyTransform` currently
  hardcodes `CompressedHeight = 0`.
- `ProjectM.CastleBuilding.CastleFloor` is a first-class **vertical** component —
  it carries directional neighbour links including **`NeighbourFloorUp` /
  `NeighbourFloorDown`**, plus `FloorType`, `RoofType`, and `GroundConnectionType`.
- Stairs come in `Upper`/`Lower` variants (`CastleStairs.StairsType` =
  `PartTopFloor` / `End` / …), so up/down traversal is a native concept.
- `GroundConnectionType` is the "this floor rests on terrain" gate — the ground
  floor needs terrain contact; upper floors don't.

So the server could, in principle, write a lower/negative height and wire
`NeighbourFloorDown`. The *data* can describe a basement.

### Presentation layer — the server CANNOT reach this (the wall)
- **Terrain is a fixed, baked mesh.** V Rising has **no runtime terrain
  deformation** anywhere — the game's own underground/cave areas are hand-authored
  static geometry, not dug at runtime. A floor tile placed below ground renders
  *inside solid terrain*, not in an open carved room. The server cannot carve,
  hole, or hide terrain: that geometry is local to each client's map, not
  replicated state.
- **The camera is fixed top-down isometric.** Even if a sub-surface room existed,
  the player would be looking at the terrain surface above it. There is no
  "descend into the room" camera behaviour, and the server can't add one.
- Lighting, occlusion, and the build-preview ghost are all client-computed.

This is the crux, and it is on the wrong side of the network boundary for a server
mod. It is also why this is fundamentally different from **stair hot-swap** and
**object spawning**, which work precisely because they manipulate entities that are
*already in a valid, renderable state*. A sub-terrain floor asks the client to
render something it has no path to render correctly.

## What IS feasible server-side (adjacent options, if the goal is "more vertical")

The engine supports stacking **upward** natively — the direction terrain + camera
already accommodate. Genuinely doable from Uriel:

1. **Raise / remove the vanilla floor-count cap** — let players build *more floors
   up* than vanilla allows. Rides the supported render path; highest-value,
   lowest-risk "more vertical building." (Find the floor-limit check; patch it.)
2. **"Sunken" rooms on naturally-low terrain** — place pieces at a lower-but-still-
   above-local-ground height where terrain already slopes down. A basement
   *aesthetic* without going under the mesh. Finicky and location-dependent.
3. **Decorative `CompressedHeight` offset** on spawned props (extends Object
   Spawning) — useful for decoration, never for walkable floors.

A literal underground floor would only work paired with a **client** mod that hides
terrain and re-aims the camera (BCH territory), and even then terrain occlusion is a
hard problem.

## If we ever revisit — the cheap first step

A **live spike**, not design: spawn one floor tile with a lowered/negative
`CompressedHeight` on the test server and look at it in-game. ~15 min confirms (a)
whether placement validation even accepts a sub-zero level, and (b) the
terrain-clipping prediction firsthand — before investing in anything. Uses the
existing owner-live-test loop.

## Key references (read-only Beelzebub workspace)
- `Reference Data/Prefabs/TM_Castle_Floor_Foundation_Stable02 PrefabGuid(2025363113).txt`
  — `CastleFloor` (FloorType/GroundConnectionType/NeighbourFloor{Up,Down,…}),
  `TileModelSpatialData`, `TilePosition.CompressedHeight`.
- `Reference Data/Prefabs/TM_Castle_Stairs_Single_Upper_*` / `…_Lower_*` —
  `CastleStairs.StairsType` up/down variants.
- Uriel `Services/ObjectSpawnService.cs` — `ApplyTransform` (CompressedHeight=0),
  `CheckPlacement` (territory + ownership only; no floor/height checks).
