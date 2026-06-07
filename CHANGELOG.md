# Changelog — Uriel, Lord of Hosts (full)

This is the **complete** changelog (GitHub). The Thunderstore package carries
a condensed, player-facing changelog at `Uriel/Uriel/CHANGELOG.md`; every
release updates **both** (see CLAUDE.md → "Release & changelog discipline").

Format: [Keep a Changelog](https://keepachangelog.com/) flavored;
versions follow the mod's own incremental scheme (pre-1.0: minor = feature
batch, patch = fixes).

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
