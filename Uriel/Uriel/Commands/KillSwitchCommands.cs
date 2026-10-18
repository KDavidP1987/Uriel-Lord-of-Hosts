using System;
using Uriel.Config;
using Uriel.Services;
using VampireCommandFramework;

namespace Uriel.Commands;

/// <summary>
/// Server-wide admin controls: which features are on, and the kill switches that undo what Uriel did. None of these
/// read the feature switches, so they work with a feature (or the whole mod, [General] Enabled) switched off.
/// </summary>
[CommandGroup("uriel")]
internal static class KillSwitchCommands
{
    static DateTime _killArmedUntil = DateTime.MinValue;
    static DateTime _purgeArmedUntil = DateTime.MinValue;

    static bool IsConfirm(string arg) => "confirm".Equals(arg?.Trim(), StringComparison.OrdinalIgnoreCase);

    [Command("features", description: "Admin: show which Uriel features are switched on (config file BepInEx/config/kdpen.Uriel.cfg). Usage: .uriel features", adminOnly: true)]
    public static void Features(ChatCommandContext ctx)
    {
        static string S(bool on) => on ? "on" : "OFF";
        string master = Settings.Master_Enabled.Value ? "" : " Master switch [General] Enabled is OFF: everything below is off.";
        ctx.Reply($"Uriel features:{master} Stair swap {S(Settings.StairSwapOn)}. Object spawning {S(Settings.ObjectSpawnOn)}" +
                  $"{(Settings.ObjectSpawnOn && Settings.ObjectSpawn_AdminOnly.Value ? " (admins only)" : "")}. " +
                  $"NPC units and castle events {S(Settings.CastleEventsOn)}{(Settings.CastleEventsOn && !Settings.CastleEvents_PlayerEvents.Value ? " (admins only)" : "")}, " +
                  $"event doors {S(Settings.DoorGatesOn)}. Public chests {S(Settings.PublicStorageOn)}, prison cells {S(Settings.PublicPrisonOn)}, " +
                  $"paid shares {S(Settings.StorageCostsOn)}. Changes to the config take effect at the next server start.");
    }

    [Command("killswitch", description: "Admin: restore vanilla server-wide. Stops every castle event (doors put back), removes every Uriel unit and every Uriel-spawned object, and makes every shared chest and prison cell private. Run once to arm, then '.uriel killswitch confirm' within 30 s. Usage: .uriel killswitch [confirm]", adminOnly: true)]
    public static void Kill(ChatCommandContext ctx, string confirm = null)
    {
        if (!Core.IsReady) { ctx.Reply("Uriel is not yet initialized."); return; }
        if (!IsConfirm(confirm))
        {
            _killArmedUntil = DateTime.UtcNow.AddSeconds(30);
            ctx.Reply($"This removes everything Uriel put in the world: {Core.ObjectSpawn?.RecordCount ?? 0} tracked object(s), every Uriel unit, " +
                      $"every running event, and {Core.PublicStorage?.Entries.Count ?? 0} share(s). Saved settings are kept. " +
                      "Run '.uriel killswitch confirm' within 30 s. To keep it off afterwards, set [General] Enabled = false in the config.");
            return;
        }
        if (DateTime.UtcNow > _killArmedUntil) { ctx.Reply("Not armed. Run '.uriel killswitch' first."); return; }
        _killArmedUntil = DateTime.MinValue;
        Core.Log.LogWarning($"[Uriel KILLSWITCH] run by admin {ctx.Event.User.PlatformId}");
        foreach (var line in KillSwitch.RestoreVanilla("command")) ctx.Reply(line);
    }

    [Command("purgeall", description: "Admin: remove EVERY Uriel-spawned object on the server, on every plot. Native objects are never touched. Run once to arm, then '.uriel purgeall confirm' within 30 s. Usage: .uriel purgeall [confirm]", adminOnly: true)]
    public static void PurgeAll(ChatCommandContext ctx, string confirm = null)
    {
        if (!Core.IsReady || Core.ObjectSpawn is null) { ctx.Reply("Uriel is not yet initialized."); return; }
        if (!IsConfirm(confirm))
        {
            _purgeArmedUntil = DateTime.UtcNow.AddSeconds(30);
            ctx.Reply($"This removes every Uriel-spawned object on the server ({Core.ObjectSpawn.RecordCount} tracked). Run '.uriel purgeall confirm' within 30 s.");
            return;
        }
        if (DateTime.UtcNow > _purgeArmedUntil) { ctx.Reply("Not armed. Run '.uriel purgeall' first."); return; }
        _purgeArmedUntil = DateTime.MinValue;
        var (removed, unresolved) = Core.ObjectSpawn.PurgeAll($"admin {ctx.Event.User.PlatformId}");
        ctx.Reply($"Removed {removed} Uriel-spawned object(s) server-wide." +
                  (unresolved > 0 ? $" {unresolved} couldn't be found right now and are still tracked; run it again once their area is loaded." : ""));
    }
}
