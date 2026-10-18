# Uriel, Lord of Hosts

![Uriel, Lord of Hosts](https://raw.githubusercontent.com/KDavidP1987/Uriel-Lord-of-Hosts/main/docs/img/uriel-cover.jpg)

A **server-side** umbrella mod for V Rising dedicated servers — a *host* of
independent, admin-toggleable quality-of-life enhancements and structural fixes.

---

## ⚠ Heads-up before you install

**Pre-1.0 — in active development. Test it first; don't roll it out broadly on a
busy live server until you've vetted it yourself.** Uriel is pre-release and
evolving quickly. The features below are implemented and tested **in local and
dedicated development/test-server environments — not yet on a live production
server**. This is a **server-side mod still under development**: by running it you
are helping test it, and **you take that risk on yourself.** Please **back up your
server save** before adding any new mod, and ideally **trial Uriel on a test server
(or a quiet plot) first**. Commands, config keys, and behavior may still change
before 1.0.

**Every feature is independently toggleable.** If any one component feels
unstable, you can **turn just that part off** in the config (each feature has its
own `Enabled` switch) and keep using the rest — you never have to disable Uriel
wholesale. See the per-section notes below and the **Configuration** table.
The switches bind admins too: a feature that's off is off for everyone.

**Want to play it safe?** Set `ObjectSpawn.Enabled=false` (no spawned objects) and
leave `CastleEvents.Enabled=false` (the default: no NPC units). If something ever goes
wrong, **`.uriel killswitch`** (then `.uriel killswitch confirm`) removes everything Uriel
put in the world, and `General.Enabled=false` switches the whole mod off. See
**Emergency: restore vanilla** below.

**Found a bug or have an idea?** The fastest way to get a fix into the next
release is the **[The Shadow Realm Discord](https://discord.gg/usC9QgBrXK)** —
that's the primary bug-report and feedback channel. Written-up GitHub issues are
welcome too.

---

**Repo:** https://github.com/KDavidP1987/Uriel-Lord-of-Hosts
**Status:** pre-1.0 public testing · **server-side only** · several feature groups
live today, more sub-mods planned under the Uriel umbrella.

| Feature | Stage | Default |
|---|---|---|
| 🏺 Object Spawning | Beta | On (admin-only until you open it to players) |
| 🗄 Public Storage and prison cells | Beta | On |
| 🪜 Stair Editing | Beta | On |
| 🏰 Castle Events | **Alpha** | **Off** |

**Beta:** in testers' hands, core paths validated live. **Alpha:** built and tested on a
development server only; expect rough edges, and commands may change.

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
If an object sits floating or half-buried, **nudge its height** with `.uriel raise`
/ `.uriel lower` — or, as an admin, set a permanent per-object correction with
`.uriel objcfg <object> yoffset <metres>` so it always sits flush.
Everything you place persists across restarts and is **cleaned up automatically —
during play, within a couple of minutes — if its castle is ever abandoned or
destroyed** (and you can't keep building on a plot you've given up).

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

**How it's managed:** spawned world objects are placed and moved with the `.uriel`
commands above, not the vanilla build menu (an engine limitation — the game won't let
arbitrary world objects be selected in build mode). The optional **Raphael** companion
mod *(formerly BloodCraftHub)* can present a point-and-click palette UI.

**Admins can put rules on player spawning.** Set a **per-object or global limit** on how
many of an object fit on one plot, an **item cost** to build it, and whether players may
make it **indestructible** or **auto-respawning** — `.uriel objcfg <object> max 3`,
`.uriel objcfg <object> cost 100 <itemId>`, `.uriel objcfgglobal …` for the defaults,
`.uriel objcfglist` to review. Admins themselves are never restricted.

**🧪 Pre-1.0 guidance & quick tips (Object Spawning)**

- **Not every spawnable object has been individually tested.** The game ships thousands
  of prefabs; some may misbehave when placed as décor (spawn invisible, or not function).
  This is a big reason Uriel is **pre-1.0**, and exactly why **we're asking for feedback** —
  every broken object you report goes onto the block list for the next release.
- **Hazardous/problematic prefabs are blocked from spawning.** The game's object list
  contains many entries that aren't real placeable décor (world-gen controllers, NPC-spawner
  "points of interest", invisible markers, baked "source" copies). Known-bad ones are filtered
  out of the catalog so players can't pick them. **This filter is still being refined** — there
  may be problematic objects not yet found.
- **If something spawns but you can't see it, it's INVISIBLE, not absent.** The object is
  almost certainly there on the server but not rendering correctly (it's one of the
  not-yet-caught bad ones). **`.uriel despawn` it** to be safe — don't leave it sitting there —
  and please report it so it can be blocked.
- **Don't overlap objects on purpose.** Stacking objects in the same spot can cause problems;
  Uriel blocks overlapping placements by default (`ObjectSpawn.PreventOverlap`). Offset things
  slightly instead.
- **Admins can block any object** they don't want available — even one that isn't hard-coded
  blocked — with `.uriel block <guid>` (or by configuring their server). Blocked prefabs vanish
  from the catalog and can't be spawned.
- **Prefer admin-only while you vet it:** the default `ObjectSpawn.AdminOnly=true` keeps spawning
  to admins; open it to players once you're comfortable. You can also disable this whole feature
  (`ObjectSpawn.Enabled=false`) and keep the rest of Uriel.

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

**Heads-up for players accessing a share:** when a chest or cell is first made public,
a player trying to use it may need to **log out and back in once** before their client
recognizes it as accessible. After that initial relog, it behaves like any other container
you can open.

**🧪 Pre-1.0 guidance (Public Storage):** sharing is always **per-container, owner opt-in** —
nothing is shared unless someone shares it, so the blast radius is small. Trial it on a few
containers first. Chest sharing and prison-cell sharing have **separate switches**
(`PublicStorage.Enabled` / `PublicStorage.PrisonEnabled`), so you can enable one and not the
other. Found an issue? Report it on Discord and, if needed, flip the switch off — your
containers simply go back to private.

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

**🧪 Pre-1.0 guidance (Stair Editing):** restyling rebuilds the staircase on the spot
(destroy + respawn in the new style); position, rotation, and ownership are preserved. As
with any pre-1.0 feature, try it on a spare staircase first. If a stray "ghost" stair is ever
left behind, an admin can clean it with `.uriel stairpurge`. You can disable the whole feature
with `StairSwap.Enabled=false`.

### 🏰 Castle Events *(alpha, off by default)*

Turn a castle into an event space. Walk your castle and drop **spawn markers** for mobs and
bosses, bind **doors** that stay shut and locked until the units guarding them are defeated,
then start a run. Players fight their way through room by room: a gated door doesn't open
until its group is beaten.

**How it fits together**

- **Layout** (per castle): markers grouped by name. Each group sits on a floor and may hold
  one door. `.uriel event marker <group> [mob|boss]` places a marker where you stand;
  `.uriel event door <group>` binds the door nearest you.
- **Squad**: what a group spawns. Random picks: `distinct` takes N different units ("1 of
  these 3 bosses", "2 of these 10"), `any` draws an assortment. A squad can hold several
  combinations; one is chosen per run.
- **Floor template**: which squad each group on a floor gets.
- **Template**: which floor template each floor uses. Give a floor several and one is
  chosen per run, so a floor can have a different theme each time.

**Example** (floor 1: five militia guard a door, then a boss, one of three):

```
# the layout: stand in each spot
.uriel event marker g1 mob
.uriel event door g1
.uriel event marker g2 boss
# what spawns
.uriel squad create militia5
.uriel squad add militia5 mob CHAR_Militia_Guard,CHAR_Militia_Light 5 any
.uriel squad create boss1of3
.uriel squad add boss1of3 boss Foreman_VBlood,StoneBreaker_VBlood,Stalker_VBlood 1 distinct
.uriel floor create f1
.uriel floor assign f1 g1 militia5
.uriel floor assign f1 g2 boss1of3
.uriel template create castle1
.uriel template floor castle1 1 f1
# run it, scaled to the strongest player present
.uriel event start castle1 hard,auto+2
```

**Run options** (comma-separated, after the template name):

| Option | Effect |
|---|---|
| `easy` · `normal` · `hard` · `brutal` | Presets for health, damage and speed |
| `level=40` · `level+5` · `auto` · `auto+2` | Set the level; `auto` = the highest player level on the plot |
| `hp*2` · `dmg*1.5` · `speed*1.2` · `aspd*1.3` | Multiply health, damage, move speed, attack speed (0.1 to 10) |
| `limit=15m` | Time limit (seconds, minutes or hours, up to 6 h); the run fails when it runs out |
| `faction=keep` | Units keep their own factions (by default a run fights as one side) |

A template can carry default options; the ones you type at the start add to them.

**During a run** the clock starts once every foe is in place. Clearing a group opens its
door; defeating the last boss and finishing the event are timed and announced to everyone
on the plot. `.uriel event status` shows progress, `.uriel event stop` removes the units
and puts every door back, and `.uriel event history` shows recent runs and best times.

**Containment:** event units never leave the castle plot, and by default stay on the floor
they spawned on (`CastleEvents.DefaultContain`). A marker can keep its units to a room, a
floor, or a leash radius instead.

**🧪 Alpha guidance (Castle Events)**

- **Off by default.** Turn it on with `CastleEvents.Enabled=true`. Admins can run events on
  any plot. To let castle owners run events on their own plot, also set
  `CastleEvents.PlayerEvents=true`; marker and run costs are configurable and free by default.
- **Tested by the developer on a development server, single player.** Several players in one
  event, and players running events on their own castles, have not been tested yet. Please
  report what you find.
- **Spawned units are never saved.** A server restart removes them (and ends any run, putting
  doors back). Units drop no loot.
- **Keep chat commands short.** The game cuts chat lines at about 120 characters. Unit names
  can be shortened (`Foreman_VBlood`), and long unit lists can be added in parts with
  `.uriel squad extend`.
- **Kill switch:** `.uriel unit purge` (then `.uriel unit purge confirm`) removes every Uriel
  unit on the server.

## Emergency: restore vanilla

Every admin cleanup below works even with the feature, or the whole mod, switched off.

| Command | What it undoes |
|---|---|
| `.uriel killswitch [confirm]` | **Everything at once:** stops every castle event (doors put back as they were), removes every Uriel unit and every Uriel-spawned object, and makes every shared chest and prison cell private |
| `.uriel purgeall [confirm]` | Every Uriel-spawned object on the server |
| `.uriel unit purge [confirm]` | Every Uriel NPC unit on the server |
| `.uriel event stopall` | Every running castle event, doors put back |
| `.uriel unshareall` | Every shared chest and prison cell back to private |
| `.uriel features` | Shows which features are on |

Can't get into the server? Set `KillSwitch.RestoreVanillaOnBoot=true` in the config and
restart: Uriel does the same reset at start, then switches the setting back off. To keep
Uriel off afterwards, set `General.Enabled=false`.

Only Uriel's own objects and units are ever removed; native objects are never touched.
Saved settings (unlocks, event layouts, squads) are kept. Swapped stairs stay as they are:
a swap leaves an ordinary game staircase in another style.

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
[**Raphael**](https://thunderstore.io/c/v-rising/p/TheShadowRealm/Raphael/) *(formerly
BloodCraftHub)* is designed to work alongside Uriel — players who install it on their own
client can drive Uriel's features from in-game UI panels and buttons instead of typing.
It is **not required**: like any server-side mod, every Uriel feature works fully
through the `.uriel` chat commands, with or without Raphael.

## Commands

All commands are chat commands prefixed with `.uriel`. Type `.uriel` for a quick
overview, or **`.uriel help`** for a clean, topic-by-topic menu (`objects` /
`storage` / `stairs` / `events` / `admin`). Most targeted commands also accept a trailing
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
| `.uriel raise [m]` / `.uriel lower [m]` | Nudge the nearest object you spawned up / down so it sits flush with the ground (default 0.25m) |
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
| `.uriel spawnlist` · `.uriel purgeplot` | List / clear spawned objects on the plot (live spawns + records; native objects never touched) |
| `.uriel forcedespawn [confirm]` | Force-remove the aimed object, ignoring records/ownership (recovers untracked objects); names it, then `confirm` within 30s |
| `.uriel forcepurgeplot` | Synonym for `purgeplot` (live spawns + records; native objects never touched). The old chain sweep was removed — it could delete native resources |
| `.uriel purgeorphans` | **Server-wide cleanup** — scan the whole map and remove orphaned Uriel objects (castle gone, or no living heart governing them). Only Uriel's objects are touched; a backup for the automatic boot-time cleanup |
| `.uriel bossmap add\|remove\|list <vblood> <obj>` | Curate which objects a V-blood defeat unlocks |
| `.uriel objcfg <name\|guid> <field> [v1] [v2]` | Per-object rules: `max <n>` (per plot), `cost <amount> <itemGuid>`, `indestructible <true\|false>`, `respawn <true\|false>` (these gate players only) · `yoffset <metres>` (permanent flush-to-ground height, applies to everyone) · `clear`, `show` |
| `.uriel objcfgglobal <field> [v1] [v2]` · `.uriel objcfglist` | Global default condition (per-object overrides it) · list all conditions |
| `.uriel api version\|catalog\|unlocked` | Machine API for Raphael |
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

<details>
<summary><b>🏰 Castle Events commands (alpha)</b></summary>

Admins on any plot; castle owners on their own plot when `CastleEvents.PlayerEvents=true`.

**Layout** (stand where you want things):

| Command | What it does |
|---|---|
| `.uriel event marker <group> [mob\|boss] [unit\|-] [count] [level] [contain\|-]` | Place a spawn marker where you stand. Without a unit, the group's squad decides what spawns. `contain`: `room`, `storey`, `plot`, or a leash in metres |
| `.uriel event door <group>` · `undoor <group>` | Bind the castle door nearest you (within 6 m) to a group, or unbind it |
| `.uriel event squad <group> <squad\|->` | Give a group its own squad, used when the template doesn't cover it |
| `.uriel event list` · `remove <#id\|group>` | Show the layout · remove a marker or a whole group |

**Runs:**

| Command | What it does |
|---|---|
| `.uriel event start [template\|-] [options]` | Start a run (a template, floor template or squad name; `-` = each group's own squad) |
| `.uriel event status` · `stop` · `history` | Progress · end the run and restore doors · recent runs and best times |
| `.uriel event stopall` | Admin: stop every event on the server, doors put back |

**Library** (an admin's entries are server-wide, a player's are their own):

| Command | What it does |
|---|---|
| `.uriel squad create <name>` | New squad |
| `.uriel squad add <name> <mob\|boss> <unit[,unit…]> [count] [any\|distinct] [level] [set]` | Add an entry. `set` 2, 3… starts another combination |
| `.uriel squad extend <name> <set> <entry> <unit[,unit…]>` | Add more units to an entry's list |
| `.uriel squad remove <name> <set> [entry]` · `weight <name> <set> <1-100>` | Remove an entry or set · how often a set is chosen |
| `.uriel floor create <name>` · `assign <name> <group\|*> <squad[,squad…]\|->` | Floor template: a squad per group (several = one chosen per run) |
| `.uriel template create <name>` · `floor <name> <floor\|*> <floor template[,…]\|->` | Template: a floor template per floor (several = one chosen per run) |
| `.uriel template options <name> <options\|->` | Default run options |
| `.uriel squad\|floor\|template list` · `show <name>` · `delete <name>` | Browse and manage |

<details>
<summary><i>Admin unit tools</i></summary>

| Command | What it does |
|---|---|
| `.uriel unit spawn <name\|guid> [count] [level] [here\|aim] [contain]` | Spawn units into the castle plot you're in |
| `.uriel unit list` | Uriel units per plot |
| `.uriel unit kill [radius] [all\|boss\|mob]` | Kill nearby Uriel units (counts as a defeat; groups clear, doors open) |
| `.uriel unit despawn [radius]` · `clear` | Remove the nearest unit · every unit on this plot |
| `.uriel unit purge [confirm]` | Kill switch: every Uriel unit on the server |
| `.uriel unit status [radius]` · `rooms` · `door <open\|close\|lock\|unlock\|show>` | Diagnostics (details in the server log) |
</details>
</details>

## Configuration

`BepInEx/config/kdpen.Uriel.cfg` — every feature has its own `Enabled` switch.
Config changes take effect on server restart.

| Section | Key | Default | Effect |
|---|---|---|---|
| General | Enabled | `true` | Master switch: `false` turns every Uriel feature off for everyone, admins included. The cleanup commands still work |
| KillSwitch | RestoreVanillaOnBoot | `false` | One-shot emergency reset: the next start removes every Uriel object and unit, makes every shared chest/cell private and puts event doors back, then sets itself back to `false` |
| ObjectSpawn | Enabled | `true` | Master switch for object spawning |
| ObjectSpawn | AdminOnly | `true` | When true, only admins may spawn — set `false` to open the player collection path |
| ObjectSpawn | PlayerAccessMode | `Discovery` | `Discovery` (unlock by destroying) or `Full` (whole catalog) |
| ObjectSpawn | DiscoveryChancePercent | `25` | Chance (0–100) that destroying a world object unlocks it |
| ObjectSpawn | NonDestructibleUnlock | `Off` | How non-destructible objects unlock: `Off`/`Collection`/`FinalBoss`/`AllBosses` |
| ObjectSpawn | IncludeCastleBuildables | `false` | Include the normal build-menu pieces in the catalog (off = world objects only) |
| ObjectSpawn | PrefabCostItem / PrefabCostStack | `0` / `0` | Item + amount a player pays to build one object (0 = free) |
| ObjectSpawn | Indestructible | `true` | Spawned objects are immortal + decay-proof by default (per-spawn `breakable`/`smashable` flags override; turn off globally for PvP) |
| ObjectSpawn | PreventOverlap | `true` | Refuse to spawn/move an object onto a wall, station, or another spawned object (only floors may sit under décor) — prevents destabilizing pile-ups. Set `false` to allow free stacking at your own risk |
| ObjectSpawn | OverlapMinDistance | `0.5` | Minimum spacing (m) between objects when PreventOverlap is on. Lower it to place décor closer together; `0` = only block the exact same tile. Doesn't affect placing flush against walls |
| ObjectSpawn | RespawnEnabled | `true` | Master switch for the `respawn` flag (auto-return destroyed objects until the castle is gone) |
| ObjectSpawn | RespawnPollSeconds | `30` | How often (seconds, min 5) the respawn check runs |
| ObjectSpawn | AutoPurgeOrphans | `true` | Clean up objects on an abandoned/destroyed castle **during play** (within ~2 min), not just at the next restart. Only Uriel's own objects are touched |
| ObjectSpawn | OrphanPollSeconds | `120` | How often (seconds, min 30) the mid-session orphan sweep runs |
| ObjectSpawn | *(more)* | — | CollectionEnabled, BossUnlocksEnabled, DiscoveryNotify, RefundOnRemove, PurgeOrphansOnBoot — see the generated `.cfg`. Per-object/global player limits live in `object_conditions.json` (`.uriel objcfg`) |
| PublicStorage | Enabled | `true` | Master switch for chest sharing |
| PublicStorage | PrisonEnabled | `true` | Independently allow prison-cell sharing |
| PublicStorage | CostsEnabled | `true` | Allow paid shares (`cost` modifier, `.uriel paychest`). Off: no new prices, no payments moved, and an already-priced chest stays closed to others (never made free) until its owner removes the price |
| PublicStorage | MaxTargetDistance | `5` | Aim distance for `.uriel share` / `unshare` targeting |
| StairSwap | Enabled | `true` | Allow live stair restyling + `.uriel removestairs` |
| StairSwap | MaxTargetDistance | `6` | Aim distance for the stair commands |
| StairSwap | RespawnGapFrames | `5` | Frames between destroy & respawn when restyling a stair |
| CastleEvents | Enabled | `false` | Master switch for Castle Events and the unit commands (alpha) |
| CastleEvents | DoorGates | `true` | Let events shut and lock castle doors. Off: doors can't be bound and runs never touch a door |
| CastleEvents | PlayerEvents | `false` | Let castle owners run events on their own plot (admins always can, anywhere) |
| CastleEvents | DefaultContain | `storey` | Where a marker's units stay when it doesn't say: `plot`, `room`, `storey`, a leash in metres, or a mix like `room,15` |
| CastleEvents | MaxMarkersPerPlot | `40` | Most spawn markers a player's castle may have |
| CastleEvents | MarkerCostItem / MarkerCostAmount / BossMarkerCostAmount | `0` | Item a player pays per mob or boss marker (refunded on removal; 0 = free) |
| CastleEvents | RunCostItem / RunCostAmount | `0` | Item a player pays to start a run (0 = free) |
| CastleEvents | MaxUnitsPerPlot / MaxTrackedUnits | `60` / `300` | Most Uriel units alive on one plot / on the server |
| CastleEvents | *(more)* | — | MaxUnitsPerCommand, MaxSpawnsPerTick, MaxDespawnsPerTick, UnitLifetimeSeconds, ClearDrops, ContainCheckSeconds — see the generated `.cfg`. Layouts, the library and run history live in `BepInEx/config/Uriel/castle_events.json` |
| Diagnostics | VerboseLogging | `false` | Extra per-action log lines (useful when testing) |

## Roadmap to 1.0

Full detail in the
**[roadmap](https://github.com/KDavidP1987/Uriel-Lord-of-Hosts/blob/main/docs/ROADMAP.md)**.
Tentative and driven by tester feedback.

1. **Harden the beta features.** Live-server validation of Object Spawning, Public Storage and
   Stair Editing; less server cost on repeated move/rotate/delete.
2. **Castle Events to beta.** Multiplayer and player-castle testing, the remaining indoor
   checks (castle servants and defences, sunlight), and Raphael screens for building and
   running events.
3. **Castle Events complete.** A toll door that charges entry and starts a run, entry triggers
   and capacity limits, reward tables, event decorations and barriers, and maze stairs that
   change the path each run.
4. **Castle Rentals.** Rent a coffin or a room for a set time, with access to chosen storage.
5. **1.0:** every feature validated on a live server, commands and config frozen, Raphael
   covering the main screens.

**After 1.0 (exploratory):** conditional door access (payment or set hours), bulk storage
sharing, teleporters, plot expansion, expanded castle decor.

Have a feature you'd like to see in the host? Bring it to the
[Discord](https://discord.gg/usC9QgBrXK).

## Feedback & community

Developed and tested **locally and on dedicated development/test servers** — it
has **not yet been run on a live production server**, which is exactly why it's
pre-1.0 (see the heads-up at the top). It comes out of the V Rising community
**The Shadow Realm** (Brutal PvE), maintained by Chaos, whose Discord is the home
for Uriel testing and feedback. As a pre-1.0 mod, **bug reports and feedback are
hugely valued** — and the people who help test now shape what 1.0 becomes.

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
