# Feature: Stair Hot-Swap

**Status:** RESEARCHED — mechanism identified, implementation route chosen pending POC
**Config:** `[StairSwap] Enabled` (default `true`)

## Problem

In vanilla V Rising castle building, most tile sets (walls, floors, windows)
can be re-skinned/swapped in place, but **stairs cannot**: changing a stair to
another stair type requires demolishing the existing stair and placing a new
one. This is an inconsistency in the building system, not a balance decision —
confirmed as a standing community feature request
(feedback.playvrising.com/suggestions/635433/stair-replacement).

## Prior art (researched 2026-06-06)

**No existing mod implements stair swapping or any in-place tile-type
replacement.** Thunderstore search for "stair" returns zero mods. Closest
neighbors, all open source and useful as references:

- **KindredSchematics** (odjit) — spawns/deletes/moves individual tiles
  (`Services/BuildService.cs` → `PlaceOrGrabObject()` shows how to instantiate
  a tile prefab with correct `Translation`, `Rotation`, `TilePosition`,
  `TileRotation`, ownership via `CastleHeartConnection` + `Team` + `UserOwner` +
  `TeamReference`). No in-place replacement, no stair handling.
- **PalacePrivileges** (cheesasaurus, ProfuselyViolentProgression repo) —
  patches `PlaceTileModelSystem.OnUpdate`; its `notes/misc.md` is the best
  available documentation of the tile/build internals.

## Mechanism findings

### The system

`PlaceTileModelSystem` (server) is the single system that owns ALL build
events via separate event queries: **BuildTile, StartEdit, CancelEdit,
MoveTile, DismantleTile, RepairTile, BuildWallpaper, SetVariation,
AbilityCastFinished**. `BuildTileModelEvent` carries `PrefabGuid` +
`SpawnTranslation`; the acting player arrives as `FromCharacter`.
This is the patch point for any build-menu-integrated approach.

Supporting internals (from PalacePrivileges' notes): `GetPlacementResult` /
`ApplyPlacementResult` (placement), `GetPlacementResourcesResult` /
`ApplyPlacementResourcesResult` (cost/refund), `CreateTileModelData`,
`CurrentTileModelEditing` (component on the player: `Entity TileModel`),
`TileData`/`TileBlob`, `PlacementTypeBasicFlags`, `TileUtility`,
`RoomUtility`, `SpaceConversion`.

### Why walls swap but stairs don't

- **`PlacementData` has a `replaces` category** alongside requirements/
  restrictions/attachesTo. This is the prime suspect: wall/floor placements
  declare what they may replace in place; stair placements presumably don't.
  → First investigation step: dump/inspect `PlacementData` for a wall vs. a
  stair blueprint and confirm.
- Prefab-dump comparison found **no swap/variant component** on either walls
  or stairs — swap capability is data-driven (placement data / blueprint
  config), not component-driven. Stair prefabs across tilesets are
  structurally identical (component lists match; only costs/art differ), so
  an in-place replace is structurally safe at the component level.

### Stair prefab landscape (from the dump)

- **`BP_Castle_Stairs_*`** — the placeable blueprints (24): `Single`,
  `Single_CW`, `Single_CCW`, `Double` × tilesets `Stone01-03`,
  `DLC_Gloomrot01`, `DLC_ProjectK01`, `DLC_StrongbladeDLC01`.
  e.g. `BP_Castle_Stairs_Single_Stone01` = GUID `-541385494`,
  `BP_Castle_Stairs_Double_Stone01` = `77571580`.
- **`TM_Castle_Stairs_*`** — modular placed segments (Lower/Upper ×
  Start/Mid/End × CW/CCW, Double variants L/R, 18 total). A placed staircase
  is a *fused composite*: blueprints carry `CastleBuildingFusedChildrenBuffer`
  / `CastleBuildingFusedRoot`.
- Blueprint cost lives in `BlueprintRequirementBuffer` (e.g. Stone Brick ×24).
- Key components on a stair blueprint: `EditableTileModel`, `TileModel`,
  `TilePosition`, `TileBounds`, `TileModelSpatialData`, `Team`/`TeamReference`,
  `UserOwner`, `CastleHeartConnection`, `LastEditedBy`, `TileHeightTag`
  (stairs-only vs. walls), `DismantleDestroyData`, `PlacementDestroyData`.

### Targeting a placed stair (for the command route)

KindredCommands pattern (`Helper.cs:135-167`, `ServantCommands.cs:18-51`):

```csharp
var aimPos = ctx.Event.SenderCharacterEntity.Read<EntityAimData>().AimPosition;
// then spatial lookup via GenerateCastleSystem._TileModelLookupSystemData
//   .GetSpatialLookupReadOnlyAndComplete(...).GetEntities(ref bounds, TileType.All)
// filter: Has<TilePosition>, prefab name StartsWith("TM_"/"BP_"), closest distancesq
```

## Implementation routes (decide after `replaces` investigation)

**Route 1 — build-menu integration (ideal UX):** patch
`PlaceTileModelSystem`'s build/placement validation so a stair blueprint
placed onto an existing stair is treated as a replace: capture the old
stair's transform + ownership, dismantle-refund it (or charge a delta),
place the new one at the identical `TilePosition`/`TileRotation`. Open
question: does the client even *send* the build event when targeting an
occupied stair cell, or does client-side placement validation reject it
before any event reaches the server? If client-blocked → Route 2.

**Route 2 — command-driven (guaranteed server-side feasible):**
`.uriel stairswap <type>` — aim at the stair, resolve via EntityAimData +
spatial lookup, verify ownership (`Team`/`UserOwner` match the caller),
capture transform + ownership, `DestroyUtility.Destroy(...)` the old fused
root, instantiate the new `BP_Castle_Stairs_*` with preserved
`Translation/Rotation/TilePosition/TileRotation/Team/TeamReference/UserOwner/
CastleHeartConnection` (KindredSchematics `PlaceOrGrabObject` is the model).

## Constraints

- **Server-side only** — no client UI additions possible.
- Only the castle's owner/clan (whoever could demolish + rebuild legally) may
  swap; respect `CastleAreaRequirement`/territory checks.
- Cost model TBD: free swap vs. material delta vs. full-cost-with-refund
  (`GetPlacementResourcesResult` handles vanilla cost/refund — reuse it).
- Must not corrupt castle persistence: stairs are fused composites — swap the
  *blueprint/fused root*, never an individual `TM_` segment.

## Open questions

- [ ] Confirm `PlacementData.replaces` content for stairs vs. walls (dump or
      runtime inspection).
- [ ] Does the client send `BuildTileModelEvent` targeting an occupied stair
      cell? (Decides Route 1 vs. Route 2.)
- [ ] Destroy-then-spawn atomicity: can both happen in one frame without the
      castle grid recomputing in between (room detection, connectivity)?
- [ ] Cost model decision (lean: charge new blueprint cost, refund old —
      vanilla dismantle+build economics, zero exploit surface).

## Test plan (fill in during implementation)

- [ ] Swap each stair archetype (Single/CW/CCW/Double) → each other tileset.
- [ ] Swap on multi-floor connected stairs — verify pathing/connectivity/rooms.
- [ ] Save, restart server, verify swapped stairs persist correctly.
- [ ] Non-owner attempts swap → denied.
- [ ] Decayed/enemy territory → denied.
- [ ] Feature disabled in config → pure vanilla behavior.
