# Feature: Stair Hot-Swap

**Status:** WORKING v0.14.x (2026-06-07) — **destroy + respawn, applies LIVE**
(no restart). Validated on straight, curved, and wide shapes. `.uriel stairswap`
is player-facing. **Open investigation (2026-06-08):** a possible clan
ownership-transfer edge with swapped stairs — see "🔍 OPEN INVESTIGATION" below
(awaiting a player's repro/logs; swapped stairs are confirmed castle-owned, not
caster-owned).

## Final mechanism (v0.14.x): DESTROY + RESPAWN (live)

`SwapViaRespawn` / `ExecuteRespawn` in `Services/StairSwapService.cs`. A placed
staircase is a fused structure: one `BP_Castle_Stairs_<archetype>_<style>` root
+ N `TM_Castle_Stairs_*` fused-child segments (the rendered geometry). The swap:

1. **Capture** the whole structure before touching it — per piece: `Translation`,
   `Rotation`, `TilePosition`, `TileBounds`, `StaticTransformCompatible`,
   `Team`/`TeamReference`/`UserOwner`/`CastleHeartConnection`, and each child's
   `CastleBuildingAttachToParentsBuffer` (the floors/walls it connects to). The
   root is captured with the **target style** prefab; children keep their own
   (style-agnostic) prefabs, so shape is preserved exactly.
2. **Safety gate:** confirm every prefab resolves BEFORE destroying anything — a
   missing prefab can never leave a player with a deleted staircase.
3. **Destroy** every piece (root + children) via `DestroyUtility.Destroy` (clean
   tile-grid deregister; no ghost claims). It does NOT walk attach-parents (that
   would delete the floors the stair rests on).
4. After **`[StairSwap] RespawnGapFrames`** (default 5) — so clients register the
   removal first — **re-Instantiate** the root from the target-style prefab and
   each child from its own prefab, restore the captured placement/ownership,
   re-wire the fused parent/children, and re-attach to the same floors.

The new pieces get **fresh NetworkIds**, so connected clients build them from
scratch and render the new style **live** — which is exactly what a server
restart does, but for one staircase.

### Why the in-place identity swap (v0.13.x) couldn't do this — corrected

The v0.13.x theory ("placed tiles are MegaStatic; visual baked at load; only a
restart refreshes") was **wrong**, proven by live `.uriel stairrefresh` dumps:
placed stairs are ordinary `NetworkId.Type=Normal` entities, not MegaStatic.
The real constraint: the client binds a placed stair's rendered look to the
entity at FIRST receipt of its `NetworkId` and never re-derives it from any
server-side data change — relog, area-reload, re-stream blink, and re-baking
`TileData`/`NetworkedPrefabChildren` blobs were all tested and did nothing. Only
a genuinely NEW entity (new NetworkId) refreshes it. Hence destroy + respawn.
(`Instantiate(BP_root)` alone is insufficient — the fused children spawn via
`SubScenePrefabSpawnerSystem` on the load path, so we spawn + wire them by hand.)

**Trade-off:** the swap is now destructive and the stair becomes a NEW entity
(BCH must not cache stair entity ids across a swap). Known v1 simplification:
the attachment/decay/pathing graph is restored minimally — refine if stairs
float, decay, or mis-path. The legacy non-destructive identity swap
(`Swap`/`ExecuteSwap`) remains in the file, unwired, as a fallback reference.

## 🔍 OPEN INVESTIGATION (2026-06-08): clan ownership of swapped stairs

**Trigger:** a server owner relayed that a player testing with friends hit "some
issue with the stairs," vaguely "when leaving Claude/a clan or switching clans."
No details/logs yet — they're trying to reproduce. The mod owner asked a precise
sub-question: in a 4-person clan, when a non-owner member swaps a stair, the
result must belong to the **castle**, not the individual.

**Verdict on that sub-question — by design AND in code, the stair stays
castle-owned, not caster-owned. The current implementation is correct here.**
- `.uriel stairswap` runs the respawn path (`SwapViaRespawn` → `ExecuteRespawn`).
  `Capture()` reads `Team` / `TeamReference` / `UserOwner` / `CastleHeartConnection`
  **from the existing stair** (`StairSwapService.cs:505-508`) and `SpawnPiece()`
  writes those same values onto the new entity (`:533-536`). So the new stair
  inherits the castle's ownership, never the caster's.
- The caster (`character`/`userEntity`) is used for exactly two things, neither of
  which writes ownership: the **permission** check
  (`PublicStorageService.CharacterControlsContainer` — caster's `Team` == the
  castle heart's `Team`; clanmates share the clan team, so any member passes) and
  the **DLC entitlement** check (`UserOwnsStyle`).

**Open hypothesis for the friend's actual bug (UNCONFIRMED — needs his repro/logs).**
Since v0.14.0 the swap **destroys the stair and spawns a fresh entity**, copying
the castle's ownership directly rather than going through the vanilla placement/
registration pipeline (it sets `CastleHeartConnection` + re-wires the fused/attach
buffers by hand). If V Rising's castle-ownership **transfer** (a player leaving a
clan, a clan disbanding, a castle being claimed) re-stamps team/owner only on the
pieces the **castle heart has REGISTERED**, a respawned stair that isn't fully in
that registry could be **skipped** by the transfer and keep the now-defunct team —
surfacing as a foreign/"enemy" staircase inside the new owner's castle, or one
nobody can dismantle/swap. That matches "stairs broke after leaving/switching clans."
- Caveat that *weakens* the hypothesis: a respawned stair is a real
  `BP_Castle_Stairs_*` instantiated from its prefab, so it carries the full
  castle-building component set and *may* be auto-registered by the vanilla
  systems (unlike a hand-spawned world object — see OBJECT_SPAWNING.md, where
  setting `CastleHeartConnection` was proven NOT to register a piece with the
  heart). Whether a stair respawn actually lands in the heart's building registry
  is **untested**.

**Decisive test to ask the friend for:** in the affected castle, does a stair he
**SWAPPED** break after the clan change while an ordinary **never-swapped** vanilla
stair in the same castle survives? Swapped-break + vanilla-survive isolates it to
the respawn/registration path. Also capture: exact repro (who founded the castle,
who swapped what, the precise clan action), the **server log** across the clan
change (`[Uriel STAIRRESPAWN]` lines), and `.uriel stairstyles` aimed at a broken
stair.

**Candidate fix direction (hold until confirmed):** on respawn, re-assert ownership
from the **live castle heart** (not the copied pre-swap values) AND ensure the new
pieces are entered into the heart's building registry — so a later ownership
transfer includes them. If registration proves unreachable (as with world objects),
the fallback is a re-assert hook that re-stamps team/owner from the heart whenever a
swapped stair is detected on a mismatched team.

### ⚠️ Correction (live test 2026-06-07): stairs are NOT MegaStatic

A live `.uriel stairrefresh` dump **disproved the MegaStatic-bake theory above**
(it was inferred from a decompile pass + the old v0.13.x code comments; treat
the "MegaStatic snapshot / bake-at-load" paragraphs in this doc as historical).
Runtime truth:

- A placed staircase is a **fused multi-entity structure**: one
  `BP_Castle_Stairs_*` root (carries the style identity; has **no
  Translation/mesh**, so it does not render itself) + **4 fused
  `TM_Castle_Stairs_Single_*` child segments** that carry the rendered geometry,
  physics, and `StaticTransformCompatible`. **The children render.**
- **Every piece is `NetworkId.Type=Normal`, not MegaStatic** (`megaStaticTiles=0`).
  So Normal entities are live-replicated and the MegaStatic/`MegaStaticDestroyedBuffer`
  surgery path is a **dead end** for stairs. (With `Type=Normal`, the
  `NetworkId.MegaStatic_*` fields are meaningless union bytes — ignore them.)
- The children are **style-agnostic** (identical `TM_` GUIDs across styles) and
  are unchanged by the swap; only the root's `PrefabGUID`/`BlueprintData` rewrite.
- So the restart requirement narrows to: **the client derives the staircase
  cosmetic from the root identity when it FIRST receives the structure, and does
  not re-derive it when the root's `PrefabGUID` changes in place.**

### Experimental live-refresh probe (v0.14.0-exp, admin only)

`.uriel stairrefresh` (adminOnly), gated by `[StairSwap] ExperimentalLiveRefresh`:

- **Flag off (default):** dumps the full runtime component set of the root and
  every fused child (`EntityManager.Debug.GetEntityInfo`) to the server log, plus
  each piece's NetworkId type. Zero mutation. Purpose: find whatever component
  carries the cosmetic so we can rewrite it if needed.
- **Flag on:** additionally **re-streams** the whole structure (root + children)
  through the game's Disabled→enabled streaming path (`ForceResync`, the same
  mechanism public-storage uses) so clients drop and re-receive every piece. If
  the client re-derives the cosmetic from the (already-rewritten) root on
  re-receive, the new style appears live.

Never touches the durable swap; restart-safe. If re-stream doesn't refresh the
look, the next step is to target the style-bearing component identified in the
component dump, or fall back to BCH client-side re-render (handoff §4.7).

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

## Command UX

```
.uriel stairswap <style>     restyle the aimed stair LIVE (destroy+respawn).
                             style ∈ stone1 | stone2 | stone3 | gloomrot | projectk | strongblade
.uriel stairswap next        cycle to the next style in the same archetype
.uriel removestairs          cleanly delete the aimed staircase (whole fused structure)
                             WITHOUT disturbing connected floors/walls. Ownership-gated.
.uriel stairstyles           list styles + which one the aimed stair has
```

All accept a trailing `nearest` token (BCH UI relays). `.uriel stairswap` and
`.uriel removestairs` are player-facing (ownership-gated); `.uriel stairpurge`
(radius ghost-cleanup) remains admin-only.

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
