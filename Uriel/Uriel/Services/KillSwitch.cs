using System;
using System.Collections.Generic;
using Uriel.Config;

namespace Uriel.Services;

/// <summary>
/// Server-wide restore-to-vanilla: ends every castle event (its doors put back as they were), removes every Uriel
/// unit and every Uriel-spawned object, and makes every shared container private. Runs from '.uriel killswitch' or,
/// once, at boot when [KillSwitch] RestoreVanillaOnBoot is set. Works whatever the feature switches say. Swapped
/// stairs are left alone: a swap leaves an ordinary game staircase in another style, with nothing of Uriel's in it.
/// </summary>
internal static class KillSwitch
{
    public static List<string> RestoreVanilla(string why)
    {
        var lines = new List<string>();
        int runs = 0, units = 0, pending = 0, objects = 0, unresolved = 0, unshared = 0, stale = 0;

        try { runs = Core.Events?.StopAll("killswitch") ?? 0; }
        catch (Exception ex) { lines.Add($"Events: failed ({ex.Message})."); Core.Log.LogWarning($"[Uriel KILLSWITCH] events failed: {ex}"); }

        try { if (Core.Units is not null) (units, pending) = Core.Units.PurgeAllNow(); }
        catch (Exception ex) { lines.Add($"Units: failed ({ex.Message})."); Core.Log.LogWarning($"[Uriel KILLSWITCH] units failed: {ex}"); }

        try { if (Core.ObjectSpawn is not null) (objects, unresolved) = Core.ObjectSpawn.PurgeAll(why); }
        catch (Exception ex) { lines.Add($"Objects: failed ({ex.Message})."); Core.Log.LogWarning($"[Uriel KILLSWITCH] objects failed: {ex}"); }

        try { if (Core.PublicStorage is not null) unshared = Core.PublicStorage.UnshareAll(out stale); }
        catch (Exception ex) { lines.Add($"Storage: failed ({ex.Message})."); Core.Log.LogWarning($"[Uriel KILLSWITCH] storage failed: {ex}"); }

        int doorsPending = Core.Events?.PendingDoors ?? 0;
        Core.Log.LogInfo($"[Uriel KILLSWITCH] why={why} events={runs} units={units} pendingSpawns={pending} objects={objects} objectsUnresolved={unresolved} unshared={unshared} staleShares={stale} doorsPending={doorsPending}");

        lines.Insert(0, $"Kill switch done: {runs} event(s) stopped, {units} unit(s) being removed, {objects} object(s) removed, {unshared} container(s) made private.");
        if (unresolved > 0)
            lines.Add($"{unresolved} spawned object(s) couldn't be found right now and are still tracked; run it again once their area is loaded.");
        if (doorsPending > 0)
            lines.Add($"{doorsPending} event door(s) weren't found yet; Uriel keeps trying to put them back.");
        return lines;
    }

    /// <summary>Boot: run once if the config asks for it, then switch the setting back off. Objects that weren't
    /// found at boot get one more try a minute later.</summary>
    public static void RunOnBootIfRequested()
    {
        if (!Settings.KillSwitch_RestoreVanillaOnBoot.Value) return;
        Core.Log.LogWarning("[Uriel KILLSWITCH] RestoreVanillaOnBoot is set: restoring vanilla.");
        RestoreVanilla("boot");
        Settings.KillSwitch_RestoreVanillaOnBoot.Value = false; // the config file saves on set
        if (Core.ObjectSpawn is { RecordCount: > 0 })
            Tick.RunLater(3600, () =>
            {
                var (removed, unresolved) = Core.ObjectSpawn.PurgeAll("boot-retry");
                Core.Log.LogInfo($"[Uriel KILLSWITCH] boot retry removed={removed} unresolved={unresolved}");
            });
    }
}
