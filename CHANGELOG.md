# Changelog — Uriel, Lord of Hosts (full)

This is the **complete** changelog (GitHub). The Thunderstore package carries
a condensed, player-facing changelog at `Uriel/Uriel/CHANGELOG.md`; every
release updates **both** (see CLAUDE.md → "Release & changelog discipline").

Format: [Keep a Changelog](https://keepachangelog.com/) flavored;
versions follow the mod's own incremental scheme (pre-1.0: minor = feature
batch, patch = fixes).

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
