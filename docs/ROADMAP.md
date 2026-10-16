# Uriel — Roadmap

Uriel is an **umbrella mod**: a *host* of small, independently toggleable server-side features. This page
lists what has shipped, how mature each part is, and what remains before 1.0. Priorities follow tester
feedback; nothing here is a promise or a date.

Ideas and votes: **[The Shadow Realm Discord](https://discord.gg/usC9QgBrXK)**.

## Maturity

| Stage | Meaning |
|---|---|
| **Alpha** | Built and tested by the developer on a dev server. Expect rough edges; commands and config may change. Off by default. |
| **Beta** | In testers' hands. Core paths validated live; edge cases and broader validation still open. |
| **Stable** | Validated on a live production server; commands and config keys frozen. The bar for 1.0. |

## Where each feature stands (v0.21.0)

| Feature | Stage | Since | Still open |
|---|---|---|---|
| Object Spawning | Beta | 0.15.0 | Live validation of abandon cleanup, spawn conditions and boss-kill unlocks; more server-lag work on move/rotate/delete; the block list keeps growing as testers report objects |
| Public Storage (chests) | Beta | 0.2.0 | Broader validation of withdrawal limits and access costs |
| Public prison cells | Beta | 0.5.0 | Broader validation of `.uriel takeprisoner` |
| Stair Editing | Beta | 0.7.0 | Edge cases (attachments, decay, pathing); an open report of a clan ownership issue with swapped stairs |
| Castle Events | **Alpha** | 0.21.0 | See milestones 2 and 3 |

## Road to 1.0

### Milestone 1 — Harden the beta features

- Run Object Spawning, Public Storage and Stair Editing on a live production server and fix what it finds.
- Reduce server cost of repeated move / rotate / delete (in-place rotate, a short-lived index cache).
- Resolve the swapped-stairs clan ownership report once a reproduction is available.

### Milestone 2 — Castle Events to beta

- Second-player tests: refusal on another player's castle, the player cost path, events with several
  players inside.
- Re-test plot-edge containment on a new edge (the original test edge has since been fenced).
- Answer the remaining indoor questions: castle servants and defences against event units, units on a plot
  with nobody near, sunlight through windows and doorways.
- A machine-readable `[URIEL:event*]` API so Raphael can show layouts, the library and live runs without
  parsing chat text, and the Raphael screens on top of it
  ([handoff](../Uriel/Uriel/docs/RAPHAEL_CASTLE_EVENTS_HANDOFF.md)).

### Milestone 3 — Castle Events complete

- **Toll door:** a public door that charges entry and starts a run.
- **Entry trigger and capacity:** start a run when players enter; cap how many are inside at once.
- **Rewards:** a configurable prize table (for example bosses × level → a weighted item tier).
- **Event objects:** decorations and barriers spawned at the start of a run and removed at the end.
- **Maze stairs:** stair variants rolled per run so the path changes, restored afterwards.

### Milestone 4 — Castle Rentals

Rent a coffin or a room for a set time: a paid lease, a spawn point, access to chosen storage, and eviction
with item return when it ends. Design: [`docs/features/CASTLE_RENTALS.md`](features/CASTLE_RENTALS.md).

### 1.0 release criteria

- Every feature is **Stable**: validated on a live production server, commands and config keys frozen.
- Raphael covers the main screens of every feature.
- Player and admin documentation is complete.

## After 1.0 (exploratory)

Ideas under consideration; they may change shape or not happen.

- **Conditional door access:** share a door for a payment or during set hours.
- **Bulk storage sharing:** share all heart-linked storage at a location at once, excluding the pay chest and
  private chests.
- **Teleporters** to set map coordinates.
- **Plot expansion:** a phase gate to a separate instance, or copying map sections into buildable plots.
- **Expanded castle decor:** more in-game assets usable as decoration.

## Shelved

- **Basement levels:** building below the first floor isn't feasible server-side (the terrain mesh and the
  camera are client-side). See [`docs/features/BASEMENT_BUILDING.md`](features/BASEMENT_BUILDING.md).

## Shipped history

| Version | Highlights |
|---|---|
| 0.21.0 | Castle Events (alpha): spawned units, containment, layouts, door gates, layered templates, scaling, timer |
| 0.20.0 | Flush-to-ground object placement, hazard and container block lists, less lag on move |
| 0.19.0 | Per-object and global spawn conditions, abandoned-plot cleanup during play |
| 0.15.0 – 0.18.x | Object Spawning: catalog, Discovery unlocks, respawn and durability flags, overlap guard |
| 0.14.0 | Live stair restyle |
| 0.7.0 – 0.13.x | Stair hot-swap, clean stair removal |
| 0.2.0 – 0.6.0 | Public storage, sharing rules, public prison cells |
| 0.1.0 | Project scaffold |

Full detail: [`CHANGELOG.md`](../CHANGELOG.md).
