# Changelog

Condensed, player-facing changelog. Full technical history:
[GitHub](https://github.com/KDavidP1987/Uriel-Lord-of-Hosts/blob/main/CHANGELOG.md)

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
