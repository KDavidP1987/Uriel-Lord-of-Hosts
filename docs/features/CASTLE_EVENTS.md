# Castle Events — spawn units, bosses, objects and gates into a castle

**Status:** PLANNED (2026-10-15). No code yet. Phase 0 (indoor spikes) and Phase 1 (unit spawner) are next.

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
7. **Door control** (shared with roadmap #4 and Rentals): can the server lock and unlock a castle door for
   non-owners? Rentals research says doors are gated client-side and per-player grants are clan-scoped. If no,
   the fallback for gated doors is a **barrier object** spawned in the doorway and removed when the zone clears;
   Object Spawning already does that.

### Phase 1 — unit spawner (admin only)

Port the recipe (lessons §1, "Suggested architecture" steps 1-2) as a new service, separate from
`ObjectSpawnService` (whose `IsNonObject` rightly refuses units). Marker, LifeTime safety net, `CanDisable = false`,
budgeted queues, config caps, boot sweep, refused-prefab list, `.uriel unit spawn <prefab|guid> [count] [here]`,
`.uriel unit list`, `.uriel unit despawn`, `.uriel unit purge` + `confirm`. Only on a claimed, alive plot.
Feature switch `[CastleEvents] Enabled`, default **off**.

### Phase 2 — spawn points (admin, then players at a cost)

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
