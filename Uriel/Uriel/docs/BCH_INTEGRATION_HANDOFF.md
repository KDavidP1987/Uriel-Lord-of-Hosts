# Uriel → BloodCraftHub (BCH) Integration Handoff

> **Purpose.** The single reference for everything **BloodCraftHub (the
> client-side companion mod)** needs to build to surface and extend **Uriel,
> Lord of Hosts**. Authored and maintained in the **Uriel** workspace
> (server-side mod); carry it to the BCH workspace when you switch projects
> and keep building from it there.
>
> **Status legend:** ✅ shipped in Uriel · 🟡 contract present, BCH UI pending ·
> 🔬 experimental / pending live validation · ⛔ not possible server-side
> (BCH-only path) · 📋 planned, not yet implemented in Uriel.
>
> **Cross-workspace boundary.** BCH lives at
> `C:\Users\KDPen\OneDrive\Documents\CURSOR PROJECTS\Games\V Rising\BloodCraftUI 2\`.
> The two repos never cross-edit (same rule as Beelzebub↔BCH). All integration
> flows through the **chat-command API** below — exactly how BCH already talks
> to Bloodcraft and Beelzebub. This doc is the contract; implementation
> happens in the BCH workspace.
>
> **Canonical source of truth:** `Uriel/Uriel/Commands/*.cs`. Uriel has **no
> machine-readable `api` command yet** (see §6) — current replies are
> human-facing chat text. If this doc and the code disagree, the code wins and
> this doc gets corrected.

**Current server build: Uriel v0.10.0 (2026-06-07). No ApiVersion yet — the
machine wire API is 📋 planned; BCH should tell Uriel which endpoints it wants
first.**

---

## 1. What Uriel is (one paragraph)

Server-side BepInEx umbrella mod ("a host of capabilities"), sibling of
Beelzebub. Live features: **per-container public storage** (share a specific
chest with everyone, with optional permissions / withdrawal limits / access
costs paid to the owner), **public prison cells** (feed/extract for everyone +
command-based prisoner takeover), and **stair hot-swap** (restyle placed
stairs in place, same-shape cosmetics only, DLC-gated per user). Every
feature has an admin config kill-switch. Repo:
github.com/KDavidP1987/Uriel-Lord-of-Hosts.

## 2. The mechanism BCH must understand (client-visible state!)

A SHARED container/cell is made public by **replicated state** — which means
**BCH can detect shared containers client-side without any server query**:

| Replicated marker | Private container | SHARED container |
|---|---|---|
| `Team.Value` | castle team value | **NeutralTeam value (1 on the dev server)** |
| `TeamReference.Value` | castle team entity | **the NeutralTeam singleton entity** |
| `CastleHeartConnection.CastleHeartEntity` | the castle heart | **Entity.Null (severed)** |

The robust client-side check: *container has `InventoryOwner` (or
`Prisonstation`) AND `CastleHeartConnection.CastleHeartEntity == Entity.Null`
AND its `TeamReference` resolves to an entity carrying the `NeutralTeam`
component.* World chests have no `CastleHeartConnection` at all — placed
castle containers always have the component (just nulled while shared), so
the component-present-but-null shape is uniquely "Uriel-shared".

⚠️ v0.9.0+: sharing/unsharing STORAGE **rebuilds the entity** (new entity id;
contents transferred) — BCH must not cache container entity ids across share
state changes. Prison cells keep their entity (mutate + a brief
Disabled-blink). Boot re-applies shared state before clients join.

## 3. Player command surface (all ✅ shipped, replies are human text)

```
.uriel                            overview
.uriel share                      share the AIMED container (auto-detects chest vs prison cell)
.uriel share permission <take|give|givetake>
.uriel share limithours <h>
.uriel share limitwithdrawal <n>
.uriel share cost <itemId> <amount>      (0 0 = free)
   ↳ modifiers STACK in one command, any order; ALL validated before ANY apply
.uriel unshare                    revert the aimed container
.uriel unsharemine                bulk-revert everything I shared/control
.uriel shared                     list MY shares with full rules
.uriel info                       rules of the aimed container (works for anyone)
.uriel paychest                   designate aimed PRIVATE general chest for cost payments
.uriel finditem <name>            item-name → numeric id search (≤8 results)
.uriel takeprisoner               take the prisoner from the aimed SHARED cell (subdued, follows taker)
.uriel stairswap <style|next>     restyle aimed stair (stone1|stone2|stone3|gloomrot|projectk|strongblade)
.uriel stairstyles                aimed stair's shape + current style + owned styles
```

Admin: `.uriel sharedall [name|steamId]` · `.uriel unshareplayer <name|steamId>`
· `.uriel unshareall` · `.uriel sharedebug` (state dump) · admins may aim+
share/unshare/modify ANY container.

**Targeting model:** every aimed command resolves the closest qualifying
entity to `EntityAimData.AimPosition` within config range (storage 5m,
stairs 6m). BCH buttons should ensure the player is looking at the target
when relaying (same UX BCH uses for Beelzebub aim commands).

**Reply shapes BCH may parse today (stable-ish, but human text — push Uriel
for the `[URIEL:*]` API before building heavy parsers):**
- `.uriel info` → `"<PrefabName>: PUBLIC [<permission>], limit N stack(s)/Hh per player, cost N× <Item> per stack. Shared by <steamId> since <utc>."` or `"<PrefabName>: private (not shared)..."`
- `.uriel shared` → numbered lines `"<n>. <PrefabName> @(<x>,<y>) [<class>] <permission>, limit …, cost …"`
- `.uriel stairstyles` → header `"Stair: <Archetype>, current style '<style>'. Available:"` then one line per style, `" — locked (<DLC>)"` suffix when unowned.
- `.uriel finditem` → lines `"Item_… → <guid>"`.
- Denials/notifications arrive as system chat prefixed `[Uriel]` (move-event
  denials, cost receipts `"[Uriel] Paid N× <item> for this withdrawal."`).

## 4. ⭐ BCH-side opportunities (why this handoff exists)

1. **⛔→BCH: the prison SUBDUE button.** Research verdict (2026-06-07): the
   native subdue/kill buttons are hard-gated in CLIENT code by team match —
   feed/extract are inventory-recipe surfaces (show up with shared access),
   subdue is an `InteractWithPrisonerEvent` the client only offers to the
   cell's castle team; PalacePrivileges' anti-cheat even flags cross-team
   charm events. **No server-side state can reveal the button — but BCH is
   client-side.** BCH can render a **"Take Prisoner" button** when the local
   player views a shared cell (detect via §2 markers + `Prisonstation` +
   `PrisonCell.ImprisonedEntity != null`) that relays `.uriel takeprisoner`.
   This closes the only UX gap in prison sharing. Remind the player to have
   Dominating Presence ready (the reply text does too).
2. **🟡 Share-management panel.** Owner aims at a container → BCH panel:
   share/unshare toggle, permission radio (take/give/givetake), limit
   sliders (stacks + hours), cost row (item picker + amount), paychest
   setter. All writes are single chat commands; current state read from
   `.uriel info` (or client-side §2 detection + future api).
3. **🟡 Stair style picker.** Aim at stairs → BCH radial/panel of the six
   styles for that archetype (parse `.uriel stairstyles` for current/owned;
   grey out ` — locked` entries) → `.uriel stairswap <style>`.
4. **🟡 Item picker for costs.** BCH-side searchable item browser feeding
   `cost <id> <amt>` (BCH has client prefab access; or relay
   `.uriel finditem`).
5. **🟡 Shared-container badges.** World-space or tooltip badge "PUBLIC
   (take-only, 2/24h, costs 5× Fish)" on shared containers — detection fully
   client-side via §2; rules text via `.uriel info` on demand.
6. **🔬 Charm escort timer.** After `takeprisoner`, the prisoner carries
   `AB_Charm_Active_Human_Buff` (GUID 1303169868) owned by the taker — BCH
   could show the remaining charm duration so they get the prisoner home in
   time.

## 5. Feature state & caveats BCH must respect

| Area | Status | Caveats for BCH |
|---|---|---|
| Chest share/unshare | 🔬 v0.9.0 rebuild pending live validation | entity id changes on share/unshare; contents preserved |
| Policies (permission/limits/cost) | 🔬 enforced server-side in move events | denials arrive as `[Uriel]` system chat — BCH can surface them as toasts |
| Pay chest + payment routing | 🔬 | payments only to general (unrestricted) storage |
| Prison share (feed/extract) | ✅ validated live | inventory surfaces appear natively once shared |
| Prison subdue via UI | ⛔ client-gated | **BCH button → `.uriel takeprisoner`** (§4.1) |
| `.uriel takeprisoner` | 🔬 v0.10.0, two-layer (vanilla event → manual charm) | untested live; log says which layer fired |
| Stair swap | 🔬 v0.9.0 transform fix pending validation | swapped stair = NEW entity; pre-v0.9.0 stuck stairs repaired by re-swapping |
| Config kill-switches | ✅ | `PublicStorage.Enabled`, `PublicStorage.PrisonEnabled`, `StairSwap.Enabled` — commands reply "disabled by the server admin"; BCH should hide UI on that reply |

## 6. 📋 Planned machine wire API (implement in Uriel BEFORE BCH parses)

Mirroring the Beelzebub `[BEELZ:*]` pattern: an `.uriel api …` command group
emitting `[URIEL:…]` lines, gated by `ApiVersion` (starts at 1), chunked like
Beelzebub's for long replies. Proposed endpoints — **BCH: rank these and
Uriel will implement in that order, bumping this doc per the change rule:**

```
.uriel api version          → [URIEL:version] api=1 plugin=0.10.0 ready=1 storage=1 prison=1 stairs=1
.uriel api shares <page>    → [URIEL:shares] page=1/1 n=3
                              [URIEL:share] guid=<prefab> tile=<x>,<y> class=storage perm=givetake limit=2/24h cost=<itemguid>x5 by=<steamId>
.uriel api info             → single [URIEL:share] row for the aimed container (or [URIEL:share] none=1)
.uriel api stairinfo        → [URIEL:stair] archetype=Single_CW current=stone2 owned=stone1,stone2,stone3 locked=gloomrot:DLC_Gloomrot,…
.uriel api items <page> <search> → [URIEL:item] name=… guid=…
```

Capability gating: `api>=N` per endpoint once versions move. Sentinels: `-`
unknown, `0/1` booleans (Beelzebub conventions).

## 7. Change discipline (the living-contract rule)

Same rule as Beelzebub's handoff: **whenever Uriel work changes anything
BCH-facing — a chat command, a reply format BCH parses, the §2 replicated
markers, a config key, or a `[URIEL:*]` line — update THIS doc in the same
commit.** When the wire API lands, its `ApiVersion` lives in
`Commands/ApiCommands.cs` and the banner at the top of this doc must state
it. Catch-up banners (⭐ style) go at the top when BCH has been away for
several Uriel versions.
