# Castle Events — spawn units, bosses, objects and gates into a castle

**Status:** ALPHA, released in v0.21.0 (2026-10-15), off by default. Built: unit spawner, containment, layouts,
door gates, layered templates with random rolls, run scaling, timer and history (sections "… as built" below).
Open, per [`docs/ROADMAP.md`](../ROADMAP.md) milestones 2-3: second-player and multiplayer tests, spikes 4-6, the
plot-edge containment retest, the `[URIEL:*]` wire API and Raphael screens
([handoff](../../Uriel/Uriel/docs/RAPHAEL_CASTLE_EVENTS_HANDOFF.md)), then the toll door, entry trigger and
capacity, rewards, event objects and maze stairs.

**Sources:** the owner's vision (2026-10-15, below) and Nyarlathotep's lessons file
`..\Nyarlathotep Lord of Chaos\docs\lessons\SPAWNING_LESSONS_FOR_URIEL.md` (cited as "lessons §N"). That file's marks
(**proven** / **unit-tested** / **untested**) carry over: almost everything it proves was proven *outdoors*.

Supersedes these roadmap items, which become parts of this feature: *Triggered NPC spawns*, *PvP Arena boss mode*,
*Area access gating*, and the event half of *Door & teleport controls*. It shares its door work with *Conditional
door access* (roadmap #4) and Castle Rentals Phase 4. The door spike is done once, for all three.

## Owner vision (2026-10-15)

A castle owner, or an admin, turns a castle into an event space:

1. **Spawn units into the castle**, by an admin or by a player at a cost. The owner walks the castle and marks what
   spawns where, through Uriel commands or, ideally, Raphael.
2. **Events and event types.** Entrants don't know what they will face.
3. **Scaling.** Spawns can scale to the entrants' level.
4. **Bosses.** Rules for where bosses spawn, and a choice of bosses per event and per type.
5. **Themes.** One floor undead, the next floor another faction. Themes rolled per floor (RNG) or fixed for the
   whole event.
6. **Gated doors.** A door stays locked until its floor's boss is dead, or until every enemy on the floor is dead.
7. **Path variation.** At event start, stairs are swapped so a maze takes a different route each run (uses Stair
   editing).
8. **Event objects.** At event start, spawn decoration and barriers that block off paths (uses Object Spawning).
9. **End conditions.** A timer, or detection that the entrants beat every boss or reached a goal.
10. **Stop and clean up.** Turn the event off: despawn every unit and every event object, restore stairs.
11. **Later: triggers.** A public door with a toll; paying starts the event and loads the mobs. Schedules.
12. **Open areas, not only rooms** (2026-10-15, after building the test floor). Parts of a castle may have no walls
    and no designated room: on the lowest level a player may spawn units on natural ground or on outdoor floors laid
    out like a garden or a hedge maze. Spawning must not require a room.
13. **Never outside your own castle.** A player can't spawn units in the open world or in a castle that isn't
    theirs. Part of that test needs testers who own other castles (the dev server has only Chaos's).
14. **Units never leave the castle.** Spawned units stay on the castle plot. Narrower containment is wanted too: a
    unit kept to its room (it can't pass a doorway), or to its floor; chosen per unit or per floor.

## Model

| Concept | What it is |
|---|---|
| **Spawn point** | A spot in a castle: position, storey, rotation, zone, slot (`mob`, `elite`, `boss`), what spawns there (a pool or a fixed prefab), count, leash radius. Persisted. Placed with `here`. |
| **Pool** | A named list of unit prefabs that are allied with each other, split by slot. Seeded from Nyarlathotep's mix-tested `FactionGroups` (undead, spiders, bandits, militia); a unit joins a pool only after a mix test shows it allied. |
| **Theme** | A pool assigned to a zone or to a whole event. |
| **Zone** | A tag that groups spawn points: a floor, a room, a wing. Doors and win rules refer to zones. |
| **Event definition** | One castle's event: its zones, theme policy (fixed, rolled per event, rolled per zone), scaling policy, boss rules, gates, stair variants, object sets, timer, win condition, rewards (later), trigger (admin start first, toll later). |
| **Event type** | A reusable template for a definition's policies: "Undead crypt", "Mixed gauntlet", "Boss rush". |
| **Run** | One play of an event: the rolled themes, the unit ledger, the object ledger, the timer. States: `Idle → Arming → Running → Won / Lost / TimedOut / Stopped → CleaningUp → Idle`. |

Units are never persisted. A run spawns them; the end of a run, a stop, a restart or the boot sweep removes them
(lessons §1, §7: sweep and respawn beats persisting units). Spawn points, pools, definitions and types are persisted
as JSON under `BepInEx/config/Uriel/`.

## What we reuse

**From Uriel:** plot resolution and the claimed-and-alive gate, the storey band, the `here` token and the overlap
guard (`ObjectSpawnService`); the per-object cost and caps model (`ObjectConditionsService`); the death postfix
(`DeathEventListenerSystemPatch`, extended for units); `Tick`; stair restyling (`StairSwapService`) for path
variants; object spawning for event objects; the respawn poll's "destroyed vs streamed out" idea.

**From Nyarlathotep** (copying its code is allowed; Learning Mods are learn-only): the spawn recipe, marker buff,
boot sweep, budgeted spawn/despawn queues, caps and two-step purge (`SpawnTracker`, `UnitSetup`, `Markers`,
`SpawnLedger`, `EntityExtensions`); the fight and death reads with the killer chain (`FightPatch`, `DeathEventPatch`,
`ScoreFeed`); arena containment (`Arena`, `SpawnTracker.Return`); `UnitWatch` for stuck units; `FactionGroups`; the
`vrclient` test tool. Uriel gets its **own marker value** so the two mods' sweeps never touch each other's units.

**In Raphael:** it already has a Uriel client (`Services/Uriel/`) and a Nyarlathotep client (`Services/Nyar/`). The
Nyar client's event screens are the model for Uriel's event screens.

## Phases

Each phase works and is tested on the live server before the next starts. The kill switch works from Phase 1 on.

### Phase 0 — indoor spikes (answers the lessons §11 questions)

One short test each, a greppable `[uriel …]` log line per reading, results recorded below.

1. Does the tile map see castle-built floors, walls, doors and stairs? (lessons §11.1; decides placement checks)
   **Answered (Session 2):** walls read blocked, castle floors read free with ground; and the game's own room data
   (`CastleFloor` → `CastleRoomConnection.RoomEntity`) says which room every floor tile belongs to. A stairwell is
   its own room on each storey (Session 3).
2. Do units chase through an open doorway, around a corner, up a storey? Does a closed door hold them? (§11.2, §11.3)
   **Answered (Sessions 2-3):** they follow through an open entrance and an open door, and up the stairs to the
   next storey; behind a closed door they never noticed Chaos 12 m away.
3. Do units keep their storey in a fight? **Answered (Session 3):** spawned upstairs they stayed at y 10 in
   Combat; without containment they climb the stairs after a player; `storey` stops that.
4. What do servants, defences and the heart do; does a unit hitting a structure count as raid damage? (§11.5)
5. Do units in a castle with no players near stay alive and un-frozen? (§11.6)
6. Do units die in sunlight through windows or open doors, and does that raise a `DeathEvent`? (§11.8)
7. ~~**Door control**~~ **Answered (Session 4, `door-control.vrs` 15/15):** yes. Writing `Door.OpenState` opens or
   closes a castle door (the game's DoorSystem swaps the collision and plays the animation; the client renders it),
   and `Interactable.Disabled = true` stops anyone, the owner included, from opening it with F. A closed door holds
   units; opened, they come through. No barrier object is needed.

### Phase 1 — unit spawner (admin only)

Port the recipe (lessons §1, "Suggested architecture" steps 1-2) as a new service, separate from
`ObjectSpawnService` (whose `IsNonObject` rightly refuses units). Marker, LifeTime safety net, `CanDisable = false`,
budgeted queues, config caps, boot sweep, refused-prefab list, `.uriel unit spawn <prefab|guid> [count] [here]`,
`.uriel unit list`, `.uriel unit despawn`, `.uriel unit purge` + `confirm`. Only on a claimed, alive plot.
Feature switch `[CastleEvents] Enabled`, default **off**.

### Phase 2 — spawn points (admin, then players at a cost)

**Superseded by the alpha MVP** (owner, 2026-10-15; see "Phase 2 MVP as built"): markers, templates, manual runs
and door gates landed together, pulling Phase 4's gates and Phase 3's start/stop/status forward.

`.uriel point add <pool|prefab> [count] [mob|elite|boss] [zone]` at `here`, `point list / remove / show`, and
`.uriel event preview` (spawn every point's units for a look, then clear). Persisted in `unit_points.json`. Players:
own plot only, per-plot caps, a placement cost per slot (bosses cost more) through the conditions model; admins
bypass. Raphael wire lines and handoff doc update.

### Phase 3 — events and runs

Definitions, types, `.uriel event start / stop / status`. Theme rolling (fixed / per event / per zone), boss rules,
level scaling to the entrants (lessons §1 item 9: a level write rescales stats; V Bloods keep their own level),
arena containment per zone, timer, win detection from the ledger (all bosses dead, or zone cleared), stuck-unit
handling, full cleanup on stop, restart or castle loss.

### Phase 4 — gates

Doors (or barrier objects, per spike 7) bound to "boss of zone X dead" or "zone X cleared".

### Phase 5 — event objects and path variants

Object sets spawned at run start and removed at cleanup; stair variants rolled per run and restored after.

### Phase 6 — triggers and rewards

Public door with a toll that starts a run; entry trigger; schedule; reward table (the PvP Arena idea: bosses × level
→ weighted item tier). Capacity limit (the Area access gating idea).

### Raphael

Each phase that adds commands updates `Uriel/Uriel/docs/BCH_INTEGRATION_HANDOFF.md` in the same commit. Raphael's
screens are built in its own workspace.

## Decisions

Decided (owner, 2026-10-15):

1. **Who may place spawn points:** the castle owner, on their own plot only. Admins anywhere. A point whose spot
   resolves to someone else's heart is refused.
2. **What a player pays for:** both: a placement cost per spawn point (bosses and elites cost more) and a cost per
   run. Both configurable; admins bypass.
3. **Testing:** adopt Nyarlathotep's in-game testing practice: Claude stops and starts the server, deploys, drives
   the game client through scripted scenarios and admin console commands, then reads the server logs and data
   to judge results and iterate.

4. **Spawning needs a plot, not a room** (vision 12): any free spot on a claimed, alive plot. A spot inside a wall
   or obstacle is refused.
5. **Containment** (vision 14): every unit always stays on its plot. Narrower scopes are per spawn today (per unit)
   and become a property of a spawn point and a zone default (per floor) in Phase 2: `room` (the game's room under
   the spawn spot; it ends at walls and doorways), `storey`, and a leash radius, combinable.

6. **Alpha MVP** (owner, 2026-10-15): units set per floor across a castle, at a cost or by coordinates, triggered
   manually; spawn markers that name their units, and boss spawns; templates a player or admin picks from; and
   doors that stay shut while a group of units lives and open once it is defeated, in a sequence per event. The
   model is layered: an event **template** has a sub-template per **floor**, and within it **groups** of units, each
   group tied to a door. Test: a door, a group of units near it from that floor's template, and the door opens only
   when all of them are dead.

Open, for the owner (needed by Phase 3):

1. **Scaling target** when several players enter at different levels: highest, average, or the starter.
2. **Feeding on spawned V Bloods:** allowed or blocked (lessons §9: a spawned V Blood raises no VBloodKilled).

## Risks

- **Line of sight** (lessons §3): units behind a wall froze. A room behind a closed door may freeze its mob until the
  door opens. Spike 2 decides.
- **Raid damage:** units that hit castle structures outside raid hours. Spike 4 decides.
- **Admins draw no aggro while invulnerable** (lessons §3): tests need a vulnerable character or a second player.
- **Never touch unmarked entities.** Nyarlathotep's `.devkill` once killed 232 world entities at a castle.

## Phase 1 as built (2026-10-15, unreleased)

`Services/UnitSpawnService.cs`, `Commands/UnitCommands.cs`, config `[CastleEvents]` (`Enabled` default **false**;
caps `MaxUnitsPerCommand` 10, `MaxUnitsPerPlot` 60, `MaxTrackedUnits` 300; budgets `MaxSpawnsPerTick` /
`MaxDespawnsPerTick` 5 per pump, about 6 pumps a second; `UnitLifetimeSeconds` 3600; `ClearDrops` true).

- **Recipe** (lessons §1): `InstantiateEntityImmediate` → marker buff (`AB_Consumable_PhysicalPowerPotion_T01_Buff`,
  gameplay-event components stripped, stat buffer cleared, own LifeTime 0/None) with `SpellLevel = 1431456069`
  ("URIE", Uriel's own value) → LifeTime/Destroy + `Age = 0` → Translation/LastTranslation → `DestroyWhenDisabled`
  → drop table cleared → `CanDisable = false`, aggro on, optional level. A unit failing any step is destroyed.
- **Ledger and queues:** spawn and despawn queues drained every 5 frames under the budgets; units the game removed
  are pruned each pump; deaths are read in the existing `DeathEventListenerSystem` postfix with the killer chain
  (up to three `EntityOwner` hops to a player).
- **Boot sweep:** every buff carrying the marker (`IncludeDisabled | IncludeSpawnTag`) → its unit is queued for
  removal. Units are never persisted. Runs even with the feature off.
- **Refused:** anything not `CHAR_*` with Health + UnitLevel + AggroConsumer; player characters;
  `DropInInventoryOnSpawn`; name fragments `Mount_Horse`, `CarriagePrisonerRelease`, `MicroPOI`,
  `Undead_ShadowSoldier`, `VampireMale/Female`, servants, prisoners, `_Summon`, `Dummy`, `Test`.
- **Placement:** 2.5 m in front of the admin (or `aim`), several units on a 1.2 m golden-angle ring; the spot must
  resolve to a claimed, alive castle plot (`ObjectSpawnService.TryResolvePlot` + `CastleClaimedAndAlive`).
- **Commands (admin):** `.uriel unit spawn <name|guid> [count] [level] [here|aim] [contain]` (see Containment),
  `list`, `despawn [radius]`, `rooms`,
  `clear` (this plot), `purge` + `purge confirm` (works with the feature off), `status [radius]`,
  `probe [label] [here|aim]`, `probemap [label] [radius]`.
- **Log lines** for tests: `[Uriel UNIT] queued|spawned|status|despawned|gone|died|purge|refused|boot sweep`,
  `[Uriel PROBE] label=…|map|row|map-end`.

## Containment as built (2026-10-15, unreleased)

`Services/CastleFloorIndex.cs` plus a containment section in `UnitSpawnService`. Config `[CastleEvents]
ContainCheckSeconds` (default 1).

- **Scopes**, the last `spawn` argument: `plot` (default, and always enforced), `room`, `storey` (or `floor`), a
  leash radius in metres (3-60), or a comma mix such as `room,15`. Unknown words and out-of-range radii are refused
  before anything spawns. The reply ends `contained to: <scope>`.
- **Rooms are the game's own.** Every `CastleFloor` links to a room (`CastleRoomConnection.RoomEntity`); a room ends
  at its walls, doorways included. An area with floors but missing walls is one room spanning the connected floors
  (the courtyard on plot 11 is one 102-tile room). `room` containment needs a floor that belongs to a room under the
  spawn spot; on natural ground use `plot`, `storey` or a leash.
- **Floor lookup:** floors are 5 m tiles on the same 5 m blocks as territories. The index caches each castle's floors
  by block (refreshed every 15 s, and on every spawn on the plot) and each territory's blocks, so the per-second check
  is a dictionary lookup. The floor under a position is the highest one in its block at or below it (+1.5 m).
- **Spawn home:** at spawn, `AggroConsumer.PreCombatPosition` and `SpawnTransform.Position` are the spawn spot; with a
  leash, `AggroConsumer.MaxDistanceFromPreCombatPosition` is the leash, so the game itself gives up the chase and
  walks home (Session 2: it does, before the hard return is needed).
- **The hard return** (Nyarlathotep's arena): every check, a unit outside its scope (`plot`: its position's block not
  in the territory; `storey`: more than 2.5 m above or below its spawn height; leash: more than leash + 2 m from its
  spawn spot; `room`: no floor, or a floor of another room) loses its AggroBuffer entries on players outside the same
  scope and is moved to its spawn spot (Translation, LastTranslation, return point, path cleared, velocity zeroed).
  One `[Uriel UNIT] contain key=… reason=plot|storey|leash|room|room-nofloor … dropped=N returns=N` line each.
- **Blocked spots** are refused: a 0.4 m circle at the spot's height level that the static tile map reports blocked.
- **Survey:** `.uriel unit rooms` writes each room (tiles, heights, enclosed / missing walls / roof, bounds), a 5 m
  block map of the plot per storey (a letter per room), and every door and wall entrance with the rooms on each side.
  `.uriel unit probe` also reports the floor and room under you.

Watch: a returned unit drops only the players outside its scope, so a player standing in the doorway could pull it
back out repeatedly. Session 2 saw one return per unit and no rubber-banding.

## Phase 2 MVP as built (2026-10-15, unreleased)

`Services/CastleEventService.cs`, `Services/CastleDoors.cs`, `Commands/EventCommands.cs` (`.uriel event`,
`.uriel template`). Persisted in `BepInEx/config/Uriel/castle_events.json` (layouts, templates, doors held by a
run), saved on every change and on unload.

- **Layout** (one per castle plot): **groups**, each on a **floor**, each with **markers** and optionally one
  **door**. A group is made by its first marker, on the floor its placer stands on. Floors are counted from the
  castle's own floor heights: 1 + the number of floor levels (heights within 2 m are one level) more than 2 m below.
  Plot 11: ground = 1, upstairs = 2.
- **Marker:** a spot (where you stand), a slot `mob` or `boss`, optionally its own unit (else the template's), a
  count and level (0 = the template's), and a containment (else `[CastleEvents] DefaultContain`, default `storey`).
- **Door:** `.uriel event door <group>` binds the castle door nearest you (within 6 m) by position; one door per
  group, one group per door.
- **Template:** `[floor 1, 2, ... | *] -> [group | *] -> mob / boss -> units` (a list; one is picked at random per
  marker per run), a count per marker, a level. A marker without its own unit takes the most specific entry:
  floor+group, floor+*, *+group, *+*. An admin's template is a server template anyone may use; a player's is their own.
- **Start** (`.uriel event start [template]`, manual): every marker must resolve and the units must fit under the caps
  before anything spawns or is charged. Each group's door is shut and locked (its state remembered and saved); every
  marker is queued through the unit spawner with the tag `ev<territory>:<group>`. Players on the plot get a message.
- **Monitor** (about once a second): a group with no unit alive or queued is **cleared**: its door unlocks and opens,
  and the players on the plot are told. A held door someone forced open is shut again. All groups cleared = the
  event is complete (time logged and announced).
- **Stop:** removes the run's units (and pending spawns) and puts every held door back as it was. A restart ends a
  run: units are swept at boot as always, and saved held doors are put back. A door that can't be found stays
  pending and is retried (every ~10 s, then every minute) until it is found or its castle is gone.
- **Disabled entities:** castle doors and floors far from every player are Disabled (`DisableWhenNoPlayersInRange`);
  `CastleDoors` and `CastleFloorIndex` query with `IncludeDisabled`. Found at boot: a default query saw zero doors.
- **Who:** admins on any plot; castle owners (team match, so clan members) on their own plot when
  `[CastleEvents] PlayerEvents` is on (default off). Players are capped at `MaxMarkersPerPlot` (40).
- **Costs (players):** `MarkerCostItem` x `MarkerCostAmount` per mob marker, x `BossMarkerCostAmount` per boss marker,
  refunded to the payer on removal; `RunCostItem` x `RunCostAmount` per start, not refunded. All 0 (free) by default.
- **Log lines:** `[Uriel EVENT] loaded|marker-added|door-bound|door-unbound|removed|layout|template-created|template-set|
  template-deleted|door-held|started|marker-failed|status|door-reshut|group-cleared|complete|stopped|door-released|
  door-pending|door-abandoned|refused`; spawned units carry `tag=ev...` on `[Uriel UNIT] queued|spawned`.

Superseded the same day: the flat template (`template set`) became the layers below.

## Layers, random rolls, scaling, timer as built (2026-10-15, unreleased)

The owner's model (message of 2026-10-15): "group 1 floor 1 spawn 5 militia; group 2 floor 1 spawn 1 boss; these
feed a floor-one template; group 3 floor 2 10 skeletons, group 4 floor 2 2 bosses feed floor two; then a castle-wide
template of floor 1 and 2." Code: `Services/CastleEventService.Library.cs`, `Services/UnitScaling.cs`.

- **Squad** = what one group spawns. One or more **sets** (combinations); one set is rolled per run, by weight
  (`squad weight`). A set is a list of **entries**: role `mob`/`boss`, a unit list, a count, a pick, a level.
  `any` = each of the count drawn at random with repeats (an assortment); `distinct` = that many different ones
  ("1 of these 3 bosses", "2 of these 10"). `squad extend` appends units to an entry (chat lines are short).
- **Floor template** = layout group name (or `*`) -> squad list; several = one rolled per run.
- **Template** = floor (`1`, `2`, … or `*`) -> floor-template list; several = one rolled per floor per run (a theme),
  plus default run options.
- **Start** `.uriel event start [name] [options]`: the name is a template, else a floor template (used for every
  floor), else a squad (every group gets it); none = each group's own squad (`event squad`). Per group: its squad is
  rolled; its mobs go round-robin to its mob markers, its bosses to its boss markers (to any marker if it has none of
  that slot). A marker with its own unit keeps it. Everything resolves and fits the caps before anything spawns.
- **Scaling** (run options; `UnitScaling`): `level=N` / `level±N` / `auto[+N]` (the highest player level on the plot,
  `Equipment.GetFullLevel`) write `UnitLevel` (the game rescales base stats; V Bloods included here). Multipliers
  `hp`, `dmg` (`pp`+`sp`), `speed`, `aspd` are `ModifyUnitStatBuff_DOTS` entries (`MultiplyBaseAdd`, value x-1) on
  the unit's marker buff, Nyarlathotep's method. Presets: easy (hp, dmg 0.75), normal, hard (hp 1.5, dmg 1.25), brutal
  (hp 2.5, dmg 1.5, speed 1.1, aspd 1.15). Template options first, the start's add to or override them.
- **One faction per run** (default): every unit takes the faction (`FactionReference`) and `Team` of the run's first
  boss, else its first unit. Found in testing: the floor-2 wolf V Blood killed a skeleton (beasts and undead are
  hostile; undead, bandits and militia are allied, per Nyarlathotep's faction-mix test). Options `faction=keep` /
  `mixed` opt out; `faction=one` / `allied` force it on over a template's `keep`. Logged: `started … faction=<prefab|keep>`,
  `spawned … faction=Faction_Bandits/team=2:0`.
- **Timer:** the clock starts when every unit of the run is in place (`clock-started`). Logged and announced: each
  group cleared, all bosses down (`bosses-defeated … seconds=`), complete. `limit=` (max 6 h) warns at 60 and 10 s
  and then fails the run (`time-up`, then a stop with `timed-out`). The last 100 runs are kept with template, options,
  units, bosses, result, time and time to the last boss; `.uriel event history` shows the last 5 and the best times.
- **Testing aid:** `.uriel unit kill [radius] [all|boss|mob]` (admin) kills Uriel's own units through the game's
  kill path, so groups clear the way they do in play.
- **Log lines added:** `squad-created|squad-entry|squad-extend|floor-template-created|floor-assign|template-created|
  template-floor|template-options|*-deleted|group-squad|auto-level|floor-rolled|group-rolled|clock-started|
  bosses-defeated|time-warning|time-up|timed-out|history`; `[Uriel UNIT] spawned … boss=1 scale=… level=N`,
  `[Uriel UNIT] killed n= which= radius=`; unit status adds `pp= sp= mspd= aspd=`.

Not yet (later phases): an event toll door, maze stair variants, event objects, rewards, Raphael wire lines. Untested: the player path (PlayerEvents, costs, refusal on another's castle) needs a
second player.

## Switches and kill switch (v0.22.0, 2026-10-18)

Every gate reads `Settings.<Feature>On` = `[General] Enabled` AND the feature's own switch, so a
switched-off feature is off for admins too. Admin cleanup never reads the switches. Server-wide reset:
`.uriel killswitch [confirm]` or `[KillSwitch] RestoreVanillaOnBoot=true` (one shot at the next start,
then it sets itself back to false); both run `Services/KillSwitch.cs`.

- `CastleEvents.Enabled=false` turns off unit spawning and events. `unit list|kill|despawn|clear|purge`
  and admin `.uriel event stopall` still work. Units are never saved; the boot sweep removes any left.
- `CastleEvents.DoorGates=false`: `event door` binding and the `unit door` tool are refused, and a run
  never holds a door (bound groups simply run without one). Unbinding still works.
- Kill switch order: `StopAll` (each run's units despawned, its doors put back as they were, not
  opened), then `PurgeAllNow` for any unit left. Doors not found yet stay on the pending-release retry.

## Testing

In-game tests follow Nyarlathotep's practice (CLAUDE.md › In-game tests): `tools/devserver.ps1` stops, deploys and
starts the shared dev server; `tools/vrclient/` plays Chaos on 127.0.0.1:9876 and runs `tools/vrclient/scenarios/*.vrs`.

## Test results

### Session 1 · 2026-10-15 · Phase 1 unit spawner + spike 1 baseline (Claude via vrclient, world nyardev, plot 11)

- `smoke.vrs`: **SCENARIO PASS 3/3** (`[URIEL:version] api=1 plugin=0.20.0 ready=1`).
- `unit-spawner.vrs`: **SCENARIO PASS 23/23** (results `%TEMP%\vrclient\results\unit-spawner-20261015-125028.json`).
  Refused `CHAR_VampireMale`; queued 3 `CHAR_Bandit_Thug` at x=-2001.7 y=5.0 z=-1716.7 (hlevel 11, territory 11),
  all 3 spawned over consecutive pumps; still alive 15 s later (no `gone`); despawn 1, clear 2 (`left=0`); purge
  arm + confirm removed 2; list empty after. Boot sweep at start: `found=0 queued=0`.
- **Aggro (spike 2, first reading):** with Chaos vulnerable (727 HP), all 3 thugs were `state=Combat target=me` within
  2 s at 1.2-1.8 m, level 16 (prefab level), full HP. Outdoors on the plot; indoors not yet tested.
- **Spike 1 baseline** (`castle-probe.vrs`, **PASS 4/4**): Chaos's plot is **bare** (heart only, no floors or walls),
  so this is the open-ground reference. At hlevel 11 (y 5.0), trees and rocks read as 2x2 `##` blocks among `.`
  ground (59 blocked / 565 free in a 25 x 25 m square); hlevel 12 reads **all blocked** (624/624): a level with no
  surface is not free. So a second storey should show as free cells on a higher level, and walls as `#` lines.
- Logs: no Uriel errors or warnings. Unity log has 224 "PrefabGUID … unknown state" warnings at world load (the
  known baseline, ~226 in Nyarlathotep's notes).
- **Blocker for spikes 1-6:** they need a built castle. The owner builds a small test structure on plot 11 (see
  the session summary for the spec), then the spikes run unattended.

### Session 2 · 2026-10-15 · containment, built ground floor (Claude via vrclient, world nyardev, plot 11)

The owner built a ground floor: 177 floor tiles in 7 rooms (`.uriel unit rooms`): a 102-tile outdoor-floor courtyard
(missing walls, no roof), a 38-tile enclosed hall north of it, and five smaller rooms, joined by 12 openings: two
closed wide doors (wood, metal), wide and plain stone entrances, a wood entrance. No second storey yet. The plot is a
plateau: a 30 m cliff to the south (y -25) and 5 m drops east and north (y 0); level ground continues west (y 5).

- `floor-survey.vrs`: **PASS 5/5**. Probe at home: floor (-2002.5, 5.0, -1712.5), courtyard room, `enclosed=False
  missingWalls=True roof=False`. Tile map: walls are `#` rows, outdoor floors `.`; the level above reads all blocked.
- `castle-contain.vrs`: **PASS 31/31** (results `%TEMP%\vrclient\results\castle-contain-20261015-144927.json`).
  - Refused: a spawn 15 m off the plot ("Units can only be spawned inside a castle plot."), containment `sideways`,
    leash 500. An earlier draft also proved the blocked-spot refusal: a spot 0.7 m from a room's wall was refused.
  - **Plot:** 2 thugs at the courtyard's west edge chased Chaos 12 m off the plot; both returned at x -2041 (edge
    -2040), 8-9 m from spawn, 1 player entry dropped each, then Idle at their spawn spots.
  - **Leash control** (no leash): Chaos stepped 12 m away; both followed, Combat on Chaos at 1.1-1.4 m.
    **Leash 6:** the same move; both Idle 2.9-4.2 m from spawn, no hard return: the game's own leash did it.
  - **Room:** 2 thugs in a 10-tile enclosed room followed Chaos to its open entrance; each was returned the moment it
    stood on the hall's floor (x -2022.6 and -2024.4; the entrance is at x -2025). **Control** (plot only): both
    followed into the hall, Combat on Chaos, no return.
  - **Closed door (observation):** 2 thugs in the room between the two closed doors, Chaos 12 m away beyond the wood
    door: both stayed Idle, no target. A closed castle door already holds units.
- Regression after the last build: `smoke` 3/3, `unit-spawner` 23/23.
- Logs: no Uriel errors or warnings; the server log has no exceptions. Other mods' start-up warnings only.
- Tool fixes: `devserver.ps1` now minimizes the server's BepInEx console (it covered the main menu and vrclient's
  OCR couldn't find PLAY). One run lost the chat box for a few steps inside the castle (OCR); it didn't recur.
- **Still open:** `storey` containment and units on stairs (needs a second floor; done in Session 3); refusal on
  another player's castle (needs a tester's castle and Phase 2's player path, since admins may spawn anywhere);
  sunlight, servants and raid damage (spikes 4 and 6).

### Session 3 · 2026-10-15 · second storey, stairs, doors (Claude via vrclient, world nyardev, plot 11)

The owner added a second storey (y 10): 247 floor tiles in 14 rooms over two storeys, 22 openings (closed and open
wood doors upstairs, a closed door on the ground floor, an open iron gate). The stairwell (ground room x
-2022.5..-2017.5, z -1702.5..-1697.5) is all stairs, from the small ground room east of it (y 5) to a top landing at
(-2022.5, -1702.5), y 9.3, in the upstairs stairwell room; no door on the stairs. The upstairs and ground stairwells
are separate rooms. On a stair, the floor lookup reads the ground stairwell up to y 8.5, the upstairs room above.

- `storey-survey.vrs`: **PASS 5/5**. Upstairs floors read free but not grounded in the tile map (`' '`), the level
  above them all blocked; upstairs walls are `#`.
- `storey-contain.vrs`: **PASS 24/24** (results `%TEMP%\vrclient\results\storey-contain-20261015-160322.json`).
  - **Upstairs spawn:** 2 thugs spawned at y 10.0 in the big upstairs room stayed at y 10.0, Combat on Chaos.
  - **Control** (plot only): 2 thugs in the small ground room chased Chaos to the top landing and fought him there
    (y 8.7 and 9.1).
  - **`storey`:** the same move; both returned mid-stairs at y 7.8-7.9 (dy 2.8-2.9), then Idle at their spawn.
  - **`room`:** the same move; both returned as they stepped onto the stairwell (y 6.3 and 7.3).
  - **Upstairs `room` through an open door:** 2 thugs in the big upstairs room followed Chaos through the open door
    (z -1695) into the corridor room; returned at z -1696.3 and -1698.0, y 10.0, then Idle.
  - A first draft taught a test lesson: `.devtp x z 10` over a stairwell lands on the stair surface (y 6.5), not the
    landing; probe heights first (`.uriel unit probe` after each `.devtp`).
- Logs: no Uriel errors or warnings; server log has no exceptions.
- **Still open:** refusal on another player's castle (tester + Phase 2 player path); sunlight, servants, raid damage
  (spikes 4 and 6).

### Session 4 · 2026-10-15 · door gates and the Phase 2 MVP (Claude via vrclient, world nyardev, plot 11)

- `door-control.vrs`: **PASS 15/15** (spike 7). Server close holds two thugs in room H; server open lets them through;
  locked, F doesn't open it; unlocked, F does. Screenshots show the door open and closed.
- `castle-event.vrs`: **PASS 33/33** three times (last results `%TEMP%\vrclient\results\castle-event-20261015-171709.json`).
  Template `evtest` (floor 1 / any group: 2 thugs per mob marker; any floor / any group: 1 stalker per boss marker);
  group `gate1` = a mob marker in courtyard A holding the wood door (-2030, -1710); group `gate2` = a boss marker in
  room H holding the metal door (-2030, -1695).
  - Start: both doors shut and locked, 3 units spawned with their group tags, status 2/2 and 1/1 left.
  - A held door forced open was shut again within a second (`door-reshut`).
  - Thugs killed (`.devkill`): `gate1` cleared, its door unlocked and opened (screenshot: open, chat "Group 'gate1'
    is defeated; its door opens"); `gate2` still held.
  - Stalker killed: `gate2` cleared, its door opened, the event completed (77 s).
  - Second run, stopped: 3 units removed, both doors put back as they were (open, unlocked).
- **Restart mid-run:** run started with the wood door open, server restarted. First build: the doors weren't found at
  boot (a default query saw zero Door entities with nobody online) and were dropped, leaving them locked; fixed with a
  retry loop and then `IncludeDisabled` queries. Final build: released on the first try with nobody online, and the
  door was back open and unlocked when Chaos arrived.
- Regression: `unit-spawner` 23/23, `door-control` 15/15, `storey-contain` 24/24. `castle-contain` 29/31: the two
  plot-edge checks failed because the owner has since fenced and hedged the courtyard's west edge, so the thugs had
  no path to Chaos off the plot (they stayed Idle, no target). That test needs a new edge; the containment code
  itself is unchanged.
- Logs: no Uriel errors or warnings; server log has no exceptions.

### Session 5 · 2026-10-15 · layers, random rolls, scaling, timer, one faction (Claude via vrclient, world nyardev, plot 11)

- `event-layers.vrs`: **PASS 37/37** (`%TEMP%\vrclient\results\event-layers-20261015-191116.json`). The owner's
  example: squads militia5 (5 of guard/light militia, `any`), boss1of3 (1 of 3 V Bloods, `distinct`), skel10 (set 1:
  10 soldiers; set 2: 6 soldiers + 4 crossbows), bosses2 (2 of 10 V Bloods, `distinct`, the list added in parts with
  `squad extend`); floor templates f1 (g1, g2) and f2 (g3, g4); template castle1 (floor 1 f1, floor 2 f2). Groups g1
  (courtyard, wood door), g2 (room H, metal door), g3 (upstairs, upstairs door), g4 (upstairs, boss marker). Start:
  18 units, 3 bosses, 3 doors held; clock started 1.6 s later; each group cleared opened its door; bosses-defeated and
  complete timed (54 s); history recorded.
- Found and fixed on the way: chat lines are cut at ~120 characters (long `squad add` lost its `distinct`; short unit
  names and `squad extend` added); the skeletons aren't in the game's spawnable-name list (name lookup falls back to
  all prefab names); a squad started on its own matched every group through a `\u0000` sentinel and culture-sensitive
  `StartsWith` (replaced by a field); **the wolf V Blood killed a skeleton** on floor 2 (one faction per run added; the
  passing run had every unit on `Faction_Bandits/team=2:0` and only Chaos killed anything).
- vrclient: the chat input won't open (OCR) while Chaos stands at (-2030, -1698) right by the metal door; the door is
  bound from (-2030, -1700) instead.
- `event-scaling.vrs`: **PASS 25/25** (`event-scaling-20261015-191622.json`). Courtyard thug readings:

  | Run | level | hp | pp / sp | mspd | aspd |
  |---|---|---|---|---|---|
  | A none | 16 | 53.8 | 13.7 | 3.2 | 1.0 |
  | B `level=40` | 40 | 99.4 | 26.6 | 3.2 | 1.0 |
  | C `level=40,hp*2,dmg*1.5,speed*1.2,aspd*1.3` | 40 | 198.7 (x2.00) | 39.9 (x1.50) | 3.8 (x1.19) | 1.3 (x1.30) |

  D `hard,auto+2`: the highest level on the plot was 66, so level 68 with hp*1.5, dmg*1.25. E: template `tscaled`
  (`hp*3,limit=10m`) + start `speed*1.1` = `hp*3,speed*1.1`, limit 600 s. F `limit=25s`: 10 s warning, time-up at
  25 s, run removed (4 units, 3 doors put back), history `timed-out`. Bad options (`hp*99`, `banana`, `limit=9d`)
  refused before anything spawned.
- Regression: `castle-event.vrs` **PASS 44/44** (now removes event-layers' g1-g4 first, which hold the same doors;
  the rolls log before the doors are held), `unit-spawner.vrs` **PASS 23/23** (now spawns in the courtyard: TPHome
  lands in the owner's new hedge maze, where the spot in front is a hedge). vrclient's chat opener retries 5 times
  with longer waits (at night the chat hint is low-contrast and OCR missed it).
- Logs: no Uriel errors or warnings; the only exceptions are the boot-time Harmony reflection warnings.
- Scenario order on plot 11: `event-layers` builds the g1-g4 layout that `event-scaling` uses; `castle-event` removes it.

### Test results: switches and kill switch (v0.22.0, 2026-10-18)

vrclient on the local dev server, single player, each with its own server config:
- `SCENARIO PASS killswitch 19/19` (results `%TEMP%\vrclient\results\killswitch-20261018-133222.json`): a spawned
  object and a running event (two held doors, three units) → `.uriel killswitch confirm` →
  `[Uriel EVENT] killswitch territory=11 units=3 doorsRestored=2`, `[Uriel KILLSWITCH] why=command events=1 units=3
  … objects=1`, no units left, the gate1 door back to its pre-run state; `purgeall` removed 1; `event stopall` with
  nothing running. The first run (16/19) caught the purge double-count, fixed before this pass.
- Boot reset: `RestoreVanillaOnBoot=true` → `[Uriel KILLSWITCH] why=boot … objects=1`, the setting read `false`
  afterwards, the courtyard empty in game.
- `SCENARIO PASS switches-off 13/13` (ObjectSpawn, DoorGates, CostsEnabled off): admin spawn and the api refused,
  door binding and the door tool refused, a run started with `doors=0` and held none, `purgeplot` still removed the
  object; the boot log had no `auto-respawn loop started`.
- `SCENARIO PASS master-off 14/14` (`General.Enabled=false`): stairs, objects, units, events and sharing refused for
  an admin; `unit list`, `event stopall`, `purgeorphans` and `killswitch` still work.
- Regression: `unit-spawner` 23/23, `castle-event` 44/44.
- Not tested in game: the CostsEnabled refusal on a real chest and the prison/chest boot close (no container on the
  test plot).
