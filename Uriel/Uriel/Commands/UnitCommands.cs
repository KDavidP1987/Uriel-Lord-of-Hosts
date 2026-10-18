using Uriel.Config;
using VampireCommandFramework;

namespace Uriel.Commands;

/// <summary>
/// Castle Events Phase 1 (docs/features/CASTLE_EVENTS.md): admin unit spawner plus the indoor-spike readings.
/// Admin-only and off by default ([CastleEvents] Enabled).
/// </summary>
[CommandGroup("uriel unit")]
internal static class UnitCommands
{
    static bool Ready(ChatCommandContext ctx)
    {
        if (!Core.IsReady || Core.Units is null) { ctx.Reply("Uriel is not yet initialized."); return false; }
        if (!Settings.CastleEventsOn)
        {
            ctx.Reply("Castle events are disabled by the server admin (CastleEvents.Enabled).");
            return false;
        }
        return true;
    }

    /// <summary>Counting and removing units works with castle events switched off, so leftovers can always be cleared.</summary>
    static bool CleanupReady(ChatCommandContext ctx)
    {
        if (!Core.IsReady || Core.Units is null) { ctx.Reply("Uriel is not yet initialized."); return false; }
        return true;
    }

    [Command("spawn", description: "Spawn NPC units into the castle plot you're in, 2.5 m in front of you ('aim' = at your aim point). Optional level (0 = the prefab's own). Units never leave the plot; 'contain' narrows that: room, storey, a leash radius in metres, or a mix like room,15. Usage: .uriel unit spawn <name|guid> [count] [level] [here|aim] [plot|room|storey|<m>]", adminOnly: true)]
    public static void Spawn(ChatCommandContext ctx, string prefab, int count = 1, int level = 0, string where = "here", string contain = "plot")
    {
        if (!Ready(ctx)) return;
        bool atAim = string.Equals(where, "aim", System.StringComparison.OrdinalIgnoreCase);
        Core.Units.Enqueue(ctx.Event.SenderCharacterEntity, ctx.Event.User.PlatformId, prefab, count, level, atAim, contain, out string message);
        ctx.Reply(message);
    }

    [Command("list", description: "Count the Uriel units on the server, per plot. Usage: .uriel unit list", adminOnly: true)]
    public static void List(ChatCommandContext ctx)
    {
        if (!CleanupReady(ctx)) return;
        ctx.Reply(Core.Units.List(ctx.Event.SenderCharacterEntity));
    }

    [Command("kill", description: "Kill the Uriel units within the radius (default 15 m): all, boss or mob. A real death (event groups clear, doors open), unlike despawn. Only Uriel's own units. Usage: .uriel unit kill [radius] [all|boss|mob]", adminOnly: true)]
    public static void Kill(ChatCommandContext ctx, float radius = 15f, string which = "all")
    {
        if (!CleanupReady(ctx)) return;
        ctx.Reply(Core.Units.KillNear(ctx.Event.SenderCharacterEntity, radius, which));
    }

    [Command("despawn", description: "Remove the nearest Uriel unit within the radius (default 10 m). Usage: .uriel unit despawn [radius]", adminOnly: true)]
    public static void Despawn(ChatCommandContext ctx, float radius = 10f)
    {
        if (!CleanupReady(ctx)) return;
        ctx.Reply(Core.Units.DespawnNearest(ctx.Event.SenderCharacterEntity, radius));
    }

    [Command("clear", description: "Remove every Uriel unit on the castle plot you're standing in. Usage: .uriel unit clear", adminOnly: true)]
    public static void Clear(ChatCommandContext ctx)
    {
        if (!CleanupReady(ctx)) return;
        ctx.Reply(Core.Units.ClearPlot(ctx.Event.SenderCharacterEntity));
    }

    [Command("purge", description: "Kill switch: remove EVERY Uriel unit on the server. Run once to arm, then '.uriel unit purge confirm' within 30 s. Usage: .uriel unit purge [confirm]", adminOnly: true)]
    public static void Purge(ChatCommandContext ctx, string confirm = "")
    {
        // The kill switch works even with the feature switched off.
        if (!Core.IsReady || Core.Units is null) { ctx.Reply("Uriel is not yet initialized."); return; }
        ctx.Reply(Core.Units.Purge(string.Equals(confirm, "confirm", System.StringComparison.OrdinalIgnoreCase)));
    }

    [Command("status", description: "Write each Uriel unit within the radius (default 30 m) to the server log: position, height level, health, behaviour state, target. Usage: .uriel unit status [radius]", adminOnly: true)]
    public static void Status(ChatCommandContext ctx, float radius = 30f)
    {
        if (!Ready(ctx)) return;
        ctx.Reply(Core.Units.Status(ctx.Event.SenderCharacterEntity, radius));
    }

    [Command("probemap", description: "Write an ASCII map of the tile map around you to the server log ('#' blocked, '.' floor, ' ' no ground), at your height level and the one above. Usage: .uriel unit probemap [label] [radius 2-20]", adminOnly: true)]
    public static void ProbeMap(ChatCommandContext ctx, string label = "-", int radius = 10)
    {
        if (!Ready(ctx)) return;
        ctx.Reply(Core.Units.ProbeMap(ctx.Event.SenderCharacterEntity, radius, label));
    }

    [Command("rooms", description: "Write the castle's rooms (the game's own: floors, storeys, enclosed or not) and a map of the plot per storey to the server log. Room containment keeps a unit in the room it spawned in. Usage: .uriel unit rooms", adminOnly: true)]
    public static void Rooms(ChatCommandContext ctx)
    {
        if (!Ready(ctx)) return;
        ctx.Reply(Core.Units.Rooms(ctx.Event.SenderCharacterEntity));
    }

    [Command("door", description: "Spike 7: drive the nearest castle door of this plot within 6 m from the server: open, close, lock (players can't use it), unlock, or show. Usage: .uriel unit door <open|close|lock|unlock|show>", adminOnly: true)]
    public static void Door(ChatCommandContext ctx, string action = "show")
    {
        if (!Ready(ctx)) return;
        if (!Settings.DoorGatesOn) { ctx.Reply("Event doors are disabled by the server admin (CastleEvents.DoorGates)."); return; }
        var character = ctx.Event.SenderCharacterEntity;
        if (!character.TryGetComponent<Unity.Transforms.Translation>(out var me)) { ctx.Reply("Can't read your position."); return; }
        if (!Core.ObjectSpawn.TryResolvePlot(me.Value, out var heart, out _)) { ctx.Reply("You're not standing in a castle plot."); return; }
        var door = Services.CastleDoors.Nearest(heart, me.Value, 6f);
        if (door == Unity.Entities.Entity.Null) { ctx.Reply("No castle door within 6 m."); return; }
        bool ok = action.ToLowerInvariant() switch
        {
            "open" => Services.CastleDoors.SetOpen(door, true),
            "close" => Services.CastleDoors.SetOpen(door, false),
            "lock" => Services.CastleDoors.SetLocked(door, true),
            "unlock" => Services.CastleDoors.SetLocked(door, false),
            _ => true,
        };
        string state = Services.CastleDoors.Describe(door);
        Core.Log.LogInfo($"[Uriel DOOR] action={action} ok={ok} {state}");
        ctx.Reply($"door {action}: {(ok ? "ok" : "not supported")}; {state}");
    }

    [Command("probe", description: "Read the tile map where you stand (or at your aim point): height level, and free/blocked and ground/air for nearby levels. Label it for the log. Usage: .uriel unit probe [label] [here|aim]", adminOnly: true)]
    public static void Probe(ChatCommandContext ctx, string label = "-", string where = "here")
    {
        if (!Ready(ctx)) return;
        bool atAim = string.Equals(where, "aim", System.StringComparison.OrdinalIgnoreCase);
        ctx.Reply(Core.Units.Probe(ctx.Event.SenderCharacterEntity, atAim, label));
    }
}
