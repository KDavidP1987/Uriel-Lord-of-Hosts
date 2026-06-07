using System;
using HarmonyLib;
using ProjectM;
using ProjectM.Network;
using Unity.Collections;
using Unity.Entities;
using Uriel.Config;

namespace Uriel.Patches;

/// <summary>
/// Item-movement policy enforcement for public containers (v0.3.0).
///
/// Withdrawals from a neutral-team (shared) container already pass vanilla
/// (loot semantics) — the prefix gates them by permission/limit/cost and
/// cancels denied events. Deposits are vanilla-REFUSED for neutral containers
/// (confirmed in live testing), so permitted deposits are executed manually
/// and the event destroyed. Bloodcraft's FamiliarServantPatches is the shape
/// reference for the query/event access.
/// </summary>
[HarmonyPatch(typeof(MoveItemBetweenInventoriesSystem), nameof(MoveItemBetweenInventoriesSystem.OnUpdate))]
internal static class MoveItemBetweenInventoriesSystemPatch
{
    [HarmonyPrefix]
    public static void OnUpdatePrefix(MoveItemBetweenInventoriesSystem __instance)
    {
        if (!Core.IsReady || Core.PublicStorage is null) return;
        if (!Settings.PublicStorage_Enabled.Value) return;

        NativeArray<Entity> entities = default;
        try
        {
            entities = __instance._MoveItemBetweenInventoriesEventQuery.ToEntityArray(Allocator.Temp);
            foreach (Entity entity in entities)
            {
                if (!entity.TryGetComponent<MoveItemBetweenInventoriesEvent>(out var ev)) continue;
                if (!entity.TryGetComponent<FromCharacter>(out var fromCharacter)) continue;
                Core.PublicStorage.HandleMoveEvent(entity, fromCharacter, ev.FromInventory, ev.ToInventory, ev.FromSlot);
            }
        }
        catch (Exception ex)
        {
            Core.Log.LogWarning($"[Uriel SHARE] MoveItem prefix failed: {ex.Message}");
        }
        finally
        {
            if (entities.IsCreated) entities.Dispose();
        }
    }
}

/// <summary>
/// "Move all" (take-all / smart-stash style) can't be accounted per stack —
/// blocked for non-controllers on policy-restricted containers.
/// </summary>
[HarmonyPatch(typeof(MoveAllItemsBetweenInventoriesSystem), nameof(MoveAllItemsBetweenInventoriesSystem.OnUpdate))]
internal static class MoveAllItemsBetweenInventoriesSystemPatch
{
    [HarmonyPrefix]
    public static void OnUpdatePrefix(MoveAllItemsBetweenInventoriesSystem __instance)
    {
        if (!Core.IsReady || Core.PublicStorage is null) return;
        if (!Settings.PublicStorage_Enabled.Value) return;

        NativeArray<Entity> entities = default;
        try
        {
            entities = __instance.EntityQueries[0].ToEntityArray(Allocator.Temp);
            foreach (Entity entity in entities)
            {
                if (!entity.TryGetComponent<MoveAllItemsBetweenInventoriesEvent>(out var ev)) continue;
                if (!entity.TryGetComponent<FromCharacter>(out var fromCharacter)) continue;
                Core.PublicStorage.HandleMoveAllEvent(entity, fromCharacter, ev.FromInventory, ev.ToInventory);
            }
        }
        catch (Exception ex)
        {
            Core.Log.LogWarning($"[Uriel SHARE] MoveAll prefix failed: {ex.Message}");
        }
        finally
        {
            if (entities.IsCreated) entities.Dispose();
        }
    }
}
