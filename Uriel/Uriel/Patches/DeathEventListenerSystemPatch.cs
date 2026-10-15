using System;
using HarmonyLib;
using ProjectM;
using Unity.Collections;
using Unity.Entities;

namespace Uriel.Patches;

/// <summary>
/// Object-discovery hook (docs/features/OBJECT_SPAWNING.md, Phase 2). Postfix on the death
/// stream: when a PLAYER destroys a discoverable spawnable world object,
/// <see cref="ObjectSpawnService.HandleKill"/> rolls the configured chance to unlock it for
/// that player to build (Discovery access mode).
///
/// DeathEvent fires for destroyed world objects too, not just units — Bloodcraft routes its
/// non-unit player kills (resource nodes) through this same system. We gate on
/// <c>DiscoveryActive</c> first, so this is a complete no-op in the admin test-bed
/// (AdminOnly=true) and in Full mode. Wrapped in try/catch — never throw across a patch boundary.
/// </summary>
[HarmonyPatch(typeof(DeathEventListenerSystem), nameof(DeathEventListenerSystem.OnUpdate))]
internal static class DeathEventListenerSystemPatch
{
    [HarmonyPostfix]
    public static void OnUpdatePostfix(DeathEventListenerSystem __instance)
    {
        if (!Core.IsReady) return;
        bool discovery = Core.ObjectSpawn is not null && Core.ObjectSpawn.TracksKills; // cheap config gate
        bool units = Core.Units is not null && Core.Units.TracksAny;                   // castle-event unit deaths
        if (!discovery && !units) return;

        NativeArray<DeathEvent> deaths = default;
        try
        {
            deaths = __instance._DeathEventQuery.ToComponentDataArray<DeathEvent>(Allocator.Temp);
            // Each consumer in its own try, so one failure never skips the other.
            if (units)
            {
                try { Core.Units.HandleDeaths(deaths); }
                catch (Exception ex) { Core.Log.LogError($"[Uriel UNIT] death read failed: {ex.Message}"); }
            }
            if (!discovery) return;
            for (int i = 0; i < deaths.Length; i++)
            {
                DeathEvent death = deaths[i];
                if (!death.Killer.Has<PlayerCharacter>()) continue; // only player-caused destructions
                Core.ObjectSpawn.HandleKill(death.Killer, death.Died);
            }
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[Uriel SPAWN] discovery death-hook failed: {ex}");
        }
        finally
        {
            if (deaths.IsCreated) deaths.Dispose();
        }
    }
}
