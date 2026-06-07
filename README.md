# Uriel, Lord of Hosts

A **server-side** BepInEx mod for [V Rising](https://playvrising.com/) dedicated
servers — a *host* of independent quality-of-life enhancements and structural
fixes, each individually toggleable by server admins.

> This is the **GitHub / developer** page. The player-facing mod page (what
> ships to Thunderstore) lives at [`Uriel/Uriel/README.md`](Uriel/Uriel/README.md).

**Status: pre-release.** Not yet published to Thunderstore.

## Features

| Feature | Status | Design doc |
|---|---|---|
| **Public storage** — per-container opt-in: mark a specific chest publicly accessible, with optional permissions (take/give), per-player withdrawal limits, per-stack access costs paid to the owner, and `nearest` targeting for UI relays | Implemented; open+take validated live; v0.9.0 rebuild-on-share mechanism + policies pending validation | [docs/features/PUBLIC_STORAGE.md](docs/features/PUBLIC_STORAGE.md) |
| **Public prison cells** — separately mark a prison cell publicly accessible: feed/extract via the native UI, prisoner takeover via `.uriel takeprisoner` (the native subdue button is client-gated to the cell's clan — confirmed unreachable server-side) | Implemented; feed/extract validated live; takeprisoner pending validation | [docs/features/PUBLIC_STORAGE.md](docs/features/PUBLIC_STORAGE.md) |
| **Stair hot-swap** — restyle placed stairs in place (same-shape cosmetics only, per-user DLC ownership gating) | Implemented; swap validated live; v0.9.0 editability fix pending validation | [docs/features/STAIR_HOTSWAP.md](docs/features/STAIR_HOTSWAP.md) |
| **BloodCraftHub integration** — client-side companion UI (share panels, prisoner-take button, stair picker) | Contract authored; BCH-side work pending | [Uriel/Uriel/docs/BCH_INTEGRATION_HANDOFF.md](Uriel/Uriel/docs/BCH_INTEGRATION_HANDOFF.md) |

Every feature ships behind its own config switch — Uriel is never
all-or-nothing.

## Architecture

- **Server-only** BepInEx IL2CPP plugin (`net6.0`); `Plugin.Load` early-returns
  on anything that isn't `VRisingServer`. No client install needed or wanted.
- Commands via [VampireCommandFramework](https://thunderstore.io/c/v-rising/p/deca/VampireCommandFramework/).
- Deferred initialization (`Core.TryInitialize`) — no game-type statics before
  the server world + prefab data exist.
- Sibling project of [Beelzebub, Lord of Gluttony](https://github.com/KDavidP1987/Beelzebub-Lord-of-Gluttony)
  (same author, same architecture).

## Building

```powershell
cd Uriel
dotnet restore Uriel.sln
dotnet build Uriel.sln -c Release
```

The build auto-deploys `Uriel.dll` to a local V Rising dedicated server at the
default Steam path if present (override with `-p:VRisingServerPath=...`; point
it at a non-existent path to skip deployment). Stop the server before
redeploying — it file-locks the DLL.

## Repository docs

- [`CLAUDE.md`](CLAUDE.md) — working agreements & architecture guide
- [`docs/PREFLIGHT.md`](docs/PREFLIGHT.md) — session-start checklist
- [`docs/DEV_REMINDERS.md`](docs/DEV_REMINDERS.md) — IL2CPP/ECS gotchas & process rules
- [`Uriel/Uriel/docs/BCH_INTEGRATION_HANDOFF.md`](Uriel/Uriel/docs/BCH_INTEGRATION_HANDOFF.md) — the BloodCraftHub living contract
- [`CHANGELOG.md`](CHANGELOG.md) — full changelog (the Thunderstore package
  carries a condensed player-facing changelog)

## License

[MIT](LICENSE)
