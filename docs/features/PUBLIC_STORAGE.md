# Feature: Public Storage (per-container opt-in)

**Status:** DESIGN — not yet implemented
**Config:** `[PublicStorage] Enabled` (default `true`),
`[PublicStorage] PrisonEnabled` (default `true`)

## Problem

Vanilla storage (chests/stashes) is only accessible to the owning player/clan.
On co-op or community servers, admins and players often *want* specific
containers to be open to everyone (a "free stuff" chest, a community
donation box, a shared prisoner pen) — but the game offers no per-container
sharing, and server-wide "anyone can open anything" settings (where mods
provide them) are all-or-nothing.

## Goal

Let a container's **owner** mark a **specific** container as publicly
accessible, and unmark it later. Two independent container classes:

1. **Storage containers** (chests, stashes, coffers…) — public = any player
   can open and take/put items.
2. **Prison cells** — public = any player may interact with the cell's
   prisoner (draw blood / extract). Governed by a **separate** config switch
   and a separate command, because the sharing implications differ.

Explicitly NOT goals: server-wide unlock of all storage; sharing someone
else's container; bypassing PvP castle raiding rules.

## Interaction design (initial)

Player targets the container and identifies it to the mod. Primary approach:
"the container you are looking at / last interacted with." Fallback per the
user's framing: the player supplies the container's ID directly.

Proposed commands (names provisional):

```
.uriel share            → mark the targeted/last-opened container public
.uriel unshare          → revert it to private
.uriel shared           → list YOUR public containers (id, type, location hint)
.uriel share cell       → same pair for the targeted prison cell
.uriel unshare cell
.uriel sharedinfo       → (admin) list all public containers on the server
```

Resolution order for "which container do I mean": (1) the container entity
the player most recently opened/targeted, captured by a lightweight patch;
(2) explicit entity/network ID argument as fallback (`.uriel share <id>`).

## Mechanism research checklist (do BEFORE implementation)

- [ ] How does the game authorize a container-open? Find the server system
      that validates team/ownership on `OpenInventory`-style interaction
      events — that check is the patch point (allow if container is in the
      public registry).
- [ ] Same question for prison-cell interactions (blood draw / prisoner
      extraction) — different interaction system, different patch point.
- [ ] How to robustly identify a placed container entity across restarts:
      entity IDs are not stable across save/load — find the persistent ID
      (NetworkId? PersistenceKey? coordinates+prefab fallback) the registry
      should key on.
- [ ] How to know which container a player is "looking at": interaction
      target component on the player character vs. patching the
      open-attempt event (the denied open attempt itself identifies the
      container — possibly the cleanest UX: try to open → get denied → mod
      message "this chest is private"; owner runs `.uriel share` after
      opening it themselves).
- [ ] Check Learning Mods + Thunderstore for prior art (KindredCommands and
      similar QoL mods have storage-related admin features) — read how they
      resolve container entities.

## Persistence

Public-container registry → JSON under `BepInEx/config/Uriel/`
(`public_containers.json`): owner steamId, container persistent ID, container
class (storage vs. prison), marked-at timestamp. Debounced save-on-change +
save-on-unload. Registry entries whose container no longer exists are pruned
on load (castle demolished, chest destroyed).

## Edge cases to handle

- Container destroyed / castle decayed → prune registry entry.
- Owner leaves clan / clan dissolves → owner check must follow whoever can
  legitimately control the container now.
- Raid/PvP: public access must not create a raid bypass (e.g. opening
  through walls); interaction still requires the vanilla proximity checks —
  only the *team* check is relaxed.
- Two containers same position after rebuild → persistent-ID choice must not
  mis-attach the public flag.

## Test plan (fill in during implementation)

- [ ] Owner shares chest → stranger can open/take/put. Unshare → denied again.
- [ ] Prison cell share (separate command/config) → stranger can draw blood.
- [ ] Chest share does NOT make prison cells public and vice versa.
- [ ] Restart server → shares persist; destroyed containers pruned.
- [ ] Non-owner cannot share someone else's container.
- [ ] Each config switch off → that class behaves pure vanilla.
