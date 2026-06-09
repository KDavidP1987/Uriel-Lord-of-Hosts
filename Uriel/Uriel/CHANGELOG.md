# Changelog

Condensed, player-facing changelog. Full technical history:
[GitHub](https://github.com/KDavidP1987/Uriel-Lord-of-Hosts/blob/main/CHANGELOG.md)

## 0.18.1 (2026-06-09)

Critical hotfixes from live testing of 0.18.0:

- **Fix: spawning a pre-filled container no longer crashes the server.** Some containers (e.g. the
  "Full" bookshelves/drawers/cabinets and loot chests) try to generate their contents the instant they
  spawn, which crashed the whole server. They're now kept out of the spawn catalog. **Crafting stations,
  wardrobes, empty containers, and everything else are unaffected** — you can still place an empty
  container and fill it yourself.
- **Fix (important): `.uriel forcepurgeplot` no longer removes native objects.** In 0.18.0 it could
  delete a plot's natural resource nodes/trees. It now does exactly what `.uriel purgeplot` does — only
  Uriel's own objects, never anything native.
- **Fix: object catalog / BloodCraftHub handshake no longer spams errors.** A bad internal type check
  broke the catalog and flooded the log on `.uriel api version`; resolved.

## 0.18.0 (2026-06-09)

- **New: objects can't be stacked inside each other or inside walls.** A placement guard
  (`ObjectSpawn.PreventOverlap`, on by default) refuses to spawn or move an object onto a
  wall, station, or another spawned object — only floors may sit under décor. This prevents
  the overlapping pile-ups that can destabilize a server. **Tip: don't try to overlap objects
  on purpose.** Admins can set it off to allow free stacking (at your own risk).
- **New: `.uriel spawn … here` now drops the object in FRONT of you**, not on top of your
  character. (`.uriel move` near yourself works the same way.)
- **Fix: invisible / hazardous objects can no longer be spawned.** Some prefabs (world-gen
  "point of interest" controllers, baked décor "source" copies, and **roof pieces** that only
  render up on a building) end up **invisible** when spawned on the ground — and a few even
  carry NPC spawners. These are now filtered out of the catalog so players can't pick them.
  **If an object ever still spawns invisible, it's a not-yet-found bad one — `.uriel despawn`
  it (it IS there, just not rendering) and please report it.**
- **Fix (important): `.uriel forcepurgeplot` no longer deletes native objects.** It could
  wrongly wipe a plot's plants, trees, and garden tiles. Plot-purge is now two tiers, and both
  only ever touch Uriel's own objects: **`.uriel purgeplot`** = light (your spawns), **`.uriel
  forcepurgeplot`** = strong (also clears legacy leftovers).
- **New (admin): `.uriel purgeorphans`** — a server-wide cleanup that scans the whole map and
  removes orphaned Uriel objects (ones whose castle was destroyed, or that are sitting where no
  castle governs them). Only Uriel's own objects are ever touched. A handy backup to the automatic
  cleanup that already runs at server start.

## 0.17.0 (2026-06-08)

- **New: choose how a spawned object can be destroyed.** Add a flag when you spawn:
  `breakable` (raids/decay can destroy it), `smashable` (you can destroy it by hand
  too), or leave it `indestructible` (the default). Example:
  `.uriel spawn <object> 0 smashable`.
- **New: `respawn` flag.** A spawned object set to respawn automatically comes back
  after it's destroyed — until its castle is gone or you `.uriel despawn` it. Great
  for decor you want to "stick." Admin switch: `ObjectSpawn.RespawnEnabled`
  (+ `RespawnPollSeconds`).
- **New: place at YOUR location with `here`.** `.uriel spawn <object> 0 here` (or
  `.uriel move here`) drops the object where you're standing instead of where the
  cursor points — handy when a UI panel has your aim pointing off into space.
- **Fix: breakable / indestructible now actually work.** Previously the setting did
  nothing on most world objects (and "indestructible" chests could even vanish on
  their own after a while). Both now behave correctly.
- **Fix: characters & bosses no longer show up as spawnable objects.** Any `CHAR_`
  units / V Bloods that slipped into your unlock list from an older version are
  cleaned out automatically, and they can no longer be spawned.
- **Fix (BloodCraftHub):** the object catalog and your unlocked-objects list now load
  in the companion UI (they previously came back empty).

## 0.16.0 (2026-06-08)

- **Fix: spawned objects can be managed again after a relog or server restart.**
  `.uriel despawn` / `move` / `rotate` / `spawninfo` previously couldn't find
  objects you'd spawned in an earlier session — they now re-find them reliably,
  and the spawned-object list is no longer wiped on restart.
- **Fix: removing certain older spawned objects no longer makes them reappear.**
  Some objects spawned in an early build kept re-spawning the instant you removed
  them; they're now removed for good.
- **New (admin): `.uriel forcedespawn`** — force-remove the object you're aiming
  at, even one Uriel doesn't track (e.g. left over from an older version). Run it
  once to target (it names the object), then `.uriel forcedespawn confirm` within
  30s. It never targets your castle heart.
- **New (admin): `.uriel forcepurgeplot`** — clear every leftover spawned object
  on the plot you're standing in, including untracked ones. Your normal build
  pieces are left untouched.

## 0.15.0 (2026-06-08)

- **New feature: Object Spawning — collect & place WORLD objects in your castle!**
  Bring in resource nodes, world chests, breakable props, and dungeon/GloomRot/
  Cursed decor that the normal build menu never offers. `.uriel spawn <object>`
  places it inside your plot; `.uriel move` / `.uriel rotate` / `.uriel despawn`
  manage it. Spawned objects are indestructible & decay-proof by default.
- **Collect them by playing.** In Discovery mode you unlock an object by
  **destroying one in the world** (admin-set % chance) — trees, chests, ore
  nodes, crates. `.uriel unlocks` shows your collection and %; `.uriel catalog`
  browses everything; `.uriel notify on|off` toggles the unlock messages.
- **Finish the game, get the rest.** The ~half of objects that can't be destroyed
  can unlock on 100% collection, on **defeating Dracula**, or per-boss via an
  admin map — admin's choice (or off).
- **Admin controls:** Full vs Discovery access, a build cost, block/allow specific
  prefabs, grant objects to players, and a kill-switch for the whole collection
  system. Castle build-menu pieces are excluded by default (this is about the
  *world* objects you can't otherwise get).
- **In-game help got friendlier:** `.uriel help` now gives a clean, topic-by-topic
  menu (`objects` / `storage` / `stairs` / `admin`) instead of one long list.
- Optional BloodCraftHub integration: a machine API exposes the object catalog and
  each player's collection for a future client-side palette UI.
- Note: spawned world objects are managed with the `.uriel` commands above, not
  the vanilla build menu (an engine limitation).

## 0.14.0 (2026-06-07)

- **Stair swap now applies INSTANTLY — no restart needed!** Restyling a staircase
  rebuilds it in the new style on the spot, so everyone sees the change live.
  Works on every stair shape (straight, curved left/right, wide). Now available
  to all players, not just admins.
- **New: `.uriel removestairs`** — cleanly delete a staircase you own *without*
  tearing apart the floors and walls it's attached to (which a normal dismantle
  would disturb).
- New config `[StairSwap] RespawnGapFrames` (default 5) to tune the rebuild.
- Note: a swapped stair is rebuilt as a fresh object — if anything ever looks
  off, dismantle & rebuild it.
- Licensed under **AGPL-3.0** (Uriel adapts techniques from odjit's
  KindredCommands & KindredSchematics and shares their copyleft license).

## 0.13.2 (2026-06-07)

- Stair swap works! The restyle is applied and saved instantly; the new look
  appears for everyone at the next server restart (an engine limitation —
  placed buildings render from a snapshot the server creates at startup).
  The swap message now says exactly that.

## 0.13.1 (2026-06-07)

- Fix: swapped stairs now actually change their appearance (the game hides
  the visual identity in a second place — found it). Relog if a swap doesn't
  show immediately; a server restart settles everything.

## 0.13.0 (2026-06-07)

- Stair swap reworked: the placed stair now simply BECOMES the other style —
  nothing is demolished or rebuilt, no materials move, and the stair keeps
  behaving exactly like the one you originally placed. If it still shows the
  old look right after swapping, step away and back (or relog).

## 0.12.2 (2026-06-07)

- Fix: stair swaps complete now — if the game's own dismantle doesn't finish
  (it usually doesn't outside build mode), Uriel removes the stair itself and
  refunds you its full material cost before building the new style. Tall
  staircases are two stacked halves — swap each half (aim at it).

## 0.12.1 (2026-06-07)

- Fix: stair swaps no longer leave invisible "ghost" stairs — removal now
  uses the game's own dismantle (with its normal material refund), and the
  new style is built only once the spot is truly free.
- New admin command `.uriel stairpurge` to clean up ghost stairs left by
  earlier builds (stand near them, run it, restart the server, rebuild).

## 0.12.0 (2026-06-07)

- Fix: swapped stairs are now REAL build objects — highlightable, movable,
  dismantlable — because the swap now rebuilds them through the game's own
  placement system. Stairs stuck as "permanent" from earlier builds: swap
  them once with this version to repair them. If the game ever refuses a
  placement, you keep the stair's full material cost in your inventory and
  can place it by hand — a swap can never lose your stair's value.

## 0.11.0 (2026-06-07)

- All targeted commands accept a `nearest` option to act on the container or
  stair closest to YOU instead of where you're aiming (e.g.
  `.uriel share nearest`, `.uriel stairswap stone2 nearest`) — handy when a
  UI or menu is open. Aim targeting also falls back to nearest automatically
  when nothing is in front of you.

## 0.10.0 (2026-06-07)

- NEW: `.uriel takeprisoner` — aim at a shared prison cell to take its
  prisoner: they're subdued and released to you (bring Dominating Presence
  to escort them). The game's own subdue button can't be shown to non-owners
  (it's locked inside the game client), so Uriel does it via command.

## 0.9.0 (2026-06-07)

- Fix: sharing a chest now takes effect immediately for online players — no
  game restart needed (chest contents are fully preserved through the change).
- Fix: swapped stairs stay selectable/editable. Already-stuck stairs from the
  previous build: swap them once more with this version to repair them.
- Known issue: prisoners in shared cells can be fed/extracted but not yet
  subdued by others — a dedicated command is planned.

## 0.8.2 (2026-06-07)

- Fix: share/unshare really does take effect immediately for online players
  now (the previous fix wasn't enough — containers briefly blink as they
  refresh for everyone).

## 0.8.1 (2026-06-07)

- Fix: sharing/unsharing now takes effect immediately for players already
  online (previously could require a relog).
- Fix: prisoners in a shared cell can now be subdued/charmed out by others.

## 0.8.0 (2026-06-07)

- Fix: shared chests/prison cells are now accessible to other players even
  when the server's "loot enemy containers" setting is OFF (the usual
  configuration). Shares survive restarts; unsharing fully restores the
  container to its castle.

## 0.7.0 (2026-06-07)

- STAIR HOT-SWAP (experimental): aim at a staircase and `.uriel stairswap
  <style>` re-skins it in place — no demolishing. Only styles of the same
  stair shape are offered, and DLC styles require owning that DLC (same rule
  as your build menu). `.uriel stairstyles` lists what's available;
  `.uriel stairswap next` cycles.

## 0.6.0 (2026-06-07)

- `.uriel shared` now lists your public containers with their full rules;
  `.uriel unsharemine` reverts ALL of yours in one command.
- Admins: `.uriel sharedall [player]` filter, `.uriel unshareplayer <player>`
  bulk shutdown, and aim + `.uriel share`/`.uriel unshare` now override any
  container's settings.

## 0.5.0 (2026-06-07)

- Public PRISON CELLS (experimental): `.uriel share` a cell to let anyone
  feed/extract from your prisoner — or charm the prisoner out as their own
  subdued follower. Separate admin switch (`PublicStorage.PrisonEnabled`);
  chest and cell sharing stay independent.
- Fix: deposits can no longer push wrong-type items into restricted slots
  (prison feeding slots, lumber/seed stashes).

## 0.4.1 (2026-06-06)

- Fix: depositing into a shared container no longer fails with "container is
  full" (items were always safely refunded, but the deposit never landed).
  Cost payments had the same latent bug — also fixed.
- Unshare restores the chest's ownership more faithfully; if a chest looks
  locked right after unsharing, close and reopen it.

## 0.4.0 (2026-06-06)

- Share modifiers now stack in one command, any order:
  `.uriel share LimitHours 6 Cost 123456789 100 Permission Take`.
- Payment safety: payments only collect when a destination can hold the full
  amount; specialized stashes (lumber/seed/…) are never used; if the pay chest
  is full, payment falls back to the shared chest, then the nearest general
  chest in the owner's castle — and if everything's full the trade is simply
  refused (taker keeps their items).

## 0.3.0 (2026-06-06)

- Sharing rules (experimental): `.uriel share permission take|give|givetake`,
  withdrawal limits per player (`.uriel share limithours 6`,
  `.uriel share limitwithdrawal 2`), and per-stack access costs
  (`.uriel share cost <itemId> <amount>`, payments delivered to your
  `.uriel paychest`). Look up item ids with `.uriel finditem <name>`;
  inspect any container's rules with `.uriel info`.
- Fix: others can now PUT items into shared containers (vanilla silently
  refused deposits; owners were affected too).

## 0.2.1 (2026-06-06)

- Fix: `.uriel share` reported "no neutral team source found" on live servers
  (distant world objects are disabled and were invisible to the lookup; also
  fixed the same issue in share re-application at server restart).

## 0.2.0 (2026-06-06)

- **Public storage (experimental):** aim at one of your castle chests and use
  `.uriel share` to let ANYONE on the server use it — per-container, owner
  controlled, `.uriel unshare` to revert. `.uriel shared` lists yours; admins
  get `.uriel sharedall` / `.uriel unshareall`. Shares survive restarts.
  Prison-cell sharing is a separate upcoming feature. Not yet validated on a
  live server — test feedback welcome.

## 0.1.0 (2026-06-06)

- Initial scaffold — mod loads server-side, `.uriel` overview command, config
  switches in place. No gameplay features yet; stair hot-swap and public
  storage are in development.
