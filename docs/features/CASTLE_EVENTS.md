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
2. Do units chase through an open doorway, around a corner, up a storey? Does a closed door hold them? (§11.2, §11.3)
3. Do units keep their storey in a fight? (§11.4)
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
- **Commands (admin):** `.uriel unit spawn <name|guid> [count] [level] [here|aim]`, `list`, `despawn [radius]`,
  `clear` (this plot), `purge` + `purge confirm` (works with the feature off), `status [radius]`,
  `probe [label] [here|aim]`, `probemap [label] [radius]`.
- **Log lines** for tests: `[Uriel UNIT] queued|spawned|status|despawned|gone|died|purge|refused|boot sweep`,
  `[Uriel PROBE] label=…|map|row|map-end`.

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
