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
    /// The actual swap (v0.12.0 — VANILLA PIPELINE): live testing proved raw
    /// Instantiate+component-copy produces unmanageable "permanent" stairs (the
    /// game's placement pipeline wires territory connections, the attach-to-floor
    /// graph, registration, and placement history that can't all be replicated by
    /// hand). So: refund the old stair's materials to the caller, destroy the old
    /// root, then fire the game's own BuildTileModelEvent at the same spot — the
    /// new stair is placed exactly as if the player built it (fully registered,
    /// editable, dismantlable, client-synced). Same-archetype costs are identical,
    /// so refund+charge nets zero. Worst case (placement refused): the player
    /// keeps the refunded materials and rebuilds by hand — never a loss.
    /// </summary>
    bool ExecuteSwap(Entity oldRoot, PrefabGUID targetGuid, string archetype, string fromStyle, string toStyle,
        Entity character, Entity userEntity, out string message)
    {
        message = null;

        // ---- capture placement from the old root ----
        if (!oldRoot.TryGetComponent<Unity.Transforms.Translation>(out var translation)
            || !oldRoot.TryGetComponent<TilePosition>(out var tilePos))
        {
            message = "This stair is missing placement data and can't be swapped.";
            return false;
        }
        var spawnPos = translation.Value;
        var spawnRot = tilePos.TileRotation;
        var tile = tilePos.Tile;

        if (!Core.PrefabCollectionSystem._PrefabLookupMap.TryGetValue(targetGuid, out Entity targetPrefab))
        {
            message = "Target style prefab not found on this server.";
            return false;
        }

        // ---- refund the old stair's cost (same as the new one's) so the vanilla
        //      build charge nets zero. Abort BEFORE destroying if inventory is full.
        var granted = new System.Collections.Generic.List<(PrefabGUID Item, int Amount)>();
        if (Core.ServerGameManager.TryGetBuffer<BlueprintRequirementBuffer>(targetPrefab, out var reqs))
        {
            for (int i = 0; i < reqs.Length; i++)
            {
                var req = reqs[i];
                if (req.Amount <= 0) continue;
                if (!Core.ServerGameManager.TryAddInventoryItem(character, req.PrefabGUID, req.Amount))
                {
                    foreach (var g in granted) Core.ServerGameManager.TryRemoveInventoryItem(
                        InventoryUtilities.TryGetInventoryEntity(Core.EntityManager, character, out Entity inv) ? inv : character,
                        g.Item, g.Amount);
                    message = $"Your inventory needs room for the swap materials ({req.Amount}× {req.PrefabGUID.GetPrefabName()}). Make space and retry.";
                    return false;
                }
                granted.Add((req.PrefabGUID, req.Amount));
            }
        }

        // ---- destroy the old root, then fire the game's own build event a few
        //      frames later (the destroy must settle so the cell reads as free).
        DestroyUtility.Destroy(Core.EntityManager, oldRoot);

        Entity capturedChar = character, capturedUser = userEntity;
        Tick.RunLater(3, () =>
        {
            try
            {
                Entity ev = Core.EntityManager.CreateEntity();
                Core.EntityManager.AddComponentData(ev, new FromCharacter { User = capturedUser, Character = capturedChar });
                Core.EntityManager.AddComponentData(ev, new NetworkEventType
                {
                    EventId = NetworkEvents.EventId_BuildTileModelEvent,
                    IsAdminEvent = false,
                    IsDebugEvent = false,
                });
                Core.EntityManager.AddComponent<ReceiveNetworkEventTag>(ev);
                Core.EntityManager.AddComponentData(ev, new BuildTileModelEvent
                {
                    PrefabGuid = targetGuid,
                    SpawnTranslation = new Unity.Transforms.Translation { Value = spawnPos },
                    SpawnTileRotation = spawnRot,
                    VariationIndex = 0,
                    ResourceConsumeType = BuildResourceConsumeType.LocalInventory,
                    RebuildUniqueKey = default,
                });
                Core.Log.LogInfo($"[Uriel STAIRS] build event fired: {archetype} {fromStyle} → {toStyle} at tile ({tile.x},{tile.y}).");
            }
            catch (Exception ex)
            {
                Core.Log.LogError($"[Uriel STAIRS] build event failed to create: {ex}");
                ChatNotify.ToUserEntity(capturedUser, "[Uriel] Swap failed after removal — the materials are in your inventory; place the stair by hand. (Admin: check log.)");
            }
        });

        // ---- verify placement; the refund means failure is never a loss ----
        var verifyPos = spawnPos;
        Tick.RunLater(45, () =>
        {
            try
            {
                Entity placed = ClosestStairTo(verifyPos, 2.5f, out _, out string placedStyle);
                if (placed != Entity.Null && string.Equals(placedStyle, toStyle, StringComparison.OrdinalIgnoreCase))
                {
                    ChatNotify.ToUserEntity(capturedUser, $"[Uriel] Stair swapped: {fromStyle} → {toStyle}.");
                    Core.Log.LogInfo("[Uriel STAIRS] swap verified — vanilla placement succeeded.");
                }
                else
                {
                    ChatNotify.ToUserEntity(capturedUser, "[Uriel] The game refused the placement — the old stair's materials are in your inventory; place the new style by hand from the build menu.");
                    Core.Log.LogWarning($"[Uriel STAIRS] swap verify: no {toStyle} stair found at the spot (found: {(placed == Entity.Null ? "nothing" : placedStyle)}). Player keeps the refund.");
                }
            }
            catch (Exception ex)
            {
                Core.Log.LogError($"[Uriel STAIRS] swap verify failed: {ex}");
            }
        });

        message = $"Swapping {fromStyle} → {toStyle} ({archetype})…";
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
