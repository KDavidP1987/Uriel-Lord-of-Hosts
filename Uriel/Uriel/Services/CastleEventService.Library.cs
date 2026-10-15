using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Stunlock.Core;
using Unity.Entities;
using Uriel.Config;

namespace Uriel.Services;

/// <summary>
/// The event content library (docs/features/CASTLE_EVENTS.md › Layers): three reusable layers, each server-wide (an
/// admin's, Owner 0) or a player's own.
///
///   Squad: what one group spawns. One or more SETS (alternative combinations; one is rolled per run, by weight), each
///     a list of ENTRIES: a role (mob|boss), a pool of units, a count, and how the count draws from the pool ("any":
///     each unit at random, a random assortment; "distinct": that many different units, e.g. 1 of these 3 bosses, 2 of
///     these 10), an optional level.
///   Floor template: for one floor, which squad each layout group (by name, or "*" for any group) gets; several squads
///     = one rolled per run.
///   Castle template: which floor template each floor (1, 2, ... or "*") gets; several = one rolled per floor per run
///     (a per-floor theme). Plus default run options (scaling, time limit).
/// </summary>
internal sealed partial class CastleEventService
{
    internal interface IOwned
    {
        string Name { get; }
        ulong Owner { get; }
    }

    internal sealed class SquadEntry
    {
        public string Role { get; set; } = "mob";
        public List<int> Units { get; set; } = new();
        public int Count { get; set; } = 1;
        public bool Distinct { get; set; }
        public int Level { get; set; }
    }

    internal sealed class SquadSet
    {
        public int Weight { get; set; } = 1;
        public List<SquadEntry> Entries { get; set; } = new();
    }

    internal sealed class Squad : IOwned
    {
        public string Name { get; set; }
        public ulong Owner { get; set; }
        public List<SquadSet> Sets { get; set; } = new();
    }

    internal sealed class FloorPlan : IOwned
    {
        public string Name { get; set; }
        public ulong Owner { get; set; }
        public Dictionary<string, List<string>> Groups { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class EventTemplate : IOwned
    {
        public string Name { get; set; }
        public ulong Owner { get; set; }
        public Dictionary<string, List<string>> Floors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string Options { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public string Squad { get; set; }   // a squad started on its own: every group gets it
    }

    /// <summary>One unit a squad roll produced.</summary>
    internal readonly record struct Rolled(int Guid, bool Boss, int Level);

    static string Names(IEnumerable<int> guids) => string.Join("/", guids.Select(g => new PrefabGUID(g).GetPrefabName()));

    static string Who(ulong owner) => owner == 0 ? "server" : "player's";

    static IEnumerable<T> VisibleOf<T>(Dictionary<string, T> d, ulong steamId, bool isAdmin) where T : IOwned =>
        d.Values.Where(t => t.Owner == 0 || t.Owner == steamId || isAdmin);

    static T Find<T>(Dictionary<string, T> d, string name, ulong steamId, bool isAdmin) where T : class, IOwned =>
        name != null && d.TryGetValue(name, out var t) && (t.Owner == 0 || t.Owner == steamId || isAdmin) ? t : null;

    static T Editable<T>(Dictionary<string, T> d, string kind, string name, Entity character, bool isAdmin, out string message) where T : class, IOwned
    {
        message = null;
        if (!d.TryGetValue(name ?? "", out var t)) { message = $"No {kind} '{name}'."; return null; }
        if (!isAdmin && t.Owner != character.GetSteamId()) { message = $"The {kind} '{name}' isn't yours."; return null; }
        return t;
    }

    static string CanAuthor(bool isAdmin) =>
        !isAdmin && !Settings.CastleEvents_PlayerEvents.Value ? "Castle events are admin-only on this server (CastleEvents.PlayerEvents)." : null;

    /// <summary>Names are one namespace across squads, floor templates and castle templates, so 'start <name>' is never
    /// ambiguous.</summary>
    string NameTaken(string name) =>
        _file.Squads.ContainsKey(name) ? "squad" : _file.FloorPlans.ContainsKey(name) ? "floor template" : _file.EventTemplates.ContainsKey(name) ? "template" : null;

    string Create<T>(Dictionary<string, T> d, string kind, Entity character, bool isAdmin, string name, Func<string, ulong, T> make, string next) where T : IOwned
    {
        if (CanAuthor(isAdmin) is string no) return no;
        if (!ValidName(name)) return $"A {kind} name is 1-24 letters, digits, '-' or '_'.";
        if (NameTaken(name) is string taken) return $"The name '{name}' is already a {taken}.";
        ulong owner = isAdmin ? 0 : character.GetSteamId();
        d[name] = make(name, owner);
        Save();
        Core.Log.LogInfo($"[Uriel EVENT] {kind.Replace(' ', '-')}-created name={name} owner={owner}");
        return $"The {kind} '{name}' is created ({(owner == 0 ? "server-wide, everyone can use it" : "yours")}). {next}";
    }

    string Delete<T>(Dictionary<string, T> d, string kind, Entity character, bool isAdmin, string name) where T : class, IOwned
    {
        var t = Editable(d, kind, name, character, isAdmin, out string message);
        if (t == null) return message;
        d.Remove(t.Name);
        Save();
        Core.Log.LogInfo($"[Uriel EVENT] {kind.Replace(' ', '-')}-deleted name={t.Name}");
        return $"The {kind} '{t.Name}' is deleted.";
    }

    List<string> ListOf<T>(Dictionary<string, T> d, string kind, string create, Entity character, bool isAdmin) where T : IOwned
    {
        var list = VisibleOf(d, character.GetSteamId(), isAdmin).OrderBy(t => t.Owner).ThenBy(t => t.Name).ToList();
        return new List<string>
        {
            list.Count == 0 ? $"No {kind}s yet. {create}" : $"{char.ToUpper(kind[0])}{kind[1..]}s: " + string.Join(", ", list.Select(t => $"{t.Name}{(t.Owner == 0 ? "" : " (player's)")}")),
        };
    }

    // ------------------------------------------------------------------ squads

    public string CreateSquad(Entity character, bool isAdmin, string name) =>
        Create(_file.Squads, "squad", character, isAdmin, name, (n, o) => new Squad { Name = n, Owner = o },
            $"Fill it: '.uriel squad add {name} <mob|boss> <unit[,unit...]> [count] [any|distinct] [level] [set]'.");

    public string DeleteSquad(Entity character, bool isAdmin, string name) => Delete(_file.Squads, "squad", character, isAdmin, name);

    public List<string> ListSquads(Entity character, bool isAdmin) =>
        ListOf(_file.Squads, "squad", "'.uriel squad create <name>'.", character, isAdmin);

    public string AddSquadEntry(Entity character, bool isAdmin, string name, string role, string units, int count, string pick, int level, int set)
    {
        var sq = Editable(_file.Squads, "squad", name, character, isAdmin, out string message);
        if (sq == null) return message;
        role = role.ToLowerInvariant();
        if (role != "mob" && role != "boss") return "The role is mob or boss.";
        pick = (pick ?? "any").ToLowerInvariant();
        if (pick != "any" && pick != "distinct") return "The pick is any (each unit at random) or distinct (that many different units).";
        if (count < 1 || count > 50) return "The count is 1-50.";
        if (level < 0 || level > 200) return "The level is 0 (the unit's own) to 200.";
        if (set < 1 || set > sq.Sets.Count + 1) return $"The set is 1-{sq.Sets.Count + 1} ({sq.Sets.Count + 1} starts a new combination).";
        var entry = new SquadEntry { Role = role, Count = count, Distinct = pick == "distinct", Level = level };
        foreach (var u in units.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!UnitSpawnService.TryResolveUnitPrefab(u, out int guid, out _, out message)) return message;
            entry.Units.Add(guid);
        }
        if (entry.Units.Count == 0) return "Name at least one unit.";
        if (entry.Distinct && count > entry.Units.Count) return $"'distinct' draws {count} different units but the list has {entry.Units.Count}; use any, or name more units.";
        if (set > sq.Sets.Count) sq.Sets.Add(new SquadSet());
        sq.Sets[set - 1].Entries.Add(entry);
        Save();
        Core.Log.LogInfo($"[Uriel EVENT] squad-entry name={sq.Name} set={set} entry={sq.Sets[set - 1].Entries.Count} role={role} units={Names(entry.Units)} count={count} pick={pick} level={level}");
        return $"Squad '{sq.Name}', set {set}, entry {sq.Sets[set - 1].Entries.Count}: {Describe(entry)}.";
    }

    public string RemoveSquadEntry(Entity character, bool isAdmin, string name, int set, int entry)
    {
        var sq = Editable(_file.Squads, "squad", name, character, isAdmin, out string message);
        if (sq == null) return message;
        if (set < 1 || set > sq.Sets.Count) return $"Squad '{sq.Name}' has {sq.Sets.Count} set(s).";
        if (entry == 0)
        {
            sq.Sets.RemoveAt(set - 1);
            Save();
            return $"Squad '{sq.Name}': set {set} removed.";
        }
        var es = sq.Sets[set - 1].Entries;
        if (entry < 1 || entry > es.Count) return $"Set {set} has {es.Count} entr{(es.Count == 1 ? "y" : "ies")}.";
        es.RemoveAt(entry - 1);
        if (es.Count == 0) sq.Sets.RemoveAt(set - 1);
        Save();
        return $"Squad '{sq.Name}': set {set} entry {entry} removed.";
    }

    /// <summary>Add more units to an entry's list (a chat line is short, so a long list takes several commands).</summary>
    public string ExtendSquadEntry(Entity character, bool isAdmin, string name, int set, int entry, string units)
    {
        var sq = Editable(_file.Squads, "squad", name, character, isAdmin, out string message);
        if (sq == null) return message;
        if (set < 1 || set > sq.Sets.Count) return $"Squad '{sq.Name}' has {sq.Sets.Count} set(s).";
        var es = sq.Sets[set - 1].Entries;
        if (entry < 1 || entry > es.Count) return $"Set {set} has {es.Count} entr{(es.Count == 1 ? "y" : "ies")}.";
        var e = es[entry - 1];
        foreach (var u in units.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!UnitSpawnService.TryResolveUnitPrefab(u, out int guid, out _, out message)) return message;
            if (!e.Units.Contains(guid)) e.Units.Add(guid);
        }
        Save();
        Core.Log.LogInfo($"[Uriel EVENT] squad-extend name={sq.Name} set={set} entry={entry} units={e.Units.Count} pool={Names(e.Units)}");
        return $"Squad '{sq.Name}', set {set}, entry {entry}: {Describe(e)}.";
    }

    public string WeighSquadSet(Entity character, bool isAdmin, string name, int set, int weight)
    {
        var sq = Editable(_file.Squads, "squad", name, character, isAdmin, out string message);
        if (sq == null) return message;
        if (set < 1 || set > sq.Sets.Count) return $"Squad '{sq.Name}' has {sq.Sets.Count} set(s).";
        if (weight < 1 || weight > 100) return "A weight is 1-100.";
        sq.Sets[set - 1].Weight = weight;
        Save();
        return $"Squad '{sq.Name}': set {set} weight {weight} (of {sq.Sets.Sum(s => s.Weight)}).";
    }

    static string Describe(SquadEntry e) =>
        e.Units.Count == 1
            ? $"{e.Count} {e.Role}{(e.Count == 1 ? "" : "s")} {Names(e.Units)}{(e.Level > 0 ? $" L{e.Level}" : "")}"
            : $"{e.Count} {e.Role}{(e.Count == 1 ? "" : "s")} {(e.Distinct ? "different " : "")}of {Names(e.Units)}{(e.Level > 0 ? $" L{e.Level}" : "")}";

    public List<string> ShowSquad(Entity character, bool isAdmin, string name)
    {
        var sq = Find(_file.Squads, name, character.GetSteamId(), isAdmin);
        if (sq == null) return new List<string> { $"No squad '{name}'." };
        int total = sq.Sets.Sum(s => s.Weight);
        var lines = new List<string> { $"Squad '{sq.Name}' ({Who(sq.Owner)}){(sq.Sets.Count > 1 ? $": one of {sq.Sets.Count} sets per run" : "")}:" };
        for (int i = 0; i < sq.Sets.Count; i++)
            lines.Add($"set {i + 1}{(sq.Sets.Count > 1 ? $" ({sq.Sets[i].Weight}/{total})" : "")}: " +
                      string.Join(" + ", sq.Sets[i].Entries.Select((e, j) => $"[{j + 1}] {Describe(e)}")));
        if (sq.Sets.Count == 0) lines.Add("(empty)");
        return lines;
    }

    /// <summary>Roll a squad for one run: a set by weight, then each entry's units.</summary>
    static List<Rolled> Roll(Squad sq)
    {
        var result = new List<Rolled>();
        if (sq.Sets.Count == 0) return result;
        int pick = Rng.Next(sq.Sets.Sum(s => Math.Max(1, s.Weight)));
        var set = sq.Sets.First(s => (pick -= Math.Max(1, s.Weight)) < 0);
        foreach (var e in set.Entries)
        {
            if (e.Units.Count == 0) continue;
            bool boss = e.Role == "boss";
            if (e.Distinct)
                foreach (var guid in e.Units.OrderBy(_ => Rng.Next()).Take(e.Count)) result.Add(new Rolled(guid, boss, e.Level));
            else
                for (int i = 0; i < e.Count; i++) result.Add(new Rolled(e.Units[Rng.Next(e.Units.Count)], boss, e.Level));
        }
        return result;
    }

    // ------------------------------------------------------------------ floor templates

    public string CreateFloorPlan(Entity character, bool isAdmin, string name) =>
        Create(_file.FloorPlans, "floor template", character, isAdmin, name, (n, o) => new FloorPlan { Name = n, Owner = o },
            $"Give its groups squads: '.uriel floor assign {name} <group|*> <squad[,squad...]>'.");

    public string DeleteFloorPlan(Entity character, bool isAdmin, string name) => Delete(_file.FloorPlans, "floor template", character, isAdmin, name);

    public List<string> ListFloorPlans(Entity character, bool isAdmin) =>
        ListOf(_file.FloorPlans, "floor template", "'.uriel floor create <name>'.", character, isAdmin);

    public string AssignFloorPlan(Entity character, bool isAdmin, string name, string group, string squads)
    {
        var fp = Editable(_file.FloorPlans, "floor template", name, character, isAdmin, out string message);
        if (fp == null) return message;
        if (group != "*" && !ValidName(group)) return "The group is a layout group's name, or * for every group on the floor.";
        if (squads == "-")
        {
            fp.Groups.Remove(group);
            Save();
            return $"Floor template '{fp.Name}': group {group} unassigned.";
        }
        var list = squads.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var unknown = list.Where(s => Find(_file.Squads, s, character.GetSteamId(), isAdmin) == null).ToList();
        if (list.Count == 0 || unknown.Count > 0) return $"No squad {string.Join(", ", unknown.Select(u => $"'{u}'"))}. '.uriel squad list' shows yours and the server's.";
        fp.Groups[group] = list.Select(s => _file.Squads[s].Name).ToList();
        Save();
        Core.Log.LogInfo($"[Uriel EVENT] floor-assign name={fp.Name} group={group} squads={string.Join(",", fp.Groups[group])}");
        return $"Floor template '{fp.Name}': group {group} gets {string.Join(" or ", fp.Groups[group])}{(list.Count > 1 ? " (one rolled per run)" : "")}.";
    }

    public List<string> ShowFloorPlan(Entity character, bool isAdmin, string name)
    {
        var fp = Find(_file.FloorPlans, name, character.GetSteamId(), isAdmin);
        if (fp == null) return new List<string> { $"No floor template '{name}'." };
        var lines = new List<string> { $"Floor template '{fp.Name}' ({Who(fp.Owner)}):" };
        foreach (var (g, sq) in fp.Groups.OrderBy(k => k.Key == "*" ? "~" : k.Key)) lines.Add($"group {g} · {string.Join(" or ", sq)}");
        if (fp.Groups.Count == 0) lines.Add("(empty)");
        return lines;
    }

    // ------------------------------------------------------------------ castle templates

    public string CreateTemplate(Entity character, bool isAdmin, string name) =>
        Create(_file.EventTemplates, "template", character, isAdmin, name, (n, o) => new EventTemplate { Name = n, Owner = o },
            $"Give its floors floor templates: '.uriel template floor {name} <1|2|...|*> <floor template[,...]>'.");

    public string DeleteTemplate(Entity character, bool isAdmin, string name) => Delete(_file.EventTemplates, "template", character, isAdmin, name);

    public List<string> ListTemplates(Entity character, bool isAdmin) =>
        ListOf(_file.EventTemplates, "template", "'.uriel template create <name>'.", character, isAdmin);

    public string SetTemplateFloor(Entity character, bool isAdmin, string name, string floor, string plans)
    {
        var t = Editable(_file.EventTemplates, "template", name, character, isAdmin, out string message);
        if (t == null) return message;
        if (floor != "*" && (!int.TryParse(floor, out int f) || f < 1 || f > 20)) return "The floor is 1, 2, ... or * for every floor.";
        if (plans == "-")
        {
            t.Floors.Remove(floor);
            Save();
            return $"Template '{t.Name}': floor {floor} cleared.";
        }
        var list = plans.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var unknown = list.Where(p => Find(_file.FloorPlans, p, character.GetSteamId(), isAdmin) == null).ToList();
        if (list.Count == 0 || unknown.Count > 0) return $"No floor template {string.Join(", ", unknown.Select(u => $"'{u}'"))}. '.uriel floor list' shows yours and the server's.";
        t.Floors[floor] = list.Select(p => _file.FloorPlans[p].Name).ToList();
        Save();
        Core.Log.LogInfo($"[Uriel EVENT] template-floor name={t.Name} floor={floor} plans={string.Join(",", t.Floors[floor])}");
        return $"Template '{t.Name}': floor {floor} uses {string.Join(" or ", t.Floors[floor])}{(list.Count > 1 ? " (one rolled per run)" : "")}.";
    }

    public string SetTemplateOptions(Entity character, bool isAdmin, string name, string options)
    {
        var t = Editable(_file.EventTemplates, "template", name, character, isAdmin, out string message);
        if (t == null) return message;
        if (options == "-") options = null;
        if (options != null && !TryParseOptions(options, UnitScaling.None, out _, out _, out _, out message)) return message;
        t.Options = options;
        Save();
        Core.Log.LogInfo($"[Uriel EVENT] template-options name={t.Name} options={options ?? "-"}");
        return options == null ? $"Template '{t.Name}': no default options." : $"Template '{t.Name}': runs default to {options}.";
    }

    public List<string> ShowTemplate(Entity character, bool isAdmin, string name)
    {
        var t = Find(_file.EventTemplates, name, character.GetSteamId(), isAdmin);
        if (t == null) return new List<string> { $"No template '{name}'." };
        var lines = new List<string> { $"Template '{t.Name}' ({Who(t.Owner)}){(t.Options != null ? $", options {t.Options}" : "")}:" };
        foreach (var (f, plans) in t.Floors.OrderBy(k => k.Key == "*" ? "~" : k.Key)) lines.Add($"floor {f} · {string.Join(" or ", plans)}");
        if (t.Floors.Count == 0) lines.Add("(empty)");
        return lines;
    }

    // ------------------------------------------------------------------ run options

    /// <summary>Run options: scaling tokens (see <see cref="UnitScaling.TryApply"/>) and a time limit
    /// (<c>limit=15m</c>, <c>limit=90s</c>, <c>limit=1h</c>, <c>limit=0</c> for none), comma-separated.</summary>
    static bool TryParseOptions(string options, UnitScaling start, out UnitScaling scale, out int limitSeconds, out int oneFaction, out string message)
    {
        scale = start;
        limitSeconds = -1; // -1 = not given
        oneFaction = -1;   // -1 = not given, 0 = each unit keeps its own, 1 = the whole run on one faction
        message = null;
        if (string.IsNullOrWhiteSpace(options) || options == "-") return true;
        var scaling = new List<string>();
        foreach (var raw in options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string t = raw.ToLowerInvariant();
            if (t.StartsWith("limit=") || t.StartsWith("time="))
            {
                string v = t[(t.IndexOf('=') + 1)..];
                int mult = v.EndsWith("h") ? 3600 : v.EndsWith("m") ? 60 : 1;
                if (!int.TryParse(v.TrimEnd('h', 'm', 's'), NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n * mult > 6 * 3600)
                {
                    message = $"Bad time limit '{raw}': limit=15m, limit=90s, limit=1h (6 h at most), or limit=0.";
                    return false;
                }
                limitSeconds = n * mult;
                continue;
            }
            if (t is "faction=keep" or "faction=mixed" or "mixed") { oneFaction = 0; continue; }
            if (t is "faction=one" or "allied") { oneFaction = 1; continue; }
            scaling.Add(raw);
        }
        return UnitScaling.TryApply(start, scaling, out scale, out message);
    }
}
