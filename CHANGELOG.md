# Changelog — Uriel, Lord of Hosts (full)

This is the **complete** changelog (GitHub). The Thunderstore package carries
a condensed, player-facing changelog at `Uriel/Uriel/CHANGELOG.md`; every
release updates **both** (see CLAUDE.md → "Release & changelog discipline").

Format: [Keep a Changelog](https://keepachangelog.com/) flavored;
versions follow the mod's own incremental scheme (pre-1.0: minor = feature
batch, patch = fixes).

## [0.18.0] - 2026-06-09

Object Spawning stability pass — placement overlap guard, an invisible/hazardous-prefab
filter, `here`-placement fix, and a safety rewrite of the plot-purge commands after a
tester report that `forcepurgeplot` deleted native objects. All changes are to Object
Spawning; other features untouched. See `docs/features/OBJECT_SPAWNING.md` for full
mechanism write-ups.

### Added — overlap guard (`ObjectSpawn.PreventOverlap`, default ON)

- New `ObjectSpawnService.WouldOverlap` gate on `.uriel spawn` and `.uriel move`: refuses a
  destination whose tile cell is occupied by a NON-floor tile model (wall, station, native
  prop, castle heart, or another spawned object). **Floors are the explicit exception** (décor
  sits on floors). Guards against the same-spot pile-ups that can destabilize a server.
- **Characters/units never block** (`Has<Movement>()` skip) — the player's own body carries
  `TilePosition` and was wrongly caught (refusing a `here` spawn with "would overlap Vampire
  Male"); NPCs would too. `Movement` is the Bloodcraft-proven creature-vs-build-piece signal.
- **Proximity backstop (`OverlapMinDistance = 0.5f`)** — the integer tile-cell test only trips
  on identical anchor cells, so a free-aim **move** that lands a fraction of a tile away
  (straddling a cell boundary) slipped through; a center-to-center distance check now catches
  it. Walls are exempt so décor can sit flush against them.
- Vertical band (`OverlapHeightBand = 2.5f`, ~one storey) keeps multi-storey castles unaffected.

### Added — `here`/at-player spawns land in FRONT of the player

- `InFrontOf` offsets a `here`/UI-button spawn ~1.5m (`HerePlacementForward`) along the
  character's body facing (feet height preserved), so the object drops on the floor in front of
  the player instead of inside their body. Falls back to raw feet if facing can't be read.

### Fixed — invisible / hazardous prefabs filtered out of the catalog

- `IsPlaceableObject` now also requires `ProjectM.Network.NetworkId`. A runtime-spawned tile
  object only **renders on the client** if it is networked (replicated via `NetworkSnapshot`); a
  non-networked tile model is baked world-static geometry that exists only server-side and spawns
  **invisibly**. Audit: only 36 of 3778 `TM_` prefabs lack `NetworkId` — every visible category
  (furniture, lights, containers, resource nodes, stairs) has it — so the filter is surgical.
  This is the principled fix for the whole invisible-spawn class (e.g. `TM_*_Original` world-gen
  source clusters), reported live with `TM_Strongblade_RockCluster_06_Original`.
- Belt-and-suspenders name/component filters in `IsNonObject`: `MicroPOI*` (all 85 are POI /
  territory spawner controllers carrying a **unit spawner** — a server hazard distinct from
  invisibility), `*InvisibleObject*` AI/position markers, and a `MicroPOIInstance` backstop.
- **Roof tiles blocked (context-only pieces).** `NetworkId` can't catch these — they ARE
  networked, but the roof system only renders them at a height above walls, so spawned at
  ground level they're invisible (reported with `TM_RusticHouse_Roofing_Type6`). `IsNonObject`
  now also excludes `prefab.Has<CastleRoofOrnaments>()` — exactly the 30 `TM_CastleRoof_Type*`
  + `TM_RusticHouse_Roofing_Type*` prefabs (NOT the broader `ProjectM.Roofs` namespace, which
  also tags castle floors).
- The filter flows through the catalog, the spawn-time gate, and stale-unlock pruning, so
  already-unlocked bad prefabs are scrubbed from player lists too.

### Fixed — `forcepurgeplot` no longer deletes native objects (data-loss bug)

- A tester ran `.uriel forcepurgeplot` on a plot with 3 Uriel objects and it destroyed ~2000
  ("every flower, tree, garden tile"). Root cause: the "ours" test matched
  `CastleHeartConnection.CastleHeartEntity == heart`, but adoption (`ExecuteSpawn`) makes a Uriel
  object castle-owned by **copying that vanilla component** — and native garden plants,
  castle-claimed trees/flowers, and world props inside the territory carry it identically. Uriel
  stamps no unique marker, so an adopted object is structurally indistinguishable from a natively
  claimed one. The heart-connection (and bare-`Immortal`) heuristics are removed.

### Added — two-tier plot purge + in-session live marker

- New `_liveSpawns` (`HashSet<Entity>`) marks every Uriel spawn this run: added in
  `RegisterRecord`, refreshed from the persistent registry in `ReapplySpawned` on boot, removed
  in `DestroySpawned`. A direct "this entity is ours" check, no tile-resolution, zero chance of
  matching a native object. Deliberately **not** a custom ECS component — V Rising's save drops
  mod-added components on restart — so the JSON registry stays the cross-restart source of truth
  and the marker is the fast in-session layer. (`Entity` equality includes Version, so a recycled
  slot can't false-match a stale handle.)
- Shared `PurgePlotCore` applies Uriel-only signals safest-first: (1) live marker, (2) registry
  records, (3) `SpawnChainChild` legacy chain sweep. **`.uriel purgeplot` = LIGHT** (1–2);
  **`.uriel forcepurgeplot` = STRONG** (1–3). Run light first, escalate to strong for legacy
  leftovers. Replies report the per-tier breakdown. A truly-untracked non-chain object (record
  lost AND not in this session's marker) is removed per-object via `.uriel forcedespawn`.

### Added — `.uriel purgeorphans` (server-wide orphan cleanup, admin)

- On-demand whole-map sweep that removes any tracked object no longer governed by a **living castle
  heart** (its castle was destroyed/decayed, or it's otherwise in open world). The manual backup for
  the automatic boot-time orphan purge (`ObjectSpawn.PurgeOrphansOnBoot`).
- `ObjectSpawnService.PurgeOrphans` builds the set of all plot blocks owned by a living heart (every
  `CastleHeart`, disabled-included so streamed-out-but-alive castles still count; a destroyed heart is
  absent), then flags each resolvable registry object whose block isn't in that set. Registry-driven, so
  it only ever touches Uriel's own objects — native world objects are never affected. Streamed-out
  objects (unresolvable) are left alone with their records kept. **Safety abort** if zero living plot
  blocks resolve (a query glitch), so it can never treat every object as an orphan.

## [0.17.0] - 2026-06-08

### Added — Object Spawning: breakable modes + auto-respawn

- **`smashable` spawn flag** — a breakable object the **owner can destroy by hand**,
  not just raiders/decay. Implemented by skipping castle adoption in `ExecuteSpawn`
  (a new `playerBreakable` path): the object never receives the owner's `Team` /
  `CastleHeartConnection`, so the engine stops treating it as protected own-castle
  property. Ownership and management are unaffected — `.uriel despawn`/`move` resolve
  the owner from the object's **position** (the territory's heart), not its
  components, and the spawn record still stores the heart tile. Trade-off (documented
  in-reply): the object is un-owned, so it decays and anyone in the territory can
  damage/interact with it.
- **`respawn` spawn flag** — the object **auto-respawns after it's destroyed**, until
  the castle heart is gone or the player `.uriel despawn`s it. A config-gated poll
  (`ObjectSpawn.RespawnEnabled`, default on; `ObjectSpawn.RespawnPollSeconds`, default
  30, min 5) re-spawns each flagged object whose entity is gone **and** whose castle
  heart still resolves. The "destroyed vs streamed-out" guard is the safety crux: the
  live-entity index is Disabled-included (a streamed-out object still resolves), and
  respawn is gated on the heart existing — so a streamed-out region never duplicates
  an object and a destroyed castle never resurrects one. The same record is re-used
  (re-pointed at the fresh entity), so it never duplicates.
- Spawn flags now parse order-independently after the rotation slot:
  `breakable` · `smashable` · `respawn` · `here`/`nearest`/`me` · `indestructible`,
  e.g. `.uriel spawn <prefab> 0 smashable respawn here`. `spawned_objects.json` schema
  → **v3** (`Rot`, `RespawnOnDestroy`, `PlayerBreakable`; older records load as
  0/false). Mode survives `.uriel move`/`rotate`.

### Added — `here`/`nearest` placement token (spawn & move)

- **`.uriel spawn … here`** and **`.uriel move here`** place at the **player's
  location** instead of the aim/cursor point (aliases `nearest`/`me`). This fixes
  spawning/placing from a **BloodCraftHub UI button**, where the cursor sits on the
  panel and the aim ray lands outside the plot — which previously failed with "can
  only be spawned in a castle plot." Aim-based placement is unchanged when no token is
  given.

### Fixed — breakable/indestructible now actually take effect (Health path)

- Toggling `breakable`/`indestructible` was a **no-op on most world objects**. Audit
  of the prefab data showed world chests AND furniture carry **no native `Immortal`**
  — their destructibility is the **Health path** (`Health` + `HealthConstants.
  DestroyOnDeath` + a `DestroyAfterDuration` auto-despawn timer), which the old
  `Immortal`-only toggle never touched. `ExecuteSpawn` now drives both mechanisms:
  indestructible = `Immortal` + decay-off + `DestroyOnDeath=false` + strip the
  auto-despawn timers; breakable = clear `Immortal` + `DestroyOnDeath=true` + strip
  the timers. This also fixes a latent bug where an "indestructible" world chest would
  silently vanish on its ~1200s `DestroyAfterDuration` clock.
- ⚠ Note: while an object is castle-adopted (raid/decay-only `breakable`), V Rising
  blocks the **owner** from weapon-smashing it (vanilla castle protection); that's why
  the `smashable` mode (which skips adoption) exists.

### Fixed — characters & V Bloods excluded from spawnable objects

- `CHAR_*` units / V Bloods could appear in a player's unlocked list and be selected
  to spawn (they carry `TilePosition`, so they passed the positive placement filter,
  and stale entries persisted from an older, looser catalog). Now: the unit/V-Blood
  exclusion is **component-based** (`Movement` **or** `VBloodConsumeSource`, alongside
  the existing `CHAR_`/`AB_`/… name families), `.uriel spawn` **refuses any GUID that
  isn't a real placeable object** even if unlocked (and prunes that stale unlock), and
  the unlocked list **self-heals** — `DescribeUnlocks` / `api unlocked` drop non-object
  GUIDs from `player_unlocks.json` on first view. The discovery-on-death path was
  already safe (it guards on `IsDiscoverableGuid`).

### Fixed — BloodCraftHub object catalog/unlocked lists now populate

- `.uriel api catalog` / `.uriel api unlocked` returned nothing while `.uriel api
  version` worked. The paged commands built a whole page (header + rows +
  `[URIEL:end]`) as one `\n`-joined `ctx.Reply`, but a System-chat message is **one**
  wire line and BCH (like Beelzebub) doesn't split on `\n` — so only the header parsed
  and every row was dropped. Each `[URIEL:*]` line is now emitted as its **own**
  `ctx.Reply`, matching the proven Beelzebub pattern. No wire-shape change (ApiVersion
  stays 1, no BCH change needed); page size raised 3 → 20 now that the per-page byte
  cap no longer applies per line.

### Docs

- Recorded the stair clan-ownership investigation (swapped stairs are confirmed
  castle-owned, not caster-owned) and a shelved feasibility study for building below
  the first floor (`docs/features/BASEMENT_BUILDING.md` — not possible server-side;
  terrain + camera are client-only). Updated the BCH integration handoff (spawn flag
  grammar, the `here`/`nearest` placement rule, and the catalog-wire fix).

## [0.16.0] - 2026-06-08

### Fixed — Object Spawning: cross-session management & chain-object removal

Owner live-testing of 0.15.0 surfaced two persistence defects and one removal
defect in the Object Spawning feature; all are fixed.

- **Spawned objects became unmanageable after a relog or server restart.**
  `.uriel despawn`/`move`/`rotate`/`spawninfo` reported "no Uriel object within
  Nm" while standing on the object. Two root causes:
  - *Stale entity cache.* Targeting walked an in-memory `List<Entity>` rebuilt
    only at boot. The engine recreates a castle object's entity (new handle)
    every time the castle streams out and back in — a player relogging, leaving
    and returning to the territory, or a server restart — so the cached handles
    went invalid and nothing re-resolved them. The cache is removed; management
    now re-resolves each object from the persisted registry **on demand** (by
    prefab GUID + tile, with a **world-position fallback** for save/load tile
    drift), mirroring the proven `PublicStorageService` resolution model. Records
    now store world position (`spawned_objects.json` schema v2; older records are
    backfilled on first resolve).
  - *Destructive boot re-apply.* `ReapplySpawned` deleted any record it couldn't
    match in one early boot sweep and wrote the emptied file — so an object that
    was merely streamed-out (or not yet loaded) was **forgotten permanently**.
    Boot re-apply is now non-destructive: unresolved records are kept and logged;
    only a *confirmed* orphan (object present but its castle heart gone) is purged.
- **Removing a chain-spawned object made it "flash and reappear."** Objects
  spawned in the early chain-controller era are the *child* of a `Chain_*`
  controller whose `SpawnChainInstance.LoopOnEndOfChain` re-spawns the child the
  instant it dies — and the controller (at world-origin, no tile/heart/Immortal)
  was untargetable. All removals now route through one helper that, when the
  object carries `SpawnChainChild`, destroys the **looping controller first**
  (stopping the respawn) and then the child.

### Added — admin recovery commands for untracked objects

- **`.uriel forcedespawn [confirm]`** (admin) — force-remove the object you're
  aiming at / nearest you, **ignoring Uriel records and ownership** (recovers
  objects no registry tracks, e.g. chain-era spawns). The first call names the
  exact prefab and arms a 30-second confirm; `.uriel forcedespawn confirm`
  deletes it. Never targets the castle heart; also tears down any looping chain
  controller.
- **`.uriel forcepurgeplot`** (admin) — force-remove **every** Uriel-like object
  on the plot you're standing in, including untracked chain-era children. Scoped
  to the plot by its territory blocks; matches objects that are indestructible
  (`Immortal`), a spawn-chain child, or connected to this castle's heart. Native
  build-menu pieces (`BlueprintData`) and the heart are always left untouched.

## [0.15.0] - 2026-06-08

### Added — Object Spawning: collect & place world objects into your castle

A new umbrella feature (`docs/features/OBJECT_SPAWNING.md`). Players collect
**world objects** the build menu never offers — resource nodes, world chests,
breakable props, GloomRot/Cursed/dungeon decor — and place them inside their
castle. Built across four phases:

- **Spawn engine (`ObjectSpawnService`).** Generalizes the proven stair recipe:
  resolve `PrefabGUID` (name or int) → `Instantiate` → strip `Disabled` →
  write `Translation`/`Rotation` + tile-grid (`TilePosition`/`TileBounds`/
  `StaticTransformCompatible`) → adopt into the surrounding castle (copy
  `CastleHeartConnection`/`Team`/`TeamReference`/`UserOwner` off the resolved
  heart) → `Immortal` + `CastleDecayAndRegen.CanDieFromDecay=false` by default
  (per-spawn `breakable` override; PvP servers can default it off).
- **Spawn-chain controllers refused.** `Chain_*` prefabs are `SpawnChainData`
  controllers, not objects — instantiating one spawns an uncontrollable child, so
  our edits hit the inert controller. Now detected and refused with a pointer to
  the real object.
- **Runtime object catalog (classifier).** The spawnable set is filtered by
  component signature to REAL placeable world objects, excluding: characters/NPCs
  (`CHAR_*` + a `Movement` backstop — units carry `TilePosition` like decor),
  ability-effect objects (`AB_*`), debug/`GM_`/`Liquid_`/internal families,
  spawn-chain controllers, and — unless `IncludeCastleBuildables=true` —
  the inherent build-menu pieces (identified by `BlueprintData`, ~1200 of them).
  `.uriel findprefab` ranks exact>prefix>substring; `.uriel catalog` browses all.
- **Placement gate (territory-based).** A spawn/move must land INSIDE a castle
  plot (`CastleTerritory` → owning `CastleHeart`, KindredCommands pattern):
  non-admins only in a plot their team OWNS, admins any plot, open-world refused.
- **Persistence + lifecycle.** `spawned_objects.json` (keyed by prefab GUID + tile
  coords) → despawn/move/rotate work across sessions; Immortal/decay re-apply on
  boot; orphan objects whose castle is gone are purged on boot
  (`PurgeOrphansOnBoot`, default on). `.uriel spawnlist` / `.uriel purgeplot`
  manage a whole plot. Move/rotate are respawn-based (these objects render from
  baked static batches; an in-place edit risks the stair invisible-until-restart bug).
- **Player access model (config).** `PlayerAccessMode` = `Full` (whole catalog) or
  `Discovery` (only objects you've unlocked). `CollectionEnabled` master switch.
  Build cost (`PrefabCostItem`/`PrefabCostStack`, charged from the player's
  inventory; `RefundOnRemove`). Players act only when `AdminOnly=false`.
- **Discovery-by-destruction.** A Harmony postfix on `DeathEventListenerSystem`:
  when a player destroys an eligible world object (`Health` + `DestroyOnDeath`,
  not a build piece), roll `DiscoveryChancePercent` to unlock it. Per-player
  `player_unlocks.json`; per-player `.uriel notify on|off`; already-owned
  destructions never re-notify.
- **Non-destructible unlock paths** (`NonDestructibleUnlock` = `Off`/`Collection`/
  `FinalBoss`/`AllBosses`): roughly half of placeable objects can't be destroyed,
  so they unlock on collecting 100% of the discoverable set, on defeating Dracula
  (`CHAR_Vampire_Dracula_VBlood`, game completion), or on defeating every main
  V-blood. Plus a curated `boss_unlocks.json` (`BossUnlocksEnabled` +
  `.uriel bossmap`) mapping a V-blood → the objects its defeat grants.
- **Admin tools.** `.uriel grant`/`revoke`/`grantall <player> [mode]`,
  `.uriel block`/`unblock`/`blocklist` (forbid prefabs — excluded from the catalog
  and the collection percentage), `.uriel bossmap add|remove|list`.
- **BloodCraftHub wire API** (`[CommandGroup("uriel api")]`, ApiVersion 1):
  `.uriel api version` / `catalog` / `unlocked` emit `[URIEL:*]` lines (the total
  in-game object list + a player's collection + `pct`), with `label=`/`cat=`
  display metadata for a BCH palette. The dedicated server has no images to ship;
  BCH resolves icons/previews client-side by GUID. Contract in
  `Uriel/Uriel/docs/BCH_INTEGRATION_HANDOFF.md` §6.

### Added — nested in-game help

- **`.uriel help [objects|storage|stairs|admin]`** — a clean, paged help tree so
  players without the BloodCraftHub UI can discover features without a wall of
  text. The bare `.uriel` overview now names all three feature groups.

### Known limitations (Object Spawning)

- World objects are NOT selectable in the vanilla build menu (vanilla selection
  needs castle-heart placement registration that component grafting can't
  reproduce — proven live). They're managed by `.uriel move`/`rotate`/`despawn`.
- V-blood (feed) kill detection for boss-based unlocks rides the same death hook;
  if a feed-kill doesn't register as a player kill, boss/final-boss/all-boss
  triggers won't fire (pending live validation) — the 100%-collection trigger has
  no such dependency.

## [0.14.0] - 2026-06-07

### Changed — stair swap now applies LIVE (destroy + respawn)
- Stair restyling is no longer an in-place identity rewrite (which only showed
  the new look after a server restart). `.uriel stairswap` now **destroys the
  fused staircase and respawns it in the target style as a fresh entity**, so
  the new style renders immediately for everyone — no restart, no relog.
  Validated live on straight, curved, and wide staircases.
- **Why the change (the v0.13.x theory was wrong):** deep live investigation
  (decompile + runtime `.uriel stairrefresh` dumps) proved placed stairs are
  ordinary `NetworkId.Type=Normal` entities, NOT MegaStatic. The real
  constraint: the client binds a placed stair's rendered look to its `NetworkId`
  at first receipt and never re-derives it from any server-side data change —
  relog, full area unload/reload, the Disabled re-stream blink, and re-baking
  `TileData`/`NetworkedPrefabChildren` blobs were **all tested and did nothing**.
  Only a genuinely NEW entity (new NetworkId) refreshes it, which is exactly what
  a restart does — now reproduced for a single staircase.
- **Mechanism:** capture the whole structure (root + fused children: transforms,
  `TilePosition`/`TileBounds`, `StaticTransformCompatible`, `Team`/`TeamReference`/
  `UserOwner`/`CastleHeartConnection`, and each child's
  `CastleBuildingAttachToParentsBuffer`) → verify every prefab resolves BEFORE
  destroying anything (safety: a missing prefab can never delete a stair) →
  `DestroyUtility.Destroy` each piece (clean tile-grid deregister; NO parent-walk,
  so connected floors/walls survive) → after a configurable gap, re-`Instantiate`
  root (target style) + children (own prefabs) and re-wire fused/heart/team/
  attachments. `Instantiate(BP_root)` alone is insufficient — fused children
  spawn via `SubScenePrefabSpawnerSystem` on the load path, so they're spawned +
  wired by hand.
- `.uriel stairswap` is now **player-facing** (was effectively admin-only during
  the experiment). Same-shape restriction and per-user DLC gating unchanged.
- New config `[StairSwap] RespawnGapFrames` (default 5) — frames between destroy
  and respawn so clients register the removal first.

### Added — `.uriel removestairs`
- Cleanly delete the aimed staircase (the whole fused structure) **without**
  disturbing the floors/walls/rooms it connects to — the clean targeted removal
  vanilla dismantle can't do. Player-facing, ownership-gated, accepts `nearest`.
  No material refund in this version.

### Notes
- **License:** Uriel is released under the **GNU AGPL-3.0**. It adapts
  server-side modding techniques from odjit's AGPL-licensed KindredCommands and
  KindredSchematics; in keeping with their copyleft, Uriel carries the same
  license (relicensed from MIT before first publish).
- A swapped/rebuilt stair is now a **NEW entity** (new ids for root + every
  child) — BCH must not cache stair entity ids across a swap (handoff §4.7 +
  feature-state row updated).
- The legacy in-place identity swap (`Swap`/`ExecuteSwap`) and the
  `stairrefresh` diagnostic remain in the source, unwired, for reference.
- Known simplification: the attachment/decay/pathing graph is restored
  minimally; broader testing pending (local tests clean so far).

## [0.13.2] - 2026-06-07

### Changed — honest visuals messaging; restart-refresh question answered
- **Live validation: the identity swap WORKS** — a server restart renders the
  swapped style (confirmed). Research settled the "can we refresh without a
  restart" question: placed tiles are baked into per-chunk **MegaStatic
  snapshots generated once at server load**; clients download the bake at
  connect, and the only live replication channel is the destroyed-instance
  list. There is **no modified-instance channel and no rebake API** — relog
  cannot help (confirmed live). The swap reply now says plainly: *"the new
  look appears for everyone at the NEXT SERVER RESTART (the change is
  already saved)"* — no more misleading relog suggestion.
- The realistic live-visuals path is client-side: documented in the BCH
  handoff (§4.7) — BCH can detect the identity change and re-render the
  stair locally. Deep server-side manager-buffer surgery is documented and
  parked.

## [0.13.1] - 2026-06-07

### Fixed — the visual identity lives in the NETWORK id
- v0.13.0 identity swaps didn't change the stair's appearance even after a
  relog (live test). Root cause found by reflection: placed castle tiles
  replicate as **MegaStatic** network objects — the client derives the visual
  from `NetworkId.MegaStatic_PrefabGUID`, a prefab reference embedded in the
  network identity itself, not from the `PrefabGUID` component. The swap now
  rewrites that field as well (when the tile is MegaStatic-networked).
- Visual refresh guidance: relog if the look doesn't update live; everything
  fully settles at the next server restart (network ids regenerate from the
  swapped prefab identity at load).

## [0.13.0] - 2026-06-07

### Changed — stair swap is now an IDENTITY swap (the mod owner's insight)
- All destroy/rebuild routes failed live (raw spawn → unmanaged "permanent"
  stairs; DestroyUtility → invisible ghosts holding the grid; the vanilla
  dismantle event only starts a timed ability that never completes outside
  build mode; the vanilla build event was refused). New approach, proposed by
  the mod owner: since same-archetype cosmetics are structurally identical
  and ALL stair segments are style-agnostic (verified: the BP root's prefab
  id alone carries the cosmetic), the swap now simply **rewrites the placed
  stair's prefab identity in place** (`PrefabGUID` + `BlueprintData.Guid`).
- **Nothing is destroyed or placed**: the stair remains the original
  vanilla-built object — registration, tile-grid claims, floor attachments,
  and dismantle behavior all untouched; neighbors reference it by entity id
  and are unaffected. Free, instant, atomic; no materials move at all.
- Client visual refresh rides the resync blink; if a swapped stair still
  shows the old look, step away and back (or relog) — the change is already
  applied server-side (the reply says so too). `.uriel stairpurge` stays for
  cleaning up ghosts left by the earlier mechanisms (purge each half of a
  tall staircase, restart afterwards).

## [0.12.2] - 2026-06-07

### Fixed — forced-dismantle fallback completes the swap
- Live test of v0.12.1: the vanilla dismantle event only STARTS the game's
  timed dismantle ability (visible as `AB_Interact_Dismantle_Short` casts in
  the log), which never completes outside real build-mode — so swaps aborted.
  Meanwhile `.uriel stairpurge` proved that destroying the root AND its
  segments genuinely frees the cell (the user hand-built immediately after,
  no restart). The swap now tries the vanilla dismantle briefly, then
  **force-dismantles**: manual full-cost refund to the player + purge of the
  stair's root and segments at the spot, then fires the build event
  (consuming the refund from local inventory). Net zero either way.
- **Two-half staircases** (live finding: a tall staircase is two stacked
  placements): verification now looks for the TARGET style near the spot
  instead of "the closest stair", which could be the untouched other half.
  Swap each half separately — `.uriel stairswap` converts the half you aim at.

## [0.12.1] - 2026-06-07

### Fixed — removal goes through the vanilla pipeline too
- **v0.12.0 swaps left invisible "ghost" stairs** (walkable, unbuildable-over,
  nothing visible) and the build event was refused (live test). Root cause:
  `DestroyUtility` on a placed stair removes the entity but leaves the TILE
  GRID CLAIMED and the fused segments' collision alive — so the cell never
  read as free for the new placement. This also retroactively explains the
  ghost residue under every earlier swap.
- The swap now fires the game's own **`DismantleTileModelEvent`** (proper
  grid release + fused-children teardown + vanilla material refund), polls
  until the old root is really gone, THEN fires the build event
  (`SharedInventory` consume — the refund covers it). Economics = exactly
  manual demolish+rebuild. If dismantle is refused (broken stairs from old
  builds), the swap aborts with nothing lost.
- **New admin command `.uriel stairpurge`** — destroys all stair entities
  (roots + segments) within 5m, for cleaning up the invisible ghosts already
  created; restart the server afterwards to flush remaining grid claims,
  then rebuild manually.

## [0.12.0] - 2026-06-07

### Fixed — stair swap now uses the game's own build pipeline
- **Swapped stairs were still becoming permanent/unmanageable** (live retest:
  the v0.9.0 StaticTransform fix wasn't the root cause). Verdict:
  raw-instantiated tiles can never be real build objects — the placement
  pipeline wires territory connections, attach-to-floor graphs, registration,
  and placement history that component-copying can't replicate.
- The swap now: (1) **refunds the stair's full material cost** to the caller
  (same-archetype styles cost identically, so refund + vanilla build charge
  nets zero), (2) destroys the old root, (3) fires the game's own
  `BuildTileModelEvent` at the same spot/rotation — the new stair is placed
  **exactly as if hand-built** (registered, highlightable, dismantlable,
  client-synced), then (4) verifies and reports in chat.
- **Failure is never a loss:** if the game refuses the placement, the player
  keeps the refunded materials and can place the stair by hand.
- **Repairing stuck stairs from older builds:** `.uriel stairswap` them to any
  style with this version — the vanilla rebuild replaces the broken entity.
- Note: the vanilla pipeline also re-validates blueprint/DLC unlock and
  ownership server-side (defense in depth on top of Uriel's own checks).

## [0.11.0] - 2026-06-07

### Added — `nearest` targeting mode (for BCH UI integration)
- Every targeted command now supports targeting the container/stair **closest
  to the player** instead of the aim point — built for BloodCraftHub UI
  buttons, where clicking a panel leaves the aim ray pointing anywhere
  (possibly at a different object behind the UI):
  - `.uriel share nearest …` — stackable token, any position in the modifier list
  - `.uriel unshare|info|paychest|takeprisoner|stairstyles nearest`
  - `.uriel stairswap <style> nearest`
- Aim mode (default, hand-typed commands) additionally gains an automatic
  nearest-to-player fallback when nothing qualifies at the aim point.
- BCH handoff §3 updated with the relay guidance (UI buttons always send
  `nearest`; surface the reply's object name as confirmation).

## [0.10.0] - 2026-06-07

### Added — `.uriel takeprisoner` (experimental)
- Aim at a **shared** prison cell (owners/admins: any cell) and take its
  prisoner: they're subdued and released to you — have Dominating Presence
  ready to escort them home. Two-layer mechanism: a synthesized vanilla
  `InteractWithPrisonerEvent(Charm)` first (100% vanilla behavior if the
  system accepts it from the neutral cell), with an automatic manual-charm
  fallback (`AB_Charm_Active_Human_Buff` owned by the taker; imprisoned
  state + cell links cleared via the PrisonerExchange component recipe).
  Gated by `PublicStorage.PrisonEnabled`.

### Research verdict — native subdue button for non-owners: impossible server-side
- Deep-dive confirmed the prison UI's SUBDUE/KILL buttons are hard-gated in
  CLIENT code by team match: feed/extract are inventory-recipe surfaces
  (visible with shared access), while subdue is an interact-event the client
  only offers to the cell's castle team. PalacePrivileges — the most thorough
  permissions mod — has a `prison.subdue` privilege that works only for
  same-clan players, and treats a cross-team charm event as CHEAT detection.
  No replicated state can reveal the button; hence the command approach.

## [0.9.0] - 2026-06-07

### Fixed — storage shares now take effect live; stairs stay editable
- **Sharing a chest no longer requires the other player to restart their
  game** (live test: unshare propagated, share didn't — "locked" being the
  stale-state default made unshare only LOOK instant). Clients re-evaluate a
  container only when they receive a NEW entity, so share/unshare of storage
  now REBUILDS the container: fresh entity with the target state, transform/
  tile data copied, every inventory slot transferred with its item entity
  re-pointed (durability preserved), old container destroyed. Rollback keeps
  the old container intact if the rebuild can't complete. Prison cells keep
  the mutate+blink path (rebuilding a cell with a bound prisoner is unsafe).
- **Swapped stairs no longer become permanent/uneditable.** Root cause: the
  swap copied the old entity's `StaticTransformCompatible.StaticTransform`
  INDEX — baked transform data that dies with the old entity. The swap (and
  the container rebuild) now use the dynamic-transform path
  (`UseStaticTransform=false` + NonStaticTransform fields), exactly how
  KindredSchematics places everything. **Fix for already-stuck stairs:**
  swap them to another style with this build — the rebuild replaces the
  broken entity with a correct one.

### Known issue (parked, by design decision pending)
- Prison cells: strangers can use the cell's inventory/recipes but get no
  SUBDUE/charm option even with fresh state — the prisoner-management UI
  appears to require more than team identity (likely the castle link, which
  sharing severs). Next iteration will likely add a server-side
  `.uriel takeprisoner` command instead of chasing the client UI.

## [0.8.2] - 2026-06-07

### Fixed — the client-refresh mechanism, properly this time
- The v0.8.1 `UpToDateUserBitMask` clear was insufficient: a live `sharedebug`
  dump proved the server state perfect (neutral singleton team, severed link)
  while the stranger's client kept casting the DisabledDummy interact —
  **clients only re-evaluate a container's interactability when the entity is
  (re)streamed to them**, which is why boot-applied shares always worked and
  runtime changes appeared dead.
- Share/unshare now **blink** the entity through the game's own streaming
  path: `Disabled` for ~3 frames, then re-enabled — every client drops the
  entity and re-receives it with fresh state (prisoner included for cells).
  Powered by a new per-frame tick driver (Beelzebub Heartbeat pattern:
  IL2CPP-injected MonoBehaviour).
- `.uriel share` now logs to the server log like unshare does (timeline
  reconstruction during testing).

## [0.8.1] - 2026-06-07

### Fixed — runtime share/unshare changes now reach connected clients
- **Boot-applied shares worked; runtime share/unshare appeared stale** to
  already-connected players (live test: a chest shared before restart was
  accessible at login, but unshare→re-share made it inaccessible). The
  server only re-sends entity state it considers changed for networking —
  share/unshare/re-apply now clear the entity's `UpToDateUserBitMask`,
  forcing a fresh sync to every connected client.
- **Strangers couldn't subdue/charm the prisoner out of a shared cell**
  (limited access): that interaction validates against the PRISONER's own
  team, not just the cell's. Sharing a cell now neutralizes the imprisoned
  unit's team as well; unsharing restores it with the cell's.
- New admin diagnostic: **`.uriel sharedebug`** — aim at any container to
  dump its live state (team values, neutral-singleton check, castle-link
  status, heart-anchor resolution, prisoner team) to chat + server log.

## [0.8.0] - 2026-06-07

### Fixed — sharing now works regardless of server loot settings
- **Shared containers/cells were unclickable for strangers when the server's
  "can loot enemy containers" setting was OFF** (the typical PvP/PvE
  configuration) — live two-player test. Root cause: the client gates the
  interact prompt on the container being an enemy CASTLE container; a team
  change alone doesn't clear that. (Retrospective: the earlier "open+take
  worked" result was the permissive server setting, not our team swap.)
- Sharing now applies the complete neutral recipe (KindredSchematics'
  public-build pattern): `Team`/`TeamReference` from the game's
  **NeutralTeam singleton** AND **`CastleHeartConnection` severed** while
  shared. This is what makes KindredSchematics-built public chests work on
  any server, independent of loot settings.
- Because the castle link is severed while shared, each entry now records a
  **castle-heart anchor** (heart tile) for ownership checks and restore:
  unshare reconnects the heart and restores the sibling/heart team. Existing
  entries auto-capture their anchor at the next server start.
- Ownership checks (`unshare`, policy edits, controller bypass in the
  movement patches, payment routing) all resolve the heart via the anchor
  while shared.

### Known considerations (to validate)
- A shared (heart-severed) container is invisible to castle decay while
  shared; unshare reconnects it. Prison-cell sharing may interact with the
  per-castle prison-cell limit while severed.

## [0.7.0] - 2026-06-07

### Added — stair hot-swap (experimental, first implementation)
- **`.uriel stairswap <style>`** — aim at a placed staircase and swap it to
  another cosmetic of the SAME shape, in place, free (same-archetype
  cosmetics cost identically in vanilla). Styles: `stone1|stone2|stone3|
  gloomrot|projectk|strongblade`, or `next` to cycle to the next style you
  own. Position, rotation, tile data, and castle ownership are preserved
  exactly.
- **`.uriel stairstyles`** — show the aimed stair's shape, current style,
  and which styles are available to you (DLC-locked styles are marked).
- **Archetype matching enforced**: narrow straight (`Single`), narrow
  right-curve (`Single_CW`), narrow left-curve (`Single_CCW`), and wide
  (`Double`) stairs only swap within their own shape — all 24 blueprint
  GUIDs mapped from the prefab dump.
- **DLC entitlement (owner decision)**: a player may only swap TO a style
  present in their own build menu — DLC cosmetics carry
  `ProgressionUserContentDependency`, checked against the player's
  `User.UserContent` flags via `UserContentUtility.HasUnlocked`. No
  server-wide bypass.
- Mechanism: KindredSchematics-pattern manual surgery — instantiate the
  target blueprint, copy `Translation`/`Rotation`/`TilePosition`/
  `TileBounds`/`StaticTransformCompatible`, wire `Team`/`TeamReference`/
  `UserOwner`/`CastleHeartConnection` from the castle heart, then
  `DestroyUtility` the old root only (never attach-parents). The game
  self-registers the new tile.
- New config: `StairSwap.MaxTargetDistance` (default 6m);
  `StairSwap.Enabled` now actually gates the feature.

## [0.6.0] - 2026-06-07

### Added — bulk visibility & control
- **`.uriel shared`** now shows each of your public containers with its FULL
  rule set (class, permission, limits, cost) — the "what of mine does Uriel
  affect" query.
- **`.uriel unsharemine`** — player bulk shutdown: reverts every container
  you shared (or that your castle team controls) back to private in one
  command; purges your stale entries.
- **`.uriel sharedall [name|steamId]`** (admin) — optional player filter;
  player resolution accepts a character-name fragment (must be unique) or a
  literal steamId.
- **`.uriel unshareplayer <name|steamId>`** (admin) — bulk shutdown of ALL of
  one player's shares.
- **Admin override on targeted containers:** admins can now aim at ANY
  container and use `.uriel share [modifiers]` to force/adjust its sharing
  settings (unshare-by-aim already worked). Players remain limited to
  containers they control.

### Notes — admin enable/disable (existing, now documented prominently)
- `PublicStorage.Enabled` (config) is the feature-wide kill switch: commands
  refuse, enforcement patches go inert, and on restart shared containers
  come up as normal private chests (the boot re-apply skips). Admin cleanup
  commands (`sharedall`/`unshareall`/`unshareplayer`) intentionally still
  work while disabled. `PublicStorage.PrisonEnabled` is the independent
  prison-cell switch. BepInEx reads config at boot — changes need a server
  restart.

## [0.5.0] - 2026-06-07

### Added — public prison cells (experimental)
- `.uriel share` aimed at a prison cell now shares it (was "coming soon"),
  gated by the separate `PublicStorage.PrisonEnabled` config switch. A public
  cell lets anyone tend the prisoner — feed, extract blood — or **charm the
  prisoner out as their own subdued follower** (the vanilla
  `InteractWithPrisonerSystem` Imprison/Charm/Kill flow; the cell's neutral
  team should let the built-in charm/subdue mechanics run for strangers with
  the interactor as the new escort — the headline thing to validate live).
- Prison entries are a distinct container class in the registry (`prison`),
  listed as such in `.uriel sharedall`; chest sharing and cell sharing remain
  independently governed, per design.
- Pay chests already refuse prison cells; unchanged.

### Fixed
- Manual deposits now respect inventory RESTRICTIONS
  (`InventoryInstanceElement.RestrictedType/RestrictedCategory` vs.
  `ItemData.ItemCategory`): the mod can no longer force a wrong-type item
  into a prison cell's feeding slots or a specialized stash via the
  deposit-execution path. Denied with "doesn't accept this type of item."

## [0.4.1] - 2026-06-06

### Fixed
- **Deposits into shared containers always failed with "container is full"**
  (live-test report). Root cause: placed containers keep their items on a
  separate attached external-inventory entity; v0.4.0 passed the TILE entity
  to `TryAddInventoryItem`, which silently fails. All inventory mutations now
  resolve the real inventory carrier first (`ResolveInventoryEntity` via
  `InventoryUtilities.TryGetInventoryEntity`). The same bug would have broken
  cost-payment delivery — fixed there too. (No items were ever lost — the
  refund path worked as designed.)
- Deposits now capacity-check before moving (consistent with payments) and
  every failure branch logs a `[Uriel SHARE]` warning for diagnosability.
- Unshare now restores the container's team from a SIBLING private container
  on the same castle heart (exactly the team a placed chest should carry),
  falling back to the castle heart, and logs the restored value.

### Notes
- If a chest still looks locked right after `.uriel unshare`, close and
  reopen it — the client UI can hold the pre-restore state.

## [0.4.0] - 2026-06-06

### Added
- **Stacked share modifiers, any order** — e.g.
  `.uriel share LimitHours 6 Cost 123456789 100 Permission Take`. All tokens
  are validated BEFORE anything is applied (one bad modifier = nothing
  changes). Single-modifier syntax unchanged. (VCF 0.10.x has no rest-of-line
  parameter, so this is a hand-rolled token parser over optional args.)

### Changed — payment delivery is now capacity- and restriction-safe
- **Capacity pre-check:** payment is collected from the taker only after a
  destination with room for the ENTIRE amount is found (counts empty slots ×
  max stack + same-item headroom via `ItemData.MaxAmount`) — a full chest can
  no longer cause partial transfers, duplication, or item loss.
- **Specialized stashes excluded:** containers with
  `InventoryInstanceElement.RestrictedCategory/RestrictedType` (lumber, seed,
  … stashes) are never used as payment destinations, and `.uriel paychest`
  refuses them outright (general storage only).
- **Delivery cascade:** designated pay chest → the shared container itself →
  NEAREST general non-shared storage on the same castle heart → if everything
  is full, the withdrawal is denied gracefully and the taker keeps their
  payment (clear message; never a crash).

## [0.3.0] - 2026-06-06

### Added — sharing policy modifiers (experimental)
- **Permissions** (`.uriel share permission take|give|givetake`): take-only,
  donation-box (give-only), or both (default). Enforced per move event.
- **Withdrawal limits** (`.uriel share limithours <h>` /
  `.uriel share limitwithdrawal <stacks>`): per-player rolling window —
  N stacks per H hours. Setting one side defaults the other (1 stack / 24h);
  0 clears. Usage tracked per steamId in the registry, persists restarts.
- **Access cost** (`.uriel share cost <itemId> <amount>`): each stack
  withdrawn auto-charges the taker; payment is delivered to the owner's
  designated pay chest (`.uriel paychest`, aimed at a private container) or
  into the shared container itself if none is designated. Refunds on full/
  missing payment destination. Item id 0 clears the cost.
- **Item catalog** (`.uriel finditem <name>`): searches all `Item_*` prefabs
  (built at init from the prefab collection), replies name → numeric id for
  use with `cost`.
- **`.uriel info`**: anyone can aim at a container to see its sharing rules
  (permission, limits, cost, sharer).
- Modifiers issued on an unshared container share it first, then apply.
- Owners/clan (castle-heart team) bypass all policies on their containers.

### Fixed
- **Strangers can now deposit into shared containers** (with permission
  give/givetake). Live testing showed vanilla refuses deposits into
  neutral-team containers (world-chest semantics) — a new
  `MoveItemBetweenInventoriesSystem` patch executes permitted deposits
  manually and cancels the vanilla event. "Move all" actions are blocked for
  non-controllers on policy-restricted containers (no per-stack accounting).

## [0.2.1] - 2026-06-06

### Fixed
- **"Sharing unavailable: no neutral team source found"** on live servers.
  World chests (and placed castle objects) carry `DisableWhenNoPlayersInRange`
  and sit `Disabled` whenever nobody is nearby — and default ECS queries skip
  disabled entities. Both the neutral-team donor lookup and the boot-time
  registry re-apply now query with `IncludeDisabled | IncludeSpawnTag`
  (the latter would have silently failed on every restart otherwise).
- Added a second donor fallback: if no placed world chest resolves at all,
  the neutral team is copied from the world chest *prefab* entity in the
  prefab lookup map, which always exists.
- Donor resolution now always logs which source it used (live vs. prefab,
  team value, team-ref entity) to ease diagnosing the in-game validation.

## [0.2.0] - 2026-06-06

### Added
- **Public storage (experimental — first implementation, NOT yet validated
  in-game).** Per-container opt-in sharing of placed castle containers:
  - `.uriel share` / `.uriel unshare` — aim at one of your castle containers
    to make it public / private (owner = anyone on the castle heart's team;
    the original sharer and admins can also unshare).
  - `.uriel shared` — list your public containers; `.uriel sharedall` /
    `.uriel unshareall` (admin) — list/revert everything.
  - Mechanism: team-swap. Sharing copies a live world chest's neutral
    `Team`/`TeamReference` onto the container (world chests are openable by
    everyone, and Team replicates to clients so the open prompt follows);
    unsharing restores the team from the container's own castle heart — no
    original-team persistence needed.
  - Registry persists to `BepInEx/config/Uriel/public_containers.json`,
    keyed by prefab GUID + tile coordinates (stable across restarts);
    re-applied at every server init. Saved on change and on plugin unload.
  - Guards: prison cells (separate upcoming feature) and servant coffins
    are refused; targeting requires aiming within `MaxTargetDistance`
    (config, default 5m); feature master switch `PublicStorage.Enabled`.
- New config: `PublicStorage.MaxTargetDistance`.
- `EntityExtensions` (IL2CPP-safe Exists/Has/Read/TryGetComponent/With,
  steamId + prefab-name helpers), adapted from the proven Beelzebub set.

### Known limitations / to validate in-game
- The team-swap hypothesis (neutral team ⇒ stranger can open + take/put)
  must be confirmed with a second account; fallback experiments are
  documented in `docs/features/PUBLIC_STORAGE.md`.
- Moving a shared container via castle edit changes its tile key; the share
  reverts to private on next restart (entry kept until `.uriel unshareall`).
- Raid/PvP interaction of neutral-team containers is untested.

## [0.1.0] - 2026-06-06

### Added
- Initial project scaffold: server-only BepInEx IL2CPP plugin (`kdpen.Uriel`),
  VCF command registration, Harmony bootstrap, deferred `Core` initialization
  gated on the Server world + populated `PrefabCollectionSystem`
  (via `SpawnTeamSystem_OnPersistenceLoad` postfix).
- `.uriel` root chat command (overview stub).
- Config scaffolding with per-feature master switches:
  `StairSwap.Enabled`, `PublicStorage.Enabled`, `PublicStorage.PrisonEnabled`,
  `Diagnostics.VerboseLogging`. No feature logic yet — switches are wired for
  the upcoming implementations.
- Design docs for the two launch features: stair hot-swap
  (`docs/features/STAIR_HOTSWAP.md`) and per-container public storage / public
  prison cells (`docs/features/PUBLIC_STORAGE.md`).
- Development process scaffolding: `CLAUDE.md`, `docs/PREFLIGHT.md`,
  `docs/DEV_REMINDERS.md`, release-surface sync checker (`tools/preflight.ps1`),
  Claude Code guard/reminder hooks (`.claude/hooks/`, local-only).
- Dual release surfaces: GitHub README/CHANGELOG (this file) + Thunderstore
  README/CHANGELOG under `Uriel/Uriel/`, `thunderstore.toml` manifest
  (namespace `kdpen`, deps: BepInExPack_V_Rising 1.733.2, VCF 0.10.4).

### Notes
- Not yet published to Thunderstore; no gameplay behavior changes yet.
