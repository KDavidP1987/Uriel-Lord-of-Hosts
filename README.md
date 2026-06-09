# Uriel, Lord of Hosts

![Uriel, Lord of Hosts](https://raw.githubusercontent.com/KDavidP1987/Uriel-Lord-of-Hosts/main/docs/img/uriel-cover.jpg)

A **server-side** BepInEx mod for [V Rising](https://playvrising.com/) dedicated
servers — a *host* of independent quality-of-life enhancements and structural
fixes, each individually toggleable by server admins.

> This is the **GitHub / developer** page. The player-facing mod page (what
> ships to Thunderstore) lives at [`Uriel/Uriel/README.md`](Uriel/Uriel/README.md).

## ⚠ Status: pre-1.0, in active development

Uriel is **pre-release** and not yet published to Thunderstore. The features
below are implemented and tested **in local and dedicated development/test-server
environments — not yet on a live production server**, which is why it's pre-1.0.
This is a **server-side mod under active development** — if you run it, you're
helping test it, and **you take that risk on yourself.** Back up your server save
before installing any mod, and trial it on a test server first. Commands, config
keys, and behavior may change before 1.0.

**Bug reports & feedback:** the **[The Shadow Realm Discord](https://discord.gg/usC9QgBrXK)**
is the primary channel (fastest path to a fix in the next release); written-up
GitHub issues are welcome too.

## Features

Several feature groups are live today, with more sub-mods planned under the umbrella.
Every feature ships behind its own config switch — Uriel is never all-or-nothing.

| Feature | Status | Design doc |
|---|---|---|
| **Object spawning** — collect & place **world objects** the build menu never offers (resource nodes, world chests, breakable props, dungeon/GloomRot/Cursed decor) inside your castle plot; runtime catalog classifier (units/abilities/internals/castle-buildables filtered out), territory-gated placement, JSON persistence + orphan cleanup, a collect-by-destruction unlock model (Discovery/Full access, build cost, boss/completion unlocks for non-destructibles), admin block/grant tools, and a `[URIEL:*]` API for BloodCraftHub | Implemented; placement/catalog/discovery validated live; boss-kill unlock triggers + BCH API consumption pending validation | [docs/features/OBJECT_SPAWNING.md](docs/features/OBJECT_SPAWNING.md) |
| **Public storage** — per-container opt-in: mark a specific chest publicly accessible, with optional permissions (take/give), per-player withdrawal limits, per-stack access costs paid to the owner, and `nearest` targeting for UI relays | Implemented; open+take validated live; rebuild-on-share mechanism + policies pending broader validation | [docs/features/PUBLIC_STORAGE.md](docs/features/PUBLIC_STORAGE.md) |
| **Public prison cells** — separately mark a prison cell publicly accessible: feed/extract via the native UI, prisoner takeover via `.uriel takeprisoner` (the native subdue button is client-gated to the cell's clan — confirmed unreachable server-side) | Implemented; feed/extract validated live; takeprisoner pending broader validation | [docs/features/PUBLIC_STORAGE.md](docs/features/PUBLIC_STORAGE.md) |
| **Stair editing** — restyle placed stairs **live** (destroy+respawn; same-shape cosmetics only, per-user DLC gating), plus `.uriel removestairs` to cleanly delete a staircase without disturbing connected floors/walls | Implemented; live restyle validated locally on straight/curved/wide shapes; broader testing pending | [docs/features/STAIR_HOTSWAP.md](docs/features/STAIR_HOTSWAP.md) |
| **BloodCraftHub integration** *(optional companion — not a dependency)* — client-side UI that can drive Uriel's features (share panels, prisoner-take button, stair picker); every feature also works via chat commands | Contract authored; BCH-side work pending | [Uriel/Uriel/docs/BCH_INTEGRATION_HANDOFF.md](Uriel/Uriel/docs/BCH_INTEGRATION_HANDOFF.md) |

> **Note for players accessing a share:** when a chest or cell is first made
> public, a player trying to use it may need to **log out and back in once**
> before their client recognizes it as accessible.

## Commands

Chat commands prefixed with `.uriel` (VCF). Type `.uriel` for an overview or
**`.uriel help`** for a clean, topic-by-topic menu (`objects` / `storage` /
`stairs` / `admin`). Most targeted commands accept a trailing **`nearest`** token
to act on the closest object instead of where you're aiming (for UI relays / when
a menu has your aim ray pointing away).

<details>
<summary><b>🏺 Object Spawning commands</b></summary>

**Players** (active when `ObjectSpawn.AdminOnly=false`; you must be inside your own castle plot):

| Command | What it does |
|---|---|
| `.uriel spawn <name\|guid> [rot 0-3] [flags…]` | Place an object you've unlocked at your aim point. Indestructible & decay-proof by default; flags (any order, after rotation): `breakable`, `smashable` (owner can break it), `respawn` (auto-returns), `here` (place at your location) |
| `.uriel move [here]` | Move the nearest object you spawned to your aim point (or `here` = your location) |
| `.uriel rotate [0-3]` | Rotate the nearest object you spawned (no arg = turn 90°) |
| `.uriel despawn` | Remove the nearest object you spawned (refunds cost if enabled) |
| `.uriel unlocks` | Your collected objects + collection % |
| `.uriel catalog [page]` | Browse the full world-object catalog |
| `.uriel findprefab <text>` | Search objects by name |
| `.uriel notify <on\|off>` | Toggle your object-unlock chat messages |

In **Discovery** mode you unlock objects by **destroying them in the world** (configurable chance); non-destructible objects unlock on full collection / boss defeats / admin grant.

<details>
<summary><i>Admin sub-commands</i></summary>

| Command | What it does |
|---|---|
| `.uriel grant\|revoke <player> <name\|guid>` | Unlock / remove an object for a player |
| `.uriel grantall <player> [all\|destructible\|indestructible]` | Bulk-grant the catalog (or a subset) |
| `.uriel block\|unblock <guid>` · `.uriel blocklist` | Forbid / allow a prefab (excluded from catalog + collection %) |
| `.uriel spawnlist` · `.uriel purgeplot` | List / clear all spawned objects on the plot you're in |
| `.uriel forcedespawn [confirm]` | Force-remove the aimed object, ignoring records/ownership (recovers untracked objects); names it, then `confirm` within 30s |
| `.uriel forcepurgeplot` | Synonym for `purgeplot` (live spawns + records; native objects never touched). The old chain sweep was removed — it could delete native resources |
| `.uriel purgeorphans` | Server-wide cleanup: scan the whole map and remove orphaned Uriel objects (castle gone / no living heart governing them). Backup for the boot-time orphan purge |
| `.uriel bossmap add\|remove\|list <vblood> <obj>` | Curate which objects a V-blood defeat unlocks |
| `.uriel api version\|catalog\|unlocked` | `[URIEL:*]` machine API for BloodCraftHub |
</details>
</details>

<details>
<summary><b>🗄 Public Storage &amp; Prison commands</b></summary>

**Players** (aim at a container you own):

| Command | What it does |
|---|---|
| `.uriel share [modifiers…]` | Make the aimed container public. Modifiers **stack in any order**: `permission take\|give\|givetake`, `limithours <h>`, `limitwithdrawal <stacks>`, `cost <itemId> <amount>` |
| `.uriel unshare` | Make the aimed container private again |
| `.uriel info` | Show a container's sharing rules (anyone) |
| `.uriel shared` | List every container **you** have made public |
| `.uriel unsharemine` | Revert **all** of your shares in one command |
| `.uriel paychest` | Designate the aimed **private** chest to receive access-cost payments |
| `.uriel finditem <name>` | Search item ids by name (for the `cost` modifier) |
| `.uriel takeprisoner` | Take the prisoner from a **shared** cell — subdued and released to you |

<details>
<summary><i>Admin sub-commands</i></summary>

| Command | What it does |
|---|---|
| `.uriel sharedall [name\|steamId]` | List all public containers, or just one player's |
| `.uriel unshareplayer <name\|steamId>` | Revert all of one player's shares |
| `.uriel unshareall` | Revert every public container to private; clear the registry |
| `.uriel sharedebug` | Dump the aimed container's live sharing state (diagnostics) |

Admins may also aim at **any** container and use `.uriel share […]` / `.uriel unshare` to override directly.
</details>
</details>

<details>
<summary><b>🪜 Stair Editing commands</b></summary>

**Players** (aim at a staircase you own):

| Command | What it does |
|---|---|
| `.uriel stairswap <style\|next>` | Restyle the aimed staircase **live**. Styles: `stone1`, `stone2`, `stone3`, `gloomrot`*, `projectk`*, `strongblade`* (`*` = DLC). `next` cycles to the next style you own. |
| `.uriel removestairs` | Cleanly delete the aimed staircase, leaving connected floors/walls intact |
| `.uriel stairstyles` | Show the aimed stair's shape, current style, and the styles you can use |

<details>
<summary><i>Admin sub-commands</i></summary>

| Command | What it does |
|---|---|
| `.uriel stairpurge` | Destroy stray "ghost" stair entities within 5m (cleanup for older builds) |
</details>
</details>

## Configuration

`BepInEx/config/kdpen.Uriel.cfg` — every feature has its own `Enabled` switch
(changes take effect on server restart).

| Section | Key | Default | Effect |
|---|---|---|---|
| PublicStorage | Enabled | `true` | Master switch for chest sharing |
| PublicStorage | PrisonEnabled | `true` | Independently allow prison-cell sharing |
| PublicStorage | MaxTargetDistance | `5` | Aim distance for `.uriel share` / `unshare` |
| StairSwap | Enabled | `true` | Allow live stair restyling + `.uriel removestairs` |
| StairSwap | MaxTargetDistance | `6` | Aim distance for the stair commands |
| StairSwap | RespawnGapFrames | `5` | Frames between destroy & respawn when restyling a stair |
| Diagnostics | VerboseLogging | `false` | Extra per-action log lines (useful when testing) |

## Architecture

- **Server-only** BepInEx IL2CPP plugin (`net6.0`); `Plugin.Load` early-returns
  on anything that isn't `VRisingServer`. No client install needed or wanted.
- Commands via [VampireCommandFramework](https://thunderstore.io/c/v-rising/p/deca/VampireCommandFramework/).
- Deferred initialization (`Core.TryInitialize`) — no game-type statics before
  the server world + prefab data exist.

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

## Roadmap

Uriel is an umbrella mod — the plan is to keep adding small, independently
toggleable server-side sub-mods. Full list (incl. large-scope/exploratory ideas)
in **[`docs/ROADMAP.md`](docs/ROADMAP.md)**. Highlights:

**Near-term sub-mods:**
- **Coffin sharing** — share coffin use, with an optional cost-to-use modifier.
- **Conditional door access** — share a door behind payment and/or allowed time
  windows.
- **Bulk storage sharing** — open all heart-linked storage at a location at once,
  auto-excluding the pay chest and any chests marked private.

**Platform / integration:**
- **BloodCraftHub integration** — client-side companion UI (share panels,
  prisoner-take button, stair-style picker) against Uriel's command surface.
- **Machine-readable `[URIEL:*]` API** — structured replies for companion UIs
  (see the [BCH handoff](Uriel/Uriel/docs/BCH_INTEGRATION_HANDOFF.md) §6).
- **Stair editing polish** — broader testing of attachment/decay/pathing edge
  cases on the live-rebuild path.

**Exploring (large-scope):** plot expansion (phase-gate instances or map
copy/paste), PvP-arena V Blood boss mode with prize tables, paid area-gating for
mazes, scheduled/paid door & teleport controls, expanded castle decor assets,
triggered NPC spawns, and basement levels. See
[`docs/ROADMAP.md`](docs/ROADMAP.md) for details.

## Acknowledgements

- **[VampireCommandFramework](https://thunderstore.io/c/v-rising/p/deca/VampireCommandFramework/) — by deca** — the command framework (hard dependency).
- **[BepInEx](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/)** — the loader that makes V Rising modding possible.
- **[KindredCommands & KindredSchematics](https://thunderstore.io/c/v-rising/p/odjit/) — by odjit** — open-source castle/tile techniques referenced for the live stair rebuild.
- **[Bloodcraft](https://thunderstore.io/c/v-rising/p/zfolmt/Bloodcraft/) — by zfolmt** — reference for server-side ECS/Harmony patterns.

## Feedback & community

Developed and tested **locally and on dedicated development/test servers** — not
yet on a live production server. It comes out of the V Rising community **The
Shadow Realm** (Brutal PvE), maintained by Chaos. Pre-1.0 testers shape what 1.0
becomes — feedback is hugely valued.

- **The Shadow Realm Discord (primary):** https://discord.gg/usC9QgBrXK
- Support development: [PayPal](https://www.paypal.com/paypalme/KrisPenland) · [SkillEra.IO](https://SkillEra.IO)

## Repository docs

- [`CLAUDE.md`](CLAUDE.md) — working agreements & architecture guide
- [`docs/ROADMAP.md`](docs/ROADMAP.md) — shipped / planned / exploratory feature roadmap
- [`docs/PREFLIGHT.md`](docs/PREFLIGHT.md) — session-start checklist
- [`docs/DEV_REMINDERS.md`](docs/DEV_REMINDERS.md) — IL2CPP/ECS gotchas & process rules
- [`Uriel/Uriel/docs/BCH_INTEGRATION_HANDOFF.md`](Uriel/Uriel/docs/BCH_INTEGRATION_HANDOFF.md) — the BloodCraftHub living contract
- [`CHANGELOG.md`](CHANGELOG.md) — full changelog (the Thunderstore package
  carries a condensed player-facing changelog)

## License

Licensed under the **GNU Affero General Public License v3.0
([AGPL-3.0](LICENSE))** — copyright © 2026 Kristopher Penland.

Uriel adapts server-side modding techniques and patterns from odjit's
AGPL-licensed [KindredCommands](https://github.com/Odjit/KindredCommands) and
[KindredSchematics](https://github.com/Odjit/KindredSchematics); in keeping with
their copyleft, Uriel is released under the same license. As an AGPL work, the
complete corresponding source is available in this repository.
