using Uriel.Config;
using VampireCommandFramework;

namespace Uriel.Commands;

/// <summary>
/// Castle Events (docs/features/CASTLE_EVENTS.md): a castle's event layout (groups of spawn markers, each group
/// optionally holding a door and having its own squad), the content library (squads, floor templates, castle
/// templates), and a manually started run with optional scaling and a time limit. Admins anywhere; castle owners on
/// their own plot when [CastleEvents] PlayerEvents is on. Permission checks live in CastleEventService.
/// </summary>
[CommandGroup("uriel event")]
internal static class EventCommands
{
    internal static bool Ready(ChatCommandContext ctx)
    {
        if (!Core.IsReady || Core.Events is null) { ctx.Reply("Uriel is not yet initialized."); return false; }
        if (!Settings.CastleEventsOn)
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

    [Command("marker", description: "Place a spawn marker where you stand, in a group (made on its first marker, on the floor you're on). Slot mob or boss: where the squad's mobs or bosses stand. Unit: a name/GUID for a fixed spawn here, or - for the group's squad. Count/level: for a fixed unit. Contain: plot, room, storey, <m>, or - for the server default. Usage: .uriel event marker <group> [mob|boss] [unit|-] [count] [level] [contain|-]")]
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

    [Command("squad", description: "Give a group its own squad (- for none): what it spawns when the run's template doesn't cover it. Usage: .uriel event squad <group> <squad|->")]
    public static void Squad(ChatCommandContext ctx, string group, string squad)
    {
        if (!Ready(ctx)) return;
        ctx.Reply(Core.Events.SetGroupSquad(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin, group, squad));
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

    [Command("start", description: "Start this plot's event: each group rolls its squad (from the template, a floor template or a squad you name, else its own), every group's door shuts until that group is defeated. Options, comma-separated: easy|normal|hard|brutal, level=N, level+N, auto[+N] (highest player level here), hp*X, dmg*X, pp*X, sp*X, speed*X, aspd*X, limit=15m, faction=keep (units keep their own factions; by default the whole run is one side). Usage: .uriel event start [template|-] [options]")]
    public static void Start(ChatCommandContext ctx, string template = "-", string options = "-")
    {
        if (!Ready(ctx)) return;
        ctx.Reply(Core.Events.StartRun(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin, template, options));
    }

    [Command("stop", description: "Stop this plot's event: its units are removed and its doors put back as they were. Usage: .uriel event stop")]
    public static void Stop(ChatCommandContext ctx)
    {
        if (!Ready(ctx)) return;
        ctx.Reply(Core.Events.StopRun(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin));
    }

    [Command("stopall", description: "Admin: stop every castle event on the server: their units are removed and their doors put back as they were. Works with castle events switched off. Usage: .uriel event stopall", adminOnly: true)]
    public static void StopAll(ChatCommandContext ctx)
    {
        if (!Core.IsReady || Core.Events is null) { ctx.Reply("Uriel is not yet initialized."); return; }
        int runs = Core.Events.StopAll("stopped-all");
        int pending = Core.Events.PendingDoors;
        ctx.Reply(runs == 0 ? "No castle event is running." : $"Stopped {runs} castle event(s).{(pending > 0 ? $" {pending} door(s) weren't found yet; Uriel keeps trying to put them back." : "")}");
    }

    [Command("status", description: "Show the running event on this plot: the clock, bosses left, time left, groups cleared, units left, doors. Usage: .uriel event status")]
    public static void Status(ChatCommandContext ctx)
    {
        if (!Ready(ctx)) return;
        ReplyLines(ctx, Core.Events.Status(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin));
    }

    [Command("history", description: "Show the last event runs on this plot (result, time, time to the last boss) and the best times per template. Usage: .uriel event history")]
    public static void History(ChatCommandContext ctx)
    {
        if (!Ready(ctx)) return;
        ReplyLines(ctx, Core.Events.History(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin));
    }
}

/// <summary>Castle Events squads: what one layout group spawns. Sets are alternative combinations (one rolled per run);
/// each entry draws units from a list.</summary>
[CommandGroup("uriel squad")]
internal static class SquadCommands
{
    static Unity.Entities.Entity Me(ChatCommandContext ctx) => ctx.Event.SenderCharacterEntity;

    [Command("create", description: "Create a squad (an admin's is server-wide, a player's is their own). Usage: .uriel squad create <name>")]
    public static void Create(ChatCommandContext ctx, string name)
    {
        if (!EventCommands.Ready(ctx)) return;
        ctx.Reply(Core.Events.CreateSquad(Me(ctx), ctx.Event.User.IsAdmin, name));
    }

    [Command("add", description: "Add an entry to a squad's set (set 1 by default; the next number starts a new combination, one combination rolled per run). Role mob or boss. Units: one or more, comma-separated. Pick any = each unit at random (an assortment), distinct = that many different ones (1 of these 3 bosses). Level 0 = the unit's own. Usage: .uriel squad add <name> <mob|boss> <unit[,unit...]> [count] [any|distinct] [level] [set]")]
    public static void Add(ChatCommandContext ctx, string name, string role, string units, int count = 1, string pick = "any", int level = 0, int set = 1)
    {
        if (!EventCommands.Ready(ctx)) return;
        ctx.Reply(Core.Events.AddSquadEntry(Me(ctx), ctx.Event.User.IsAdmin, name, role, units, count, pick, level, set));
    }

    [Command("extend", description: "Add more units to an entry's list (a chat line is short, so a long list takes several commands). Usage: .uriel squad extend <name> <set> <entry> <unit[,unit...]>")]
    public static void Extend(ChatCommandContext ctx, string name, int set, int entry, string units)
    {
        if (!EventCommands.Ready(ctx)) return;
        ctx.Reply(Core.Events.ExtendSquadEntry(Me(ctx), ctx.Event.User.IsAdmin, name, set, entry, units));
    }

    [Command("remove", description: "Remove an entry from a squad's set, or the whole set (entry 0). Usage: .uriel squad remove <name> <set> [entry]")]
    public static void Remove(ChatCommandContext ctx, string name, int set, int entry = 0)
    {
        if (!EventCommands.Ready(ctx)) return;
        ctx.Reply(Core.Events.RemoveSquadEntry(Me(ctx), ctx.Event.User.IsAdmin, name, set, entry));
    }

    [Command("weight", description: "How often a squad's set is rolled, relative to its other sets (default 1). Usage: .uriel squad weight <name> <set> <1-100>")]
    public static void Weight(ChatCommandContext ctx, string name, int set, int weight)
    {
        if (!EventCommands.Ready(ctx)) return;
        ctx.Reply(Core.Events.WeighSquadSet(Me(ctx), ctx.Event.User.IsAdmin, name, set, weight));
    }

    [Command("list", description: "List the squads you can use. Usage: .uriel squad list")]
    public static void List(ChatCommandContext ctx)
    {
        if (!EventCommands.Ready(ctx)) return;
        foreach (var line in Core.Events.ListSquads(Me(ctx), ctx.Event.User.IsAdmin)) ctx.Reply(line);
    }

    [Command("show", description: "Show a squad's sets and entries. Usage: .uriel squad show <name>")]
    public static void Show(ChatCommandContext ctx, string name)
    {
        if (!EventCommands.Ready(ctx)) return;
        foreach (var line in Core.Events.ShowSquad(Me(ctx), ctx.Event.User.IsAdmin, name)) ctx.Reply(line);
    }

    [Command("delete", description: "Delete a squad (yours, or any as admin). Usage: .uriel squad delete <name>")]
    public static void Delete(ChatCommandContext ctx, string name)
    {
        if (!EventCommands.Ready(ctx)) return;
        ctx.Reply(Core.Events.DeleteSquad(Me(ctx), ctx.Event.User.IsAdmin, name));
    }
}

/// <summary>Castle Events floor templates: which squad each layout group on a floor gets.</summary>
[CommandGroup("uriel floor")]
internal static class FloorCommands
{
    static Unity.Entities.Entity Me(ChatCommandContext ctx) => ctx.Event.SenderCharacterEntity;

    [Command("create", description: "Create a floor template. Usage: .uriel floor create <name>")]
    public static void Create(ChatCommandContext ctx, string name)
    {
        if (!EventCommands.Ready(ctx)) return;
        ctx.Reply(Core.Events.CreateFloorPlan(Me(ctx), ctx.Event.User.IsAdmin, name));
    }

    [Command("assign", description: "Give a layout group (by name, or * for every group on the floor) a squad; several comma-separated = one rolled per run; - unassigns. Usage: .uriel floor assign <name> <group|*> <squad[,squad...]|->")]
    public static void Assign(ChatCommandContext ctx, string name, string group, string squads)
    {
        if (!EventCommands.Ready(ctx)) return;
        ctx.Reply(Core.Events.AssignFloorPlan(Me(ctx), ctx.Event.User.IsAdmin, name, group, squads));
    }

    [Command("list", description: "List the floor templates you can use. Usage: .uriel floor list")]
    public static void List(ChatCommandContext ctx)
    {
        if (!EventCommands.Ready(ctx)) return;
        foreach (var line in Core.Events.ListFloorPlans(Me(ctx), ctx.Event.User.IsAdmin)) ctx.Reply(line);
    }

    [Command("show", description: "Show a floor template's groups and squads. Usage: .uriel floor show <name>")]
    public static void Show(ChatCommandContext ctx, string name)
    {
        if (!EventCommands.Ready(ctx)) return;
        foreach (var line in Core.Events.ShowFloorPlan(Me(ctx), ctx.Event.User.IsAdmin, name)) ctx.Reply(line);
    }

    [Command("delete", description: "Delete a floor template (yours, or any as admin). Usage: .uriel floor delete <name>")]
    public static void Delete(ChatCommandContext ctx, string name)
    {
        if (!EventCommands.Ready(ctx)) return;
        ctx.Reply(Core.Events.DeleteFloorPlan(Me(ctx), ctx.Event.User.IsAdmin, name));
    }
}

/// <summary>Castle Events castle templates: which floor template each floor gets, plus default run options.</summary>
[CommandGroup("uriel template")]
internal static class TemplateCommands
{
    static Unity.Entities.Entity Me(ChatCommandContext ctx) => ctx.Event.SenderCharacterEntity;

    [Command("create", description: "Create a castle-wide event template. Usage: .uriel template create <name>")]
    public static void Create(ChatCommandContext ctx, string name)
    {
        if (!EventCommands.Ready(ctx)) return;
        ctx.Reply(Core.Events.CreateTemplate(Me(ctx), ctx.Event.User.IsAdmin, name));
    }

    [Command("floor", description: "Give a floor (1, 2, ... or * for every floor) a floor template; several comma-separated = one rolled per floor per run (a theme); - clears. Usage: .uriel template floor <name> <floor|*> <floor template[,...]|->")]
    public static void Floor(ChatCommandContext ctx, string name, string floor, string plans)
    {
        if (!EventCommands.Ready(ctx)) return;
        ctx.Reply(Core.Events.SetTemplateFloor(Me(ctx), ctx.Event.User.IsAdmin, name, floor, plans));
    }

    [Command("options", description: "Default run options for a template (the start's own options add to them): easy|normal|hard|brutal, level=N, level+N, auto[+N], hp*X, dmg*X, pp*X, sp*X, speed*X, aspd*X, limit=15m, faction=keep|one; - for none. Usage: .uriel template options <name> <options|->")]
    public static void Options(ChatCommandContext ctx, string name, string options)
    {
        if (!EventCommands.Ready(ctx)) return;
        ctx.Reply(Core.Events.SetTemplateOptions(Me(ctx), ctx.Event.User.IsAdmin, name, options));
    }

    [Command("list", description: "List the templates you can use. Usage: .uriel template list")]
    public static void List(ChatCommandContext ctx)
    {
        if (!EventCommands.Ready(ctx)) return;
        foreach (var line in Core.Events.ListTemplates(Me(ctx), ctx.Event.User.IsAdmin)) ctx.Reply(line);
    }

    [Command("show", description: "Show a template's floors and options. Usage: .uriel template show <name>")]
    public static void Show(ChatCommandContext ctx, string name)
    {
        if (!EventCommands.Ready(ctx)) return;
        foreach (var line in Core.Events.ShowTemplate(Me(ctx), ctx.Event.User.IsAdmin, name)) ctx.Reply(line);
    }

    [Command("delete", description: "Delete a template (yours, or any as admin). Usage: .uriel template delete <name>")]
    public static void Delete(ChatCommandContext ctx, string name)
    {
        if (!EventCommands.Ready(ctx)) return;
        ctx.Reply(Core.Events.DeleteTemplate(Me(ctx), ctx.Event.User.IsAdmin, name));
    }
}
