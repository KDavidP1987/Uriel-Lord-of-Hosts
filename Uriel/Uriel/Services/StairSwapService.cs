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

        // Push the new identity to connected clients (entity re-receive).
        PublicStorageService.ForceResync(oldRoot);

        Core.Log.LogInfo($"[Uriel STAIRS] identity swap: {archetype} {fromStyle} -> {toStyle} (entity preserved).");
        message = $"Stair swapped: {fromStyle} -> {toStyle} ({archetype}). If it still LOOKS like the old style, step away and back (or relog) - the change is already applied.";
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
