using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Il2CppInterop.Runtime;
using ProjectM;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Uriel.Config;

namespace Uriel.Services;

/// <summary>
/// Castle Events (docs/features/CASTLE_EVENTS.md): a castle's event LAYOUT, the reusable content library (squads, floor
/// templates, castle templates: CastleEventService.Library.cs), and a manually started RUN.
///
///   Layout (one per castle plot, walked by its owner or an admin): groups, each on a floor, each with spawn markers
///     (a spot, a slot mob|boss, optionally its own unit), optionally a door it holds shut, optionally its own squad.
///   Run: start rolls each group's squad (from the template's floor template, else the group's own), spreads its units
///     over the group's markers (bosses to boss markers), closes and locks each group's door; when a group's last unit
///     is gone its door unlocks and opens. All groups cleared = complete. Options scale the units (level, health,
///     damage, speed) and set a time limit. The clock runs from the moment every unit is in place; the run records
///     when the last boss fell and when it completed.
///
/// Layouts, the library and run history persist in BepInEx/config/Uriel/castle_events.json; runs don't (units never
/// persist), but the doors a run holds are saved, so a restart mid-run unlocks them and puts them back.
/// Log lines: [Uriel EVENT] key=value.
/// </summary>
internal sealed partial class CastleEventService
{
    // ------------------------------------------------------------------ persisted model

    internal sealed class Marker
    {
        public int Id { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public string Slot { get; set; } = "mob";
        public int Prefab { get; set; }          // 0 = the template decides
        public int Count { get; set; }           // 0 = the template's count (or 1)
        public int Level { get; set; }           // 0 = the template's level (or the prefab's own)
        public string Contain { get; set; }      // null = CastleEvents.DefaultContain
        public int PaidItem { get; set; }
        public int PaidAmount { get; set; }
        public ulong PaidBy { get; set; }
    }

    internal sealed class Group
    {
        public string Name { get; set; }
        public int Floor { get; set; }
        public float[] Door { get; set; }        // the bound door's position, or null
        public string Squad { get; set; }        // its own squad, used when the run's template doesn't cover it
        public List<Marker> Markers { get; set; } = new();
    }

    internal sealed class Layout
    {
        public int Territory { get; set; }
        public int NextMarkerId { get; set; } = 1;
        public List<Group> Groups { get; set; } = new();
    }

    internal sealed class HeldDoor
    {
        public int Territory { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public bool WasOpen { get; set; }
        public bool WasLocked { get; set; }
        public bool Pending { get; set; }        // let go by its run, but not found yet: the retry loop finishes it
    }

    /// <summary>One finished run, for '.uriel event history'. Seconds count from the moment every unit was in place.</summary>
    internal sealed class RunRecord
    {
        public int Territory { get; set; }
        public string Template { get; set; }
        public string Options { get; set; }
        public DateTime StartedUtc { get; set; }
        public int Units { get; set; }
        public int Bosses { get; set; }
        public double BossSeconds { get; set; } = -1;   // -1 = the bosses were not all defeated (or there were none)
        public double Seconds { get; set; }
        public string Result { get; set; }              // complete | stopped | timed-out
    }

    sealed class SaveFile
    {
        public int SchemaVersion { get; set; } = 2;
        public Dictionary<string, Layout> Layouts { get; set; } = new();
        public Dictionary<string, Squad> Squads { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, FloorPlan> FloorPlans { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, EventTemplate> EventTemplates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<RunRecord> History { get; set; } = new();
        public List<HeldDoor> HeldDoors { get; set; } = new();
    }

    const int HistoryKept = 100;

    // ------------------------------------------------------------------ runtime model

    sealed class RunGroup
    {
        public string Name;
        public string Tag;
        public string Squad;           // the squad rolled for this run, or null (markers' own units only)
        public HeldDoor Door;          // null when the group holds no door
        public int Spawned;
        public int Bosses;
        public bool Cleared;
    }

    sealed class Run
    {
        public int Territory;
        public Entity Heart;
        public string Template;
        public string Options;
        public UnitScaling Scale;
        public ulong By;
        public DateTime Started;
        public DateTime? InPlace;      // every unit spawned: the clock starts
        public DateTime? BossesDown;
        public int LimitSeconds;       // 0 = none
        public int Warned;             // the last time-left warning given (seconds)
        public int Units, Bosses;
        public List<RunGroup> Groups = new();

        public string Prefix => $"ev{Territory}:";
        public double Elapsed => (DateTime.UtcNow - (InPlace ?? Started)).TotalSeconds;
    }

    SaveFile _file = new();
    readonly Dictionary<int, Run> _runs = new();
    bool _loopStarted;
    static readonly System.Random Rng = new();

    static string SaveDir => Path.Combine(BepInEx.Paths.ConfigPath, "Uriel");
    static string SavePath => Path.Combine(SaveDir, "castle_events.json");
    static string F(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);
    static string Tag(int territory, string group) => $"ev{territory}:{group.ToLowerInvariant()}";

    // ------------------------------------------------------------------ lifecycle

    public void Load()
    {
        try
        {
            if (File.Exists(SavePath))
                _file = JsonSerializer.Deserialize<SaveFile>(File.ReadAllText(SavePath)) ?? new SaveFile();
            _file.Layouts ??= new();
            _file.Squads = new Dictionary<string, Squad>(_file.Squads ?? new(), StringComparer.OrdinalIgnoreCase);
            _file.FloorPlans = new Dictionary<string, FloorPlan>(_file.FloorPlans ?? new(), StringComparer.OrdinalIgnoreCase);
            _file.EventTemplates = new Dictionary<string, EventTemplate>(_file.EventTemplates ?? new(), StringComparer.OrdinalIgnoreCase);
            foreach (var fp in _file.FloorPlans.Values) fp.Groups = new(fp.Groups ?? new(), StringComparer.OrdinalIgnoreCase);
            foreach (var t in _file.EventTemplates.Values) t.Floors = new(t.Floors ?? new(), StringComparer.OrdinalIgnoreCase);
            _file.History ??= new();
            _file.HeldDoors ??= new();
            Core.Log.LogInfo($"[Uriel EVENT] loaded layouts={_file.Layouts.Count} squads={_file.Squads.Count} floorTemplates={_file.FloorPlans.Count} templates={_file.EventTemplates.Count} history={_file.History.Count} heldDoors={_file.HeldDoors.Count}");
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[Uriel EVENT] failed loading {SavePath}: {ex}");
            _file = new SaveFile();
        }
    }

    public void SaveSync() => Save();

    void Save()
    {
        try
        {
            Directory.CreateDirectory(SaveDir);
            File.WriteAllText(SavePath, JsonSerializer.Serialize(_file, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[Uriel EVENT] failed saving {SavePath}: {ex}");
        }
    }

    /// <summary>Boot: doors a run held when the server stopped are put back (the run itself is gone), then the run
    /// monitor starts.</summary>
    public void Start()
    {
        // A restart ends every run, so every door still held is let go.
        foreach (var h in _file.HeldDoors) h.Pending = true;
        StartPendingLoop();
        if (_loopStarted) return;
        _loopStarted = true;
        Tick.RunRepeating(30, Monitor); // about once a second
    }

    const int PendingTries = 30;
    bool _pendingLoop;

    void StartPendingLoop()
    {
        if (_pendingLoop || !_file.HeldDoors.Any(h => h.Pending)) return;
        _pendingLoop = true;
        Tick.RunLater(300, () => ReleasePending(1));
    }

    /// <summary>Finish letting go of doors whose run has ended (a restart, a stop, a cleared group) but which weren't
    /// found (the castle not loaded yet at boot, say). Such a door is kept and tried again, every ~10 s at first, then
    /// every minute, for as long as the castle stands; it is given up only when the castle is gone (after
    /// <see cref="PendingTries"/> tries).</summary>
    void ReleasePending(int attempt)
    {
        try
        {
            var pending = _file.HeldDoors.Where(h => h.Pending).ToList();
            int released = 0, abandoned = 0;
            foreach (var h in pending)
            {
                if (Release(h, restore: true, out string why))
                {
                    _file.HeldDoors.Remove(h);
                    released++;
                    Core.Log.LogInfo($"[Uriel EVENT] door-released territory={h.Territory} x={F(h.X)} z={F(h.Z)} open={h.WasOpen} locked={h.WasLocked} attempt={attempt}");
                }
                else if (attempt >= PendingTries && why == "no-heart")
                {
                    _file.HeldDoors.Remove(h);
                    abandoned++;
                    Core.Log.LogWarning($"[Uriel EVENT] door-abandoned territory={h.Territory} x={F(h.X)} z={F(h.Z)} reason={why}");
                }
                else if (attempt == 1)
                    Core.Log.LogInfo($"[Uriel EVENT] door-pending territory={h.Territory} x={F(h.X)} z={F(h.Z)} reason={why}");
            }
            if (released + abandoned > 0) Save();
        }
        catch (Exception ex) { Core.Log.LogWarning($"[Uriel EVENT] pending doors failed: {ex.Message}"); }
        if (_file.HeldDoors.Any(h => h.Pending)) Tick.RunLater(attempt < PendingTries ? 300 : 1800, () => ReleasePending(attempt + 1));
        else _pendingLoop = false;
    }

    // ------------------------------------------------------------------ permissions

    /// <summary>The plot the caller stands on, if they may edit or run its event: admins anywhere; a player only on a
    /// plot their team owns, and only with CastleEvents.PlayerEvents on.</summary>
    bool TryCallerPlot(Entity character, bool isAdmin, out float3 feet, out Entity heart, out int territory, out string message)
    {
        feet = default; heart = Entity.Null; territory = -1; message = null;
        if (!isAdmin && !Settings.CastleEvents_PlayerEvents.Value)
        {
            message = "Castle events are admin-only on this server (CastleEvents.PlayerEvents).";
            return false;
        }
        if (!character.TryGetComponent<Translation>(out var t)) { message = "Can't read your position."; return false; }
        feet = t.Value;
        if (!Core.ObjectSpawn.TryResolvePlot(feet, out heart, out territory))
        {
            message = "Stand inside a castle plot to set up its event.";
            return false;
        }
        if (!ObjectSpawnService.CastleClaimedAndAlive(heart)) { message = "That castle plot is abandoned or decaying."; return false; }
        if (!isAdmin && !ObjectSpawnService.OwnsHeart(character, heart))
        {
            message = "You can only set up events in your own castle.";
            Core.Log.LogInfo($"[Uriel EVENT] refused reason=not-owner territory={territory} by={character.GetSteamId()}");
            return false;
        }
        return true;
    }

    Layout LayoutFor(int territory, bool create)
    {
        string key = territory.ToString(CultureInfo.InvariantCulture);
        if (_file.Layouts.TryGetValue(key, out var l)) return l;
        if (!create) return null;
        l = new Layout { Territory = territory };
        _file.Layouts[key] = l;
        return l;
    }

    static Group FindGroup(Layout l, string name) =>
        l?.Groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    static bool ValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 24 && name != "*" && name.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_');

    // ------------------------------------------------------------------ layout editing

    public string AddMarker(Entity character, bool isAdmin, string group, string slot, string prefabArg, int count, int level, string containArg)
    {
        if (!TryCallerPlot(character, isAdmin, out var feet, out var heart, out int territory, out string message)) return message;
        if (_runs.ContainsKey(territory)) return "An event is running on this plot. Stop it first ('.uriel event stop').";
        if (!ValidName(group)) return "A group name is 1-24 letters, digits, '-' or '_'.";
        slot = (slot ?? "mob").ToLowerInvariant();
        if (slot != "mob" && slot != "boss") return "The slot is mob or boss.";

        int guid = 0;
        string unitName = null;
        if (!string.IsNullOrEmpty(prefabArg) && prefabArg != "-" && !prefabArg.Equals("template", StringComparison.OrdinalIgnoreCase))
            if (!UnitSpawnService.TryResolveUnitPrefab(prefabArg, out guid, out unitName, out message)) return message;
        string contain = string.IsNullOrWhiteSpace(containArg) || containArg == "-" ? null : containArg;
        if (contain != null && !UnitSpawnService.TryParseContain(contain, out _, out message)) return message;
        count = Math.Clamp(count, 0, Math.Max(1, Settings.CastleEvents_MaxUnitsPerCommand.Value));

        var layout = LayoutFor(territory, create: true);
        int markers = layout.Groups.Sum(g => g.Markers.Count);
        if (!isAdmin && markers >= Settings.CastleEvents_MaxMarkersPerPlot.Value)
            return $"This castle has the most markers allowed ({markers}/{Settings.CastleEvents_MaxMarkersPerPlot.Value}). Remove one first.";

        // Cost (players only): placement, more for a boss; refunded on removal.
        int costItem = Settings.CastleEvents_MarkerCostItem.Value;
        int costAmount = slot == "boss" ? Settings.CastleEvents_BossMarkerCostAmount.Value : Settings.CastleEvents_MarkerCostAmount.Value;
        bool charge = !isAdmin && costItem != 0 && costAmount > 0;
        if (charge && !ObjectSpawnService.HasInventoryItems(character, costItem, costAmount))
            return $"A {slot} marker costs {costAmount}x {new PrefabGUID(costItem).GetPrefabName()}; you don't have enough in your inventory.";

        CastleFloorIndex.Invalidate(heart, territory);
        int floor = CastleFloorIndex.StoreyOf(heart, feet.y);
        var g = FindGroup(layout, group);
        if (g == null) layout.Groups.Add(g = new Group { Name = group, Floor = floor });
        if (charge && !ObjectSpawnService.ChargeInventory(character, costItem, costAmount))
            return "Payment failed; the marker was not placed.";

        var m = new Marker
        {
            Id = layout.NextMarkerId++, X = feet.x, Y = feet.y, Z = feet.z, Slot = slot, Prefab = guid, Count = count, Level = level,
            Contain = contain, PaidItem = charge ? costItem : 0, PaidAmount = charge ? costAmount : 0, PaidBy = charge ? character.GetSteamId() : 0,
        };
        g.Markers.Add(m);
        Save();
        Core.Log.LogInfo($"[Uriel EVENT] marker-added territory={territory} group={g.Name} id={m.Id} slot={slot} floor={floor} x={F(m.X)} y={F(m.Y)} z={F(m.Z)} prefab={unitName ?? "squad"} count={count} level={level} contain={contain ?? "default"} paid={m.PaidAmount}");
        return $"Marker #{m.Id} ({slot}) in group '{g.Name}' (floor {g.Floor}): {unitName ?? "units from the group's squad"}" +
               $"{(count > 0 ? $" x{count}" : "")}. The group has {g.Markers.Count} marker(s){(g.Door != null ? " and a door" : ", no door")}." +
               (m.PaidAmount > 0 ? $" Paid {m.PaidAmount}x {new PrefabGUID(costItem).GetPrefabName()}." : "");
    }

    public string BindDoor(Entity character, bool isAdmin, string group, bool unbind)
    {
        if (!TryCallerPlot(character, isAdmin, out var feet, out var heart, out int territory, out string message)) return message;
        if (_runs.ContainsKey(territory)) return "An event is running on this plot. Stop it first ('.uriel event stop').";
        if (!unbind && !Settings.DoorGatesOn) return "Event doors are disabled by the server admin (CastleEvents.DoorGates).";
        var g = FindGroup(LayoutFor(territory, create: false), group);
        if (g == null) return $"No group '{group}' on this plot. A group starts with its first marker ('.uriel event marker {group}').";
        if (unbind)
        {
            g.Door = null;
            Save();
            Core.Log.LogInfo($"[Uriel EVENT] door-unbound territory={territory} group={g.Name}");
            return $"Group '{g.Name}' no longer holds a door.";
        }
        var door = CastleDoors.Nearest(heart, feet, 6f);
        if (door == Entity.Null) return "No castle door within 6 m. Stand next to the door this group should hold shut.";
        var p = door.Read<Translation>().Value;
        var other = LayoutFor(territory, false).Groups.FirstOrDefault(o => o != g && o.Door != null && math.distance(new float3(o.Door[0], o.Door[1], o.Door[2]), p) < 1f);
        if (other != null) return $"That door is already held by group '{other.Name}'.";
        g.Door = new[] { p.x, p.y, p.z };
        Save();
        Core.Log.LogInfo($"[Uriel EVENT] door-bound territory={territory} group={g.Name} {CastleDoors.Describe(door)}");
        return $"Group '{g.Name}' holds the door {door.GetPrefabGuid().GetPrefabName()} ({F(math.distance(p, feet))} m away): shut while its units live, open once they are all defeated.";
    }

    /// <summary>Give a layout group its own squad (or "-" for none): what it spawns when the run's template doesn't say.</summary>
    public string SetGroupSquad(Entity character, bool isAdmin, string group, string squad)
    {
        if (!TryCallerPlot(character, isAdmin, out _, out _, out int territory, out string message)) return message;
        if (_runs.ContainsKey(territory)) return "An event is running on this plot. Stop it first ('.uriel event stop').";
        var g = FindGroup(LayoutFor(territory, create: false), group);
        if (g == null) return $"No group '{group}' on this plot. A group starts with its first marker ('.uriel event marker {group}').";
        if (squad == "-") g.Squad = null;
        else
        {
            var sq = Find(_file.Squads, squad, character.GetSteamId(), isAdmin);
            if (sq == null) return $"No squad '{squad}'. '.uriel squad list' shows yours and the server's.";
            g.Squad = sq.Name;
        }
        Save();
        Core.Log.LogInfo($"[Uriel EVENT] group-squad territory={territory} group={g.Name} squad={g.Squad ?? "-"}");
        return g.Squad == null ? $"Group '{g.Name}' has no squad of its own." : $"Group '{g.Name}' spawns squad '{g.Squad}' unless the run's template says otherwise.";
    }

    public string Remove(Entity character, bool isAdmin, string what)
    {
        if (!TryCallerPlot(character, isAdmin, out _, out _, out int territory, out string message)) return message;
        if (_runs.ContainsKey(territory)) return "An event is running on this plot. Stop it first ('.uriel event stop').";
        var layout = LayoutFor(territory, create: false);
        if (layout == null) return "This plot has no event layout.";
        var removed = new List<Marker>();
        string label;
        if (int.TryParse(what.TrimStart('#'), out int id))
        {
            var g = layout.Groups.FirstOrDefault(x => x.Markers.Any(m => m.Id == id));
            if (g == null) return $"No marker #{id} on this plot.";
            var m = g.Markers.First(x => x.Id == id);
            g.Markers.Remove(m);
            removed.Add(m);
            if (g.Markers.Count == 0) layout.Groups.Remove(g);
            label = $"marker #{id}";
        }
        else
        {
            var g = FindGroup(layout, what);
            if (g == null) return $"No marker #{what} or group '{what}' on this plot.";
            removed.AddRange(g.Markers);
            layout.Groups.Remove(g);
            label = $"group '{g.Name}' ({removed.Count} marker(s))";
        }
        string refund = RefundMarkers(character, removed);
        Save();
        Core.Log.LogInfo($"[Uriel EVENT] removed territory={territory} what={what} markers={removed.Count}");
        return $"Removed {label}.{refund}";
    }

    static string RefundMarkers(Entity character, List<Marker> markers)
    {
        ulong me = character.GetSteamId();
        int n = 0;
        foreach (var m in markers.Where(m => m.PaidAmount > 0 && m.PaidItem != 0 && m.PaidBy == me))
        {
            ObjectSpawnService.RefundInventory(character, m.PaidItem, m.PaidAmount);
            n += m.PaidAmount;
        }
        return n > 0 ? $" Refunded {n} item(s)." : "";
    }

    /// <summary>The layout of the plot the caller stands on: one line per group (floor, door, markers).</summary>
    public List<string> Describe(Entity character, bool isAdmin)
    {
        var lines = new List<string>();
        if (!TryCallerPlot(character, isAdmin, out _, out var heart, out int territory, out string message)) { lines.Add(message); return lines; }
        var layout = LayoutFor(territory, create: false);
        if (layout == null || layout.Groups.Count == 0) { lines.Add("This plot has no event layout yet. Start one: '.uriel event marker <group>'."); return lines; }
        lines.Add($"Event layout of plot {territory}: {layout.Groups.Count} group(s), {layout.Groups.Sum(g => g.Markers.Count)} marker(s){(_runs.ContainsKey(territory) ? "; RUNNING" : "")}.");
        foreach (var g in layout.Groups.OrderBy(g => g.Floor).ThenBy(g => layout.Groups.IndexOf(g)))
        {
            string door = g.Door == null ? "no door" : DoorLabel(heart, g.Door);
            string markers = string.Join(", ", g.Markers.Select(m =>
                $"#{m.Id} {m.Slot} {(m.Prefab != 0 ? new PrefabGUID(m.Prefab).GetPrefabName() : "squad")}{(m.Count > 0 ? $" x{m.Count}" : "")}"));
            lines.Add($"Floor {g.Floor} · {g.Name} · {door} · squad {g.Squad ?? "-"} · {markers}");
            Core.Log.LogInfo($"[Uriel EVENT] layout territory={territory} group={g.Name} floor={g.Floor} door={(g.Door == null ? "none" : $"{F(g.Door[0])},{F(g.Door[1])},{F(g.Door[2])}")} squad={g.Squad ?? "-"} markers={g.Markers.Count}");
        }
        return lines;
    }

    static string DoorLabel(Entity heart, float[] d)
    {
        var door = CastleDoors.At(heart, new float3(d[0], d[1], d[2]));
        return door == Entity.Null ? $"door missing at {F(d[0])},{F(d[2])}" : $"door at {F(d[0])},{F(d[2])}{(CastleDoors.IsOpen(door) ? " (open)" : " (shut)")}";
    }

    // ------------------------------------------------------------------ runs

    public string StartRun(Entity character, bool isAdmin, string templateName, string options)
    {
        if (!Settings.CastleEventsOn) return "Castle events are disabled by the server admin (CastleEvents.Enabled).";
        if (!TryCallerPlot(character, isAdmin, out _, out var heart, out int territory, out string message)) return message;
        if (_runs.ContainsKey(territory)) return "An event is already running on this plot. '.uriel event stop' ends it.";
        var layout = LayoutFor(territory, create: false);
        if (layout == null || layout.Groups.All(g => g.Markers.Count == 0)) return "This plot has no event markers. Place some: '.uriel event marker <group>'.";
        ulong me = character.GetSteamId();

        // What to run: a castle template, a floor template (every floor), a squad (every group), or nothing (each
        // group's own squad and the markers' own units).
        EventTemplate template = null;
        string label = null;
        if (!string.IsNullOrEmpty(templateName) && templateName != "-")
        {
            if (Find(_file.EventTemplates, templateName, me, isAdmin) is { } t) template = t;
            else if (Find(_file.FloorPlans, templateName, me, isAdmin) is { } fp)
                template = new EventTemplate { Name = fp.Name, Floors = new(StringComparer.OrdinalIgnoreCase) { ["*"] = new() { fp.Name } } };
            else if (Find(_file.Squads, templateName, me, isAdmin) is { } sq)
                template = new EventTemplate { Name = sq.Name, Squad = sq.Name };
            else return $"No template, floor template or squad '{templateName}'. '.uriel template list' shows the ones you can use.";
            label = template.Name;
        }

        // Options: the template's defaults, then the start's own.
        if (!TryParseOptions(template?.Options, UnitScaling.None, out var scale, out int limit, out int faction, out message)) return $"Template options: {message}";
        if (!TryParseOptions(options, scale, out scale, out int startLimit, out int startFaction, out message)) return message;
        if (startLimit >= 0) limit = startLimit;
        if (startFaction >= 0) faction = startFaction;
        string optionText = string.Join(",", new[] { template?.Options, options == "-" ? null : options }.Where(o => !string.IsNullOrWhiteSpace(o)));
        if (scale.Auto)
        {
            int top = HighestPlayerLevel(territory);
            scale = scale with { Auto = false, Level = top > 0 ? Math.Clamp(top + scale.LevelAdd, 1, 200) : 0, LevelAdd = 0 };
            Core.Log.LogInfo($"[Uriel EVENT] auto-level territory={territory} highest={top} level={scale.Level}");
        }

        // Roll every group's units and check that everything resolves and fits before anything spawns or is charged.
        var rolledFloors = new Dictionary<int, FloorPlan>();
        var plan = new List<(Group G, string Squad, List<(Marker M, Rolled U)> Units)>();
        var empty = new List<string>();
        foreach (var g in layout.Groups.Where(g => g.Markers.Count > 0))
        {
            string squadName = SquadFor(template, g, rolledFloors, me, isAdmin);
            var sq = squadName != null ? Find(_file.Squads, squadName, me, isAdmin) : null;
            if (squadName != null && sq == null) return $"Group '{g.Name}' gets squad '{squadName}', which doesn't exist (or isn't yours).";
            var units = Spread(g, sq != null ? Roll(sq) : new List<Rolled>());
            if (units.Count == 0) empty.Add($"'{g.Name}' (floor {g.Floor})");
            plan.Add((g, sq?.Name, units));
        }
        if (empty.Count > 0)
            return $"No units for group(s) {string.Join(", ", empty)}: give the group a squad ('.uriel event squad <group> <squad>'), give its markers units, or start a template that covers it.";
        int total = plan.Sum(p => p.Units.Count);
        int room = Core.Units.RoomOnPlot(territory);
        if (total > room) return $"This event needs {total} units; only {Math.Max(0, room)} fit under the unit caps (CastleEvents.MaxUnitsPerPlot / MaxTrackedUnits).";

        int costItem = Settings.CastleEvents_RunCostItem.Value, costAmount = Settings.CastleEvents_RunCostAmount.Value;
        if (!isAdmin && costItem != 0 && costAmount > 0)
        {
            if (!ObjectSpawnService.HasInventoryItems(character, costItem, costAmount))
                return $"Starting an event costs {costAmount}x {new PrefabGUID(costItem).GetPrefabName()}; you don't have enough in your inventory.";
            if (!ObjectSpawnService.ChargeInventory(character, costItem, costAmount)) return "Payment failed; the event did not start.";
        }

        // One faction for the whole run (the default): units of other factions fight each other (a beast V Blood killed
        // skeletons in testing). The first boss's faction, else the first unit's; faction=keep opts out.
        int factionFrom = 0;
        if (faction != 0)
        {
            var all = plan.SelectMany(p => p.Units).Select(x => x.U).ToList();
            if (all.Count > 0) factionFrom = (all.Any(u => u.Boss) ? all.First(u => u.Boss) : all[0]).Guid;
        }

        var run = new Run
        {
            Territory = territory, Heart = heart, Template = label, Options = optionText.Length > 0 ? optionText : null, Scale = scale,
            By = me, Started = DateTime.UtcNow, LimitSeconds = Math.Max(0, limit),
        };
        string defaultContain = Settings.CastleEvents_DefaultContain.Value;
        int failed = 0;
        foreach (var (g, squad, units) in plan)
        {
            var rg = new RunGroup { Name = g.Name, Tag = Tag(territory, g.Name), Squad = squad };
            run.Groups.Add(rg);
            if (g.Door != null && Settings.DoorGatesOn) rg.Door = Hold(territory, heart, g);
            // One queue call per marker and unit kind; several kinds at one marker continue one ring.
            foreach (var atMarker in units.GroupBy(u => u.M))
            {
                var m = atMarker.Key;
                if (!UnitSpawnService.TryParseContain(m.Contain ?? defaultContain, out var contain, out _))
                    contain = new UnitSpawnService.Containment(UnitSpawnService.Contain.Plot, 0f, Entity.Null);
                int ring = 0;
                foreach (var kind in atMarker.GroupBy(u => u.U))
                {
                    int n = kind.Count();
                    var u = kind.Key;
                    if (Core.Units.EnqueueAt(new float3(m.X, m.Y, m.Z), me, u.Guid, new PrefabGUID(u.Guid).GetPrefabName(), n, u.Level, contain, rg.Tag,
                            out string why, boss: u.Boss, scale: scale.IsNone ? null : scale, ringOffset: ring, factionFrom: factionFrom))
                    {
                        rg.Spawned += n;
                        if (u.Boss) rg.Bosses += n;
                    }
                    else
                    {
                        failed++;
                        Core.Log.LogWarning($"[Uriel EVENT] marker-failed territory={territory} group={g.Name} id={m.Id} reason={why}");
                    }
                    ring += n;
                }
            }
            Core.Log.LogInfo($"[Uriel EVENT] group-rolled territory={territory} group={g.Name} floor={g.Floor} squad={squad ?? "-"} units={rg.Spawned} bosses={rg.Bosses} " +
                             $"spawns={string.Join(",", units.GroupBy(u => u.U.Guid).Select(k => $"{new PrefabGUID(k.Key).GetPrefabName()}x{k.Count()}"))}");
        }
        run.Units = run.Groups.Sum(x => x.Spawned);
        run.Bosses = run.Groups.Sum(x => x.Bosses);
        _runs[territory] = run;
        Save();
        int doors = run.Groups.Count(x => x.Door != null);
        Core.Log.LogInfo($"[Uriel EVENT] started territory={territory} template={run.Template ?? "-"} groups={run.Groups.Count} units={run.Units} bosses={run.Bosses} doors={doors} failedMarkers={failed} scale={scale} limit={run.LimitSeconds} faction={(factionFrom == 0 ? "keep" : new PrefabGUID(factionFrom).GetPrefabName())} by={run.By}");
        Announce(run, $"A castle event has begun: {run.Units} foes{(run.Bosses > 0 ? $" ({run.Bosses} boss{(run.Bosses == 1 ? "" : "es")})" : "")} in {run.Groups.Count} group(s)" +
                      $"{(doors > 0 ? $"; {doors} door(s) are sealed until their guards fall" : "")}{(run.LimitSeconds > 0 ? $"; time limit {Clock(run.LimitSeconds)}" : "")}.");
        return $"Event started{(run.Template != null ? $" from '{run.Template}'" : "")}: {run.Units} unit(s), {run.Bosses} boss(es), {run.Groups.Count} group(s), {doors} door(s) held" +
               $"{(scale.IsNone ? "" : $", scaled {scale}")}{(run.LimitSeconds > 0 ? $", limit {Clock(run.LimitSeconds)}" : "")}." +
               (failed > 0 ? $" {failed} marker(s) could not spawn (see the server log)." : "");
    }

    static string Clock(double seconds)
    {
        int s = (int)Math.Round(seconds);
        return s >= 3600 ? $"{s / 3600}h {s % 3600 / 60:00}m" : $"{s / 60}m {s % 60:00}s";
    }

    /// <summary>The squad a group gets for this run: the template's floor template (rolled once per floor per run) for
    /// the group's name or "*", rolled among its squads; else the group's own squad.</summary>
    string SquadFor(EventTemplate t, Group g, Dictionary<int, FloorPlan> rolledFloors, ulong me, bool isAdmin)
    {
        if (t?.Squad != null) return t.Squad;   // a squad started on its own: every group gets it
        if (t != null)
        {
            string f = g.Floor.ToString(CultureInfo.InvariantCulture);
            var plans = t.Floors.TryGetValue(f, out var p) ? p : t.Floors.TryGetValue("*", out var any) ? any : null;
            if (plans != null && plans.Count > 0)
            {
                if (!rolledFloors.TryGetValue(g.Floor, out var fp))
                {
                    fp = Find(_file.FloorPlans, plans[Rng.Next(plans.Count)], me, isAdmin);
                    rolledFloors[g.Floor] = fp;
                    if (fp != null) Core.Log.LogInfo($"[Uriel EVENT] floor-rolled floor={g.Floor} template={fp.Name} of={plans.Count}");
                }
                if (fp != null && (fp.Groups.TryGetValue(g.Name, out var squads) || fp.Groups.TryGetValue("*", out squads)) && squads.Count > 0)
                    return squads[Rng.Next(squads.Count)];
            }
        }
        return g.Squad;
    }

    /// <summary>Put a group's rolled units on its markers: bosses round-robin on boss markers (any marker if it has
    /// none), mobs on mob markers (likewise); markers that name their own unit keep it and take no squad units unless
    /// every marker names one.</summary>
    static List<(Marker M, Rolled U)> Spread(Group g, List<Rolled> rolled)
    {
        var result = new List<(Marker, Rolled)>();
        foreach (var m in g.Markers.Where(m => m.Prefab != 0))
            for (int i = 0; i < Math.Max(1, m.Count); i++) result.Add((m, new Rolled(m.Prefab, m.Slot == "boss", m.Level)));
        var open = g.Markers.Where(m => m.Prefab == 0).ToList();
        if (open.Count == 0) open = g.Markers;
        var bossSpots = open.Where(m => m.Slot == "boss").ToList();
        var mobSpots = open.Where(m => m.Slot != "boss").ToList();
        if (bossSpots.Count == 0) bossSpots = open;
        if (mobSpots.Count == 0) mobSpots = open;
        int b = 0, n = 0;
        foreach (var u in rolled)
            result.Add(u.Boss ? (bossSpots[b++ % bossSpots.Count], u) : (mobSpots[n++ % mobSpots.Count], u));
        return result;
    }

    /// <summary>The highest gear level among players on the plot (auto scaling).</summary>
    static int HighestPlayerLevel(int territory)
    {
        int top = 0;
        foreach (var p in PlayersOn(territory))
            if (p.TryGetComponent<Equipment>(out var eq)) top = Math.Max(top, (int)Math.Round(eq.GetFullLevel()));
        return top;
    }

    /// <summary>Shut and lock a group's door for a run, remembering how it was. Saved, so a restart puts it back.</summary>
    HeldDoor Hold(int territory, Entity heart, Group g)
    {
        var at = new float3(g.Door[0], g.Door[1], g.Door[2]);
        var door = CastleDoors.At(heart, at);
        if (door == Entity.Null)
        {
            Core.Log.LogWarning($"[Uriel EVENT] door-missing territory={territory} group={g.Name} x={F(at.x)} z={F(at.z)}");
            return null;
        }
        var h = new HeldDoor { Territory = territory, X = at.x, Y = at.y, Z = at.z, WasOpen = CastleDoors.IsOpen(door), WasLocked = CastleDoors.IsLocked(door) };
        CastleDoors.SetOpen(door, false);
        CastleDoors.SetLocked(door, true);
        _file.HeldDoors.Add(h);
        Core.Log.LogInfo($"[Uriel EVENT] door-held territory={territory} group={g.Name} {CastleDoors.Describe(door)} wasOpen={h.WasOpen}");
        return h;
    }

    /// <summary>Let a held door go: unlock it and either open it (a cleared group) or put it back as it was (a stop, a
    /// restart). False when the door is gone.</summary>
    bool Release(HeldDoor h, bool restore)
    {
        if (Release(h, restore, out string why))
        {
            _file.HeldDoors.Remove(h);
            return true;
        }
        // Not found: the retry loop finishes it, to the state this release asked for.
        if (!restore) h.WasOpen = true;
        h.Pending = true;
        Core.Log.LogInfo($"[Uriel EVENT] door-pending territory={h.Territory} x={F(h.X)} z={F(h.Z)} reason={why}");
        StartPendingLoop();
        return false;
    }

    /// <summary>The door-setting part of <see cref="Release(HeldDoor, bool)"/>, leaving the save alone; why = the
    /// reason when it fails.</summary>
    static bool Release(HeldDoor h, bool restore, out string why)
    {
        var pos = new float3(h.X, h.Y, h.Z);
        if (!Core.ObjectSpawn.TryResolvePlot(pos, out var heart, out _)) { why = "no-heart"; return false; }
        var door = CastleDoors.At(heart, pos);
        if (door == Entity.Null) { why = "no-door " + CastleDoors.Diagnose(pos); return false; }
        CastleDoors.SetLocked(door, h.WasLocked);
        CastleDoors.SetOpen(door, restore ? h.WasOpen : true);
        why = null;
        return true;
    }

    /// <summary>Once a second: the clock starts when every unit is in place; a group with no unit left (alive or
    /// pending) is cleared and its door opens; a door of a live group that someone opened is shut again; the last boss
    /// down is timed; the time limit warns and ends the run; all groups cleared completes it.</summary>
    void Monitor()
    {
        if (!Core.IsReady || _runs.Count == 0) return;
        foreach (var run in _runs.Values.ToList())
        {
            try
            {
                if (run.InPlace == null && Core.Units.CountTaggedPrefix(run.Prefix, queuedOnly: true) == 0)
                {
                    run.InPlace = DateTime.UtcNow;
                    Core.Log.LogInfo($"[Uriel EVENT] clock-started territory={run.Territory} units={run.Units} bosses={run.Bosses} afterSeconds={(run.InPlace.Value - run.Started).TotalSeconds:0.0} limit={run.LimitSeconds}");
                    Announce(run, $"All {run.Units} foes are in place. The clock is running{(run.LimitSeconds > 0 ? $": {Clock(run.LimitSeconds)}" : "")}.");
                }
                foreach (var g in run.Groups.Where(g => !g.Cleared))
                {
                    int left = Core.Units.CountTagged(g.Tag);
                    if (left > 0)
                    {
                        if (g.Door != null && CastleDoors.At(run.Heart, new float3(g.Door.X, g.Door.Y, g.Door.Z)) is var d && d != Entity.Null && CastleDoors.IsOpen(d))
                        {
                            CastleDoors.SetOpen(d, false);
                            CastleDoors.SetLocked(d, true);
                            Core.Log.LogInfo($"[Uriel EVENT] door-reshut territory={run.Territory} group={g.Name}");
                        }
                        continue;
                    }
                    g.Cleared = true;
                    bool opened = g.Door != null && Release(g.Door, restore: false);
                    Save();
                    Core.Log.LogInfo($"[Uriel EVENT] group-cleared territory={run.Territory} group={g.Name} units={g.Spawned} door={(g.Door == null ? "none" : opened ? "opened" : "pending")} seconds={run.Elapsed:0.0}");
                    Announce(run, $"Group '{g.Name}' is defeated{(opened ? "; its door opens" : "")} ({Clock(run.Elapsed)}).");
                }
                if (run.Bosses > 0 && run.BossesDown == null && run.InPlace != null && Core.Units.CountTaggedPrefix(run.Prefix, bossOnly: true) == 0)
                {
                    run.BossesDown = DateTime.UtcNow;
                    Core.Log.LogInfo($"[Uriel EVENT] bosses-defeated territory={run.Territory} bosses={run.Bosses} seconds={run.Elapsed:0.0}");
                    Announce(run, $"All {run.Bosses} boss{(run.Bosses == 1 ? " is" : "es are")} defeated: {Clock(run.Elapsed)}.");
                }
                if (run.Groups.All(g => g.Cleared))
                {
                    _runs.Remove(run.Territory);
                    Record(run, "complete");
                    Core.Log.LogInfo($"[Uriel EVENT] complete territory={run.Territory} groups={run.Groups.Count} seconds={run.Elapsed:0.0} bossSeconds={(run.BossesDown is { } b ? (b - run.InPlace.Value).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) : "-")}");
                    Announce(run, $"The castle event is complete: {Clock(run.Elapsed)}.");
                    continue;
                }
                if (run.LimitSeconds > 0 && run.InPlace != null)
                {
                    double left = run.LimitSeconds - run.Elapsed;
                    if (left <= 0)
                    {
                        Core.Log.LogInfo($"[Uriel EVENT] time-up territory={run.Territory} limit={run.LimitSeconds}");
                        Announce(run, "Time is up: the castle event has failed.");
                        Stop(run, "timed-out");
                        continue;
                    }
                    foreach (int warn in new[] { 60, 10 })
                        if (left <= warn && run.Warned != warn && (run.Warned == 0 || run.Warned > warn) && run.LimitSeconds > warn * 2)
                        {
                            run.Warned = warn;
                            Announce(run, $"{warn} seconds left.");
                            Core.Log.LogInfo($"[Uriel EVENT] time-warning territory={run.Territory} left={warn}");
                        }
                }
            }
            catch (Exception ex)
            {
                Core.Log.LogWarning($"[Uriel EVENT] monitor failed territory={run.Territory}: {ex.Message}");
            }
        }
    }

    void Record(Run run, string result)
    {
        var r = new RunRecord
        {
            Territory = run.Territory, Template = run.Template, Options = run.Options, StartedUtc = run.Started, Units = run.Units,
            Bosses = run.Bosses, Seconds = Math.Round(run.Elapsed, 1), Result = result,
            BossSeconds = run.BossesDown is { } b && run.InPlace is { } p ? Math.Round((b - p).TotalSeconds, 1) : -1,
        };
        _file.History.Add(r);
        if (_file.History.Count > HistoryKept) _file.History.RemoveRange(0, _file.History.Count - HistoryKept);
        Save();
    }

    public string StopRun(Entity character, bool isAdmin)
    {
        if (!TryCallerPlot(character, isAdmin, out _, out _, out int territory, out string message)) return message;
        if (!_runs.TryGetValue(territory, out var run)) return "No event is running on this plot.";
        return Stop(run, "stopped");
    }

    string Stop(Run run, string why)
    {
        _runs.Remove(run.Territory);
        Record(run, why);
        int units = Core.Units.DespawnTagged(t => t.StartsWith(run.Prefix, StringComparison.Ordinal));
        int doors = 0;
        foreach (var g in run.Groups.Where(g => !g.Cleared && g.Door != null))
            if (Release(g.Door, restore: true)) doors++;
        Save();
        Core.Log.LogInfo($"[Uriel EVENT] {why} territory={run.Territory} units={units} doorsRestored={doors} seconds={run.Elapsed:0.0}");
        if (why != "timed-out") Announce(run, "The castle event was stopped.");
        return $"Event {why}: removing {units} unit(s), {doors} door(s) put back as they were.";
    }

    public List<string> Status(Entity character, bool isAdmin)
    {
        var lines = new List<string>();
        if (!TryCallerPlot(character, isAdmin, out _, out _, out int territory, out string message)) { lines.Add(message); return lines; }
        if (!_runs.TryGetValue(territory, out var run)) { lines.Add("No event is running on this plot."); return lines; }
        int bossesLeft = Core.Units.CountTaggedPrefix(run.Prefix, bossOnly: true);
        lines.Add($"Event running {(run.InPlace == null ? "(spawning)" : Clock(run.Elapsed))}{(run.Template != null ? $", '{run.Template}'" : "")}{(run.Options != null ? $" [{run.Options}]" : "")}: " +
                  $"{run.Groups.Count(g => g.Cleared)}/{run.Groups.Count} group(s) cleared" +
                  $"{(run.Bosses > 0 ? (run.BossesDown is { } b ? $", bosses down at {Clock((b - run.InPlace.Value).TotalSeconds)}" : $", bosses {bossesLeft}/{run.Bosses} left") : "")}" +
                  $"{(run.LimitSeconds > 0 && run.InPlace != null ? $", {Clock(Math.Max(0, run.LimitSeconds - run.Elapsed))} left" : "")}.");
        Core.Log.LogInfo($"[Uriel EVENT] status territory={territory} run seconds={run.Elapsed:0.0} inPlace={run.InPlace != null} bossesLeft={bossesLeft} bosses={run.Bosses} limit={run.LimitSeconds}");
        foreach (var g in run.Groups)
        {
            int left = Core.Units.CountTagged(g.Tag);
            lines.Add($"{g.Name}{(g.Squad != null ? $" ({g.Squad})" : "")}: {(g.Cleared ? "cleared" : $"{left}/{g.Spawned} left")}{(g.Door != null ? (g.Cleared ? ", door open" : ", door held") : "")}");
            Core.Log.LogInfo($"[Uriel EVENT] status territory={territory} group={g.Name} left={left} spawned={g.Spawned} cleared={g.Cleared} door={(g.Door == null ? "none" : g.Cleared ? "open" : "held")}");
        }
        return lines;
    }

    /// <summary>The last runs on the caller's plot, and the best times per template.</summary>
    public List<string> History(Entity character, bool isAdmin)
    {
        var lines = new List<string>();
        if (!TryCallerPlot(character, isAdmin, out _, out _, out int territory, out string message)) { lines.Add(message); return lines; }
        var mine = _file.History.Where(r => r.Territory == territory).ToList();
        if (mine.Count == 0) { lines.Add("No events have run on this plot yet."); return lines; }
        lines.Add($"Last runs on plot {territory} ({mine.Count} recorded):");
        foreach (var r in Enumerable.Reverse(mine).Take(5))
        {
            lines.Add($"{r.StartedUtc:MM-dd HH:mm} · {r.Template ?? "-"}{(r.Options != null ? $" [{r.Options}]" : "")} · {r.Result} {Clock(r.Seconds)}" +
                      $"{(r.Bosses > 0 ? r.BossSeconds >= 0 ? $" · bosses {Clock(r.BossSeconds)}" : " · bosses not all down" : "")} · {r.Units} units");
            Core.Log.LogInfo($"[Uriel EVENT] history territory={territory} template={r.Template ?? "-"} result={r.Result} seconds={r.Seconds:0.0} bossSeconds={r.BossSeconds:0.0} units={r.Units} bosses={r.Bosses}");
        }
        foreach (var best in mine.Where(r => r.Result == "complete").GroupBy(r => r.Template ?? "-"))
        {
            var bossTimes = best.Where(r => r.BossSeconds >= 0).Select(r => r.BossSeconds).ToList();
            lines.Add($"Best '{best.Key}': complete {Clock(best.Min(r => r.Seconds))}{(bossTimes.Count > 0 ? $", bosses {Clock(bossTimes.Min())}" : "")} ({best.Count()} run(s)).");
        }
        return lines;
    }

    /// <summary>A purge or a castle loss ends every run: its doors are put back.</summary>
    public int StopAll(string why)
    {
        var runs = _runs.Values.ToList();
        foreach (var run in runs) Stop(run, why);
        return runs.Count;
    }

    /// <summary>Doors still held by a run that has ended but weren't found yet (being retried).</summary>
    public int PendingDoors => _file.HeldDoors.Count(h => h.Pending);

    /// <summary>A system message to every player standing on the run's plot.</summary>
    static void Announce(Run run, string text)
    {
        try
        {
            foreach (var p in PlayersOn(run.Territory)) ChatNotify.ToCharacter(p, text);
        }
        catch (Exception ex) { Core.Log.LogWarning($"[Uriel EVENT] announce failed: {ex.Message}"); }
    }

    /// <summary>The player characters standing on a plot.</summary>
    static List<Entity> PlayersOn(int territory)
    {
        var result = new List<Entity>();
        var query = Core.EntityManager.CreateEntityQuery(ComponentType.ReadOnly(Il2CppType.Of<PlayerCharacter>()), ComponentType.ReadOnly(Il2CppType.Of<Translation>()));
        try
        {
            var players = query.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (var p in players)
                    if (CastleFloorIndex.InTerritory(territory, p.Read<Translation>().Value)) result.Add(p);
            }
            finally { players.Dispose(); }
        }
        finally { query.Dispose(); }
        return result;
    }
}
