using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using ProjectM.Shared;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using Uriel.Config;

namespace Uriel.Services;

/// <summary>
/// Stair hot-swap (docs/features/STAIR_HOTSWAP.md). A placed stair may swap to
/// another COSMETIC of the SAME archetype (Single / Single_CW / Single_CCW /
/// Double) — cosmetics within an archetype are structurally identical and cost
/// identically, so the swap is free and in place.
///
/// MECHANISM (KindredSchematics production pattern): instantiate the target
/// blueprint's prefab entity, copy the old root's transform/tile components,
/// wire ownership from the castle heart, DestroyUtility the old root — the game
/// self-registers the new tile. NO walk of attach-parents on destroy (that's
/// delete semantics and would take out the floor the stair attaches to).
///
/// DLC GATE (owner decision 2026-06-07): a player may only swap TO a style they
/// could build themselves — DLC cosmetics carry ProgressionUserContentDependency,
/// checked against User.UserContent via UserContentUtility.HasUnlocked.
/// </summary>
internal sealed class StairSwapService
{
    const string BlueprintPrefix = "BP_Castle_Stairs_";

    // Style keys in cycle order. Matrix from the prefab dump (docs/features/STAIR_HOTSWAP.md).
    static readonly string[] StyleOrder = { "stone1", "stone2", "stone3", "gloomrot", "projectk", "strongblade" };

    static readonly Dictionary<string, string> SuffixToStyle = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Stone01"] = "stone1",
        ["Stone02"] = "stone2",
        ["Stone03"] = "stone3",
        ["DLC_Gloomrot01"] = "gloomrot",
        ["DLC_ProjectK01"] = "projectk",
        ["DLC_StrongbladeDLC01"] = "strongblade",
    };

    // archetype → (style → blueprint GUID)
    static readonly Dictionary<string, Dictionary<string, int>> Matrix = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Single"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["stone1"] = -541385494, ["stone2"] = 1267405974, ["stone3"] = 1019577856,
            ["gloomrot"] = -1808336362, ["projectk"] = -601630508, ["strongblade"] = 1831814293,
        },
        ["Single_CW"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["stone1"] = -628212401, ["stone2"] = 891240110, ["stone3"] = 1737385414,
            ["gloomrot"] = -671167268, ["projectk"] = -2111252824, ["strongblade"] = 54801101,
        },
        ["Single_CCW"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["stone1"] = -1323146211, ["stone2"] = 171455823, ["stone3"] = 2042236287,
            ["gloomrot"] = 249484894, ["projectk"] = 787873859, ["strongblade"] = 1748214728,
        },
        ["Double"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["stone1"] = 77571580, ["stone2"] = -887317616, ["stone3"] = -1186795702,
            ["gloomrot"] = -1940654627, ["projectk"] = 384472583, ["strongblade"] = -2042297302,
        },
    };

    // ---------------------------------------------------------------- parsing

    /// <summary>"BP_Castle_Stairs_Single_CW_Stone02" → ("Single_CW", "stone2"). False for non-stairs.</summary>
    public static bool TryParseStairName(string prefabName, out string archetype, out string style)
    {
        archetype = null;
        style = null;
        if (string.IsNullOrEmpty(prefabName) || !prefabName.StartsWith(BlueprintPrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        string rest = prefabName.Substring(BlueprintPrefix.Length);
        // Order matters: check CW/CCW before bare Single.
        foreach (string arch in new[] { "Single_CCW", "Single_CW", "Single", "Double" })
        {
            if (!rest.StartsWith(arch + "_", StringComparison.OrdinalIgnoreCase)) continue;
            string suffix = rest.Substring(arch.Length + 1);
            if (!SuffixToStyle.TryGetValue(suffix, out style)) return false;
            archetype = arch;
            return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- targeting

    /// <summary>
    /// Resolve the stair BLUEPRINT ROOT: closest tile entity within range whose
    /// own prefab (or fused root's prefab) is a BP_Castle_Stairs_*. Default mode
    /// searches around the AIM position with an automatic nearest-to-player
    /// fallback; nearestToPlayer mode (BCH UI buttons) searches around the
    /// player only (v0.11.0).
    /// </summary>
    public Entity ResolveTargetStair(Entity character, out string archetype, out string currentStyle, out string error, bool nearestToPlayer = false)
    {
        archetype = null;
        currentStyle = null;
        error = null;
        float maxDist = Settings.StairSwap_MaxTargetDistance.Value;

        if (!nearestToPlayer && character.TryGetComponent<EntityAimData>(out var aimData))
        {
            Entity hit = ClosestStairTo(aimData.AimPosition, maxDist, out archetype, out currentStyle);
            if (hit != Entity.Null) return hit;
        }
        if (!PublicStorageService.TryGetCharacterPosition(character, out var charPos))
        {
            error = "Could not read your position.";
            return Entity.Null;
        }
        Entity nearest = ClosestStairTo(charPos, maxDist, out archetype, out currentStyle);
        if (nearest == Entity.Null)
            error = $"No stair found within {maxDist:F0}m of you. Stand next to the staircase (or aim at it).";
        return nearest;
    }

    static Entity ClosestStairTo(Unity.Mathematics.float3 refPos, float maxDist, out string archetype, out string currentStyle)
    {
        archetype = null;
        currentStyle = null;
        var aimPos = refPos;
        float maxDistSq = maxDist * maxDist;

        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(new(Il2CppType.Of<TilePosition>(), ComponentType.AccessMode.ReadOnly))
            .AddAll(new(Il2CppType.Of<Unity.Transforms.Translation>(), ComponentType.AccessMode.ReadOnly))
            .WithOptions(EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludeSpawnTag);
        var query = Core.EntityManager.CreateEntityQuery(ref builder);
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            Entity bestRoot = Entity.Null;
            float bestSq = float.MaxValue;
            string bestArch = null, bestStyle = null;
            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                if (!e.TryGetComponent<Unity.Transforms.Translation>(out var t)) continue;
                float dx = t.Value.x - aimPos.x, dy = t.Value.y - aimPos.y, dz = t.Value.z - aimPos.z;
                float dsq = dx * dx + dy * dy + dz * dz;
                if (dsq > maxDistSq || dsq >= bestSq) continue;

                Entity root = ResolveStairRoot(e);
                if (root == Entity.Null) continue;
                if (!TryParseStairName(root.GetPrefabGuid().GetPrefabName(), out string arch, out string style)) continue;

                bestSq = dsq;
                bestRoot = root;
                bestArch = arch;
                bestStyle = style;
            }
            archetype = bestArch;
            currentStyle = bestStyle;
            return bestRoot;
        }
        finally
        {
            entities.Dispose();
        }
    }

    /// <summary>Entity (segment or root) → its stair blueprint root, or Null.</summary>
    static Entity ResolveStairRoot(Entity entity)
    {
        if (IsStairRoot(entity)) return entity;
        // Fused segments point at their root.
        if (entity.TryGetComponent<CastleBuildingFusedChild>(out var fusedChild))
        {
            Entity parent = fusedChild.ParentEntity.GetEntityOnServer();
            if (IsStairRoot(parent)) return parent;
        }
        return Entity.Null;
    }

    static bool IsStairRoot(Entity entity)
    {
        if (!entity.Exists()) return false;
        string name = entity.GetPrefabGuid().GetPrefabName();
        return name.StartsWith(BlueprintPrefix, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- entitlement

    /// <summary>
    /// May this user build the target style? Non-DLC styles: always. DLC styles:
    /// only if the prefab's ProgressionUserContentDependency flag is present in
    /// User.UserContent — i.e. exactly when it appears in THEIR build menu.
    /// </summary>
    public static bool UserOwnsStyle(Entity userEntity, PrefabGUID styleGuid, out string dlcName)
    {
        dlcName = null;
        if (!Core.PrefabCollectionSystem._PrefabLookupMap.TryGetValue(styleGuid, out Entity prefab))
            return false;
        if (!prefab.Has<ProgressionUserContentDependency>()) return true; // non-DLC cosmetic
        var required = prefab.Read<ProgressionUserContentDependency>().Value;
        dlcName = required.ToString();
        if (!userEntity.TryGetComponent<User>(out var user)) return false;
        return UserContentUtility.HasUnlocked(user.UserContent, required);
    }

    // ---------------------------------------------------------------- swap

    public bool Swap(Entity character, Entity userEntity, string styleKey, out string message, bool nearestToPlayer = false)
    {
        var root = ResolveTargetStair(character, out string archetype, out string currentStyle, out message, nearestToPlayer);
        if (root == Entity.Null) return false;

        if (!PublicStorageService.CharacterControlsContainer(character, root))
        {
            message = "You don't control this stair (its castle isn't yours/your clan's).";
            return false;
        }

        // Resolve the requested style ("next" cycles to the next style the user owns).
        string targetStyle;
        if (string.Equals(styleKey, "next", StringComparison.OrdinalIgnoreCase))
        {
            targetStyle = NextOwnedStyle(userEntity, archetype, currentStyle);
            if (targetStyle is null)
            {
                message = "No other style available to you for this stair.";
                return false;
            }
        }
        else
        {
            targetStyle = styleKey?.Trim().ToLowerInvariant();
            if (targetStyle is null || !Matrix[archetype].ContainsKey(targetStyle))
            {
                message = $"Unknown style '{styleKey}'. Styles: {string.Join(", ", StyleOrder)} (or 'next').";
                return false;
            }
        }
        if (string.Equals(targetStyle, currentStyle, StringComparison.OrdinalIgnoreCase))
        {
            message = $"That stair is already style '{currentStyle}'.";
            return false;
        }

        var targetGuid = new PrefabGUID(Matrix[archetype][targetStyle]);
        if (!UserOwnsStyle(userEntity, targetGuid, out string dlcName))
        {
            message = dlcName is null
                ? "That style could not be resolved."
                : $"Style '{targetStyle}' requires the {dlcName} DLC — it isn't in your build menu.";
            return false;
        }

        try
        {
            return ExecuteSwap(root, targetGuid, archetype, currentStyle, targetStyle, character, userEntity, out message);
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[Uriel STAIRS] swap failed: {ex}");
            message = "Swap failed unexpectedly — nothing was changed if the old stair is still standing. Check the server log.";
            return false;
        }
    }

    string NextOwnedStyle(Entity userEntity, string archetype, string currentStyle)
    {
        int start = Array.IndexOf(StyleOrder, currentStyle);
        for (int step = 1; step <= StyleOrder.Length; step++)
        {
            string candidate = StyleOrder[(start + step) % StyleOrder.Length];
            if (candidate == currentStyle) continue;
            if (UserOwnsStyle(userEntity, new PrefabGUID(Matrix[archetype][candidate]), out _)) return candidate;
        }
        return null;
    }


    /// <summary>
    /// The actual swap (v0.13.0 — IDENTITY SWAP, the mod owner's insight). All
    /// prior routes failed live: raw spawn → unmanaged "permanent" stairs;
    /// DestroyUtility → ghost grid claims; vanilla dismantle event → only starts
    /// a timed ability that never completes outside build mode; vanilla build
    /// event → refused. The breakthrough: same-archetype cosmetics are
    /// structurally IDENTICAL and all 18 TM_ stair segments are STYLE-AGNOSTIC —
    /// the BP root's PrefabGUID alone carries the cosmetic. So the swap simply
    /// REWRITES THE ROOT'S IDENTITY in place. Nothing is destroyed or placed:
    /// the stair remains the original vanilla-built object (registration, grid
    /// claims, floor attachments, dismantle behavior untouched), and neighbors
    /// referencing it by entity id are unaffected. Free, instant, atomic.
    /// </summary>
    bool ExecuteSwap(Entity oldRoot, PrefabGUID targetGuid, string archetype, string fromStyle, string toStyle,
        Entity character, Entity userEntity, out string message)
    {
        // Rewrite the prefab identity (the cosmetic).
        oldRoot.With((ref PrefabGUID g) => g._Value = targetGuid._Value);
        // Keep the blueprint identity consistent (dismantle/refund/menu data).
        if (oldRoot.Has<BlueprintData>())
            oldRoot.With((ref BlueprintData b) => b.Guid = targetGuid);

        // v0.13.1 (live finding: even a RELOG kept the old look): placed castle
        // tiles replicate as MEGA-STATIC network objects — the client derives
        // the visual from MegaStatic_PrefabGUID embedded in the NetworkId
        // itself, NOT from the PrefabGUID component. Rewrite it too.
        bool wasMegaStatic = false;
        if (oldRoot.TryGetComponent<ProjectM.Network.NetworkId>(out var netId)
            && netId.Type == ProjectM.Network.NetworkIdType.MegaStatic)
        {
            wasMegaStatic = true;
            oldRoot.With((ref ProjectM.Network.NetworkId n) => n.MegaStatic_PrefabGUID = targetGuid._Value);
        }

        // Push the new identity to connected clients (entity re-receive).
        PublicStorageService.ForceResync(oldRoot);

        Core.Log.LogInfo($"[Uriel STAIRS] identity swap: {archetype} {fromStyle} -> {toStyle} (entity preserved; megaStatic={wasMegaStatic}).");
        // Honest UX (live finding): placed tiles are baked into per-chunk
        // MegaStatic snapshots generated ONCE at server load — there is no
        // modified-instance channel, so the new look appears at the next
        // server restart (not on relog). The swap itself is already durable.
        message = $"Stair swapped: {fromStyle} -> {toStyle} ({archetype}). The new look appears for everyone at the NEXT SERVER RESTART (the change is already saved).";
        return true;
    }
    /// <summary>
    /// Admin ghost cleanup (v0.12.1): DestroyUtility-destroyed stairs leave
    /// invisible collision + grid claims. Purge every stair entity (BP roots and
    /// TM segments) within range of the player; a server restart afterwards
    /// flushes any remaining grid claims.
    /// </summary>
    public string PurgeNear(Entity character, float radius = 5f)
    {
        if (!PublicStorageService.TryGetCharacterPosition(character, out var pos))
            return "Could not read your position.";
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(new(Il2CppType.Of<TilePosition>(), ComponentType.AccessMode.ReadOnly))
            .AddAll(new(Il2CppType.Of<Unity.Transforms.Translation>(), ComponentType.AccessMode.ReadOnly))
            .WithOptions(EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludeSpawnTag);
        var query = Core.EntityManager.CreateEntityQuery(ref builder);
        var entities = query.ToEntityArray(Allocator.Temp);
        int purged = 0;
        try
        {
            float rSq = radius * radius;
            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                string name = e.GetPrefabGuid().GetPrefabName();
                if (!name.StartsWith("BP_Castle_Stairs_", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("TM_Castle_Stairs_", StringComparison.OrdinalIgnoreCase)) continue;
                if (!e.TryGetComponent<Unity.Transforms.Translation>(out var t)) continue;
                float dx = t.Value.x - pos.x, dy = t.Value.y - pos.y, dz = t.Value.z - pos.z;
                if (dx * dx + dy * dy + dz * dz > rSq) continue;
                DestroyUtility.Destroy(Core.EntityManager, e);
                purged++;
            }
        }
        finally
        {
            entities.Dispose();
        }
        Core.Log.LogInfo($"[Uriel STAIRS] stairpurge: destroyed {purged} stair entity(ies) within {radius:F0}m.");
        return purged == 0
            ? $"No stair entities found within {radius:F0}m."
            : $"Purged {purged} stair entity(ies) within {radius:F0}m. RESTART the server to flush any remaining ghost grid claims, then rebuild the stairs.";
    }

    // ----------------------------------------- experimental live visual refresh

    /// <summary>
    /// EXPERIMENTAL (admin; the action step is gated by [StairSwap]
    /// ExperimentalLiveRefresh). LIVE FINDING (2026-06-07): placed stairs are NOT
    /// mega-static — every piece (BP_ root + fused TM_ children) is a NORMAL-networked
    /// entity (the v0.13.x "MegaStatic bake" theory was wrong). Normal entities ARE
    /// live-replicated, so the reason a swap needs a restart is narrower: the client
    /// builds the staircase visual from the root's identity when it FIRST receives the
    /// structure and does not re-derive it when the root's PrefabGUID changes in place.
    ///
    /// This command (a) DUMPS the full runtime component set of the root and every
    /// fused child to the server log — so we can find whatever carries the cosmetic
    /// and, if needed, rewrite it — and (b) when the flag is on, RE-STREAMS the whole
    /// structure through the game's own Disabled→enabled streaming path (the same
    /// mechanism public-storage uses), so clients drop and re-receive every piece. If
    /// the client re-derives the cosmetic from the (already-rewritten) root on
    /// re-receive, the new style appears live. Reversible; never touches the durable
    /// swap; a restart still renders correctly regardless.
    /// </summary>
    public string DiagnoseLiveRefresh(Entity character, bool performAction)
    {
        Entity root = ResolveTargetStair(character, out string archetype, out string currentStyle, out string error);
        if (root == Entity.Null) return error ?? "No staircase targeted.";

        void L(string line) => Core.Log.LogInfo("[Uriel STAIRREFRESH] " + line);

        L($"root={Describe(root)} archetype={archetype} style={currentStyle} performAction={performAction}");

        // The rendered tiles are the fused TM_ children; the root carries the identity.
        var targets = new List<Entity> { root };
        if (Core.ServerGameManager.TryGetBuffer<CastleBuildingFusedChildrenBuffer>(root, out var fused))
        {
            for (int i = 0; i < fused.Length; i++)
            {
                Entity child = fused[i].ChildEntity.GetEntityOnServer();
                if (child.Exists()) targets.Add(child);
            }
        }
        L($"fused children: {targets.Count - 1}");

        // Per-entity IDENTITY dump. Hypothesis: the swap rewrites only the ROOT's
        // PrefabGUID/BlueprintData, but a CHILD-level field carries the cosmetic and is
        // left stale (the restart re-spawns children from the blueprint, which is why
        // only a restart refreshes). Log each piece's PrefabGUID + BlueprintData.Guid so
        // we can see whether the children still point at the OLD style.
        foreach (Entity e in targets)
        {
            string netType = e.TryGetComponent<ProjectM.Network.NetworkId>(out var nid) ? nid.Type.ToString() : "none";
            var pg = e.GetPrefabGuid();
            string bp = e.TryGetComponent<BlueprintData>(out var bpd)
                ? $"{bpd.Guid.GetPrefabName()}({bpd.Guid._Value})"
                : "none";
            L($"  {Describe(e)}: NetId={netType} static={e.Has<StaticTransformCompatible>()} PrefabGUID={pg.GetPrefabName()}({pg._Value}) BlueprintData.Guid={bp}");
        }

        if (!performAction)
            return $"Stair diagnostic written to the server log (root + {targets.Count - 1} child tile(s), full component dumps). No change made — set [StairSwap] ExperimentalLiveRefresh=true to test the live re-stream.";

        // EXPERIMENT v2: RE-BAKE each piece's visual blobs from its CURRENT prefab, then
        // re-stream. The placed pieces kept the blob references (TileData, and the root's
        // NetworkedPrefabChildren) that were resolved at ORIGINAL placement (old style);
        // an in-place PrefabGUID rewrite never refreshes those blobs, which is why only a
        // restart (= fresh spawn from the new blueprint) shows the new look. Re-copying
        // the blobs from each piece's current prefab is what that fresh spawn does.
        int rebaked = 0;
        foreach (Entity e in targets)
        {
            try
            {
                var pgid = e.GetPrefabGuid();
                if (!Core.PrefabCollectionSystem._PrefabLookupMap.TryGetValue(pgid, out Entity prefab) || !prefab.Exists())
                {
                    L($"    no prefab for {Describe(e)} — skipped rebake");
                    continue;
                }
                bool didTile = false, didChildren = false;
                if (e.Has<ProjectM.TileData>() && prefab.TryGetComponent<ProjectM.TileData>(out var ptd))
                {
                    e.With((ref ProjectM.TileData x) => x.Data = ptd.Data);
                    didTile = true;
                }
                if (e.Has<ProjectM.Tiles.NetworkedPrefabChildren>() && prefab.TryGetComponent<ProjectM.Tiles.NetworkedPrefabChildren>(out var pnpc))
                {
                    e.With((ref ProjectM.Tiles.NetworkedPrefabChildren x) => x.Data = pnpc.Data);
                    didChildren = true;
                }
                rebaked++;
                L($"    rebaked {Describe(e)} from prefab {pgid.GetPrefabName()} (TileData={didTile} NetworkedPrefabChildren={didChildren})");
            }
            catch (Exception ex) { L($"    rebake failed for {Describe(e)}: {ex.Message}"); }
        }

        int restreamed = 0;
        foreach (Entity e in targets)
        {
            try { PublicStorageService.ForceResync(e); restreamed++; }
            catch (Exception ex) { L($"    re-stream failed for {Describe(e)}: {ex.Message}"); }
        }
        L($"rebaked {rebaked}, re-streamed {restreamed} entity(ies).");
        return $"Stair live-refresh EXPERIMENT v2: re-baked visual blobs (TileData / NetworkedPrefabChildren) on {rebaked} piece(s) from their current prefab, then re-streamed {restreamed}. WATCH the staircase for a few seconds. If it updates → that's the fix and I fold it into the swap. If still nothing → the visual is resolved purely client-side from structure identity and only a true respawn (or client-side BCH) can refresh it. Durable swap unaffected.";
    }

    static string Describe(Entity e)
        => e.Exists() ? $"{e.GetPrefabGuid().GetPrefabName()}({e.Index}:{e.Version})" : "Entity.Null";

    // ------------------------------------------- destroy + respawn (experimental)

    /// <summary>Captured state of one stair piece — enough to re-create it as a fresh entity.</summary>
    sealed class PieceCapture
    {
        public PrefabGUID Prefab;
        public Translation Translation;
        public bool HasRotation; public Rotation Rotation;
        public bool HasTilePos; public TilePosition TilePos;
        public bool HasTileBounds; public TileBounds TileBounds;
        public bool HasStatic; public StaticTransformCompatible Static;
        public bool HasTeam; public Team Team;
        public bool HasTeamRef; public TeamReference TeamRef;
        public bool HasOwner; public UserOwner Owner;
        public bool HasHeart; public CastleHeartConnection Heart;
        public readonly List<Entity> AttachParents = new();
    }

    static PieceCapture Capture(Entity e, PrefabGUID prefabOverride)
    {
        var c = new PieceCapture { Prefab = prefabOverride._Value != 0 ? prefabOverride : e.GetPrefabGuid() };
        if (e.TryGetComponent<Translation>(out var tr)) c.Translation = tr;
        if (e.TryGetComponent<Rotation>(out var ro)) { c.HasRotation = true; c.Rotation = ro; }
        if (e.TryGetComponent<TilePosition>(out var tp)) { c.HasTilePos = true; c.TilePos = tp; }
        if (e.TryGetComponent<TileBounds>(out var tb)) { c.HasTileBounds = true; c.TileBounds = tb; }
        if (e.TryGetComponent<StaticTransformCompatible>(out var st)) { c.HasStatic = true; c.Static = st; }
        if (e.TryGetComponent<Team>(out var tm)) { c.HasTeam = true; c.Team = tm; }
        if (e.TryGetComponent<TeamReference>(out var trf)) { c.HasTeamRef = true; c.TeamRef = trf; }
        if (e.TryGetComponent<UserOwner>(out var uo)) { c.HasOwner = true; c.Owner = uo; }
        if (e.TryGetComponent<CastleHeartConnection>(out var hc)) { c.HasHeart = true; c.Heart = hc; }
        if (Core.ServerGameManager.TryGetBuffer<CastleBuildingAttachToParentsBuffer>(e, out var ap))
            for (int i = 0; i < ap.Length; i++)
            {
                Entity pe = ap[i].ParentEntity.GetEntityOnServer();
                if (pe.Exists()) c.AttachParents.Add(pe);
            }
        return c;
    }

    /// <summary>Instantiate a fresh entity from the captured piece and restore its placement/ownership.</summary>
    static Entity SpawnPiece(PieceCapture c)
    {
        if (!Core.PrefabCollectionSystem._PrefabLookupMap.TryGetValue(c.Prefab, out Entity prefab) || !prefab.Exists())
        {
            Core.Log.LogError($"[Uriel STAIRRESPAWN] no prefab for {c.Prefab.GetPrefabName()}({c.Prefab._Value}) — piece skipped.");
            return Entity.Null;
        }
        Entity e = Core.EntityManager.Instantiate(prefab);
        if (e.Has<Disabled>()) Core.EntityManager.RemoveComponent<Disabled>(e);
        e.With((ref Translation t) => t = c.Translation);
        if (c.HasRotation && e.Has<Rotation>()) e.With((ref Rotation r) => r = c.Rotation);
        if (c.HasTilePos && e.Has<TilePosition>()) e.With((ref TilePosition t) => t = c.TilePos);
        if (c.HasTileBounds && e.Has<TileBounds>()) e.With((ref TileBounds t) => t = c.TileBounds);
        if (c.HasStatic && e.Has<StaticTransformCompatible>()) e.With((ref StaticTransformCompatible s) => s = c.Static);
        if (c.HasTeam && e.Has<Team>()) e.With((ref Team t) => t = c.Team);
        if (c.HasTeamRef && e.Has<TeamReference>()) e.With((ref TeamReference t) => t = c.TeamRef);
        if (c.HasOwner && e.Has<UserOwner>()) e.With((ref UserOwner u) => u = c.Owner);
        if (c.HasHeart && e.Has<CastleHeartConnection>()) e.With((ref CastleHeartConnection h) => h = c.Heart);
        return e;
    }

    /// <summary>
    /// EXPERIMENTAL respawn swap. The identity swap can't refresh the live visual
    /// (the client binds a placed stair's look to its NetworkId at first receipt and
    /// never re-derives it — only a restart, which gives every entity a NEW NetworkId,
    /// refreshes it). So this DESTROYS the whole fused structure and SPAWNS a brand-new
    /// one of the target style (new NetworkIds = what a restart does for one staircase),
    /// with a configurable gap so clients register the removal first.
    /// </summary>
    public bool SwapViaRespawn(Entity character, Entity userEntity, string styleKey, out string message, bool nearestToPlayer = false)
    {
        var root = ResolveTargetStair(character, out string archetype, out string currentStyle, out message, nearestToPlayer);
        if (root == Entity.Null) return false;
        if (!PublicStorageService.CharacterControlsContainer(character, root))
        {
            message = "You don't control this stair (its castle isn't yours/your clan's).";
            return false;
        }

        string targetStyle;
        if (string.Equals(styleKey, "next", StringComparison.OrdinalIgnoreCase))
        {
            targetStyle = NextOwnedStyle(userEntity, archetype, currentStyle);
            if (targetStyle is null) { message = "No other style available to you for this stair."; return false; }
        }
        else
        {
            targetStyle = styleKey?.Trim().ToLowerInvariant();
            if (targetStyle is null || !Matrix[archetype].ContainsKey(targetStyle))
            {
                message = $"Unknown style '{styleKey}'. Styles: {string.Join(", ", StyleOrder)} (or 'next').";
                return false;
            }
        }
        if (string.Equals(targetStyle, currentStyle, StringComparison.OrdinalIgnoreCase))
        {
            message = $"That stair is already style '{currentStyle}'.";
            return false;
        }

        var targetGuid = new PrefabGUID(Matrix[archetype][targetStyle]);
        if (!UserOwnsStyle(userEntity, targetGuid, out string dlcName))
        {
            message = dlcName is null
                ? "That style could not be resolved."
                : $"Style '{targetStyle}' requires the {dlcName} DLC — it isn't in your build menu.";
            return false;
        }

        try
        {
            return ExecuteRespawn(root, targetGuid, currentStyle, targetStyle, out message);
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[Uriel STAIRRESPAWN] failed: {ex}");
            message = "Respawn failed unexpectedly — check the server log.";
            return false;
        }
    }

    bool ExecuteRespawn(Entity oldRoot, PrefabGUID targetGuid, string fromStyle, string toStyle, out string message)
    {
        // 1. Capture the whole fused structure BEFORE destroying anything. The root
        //    is re-created from the TARGET style prefab; children keep their own
        //    (style-agnostic) prefabs.
        var rootCap = Capture(oldRoot, targetGuid);
        var childCaps = new List<PieceCapture>();
        var oldPieces = new List<Entity> { oldRoot };
        if (Core.ServerGameManager.TryGetBuffer<CastleBuildingFusedChildrenBuffer>(oldRoot, out var fch))
            for (int i = 0; i < fch.Length; i++)
            {
                Entity ce = fch[i].ChildEntity.GetEntityOnServer();
                if (ce.Exists()) { childCaps.Add(Capture(ce, default)); oldPieces.Add(ce); }
            }

        // 1b. SAFETY: confirm EVERY prefab we'll re-spawn resolves BEFORE destroying
        //     anything — a missing prefab must never leave the player with a deleted
        //     staircase.
        if (!Core.PrefabCollectionSystem._PrefabLookupMap.TryGetValue(rootCap.Prefab, out _))
        {
            message = $"Could not resolve the '{toStyle}' staircase prefab — nothing was changed.";
            return false;
        }
        foreach (var cc in childCaps)
            if (!Core.PrefabCollectionSystem._PrefabLookupMap.TryGetValue(cc.Prefab, out _))
            {
                message = "Could not resolve a stair segment prefab — nothing was changed.";
                return false;
            }

        // 2. Destroy the old structure (root + children) — each via DestroyUtility so the
        //    tile grid deregisters cleanly (no ghost claims). Do NOT walk attach-parents
        //    (that would take out the floors the stair connects to).
        int destroyed = 0;
        foreach (Entity p in oldPieces)
            if (p.Exists()) { DestroyUtility.Destroy(Core.EntityManager, p); destroyed++; }
        Core.Log.LogInfo($"[Uriel STAIRRESPAWN] {fromStyle}->{toStyle}: destroyed {destroyed} old piece(s) (root + {childCaps.Count} children). Respawning after gap.");

        // 3. After a gap (so clients register the removal first), spawn the new structure.
        int gap = System.Math.Max(1, Settings.StairSwap_RespawnGapFrames.Value);
        Tick.RunLater(gap, () =>
        {
            try
            {
                Entity newRoot = SpawnPiece(rootCap);
                if (newRoot == Entity.Null) { Core.Log.LogError("[Uriel STAIRRESPAWN] new root spawn failed — staircase NOT restored."); return; }

                var newChildren = new List<Entity>();
                foreach (var cc in childCaps)
                {
                    Entity nc = SpawnPiece(cc);
                    if (nc == Entity.Null) continue;
                    if (nc.Has<CastleBuildingFusedChild>())
                        nc.With((ref CastleBuildingFusedChild f) => f.ParentEntity = newRoot);
                    // Re-attach to the same parent tiles (floors/walls) the old child held.
                    if (cc.AttachParents.Count > 0
                        && Core.ServerGameManager.TryGetBuffer<CastleBuildingAttachToParentsBuffer>(nc, out var apb))
                    {
                        apb.Clear();
                        foreach (Entity pe in cc.AttachParents)
                        {
                            if (!pe.Exists()) continue;
                            apb.Add(new CastleBuildingAttachToParentsBuffer { ParentEntity = pe });
                            if (Core.ServerGameManager.TryGetBuffer<CastleBuildingAttachedChildrenBuffer>(pe, out var acb))
                                acb.Add(new CastleBuildingAttachedChildrenBuffer { ChildEntity = nc });
                        }
                    }
                    newChildren.Add(nc);
                }

                if (Core.ServerGameManager.TryGetBuffer<CastleBuildingFusedChildrenBuffer>(newRoot, out var ncb))
                {
                    ncb.Clear();
                    foreach (Entity nc in newChildren)
                        ncb.Add(new CastleBuildingFusedChildrenBuffer { ChildEntity = nc });
                }

                Core.Log.LogInfo($"[Uriel STAIRRESPAWN] respawned {targetGuid.GetPrefabName()} root({newRoot.Index}:{newRoot.Version}) + {newChildren.Count} child(ren). New NetworkIds assign next tick.");
            }
            catch (Exception ex)
            {
                Core.Log.LogError($"[Uriel STAIRRESPAWN] respawn lambda failed: {ex}");
            }
        });

        message = $"Stair RESPAWN {fromStyle} -> {toStyle}: old staircase destroyed; new-style staircase appears after a {gap}-frame gap (it should render the NEW style live since it's a fresh entity). EXPERIMENTAL — if it looks wrong or floats, dismantle & rebuild, or restart to normalize.";
        return true;
    }

    /// <summary>
    /// Cleanly remove the aimed staircase: destroy the whole fused structure (root +
    /// all fused TM_ children) via DestroyUtility so the tile grid deregisters
    /// properly, WITHOUT walking attach-parents — so the floors/walls/rooms the stair
    /// connects to are left untouched. This is the destroy step of the respawn swap,
    /// on its own (vanilla dismantle drags connected pieces; this targets only the stair).
    /// </summary>
    public bool RemoveStair(Entity character, out string message, bool nearestToPlayer = false)
    {
        var root = ResolveTargetStair(character, out string archetype, out string currentStyle, out message, nearestToPlayer);
        if (root == Entity.Null) return false;
        if (!PublicStorageService.CharacterControlsContainer(character, root))
        {
            message = "You don't control this stair (its castle isn't yours/your clan's).";
            return false;
        }

        var pieces = new List<Entity> { root };
        if (Core.ServerGameManager.TryGetBuffer<CastleBuildingFusedChildrenBuffer>(root, out var fch))
            for (int i = 0; i < fch.Length; i++)
            {
                Entity ce = fch[i].ChildEntity.GetEntityOnServer();
                if (ce.Exists()) pieces.Add(ce);
            }

        int destroyed = 0;
        foreach (Entity p in pieces)
            if (p.Exists()) { DestroyUtility.Destroy(Core.EntityManager, p); destroyed++; }

        Core.Log.LogInfo($"[Uriel STAIRS] removestairs: destroyed {destroyed} piece(s) of a {archetype} '{currentStyle}' staircase (connected tiles untouched).");
        message = $"Removed the {archetype} staircase ({destroyed} piece(s)). The floors/walls it was connected to were left intact. (No materials are refunded.)";
        return true;
    }

    // ---------------------------------------------------------------- info

    public string DescribeStyles(Entity character, Entity userEntity, bool nearestToPlayer = false)
    {
        var root = ResolveTargetStair(character, out string archetype, out string currentStyle, out string error, nearestToPlayer);
        if (root == Entity.Null) return error;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Stair: {archetype}, current style '{currentStyle}'. Available:");
        foreach (string s in StyleOrder)
        {
            bool owned = UserOwnsStyle(userEntity, new PrefabGUID(Matrix[archetype][s]), out string dlc);
            sb.AppendLine($"  {s}{(s == currentStyle ? " (current)" : "")}{(owned ? "" : $" — locked ({dlc} DLC)")}");
        }
        sb.Append("Swap with: .uriel stairswap <style>  (or 'next')");
        return sb.ToString();
    }
}
