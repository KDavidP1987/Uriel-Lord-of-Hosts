# Feature: Public Storage (per-container opt-in)

**Status:** IMPLEMENTED v0.3.0 (storage class + policy modifiers) — open/take
VALIDATED live (v0.2.1, 2026-06-06); deposits + policies pending validation

## Live validation results (v0.2.1, 2026-06-06)

Two-player test confirmed the team-swap mechanism: a non-clan player got the
open prompt on a shared chest, could withdraw, and could put back items
originating from the chest. **But could NOT deposit his own items** — vanilla
refuses deposits into neutral-team containers (world-chest semantics: world
loot chests are take-only). v0.3.0 addresses this with a
`MoveItemBetweenInventoriesSystem` patch that executes permitted deposits
manually and cancels the vanilla event.

## Policy modifiers (v0.3.0)

Per-entry policy enforced in the move-event patch (owners/clan bypass all):

- **Permission** `take | give | givetake` — withdrawals denied on `give`
  (donation box); deposits denied on `take`.
- **Withdrawal limit** — N stacks per rolling H-hour window per player
  (`LimitWithdrawStacks` + `LimitHours`; setting one defaults the other to
  1 stack / 24 h). Usage records persisted per steamId; a "stack" = one move
  event (partial-stack drags count as one).
- **Cost** — `CostItemGuid` × `CostAmount` charged per stack withdrawn,
  auto-collected from the taker's inventory. Item ids discoverable via
  `.uriel finditem` (runtime `Item_*` catalog from PrefabCollectionSystem).
  **Payment delivery (v0.4.0) is capacity- and restriction-safe:** the
  destination must fit the ENTIRE amount before payment is collected
  (`CountFit`: empty slots × `ItemData.MaxAmount` + same-item headroom), and
  specialized stashes (`InventoryInstanceElement.RestrictedCategory/
  RestrictedType != 0`) are never used. Cascade: designated pay chest
  (`.uriel paychest`, general storage only — enforced at designation) → the
  shared container itself → nearest general non-shared storage on the same
  castle heart → graceful deny (taker keeps payment). No partial transfers,
  no duplication, no forced wrong-type items.
- **Stacked modifiers (v0.4.0)** — `.uriel share` accepts all modifiers in
  one command, any order, fully validated before any are applied. VCF 0.10.x
  lacks `[Remainder]` (added in 0.11), so the share command takes 10 optional
  string tokens and parses them by hand.
- **Move-all** ("take all") is blocked for non-controllers on any restricted
  container — it can't be accounted per stack.

Registry schema v2 (v1 files migrate implicitly via JSON defaults): entries
gain policy fields + per-player usage; file gains a `PayChests` map
(owner steamId → private container key).
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

## Test plan

**Validated (v0.2.1, 2026-06-06):**
- [x] Stranger gets open prompt on shared chest (team-swap works).
- [x] Stranger can withdraw.
- [x] Stranger can put back chest-originated items.
- [x] Prison cell share refused with "coming soon".
- [x] Stranger deposits of own items → vanilla-refused (fixed in v0.3.0,
      revalidate below).

**v0.3.0 validation (run on the live local server):**
- [ ] Stranger can now DEPOSIT own items into a givetake/give chest.
- [ ] Owner can deposit into their own shared chest (was also affected).
- [ ] `permission take` → stranger deposit denied with message.
- [ ] `permission give` → stranger withdrawal denied with message.
- [ ] `limitwithdrawal 2` + `limithours 1` → 3rd stack within the hour
      denied with remaining-time message; works again after window.
- [ ] `cost <id> <amt>` → withdrawal auto-charges; payment lands in
      `.uriel paychest` chest (and in the shared chest when no paychest);
      insufficient funds → denied with message; taker sees "Paid X×..." line.
- [ ] `.uriel finditem blood` style searches return sane id lists.
- [ ] `.uriel info` shows the rules from a stranger account.
- [ ] Move-all ("take all") on a restricted chest → blocked with message;
      on an unrestricted chest → vanilla take-all still works.
- [ ] `.uriel unshare` → stranger denied again (prompt gone/locked).
- [ ] Restart server → share + policies + usage windows persist.
- [ ] `PublicStorage.Enabled = false` → commands refuse; patches inert.

**Still to verify sometime:** sort/split paths from stranger account, castle
decay behavior, raid-breach interaction.
