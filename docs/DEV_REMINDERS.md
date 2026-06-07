# DEV REMINDERS — standing rules for working on Uriel

Hard-won rules (most inherited from Beelzebub development). Add to this file
whenever a new gotcha costs real debugging time — that's the rule for what
belongs here: things that bit once and must not bite twice.

## IL2CPP / ECS

- **No game-type statics at Load.** `Il2CppType.Of<T>()`,
  `ComponentType.ReadOnly<T>()`, prefab lookups — all NRE before `TypeManager`
  is built. Initialize in `Core.TryInitialize`, which only runs once the
  Server world + `PrefabCollectionSystem` are populated.
- **Gate every patch on `Core.IsReady`.** Harmony patches fire during boot.
- **Never throw across a Harmony boundary.** try/catch + `Core.Log` inside
  every patch body. A leaked exception inside a server system update can
  corrupt the tick or crash Burst jobs.
- **Two writers, one component = crash.** If two systems (or the mod + the
  game) write the same buffer/slot, expect
  `AppendRemovedComponentRecordError`-style Burst crashes. Before writing a
  shared component, find out who else writes it (Beelzebub's Mountup bug).
- **Entity lifetime is not your call.** Cache `Entity` handles only with an
  `Exists()` check on every later use; entities despawn between frames.

## V Rising specifics

- **Server-only.** `Application.productName == "VRisingServer"` guard stays.
  Nothing in this mod may require a client-side counterpart to function.
- **Prefab research before code.** Before referencing any prefab/component,
  confirm its layout in the prefab dump
  (`..\Beelzebub Lord of Gluttony\Reference Data\Prefabs\`). Castle tiles are
  `TM_*`, abilities `AB_*`, buffs `Buff_*`, characters `CHAR_*`.
- **Pick patch targets by reading prior art.** Bloodcraft and KindredCommands
  have probably already hooked the system you need; copy their target choice,
  not just their idea.
- **Mod state persists itself.** The game save won't store mod data. JSON
  under `BepInEx/config/Uriel/`, debounced save-on-change + save-on-unload,
  and a migration path whenever the schema changes.

## Process

- **Feature flag everything.** Every feature ships with a `Settings` master
  switch, default chosen for least surprise. Admins opt in/out per feature.
- **One feature, one design doc.** `docs/features/<FEATURE>.md` is the living
  spec; code changes that alter behavior update the doc in the same commit.
- **Conventional Commits**, release commits `chore(release): vX.Y.Z`.
- **Six release surfaces move together** (see CLAUDE.md): csproj version,
  toml version, root CHANGELOG (full/GitHub), package CHANGELOG
  (concise/Thunderstore), root README (GitHub), package README (Thunderstore).
  `tools/preflight.ps1` verifies before the release commit.
- **Stop the server before deploying.** The running server file-locks the DLL.
- **Test on the live local server, then record results** in the feature doc
  (what was tested, what passed, what's still unverified). Untested code is
  marked as such in the changelog ("experimental").

## Lessons learned (append below as they happen)

- **2026-06-07 (v0.13.0) — when two prefabs are structurally identical, swap
  the IDENTITY, not the entity.** Four destroy/rebuild routes failed for stair
  restyling (raw spawn → unmanaged; DestroyUtility → ghost grid claims;
  dismantle event → starts a timed ability that never completes outside build
  mode; build event → refused). The owner's insight won: same-archetype stair
  cosmetics are component-identical and the TM_ segments are style-agnostic,
  so rewriting the BP root's `PrefabGUID` (+ `BlueprintData.Guid`) in place IS
  the swap — the entity stays the original vanilla-placed object with all
  registration/grid/attachment state intact. Generalize: prefer identity
  mutation over destroy+recreate whenever the prefab dump proves structural
  equality. (Client visual refresh is the remaining replication question —
  same family as the share-blink saga.)

- **2026-06-07 (v0.12.0) — raw-Instantiated tiles are never real build objects.**
  Two rounds of component-copying (transform/tile/ownership, then the
  StaticTransform-index fix) still produced "permanent" stairs the build UI
  couldn't highlight/dismantle. The placement pipeline
  (`PlaceTileModelSystem.TryDoStuff` → `ApplyPlacementResult`) wires territory
  connections, the attach-to-floor graph, registration, and placement history
  — un-replicable by hand. **To place a managed tile, fire the game's own
  `BuildTileModelEvent`** (entity with `FromCharacter` + `NetworkEventType
  { EventId_BuildTileModelEvent }` + `ReceiveNetworkEventTag` + the event;
  KindredCommands' KickEvent shape). Net-zero economics: refund the
  blueprint's `BlueprintRequirementBuffer` to the player first, consume
  `LocalInventory`. Corollary: destroy-then-build needs a few frames between
  (cell must read free), and a verify step — worst case the player keeps the
  refund and rebuilds by hand.

- **2026-06-07 (v0.8.0) — a Team swap alone does not make a castle container
  public; the client gates on the CASTLE LINK.** With the common
  `CanLootEnemyContainers=false` server setting, a container whose
  Team/TeamReference were neutralized but whose `CastleHeartConnection` was
  intact stayed completely unclickable for strangers (no interact prompt —
  the client never sends anything, so no server patch can help). The earlier
  "open+take worked" test result was actually the permissive server setting
  doing the work, not the team swap. The complete neutral recipe
  (KindredSchematics): NeutralTeam-singleton Team/TeamReference **AND**
  `CastleHeartConnection = Entity.Null`. Corollary: anything severed for
  sharing must be re-derivable for restore — hence the heart-tile anchor in
  the registry. Always ask which side enforces a rule: if it's the client,
  only replicated STATE changes work, never server patches.

- **2026-06-06 (v0.4.1) — the tile entity is NOT the inventory carrier.**
  Placed containers keep their items on a separate attached external-inventory
  entity (`InventoryInstanceElement.ExternalInventoryEntityPrefabGuid →
  External_Inventory`). `TryAddInventoryItem`/`TryRemoveInventoryItem` against
  the `TM_*` tile entity silently fail ("container full" symptoms). ALWAYS
  resolve through `InventoryUtilities.TryGetInventoryEntity` (or check for an
  `InventoryBuffer` directly) before any inventory mutation — see
  `PublicStorageService.ResolveInventoryEntity`. Player characters resolve
  internally and are exempt; containers are not.

- **2026-06-06 (v0.2.1) — default EntityQueries skip Disabled entities.**
  World chests AND placed castle objects carry `DisableWhenNoPlayersInRange`,
  so they are `Disabled` whenever no player is nearby — which is *always* the
  case during boot-time work and *usually* the case for distant world objects.
  The donor-team lookup returned nothing on a live server because of this.
  Any query that must see placed/world objects needs
  `EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludeSpawnTag`
  via `EntityQueryBuilder` (KindredCommands does this everywhere — now we
  know why). Queries for *players interacting right now* can stay default.
