# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this workspace is

**Uriel, Lord of Hosts** (folder name: *Uriel Lord of Oaths* — the title was
renamed; the local folder was not) — a **server-side** BepInEx IL2CPP plugin
for V Rising's dedicated server. Uriel is an umbrella mod: a *host* of
independent quality-of-life enhancements and structural fixes, each
individually toggleable by server admins. It is the sibling of **Beelzebub,
Lord of Gluttony** (same author, same architecture, separate repo).

The only buildable project lives at:

```
Uriel/                                  ← target codebase (Uriel.sln)
```

Initial feature set (design docs in `docs/features/`):

1. **Stair hot-swap** (`docs/features/STAIR_HOTSWAP.md`) — swap placed stairs
   to another stair type from the build menu without demolishing first.
2. **Public storage** (`docs/features/PUBLIC_STORAGE.md`) — per-container
   opt-in: an owner marks a *specific* chest (or, separately, a prison cell)
   as publicly accessible. Never all-or-nothing; chests and prison cells are
   distinct container types governed independently.

## Reference-only paths (READ, never edit)

All reference material lives in the **sibling Beelzebub workspace** — this
project reads from it but **must never modify anything there**:

```
..\Beelzebub Lord of Gluttony\                          ← entire workspace is read-only from here
├── Beelzebub\                                          ← proven mod architecture to mirror
├── Learning Mods\Bloodcraft-main\                      ← zfolmt's server mod (ECS/Harmony patterns)
├── Learning Mods\KindredCommands-main\                 ← odjit's admin-command mod (simple VCF pattern)
├── Learning Mods\VampireCommandFramework-main\         ← VCF framework source
└── Reference Data\Prefabs\                             ← V Rising prefab dump (~23,500 .txt, one per GUID)
```

- Prefab lookup: glob `..\Beelzebub Lord of Gluttony\Reference Data\Prefabs\<Prefix>_<keyword>*.txt`
  (filenames are `<PrefabName> PrefabGuid(<int>).txt`). Stairs/castle pieces
  are typically `TM_*`; abilities `AB_*`; buffs `Buff_*`.
- A `PreToolUse` hook (`.claude/hooks/guard-reference-paths.ps1`) warns when an
  edit targets the Beelzebub workspace; treat the warning as a stop sign unless
  the user explicitly asked for the edit (and even then, prefer making
  Beelzebub edits from a session rooted in that workspace).

## Project layout

```
Uriel/
├── Uriel.sln
└── Uriel/                      ← C# project root
    ├── Uriel.csproj            ← single version source (auto-generates MyPluginInfo)
    ├── Plugin.cs               ← entry point (BasePlugin.Load; server-only guard)
    ├── Core.cs                 ← deferred init hub (world/system handles; IsReady gate)
    ├── Patches/                ← Harmony patches (one file per patched system)
    ├── Services/               ← feature services (stateless logic + state owners)
    ├── Commands/               ← VCF commands (.uriel ...)
    ├── Config/Settings.cs      ← BepInEx config bindings (every feature has Enabled switch)
    ├── README.md               ← THUNDERSTORE mod page (player-facing)
    ├── CHANGELOG.md            ← THUNDERSTORE changelog (concise, player-facing)
    └── thunderstore.toml       ← Thunderstore manifest (versionNumber synced to csproj)
docs/
├── PREFLIGHT.md                ← session-start checklist (read at the top of every session)
├── DEV_REMINDERS.md            ← standing IL2CPP/ECS gotchas + process reminders
└── features/                   ← one design doc per feature; keep current as features evolve
tools/
└── preflight.ps1               ← automated release-surface sync check (run before any release commit)
```

- Plugin GUID: `kdpen.Uriel`
- Target framework: `net6.0`, IL2CPP via `BepInEx.Unity.IL2CPP` 6.0.0-be.733,
  V Rising types via `VampireReferenceAssemblies`, commands via
  `VRising.VampireCommandFramework` (NuGet 0.10.*; Thunderstore dep
  `deca-VampireCommandFramework`). Versions intentionally match Beelzebub so
  both mods coexist on the same server. VCF 0.11.0 exists upstream — bump both
  mods together if/when needed.
- GitHub repo: `KDavidP1987/Uriel-Lord-of-Hosts`.

## Build & local deploy

```powershell
cd "Uriel"
dotnet restore Uriel.sln
dotnet build Uriel.sln -c Release
```

**Deploy target:** the local V Rising Dedicated Server (Steam Tool AppID
1829350) at `C:\Program Files (x86)\Steam\steamapps\common\VRisingDedicatedServer\BepInEx\plugins`.
The `BuildToServer` target auto-copies the DLL after every build when that
folder exists (same flow as Beelzebub). To build **without** deploying (e.g.
CI-style compile checks), point the server path at a non-existent folder:

```powershell
dotnet build Uriel.sln -c Release -p:VRisingServerPath=C:\__nodeploy__
```

The dedicated server file-locks the DLL while running — **stop the server
process** before redeploying.

## Versioning — single source

`BepInEx.PluginInfoProps` auto-generates `MyPluginInfo.PLUGIN_VERSION` from
`<Version>` in `Uriel.csproj`. There is no version constant in `Plugin.cs`.

## Release & changelog discipline — SIX surfaces move together

Unlike Beelzebub (which uses one changelog/README for both audiences), Uriel
deliberately keeps **separate GitHub and Thunderstore documents**. On every
version bump, all of these move together in one `chore(release): vX.Y.Z` commit:

1. **Version** — `Uriel/Uriel/Uriel.csproj <Version>` **and**
   `Uriel/Uriel/thunderstore.toml versionNumber`. Keep identical.
2. **`CHANGELOG.md` (repo root)** — the FULL GitHub changelog. Complete,
   technical-when-useful history. Every version gets an entry; never collapse
   or skip versions in multi-version batches.
3. **`Uriel/Uriel/CHANGELOG.md`** — the THUNDERSTORE changelog. Concise and
   player-facing (Thunderstore page real estate is limited — keep entries
   short, trim ancient versions to a "see GitHub for full history" link when
   the file grows long). Staged into the package by `BuildToDist`.
4. **`README.md` (repo root)** — GitHub landing page (developer/admin facing:
   architecture, building, contributing).
5. **`Uriel/Uriel/README.md`** — Thunderstore mod page (player/admin facing:
   what it does, commands, config). `thunderstore.toml description` is the
   ≤250-char listing tagline.
6. **Feature docs** — if the release changed a feature's behavior, its
   `docs/features/*.md` design doc must reflect reality.

Run `tools/preflight.ps1` before any release commit — it checks version
parity and that both changelogs have an entry for the current version.
A `PostToolUse` hook (`.claude/hooks/release-sync-reminder.ps1`) fires on
edits to the version files / changelogs and surfaces this checklist. Hooks
are backstops; **this CLAUDE.md rule is the authoritative process.**

## Things to watch out for (IL2CPP / V Rising server)

- **IL2CPP, not Mono**: static field initializers that touch
  `ComponentType.ReadOnly(Il2CppType.Of<T>())` NRE at `Plugin.Load` because
  `TypeManager` isn't built yet. Defer to first use or to `Core.TryInitialize`
  (wired via `Patches/GameDataInitializedPatch.cs`).
- **Server-only guard**: `Plugin.Load` early-returns unless
  `Application.productName == "VRisingServer"`. Never assume client systems exist.
- **`Core.IsReady` gate**: every Harmony patch body that touches game state
  must early-return `if (!Core.IsReady) return;` — patches can fire during
  server boot before init completes.
- **Never throw across a patch boundary**: wrap patch bodies in try/catch and
  log; an unhandled exception inside a Harmony hook can corrupt the server
  tick (see Beelzebub's `ServerBootstrapSystemPatch` for the pattern).
- **Pick the right system to patch**: V Rising often has several systems that
  *look* right (e.g. multiple death/interaction surfaces). Read how Bloodcraft
  or KindredCommands hook the same surface before choosing; the wrong one
  fires too early, too late, or multiple times.
- **Persistence**: anything the mod must remember across restarts (e.g. which
  containers are public) needs its own JSON persistence under
  `BepInEx/config/Uriel/` — V Rising's save file won't carry mod state.
  Save on change (debounced) AND on `Plugin.Unload`.

## BCH integration handoff — keep it current

`Uriel/Uriel/docs/BCH_INTEGRATION_HANDOFF.md` is the **living contract**
BloodCraftHub (the client-side companion mod, separate workspace at
`..\..\BloodCraftUI 2\`) builds against: the chat-command surface, the reply
shapes BCH parses, the replicated state markers that identify shared
containers client-side (§2 of the doc), config keys, and the future
`[URIEL:*]` wire API.

**Rule:** whenever ongoing work changes anything BCH-facing — a chat command
(name/args/reply text), the shared-container replicated markers
(Team/TeamReference/CastleHeartConnection recipe), a config key, or a
`[URIEL:*]` line once the api command exists — update the handoff doc **in
the same commit**. Purely internal changes need no doc update. A
`PostToolUse` hook (`.claude/hooks/bch-relevance-reminder.ps1`) fires on
edits to `Commands/*.cs`, `Config/Settings.cs`, and the share-mechanism
service files as a backstop; this CLAUDE.md rule is authoritative. The two
workspaces never cross-edit — integration flows through chat commands only.

## Git workflow

- Conventional Commits:
  `feat|fix|chore|docs|refactor|test|build|ci|perf|style|revert(scope)?: subject`
- Release commits: `chore(release): vX.Y.Z`.
- `gh` CLI is authenticated as `KDavidP1987`.
- Thunderstore publication is deferred until the mod is ready; GitHub is the
  public home until then.

## Session-start sanity checks

Work `docs/PREFLIGHT.md` at the top of every session. Summary:

1. `git status` — confirm clean state before starting.
2. About to edit anything under `..\Beelzebub Lord of Gluttony\`? STOP — that
   workspace is read-only from here.
3. Feature work? Open the matching `docs/features/*.md` first and keep it
   current as the design evolves.
4. Deploying? Confirm the dedicated server process is stopped first.
