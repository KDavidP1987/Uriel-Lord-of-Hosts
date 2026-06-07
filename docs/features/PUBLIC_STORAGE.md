# Feature: Public Storage (per-container opt-in)

**Status:** IMPLEMENTED v0.2.0 (storage class only) — **pending live-server validation**
**Config:** `[PublicStorage] Enabled` (default `true`),
`[PublicStorage] PrisonEnabled` (default `true`, prison not yet implemented),
`[PublicStorage] MaxTargetDistance` (default `5`)

## Implementation decision (v0.2.0)

Went with a refined **Approach B (team-swap)** rather than validation patches,
after the prefab comparison showed castle stashes and world chests carry
IDENTICAL default `Team`/`TeamReference` — meaning access is decided purely by
the *runtime* team assigned at placement, and world chests (neutral team) are
openable by everyone, client prompt included (Team replicates).

- **Share** = copy a live world chest's `Team`/`TeamReference` onto the
  container ("neutral donor", resolved lazily from known `TM_WorldChest_*`
  GUIDs and cached).
- **Unshare** = restore `Team`/`TeamReference` from the container's own
  `CastleHeartConnection` → castle heart. The heart is always the
  authoritative restore source, so no original-team persistence is needed —
  this also makes recovery trivial (worst case: restart reverts everything
  not in the registry; `.uriel unshareall` force-restores from hearts).
- **Ownership check** = caller's `Team.Value` vs. the castle *heart's*
  `Team.Value` (not the container's, which is neutral while shared).
- **Registry** keys on prefab GUID + `TilePosition.Tile` (stable across
  save/load; entity/NetworkIds are not), persisted at
  `BepInEx/config/Uriel/public_containers.json`, re-applied at server init.
- Zero Harmony patches needed for this mechanism — if validation proves the
  hypothesis wrong, the per-system patch table below is the fallback plan.

Code: `Services/PublicStorageService.cs`, `Commands/ShareCommands.cs`.

## Problem

Vanilla storage (chests/stashes) is only accessible to the owning player/clan.
On co-op or community servers, admins and players often *want* specific
containers open to everyone — but the game offers no per-container sharing,
and existing approaches are castle-wide or clan-scoped, never per-chest and
never "public to all."

## Goal

Let a container's **owner** mark a **specific** container as publicly
accessible, and unmark it later. Two independent container classes:

1. **Storage containers** (chests/stashes) — public = any player can open
   and take/put items.
2. **Prison cells** — public = any player may interact with the cell's
   prisoner (extract blood / feed). Separate config switch + command; the
   user requires these be treated as distinct storage types.

Explicitly NOT goals: server-wide unlock; sharing someone else's container;
raid-rule bypasses.

## Prior art (researched 2026-06-06)

**No mod implements per-individual-container public access.** Closest
neighbors (all open source — these are our pattern libraries):

- **PalacePrivileges** (cheesasaurus,
  github.com/cheesasaurus/ProfuselyViolentProgression →
  `BepInExPlugins/PalacePrivileges/`) — *castle-wide* privilege grants to clan
  or named players via `.castlePrivs` (`CastlePrivileges` flags struct incl.
  `prison.extractBlood`, `prison.feedSafeFood`). Conceptually our prison
  half, minus public-to-all and per-cell granularity. **Best blueprint for
  the authorization machinery**; its `notes/misc.md` documents the internals.
- **KindredInnkeeper** (odjit) — *room-scoped* container blocking (inn rooms):
  patches the same inventory systems to DENY; we patch to ALLOW. Shows the
  full set of side-channel systems that must be covered consistently.
- **ScarletCore** (markvaaz) — generic interact hook: patches
  `InteractValidateAndStopSystemServer.OnUpdate` and re-emits OnInteract
  events (`Patches/InteractPatch.cs`).

## Mechanism findings

### How container access is decided (no single gate!)

There is **no `PublicAccess` component** in the game. Access control is
multi-layered on the container entity: `Team` (int value) + `TeamReference` +
`UserOwner`, enforced per-action by separate server systems. Relevant
container components (from prefab dump, `TM_Stash_Chest_Wood_General`,
GUID `-251472465`): `InventoryOwner`, `InventoryInstanceElement` (14 slots),
`Interactable`, `InteractAbilityBuffer` (`AB_Interact_OpenContainer_*`),
`CastleHeartConnection`, `CastleSharedInventory`, `NameableInteractable`
(`OnlyAllyRename: True`, `OnlyAllySee: True` — nameplates!).

### The systems that must agree (patch surface)

An "allow" must cover every path or the feature feels broken:

| Action | System (server) | Event/data |
|---|---|---|
| Open/use container | `InteractValidateAndStopSystemServer.OnUpdate` | interact queries; ScarletCore shows the hook |
| Move one item | `MoveItemBetweenInventoriesSystem.OnUpdate` | `MoveItemBetweenInventoriesEvent` (`FromInventory`/`ToInventory` NetworkIds) + `FromCharacter` |
| Move all items | `MoveAllItemsBetweenInventoriesSystem` (+ `V2` variant) | same shape |
| Sort | `SortAllInventoriesSystem`, `SortSingleInventorySystem` | KindredInnkeeper patches these |
| Split stack | `SplitItemSystem` | " |
| Drop from container | `DropInventoryItemSystem` | " |
| Prisoner interaction | `InteractWithPrisonerSystem.OnUpdate` | `InteractWithPrisonerEvent` (`Prison` NetworkId, `PrisonInteraction`: Imprison/Charm/Kill) — PalacePrivileges patches this |

Common patterns: resolve NetworkId → Entity via the
`NetworkIdSystem._NetworkIdLookupMap` singleton; cancel a vanilla-denied
action with `EntityManager.DestroyEntity(eventEntity)`; we do the inverse —
let an event through (or suppress the vanilla denial) when the container is
in the public registry.

### Two candidate implementation approaches

**Approach A — validation patches (precise, recommended start):** prefix the
systems above; when the target container is registered public (and the
relevant config switch is on), bypass/satisfy the team check for that event
only. Pros: surgical, no persistent game-state mutation, unshare = just stop
allowing. Cons: many systems to cover; risk of missing one path.

**Approach B — team mutation (broad, riskier):** rewrite the container's
`Team`/`TeamReference` to a neutral/global team (Bloodcraft's
`SetTeam` extension pattern, `VExtensions.cs:695`). Pros: every system
(client prediction included) agrees automatically. Cons: mutates persisted
state (restart-stranded if registry lost), unknown interactions with raids/
decay/`CastleHeartConnection`, harder to undo cleanly. → Keep as fallback.

**Client-side caveat (key open question):** if the *client* refuses to even
send the interact attempt on a non-allied container (greyed-out prompt), a
server-side allow never gets exercised. KindredInnkeeper only ever *denies*,
which proves nothing about allowing. POC must test: does a stranger's client
send the open-attempt for a non-allied chest? (During PvP raids enemy
containers ARE openable, so the path plausibly exists server-side; the
`InteractAbilityBuffer` includes a `DisabledDummy` ability that may be the
client-side gate.) If client-blocked → Approach B for the open step
(team-spoof just the `Interactable` surface) or nameplate-level tricks via
`OnlyAllySee`.

### Identifying "the container I mean" + persistence

- Targeting: same `EntityAimData.AimPosition` + closest-entity pattern as
  stair swap (KindredCommands `ServantCommands.cs:18-51`), filtered to
  entities with `InventoryOwner` (chests) or `PrisonCell`/`Prisonstation`
  (cells). Fallback: explicit NetworkId argument.
- **NetworkIds are runtime handles — assume NOT stable across restarts.**
  Registry must key on something durable: prefab GUID + `TilePosition` (+
  castle heart entity's persistent identity) until a stable persistent ID is
  confirmed. Prune entries whose container no longer resolves on load.
- Prison cell identification: `TM_SpecialStation_PrisonCell` GUID
  `-1253061408`; distinctive components `PrisonCell` (`ImprisonedEntity`),
  `Prisonstation` (`HasPrisoner`), `CastleLimited: PrisonCell`,
  `WorkstationRecipesBuffer` (extract/feed recipes),
  `InventoryRouteParent_Outgoing`. 8 inventory slots vs. chest's 14.

## Persistence

`BepInEx/config/Uriel/public_containers.json`: owner steamId, durable
container key (see above), container class (`storage` | `prison`), marked-at
timestamp. Debounced save-on-change + save-on-unload. Prune dead entries on
load. Schema version field from day one.

## Edge cases to handle

- Container destroyed / castle decayed → prune registry entry.
- Owner leaves clan / clan dissolves → "may (un)share" check follows current
  legitimate control (Team match), not the original sharer.
- Raid/PvP: only the team check is relaxed; vanilla proximity/LOS checks
  (`Interactable.IgnoreLineOfSight: False`) remain.
- Sort/split/move-all on a public chest by a stranger — every system in the
  table above must agree, or items get stuck mid-interaction.
- Two containers at the same position after rebuild → durable key must not
  mis-attach the public flag.

## Test plan (v0.2.0 build — run on the live local server)

**The decisive test (validates the whole mechanism):**
- [ ] Owner: aim at own chest, `.uriel share` → confirm reply. Second
      account (different clan/no clan): walk up — does the open prompt
      appear? Can they open, take, put? ← the team-swap hypothesis test.

Then:
- [ ] Sort, split, move-all on the shared chest from the stranger account.
- [ ] `.uriel unshare` → stranger denied again (prompt gone/locked).
- [ ] Owner + clanmate can still use the chest normally WHILE shared.
- [ ] Restart server → share persists (registry re-applied; check log line
      `[Uriel SHARE] re-applied public team ...`).
- [ ] Stranger cannot `.uriel share`/`unshare` someone else's container.
- [ ] `.uriel shared`, `.uriel sharedall`, `.uriel unshareall` outputs sane.
- [ ] Aim at prison cell + `.uriel share` → refused with "coming soon".
- [ ] `PublicStorage.Enabled = false` → commands refuse; restart with it
      false → shares NOT re-applied (containers private).
- [ ] Castle decay/refault behavior: shared chest in decayed castle.
- [ ] Raid scenario: shared chest during breach — no unintended changes.

**If the stranger gets NO open prompt (client gates on something else):**
fallback experiments, in order: (1) also neutralize `UserOwner.Owner`
(store + restore); (2) check `NameableInteractable.OnlyAllySee/OnlyAllyRename`;
(3) the per-system validation-patch table above (Approach A).
