# Uriel, Lord of Hosts

A **host** of server-side enhancements and fixes for V Rising — each one
independently toggleable by your server admin. Install on the **server only**;
players need nothing.

**Status: early pre-release.** Features are arriving incrementally.

## Features

### Public storage *(experimental — testing welcome!)*
Mark a *specific* chest as publicly accessible so anyone on the server can use
it — community chests, donation boxes, free-stuff stashes, even paid vending
boxes. Always per-container and owner-controlled: nothing is shared unless its
owner shares it. Shares survive server restarts.

**Public prison cells** *(experimental)*: share a prison cell to let anyone
tend your prisoner — feed, extract blood, or charm the prisoner out as their
own subdued follower. Governed by its own admin switch, independent of chest
sharing.

**Sharing rules** (set by aiming at your shared container):
- *Permission*: take-only, give-only (donation box), or both.
- *Withdrawal limit*: e.g. 1 stack per player per 24 hours.
- *Access cost*: charge an item per stack withdrawn — payment is delivered to
  your designated private pay chest. A vending machine, basically.

### Stair hot-swap *(experimental — testing welcome!)*
Aim at a placed staircase and swap it to another style **in place** — no more
demolishing just to restyle. Only styles of the same stair shape (narrow,
narrow-curved left/right, wide) are offered, and DLC styles require owning
that DLC — exactly the same rule as your build menu. Free, instant, preserves
position/rotation/ownership.

## Commands

```
.uriel                            → overview of the mod and active features
.uriel share                      → make the castle container you're aiming at PUBLIC
.uriel unshare                    → make it private again
.uriel info                       → show a container's sharing rules (anyone)
.uriel shared                     → list the containers you've made public
.uriel share permission <mode>    → take | give | givetake (default givetake)
.uriel share limithours <h>       → rolling window for the withdrawal limit
.uriel share limitwithdrawal <n>  → stacks a player may take per window
.uriel share cost <itemId> <amt>  → charge per stack withdrawn (0 0 = free)
  ↳ modifiers STACK in one command, any order:
    .uriel share LimitHours 6 Cost 123456789 100 Permission Take
.uriel paychest                   → aim at a PRIVATE chest: receives payments
.uriel finditem <name>            → search item ids for the cost command
.uriel takeprisoner               → take the prisoner from a SHARED cell
                                    (subdued + released to you)
.uriel unsharemine                → revert ALL of your shares in one command
.uriel stairswap <style|next>     → re-skin the aimed stair (stone1/2/3,
                                    gloomrot, projectk, strongblade)
.uriel stairstyles                → list styles available for the aimed stair
.uriel sharedall [player]         → (admin) list all/one player's public containers
.uriel unshareplayer <player>     → (admin) revert ALL of one player's shares
.uriel unshareall                 → (admin) revert everything to private
```

Admins can also aim at **any** container and use `.uriel share [modifiers]` /
`.uriel unshare` to override its sharing settings directly.

**Admin kill switches:** `PublicStorage.Enabled` disables the whole feature
(commands refuse, enforcement stops, shares boot as private chests after
restart); `PublicStorage.PrisonEnabled` independently governs prison cells.
Config changes take effect on server restart.

## Configuration

`BepInEx/config/kdpen.Uriel.cfg` — every feature has its own `Enabled` switch:

| Section | Key | Default | Effect |
|---|---|---|---|
| PublicStorage | Enabled | true | Allow per-chest public sharing |
| PublicStorage | PrisonEnabled | true | Allow per-cell public prison sharing *(experimental)* |
| PublicStorage | MaxTargetDistance | 5 | Aim distance for `.uriel share`/`unshare` targeting |
| StairSwap | Enabled | true | Allow in-place stair restyling *(experimental)* |
| StairSwap | MaxTargetDistance | 6 | Aim distance for the stair commands |

## Requirements

- [BepInExPack V Rising](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/)
- [VampireCommandFramework](https://thunderstore.io/c/v-rising/p/deca/VampireCommandFramework/)

## Links

- Source, full changelog & issue tracker: [GitHub](https://github.com/KDavidP1987/Uriel-Lord-of-Hosts)
- By the author of [Beelzebub, Lord of Gluttony](https://thunderstore.io/c/v-rising/p/kdpen/Beelzebub/)
