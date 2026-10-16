# Uriel, Lord of Hosts

![Uriel, Lord of Hosts](https://raw.githubusercontent.com/KDavidP1987/Uriel-Lord-of-Hosts/main/docs/img/uriel-cover.jpg)

A **server-side** BepInEx mod for [V Rising](https://playvrising.com/) dedicated
servers — a *host* of independent quality-of-life enhancements and structural
fixes, each individually toggleable by server admins.

> This is the **GitHub / developer** page. The player-facing mod page (what
> ships to Thunderstore) lives at [`Uriel/Uriel/README.md`](Uriel/Uriel/README.md).

## ⚠ Status: pre-1.0, in active development

Uriel is **pre-1.0** and in public testing. The features below are implemented and
tested **in local and dedicated development/test-server environments — not yet on a
live production server**, which is why it's pre-1.0. Each feature carries a stage:
**beta** (in testers' hands, core paths validated live) or **alpha** (developer-tested
on a dev server only, off by default). The [roadmap](docs/ROADMAP.md) defines the
stages and what remains before 1.0.
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

| Feature | Stage and status | Design doc |
|---|---|---|
| **Castle Events** — turn a castle into an event space: spawn markers for mobs and bosses grouped per floor, doors held shut and locked until their group is defeated, layered templates (squad → floor template → castle template) with random picks at every layer, per-run scaling (presets, level or `auto` = highest player level, health/damage/speed/attack-speed multipliers via marker-buff stat mods), one faction per run, a clock with boss and completion times, time limits and run history. Units are contained to the plot, a room, a storey or a leash radius; never saved; removed at boot | **Alpha**, off by default. Developer-tested in game (vrclient scenarios, single player); multiplayer, player-castle path and Raphael UI pending | [docs/features/CASTLE_EVENTS.md](docs/features/CASTLE_EVENTS.md) |
| **Object spawning** — collect & place **world objects** the build menu never offers (resource nodes, world chests, breakable props, dungeon/GloomRot/Cursed decor) inside your castle plot; runtime catalog classifier (units/abilities/internals/castle-buildables filtered out), territory-gated placement that rejects abandoned/decayed plots, JSON persistence + boot-time and **mid-session orphan cleanup**, a collect-by-destruction unlock model (Discovery/Full access, boss/completion unlocks for non-destructibles), **admin-managed global + per-object spawn conditions** (max-per-plot, item cost, permit-indestructible/respawn), admin block/grant tools, and a `[URIEL:*]` API for Raphael | **Beta.** Placement/catalog/discovery validated live; abandon-cleanup + spawn-conditions + boss-kill unlock triggers + Raphael API consumption pending validation | [docs/features/OBJECT_SPAWNING.md](docs/features/OBJECT_SPAWNING.md) |
| **Public storage** — per-container opt-in: mark a specific chest publicly accessible, with optional permissions (take/give), per-player withdrawal limits, per-stack access costs paid to the owner, and `nearest` targeting for UI relays | **Beta.** Open+take validated live; rebuild-on-share mechanism + policies pending broader validation | [docs/features/PUBLIC_STORAGE.md](docs/features/PUBLIC_STORAGE.md) |
| **Public prison cells** — separately mark a prison cell publicly accessible: feed/extract via the native UI, prisoner takeover via `.uriel takeprisoner` (the native subdue button is client-gated to the cell's clan — confirmed unreachable server-side) | **Beta.** Feed/extract validated live; takeprisoner pending broader validation | [docs/features/PUBLIC_STORAGE.md](docs/features/PUBLIC_STORAGE.md) |
| **Stair editing** — restyle placed stairs **live** (destroy+respawn; same-shape cosmetics only, per-user DLC gating), plus `.uriel removestairs` to cleanly delete a staircase without disturbing connected floors/walls | **Beta.** Live restyle validated locally on straight/curved/wide shapes; broader testing pending | [docs/features/STAIR_HOTSWAP.md](docs/features/STAIR_HOTSWAP.md) |
| **Raphael integration** *(optional companion — not a dependency; formerly BloodCraftHub)* — client-side UI that can drive Uriel's features (share panels, prisoner-take button, stair picker); every feature also works via chat commands | Object Spawning API live (ApiVersion 1); Castle Events handoff written, wire API and companion screens pending | [BCH_INTEGRATION_HANDOFF.md](Uriel/Uriel/docs/BCH_INTEGRATION_HANDOFF.md), [RAPHAEL_CASTLE_EVENTS_HANDOFF.md](Uriel/Uriel/docs/RAPHAEL_CASTLE_EVENTS_HANDOFF.md) |

> **Note for players accessing a share:** when a chest or cell is first made
> public, a player trying to use it may need to **log out and back in once**
> before their client recognizes it as accessible.

## Commands

Chat commands prefixed with `.uriel` (VCF). Type `.uriel` for an overview or
**`.uriel help`** for a clean, topic-by-topic menu (`objects` / `storage` /
`stairs` / `events` / `admin`). Most targeted commands accept a trailing **`nearest`** token
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
| `.uriel raise [m]` / `.uriel lower [m]` | Nudge the nearest object up/down so it sits flush with the ground (default 0.25m) |
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
| `.uriel objcfg\|objcfgglobal <field> …` · `.uriel objcfglist` | Set/list global + per-object spawn conditions: `max <n>` per plot, `cost <amount> <itemGuid>`, `indestructible <true\|false>`, `respawn <true\|false>` (these gate players; admins unrestricted) · `yoffset <metres>` (permanent flush-to-ground height, applies to everyone) |
| `.uriel api version\|catalog\|unlocked` | `[URIEL:*]` machine API for Raphael |
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

<details>
<summary><b>🏰 Castle Events commands (alpha)</b></summary>

Admins on any plot; castle owners on their own plot when `CastleEvents.PlayerEvents=true`.
Full syntax and reply text: [`RAPHAEL_CASTLE_EVENTS_HANDOFF.md`](Uriel/Uriel/docs/RAPHAEL_CASTLE_EVENTS_HANDOFF.md).

| Command | What it does |
|---|---|
| `.uriel event marker <group> [mob\|boss] [unit\|-] [count] [level] [contain\|-]` | Spawn marker where you stand; the group is made on its first marker, on your floor |
| `.uriel event door\|undoor <group>` | Bind / unbind the castle door nearest you (within 6 m) |
| `.uriel event squad <group> <squad\|->` · `list` · `remove <#id\|group>` | Group's own squad · layout · remove |
| `.uriel event start [template\|-] [options]` · `status` · `stop` · `history` | Run control |
| `.uriel squad create\|add\|extend\|remove\|weight\|list\|show\|delete …` | Squads: sets of `<mob\|boss> <units> <count> <any\|distinct> [level]` entries |
| `.uriel floor create\|assign\|list\|show\|delete …` | Floor templates: group → squads |
| `.uriel template create\|floor\|options\|list\|show\|delete …` | Templates: floor → floor templates, default options |
| `.uriel unit spawn\|list\|kill\|despawn\|clear\|purge …` (admin) | Unit tools and the server-wide kill switch |
| `.uriel unit status\|probe\|probemap\|rooms\|door …` (admin) | Diagnostics; details go to the server log |

Run options: `easy|normal|hard|brutal`, `level=N`, `level±N`, `auto[+N]`, `hp*X`, `dmg*X`, `pp*X`,
`sp*X`, `speed*X`, `aspd*X`, `limit=15m`, `faction=keep|one`.
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
| CastleEvents | Enabled | `false` | Master switch for Castle Events and the unit commands (alpha) |
| CastleEvents | PlayerEvents | `false` | Castle owners may run events on their own plot |
| CastleEvents | DefaultContain | `storey` | Containment for a marker that doesn't name one |
| CastleEvents | MaxMarkersPerPlot | `40` | Marker cap for players (admins unlimited) |
| CastleEvents | MarkerCostItem / MarkerCostAmount / BossMarkerCostAmount | `0` | Player cost per marker, refunded on removal |
| CastleEvents | RunCostItem / RunCostAmount | `0` | Player cost per run start |
| CastleEvents | MaxUnitsPerCommand / MaxUnitsPerPlot / MaxTrackedUnits | `10` / `60` / `300` | Unit caps |
| CastleEvents | MaxSpawnsPerTick / MaxDespawnsPerTick | `5` / `5` | Spawn and removal budget per tick |
| CastleEvents | UnitLifetimeSeconds | `3600` | The game destroys a unit after this long, even without Uriel |
| CastleEvents | ClearDrops | `true` | Spawned units drop no loot |
| CastleEvents | ContainCheckSeconds | `1` | How often containment is checked |
| Diagnostics | VerboseLogging | `false` | Extra per-action log lines (useful when testing) |

Object Spawning keys are listed on the [Thunderstore page](Uriel/Uriel/README.md#configuration).
Persistent data lives under `BepInEx/config/Uriel/`: `castle_events.json`, `spawned_objects.json`,
`player_unlocks.json`, `object_conditions.json`, `blocked_prefabs.json`, `boss_unlocks.json`,
`public_containers.json`.

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

## In-game testing

Features are tested by driving the real game client against a local dev server:

- `tools/devserver.ps1 status | stop | start | deploy` builds, deploys (verifying the DLL hash), and restarts
  the dev server, waiting for `Uriel initialized`.
- `tools/vrclient/` plays the game as the test character, runs scenario scripts from
  `tools/vrclient/scenarios/*.vrs` (chat commands, console commands, screenshots, log expectations), and writes
  a `SCENARIO PASS n/n` line plus a results JSON. Guide: [`tools/vrclient/README.md`](tools/vrclient/README.md).
- The mod writes one greppable log line per state change a test needs (`[Uriel UNIT] …`, `[Uriel EVENT] …`).
  Results are recorded in each feature doc under `## Test results`.

## Roadmap

Full detail, including the maturity stages, in **[`docs/ROADMAP.md`](docs/ROADMAP.md)**.

| Milestone | Scope |
|---|---|
| 1. Harden the beta features | Live-server validation of Object Spawning, Public Storage and Stair Editing; cheaper move/rotate/delete; the open swapped-stairs ownership report |
| 2. Castle Events to beta | Second-player and multiplayer tests, plot-edge containment retest, remaining indoor checks (servants and defences, idle plots, sunlight), an `[URIEL:event*]` wire API, Raphael screens |
| 3. Castle Events complete | Toll door that starts a run, entry trigger and capacity limit, reward tables, event objects (decor and barriers), maze stair variants |
| 4. Castle Rentals | Timed coffin and room leases ([design](docs/features/CASTLE_RENTALS.md)) |
| 1.0 | Every feature validated on a live server; commands and config keys frozen; Raphael covers the main screens |

After 1.0 (exploratory): conditional door access, bulk storage sharing, teleporters, plot expansion, expanded
castle decor. Shelved: basement levels ([why](docs/features/BASEMENT_BUILDING.md)).

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
- [`docs/ROADMAP.md`](docs/ROADMAP.md) — maturity stages, road to 1.0, exploratory ideas
- [`docs/PREFLIGHT.md`](docs/PREFLIGHT.md) — session-start checklist
- [`docs/DEV_REMINDERS.md`](docs/DEV_REMINDERS.md) — IL2CPP/ECS gotchas & process rules
- [`Uriel/Uriel/docs/BCH_INTEGRATION_HANDOFF.md`](Uriel/Uriel/docs/BCH_INTEGRATION_HANDOFF.md) — the Raphael (companion-mod) living contract
- [`Uriel/Uriel/docs/RAPHAEL_CASTLE_EVENTS_HANDOFF.md`](Uriel/Uriel/docs/RAPHAEL_CASTLE_EVENTS_HANDOFF.md) — building Raphael's Castle Events screens
- [`docs/features/`](docs/features/) — one design doc per feature, with test results
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
