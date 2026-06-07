# Uriel, Lord of Hosts

A **host** of server-side enhancements and fixes for V Rising — each one
independently toggleable by your server admin. Install on the **server only**;
players need nothing.

**Status: early pre-release.** Features are arriving incrementally.

## Features

### Public storage *(experimental — testing welcome!)*
Mark a *specific* chest as publicly accessible so anyone on the server can use
it — community chests, donation boxes, free-stuff stashes. Always
per-container and owner-controlled: nothing is shared unless its owner shares
it. Shares survive server restarts. Marking a prison cell public (so others
may draw from your prisoners) is a separate upcoming feature.

### Stair hot-swap *(coming soon)*
Swap placed stairs to another stair type straight from the build menu — no
more demolishing a staircase just to change its style, the way other tiles
already swap.

## Commands

```
.uriel                  → overview of the mod and active features
.uriel share            → make the castle container you're aiming at PUBLIC
.uriel unshare          → make it private again
.uriel shared           → list the containers you've made public
.uriel sharedall        → (admin) list all public containers on the server
.uriel unshareall       → (admin) revert everything to private
```

## Configuration

`BepInEx/config/kdpen.Uriel.cfg` — every feature has its own `Enabled` switch:

| Section | Key | Default | Effect |
|---|---|---|---|
| PublicStorage | Enabled | true | Allow per-chest public sharing |
| PublicStorage | PrisonEnabled | true | Allow per-cell public prison sharing *(not yet implemented)* |
| PublicStorage | MaxTargetDistance | 5 | Aim distance for `.uriel share`/`unshare` targeting |
| StairSwap | Enabled | true | Allow in-place stair swapping *(not yet implemented)* |

## Requirements

- [BepInExPack V Rising](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/)
- [VampireCommandFramework](https://thunderstore.io/c/v-rising/p/deca/VampireCommandFramework/)

## Links

- Source, full changelog & issue tracker: [GitHub](https://github.com/KDavidP1987/Uriel-Lord-of-Hosts)
- By the author of [Beelzebub, Lord of Gluttony](https://thunderstore.io/c/v-rising/p/kdpen/Beelzebub/)
