# Feature: Object Spawning (Extended Build Palette & World-Object Decor)

**Status:** RESEARCHED & APPROVED (2026-06-08). Feasibility fully mapped via
decompile + prefab-dump + reference-mod spikes. **Phase 1 (admin test-bed) is the
next build.** Inspiration (technique only — original code, AGPL-aware): odjit's
KindredCommands (`Helper.ConvertPosToTileGrid`, `CastleTerritoryService`) and
KindredSchematics (live spawn/move/delete of arbitrary placed entities).

## Concept

Let players (and admins) **spawn game objects into a castle** to expand what can
be built/decorated with — beyond the standard build menu. Two object worlds:

1. **Castle-buildables** (`TM_Castle_*`, `BP_Castle_*`, DLC families) — already
   carry `CastleHeartConnection` + `BlueprintData` + `EditableTileModel` +
   `TilePosition`. Mostly things players already unlock; primarily a **low-risk
   test-bed** to prove the spawn engine.
2. **World objects** (resource veins, trees, world chests, breakable props,
   `DG_Breakable_*`, urns, etc.) — found out in the world, classified as their
   own destroyable entity. They **lack** `CastleHeartConnection`/`BlueprintData`/
   `EditableTileModel`. **This is the real value**: novel decoration the build
   menu never offers.

## Owner requirements (non-negotiables)

1. **Spawn into the castle** — object appears at the player's aim point (or feet),
   owned by the castle they're standing in, persisting across restarts.
2. **Manipulable in build mode** — once placed, the object can be moved/rotated
   through the normal build UI (like any placed piece).
3. **Removable** — by command AND/OR build-menu dismantle.
4. **Indestructible by default** — spawned objects don't break, AND aren't eaten
   by castle decay. EXCEPTIONS: PvP servers (raid rules apply), or objects that
   are breakable by nature (resource veins/trees) when the host wants them so.
5. **Respawn-on-break** — breakable resource objects (veins/trees) regrow and
   keep yielding after harvest.
6. **Access:** admins spawn freely; players get a curated, cost-gated subset.

## Feasibility map (honest — from the spikes)

**🟢 Solved / proven (reuses shipped Uriel code):**
- The spawn recipe itself. `StairSwapService.SpawnPiece` already does:
  resolve `PrefabGUID → Entity` (`PrefabCollectionSystem._PrefabLookupMap`) →
  `EntityManager.Instantiate` → strip `Disabled` → write `Translation`/`Rotation`/
  `TilePosition`/`TileBounds`/`StaticTransformCompatible` → wire `Team`/
  `TeamReference`/`UserOwner`/`CastleHeartConnection`. Generalizing it to any
  prefab is a refactor, not new R&D.
- Removal: `DestroyUtility.Destroy` (no attach-parent walk) — shipped.
- **World → tile grid:** `Tile = int2(floor(x*2)+6400, floor(z*2)+6400)`,
  `CompressedHeight = 0` (height stays in `Translation.y`), `TileRotation = None`
  (KindredCommands `Helper.ConvertPosToTileGrid`). For a single-tile object
  `TileBounds.Value = {Min=Tile, Max=Tile}`.
- **Owning castle heart:** position → block coord (`gridPos/10`) → territory index
  → the `CastleHeart` whose `CastleTerritoryEntity.CastleTerritoryIndex` matches
  (KindredCommands `CastleTerritoryService`). Phase-1 shortcut: copy the heart +
  `Team`/`TeamReference`/`UserOwner` off the **nearest placed castle tile** to the
  spawn point (correct because it's the same castle); fall back to the
  player-team heart. Upgrade to the full territory map later.
- **Indestructible:** `ProjectM.Immortal { IsImmortal = true }` (confirmed on a
  castle bookshelf). **Decay-proof:** `ProjectM.CastleDecayAndRegen { CanDieFromDecay
  = false }` — answers the "castle reclaims my object" risk. Add the components if
  the prefab lacks them.

**🟡 Live-test unknown (code path known; only the client behavior is unverified):**
- **Build-menu editability of grafted WORLD objects.** Castle prefabs have
  `EditableTileModel`; raw world objects do not. The open question: does grafting
  `EditableTileModel` (+ `Team`/`CastleHeartConnection` adoption) make the build
  UI treat a world chest/prop as movable/dismantlable? Castle-buildables (Tier 1)
  already work — so Phase 1 confirms the rest of the pipeline while we test the
  graft on a world object in Phase 2.

**🔴 Needs its own mechanism (no native support):**
- **Respawn-on-break for wild resources.** A hand-spawned wild vein/tree will NOT
  respawn — respawn is owned by the world-region system or a `SpawnChain`, never
  the node entity. Two options: (a) run our own "detect death → re-spawn Stage0
  after a timer" loop (`Tick.RunLater`); (b) **prefer the castle-native chain
  prefabs** — `TM_Castle_Chain_Tree_*` / `TM_Castle_Chain_Plant_*` use
  `SpawnChainData { LoopOnEndOfChain = true }` and are *designed* to live in a
  castle and regrow on harvest. (b) is the clean path for "a harvestable that
  regrows in your castle."

## Command grammar (draft)

### Admin (Phase 1)
| Command | Does |
|---|---|
| `.uriel spawn <prefab> [rot] [breakable] [here]` | Spawn a prefab (name fragment or GUID int) at your aim point, adopted by the castle you're in. **`here` (aliases `nearest`/`me`) places at YOUR location instead of the cursor** — use this from a BCH UI button, where the cursor sits on the panel and the aim ray lands outside the plot ("…can only be spawned in a castle plot"). `rot` 0–3 = tile rotation. Indestructible + decay-proof by default. Spawn-chain controllers are refused (spawn the real object). |
| `.uriel move [here]` | Move the nearest session-spawned object to your aim point (respawn-based). Stand near it, aim, run — **or pass `here`/`nearest` to bring it to YOUR location** (for UI buttons, same cursor reason as `spawn`). |
| `.uriel rotate [0-3]` | Rotate the nearest session-spawned object — no arg turns 90°, 0–3 sets the tile rotation. |
| `.uriel despawn` | Remove the nearest spawned object you're aiming at / standing near (cross-session). |
| `.uriel spawnlist` | List the Uriel-spawned objects on the castle plot you're standing in. |
| `.uriel purgeplot` | Remove ALL Uriel-spawned objects on the plot you're standing in (admin-only). |
| `.uriel forcedespawn [confirm]` | **Admin** — force-remove the aimed/nearest object IGNORING Uriel records/ownership (recovers untracked objects, e.g. chain-era spawns). Arm (names the prefab), then `confirm` within 30s. |
| `.uriel forcepurgeplot` | **Admin** — force-remove every Uriel-LIKE indestructible object adopted into the plot (even untracked ones); native build pieces + breakables are left. |
| `.uriel findprefab <text>` | Search the **placeable-object** catalog by name (paged, ranked) to discover GUIDs/names to spawn. |
| `.uriel spawninfo` | Inspect the aimed/nearest spawned object (prefab, owner heart, flags, plot). |

**Placement rules (Phase 1.5):** every spawn/move must land INSIDE a castle plot
(`CastleTerritory`). Non-admins may only place/move/remove in a plot their team OWNS; admins
may act in any plot. Open-world placement is refused for everyone.

### Player / access (Phase 2 — BUILT Session 6; active only when `ObjectSpawn.AdminOnly=false`)
| Command | Does |
|---|---|
| `.uriel spawn <name\|guid> [rot]` | The unified build command. For non-admins it enforces access mode (Discovery → must be unlocked) + item cost; admins are free/unrestricted. (No separate `.uriel decor` — `spawn` serves both.) |
| `.uriel unlocks [player]` | List the objects you've unlocked + collection % (of the discoverable set). Admins: pass a player name to view theirs. |
| `.uriel catalog [page]` | Browse the full placeable-object catalog (paged) with totals (total + discoverable). |
| `.uriel notify <on\|off>` | Per-player toggle for your discovery chat messages (overrides the server `DiscoveryNotify` default). |
| `.uriel grant <player> <name\|guid>` | **Admin** — unlock an object for a player (covers non-destroyable objects discovery can't reach). |
| `.uriel revoke <player> <name\|guid>` | **Admin** — remove an object from a player's unlocks. |
| `.uriel block <guid\|name>` / `.uriel unblock` / `.uriel blocklist` | **Admin** — block/allow a prefab (by GUID primarily). Blocked = excluded from the catalog and refused at spawn for everyone. |
| `.uriel despawn` | Remove your own object; refunds the paid cost if `ObjectSpawn.RefundOnRemove=true`. |

**BCH wire API** (`[CommandGroup("uriel api")]`, ApiVersion 1 — see `docs/BCH_INTEGRATION_HANDOFF.md` §6):
`.uriel api version` (capabilities + totals), `.uriel api catalog <page>` (total in-game prefab
list), `.uriel api unlocked <page>` (the caller's unlocked prefabs + collection %). Emits
`[URIEL:*]` lines for BCH's "Uriel list", analogous to Beelzebub's ability-catalog API.

**Collection config:** `ObjectSpawn.CollectionEnabled` (master on/off for discovery-by-destruction),
`PlayerAccessMode` (Full/Discovery), `DiscoveryChancePercent`, `DiscoveryNotify` (server default;
per-player override via `.uriel notify`), `PrefabCostItem`/`PrefabCostStack`/`RefundOnRemove`.
Discovery messages fire only on a NEW unlock (already-owned destructions are silent).

## Phased plan

- **Phase 1 — admin test-bed (NEXT):** generalized `ObjectSpawnService` +
  `.uriel spawn`/`.uriel despawn`/`.uriel findprefab`/`.uriel spawninfo`, admin-only.
  Spawn ANY prefab, adopt into the nearest castle, apply Immortal +
  `CanDieFromDecay=false` by default. **Live tests to run:** (a) does a Tier-1
  buildable spawn + render + place correctly? (b) does a Tier-2 world object
  (e.g. a world chest, an urn, a tree) spawn + render? (c) is either editable/
  dismantlable in build mode? (d) is Immortal/decay-off holding? Results drive
  Phase 2.
- **Phase 1.5 — persistence & management (BUILT, Session 5):** spawned-object registry
  (`spawned_objects.json`, keyed by prefab GUID + tile coords); boot re-apply of
  Immortal/decay + live-cache rebuild so despawn/move/rotate work cross-session; orphan
  purge when the castle heart is gone (`ObjectSpawn.PurgeOrphansOnBoot`, default on);
  territory-based **placement gate** (inside-a-plot for all; owned-plot for non-admins,
  any plot for admins) replacing the old 60m nearest-tile adoption; `.uriel spawnlist`
  / `.uriel purgeplot`. (The `EditableTileModel` graft idea was dropped — Session-4
  proved it can't make objects build-menu selectable; objects are command-managed.)
- **Phase 2 — player decor:** curated catalog (admin-defined keys → GUIDs),
  cost-gated `.uriel decor`, per-player limits, ownership so players only remove
  their own. Reuse `PublicStorageService` cost/pay-chest model + `ItemCatalogService`.
- **Phase 3 — breakable/respawnable resources:** opt-in breakable spawns; for
  regrowth prefer castle-chain prefabs; otherwise a mod respawn loop. PvP-aware
  (leave `Immortal` off → vanilla raid rules).

## Engine reference (resolved primitives)

- **Spawn:** `Core.EntityManager.Instantiate(prefab)`; strip `Disabled`.
- **Prefab resolve:** `Core.PrefabCollectionSystem._PrefabLookupMap.TryGetValue(guid, out e)`;
  names via the lookup map / `SpawnableNameToPrefabGuidDictionary`.
- **Position:** the `here`/`nearest`/`me` token (spawn + move) forces the player's location via
  `PublicStorageService.TryGetCharacterPosition` — needed for BCH UI-button relays (cursor on the
  panel → aim ray outside the plot). Otherwise aim via `EntityAimData.AimPosition`, falling back to
  the character position when no aim is available.
- **World→tile:** `int2(floor(x*2)+6400, floor(z*2)+6400)`, `CompressedHeight=0`.
- **Heart (Phase 1):** nearest placed castle tile's `CastleHeartConnection` →
  copy `CastleHeartEntity` + `Team`/`TeamReference`/`UserOwner`; fallback heart by
  player team. (Phase 1.5: full `CastleTerritoryService` territory map.)
- **Indestructible:** `Immortal{IsImmortal=true}` + `CastleDecayAndRegen{CanDieFromDecay=false}` (add if absent).
- **Remove:** `DestroyUtility.Destroy(EntityManager, e)` (no attach-parent walk).
- **Editable graft (to test):** `EditableTileModel` (CanMoveAfterBuild/CanRotateAfterBuild/CanDismantle).
- **Resync to clients:** `PublicStorageService.ForceResync(e)`.

## Phase 1 testing log (live, local server)

**Build state:** Session-2 follow-up (items **A + B**, the classifier + chain-refusal
pass) is BUILT and compiles clean (0 warnings) but is **NOT yet deployed** — it sits
in `bin/Release`; deploying needs the dedicated server stopped (file lock). Commands
unchanged: `.uriel spawn <name|guid> [rot 0-3] [breakable|indestructible]`,
`.uriel despawn`, `.uriel spawninfo`, `.uriel findprefab <text> [page]` — all
admin-gated (`ObjectSpawn.AdminOnly`). (Server runs alongside Beelzebub; coexist OK.)

**🔧 Built in Session 2 (items A + B — `ObjectSpawnService`):**
- **Object catalog** — `EnsureCatalog()` lazily classifies the spawnable set by
  component signature and keeps only REAL placeable objects: a prefab qualifies via
  `TilePosition` / `EditableTileModel` / `CastleHeartConnection`; abilities/buffs and
  `SpawnChainData` controllers are dropped. Built once, logs
  `object catalog built: N placeable objects (M spawn-chain controllers skipped) of K spawnables`.
- **`findprefab`** now searches the catalog only, requires fragment length ≥3, and
  ranks exact > prefix > substring (kills the `urn`→`Return` noise).
- **Name resolution** (`.uriel spawn <name>`) matches the catalog, preferring a unique
  prefix over a pile of substring hits.
- **Chain refusal** — `.uriel spawn` of a `SpawnChainData` controller (by name OR GUID)
  is rejected with a pointer to the real object: "…spawn the real object instead:
  '.uriel findprefab <longest-token>'." Numeric-GUID spawns of non-chain prefabs stay
  permissive (admin power path).

**✅ Confirmed working (2026-06-08 session):**
- Tier-1 castle-buildable spawn: `.uriel spawn TM_Castle_ObjectDecor_Bench_Gothic01`
  spawned at the aim point, adopted into the surrounding castle
  (`immortal=True`, heart owned). Log: `[Uriel SPAWN] ...Bench_Gothic01(-569832943)
  at (-2016.1,5.0,-1685.2) rot=0 immortal=True Adopted into the surrounding castle.`
- `.uriel spawninfo` works.
- **Build-mode MOVE** of the spawned bench works (core editability for Tier-1 ✔).

**🐛 Fixed this session:** `.uriel findprefab` threw `FixedString512Bytes:
Truncation` — VCF chat replies are capped at 512 BYTES and a full page overflowed.
Fixed: pages are now byte-budgeted (`ReplyByteBudget=480`, pageSize 6, ASCII only,
`Clamp()` safety net). Deployed. (Lesson: every VCF `ctx.Reply` must stay < 512
bytes — applies to all future commands.)

**⏳ Pending tests (run these on resume, in order):**
1. **Tier-1 dismantle:** in build mode, dismantle the spawned bench — does it
   remove cleanly? (move already ✔; this confirms FULL editability.)
2. **World object render + editability (THE key test):**
   `.uriel findprefab Chest` (also `Tree`, `Urn`, `Node`) → `.uriel spawn <one>`
   → `.uriel spawninfo`. Watch: does it RENDER? Is it editable in build mode
   (move/dismantle)? Expectation/hypothesis: world objects lack `EditableTileModel`
   so build-menu editing likely WON'T work yet → that becomes the Phase-1.5 graft.
   `spawninfo` will show whether `build-editable` is present.
3. **Breakable path:** `.uriel spawn <world object> 0 breakable` → confirm it CAN
   be destroyed (vs. the immortal default).
4. **Cleanup:** `.uriel despawn` (removes nearest object spawned this session).
   NOTE: despawn tracking is in-memory only (Phase 1) — it won't find objects from
   a previous server session; dismantle those in build mode. JSON persistence +
   boot re-apply of Immortal/decay = Phase 1.5.

**✅/❌ Session 2 results (2026-06-08) — the pending tests, run:**
1. **Tier-1 dismantle ✅** — spawned Gothic bench dismantled AND moved cleanly in
   build mode. Tier-1 (castle buildable) full editability fully proven.
2. **findprefab ⚠️ noise** — `Chest`/`Tree` returned sensible hits, but `Urn`
   returned junk: short fragments substring-match unrelated names (`Return`,
   `Burn`, …) AND the spawnable dict contains non-objects (abilities, etc.). The
   catalog must be filtered to *real placeable objects* (see "Catalog audit" below).
3. **World object spawn ✅ renders / ❌ build-edit** — spawned
   `Chain_Container_WorldChest_Epic_01` by GUID: rendered, openable like in the
   world, but **NOT selectable in the build menu** (no move/dismantle). Confirms
   the hypothesis: world objects lack `EditableTileModel` → Phase-1.5 graft.
   `spawninfo` worked; `despawn` removed it.
4. **Immortal/breakable toggle ❌ no effect — ROOT CAUSE FOUND.** Toggling
   `breakable`/`indestructible` changed nothing: a naturally-immortal world chest
   stayed unbreakable even with `breakable`; a naturally-breakable crate
   (`Chain_Crate_Large_01`) stayed breakable even with `indestructible`.

**🔬 Root cause (prefab-dump audit): we spawned CHAIN CONTROLLERS, not objects.**
`Chain_*` prefabs are `SpawnChainData` controllers, NOT the object itself. Dump of
`Chain_Container_WorldChest_Epic_01(-1374682671)` / `Chain_Crate_Large_01(-1273737741)`
is pure chain machinery (`SpawnChainData+SpawnChainInstance{LoopOnEndOfChain:True}`,
`AutoChainInstanceData`, `DestroyData`) at pos `(0,0,0)` with **no `Health`, no
`Immortal`, no `TileModel`, no `Team`**. `Instantiate` of a `Chain_*` makes the
spawn-chain system spawn the *real* object (`TM_WorldChest_Epic_01_Full/_Empty`) as
a child. So:
- Our `Immortal`/decay edits landed on the inert **controller**, never the visible
  object → zero effect on destructibility (explains test 4 entirely).
- `spawninfo`'s `immortal=True` was reading the component *we* added to the
  controller — misleading, not the real object's state.
- `despawn` still worked because destroying the controller tore down its child.

The REAL object (`TM_WorldChest_Epic_01_Full`) is a proper tile model: has `Health
{MaxHealth:1, DestroyOnDeath:true}`, `TileModel`/`TileData`/`TilePosition`/`TileBounds`,
`Team`/`TeamReference`, collider — but **no `EditableTileModel`** and **no native
`Immortal`**. Its destructibility is `Health` + `HealthConstants.DestroyOnDeath` +
`DestroyAfterDuration(1200s)` — **NOT `Immortal`**. So even on the real object our
`Immortal`-based path wouldn't bite; indestructibility for world objects must
control the Health/Destroy path (DestroyOnDeath=false / drop DestroyAfterDuration /
boost MaxHealth) instead.

**➡️ Action items out of Session 2:**
- **A. Chain detection at spawn — ✅ BUILT (refuse-with-guidance).** Chose (ii): refuse
  `SpawnChainData` controllers and point at the real object. Chain-FOLLOWING (i) was
  rejected for Phase 1 — no reference mod reads the `SpawnChainBlobAsset`; the blob walk
  is risky in IL2CPP. The catalog surfaces the real `TM_*` object so users spawn it directly.
- **B. Catalog audit/filter — ✅ BUILT** (runtime component-signature classifier; see
  "Built in Session 2" above). Done at runtime over the spawnable dictionary, not by
  grepping the 23k dump — auto-tracks game patches.
- **C. World-object indestructibility via Health path** (not Immortal) — ✅ BUILT (2026-06-08),
  ⏳ live-validation pending. Audit confirmed `TM_GloomRot_Laboratory_*` AND `TM_WorldChest_*`
  carry `Health` + `HealthConstants.DestroyOnDeath` + `DestroyAfterDuration` and **no native
  `Immortal`** — so the old `Immortal`-only toggle was a no-op on world objects (the cause of the
  reported "breakable came in invulnerable" and the latent "indestructible chest auto-despawns").
  `ExecuteSpawn` now drives BOTH mechanisms:
  - **Indestructible:** `Immortal{true}` + decay-off + `HealthConstants.DestroyOnDeath=false` +
    `StripAutoDestroyTimers` (removes `DestroyAfterDuration`/`LifeTime`, so it can't die or evaporate).
  - **Breakable:** clear `Immortal` + `DestroyOnDeath=true` + strip the auto-despawn timer, so it
    follows vanilla raid/decay rules instead of vanishing on a hidden ~1200s clock.
  ⚠️ **Owner-smash caveat:** while an object is ADOPTED into the castle (owner team), V Rising blocks
  the OWNER from weapon-damaging it (vanilla castle protection — same reason you dismantle your own
  furniture rather than hitting it). So "breakable" means *raiders/enemies + decay* can destroy it, and
  the owner removes it with `.uriel despawn` — NOT that the owner can smash it by hand. Making breakable
  objects owner-smashable would require NOT adopting them onto the owner's team (open design choice —
  trade-off: no castle ownership, decays, anyone in the territory could damage/interact). `Health.MaxHealth`
  boost intentionally skipped (not needed once death + auto-despawn are off).

### Breakable modes + auto-respawn (2026-06-08) — ✅ BUILT, ⏳ live-validation pending

Spawn flags (order-independent, AFTER the rotation slot): `.uriel spawn <prefab> [rot] [flags…]`.
- **`breakable`** — raid/decay can destroy it; stays castle-owned, so the OWNER can't weapon-smash it
  (vanilla castle protection). Removed via `.uriel despawn`.
- **`smashable`** (aliases `smash`/`playerbreakable`) — breakable AND owner-destroyable. Implemented by
  **skipping castle adoption** in `ExecuteSpawn` (`playerBreakable` param): the object never gets the
  owner's `Team`/`CastleHeartConnection`, so the engine lets the owner damage it. Ownership/management
  still work — `CallerOwnsObject` resolves the owner from the object's POSITION (territory heart), not its
  components, and the spawn record still stores the heart tile for orphan-purge + respawn. Trade-off:
  un-owned (decays, anyone in the territory can damage/interact).
- **`respawn`** — the object auto-respawns after it's destroyed, **until the castle heart is gone or the
  player `.uriel despawn`s it.** Config: `ObjectSpawn.RespawnEnabled` (master switch, default on) +
  `ObjectSpawn.RespawnPollSeconds` (cadence, default 30, min 5).
- `indestructible` — permanent (default unless `ObjectSpawn.Indestructible=false`). Forces `playerBreakable`
  + `respawn` off (nothing to break/respawn).

**Auto-respawn mechanism** (`ObjectSpawnService.RespawnTick` / `TryRespawnRecord`, polled via
`Tick.RunRepeating`): the poll re-spawns every `RespawnOnDestroy` record whose entity is currently GONE
but whose castle heart still resolves. The "destroyed vs streamed-out" distinction is the safety crux —
`BuildLiveIndex` is Disabled-included, so a streamed-out object still resolves (only a truly destroyed one
is `Entity.Null`), and respawn is gated on `HeartExistsByTile` (region loaded / castle alive). So: a
streamed-out region (heart also absent) never duplicates, and a destroyed castle never resurrects its
objects. The SAME record is re-used (re-pointed at the fresh entity's tile) — never a new record — so it
keeps respawning and never duplicates. `SpawnRecord` schema bumped to **v3** (`Rot`, `RespawnOnDestroy`,
`PlayerBreakable`; old records load these as 0/false). Mode survives `.uriel move`/`rotate` (preserved
through `RespawnAt`).
- **D. `EditableTileModel` graft** (Phase 1.5) to make real world objects build-editable. ⏳

**✅ Session 2 live results (2026-06-08, items A+B deployed):**
- **Catalog filter works:** `object catalog built: 4483 placeable objects (1495 spawn-chain
  controllers skipped) of 14481 spawnables.` (Prefab map had 14481 entries.)
- **Chain refusal works:** `.uriel spawn -1374682671` → refused as a spawn-chain controller;
  `.uriel spawn Chain_Crate_Large_01` → "No placeable object matches" (chains are excluded
  from the name catalog, so the name doesn't resolve). Both correctly blocked. ✔
- **Real object spawn + Immortal works:** `.uriel spawn -770563232`
  (`TM_GloomRot_Laboratory_Table04`) spawned indestructible & unattackable — confirms
  **Immortal holds on a REAL object** (the Test-3 failure was purely the chain controller).
- **Two issues found:** (a) catalog build flooded the console with ~250
  `PrefabLookupMap.TryGet … unknown state` warnings; (b) `findprefab Bench` still leaked a
  debug prefab (`BP_Castle_Debug_ArtQuality_BenchmarkRoom`, -122207586); (c) the lab table is
  NOT build-editable (no `EditableTileModel`; our adoption also never wired its heart because
  it lacked `CastleHeartConnection` and the old code only set-if-present).

**🔧 Built in Session 2 follow-up (NOT yet deployed — needs server stopped):**
- **Warning flood fixed** — catalog now resolves via the raw `_PrefabLookupMap.GuidToEntityMap`
  (silent) instead of the warning-emitting `TryGetValue` wrapper.
- **Debug prefabs filtered** — name blacklist (`Debug`/`Benchmark`/`ArtQuality`/`Placeholder`/
  `Dummy`/`DELETE`/`_TBD`); count now logged separately.
- **Adoption ADDS castle components** — `CastleHeartConnection`/`Team`/`TeamReference`/`UserOwner`
  are now *added* when missing (world objects lacked them), not just set-if-present, so world
  objects become genuinely castle-owned.
- **Build-editability graft (item D, EXPERIMENTAL)** — adopted objects without a native
  `EditableTileModel` get one grafted (`CanMove/CanRotate/CanDismantle=true` + the bench's
  dismantle/repair ability GUIDs). New config `ObjectSpawn.GraftBuildEditable` (default true).
  Minimal attempt: heart-connection + EditableTileModel. If the build UI still won't manage the
  object, the next escalation is grafting `BlueprintData` + the full castle-building set.

**⏳ Next live tests (after redeploy):**
1. **No warning flood** — first `findprefab`/spawn-by-name: console should be quiet (no
   `unknown state` spam), and the catalog line should show a `… debug/dev prefabs skipped` count.
2. **Debug gone** — `.uriel findprefab Bench` no longer lists the `Debug…BenchmarkRoom`.
3. **THE graft test** — `.uriel spawn -770563232` (lab table) again → in build mode, can you now
   **move / rotate / dismantle** it? `.uriel spawninfo` should show `build-editable`. This resolves
   the 🟡 unknown. If yes → world-object decor is fully realized; if no → escalate to BlueprintData.
4. **Regression** — Tier-1 castle bench still spawns/moves/dismantles (graft must not overwrite a
   native EditableTileModel — it's guarded, but confirm).

**✅/❌ Session 3 live results (2026-06-08, items A+B follow-up + graft deployed):**
- **Warning flood GONE** ✔ — `object catalog built: 4452 placeable objects (1477 spawn-chain
  controllers, 96 debug/dev prefabs skipped) of 14481 spawnables.` Clean console.
- **Debug filter works** ✔ — 96 debug/dev prefabs skipped; `findprefab Bench` no longer leaks
  the benchmark room.
- **Immortal on world object** ✔ — `TM_GloomRot_Laboratory_Chair01(65705351)` spawned
  indestructible, Heart Owned, Can-Decay False.
- **❌ Build-menu editability graft INSUFFICIENT.** `spawninfo` shows `build-editable`
  (EditableTileModel present) BUT the chair **cannot be selected** in build mode (no move/
  rotate/dismantle). Native bench still fully works (regression clean).

**🔬 Root cause (item D is harder than a component graft):** vanilla build-mode selection
enumerates a castle heart's REGISTERED building pieces. Normal placement runs a
registration/attach pipeline (adds `BlueprintData` + `CastleBuildingAttachToParents/
AttachedChildrenBuffer`, increments the heart's building set, etc.). We set
`CastleHeartConnection` but never registered the object, so the heart doesn't list it and
build mode can't target it. Setting `EditableTileModel` makes the flag say "editable" but
does not make the piece *selectable*. No reference mod registers arbitrary objects as castle
buildings; KindredSchematics moves/deletes via its OWN commands, not the vanilla build menu.

**❌ Item D — D-escalate FAILED conclusively (Session 4, 2026-06-08).** `GraftBuildEditable`
copied the full castle-building set (BlueprintData re-pointed at the real object +
CastleBuildingMaxRange/CastleAreaRequirement/EntityCategory/DismantleDestroyData/
CastleRebuildPhaseState/LastEditedBy + the attach buffers + EditableTileModel) off the Gothic-bench
template. It applied cleanly (`[build-edit graft applied]`, **no exceptions**) — and the object was
STILL not selectable in build mode. **Verdict: vanilla build-menu editability of a hand-spawned
object is not achievable by component grafting.** Selection enumerates pieces the castle heart
REGISTERED via the placement pipeline; setting components (even the whole set) doesn't register the
piece, and no native registration hook exists in any reference mod (KindredSchematics moves/deletes
via its own commands, not the build menu). Marking item D's vanilla-build-menu path **🔴 not
feasible** for Phase 1.

**✅ Item D — D-commands BUILT (Session 4, not yet deployed):** world objects are now managed by
Uriel commands instead of the build menu:
- `.uriel move` — destroy + re-spawn the nearest session-spawned object at your aim point.
- `.uriel rotate [0-3]` — no arg turns 90°; 0-3 sets that tile rotation. Position preserved.
- `.uriel despawn` — unchanged.
Move/rotate are **respawn-based** (`RespawnAt`: destroy → `ExecuteSpawn` at the new transform,
preserving Immortal state + re-adopting), because these objects carry `MegaStaticCompatibleTag` and
render from baked static batches — an in-place transform edit risks the stair "invisible until
restart" bug. The dead `GraftBuildEditable` graft + `ObjectSpawn.GraftBuildEditable` config key were
removed; castle adoption (add CastleHeartConnection/Team/UserOwner) + Immortal/decay are kept.
`ExecuteSpawn`'s transform block was extracted to `ApplyTransform`, shared by spawn and respawn.

**🔧 Built in Session 5 (Phase 1.5 — full drop, deployed; awaiting live test):**
- **Placement gate (territory-based):** `TryResolvePlot(pos)` maps a position → block coord →
  `CastleTerritory` index → owning `CastleHeart` (KindredCommands pattern). Spawn/move refuse
  open world for everyone; non-admins must own the plot (`OwnsHeart`); admins any plot.
  Replaces the old 60m nearest-tile adoption — objects now adopt the resolved territory heart's
  team/owner.
- **Persistence:** `spawned_objects.json` (prefab GUID + tile coords + heart tile + territory +
  indestructible + sharer/time). `RegisterRecord` on spawn; `RemoveRecordFor` on despawn;
  move/rotate update via `RespawnAt`. `Load()` + `ReapplySpawned()` wired in `Core`.
- **Boot re-apply + orphan purge:** one sweep indexes all tile objects → re-resolves each record,
  re-applies Immortal/decay, drops records whose object is gone, and (if `PurgeOrphansOnBoot`)
  destroys objects whose castle heart no longer exists (`HeartExistsByTile`, disabled-included).
- **Management:** `.uriel spawnlist` (per-plot inventory), `.uriel purgeplot` (clear a plot, admin),
  `.uriel despawn` now cross-session + ownership-gated.

**⏳ Phase 1.5 live tests (run after restart):**
1. **Boot log** — after spawning some objects then restarting: console shows
   `loaded N record(s)` then `restored N object(s); 0 orphan(s) purged; 0 no longer present` —
   and despawn/move/rotate now work on objects from the previous session.
2. **Placement gate** — `.uriel spawn <obj>` aiming OUTSIDE any castle → refused
   ("only be placed inside a castle plot"). Aiming inside your castle → works.
3. **Ownership** (needs a 2nd non-admin player, or temporarily set `ObjectSpawn.AdminOnly=false`):
   a non-owner placing in someone else's plot → refused.
4. **Move boundary** — `.uriel move` aiming outside the plot → refused; inside → moves.
5. **Plot management** — `.uriel spawnlist` lists what's on the plot; `.uriel purgeplot` clears all.
6. **Orphan purge** — spawn in a castle, destroy the castle heart, restart → object is gone
   (with `PurgeOrphansOnBoot=true`); log shows `1 orphan(s) purged`.

**Known Phase-1 limitations to refine after these tests:**
- Multi-tile objects use a 1×1 `TileBounds` footprint (single-cell); refine if a
  large object mis-registers on the grid.
- `GetTerritoryIndex`/heart resolution rebuild their queries per call (fine at admin-spawn
  frequency); cache with a short TTL if it ever shows up on a busy server.

**🐛 Cross-session tracking FIX (Session 9, 2026-06-08 — owner live-test feedback).**
After v0.15.0 testing, the owner found spawned objects became unmanageable after a
**relog or server restart**: `despawn`/`move`/`rotate`/`spawninfo` reported "no Uriel
object within Nm" while standing on the object. Two distinct defects (both fixed):
- **(1) Stale entity cache.** All targeting walked an in-memory `List<Entity> _spawned`
  rebuilt only at boot. The engine recreates a castle object's entity (new handle)
  whenever the castle streams out and back in — relog, leaving/returning to the
  territory, OR a restart — so the cached handles went invalid, `PruneSpawned` dropped
  them, and nothing re-resolved the records to the new entities. **Fix:** removed the
  cache entirely; management now re-resolves each registry record to its CURRENT live
  entity on demand (`BuildLiveIndex` → `LiveIndex.Resolve`: match by prefab GUID + tile,
  with a **world-position fallback** for tile drift), mirroring the proven
  `PublicStorageService` (which keeps no entity cache). `SpawnRecord` now stores
  `PosX/Y/Z` (schema v2; backfilled for old records on first resolve).
- **(2) Destructive boot re-apply.** `ReapplySpawned` ran at `GameDataInitialized` and
  `_records.Remove(r)` + `SaveSync()` for any record it couldn't match in that one
  sweep — so an object merely streamed-out (or not yet loaded) was **permanently
  deleted from the registry** and the emptied file written to disk. **Fix:** re-apply is
  now NON-DESTRUCTIVE — unresolved records are KEPT and logged (like `ReapplyAll`); only
  a CONFIRMED orphan (object resolved but its heart is gone) is purged.
- **(3) Untracked-object recovery (admin).** Objects from the chain-controller era (or any
  record lost to defect 2) have no registry entry, so they can't be reached *by record*.
  New admin commands ignore records entirely: **`.uriel forcedespawn [confirm]`** (arm →
  names the exact prefab → `confirm` within 30s → destroy the aimed object; never targets
  the castle heart) and **`.uriel forcepurgeplot`** (destroy every object adopted into the
  plot's heart that carries our indestructible signature — `Immortal`, no `BlueprintData`
  — leaving native build pieces and breakables alone).

**🔬 Session 9 follow-up — why the stuck chest survived force-despawn (owner live-test).**
The log showed `force-despawned TM_WorldChest_Epic_01_Full (-1657744516) at tile (2338,3012)`
succeed, yet the chest **flashed and reappeared**, and `forcepurgeplot` matched **0**. Root
cause confirmed from the prefab dump: that chest is the *child* of a `Chain_*` spawn-chain
controller (`Chain_Container_WorldChest_Epic_01`) whose `SpawnChainData.SpawnChainInstance
{ LoopOnEndOfChain = true }` **re-spawns the child the instant it dies**. Force-despawn
destroyed the child; the still-living controller (sits at world-origin, no TilePosition/heart/
Immortal — untargetable by either command) looped and re-created it. And the child chest prefab
carries **no `Immortal` and no `CastleHeartConnection`**, so `forcepurgeplot`'s old filter
(Immortal + heart-connected) matched nothing.
- **The link:** the runtime child carries `ProjectM.Shared.SpawnChainChild { Entity SpawnChain;
  int SpawnChainElementIndex }` → its controller (verified by reflecting the ref assemblies).
- **Fix (Session 9b):** a single `DestroySpawned(e)` now routes ALL removals (despawn, move/rotate
  respawn, purgeplot, forcedespawn, forcepurgeplot, orphan purge): if the object has
  `SpawnChainChild`, its controller is destroyed FIRST (stops the loop, tears down the child), then
  the child if anything remains. `forcepurgeplot` was rewritten to scope by the plot's
  `CastleTerritoryBlocks` (O(1) per object) and match objects that are Immortal **OR** a
  `SpawnChainChild` **OR** heart-connected — so chain-era children are now caught. Native
  build-menu pieces (`BlueprintData`) and the heart are always skipped.

**⏳ Session 9 live tests (run after restart):**
1. **Cross-session manage** — spawn an object, relog (and separately, restart the
   server), then `.uriel spawninfo` / `move` / `rotate` / `despawn` while standing on it:
   all should now target it. Boot log: `re-applied N object(s); 0 orphan(s) purged;
   M not resolved this boot (KEPT …)` — and M should be 0 once the castle is loaded.
2. **No registry wipe** — confirm `spawned_objects.json` is NOT emptied across a restart.
3. **forcedespawn** — aim at the old stuck chest → `.uriel forcedespawn` names it →
   `.uriel forcedespawn confirm` removes it. Aiming at a wall/floor names that instead
   (don't confirm) — proves the naming safeguard.
4. **forcepurgeplot** — clears the plot's leftover indestructibles in one shot; a native
   bench/wall on the same plot survives.

## Phase 2 — player access: mode + discovery + cost (BUILT Session 6, 2026-06-08; awaiting live test)

**Status:** built & deployed. New files `Services/PlayerUnlockService.cs` (per-player
`player_unlocks.json`) + `Patches/DeathEventListenerSystemPatch.cs` (discovery hook). Config
keys (`ObjectSpawn.PlayerAccessMode`/`DiscoveryChancePercent`/`DiscoveryNotify`/`PrefabCostItem`/
`PrefabCostStack`/`RefundOnRemove`). Discoverable classifier + access/cost gates in
`ObjectSpawnService`. `.uriel unlocks`/`.uriel grant`/`.uriel revoke` commands. **All player
gating is inert while `ObjectSpawn.AdminOnly=true` (current test-bed default)** — flip it to
test the player path. **Build decisions / deviations from the original design below:**
- **No separate `.uriel decor`** — `.uriel spawn` is the single build command and enforces the
  player gates for non-admins (cleaner than a parallel command). `.uriel unlocks` = the old
  `decorlist`; `.uriel despawn` (with `RefundOnRemove`) = the old `undecor`.
- **Cost charges the player's OWN inventory only** (`InventoryUtilities.TryGetInventoryEntity`
  + `ServerGameManager.GetInventoryItemCount`/`TryRemoveInventoryItem`). Castle shared-stash
  payment is deferred (it needs the stash-network walk; non-trivial).
- **Discovery covers the destroyable subset; admin-grant covers the rest** (see Layer B).

**⏳ Phase 2 live tests (set `ObjectSpawn.AdminOnly=false`, ideally with a non-admin alt):**
1. **Full mode** (`PlayerAccessMode=Full`): a non-admin can `.uriel spawn` any catalog object
   in their own plot; still blocked outside their plot.
2. **Discovery mode** (`=Discovery`): same `.uriel spawn` is refused ("haven't discovered…")
   until the player destroys that object in the world; with `DiscoveryChancePercent=100`,
   destroying one immediately unlocks it (chat notice) and `.uriel spawn` then works. `.uriel
   unlocks` lists it.
3. **Non-destroyable**: try to discover an object with no Health (refusal names the grant path);
   `.uriel grant <self> <that object>` then lets you build it.
4. **Cost** (`PrefabCostItem`=<some item>, `PrefabCostStack`=N): spawn consumes N of the item;
   too few → refused. `RefundOnRemove=true` → `.uriel despawn` returns the N.
5. **Catalog log** now reports `… (D discoverable by destruction; …)` — sanity-check D.

### Original design notes (retained)

## Phase 2 design — player access: mode + discovery + cost (RESEARCHED 2026-06-08)

Planning for player-facing access (admins always have full, free access). Three independent,
admin-configurable layers, all gated AFTER the existing rules (inside-a-plot + own-plot).

### Layer A — access mode (`ObjectSpawn.PlayerAccessMode`)
- **`Full`** — players may spawn anything in the placeable catalog (still cost + own-plot gated).
- **`Discovery`** — players may only spawn objects they have personally UNLOCKED by destroying
  them in the world (see Layer B). Admins bypass.
- (Enum config; default `Discovery` is the interesting mode, `Full` the simple one.)

### Layer B — discovery tracking (the researched mechanism)
**Question answered:** V Rising fires `DeathEvent { Died, Killer, StatChangeReason }` through
`DeathEventListenerSystem` for **destroyed world objects too**, not just units — Bloodcraft
routes non-unit deaths (`Died` lacks `Movement`) by a player (`Killer.IsPlayer()`) into its
ProfessionSystem (resource nodes carry `YieldResourcesOnDamageTaken`/`DropTableBuffer`). That
same hook is exactly "player destroyed object X."
- **No native per-object discovery store** usable for this — the native `UnlockedPrefab`/
  `Journal`/`Discovered` system tracks V-bloods/milestones, not arbitrary world props. So Uriel
  keeps its OWN per-player unlock registry: `player_unlocks.json` (steamId → set of prefab GUIDs),
  mirroring the `PublicStorageService` JSON pattern.
- **Mechanism:** Harmony-postfix `DeathEventListenerSystem.OnUpdate` (Core.IsReady guard +
  try/catch — never throw across the patch boundary, per CLAUDE.md). For each event where
  `Killer` resolves to a player (`ValidateSource`-style: player, or their familiar/summon's owner)
  and `Died` is a catalog-eligible placeable object: roll `ObjectSpawn.DiscoveryChancePercent`
  (0–100). On success, add the prefab GUID to that player's unlock set + optional "you can now
  build X" notice. Naturally covers WORLD objects (chests/veins/breakables fire DeathEvent on
  Health→0); castle build-menu pieces aren't discovered this way (already player-accessible).
- **Open nuance:** harvested resource nodes transition through `Chain_*` stages — the `Died`
  prefab may be a stage, not the clean spawnable. Map to the catalog entry (or record raw +
  let the admin catalog curate). Decide at build time.

### Layer C — build cost (`ObjectSpawn.PrefabCostItem` + `ObjectSpawn.PrefabCostStack`)
- Admin sets a flat cost, e.g. `PrefabCostItem = <itemGuid>`, `PrefabCostStack = 100`: to spawn
  ANY object a player must hold (or have in their castle's shared inventory) 100× that item; it's
  consumed on spawn. `0` item = free.
- **Confirmed primitives:** `ServerGameManager.GetInventoryItemCount` (check),
  `TryRemoveInventoryItem` (charge), `TryAddInventoryItem` (refund on `.uriel undecor`).
  Validate the item GUID via `ItemCatalogService` (the existing `.uriel finditem`). Charge from
  the player's inventory first, then the castle's shared storage (reuse the `PublicStorageService`
  cost model / pay-chest plumbing). Admins exempt.
- Future: per-prefab or per-category cost overrides; for now one flat global cost (as requested).

### Player commands (Phase 2, when built)
`.uriel decor <catalogKey|guid> [rot]` (spawn, pays cost, Discovery-gated), `.uriel decorlist`
(what you've unlocked + costs), `.uriel undecor` (remove your own, optional refund). These wrap
the same `ObjectSpawnService` spawn/despawn already built, adding the access/cost gates.

### Build order (when greenlit)
1. Per-player unlock registry + `DeathEventListenerSystem` patch + `DiscoveryChancePercent` (Layer B).
2. `PlayerAccessMode` gate wired into spawn (Layer A).
3. Cost charge/refund + config (Layer C) + the `.uriel decor*` player commands.

## Catalog audit (Session 8, 2026-06-08)

Full prefab-dump audit of what survives the catalog filter (`Tiles.TileModel` is useless as a
discriminator — CHAR_/AB_/MicroPOI/EH/TM all carry it 100%, so name-family is the signal).
**Now excluded** (`NonObjectPrefixes` + component backstop): `CHAR_` units, `AB_` ability-effect
objects (spike traps, boss hazard spinners, continuous-damage areas), `GM_` debug props, `Liquid_`
placement-rule objects, `Summon*`/`USB_`/`PrefabVariant` internals. **False-positive fixed:** dropped
`"Dummy"` from the debug filter — it was wrongly excluding legit `TM_*_TargetDummy_*` decor.

**Unit/V-Blood exclusion is component-based, not just name (2026-06-08).** Audit confirmed every
`CHAR_*` prefab carries `TilePosition` (so it passes `IsPlaceableObject`) AND `Movement` (no real
placeable object has it); V Bloods additionally carry `VBloodConsumeSource`. `IsNonObject` now backstops
on `Movement || VBloodConsumeSource`, so a unit whose name doesn't start with `CHAR_` is still excluded.
**Defense in depth for stale data:** `CHAR_`/V-Blood GUIDs that were unlocked under an *older, looser*
filter still sat in `player_unlocks.json` and showed in the unlocked list / were spawnable (the unit
spawned then immediately vanished — it's not a real object). Two guards close this:
- **`.uriel spawn` refuses any GUID that isn't a real placeable object** (`IsRealPlaceableObject`, a
  structural check that ignores the *reversible* admin blocklist so blocking never deletes an unlock),
  and prunes that stale unlock on the spot.
- **The unlocked list self-heals:** `DescribeUnlocks` / `ApiUnlockedPage` run `PruneStaleUnlocks` first
  (`PlayerUnlockService.PruneUnlocked`), dropping non-object GUIDs from persistence on first view.
The discovery-on-death path was already safe (the roll guards on `IsDiscoverableGuid`, so killing a
`CHAR_` never unlocked it); the stale entries came from an older `grantall`/catalog, and `grantall` now
grants only from the current (clean) `_placeableGuids`.
**Kept (real world decor; admin can `.uriel block`):** `MicroPOI_*` (tree/flower clusters), `EH_*`
(armor racks, cages), `TM_*_Invisible*` (legit invisible build pieces), `*_WithCollision` variants.
**Known 1-off internals to block by GUID if undesired:** `TM_WarEvent_GateObject_DestroyTrigger`,
`TM_Castle_Floor_InvisibleRoofBlocker`.

**Castle-buildable toggle** (`ObjectSpawn.IncludeCastleBuildables`, default FALSE): the inherent
build-menu pieces are identified by the **`BlueprintData`** component (1198 prefabs = 1052 `TM_` +
146 `BP_`) — confirmed clean: every `BlueprintData` prefab is Castle-named, and the world objects we
keep (lab table, chest, resource vein) have it zero times. Excluded by default so the catalog focuses
on world objects players can't otherwise obtain; flip TRUE to also expose the standard buildables.
Component-based, not the substring "Castle". (Admins can still GUID-spawn anything.)

## Phase 3 — unlocking NON-destructible objects (BUILT Session 7, 2026-06-08; awaiting deploy/test)

**Status:** built & compiling (staged in `bin/Release`, NOT yet deployed — server was running). Also
in this build: **catalog unit-exclusion fix** — `CHAR_*` units carry `TilePosition`/`TileModel` so ~530
leaked into `.uriel catalog`; now excluded via the `CHAR_` name prefix + a `Movement` backstop (no real
placeable object has `Movement`). New catalog log: `… (… U units/NPCs … skipped)`.
- **(A) Non-destructible unlock trigger** — `ObjectSpawn.NonDestructibleUnlock` enum (replaced the
  earlier bool): `Off` (admin disable — only `.uriel grant`/`grantall`) | `Collection` (100% of the
  discoverable set) | `FinalBoss` (defeat Dracula `CHAR_Vampire_Dracula_VBlood` -327335305 = game
  completion — easiest) | `AllBosses` (defeat every main V-blood). All call `GrantIndestructibles`
  (idempotent bulk-grant of the non-discoverable placeables; notifies once). FinalBoss/AllBosses fire
  from the V-blood death; `AllBosses` tracks a per-player defeated-boss set (`PlayerUnlockService`) vs
  the `_bossRoster` (CHAR_*_VBlood with `VBloodConsumeSource`, gate-fight duplicates excluded ≈ 61).
  **Research note:** V-bloods carry native `VBloodConsumeSource.Tier` + `JournalCategory` (region) +
  `VBloodUnlockTechBuffer`, but OBJECTS carry no reciprocal tier/boss tag — so per-boss object
  auto-association isn't possible from native data (curated map only); the final-boss completion gate
  needs no mapping, which is why it's the recommended path.
- **(B) Boss-tier** — `ObjectSpawn.BossUnlocksEnabled` + `boss_unlocks.json` (V-blood GUID → object
  GUIDs). `.uriel bossmap <add|remove|list>` builds it in-game (ships empty). The death hook's
  `GrantBossObjects` grants on a mapped-V-blood kill. **⚠️ Live-test risk:** V-blood (feed) kills may
  not attribute `Killer=player` through `DeathEventListenerSystem` the way object/mob kills do
  (Bloodcraft uses a separate `VBloodSystem` patch for V-blood unlocks) — if boss unlocks don't fire on
  a V-blood kill in testing, switch detection to a `VBloodSystem`/`VBloodConsumed` hook. Also: familiar
  kills (Killer = familiar, not PlayerCharacter) won't trigger — direct player kills only for v1.
- The death hook now gates on `TracksKills` (was `DiscoveryActive`) = collection open to players in
  Discovery mode with ≥1 unlock source (discovery chance OR boss map) active.

### Original design notes (retained)

## Phase 3 design — unlocking NON-destructible objects (RESEARCHED 2026-06-08)

Discovery-by-destruction can't reach ~44% of placeable objects (no Health). Two admin-configurable
progression paths to grant those, beyond the existing `.uriel grant`:

**Research finding:** there is **no clean tier/region component** on object prefabs — location is
name-encoded (GloomRot/Cursed/Ruins/Church/Bandit/Militia/SilverLight…), incomplete (no Farbane
keyword) and noisy ("Castle" ×1130). Objects have a coarse `ResourceLevel`; bosses are `CHAR_*_VBlood`
with `UnitLevel`. ⇒ a name-keyword auto-mapping is too leaky to trust; a **curated mapping** is the
reliable route.

- **(A) Completion unlock** — `ObjectSpawn.UnlockNonDestructibleOnCompletion` (bool). When a player's
  collection hits 100% of the discoverable (non-blocked) set, grant the non-destructible placeables
  (all, or a configured subset). Cheap & robust — the % is already computed; check it in
  `HandleKill`/on unlock and bulk-grant once. No fuzzy data.
- **(B) Boss-tier unlock** — a CURATED `boss_unlocks.json`: V-blood (boss) PrefabGUID → { tier, object
  GUIDs it unlocks }. Reuse the existing `DeathEventListenerSystem` hook: when a player kills a mapped
  V-blood, grant those objects (tier-gated — the boss IS the tier). Uriel ships a starter map; admins
  extend/override. Config `ObjectSpawn.BossUnlocksEnabled`. NOT name-heuristic (too leaky).
- The two compose: a server can enable either/both as independent sources of non-destructible unlocks.
  `.uriel grant` remains the manual override.

## References
- KindredCommands (odjit, AGPL-3.0) — `Helper.ConvertPosToTileGrid`,
  `Services/CastleTerritoryService.cs` (position → heart). Technique only.
- KindredSchematics (odjit, AGPL-3.0) — live spawn/move/delete of arbitrary
  placed entities; the public-build recipe Uriel already mirrors.
- Uriel `StairSwapService.SpawnPiece`/`Capture` and `PublicStorageService`
  (the spawn recipe + neutral-team/heart wiring this feature generalizes).
