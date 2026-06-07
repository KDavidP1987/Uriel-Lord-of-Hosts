# Changelog — Uriel, Lord of Hosts (full)

This is the **complete** changelog (GitHub). The Thunderstore package carries
a condensed, player-facing changelog at `Uriel/Uriel/CHANGELOG.md`; every
release updates **both** (see CLAUDE.md → "Release & changelog discipline").

Format: [Keep a Changelog](https://keepachangelog.com/) flavored;
versions follow the mod's own incremental scheme (pre-1.0: minor = feature
batch, patch = fixes).

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
