# Feature: Stair Hot-Swap

**Status:** DESIGN — not yet implemented
**Config:** `[StairSwap] Enabled` (default `true`)

## Problem

In vanilla V Rising castle building, most tile sets (walls, floors, windows)
can be re-skinned/swapped in place, but **stairs cannot**: changing a stair to
another stair type requires demolishing the existing stair and placing a new
one — losing placement and costing the dismantle/rebuild loop. This is an
inconsistency in the building system, not a balance decision.

## Goal

Let a player swap a *placed* stair to another stair type they can build,
in place — same position/rotation — matching the UX of the game's existing
tile-swap behavior as closely as a server-side mod can.

## Constraints

- **Server-side only.** No client UI can be added; the swap must ride on
  existing client→server interactions (build-menu placement events, or a
  chat command fallback).
- Respect build permissions: only the castle's owner/clan (whoever could
  demolish + rebuild legally) may swap; cost handling should match vanilla
  expectations (ideally charge the new stair's cost / refund the old, or
  swap free — decide during research).
- Must not corrupt castle persistence: stairs participate in the castle
  grid/connectivity model; a naive entity swap could break pathing,
  snap-points, or save/load.

## Research checklist (do BEFORE implementation)

- [ ] Find stair prefabs in the dump: glob `Prefabs/TM_*Stair*` (and
      `TM_*Stairs*`) — list the per-tileset variants and their GUIDs.
- [ ] Identify how the game implements existing tile swaps (walls/floors):
      which server system processes the swap event; can it be invoked for
      stair tiles by patching its eligibility check?
- [ ] Compare the component layout of two stair prefabs from different
      tilesets — confirm a "replace prefab in place" is structurally safe
      (same footprint/sockets) vs. needing despawn+respawn at same transform.
- [ ] Check Learning Mods (KindredCommands has castle/build admin utilities)
      for prior art on placed-tile manipulation.
- [ ] Decide the interaction surface: patched build-menu swap (ideal) vs.
      `.uriel stairswap <type>` targeting the stair the player is looking at
      (fallback; mirrors the public-storage targeting approach).

## Open questions

- Does the build-menu "replace" event even reach the server for stair tiles,
  or does the client block it before sending? (If client-blocked, the chat
  command fallback is the only server-side option.)
- Cost model: free swap vs. material delta vs. full cost of new stair.
- Should swap preserve attached objects (railings, decorations) if any?

## Test plan (fill in during implementation)

- [ ] Swap each stair tileset → each other tileset on a test castle.
- [ ] Swap on multi-floor connected stairs — verify pathing/connectivity.
- [ ] Save, restart server, verify swapped stairs persist correctly.
- [ ] Non-owner attempts swap → denied.
- [ ] Feature disabled in config → behavior is pure vanilla.
