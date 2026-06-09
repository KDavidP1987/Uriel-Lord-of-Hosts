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
> **Canonical source of truth:** `Uriel/Uriel/Commands/*.cs`. The machine wire
> API now EXISTS for object spawning (`Commands/ApiCommands.cs`, `[CommandGroup("uriel api")]`)
> — see §6. Other surfaces (shares/stairs) are still human-facing chat text. If
> this doc and the code disagree, the code wins and this doc gets corrected.

**⭐ Catch-up: the `[URIEL:*]` wire API is LIVE at ApiVersion 1 — covering the new
Object Spawning feature: `.uriel api version` / `catalog` / `unlocked`. This exposes
the total in-game prefab catalog and each player's unlocked-prefab collection (the
"Uriel list" BCH wants), analogous to Beelzebub's ability-catalog API. Shares/stairs
endpoints remain 📋 planned. Object Spawning itself (Phase 1–2) is built but not yet
released.**

**🐞 FIXED 2026-06-08 — `api catalog`/`api unlocked` returned nothing (BCH P0).**
The two paged commands built their whole page (header + rows + `[URIEL:end]`) as a
single `\n`-joined string and sent it in **one** `ctx.Reply`. But a System-chat
message is **one wire line** — BCH's reader (`UrielProtocolService.HandleLine` →
`UrielWireParser.Parse`) and the proven Beelzebub pattern do NOT split on `\n`, so
only the first line (the `catalog`/`unlocked` header) was parsed and every
`[URIEL:object]`/`[URIEL:end]` row was lost → "no rows" → BCH's 6s scan timeout.
`api version` worked only because it is a single line. **Fix (Uriel-side only, no BCH
change):** each `[URIEL:*]` line is now emitted via its **own** `ctx.Reply`, exactly
like Beelzebub emits one reply per `[BEELZ:*]` line. **No wire-shape change → ApiVersion
stays 1**; the line grammar is identical, BCH already handles one-line-per-message and
arbitrary page counts. Page size also went 3→20 rows (the old "3/page" only existed to
fit a whole page under the 512-byte cap, which no longer applies per-line).

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
.uriel stairswap <style|next>     restyle aimed stair LIVE (stone1|stone2|stone3|gloomrot|projectk|strongblade) — rebuilds as a NEW entity
.uriel removestairs               cleanly delete the aimed staircase (leaves connected floors/walls intact)
.uriel stairstyles                aimed stair's shape + current style + owned styles
```

Admin: `.uriel sharedall [name|steamId]` · `.uriel unshareplayer <name|steamId>`
· `.uriel unshareall` · `.uriel sharedebug` (state dump) · admins may aim+
share/unshare/modify ANY container.

**Targeting model (v0.11.0 — built FOR BCH buttons):** two modes:
- **Default (aim):** closest qualifying entity to `EntityAimData.AimPosition`
  within config range (storage 5m, stairs 6m); if nothing is at the aim
  point, automatically falls back to nearest-to-player.
- **`nearest` (deterministic — BCH buttons MUST use this):** closest entity to
  the PLAYER, ignoring aim entirely. Clicking a UI panel leaves the aim ray
  pointing anywhere (possibly at a DIFFERENT container behind the UI), so UI
  relays should always append the nearest token:
  `share` takes `nearest` as a stackable token anywhere in the modifier list
  (`.uriel share nearest permission take`); `unshare` / `info` / `paychest` /
  `takeprisoner` / `stairstyles` take it as a trailing arg
  (`.uriel unshare nearest`); `stairswap` takes it after the style
  (`.uriel stairswap stone2 nearest`).
  BCH UX tip: after a `nearest` relay, the reply names the affected container/
  stair — surface it so the player can confirm the right object was hit.

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
7. **✅ live stair-swap visuals — SOLVED server-side (v0.14.x); no BCH
   re-render needed.** Earlier this doc asked BCH to re-render swapped stairs
   because the in-place identity swap only showed the new look after a server
   restart. That is **obsolete**: the prior "MegaStatic bake" diagnosis was
   wrong (placed stairs are ordinary `NetworkId.Type=Normal` entities), and
   `.uriel stairswap` now **destroys the fused staircase and respawns it in the
   new style** with fresh NetworkIds, so it renders **live** with no client
   help. **The one thing BCH MUST respect: a stair swap now produces a NEW
   entity** (new id for the root and every fused child). BCH must NOT cache a
   stair entity id across a swap — re-resolve by aim/position after any
   `stairswap`. Stair-root detection is unchanged: `CastleBuildingFusedRoot` +
   prefab name `BP_Castle_Stairs_*`.

## 5. Feature state & caveats BCH must respect

| Area | Status | Caveats for BCH |
|---|---|---|
| Chest share/unshare | 🔬 v0.9.0 rebuild pending live validation | entity id changes on share/unshare; contents preserved |
| Policies (permission/limits/cost) | 🔬 enforced server-side in move events | denials arrive as `[Uriel]` system chat — BCH can surface them as toasts |
| Pay chest + payment routing | 🔬 | payments only to general (unrestricted) storage |
| Prison share (feed/extract) | ✅ validated live | inventory surfaces appear natively once shared |
| Prison subdue via UI | ⛔ client-gated | **BCH button → `.uriel takeprisoner`** (§4.1) |
| `.uriel takeprisoner` | 🔬 v0.10.0, two-layer (vanilla event → manual charm) | untested live; log says which layer fired |
| Stair swap | ✅ v0.14.x destroy+respawn, applies LIVE (validated straight/curved/wide) | swapped stair = NEW entity (root + all fused children) — re-resolve by position after a swap; never cache stair ids |
| Config kill-switches | ✅ | `PublicStorage.Enabled`, `PublicStorage.PrisonEnabled`, `StairSwap.Enabled` — commands reply "disabled by the server admin"; BCH should hide UI on that reply |

## 6. Machine wire API (`[CommandGroup("uriel api")]`, ApiVersion 1)

Mirroring the Beelzebub `[BEELZ:*]` pattern. Every line begins `[URIEL:<tag>]` +
space-separated `key=value` tokens (bare; prefab names are `[A-Za-z0-9_]`). **Each line
is its own chat message / `ctx.Reply` — a list page is a HEADER line, then one
`[URIEL:object]` line per row, then an `[URIEL:end]` line** (BCH reads one line per
message; it does NOT split on `\n`). Pages terminate with
`[URIEL:end] cmd=<name> page=<x>/<y> count=<n>`. Each individual line stays under VCF's
509-char reply cap. Sentinels: `-` unknown, `0/1` booleans.

### ✅ Implemented (ApiVersion 1) — Object Spawning collection
```
.uriel api version          → [URIEL:version] api=1 plugin=<ver> ready=1 objectspawn=1 adminonly=0|1
                              collection=0|1 mode=Discovery|Full chance=<0-100> total=<N> discoverable=<D> blocked=<B>
.uriel api catalog <page>   → [URIEL:catalog] page=1/Y total=<N> discoverable=<D>
                              [URIEL:object] guid=<int> disc=0|1 label=<token> cat=<category>   (≤20/page, each its own line)
                              [URIEL:end] cmd=catalog page=1/Y count=<n>
.uriel api unlocked <page>  → [URIEL:unlocked] page=1/Y steam=<id> n=<count> discoverable=<D> pct=<%>
                              [URIEL:object] guid=<int> disc=0|1 label=<token> cat=<category>   (≤20/page, each its own line; SELF only)
                              [URIEL:end] cmd=unlocked page=1/Y count=<n>
```
**`[URIEL:object]` fields:** `guid` = the spawn id (BCH fires `.uriel spawn <guid>`); `disc` =
1 if discoverable-by-destruction; `label` = a humanized display name, **wire-safe (spaces→`_`)** —
reverse `_`→space for display, or ignore it and resolve the true localized name + icon client-side
by `guid`; `cat` = a coarse category for grouping + fallback icons, one of
`container|plant|ore|breakable|light|furniture|resource|buildable|decor|other` (treat unknown as
`decor`). The raw dev `name=` was intentionally dropped — BCH resolves it from `guid` client-side
(same place it gets the icon), which keeps each line short and the page compact.

- `catalog` = the **total prefab list available in-game** (placeable WORLD objects; excludes units,
  abilities, internals, admin-blocked, and — unless `IncludeCastleBuildables=true` — castle build pieces).
- `unlocked` = the **calling player's** unlocked prefabs + collection `pct` (of the discoverable set;
  blocked/invalid never counted). Self-scoped; admins read others via chat `.uriel unlocks <player>`.
- `version`: `collection` master on/off; `mode`/`chance` = discovery rules; `blocked` = blocklist size.
  Errors: `[URIEL:err] code=notready|disabled`.

**⚠️ Transport guidance (important — the chat channel is one wire line per message, ≤20 objects/page):**
- For the **player's spawn menu, page `unlocked`** — it's what they can actually build and is small
  (grows with play). Don't enumerate the whole `catalog` to build the menu.
- For **collection progress** (e.g. "812 / 1500 = 54%"), read the counts from `version` (`total`,
  `discoverable`) and `unlocked` (`pct`, `n`) — you do NOT need to page every object.
- Only page the full `catalog` for an optional "everything that exists to collect" browse, and
  **cache it aggressively** (it can be hundreds of pages). It only changes on server config/blocklist
  changes — pull once per session.

- Player-facing chat equivalents (non-API): `.uriel catalog [page]`, `.uriel unlocks`,
  `.uriel notify <on|off>` (per-player message suppression). Admin: `.uriel block`/`unblock`/
  `blocklist`, `.uriel grant`/`revoke`/`grantall`, `.uriel bossmap`.
- Object MANAGEMENT (placed objects, plain-text replies — not `[URIEL:*]`): `.uriel despawn`/
  `move`/`rotate`/`spawninfo`/`spawnlist` (player, own plot), `.uriel purgeplot` (admin). These
  re-resolve the target from Uriel's spawned-object registry on demand, so they work across
  relog/restart. `.uriel purgeplot` removes Uriel's objects on the plot (in-session live-spawn marker
  + persistent records); `.uriel forcepurgeplot` is now a SYNONYM for it (the old `SpawnChainChild`
  chain sweep was removed — that component is the game's resource-respawn marker, and the sweep deleted
  native resources). Both touch only Uriel's objects — native objects/plants/build pieces are never
  removed. `.uriel purgeorphans` (admin) is a server-wide sweep that removes tracked objects no
  longer governed by a living castle heart (castle destroyed / open world). Admin record-IGNORING
  per-object recovery: `.uriel forcedespawn [confirm]` (arm → names the prefab → confirm within
  30s). All reply in plain text; none emit `[URIEL:*]` wire lines
  (no BCH parsing required).

### 🎨 Object palette UI: rendering icons/previews (server can't ship images)

Architectural guidance for a BCH "spawnable objects" panel:

- **The vanilla build menu can't host these.** Uriel proved (Phase 1) that making a world object
  build-menu-SELECTABLE needs castle-heart placement REGISTRATION that component grafting can't
  reproduce. So BCH builds its **own palette window**, not an injection into the vanilla menu. Click →
  BCH sends `.uriel spawn <guid>` (server executes; it has the ECS authority to instantiate).
- **The dedicated server has NO images to send.** It's headless — it never loads sprites/textures/
  meshes, and the icon reference isn't even in the server-side ECS data (verified: a buildable's dump
  has no icon field; UI icons live in the CLIENT's managed-asset/icon registry, keyed by PrefabGUID).
  So Uriel passes only the **PrefabGUID** (already in `[URIEL:object] guid=…`); BCH resolves visuals
  **client-side**.
- **Icons by GUID, client-side:** the game registers UI icons only for things shown in UI — inventory
  items, abilities, and **build-menu buildables**. So:
  - Castle buildables (excluded by default via `IncludeCastleBuildables`) DO have a resolvable
    build-menu icon BCH can look up by GUID.
  - **World objects — the feature's focus — have NO vanilla UI icon** (they never appear in a menu).
- **For world objects, BCH's realistic options (all client-side):**
  1. **Live 3D preview render** — the CLIENT has the model assets (it renders the spawned object in
     world), so BCH can instantiate the prefab model into a render-texture "preview camera" and show
     that thumbnail. Highest fidelity; the only way to get a true picture of an icon-less world object.
  2. **Category/fallback icons** — BCH ships a small set (chest/resource/tree/urn/decor) keyed off the
     name or a category hint.
  3. **Name + metadata only** (humanized label + disc/tier flags).
- **What Uriel can add to help (ask if wanted):** the API can enrich `[URIEL:object]` with a humanized
  `label=` and a `cat=` category tag (chest/resource/decor/…) to drive fallback icons + grouping —
  Beelzebub-style additive fields. Uriel CANNOT provide the image itself (no assets server-side).

### 📋 Still planned — BCH-ranked implementation order (BCH request 2026-06-08)
BCH wants all three; implement **in this order** (each retires a fragile human-text regex
parser on the BCH side once it lands; gate each with `api>=N`):
```
1. .uriel api info          → single [URIEL:share] row for the aimed/nearest container (or [URIEL:share] none=1).
                              HIGHEST VALUE — powers the Storage tab's live "current rules" panel, retires the
                              `.uriel info` regex. Shape: [URIEL:share] guid=<prefab> tile=<x>,<y> class=storage
                              perm=givetake limit=2/24h cost=<itemguid>x5 by=<steamId>
2. .uriel api shares <page> → paged [URIEL:share] rows (same shape). Powers a "My Shares" list, retires the
                              `.uriel shared` numbered-text parse.
3. .uriel api stairinfo     → [URIEL:stair] archetype=Single_CW current=stone2 owned=stone1,stone2,stone3
                              locked=gloomrot:DLC_Gloomrot,… Powers a stair-style picker that greys out unowned
                              styles, retires the `.uriel stairstyles` parse.
```
When implemented, follow the §6 transport rule above: **one `ctx.Reply` per `[URIEL:*]` line**
(header → rows → `[URIEL:end]`), never a `\n`-joined block.

### ✅ Build-mode commands BCH relays — placement vs. targeting (updated 2026-06-08)
**The cursor problem (UI buttons):** when the player clicks a BCH panel button, the V Rising cursor is
on the UI, so the aim ray points *outside the plot* — aim-based PLACEMENT then fails with "…can only be
spawned/placed in a castle plot." Commands that PLACE at a point therefore accept a player-position
token; commands that only TARGET the nearest spawned object already use the player's feet and need no
token.

- **`.uriel spawn <guid> [rot] [flags…]`** — places at the aim point by default, **or at the player's
  location with the `here` token** (aliases `nearest`/`me`). **From a UI button, BCH MUST append `here`**
  (or `nearest`) so it lands in the plot the player is standing in. Flags are order-independent AFTER the
  rotation slot (up to 4), e.g. `.uriel spawn 12345 0 here` or `.uriel spawn 12345 0 smashable respawn here`.
  (The rotation int must come before the flags — `.uriel spawn 12345 here` would try to bind `here` to the
  rotation and fail; send `0`.) **Durability/lifecycle flags** (optional — a BCH spawn panel can expose
  these as toggles):
  - `breakable` — raid/decay can destroy it (still castle-owned; the OWNER can't weapon-smash it — vanilla castle protection).
  - `smashable` — breakable AND the owner can destroy it too (the object skips castle adoption; trade-off: not castle-owned, decays, anyone in the territory can damage it).
  - `respawn` — auto-respawns after destruction until the castle is gone or the object is `.uriel despawn`ed (server config `ObjectSpawn.RespawnEnabled`, default on; cadence `ObjectSpawn.RespawnPollSeconds`, default 30s).
  - `indestructible` — permanent (the default unless `ObjectSpawn.Indestructible=false`).
- **`.uriel move [here]`** — moves the nearest spawned object to the aim point by default, **or to the
  player's location with `here`/`nearest`**. **From a UI button, append `here`.** Bare still works for
  in-world aim placement.
- **`.uriel rotate [0-3]`** — rotates the nearest spawned object in place (no destination point). **Bare**
  = one 90° step; optional `0-3` sets an absolute tile rotation. No position token needed.
- **`.uriel despawn`** — removes the nearest spawned object (targets by the player's feet). **Bare.**

`nearest` is accepted as a synonym of `here` on spawn/move so BCH's existing "UI relays append `nearest`"
convention (§3, used for storage/stairs) extends here unchanged.

**Overlap refusal (new 2026-06-09):** `.uriel spawn` and `.uriel move` can now be REFUSED when the target
tile is already occupied by a non-floor build piece (wall/station/native prop) or another spawned object —
reply begins `Can't place <name> there — it would overlap <blocker>.` (or `Can't move … there — …`). This
is a plain chat reply (not a `[URIEL:*]` line); a BCH placement panel should surface it as a "try a clear
spot" message rather than treat the spawn as succeeded. Floors never trigger it (decor sits on floors).
Server-gated by `ObjectSpawn.PreventOverlap` (default on); when an admin turns it off, the refusal never
fires. No command/arg/wire shape changed — only this added failure path.

## 7. Change discipline (the living-contract rule)

Same rule as Beelzebub's handoff: **whenever Uriel work changes anything
BCH-facing — a chat command, a reply format BCH parses, the §2 replicated
markers, a config key, or a `[URIEL:*]` line — update THIS doc in the same
commit.** When the wire API lands, its `ApiVersion` lives in
`Commands/ApiCommands.cs` and the banner at the top of this doc must state
it. Catch-up banners (⭐ style) go at the top when BCH has been away for
several Uriel versions.
