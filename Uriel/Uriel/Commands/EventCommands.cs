using Uriel.Config;
using VampireCommandFramework;

namespace Uriel.Commands;

/// <summary>
/// Castle Events Phase 2 MVP (docs/features/CASTLE_EVENTS.md): a castle's event layout (groups of spawn markers, each
/// group optionally holding a door), reusable templates, and a manually started run. Admins anywhere; castle owners on
/// their own plot when [CastleEvents] PlayerEvents is on. Permission checks live in CastleEventService.
/// </summary>
[CommandGroup("uriel event")]
internal static class EventCommands
{
    internal static bool Ready(ChatCommandContext ctx)
    {
        if (!Core.IsReady || Core.Events is null) { ctx.Reply("Uriel is not yet initialized."); return false; }
        if (!Settings.CastleEvents_Enabled.Value)
        {
            ctx.Reply("Castle events are disabled by the server admin (CastleEvents.Enabled).");
            return false;
        }
        return true;
    }

    static void ReplyLines(ChatCommandContext ctx, System.Collections.Generic.List<string> lines)
    {
        foreach (var line in lines) ctx.Reply(line);
    }

    [Command("marker", description: "Place a spawn marker where you stand, in a group (made on its first marker, on the floor you're on). Slot mob or boss. Unit: a name/GUID, or - to let the template decide. Count/level 0 = the template's. Contain: plot, room, storey, <m>, or - for the server default. Usage: .uriel event marker <group> [mob|boss] [unit|-] [count] [level] [contain|-]")]
    public static void Marker(ChatCommandContext ctx, string group, string slot = "mob", string unit = "-", int count = 0, int level = 0, string contain = "-")
    {
        if (!Ready(ctx)) return;
        ctx.Reply(Core.Events.AddMarker(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin, group, slot, unit, count, level, contain));
    }

    [Command("door", description: "Bind the castle door nearest you (within 6 m) to a group: during an event it stays shut and locked until every unit of the group is defeated, then opens. Usage: .uriel event door <group>")]
    public static void Door(ChatCommandContext ctx, string group)
    {
        if (!Ready(ctx)) return;
        ctx.Reply(Core.Events.BindDoor(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin, group, unbind: false));
    }

    [Command("undoor", description: "Unbind a group's door. Usage: .uriel event undoor <group>")]
    public static void Undoor(ChatCommandContext ctx, string group)
    {
        if (!Ready(ctx)) return;
        ctx.Reply(Core.Events.BindDoor(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin, group, unbind: true));
    }

    [Command("remove", description: "Remove a marker (by its #number) or a whole group (by name) from this plot's layout; paid markers are refunded. Usage: .uriel event remove <#id|group>")]
    public static void Remove(ChatCommandContext ctx, string what)
    {
        if (!Ready(ctx)) return;
        ctx.Reply(Core.Events.Remove(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin, what));
    }

    [Command("list", description: "Show this plot's event layout: groups by floor, their doors and markers. Usage: .uriel event list")]
    public static void List(ChatCommandContext ctx)
    {
        if (!Ready(ctx)) return;
        ReplyLines(ctx, Core.Events.Describe(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin));
    }

    [Command("start", description: "Start this plot's event: every marker spawns, every group's door shuts and locks until that group is defeated. A template fills markers that don't name a unit. Usage: .uriel event start [template]")]
    public static void Start(ChatCommandContext ctx, string template = "-")
    {
        if (!Ready(ctx)) return;
        ctx.Reply(Core.Events.StartRun(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin, template));
    }

    [Command("stop", description: "Stop this plot's event: its units are removed and its doors put back as they were. Usage: .uriel event stop")]
    public static void Stop(ChatCommandContext ctx)
    {
        if (!Ready(ctx)) return;
        ctx.Reply(Core.Events.StopRun(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin));
    }

    [Command("status", description: "Show the running event on this plot: groups cleared, units left, doors. Usage: .uriel event status")]
    public static void Status(ChatCommandContext ctx)
    {
        if (!Ready(ctx)) return;
        ReplyLines(ctx, Core.Events.Status(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin));
    }
}

/// <summary>Castle Events templates: per floor, per group, per slot (mob/boss), the units a layout's markers spawn.</summary>
[CommandGroup("uriel template")]
internal static class TemplateCommands
{
    [Command("create", description: "Create an event template (an admin's is a server template everyone can use; a player's is their own). Usage: .uriel template create <name>")]
    public static void Create(ChatCommandContext ctx, string name)
    {
        if (!EventCommands.Ready(ctx)) return;
        ctx.Reply(Core.Events.CreateTemplate(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin, name));
    }

    [Command("set", description: "Set what a template spawns for a floor (1, 2, ... or *) and group (a name or *), per slot: units separated by commas (one is picked at random per marker), a count per marker, a level (0 = the unit's own). Usage: .uriel template set <name> <floor|*> <group|*> <mob|boss> <unit[,unit...]> [count] [level]")]
    public static void Set(ChatCommandContext ctx, string name, string floor, string group, string slot, string units, int count = 1, int level = 0)
    {
        if (!EventCommands.Ready(ctx)) return;
        ctx.Reply(Core.Events.SetTemplate(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin, name, floor, group, slot, units, count, level));
    }

    [Command("list", description: "List the templates you can use. Usage: .uriel template list")]
    public static void List(ChatCommandContext ctx)
    {
        if (!EventCommands.Ready(ctx)) return;
        foreach (var line in Core.Events.ShowTemplate(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin, null)) ctx.Reply(line);
    }

    [Command("show", description: "Show a template's entries. Usage: .uriel template show <name>")]
    public static void Show(ChatCommandContext ctx, string name)
    {
        if (!EventCommands.Ready(ctx)) return;
        foreach (var line in Core.Events.ShowTemplate(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin, name)) ctx.Reply(line);
    }

    [Command("delete", description: "Delete a template (yours, or any as admin). Usage: .uriel template delete <name>")]
    public static void Delete(ChatCommandContext ctx, string name)
    {
        if (!EventCommands.Ready(ctx)) return;
        ctx.Reply(Core.Events.DeleteTemplate(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin, name));
    }
}
