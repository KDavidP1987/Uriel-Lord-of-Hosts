# Uriel → Raphael: Castle Events and unit spawning

**For:** the Raphael workspace (`C:\dev\projects\Games\V Rising\Raphael Lord of Wisdom\Raphael\`, repo
`KDavidP1987/Raphael-Lord-of-Wisdom`), its Uriel client in `Raphael/Services/Uriel/`.
**From:** Uriel v0.21.0 (2026-10-15). Castle Events is **alpha** and off by default.
**Companion doc:** [`BCH_INTEGRATION_HANDOFF.md`](BCH_INTEGRATION_HANDOFF.md) (the living contract for
everything else Uriel exposes; its §6 wire grammar applies here too).

This document is everything Raphael needs to give Castle Events a UI: the concepts, every command with its
arguments and reply text, the screens worth building, what Raphael can parse today, and the wire API Uriel
will add next. The workspaces never cross-edit; integration flows through chat commands only. If this doc
and `Uriel/Uriel/Commands/EventCommands.cs` / `UnitCommands.cs` disagree, the code wins and this doc gets
corrected.

---

## 1. What Castle Events does

A castle owner (or an admin) turns a castle into an event space. They walk the castle and drop **spawn
markers**, bind **doors** to groups of markers, and start a **run**. The run spawns mobs and bosses at the
markers, shuts and locks each group's door, and opens it when that group is defeated. A clock times the run;
the last boss and the completion are announced; a time limit can fail the run. Stopping a run (or a server
restart) removes the units and puts every door back as it was.

What spawns is decided by a layered library:

```
Template (castle-wide)        floor 1 → f1          floor 2 → f2 or f2b (one rolled per run)
  └─ Floor template           group g1 → militia5   group g2 → boss1of3
       └─ Squad               set 1: [5 mobs, any of Militia_Guard / Militia_Light]
                              set 2: [3 mobs ...] + [1 boss ...]   (one set rolled per run, by weight)
```

## 2. Concepts and rules

| Concept | Rules |
|---|---|
| **Plot** | Everything happens on the castle plot the caller stands in. Admins: any plot. Players: only their own castle (heart team match, so clan members count), and only when `CastleEvents.PlayerEvents=true`. |
| **Layout** | One per plot. A list of **groups**. |
| **Group** | A name (1-24 of `A-Z a-z 0-9 - _`), a **floor**, its **markers**, at most one **door**, and optionally its own squad. Created by its first marker, on the floor the caller stands on. |
| **Floor** | Counted from the castle's own floor heights: ground = 1, the storey above = 2, and so on. Raphael never sends a floor for a marker; it is where the player stands. |
| **Marker** | A spot (the caller's feet), a slot `mob` or `boss`, optionally its own unit, count and level, and a containment. Numbered `#id` per plot. Players are capped at `MaxMarkersPerPlot` (40); markers may cost items (refunded on removal). |
| **Door** | The castle door nearest the caller within 6 m. One door per group, one group per door. |
| **Containment** | Where a marker's units must stay: `plot` (always applies), `room` (the game's room under the marker), `storey`, a leash radius in metres (3-60), or a comma mix (`room,15`). Default: `CastleEvents.DefaultContain` (`storey`). |
| **Squad** | Sets of entries. Entry: role `mob`/`boss`, a unit list, a count (1-50), a pick (`any` = random with repeats, `distinct` = that many different units), a level (0 = the unit's own). One set is rolled per run by weight (1-100). |
| **Floor template** | Group name (or `*` for every group on the floor) → one or more squads; one rolled per run. |
| **Template** | Floor (`1`, `2`, … or `*`) → one or more floor templates; one rolled per floor per run. Optional default run options. |
| **Library names** | Squads, floor templates and templates share one namespace. An admin's entries are server-wide (everyone can use them); a player's are their own. Players see the server's plus their own. |
| **Run** | One per plot. Started by name: a template, else a floor template (used on every floor), else a squad (every group gets it); `-` or nothing = each group's own squad. A group's mobs go to its mob markers, its bosses to its boss markers. Everything must resolve and fit the unit caps before anything spawns or is charged. |
| **Run options** | One comma-separated word: see §4. A template's defaults first, then the start's own. |
| **Units** | Never saved; a restart removes them. No loot (`ClearDrops`). One faction per run by default. |

**Chat line limit:** the game cuts a chat line at about **120 characters**, and the cut-off tail silently
falls back to defaults. Raphael sends commands through chat, so every command it builds must stay under that.
Use short unit names (any unique ending works: `Foreman_VBlood` = `CHAR_Bandit_Foreman_VBlood`), or GUIDs,
and add long unit lists in parts with `.uriel squad extend`.

## 3. Command reference

Every command needs `CastleEvents.Enabled=true` (else: `Castle events are disabled by the server admin
(CastleEvents.Enabled).`). Replies are human text today, one reply per line.

### Layout (`.uriel event …`, caller stands on the plot)

| Command | Reply starts with |
|---|---|
| `marker <group> [mob\|boss] [unit\|-] [count] [level] [contain\|-]` | `Marker #<id> (<slot>) in group '<g>' (floor <f>): <unit or "units from the group's squad">[ x<count>]. The group has <n> marker(s)[ and a door\|, no door].` (+ ` Paid <n>x <item>.`) |
| `door <group>` | `Group '<g>' holds the door <prefab> (<d> m away): shut while its units live, open once they are all defeated.` |
| `undoor <group>` | `Group '<g>' no longer holds a door.` |
| `squad <group> <squad\|->` | `Group '<g>' spawns squad '<s>' unless the run's template says otherwise.` / `Group '<g>' has no squad of its own.` |
| `remove <#id\|group>` | `Removed marker #<id>.` / `Removed group '<g>' (<n> marker(s)).` (+ ` Refunded <n> item(s).`) |
| `list` | `Event layout of plot <t>: <G> group(s), <M> marker(s)[; RUNNING].` then per group: `Floor <f> · <group> · <no door\|door at <x>,<z> (open\|shut)> · squad <s\|-> · #<id> <slot> <unit\|squad>[ x<n>], …` |

Layout edits are refused while a run is going: `An event is running on this plot. Stop it first ('.uriel event stop').`

### Runs (`.uriel event …`)

| Command | Reply starts with |
|---|---|
| `start [template\|-] [options]` | `Event started from '<name>': <U> unit(s), <B> boss(es), <G> group(s), <D> door(s) held[, scaled <scale>][, limit <Xm Ys>].` |
| `stop` | `Event stopped: removing <U> unit(s), <D> door(s) put back as they were.` |
| `status` | `Event running <Xm Ys\|(spawning)>, '<name>'[ [<options>]]: <c>/<G> group(s) cleared[, bosses <l>/<B> left\|, bosses down at <Xm Ys>][, <Xm Ys> left].` then per group: `<group>[ (<squad>)]: <n>/<m> left[, door held]` or `<group>[ (<squad>)]: cleared[, door open]` |
| `history` | `Last runs on plot <t> (<n> recorded):` then up to 5 × `MM-dd HH:mm · <name>[ [<options>]] · <result> <Xm Ys>[ · bosses <Xm Ys>\| · bosses not all down] · <U> units`, then per template `Best '<name>': complete <Xm Ys>[, bosses <Xm Ys>] (<n> run(s)).` Results: `complete`, `stopped`, `timed-out`. |

No run: `No event is running on this plot.` No history: `No events have run on this plot yet.`

### Library

| Command | Reply starts with |
|---|---|
| `.uriel squad create <name>` | `The squad '<name>' is created (server-wide, everyone can use it\|yours). Fill it: …` |
| `.uriel squad add <name> <mob\|boss> <unit[,unit…]> [count] [any\|distinct] [level] [set]` | `Squad '<name>', set <s>, entry <e>: <description>.` (`set` = an existing set, or the next number to start a new combination) |
| `.uriel squad extend <name> <set> <entry> <unit[,unit…]>` | `Squad '<name>', set <s>, entry <e>: <description>.` |
| `.uriel squad remove <name> <set> [entry]` | `Squad '<name>': set <s>[ entry <e>] removed.` (entry 0 or omitted = the whole set) |
| `.uriel squad weight <name> <set> <1-100>` | `Squad '<name>': set <s> weight <w> (of <total>).` |
| `.uriel squad show <name>` | `Squad '<name>' (server\|player's)[: one of <n> sets per run]:` then `set <s>[ (<w>/<total>)]: [1] <description> + [2] <description>` |
| `.uriel floor create <name>` | `The floor template '<name>' is created (…). Give its groups squads: …` |
| `.uriel floor assign <name> <group\|*> <squad[,squad…]\|->` | `Floor template '<name>': group <g> gets <a>[ or <b>][ (one rolled per run)].` / `… group <g> unassigned.` |
| `.uriel floor show <name>` | `Floor template '<name>' (server\|player's):` then `group <g> · <a> or <b>` |
| `.uriel template create <name>` | `The template '<name>' is created (…). Give its floors floor templates: …` |
| `.uriel template floor <name> <floor\|*> <floor template[,…]\|->` | `Template '<name>': floor <f> uses <a>[ or <b>][ (one rolled per run)].` / `… floor <f> cleared.` |
| `.uriel template options <name> <options\|->` | `Template '<name>': runs default to <options>.` / `Template '<name>': no default options.` |
| `.uriel template show <name>` | `Template '<name>' (server\|player's)[, options <o>]:` then `floor <f> · <a> or <b>` |
| `.uriel squad\|floor\|template list` | `Squads: a, b (player's), …` / `Floor templates: …` / `Templates: …` (or `No squads yet. …`) |
| `.uriel squad\|floor\|template delete <name>` | `The <kind> '<name>' is deleted.` |

An entry `<description>` reads `5 mobs of CHAR_Militia_Guard/CHAR_Militia_Light`, `1 boss different of
A/B/C`, or `10 mobs CHAR_Undead_SkeletonSoldier_Base[ L40]`.

### Admin unit tools (`.uriel unit …`, admin-only)

| Command | Reply starts with |
|---|---|
| `spawn <name\|guid> [count] [level] [here\|aim] [plot\|room\|storey\|<m>]` | `Queued <n> x <prefab>, contained to: <scope>.` |
| `list` | `Uriel units: <n> alive, <p> pending, <r> being removed.` + one line per plot / `No Uriel units.` |
| `kill [radius] [all\|boss\|mob]` | `Killed <n> Uriel unit(s) within <r> m.` (a real death: groups clear and doors open) |
| `despawn [radius]` | `Removing <prefab> (<d> m).` / `No Uriel unit within <r> m.` |
| `clear` | `Removing <n> unit(s) on this plot.` |
| `purge [confirm]` | arm: `This removes every Uriel unit on the server (…). Run '.uriel unit purge confirm' within 30 s.` → `Purging <n> unit(s); cancelled <m> pending spawn(s).` |
| `status [radius]` · `rooms` · `probe` · `probemap` · `door <open\|close\|lock\|unlock\|show>` | diagnostics; details go to the server log |

### Common refusals

| Situation | Reply |
|---|---|
| Player, `PlayerEvents` off | `Castle events are admin-only on this server (CastleEvents.PlayerEvents).` |
| Not on a plot | `Stand inside a castle plot to set up its event.` |
| Someone else's castle | `You can only set up events in your own castle.` |
| Abandoned plot | `That castle plot is abandoned or decaying.` |
| Bad name / taken | `A <kind> name is 1-24 letters, digits, '-' or '_'.` / `The name '<n>' is already a <kind>.` |
| Unit not found / ambiguous | `No unit prefab matches '<x>'.` / `'<x>' matches <n> units: a, b, c, …` |
| No door near | `No castle door within 6 m. Stand next to the door this group should hold shut.` |
| A group has nothing to spawn | `No units for group(s) '<g>' (floor <f>): give the group a squad …` |
| Too many units | `This event needs <n> units; only <m> fit under the unit caps (…).` |
| Marker cap / cost | `This castle has the most markers allowed (<n>/<max>). Remove one first.` / `A <slot> marker costs <n>x <item>; you don't have enough in your inventory.` |

## 4. Run options

One word, comma-separated, no spaces: `hard,auto+2,limit=20m`.

| Token | Effect |
|---|---|
| `easy` / `normal` / `hard` / `brutal` | easy: hp and damage ×0.75 · hard: hp ×1.5, damage ×1.25 · brutal: hp ×2.5, damage ×1.5, speed ×1.1, attack speed ×1.15 |
| `level=N` · `level+N` · `level-N` | Set the level, or shift each unit's own level |
| `auto` · `auto+N` | The highest player level on the plot at start (+N) |
| `hp*X` · `dmg*X` · `pp*X` · `sp*X` · `speed*X` · `aspd*X` | Multipliers, 0.1-10 (`=` or `x` also work as `*`). `dmg` = physical + spell power |
| `limit=15m` · `limit=90s` · `limit=1h` · `limit=0` | Time limit (max 6 h); warnings at 60 and 10 s, then the run fails |
| `faction=keep` (`mixed`) · `faction=one` (`allied`) | Units keep their own factions · force one faction (the default) |

Bad tokens are refused before anything spawns: `Bad multiplier in 'hp*99'`, `Unknown scaling 'banana'`,
`Bad time limit 'limit=9d': …`.

## 5. Broadcasts during a run

Players on the plot receive system messages Raphael can turn into a run HUD (timer, bosses left, toasts):

| Moment | Text |
|---|---|
| Start | `A castle event has begun: <U> foes[ (<B> bosses)] in <G> group(s)[; <D> door(s) are sealed until their guards fall][; time limit <Xm Ys>].` |
| All in place (clock starts) | `All <U> foes are in place. The clock is running[: <Xm Ys>].` |
| Group cleared | `Group '<g>' is defeated[; its door opens] (<Xm Ys>).` |
| Last boss down | `All <B> boss(es are\| is) defeated: <Xm Ys>.` |
| Complete | `The castle event is complete: <Xm Ys>.` |
| Time warning | `<60\|10> seconds left.` |
| Time up | `Time is up: the castle event has failed.` |
| Stopped | `The castle event was stopped.` |

## 6. Screens worth building

The flows below use only today's commands. Send mutations with `UrielClient.SendUser` (the reply shows in
chat); everything a screen needs comes back as reply text until the wire API lands (§7).

**A. Layout builder** (used while walking the castle)
- Group picker: a text field plus recent names. Buttons: **Mob marker here**, **Boss marker here**,
  **Bind nearest door**, **Unbind door**, **Remove group**, **Show layout** (`.uriel event list`).
- Marker options (collapsed by default): unit (blank = squad), count, level, containment picker
  (`plot` / `room` / `storey` / leash slider 3-60 m). Build the command only from fields that are set; use `-`
  for a skipped positional (`.uriel event marker g1 mob - 0 0 room`).
- The reply's `#id` and floor number are worth echoing in the panel.

**B. Library editor**
- Squads: create; an entry form (role toggle, unit chips, count, `any`/`distinct`, level, set number);
  "add to entry" for long lists (`extend`); set weights; show.
- Unit chips: Uriel's object catalog does not include units. Raphael can offer a curated list of common
  `CHAR_*` names (bandits, militia, undead, V Bloods) or a GUID field; send the shortest unique form to stay
  under the chat limit.
- Floor templates: rows of `group → squads` (`*` = every group).
- Templates: rows of `floor → floor templates` (`*` = every floor), plus a default-options builder.

**C. Run panel**
- Template dropdown (from `.uriel template list`, plus floor templates and squads if wanted).
- Options builder: preset buttons, level field or **Auto** toggle (+N), sliders for hp / dmg / speed / aspd,
  a time-limit picker, a **Keep factions** checkbox. Assemble one comma-separated word.
- **Start**, **Stop**, **Status** (poll on demand or every few seconds while open), **History**.
- A HUD fed by the §5 broadcasts: elapsed time from "The clock is running", bosses left, time left.

**D. Admin units** (admins only)
- Spawn form (unit, count, level, here/aim, containment), **List**, **Kill nearby** (radius, all/boss/mob),
  **Clear plot**, and **Purge** behind a confirmation.

## 7. Wire API (planned, Uriel side)

Today nothing in Castle Events emits `[URIEL:*]` lines; ApiVersion stays 1. The next Uriel step is a read API
so Raphael can fill these screens without parsing chat. Proposed shape, following §6 of the main handoff (one
line per message, `key=value` tokens, a header, rows, `[URIEL:end]`). **Raphael: confirm or amend this before
Uriel builds it.**

```
.uriel api version         → … events=0|1 playerevents=0|1           (two new fields on the existing line)
.uriel api layout          → [URIEL:layout] territory=<t> groups=<G> markers=<M> running=0|1
                             [URIEL:evgroup] name=<g> floor=<f> door=<x,y,z|-> doorstate=open|shut|- squad=<s|->
                             [URIEL:evmarker] id=<id> group=<g> slot=mob|boss unit=<guid|0> count=<n> level=<n> contain=<c|-> x= y= z=
                             [URIEL:end] cmd=layout count=<n>
.uriel api library         → [URIEL:library] squads=<n> floors=<n> templates=<n>
                             [URIEL:squad] name=<s> owner=server|me sets=<n>
                             [URIEL:sqentry] squad=<s> set=<k> weight=<w> entry=<e> role=mob|boss count=<n> pick=any|distinct level=<n> units=<guid/guid/…>
                             [URIEL:floorplan] name=<f> owner=server|me groups=<g:s1/s2;g2:s3>
                             [URIEL:template] name=<t> owner=server|me floors=<1:f1/f2;2:f3> options=<o|->
                             [URIEL:end] cmd=library count=<n>
.uriel api run             → [URIEL:run] territory=<t> state=none|spawning|running template=<t|-> options=<o|-> seconds=<s> limit=<s> bosses=<B> bossesleft=<l> cleared=<c>/<G>
                             [URIEL:rungroup] name=<g> squad=<s|-> left=<n> spawned=<m> cleared=0|1 door=held|open|none
                             [URIEL:end] cmd=run count=<n>
```

Until then, parse the reply starts in §3 (they are stable and covered by Uriel's test scenarios) or simply
show the replies as they arrive.

## 8. Config Raphael should know

| Key (`[CastleEvents]`) | Default | Raphael impact |
|---|---|---|
| `Enabled` | `false` | Hide or grey out every Castle Events screen when off |
| `PlayerEvents` | `false` | Non-admins can use screens A-C only when on |
| `DefaultContain` | `storey` | The containment a marker gets when the picker is left empty |
| `MaxMarkersPerPlot` | `40` | Players' marker cap |
| `MarkerCostItem` / `MarkerCostAmount` / `BossMarkerCostAmount` | `0` | Show a marker's cost when non-zero |
| `RunCostItem` / `RunCostAmount` | `0` | Show the run cost when non-zero |
| `MaxUnitsPerCommand` / `MaxUnitsPerPlot` / `MaxTrackedUnits` | `10` / `60` / `300` | Caps on spawns |

Until `api version` carries `events=`, detect availability by sending `.uriel event list` silently: the
disabled and admin-only replies above tell Raphael what to show.

## 9. Testing a Raphael build against Uriel

Connect to the dev server (127.0.0.1:9876) with Uriel 0.21.0 and `CastleEvents.Enabled=true`.

1. Layout: stand in a room, press **Mob marker here** (group `t1`). PASS: chat shows `Marker #… (mob) in group 't1'`.
2. Door: stand by that room's door, press **Bind nearest door**. PASS: `Group 't1' holds the door …`.
3. Library: create squad `t1s` with one entry of 2 × `CHAR_Bandit_Thug`, assign it with `.uriel event squad t1 t1s`.
   PASS: `Group 't1' spawns squad 't1s' …`.
4. Run: **Start** with no template and option `hard` (`.uriel event start - hard`). PASS: the door shuts, two thugs spawn, `Event started …, scaled hp*1.5,dmg*1.25`.
5. Kill both thugs. PASS: `Group 't1' is defeated; its door opens (…)` and `The castle event is complete: …`.
6. **History**. PASS: the run shows as `complete`.
7. **Remove group** `t1`. PASS: `Removed group 't1' (1 marker(s)).`

Uriel's own scenario for the same surface: `tools/vrclient/scenarios/castle-event.vrs` and
`event-layers.vrs` in the Uriel repo.
