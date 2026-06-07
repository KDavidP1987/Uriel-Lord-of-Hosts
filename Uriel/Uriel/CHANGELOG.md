# Changelog

Condensed, player-facing changelog. Full technical history:
[GitHub](https://github.com/KDavidP1987/Uriel-Lord-of-Hosts/blob/main/CHANGELOG.md)

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
