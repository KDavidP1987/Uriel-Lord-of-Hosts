# Changelog

Condensed, player-facing changelog. Full technical history:
[GitHub](https://github.com/KDavidP1987/Uriel-Lord-of-Hosts/blob/main/CHANGELOG.md)

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
