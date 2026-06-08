# Feature: Castle Rentals (Coffin & Room "Hotel")

**Status:** PLANNED & APPROVED (2026-06-08). No code yet — **Phase 0 research
spikes are the next step**. This doc captures the agreed vision, the (approved)
refined command grammar, an honest feasibility map, and a phased plan. Inspiration (technique only — original code, AGPL-aware): odjit's
**KindredInnkeeper** (room claiming, coffin/chest gating, `BindCoffinSystem`
patch) and cheesasaurus's **PalacePrivileges** (per-feature castle access
grants). Uriel's differentiators: **rentals work without clan membership**, and
a **payment + timed-lease system** reusing the public-storage cost model.

## Concept

A host turns part of their castle into rentable lodging. A guest **pays to rent
a coffin or a room** for a fixed period → gains **timed tenant status** → which
grants **a spawn point (the coffin)** and **access to designated storage** →
expiry (plus a grace window) **evicts** them (force-teleport out) and **returns
their left-behind items** to the host's private storage. Castle-as-hotel.

## Owner requirements (non-negotiables)

1. **No clan membership.** Renters are never added to the host's clan/team.
   Coffins are publicly *rentable*, then *privately held* for the lease.
2. **Spawn point.** A rented coffin should act as the renter's respawn point
   while the lease is active (achieved by respawn redirection, not clan binding).
3. **Same economics as shared storage.** The host sets item + amount as the
   price (or leaves it free), exactly like `.uriel share … cost`.
4. **Grace period.** Host-set (e.g. `LimitHrs 24 GracePeriodHrs 1`). After
   lease + grace, force-teleport the renter to the nearest waypoint.
5. **Item return on expiry.** Items the renter left in tenant storage transfer
   to the host's available private castle storage.
6. **Scoped occupancy.** A coffin/room can hold one active renter; storage
   access is bounded by scope (below).

## Storage access — three scopes (host chooses)

1. **Room-associated** *(private/per-room)* — storage tied to a specific room is
   usable only by that room's current renter. Whitelist, narrowest.
2. **Renter-gated public** *(any current renter)* — storage shared with a
   "renters" modifier: any active renter in the castle may use it. A new
   audience on the existing `.uriel share`.
3. **Castle-wide blacklist** — one toggle opens *all* castle storage to renters
   **except** chests the owner flags private (`.uriel keepprivate`). Blacklist,
   broadest. (Builds on roadmap #5 bulk-share + the keepprivate toggle.)

All three are enforced the same way: in Uriel's existing item-move patch
(`InventoryMovePatches`, which already reads the acting player's `FromCharacter`),
allow/deny the move by checking the mover against the chest's scope + the live
tenant list. No new engine surface for the storage half. ✅ verified.

## Refined command grammar

Modifiers **stack in any order** like `.uriel share` (VCF 0.10 has no remainder
parser, so they're parsed as tokens). Names in **bold**, with the owner's
originals noted. **Naming decision (approved):** keep `sharecoffin`/`shareroom`
(not `rentout`) — many hosts will **share coffins/rooms freely**, not only for
profit, so "share" is the right verb (a price is just an optional modifier).

### Host — offer for rent
| Refined | Does | Owner's original |
|---|---|---|
| **`.uriel sharecoffin [hours <h>] [grace <h>] [price <itemId> <amt>] [max <n>]`** | List the aimed coffin for rent (free if no `price`; `max` = how many periods may be stacked in one rental) | #1, #2 (`ShareCoffin … TimelimitHrs/Price/GracePeriodHrs/StackLimit`) |
| **`.uriel shareroom [same modifiers]`** | List the aimed **room** (the room its door bounds) — bundles the room's coffin + storage | #6, #7 (`ShareRoom …`) |
| **`.uriel share room <n>`** | Associate the aimed chest with room `<n>` (room-renter only) — scope 1 | #8 |
| **`.uriel sharecoffin room <n>`** | Tie the aimed coffin to room `<n>` so renting the room grants it | #9 |
| **`.uriel share renters`** | Modifier on the existing share: gate the aimed chest to any current renter — scope 2 | #10 (`Share Rentals`) |
| **`.uriel castlerentals on\|off`** | Castle-wide: open all storage to renters except `keepprivate` chests — scope 3 | (new, implied) |
| **`.uriel keepprivate`** | Flag the aimed chest private (excluded from bulk/castle-wide) | (roadmap #5) |
| **`.uriel unsharecoffin` / `.uriel unshareroom`** | Stop offering for rent | (implied) |

### Guest — rent & inspect
| Refined | Does | Owner's original |
|---|---|---|
| **`.uriel rent [x <n>]`** | Rent the aimed/nearest available coffin or room for `n` periods (≤ `max`); pays `price × n` | #3 (`rent Coffin x 2`) |
| **`.uriel rentinfo`** | Terms + status of the aimed rental | #4 (`rent Coffin Info`) |
| **`.uriel rentals`** | List available rentals server-wide (castle owner + area) | #5 (`rent Coffin Availability`) |
| **`.uriel myrentals`** | What I'm renting + time remaining | (new) |
| **`.uriel endrental`** | Vacate early (frees the coffin/room) | (new) |

### Owner / Admin — manage
| Refined | Does | Owner's original |
|---|---|---|
| **`.uriel evict`** (aimed) / **`.uriel evict room <n>`** | Evict the renter; force-teleport to nearest waypoint | #11 (`Owner Evict Room 1`) |
| **`.uriel evictplayer <name\|id> [room <n>]`** *(admin)* | Admin-forced eviction | #12 (`Admin Evict Player …`) |

## Rental lifecycle

1. **Offer** — host `sharecoffin`/`shareroom` with terms (price, hours, grace,
   max periods). Stored in a rentable-listing registry (prefab + tile + room id
   + terms), persisted JSON like the share registry.
2. **Rent** — guest `rent [x n]`; mod verifies availability + funds, charges via
   the existing cost/pay-chest routing, writes an **active lease** (renter
   steamId, listing, start + expiry + grace timestamps), and (a) sets their
   respawn redirect to the coffin, (b) adds them to the tenant list that the
   move-patch checks.
3. **Active** — renter spawns at the coffin (respawn redirect) and uses
   in-scope storage. A coffin/room holds one lease (occupancy scope).
4. **Expiry + grace** — `Tick`-driven timer; at lease end the renter loses
   storage access; at lease+grace they're force-teleported to the nearest
   waypoint, their tenant-storage items transfer to the host's private storage,
   and the listing frees up.
5. **Eviction** — owner/admin `evict` ends a lease early (same teardown).

## Feasibility map (honest)

**🟢 Solved / high-confidence (reuses existing Uriel infrastructure):**
- Renter/tenant-gated storage moves — the move-patch already has the actor's
  identity. All 3 scopes are checks in `HandleMoveEvent`.
- Payment + pay-chest routing, cost model, persistence, the `Tick` expiry timer,
  listing/lease registries, info/availability commands.
- Occupancy (one lease per coffin) — registry-enforced.

**🟡 Needs a research spike (primitive exists; integration unverified):**
- **Respawn redirect without clan** — verified primitive:
  `ServerBootstrapSystem.RespawnCharacter(customSpawnLocation=…)` (used by
  KindredInnkeeper). Spike: find the death→respawn interception point so a
  renter respawns at their coffin (and is teleported there on rent/login).
  *Avoids the clan route and the `VerifyRespawnPointConnectionsSystem` team
  reconciler that prunes forged bindings.*
- **Room enumeration via door** — coffins carry `CastleRoomConnection`; the game
  has a `CastleRoom` concept. Spike: map an aimed door → its room → the
  coffin/chests inside it (KindredInnkeeper does room ownership this way).
- **Force-teleport on expiry + item return** — teleport via the same respawn/
  teleport API; item transfer reuses our inventory-move patterns.

**🔴 Hard / risk-flagged (may slip to a later phase or accept a v1 limit):**
- **Door access control without clan** — making a room's *physical door* lock to
  non-renters and open for the renter. Doors are gated client-side; PalacePrivileges
  does door grants but **clan-scoped**. Non-clan, per-player door control likely
  needs an interaction/ability patch and may not be cleanly doable. **v1 fallback:**
  rooms still function (spawn + scoped storage gated server-side), but the door
  itself may not physically lock to outsiders — i.e. a non-renter could walk in,
  though they couldn't *take* from gated storage. This matters most on PvP. To be
  validated in the spike before promising "locked private room."

## Phased plan

- **Phase 0 — spikes:** respawn-redirect hook, room/door enumeration, door-access
  feasibility. Decide v1 door behavior.
- **Phase 1 — tenant-gated storage** (the 3 scopes) + `keepprivate` + bulk-share
  (#5). Pure reuse; testable without any rental concept (gate to a manual list).
- **Phase 2 — rental core:** listings + leases + payment + `rent`/`rentinfo`/
  `rentals`/`myrentals`/`endrental`, expiry+grace timer, item return, eviction.
  Wires the tenant list from Phase 1 to real leases.
- **Phase 3 — spawn:** respawn-redirect to the rented coffin + teleport-on-rent.
- **Phase 4 — rooms & doors:** `shareroom`, room enumeration, and (if the spike
  says yes) physical door locking. Otherwise ship rooms as storage+spawn bundles.

## Decisions (resolved 2026-06-08) & remaining risk

**Resolved:**
- **Naming:** keep `sharecoffin`/`shareroom` — free sharing is a first-class use,
  so "share" (with optional `price`) is correct.
- **Eviction target:** force-teleport the evicted/expired renter to the
  **nearest waypoint**.
- **No clan membership:** spawn is achieved by **respawn redirection**
  (`RespawnCharacter(customSpawnLocation)`), never clan/team binding.
- **Free rentals:** a free `sharecoffin`/`shareroom` still creates a lease (for
  occupancy + expiry + spawn), just with no charge.
- **Grammar & lifecycle:** approved as written above.

**Remaining open risk (decide AFTER the Phase 0 spike):**
- **Door locking:** if the spike shows non-clan physical door locking isn't
  cleanly doable, v1 ships rooms as **server-side storage + spawn gating**
  (a non-renter could physically walk in but can't take from gated storage);
  real door locking becomes a later enhancement. Decide once the spike reports.
- **Respawn override scope (lean):** while renting, the coffin *replaces* the
  renter's active spawn; reverts on lease end. (Confirm during Phase 3.)

## References
- KindredInnkeeper (odjit, AGPL-3.0) — room claim/teardown, `BindCoffinSystem`
  patch, `RespawnCharacter(customSpawnLocation)` usage. Technique reference only.
- PalacePrivileges (cheesasaurus) — per-feature castle access grants (clan-scoped).
- Uriel `PublicStorageService` / `InventoryMovePatches` — the cost model, share
  registry, move-event gating this feature extends.
