# Changelog — Uriel, Lord of Hosts (full)

This is the **complete** changelog (GitHub). The Thunderstore package carries
a condensed, player-facing changelog at `Uriel/Uriel/CHANGELOG.md`; every
release updates **both** (see CLAUDE.md → "Release & changelog discipline").

Format: [Keep a Changelog](https://keepachangelog.com/) flavored;
versions follow the mod's own incremental scheme (pre-1.0: minor = feature
batch, patch = fixes).

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
