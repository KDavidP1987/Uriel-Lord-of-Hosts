# Feature: Stair Hot-Swap

**Status:** REIMPLEMENTED v0.12.0 (2026-06-07) — vanilla-pipeline route; pending live validation

## Mechanism revision (v0.12.0) — Route B failed live; Route A (vanilla events) is the answer

Two live tests proved raw `Instantiate` + component copy (Route B, even with
the v0.9.0 StaticTransform-index fix) produces "permanent" stairs the build
UI can't highlight or dismantle — the placement pipeline wires territory
connections, attach graphs, registration, and history that can't be
replicated by hand. v0.12.0 switched to the originally-fallback Route A:
refund the blueprint cost (`BlueprintRequirementBuffer` → player inventory) →
`DestroyUtility` the old root → 3 frames later fire a synthesized
`BuildTileModelEvent` (`FromCharacter` + `NetworkEventType
{ EventId_BuildTileModelEvent }` + `ReceiveNetworkEventTag`,
`ResourceConsumeType.LocalInventory`) → verify ~45 frames later and report
via chat. Net-zero economics; failure leaves the player holding the refund.
**Config:** `[StairSwap] Enabled` (default `true`), `[StairSwap] MaxTargetDistance` (default 6)
**Code:** `Services/StairSwapService.cs`, `Commands/StairCommands.cs`

**DLC decision (owner, 2026-06-07):** per-user entitlement — a player may only
swap TO a style in their own build menu. Implemented via
`prefab.ProgressionUserContentDependency.Value` checked against
`User.UserContent` with `UserContentUtility.HasUnlocked` (no AllowDlcStyles
server-wide override; the build-menu rule is the rule).

## Problem

Vanilla castle building can re-skin walls/floors/windows in place, but stairs
must be demolished and rebuilt to change style. Standing community feature
request (feedback.playvrising.com/635433); **no existing mod implements it**
(Thunderstore swept 2026-06-06).

## Swap rule (owner requirement)

A placed stair may only swap to another **cosmetic of the SAME archetype**:

| Archetype | Meaning | Cost (vanilla) |
|---|---|---|
| `Single` | narrow straight | 12× Stone Brick |
| `Single_CW` | narrow right-curve (clockwise) | 18× Stone Brick |
| `Single_CCW` | narrow left-curve (counter-clockwise) | 18× Stone Brick |
| `Double` | wide straight | 24× Stone Brick |

## The cosmetic matrix (confirmed complete — 4 × 6 = 24, no stragglers)

| Archetype | Stone01 | Stone02 | Stone03 | DLC_Gloomrot01 | DLC_ProjectK01 | DLC_StrongbladeDLC01 |
|---|---|---|---|---|---|---|
| **Single** | -541385494 | 1267405974 | 1019577856 | -1808336362 | -601630508 | 1831814293 |
| **Single_CW** | -628212401 | 891240110 | 1737385414 | -671167268 | -2111252824 | 54801101 |
| **Single_CCW** | -1323146211 | 171455823 | 2042236287 | 249484894 | 787873859 | 1748214728 |
| **Double** | 77571580 | -887317616 | -1186795702 | -1940654627 | 384472583 | -2042297302 |

(Prefab names: `BP_Castle_Stairs_<Archetype>_<Cosmetic>`.)

### Swap-safety verdict (prefab diff, 2026-06-07)

Cosmetics within an archetype are **structurally identical** — same component
list, TileBounds, physics, placement flags, fused structure, and identical
build cost. The ONLY diff: DLC variants carry
`ProjectM.ProgressionUserContentDependency` (e.g. `Value: DLC_Gloomrot`) —
the DLC-entitlement marker. Swapping within an archetype is cosmetics-only
and structurally safe. Across archetypes: different costs/footprints — never
allowed (enforced by the archetype rule anyway).

## Implementation route — DECIDED: manual surgery (KindredSchematics pattern)

Two routes were evaluated against the reflected API of our exact reference
assemblies (1.1.12-r99041-b2):

**Route A — vanilla events** (`DismantleTileModelEvent` +
`BuildTileModelEvent` via `PlaceTileModelSystem`): full vanilla validation
(ownership, blueprint unlock incl. DLC, resources, `BlockReason` taxonomy),
but a swap needs dismantle-then-build across frames and the build event
validates against cell occupancy — ordering is fragile. Kept as fallback.

**Route B — manual component surgery (CHOSEN):** KindredSchematics does ALL
tile spawn/move/delete this way in production — **zero use of the game's edit
events** — and the game **self-registers** spawned tiles (no manual
spatial-grid registration needed). The swap becomes a single-frame operation:

1. **Resolve target**: aim → closest `TilePosition` entity → walk
   `CastleBuildingAttachToParentsBuffer` / `CastleBuildingFusedChild.ParentEntity`
   up to the `CastleBuildingFusedRoot` whose `PrefabGUID` is `BP_Castle_Stairs_*`.
2. **Parse archetype + verify** caller controls the castle (heart-team check,
   same as public storage).
3. **Capture** from the old root: `Translation`, `Rotation`, `TilePosition`
   (`.Tile` + `.TileRotation`), `TileBounds`, `StaticTransformCompatible`
   (`.NonStaticTransform_Pos/_Height/_Rotation`, `.UseStaticTransform`),
   `CastleHeartConnection`.
4. **Spawn new**: `Core.PrefabCollectionSystem._PrefabGuidToEntityMap
   .TryGetValue(newGuid, out prefab)` → `EntityManager.Instantiate(prefab)` →
   write all captured transform/tile components; rotation quaternion =
   `quaternion.RotateY(math.radians(90 * (int)TileRotation))` (no lookup
   table — TileRotation enum is 0–3; `TileUtility.AsQuaternion` also exists).
5. **Ownership**: write `CastleHeartConnection { CastleHeartEntity = heart }`;
   `Team { Value = heartTeamData.TeamValue, FactionIndex = -1 }`;
   `TeamReference.Value._Value = heartTeamRefEntity`; copy heart's `UserOwner`.
   (Exact KindredSchematics `SetOwnerForEntity` shape.)
6. **Destroy old root** with `DestroyUtility.Destroy` — destroying the root
   handles the fused TM_ segments. **CRITICAL DIFFERENCE from KindredSchematics'
   delete:** their `DestroyEntityAndCastleAttachments` walks attach-PARENTS
   and destroys them too (delete semantics) — a swap must NEVER do that (it
   could take out the floor the stair attaches to). Destroy ONLY the stair
   root (+ fused children if not auto-cascaded — verify in test).
7. Game systems re-register the new tile automatically.

### Cost model

Same-archetype cosmetics cost identically (vanilla swap economics = zero-sum),
so the swap is **free** — matching how wall re-skins behave. No resource
handling needed in v1. (`GetPlacementResourcesResult`/`ApplyPlacementResourcesResult`
exist if a fee is ever wanted.)

### DLC gating (open design question)

DLC cosmetics carry `ProgressionUserContentDependency`. Route B bypasses the
vanilla `HasUnlockedBlueprint`/`_ProgressionDependencySystem` check, so a
swap could grant DLC looks to non-owners. Plan: server-wide config
`StairSwap.AllowDlcStyles` (admin's choice; default TBD by owner). Per-user
entitlement checking may be possible (the platform logs per-user
`UserContentFlags`) — investigate `User`/platform components during
implementation; if readable, add `RequireDlcOwnership` mode.

## Command UX (proposed)

```
.uriel stairswap <style>     style ∈ stone1 | stone2 | stone3 | gloomrot | projectk | strongblade
.uriel stairswap next        cycle to the next style in the same archetype
.uriel stairstyles           list styles + which one the aimed stair has
```

Aim at any part of the staircase; archetype is auto-detected from the root's
prefab name; the swap preserves position, rotation, and ownership exactly.

## Key API references (verified in 1.1.12-r99041-b2)

- `PlaceTileModelSystem` — `_BuildTileQuery`/`_DismantleTileQuery`/… event
  queries; `VerifyCorrectOwnership/Team`, `HasUnlockedBlueprint` (Route A).
- `BuildTileModelEvent { PrefabGuid, SpawnTranslation, SpawnTileRotation,
  VariationIndex, ResourceConsumeType, RebuildUniqueKey }`;
  `DismantleTileModelEvent { NetworkId Target }`.
- `TilePosition { int2 Tile, TileRotation TileRotation, UInt16 CompressedHeight }`;
  `ProjectM.Tiles.TileRotation` enum None/CW90/CW180/CW270.
- `ProjectM.Tiles.TileUtility` — `AsQuaternion(TileRotation)`,
  `GetTileRotationFromQuaternion`, snapping math.
- `ApplyPlacementResult.Execute/CreateTileModels/DestroyTileModels/MoveTileModel`
  — the engine's own mutation API (heavier alternative to manual surgery).
- `CastleBuildingFusedRoot` (tag), `CastleBuildingFusedChildrenBuffer
  { NetworkedEntity ChildEntity }`, `CastleBuildingAttachToParentsBuffer
  { NetworkedEntity ParentEntity }`, `CastleBuildingFusedChild { ParentEntity }`.
- `BlueprintRequirementBuffer { PrefabGUID, Int32 Amount }`;
  `EditableTileModel` (CanDismantle/CanRotateAfterBuild/… flags).
- KindredSchematics (AGPL-3.0, github.com/odjit/KindredSchematics):
  `Services/BuildService.cs` (PlaceOrGrabObject, SetOwnerForEntity),
  `Services/SchematicService.cs` (SpawnEntity — canonical component-write list),
  `Helper.cs` (DestroyEntitiesForBuilding — note the parent-walk caveat above).

## Test plan (for implementation)

- [ ] Swap each archetype Stone01 → Stone02/03 → each DLC style → back.
- [ ] Swap preserves position, rotation (all 4 TileRotations), and floor
      attachment; no orphaned TM_ segments; no destroyed neighbors/floors.
- [ ] Multi-floor connected staircase: pathing + room detection intact.
- [ ] Save/restart: swapped stair persists; no duplicate entities.
- [ ] Non-owner denied; decayed/enemy territory denied.
- [ ] Aiming at a non-stair → helpful error; archetype mismatch impossible.
- [ ] `StairSwap.Enabled=false` → command refuses.
- [ ] DLC style swap honors `AllowDlcStyles` config.
- [ ] Servants/players standing on the stair during swap (collision blip?).
