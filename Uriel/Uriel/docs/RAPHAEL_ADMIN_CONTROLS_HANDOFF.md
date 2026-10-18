# Uriel v0.22.0 → Raphael: admin controls and kill switches

> Hand this to the Raphael workspace. It covers only what changed in **Uriel v0.22.0** (2026-10-18); the full
> contract stays in [`BCH_INTEGRATION_HANDOFF.md`](BCH_INTEGRATION_HANDOFF.md) and Castle Events in
> [`RAPHAEL_CASTLE_EVENTS_HANDOFF.md`](RAPHAEL_CASTLE_EVENTS_HANDOFF.md). Source of truth:
> `Uriel/Uriel/Commands/KillSwitchCommands.cs`, `Services/KillSwitch.cs`, `Config/Settings.cs`. If this document and
> the code disagree, the code wins and this document gets corrected.
>
> **No wire-API change.** ApiVersion stays **1**; no new `[URIEL:*]` line. Everything below is human-text chat
> replies, one reply per line, sent as system chat.

## 1. What changed, in one paragraph

Some server admins want to run Uriel with parts of it off (most often: no spawned objects until it is thoroughly
tested) and want a guaranteed way back to vanilla. v0.22.0 adds a **master switch**, two new feature switches
(**event doors**, **paid shares**), makes every switch bind **admins too**, and adds **kill-switch commands** that
undo what Uriel put in the world. The cleanup commands work even with a feature, or the whole mod, switched off.

## 2. Config keys (`BepInEx/config/kdpen.Uriel.cfg`; changes apply at server restart)

| Key | Default | What it does | Raphael impact |
|---|---|---|---|
| `[General] Enabled` | `true` | **New.** Master switch: false turns every feature off for everyone, admins included | Hide all Uriel feature screens; keep the admin cleanup panel (section 4) |
| `[KillSwitch] RestoreVanillaOnBoot` | `false` | **New.** One-shot reset at the next start (same as `.uriel killswitch`), then it sets itself back to false | None (server-side only) |
| `[StairSwap] Enabled` | `true` | Stair swap and `removestairs` | Hide the stair screen when off |
| `[ObjectSpawn] Enabled` | `true` | Object spawning, discovery, the `respawn` loop and the `uriel api` | Hide the object palette when off (the api answers `[URIEL:err] code=disabled`) |
| `[ObjectSpawn] AdminOnly` | `true` | Only admins may spawn | Non-admins: hide the palette |
| `[CastleEvents] Enabled` | `false` | NPC units and castle events | Hide the event screens when off |
| `[CastleEvents] DoorGates` | `true` | **New.** Events may shut and lock castle doors | Hide door binding when off |
| `[PublicStorage] Enabled` | `true` | Chest sharing | Hide share buttons when off |
| `[PublicStorage] PrisonEnabled` | `true` | Prison-cell sharing (needs `PublicStorage.Enabled`) | Hide cell share / take-prisoner when off |
| `[PublicStorage] CostsEnabled` | `true` | **New.** Paid shares: the `cost` modifier and `.uriel paychest` | Hide the cost field and pay-chest button when off |

A feature is on only when `[General] Enabled` **and** its own switch are on.

## 3. Finding out what is on

**Admins:** `.uriel features` (admin-only). One reply line, for example:

```
Uriel features: Stair swap on. Object spawning on (admins only). NPC units and castle events on (admins only), event doors on. Public chests on, prison cells on, paid shares on. Changes to the config take effect at the next server start.
```

- Each feature reads `on` or `OFF`. `(admins only)` follows Object spawning when `ObjectSpawn.AdminOnly` is true,
  and follows castle events when `CastleEvents.PlayerEvents` is false.
- With the master switch off the line starts `Uriel features: Master switch [General] Enabled is OFF: everything
  below is off.` and every feature reads `OFF`.
- Suggested parse: `(Stair swap|Object spawning|NPC units and castle events|event doors|Public chests|prison cells|paid shares) (on|OFF)`.

**Non-admins** can't run `.uriel features`. Keep the current approach: send the feature's own command and treat
any reply containing **`disabled by the server admin`** as "off". Every refusal contains that phrase:

| Feature | Refusal reply |
|---|---|
| Stairs | `Stair swapping is disabled by the server admin.` |
| Objects | `Object spawning is disabled by the server admin (ObjectSpawn.Enabled).` |
| Objects (api) | `[URIEL:err] code=disabled` |
| Castle events and units | `Castle events are disabled by the server admin (CastleEvents.Enabled).` |
| Event doors | `Event doors are disabled by the server admin (CastleEvents.DoorGates).` (from `.uriel event door` and `.uriel unit door`; `undoor` still works) |
| Chests | `Public storage is disabled by the server admin.` |
| Prison cells | `Prison-cell sharing is disabled by the server admin (PublicStorage.PrisonEnabled).` (share), `Prison-cell sharing is disabled by the server admin.` (`takeprisoner`) |
| Paid shares | `Paid shares are disabled by the server admin (PublicStorage.CostsEnabled).` (`share cost <id> <n>` with a non-zero item, and `paychest`) |

When the master switch is off, the refusals still name the feature's own key (for example
`(ObjectSpawn.Enabled)`), so don't rely on the key in brackets to tell why.

**Paid shares off, existing priced chest:** a chest that already has a price stays **closed** to others (never made
free behind its owner's back). A withdrawal is refused with this system message:
`This container has a price, and paid shares are switched off on this server — it's closed until its owner removes the price.`
The owner can still remove the price with `.uriel share cost 0 0` or unshare it.

## 4. New admin commands (all admin-only; all work with features or the master switch off)

| Command | Reply |
|---|---|
| `.uriel features` | See section 3 |
| `.uriel killswitch` | Arms for 30 s: `This removes everything Uriel put in the world: <n> tracked object(s), every Uriel unit, every running event, and <m> share(s). Saved settings are kept. Run '.uriel killswitch confirm' within 30 s. To keep it off afterwards, set [General] Enabled = false in the config.` |
| `.uriel killswitch confirm` | `Kill switch done: <a> event(s) stopped, <b> unit(s) being removed, <c> object(s) removed, <d> container(s) made private.` Possibly followed by `<n> spawned object(s) couldn't be found right now and are still tracked; run it again once their area is loaded.` and/or `<n> event door(s) weren't found yet; Uriel keeps trying to put them back.` If not armed: `Not armed. Run '.uriel killswitch' first.` |
| `.uriel purgeall` | Arms for 30 s: `This removes every Uriel-spawned object on the server (<n> tracked). Run '.uriel purgeall confirm' within 30 s.` |
| `.uriel purgeall confirm` | `Removed <n> Uriel-spawned object(s) server-wide.` optionally followed, on the same line, by ` <m> couldn't be found right now and are still tracked; run it again once their area is loaded.` If not armed: `Not armed. Run '.uriel purgeall' first.` |
| `.uriel event stopall` | `Stopped <n> castle event(s).` (optionally ` <m> door(s) weren't found yet; Uriel keeps trying to put them back.`) or `No castle event is running.` |

What the kill switch does, in order: stops every castle event (its units removed, its doors put back exactly as they
were before the run), removes every Uriel unit, removes every Uriel-spawned object on every plot, and makes every
shared chest and prison cell private (the share registry is cleared). It never touches native objects. Saved
settings (unlocks, event layouts, squads, templates) are kept. Swapped stairs are left as they are: a swap leaves an
ordinary game staircase.

**After a kill switch, every container Raphael showed as shared is private again** (entity ids change for rebuilt
chests, as with any unshare): refresh any share list and any object list.

These existing admin commands now also work with their feature switched off: `.uriel purgeplot`,
`forcepurgeplot`, `forcedespawn`, `purgeorphans`, `unit list|kill|despawn|clear|purge`. `.uriel unshareall`,
`unshareplayer`, `sharedall` already did.

## 5. Suggested Raphael UI (optional)

- **Admin "Server controls" panel:** a status row from `.uriel features`; buttons for Stop all events,
  Remove all units (`unit purge` + confirm), Remove all objects (`purgeall` + confirm) and the red **Restore vanilla**
  (`killswitch` + confirm). Send the confirm command only after an in-UI confirmation, and within 30 s of the arm
  command. Show the reply lines verbatim.
- **Hide what's off:** use the features line (admins) or the `disabled by the server admin` refusal (everyone)
  to hide or grey out a screen, the door-binding control (DoorGates), and the cost field / pay-chest button
  (CostsEnabled).

## 6. Proposed for a later version (not implemented; tell us if you want it)

A machine line so Raphael doesn't parse prose: `.uriel api features` →
`[URIEL:features] v=1 master=1 stairs=1 objects=1 objectsAdminOnly=1 events=0 eventsPlayers=0 doors=1 chests=1 cells=1 costs=1`,
answerable by everyone (not admin-only). This would bump ApiVersion to 2.

## 7. Tested

In game on the local dev server (vrclient, single player, 2026-10-18), each with its own server config:
`killswitch.vrs` (everything on: object + running event with two held doors → kill switch → doors restored, no units,
object gone; `purgeall`; `event stopall`), the startup reset (`RestoreVanillaOnBoot`, object removed at boot, flag
reset to false), `switches-off.vrs` (objects, doors and paid shares off: refusals, a run holds no doors, `purgeplot`
still works), `master-off.vrs` (everything refused, cleanup still works). Not tested in game: the paid-shares refusal
on a real chest, and the prison-cell switch at boot (no chest or cell on the test plot).
