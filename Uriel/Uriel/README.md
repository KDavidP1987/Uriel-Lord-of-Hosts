# Uriel, Lord of Hosts

![Uriel, Lord of Hosts](https://raw.githubusercontent.com/KDavidP1987/Uriel-Lord-of-Hosts/main/docs/img/uriel-cover.jpg)

A **server-side** umbrella mod for V Rising dedicated servers — a *host* of
independent, admin-toggleable quality-of-life enhancements and structural fixes.

---

## ⚠ Heads-up before you install

**Pre-1.0 — in active development.** Uriel is pre-release and evolving quickly.
The features below are implemented and have been tested, but
this is a **server-side mod still under development**: by running it you are
helping test it, and **you take that risk on yourself.** Please **back up your
server save** before adding any new mod. Commands, config keys, and behavior may
still change before 1.0.

**Found a bug or have an idea?** The fastest way to get a fix into the next
release is the **[The Shadow Realm Discord](https://discord.gg/usC9QgBrXK)** —
that's the primary bug-report and feedback channel. Written-up GitHub issues are
welcome too.

---

**Repo:** https://github.com/KDavidP1987/Uriel-Lord-of-Hosts
**Status:** pre-1.0 public testing · **server-side only** · several feature groups
live today, more sub-mods planned under the Uriel umbrella.

## What it does

Uriel is never all-or-nothing: **every feature ships behind its own config
switch**, and all sharing/editing is **per-object opt-in** — nothing is ever
shared or changed server-wide on your behalf.

### 🏺 Object Spawning *(collect & place world objects)*

Bring **world objects the build menu never offers** into your castle — resource
nodes, world chests, breakable crates and urns, and decorative props from across
the map (GloomRot, Cursed Forest, dungeons, ruins). Spawn one at your aim point —
or **at your own location** with the `here` flag (handy from a UI button) — inside
your own plot; it's **indestructible and decay-proof by default**, and you
move/rotate/remove it with `.uriel move` / `.uriel rotate` / `.uriel despawn`.
Everything you place persists across restarts and is cleaned up automatically if
its castle is ever destroyed.

**Choose how durable it is** with optional spawn flags: `breakable` (raids and
decay can destroy it), `smashable` (you can destroy it by hand too), or
`respawn` (it automatically comes back after being destroyed — until its castle
is gone or you `.uriel despawn` it). Mix them, e.g. `.uriel spawn <object> 0
smashable respawn`.

**Collect them by playing (Discovery mode).** You unlock an object by **destroying
one in the world** — chop a tree, smash a crate, break an ore vein — with an
admin-set chance to "learn" it. `.uriel unlocks` shows your collection and
percentage; `.uriel catalog` browses everything that exists; `.uriel notify on|off`
toggles the unlock messages. Roughly half of all objects can't be destroyed — those
unlock when you hit 100% collection, **defeat Dracula** (finish the game), or per
boss via an admin-curated map. Admins choose the model (or turn it off), can set a
build cost, block specific objects, and grant objects directly.

> **How it's managed:** spawned world objects are placed and moved with the
> `.uriel` commands above, not the vanilla build menu (an engine limitation —
> the game won't let arbitrary world objects be selected in build mode). The
> optional **BloodCraftHub** companion can present a point-and-click palette UI.

### 🗄 Public Storage *(per-container opt-in)*

Mark a **specific** chest as publicly accessible so anyone on the server can use
it — community chests, donation boxes, free-stuff stashes, even paid vending
boxes. Always owner-controlled; nothing is shared unless its owner shares it.
Shares survive server restarts.

**Sharing rules** (set by aiming at your shared container):
- **Permission** — take-only, give-only (donation box), or both.
- **Withdrawal limit** — e.g. 1 stack per player per 24 hours.
- **Access cost** — charge an item per stack withdrawn; the payment is delivered
  to a private "pay chest" you designate. A vending machine, basically.

**Public prison cells** *(separate switch)* — share a prison cell to let anyone
tend your prisoner: feed and extract blood through the normal cell UI, and take
the prisoner home with `.uriel takeprisoner` (they're subdued and released to
the taker — bring Dominating Presence to escort them). The game's own subdue
button only ever appears for the cell's own clan (an engine limitation), so the
command covers everyone else.

> **Heads-up for players accessing a share:** when a chest or cell is first made
> public, a player trying to use it may need to **log out and back in once**
> before their client recognizes it as accessible. After that initial relog, it
> behaves like any other container you can open.

### 🪜 Stair Editing

- **Live restyle** — aim at a placed staircase and swap it to another style;
  the change applies **instantly for everyone, no server restart or relog**
  (the stair is rebuilt in the new style on the spot). Only styles of the **same
  stair shape** (straight, left-curve, right-curve, wide) are offered, and DLC
  styles require owning that DLC — exactly the same rule as your build menu.
  Free, preserves position, rotation, and ownership.
- **Clean removal** — `.uriel removestairs` deletes a staircase you own **without**
  tearing apart the floors and walls it's attached to (something a normal
  dismantle can't cleanly do).

## Installation

Uriel is a **server-side** mod — install it on your **V Rising dedicated
server**, not on player clients. Players need nothing installed.  

### Via Thunderstore Mod Manager / r2modman (recommended)

1. Open the mod manager and select **V Rising → your dedicated-server profile**.
2. Search for **Uriel** under the Mods tab → **Install**.
3. Ensure its dependencies are installed in the same profile (they install
   automatically): **BepInExPack_V_Rising** and **VampireCommandFramework**.
4. Launch the server through the mod manager's profile.

### Manual install

1. Install [**BepInExPack V Rising**](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/)
   (1.733.2 or compatible) into your dedicated server.
2. Install [**VampireCommandFramework**](https://thunderstore.io/c/v-rising/p/deca/VampireCommandFramework/)
   into `BepInEx/plugins/`.
3. Drop `Uriel.dll` into `BepInEx/plugins/`.
4. Start the server once to generate the config at
   `BepInEx/config/kdpen.Uriel.cfg`, then edit and restart as needed.

> **Stop the server before replacing `Uriel.dll`** — a running server file-locks
> the DLL.

## Dependencies & compatibility

| Mod | Version | Role |
|---|---|---|
| [**BepInExPack V Rising**](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/) | 1.733.2 | Loader (hard dependency) |
| [**VampireCommandFramework**](https://thunderstore.io/c/v-rising/p/deca/VampireCommandFramework/) | 0.10.x | Chat-command framework (hard dependency) |

**Optional companion (not a dependency):** the client-side mod
[**BloodCraftHub**](https://thunderstore.io/c/v-rising/p/TheShadowRealm/BloodCraftHub/)
is designed to work alongside Uriel — players who install it on their own client
can drive Uriel's features from in-game UI panels and buttons instead of typing.
It is **not required**: like any server-side mod, every Uriel feature works fully
through the `.uriel` chat commands, with or without BloodCraftHub.

## Commands

All commands are chat commands prefixed with `.uriel`. Type `.uriel` for a quick
overview, or **`.uriel help`** for a clean, topic-by-topic menu (`objects` /
`storage` / `stairs` / `admin`). Most targeted commands also accept a trailing
**`nearest`** token to act on the closest object to you instead of where you're
aiming — handy when a menu or UI panel has your aim ray pointing elsewhere
(e.g. `.uriel share nearest`, `.uriel stairswap stone2 nearest`).

<details>
<summary><b>🏺 Object Spawning commands</b></summary>

**Players** (active when `ObjectSpawn.AdminOnly=false`; you must be inside your own castle plot):

| Command | What it does |
|---|---|
| `.uriel spawn <name\|guid> [rot 0-3] [flags…]` | Place an object you've unlocked at your aim point. Flags (any order, after the rotation): `breakable` (raid/decay), `smashable` (you can break it too), `respawn` (auto-returns until castle gone / despawned), `here` (place at YOUR location), `indestructible` (default) |
| `.uriel move [here]` / `.uriel rotate [0-3]` / `.uriel despawn` | Move (to your aim, or `here` = your location) / turn / remove the nearest object you spawned |
| `.uriel unlocks` | Your collected objects + collection % |
| `.uriel catalog [page]` | Browse the full world-object catalog |
| `.uriel findprefab <text>` | Search objects by name |
| `.uriel notify <on\|off>` | Toggle your object-unlock chat messages |

In **Discovery** mode you unlock objects by **destroying them in the world**; the non-destructible ones unlock on full collection, boss defeats, or admin grant.

<details>
<summary><i>Admin sub-commands</i></summary>

| Command | What it does |
|---|---|
| `.uriel grant\|revoke <player> <name\|guid>` | Unlock / remove an object for a player |
| `.uriel grantall <player> [all\|destructible\|indestructible]` | Bulk-grant the catalog (or a subset) |
| `.uriel block\|unblock <guid>` · `.uriel blocklist` | Forbid / allow a prefab |
| `.uriel spawnlist` · `.uriel purgeplot` | List / clear spawned objects on the plot |
| `.uriel forcedespawn [confirm]` | Force-remove the aimed object, ignoring records/ownership (recovers untracked objects); names it, then `confirm` within 30s |
| `.uriel forcepurgeplot` | Force-remove every Uriel-like object on the plot, including untracked ones (native build pieces are left) |
| `.uriel bossmap add\|remove\|list <vblood> <obj>` | Curate which objects a V-blood defeat unlocks |
| `.uriel api version\|catalog\|unlocked` | Machine API for BloodCraftHub |
</details>
</details>

<details>
<summary><b>🗄 Public Storage &amp; Prison commands</b></summary>

**Players** (aim at a container you own):

| Command | What it does |
|---|---|
| `.uriel share [modifiers…]` | Make the aimed container public. Modifiers **stack in any order**: `permission take\|give\|givetake`, `limithours <h>`, `limitwithdrawal <stacks>`, `cost <itemId> <amount>`. Example: `.uriel share permission take limithours 24 limitwithdrawal 1` |
| `.uriel unshare` | Make the aimed container private again |
| `.uriel info` | Show a container's sharing rules (works for anyone) |
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
| `.uriel unshareall` | Revert every public container to private and clear the registry |
| `.uriel sharedebug` | Dump the aimed container's live sharing state (diagnostics) |

Admins may also aim at **any** container and use `.uriel share […]` / `.uriel unshare`
to override its settings directly.
</details>
</details>

<details>
<summary><b>🪜 Stair Editing commands</b></summary>

**Players** (aim at a staircase you own):

| Command | What it does |
|---|---|
| `.uriel stairswap <style\|next>` | Restyle the aimed staircase **live**. Styles: `stone1`, `stone2`, `stone3`, `gloomrot`*, `projectk`*, `strongblade`* (`*` = requires that DLC). `next` cycles to the next style you own. |
| `.uriel removestairs` | Cleanly delete the aimed staircase, leaving connected floors/walls intact |
| `.uriel stairstyles` | Show the aimed stair's shape, current style, and which styles you can use |

<details>
<summary><i>Admin sub-commands</i></summary>

| Command | What it does |
|---|---|
| `.uriel stairpurge` | Destroy stray "ghost" stair entities within 5m (cleanup for older builds) |
</details>
</details>

## Configuration

`BepInEx/config/kdpen.Uriel.cfg` — every feature has its own `Enabled` switch.
Config changes take effect on server restart.

| Section | Key | Default | Effect |
|---|---|---|---|
| ObjectSpawn | Enabled | `true` | Master switch for object spawning |
| ObjectSpawn | AdminOnly | `true` | When true, only admins may spawn — set `false` to open the player collection path |
| ObjectSpawn | PlayerAccessMode | `Discovery` | `Discovery` (unlock by destroying) or `Full` (whole catalog) |
| ObjectSpawn | DiscoveryChancePercent | `25` | Chance (0–100) that destroying a world object unlocks it |
| ObjectSpawn | NonDestructibleUnlock | `Off` | How non-destructible objects unlock: `Off`/`Collection`/`FinalBoss`/`AllBosses` |
| ObjectSpawn | IncludeCastleBuildables | `false` | Include the normal build-menu pieces in the catalog (off = world objects only) |
| ObjectSpawn | PrefabCostItem / PrefabCostStack | `0` / `0` | Item + amount a player pays to build one object (0 = free) |
| ObjectSpawn | Indestructible | `true` | Spawned objects are immortal + decay-proof by default (per-spawn `breakable`/`smashable` flags override; turn off globally for PvP) |
| ObjectSpawn | RespawnEnabled | `true` | Master switch for the `respawn` flag (auto-return destroyed objects until the castle is gone) |
| ObjectSpawn | RespawnPollSeconds | `30` | How often (seconds, min 5) the respawn check runs |
| ObjectSpawn | *(more)* | — | CollectionEnabled, BossUnlocksEnabled, DiscoveryNotify, RefundOnRemove, PurgeOrphansOnBoot — see the generated `.cfg` |
| PublicStorage | Enabled | `true` | Master switch for chest sharing |
| PublicStorage | PrisonEnabled | `true` | Independently allow prison-cell sharing |
| PublicStorage | MaxTargetDistance | `5` | Aim distance for `.uriel share` / `unshare` targeting |
| StairSwap | Enabled | `true` | Allow live stair restyling + `.uriel removestairs` |
| StairSwap | MaxTargetDistance | `6` | Aim distance for the stair commands |
| StairSwap | RespawnGapFrames | `5` | Frames between destroy & respawn when restyling a stair |
| Diagnostics | VerboseLogging | `false` | Extra per-action log lines (useful when testing) |

## Roadmap

Uriel is an umbrella mod — the plan is to keep adding small, independently
toggleable server-side sub-mods. **Tentative and subject to change**; full list
(incl. large-scope ideas) in the
**[roadmap](https://github.com/KDavidP1987/Uriel-Lord-of-Hosts/blob/main/docs/ROADMAP.md)**.

**Planned next:**
- **Coffin sharing** — share coffin use, with an optional cost-to-use modifier.
- **Conditional door access** — share a door behind payment and/or allowed time
  windows (select hours).
- **Bulk storage sharing** — open *all* heart-linked storage at a location in one
  action, auto-excluding the pay chest and any chests you've marked private.
- **BloodCraftHub integration** — a client-side companion UI (share panels,
  prisoner-take button, stair-style picker).

**Bigger ideas being explored:** plot expansion (separate-instance phase gates or
map copy/paste), a PvP-arena V Blood boss mode with prize tables, paid area-gating
for mazes, scheduled/paid door & teleport controls, more castle decor assets,
triggered NPC spawns, and basement levels.

Have a feature you'd like to see in the host? Bring it to the
[Discord](https://discord.gg/usC9QgBrXK).

## Feedback & community

Built and tested on the V Rising server **The Shadow Realm** (Brutal PvE),
maintained by Chaos. As a pre-1.0 mod, **bug reports and feedback are hugely
valued** — and the people who help test now shape what 1.0 becomes.

- **The Shadow Realm Discord (primary bug/feedback channel):** https://discord.gg/usC9QgBrXK
- **Issues / source:** https://github.com/KDavidP1987/Uriel-Lord-of-Hosts
- Support development: [PayPal](https://www.paypal.com/paypalme/KrisPenland) · [SkillEra.IO](https://SkillEra.IO)

## Acknowledgements

Uriel stands on the shoulders of the V Rising modding community. Big thanks to:

- **[VampireCommandFramework](https://thunderstore.io/c/v-rising/p/deca/VampireCommandFramework/) — by deca**, the chat-command framework Uriel's commands are built on (a hard dependency).
- **[BepInEx](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/)**, the loader that makes V Rising modding possible.
- **[KindredCommands & KindredSchematics](https://thunderstore.io/c/v-rising/p/odjit/) — by odjit**, whose open-source castle-building/tile techniques were a key reference for Uriel's live stair rebuild.
- **[Bloodcraft](https://thunderstore.io/c/v-rising/p/zfolmt/Bloodcraft/) — by zfolmt**, a reference for server-side ECS/Harmony patterns.

## License

Licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0)** —
copyright © 2026 Kristopher Penland. Uriel adapts techniques from odjit's
AGPL-licensed KindredCommands & KindredSchematics and is released under the same
copyleft license; full corresponding source is on
[GitHub](https://github.com/KDavidP1987/Uriel-Lord-of-Hosts).
