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
/// Castle Events Phase 2 MVP (docs/features/CASTLE_EVENTS.md): a castle's event LAYOUT and reusable TEMPLATES, and a
/// manually started RUN.
///
///   Layout (one per castle plot, walked by its owner or an admin): groups, each on a floor, each with spawn markers
///     (a spot, a slot mob|boss, optionally its own unit) and optionally a door it holds shut.
///   Template (server-wide from an admin, or a player's own): per floor (or "*"), per group (or "*"), per slot, the
///     units to spawn (a list to pick from at random, a count per marker, a level). It fills every marker that doesn't
///     name its own unit, so one layout runs as many different events.
///   Run: start spawns every marker's units, closes and locks each group's door; when a group's last unit is gone its
///     door unlocks and opens. All groups cleared = complete. Stop removes the units and puts the doors back.
///
/// Layouts and templates persist in BepInEx/config/Uriel/castle_events.json; runs don't (units never persist), but the
/// doors a run holds are saved, so a restart mid-run unlocks them and puts them back.
/// Log lines: [Uriel EVENT] key=value.
/// </summary>
internal sealed class CastleEventService
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
        public List<Marker> Markers { get; set; } = new();
    }

    internal sealed class Layout
    {
        public int Territory { get; set; }
        public int NextMarkerId { get; set; } = 1;
        public List<Group> Groups { get; set; } = new();
    }

    internal sealed class UnitSpec
    {
        public List<int> Prefabs { get; set; } = new();
        public int Count { get; set; } = 1;
        public int Level { get; set; }
    }

    internal sealed class SlotSet
    {
        public UnitSpec Mob { get; set; }
        public UnitSpec Boss { get; set; }
    }

    internal sealed class FloorTemplate
    {
        public Dictionary<string, SlotSet> Groups { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class Template
    {
        public string Name { get; set; }
        public ulong Owner { get; set; }         // 0 = a server template (an admin's), usable by everyone
        public Dictionary<string, FloorTemplate> Floors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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

    sealed class SaveFile
    {
        public int SchemaVersion { get; set; } = 1;
        public Dictionary<string, Layout> Layouts { get; set; } = new();
        public Dictionary<string, Template> Templates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<HeldDoor> HeldDoors { get; set; } = new();
    }

    // ------------------------------------------------------------------ runtime model

    sealed class RunGroup
    {
        public string Name;
        public string Tag;
        public HeldDoor Door;          // null when the group holds no door
        public int Spawned;
        public bool Cleared;
    }

    sealed class Run
    {
        public int Territory;
        public Entity Heart;
        public string Template;
        public ulong By;
        public DateTime Started;
        public List<RunGroup> Groups = new();
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
            _file.Templates = new Dictionary<string, Template>(_file.Templates ?? new(), StringComparer.OrdinalIgnoreCase);
            Core.Log.LogInfo($"[Uriel EVENT] loaded layouts={_file.Layouts.Count} templates={_file.Templates.Count} heldDoors={_file.HeldDoors.Count}");
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
        Core.Log.LogInfo($"[Uriel EVENT] marker-added territory={territory} group={g.Name} id={m.Id} slot={slot} floor={floor} x={F(m.X)} y={F(m.Y)} z={F(m.Z)} prefab={unitName ?? "template"} count={count} level={level} contain={contain ?? "default"} paid={m.PaidAmount}");
        return $"Marker #{m.Id} ({slot}) in group '{g.Name}' (floor {g.Floor}): {unitName ?? "units from the template"}" +
               $"{(count > 0 ? $" x{count}" : "")}. The group has {g.Markers.Count} marker(s){(g.Door != null ? " and a door" : ", no door")}." +
               (m.PaidAmount > 0 ? $" Paid {m.PaidAmount}x {new PrefabGUID(costItem).GetPrefabName()}." : "");
    }

    public string BindDoor(Entity character, bool isAdmin, string group, bool unbind)
    {
        if (!TryCallerPlot(character, isAdmin, out var feet, out var heart, out int territory, out string message)) return message;
        if (_runs.ContainsKey(territory)) return "An event is running on this plot. Stop it first ('.uriel event stop').";
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
                $"#{m.Id} {m.Slot} {(m.Prefab != 0 ? new PrefabGUID(m.Prefab).GetPrefabName() : "template")}{(m.Count > 0 ? $" x{m.Count}" : "")}"));
            lines.Add($"Floor {g.Floor} · {g.Name} · {door} · {markers}");
            Core.Log.LogInfo($"[Uriel EVENT] layout territory={territory} group={g.Name} floor={g.Floor} door={(g.Door == null ? "none" : $"{F(g.Door[0])},{F(g.Door[1])},{F(g.Door[2])}")} markers={g.Markers.Count}");
        }
        return lines;
    }

    static string DoorLabel(Entity heart, float[] d)
    {
        var door = CastleDoors.At(heart, new float3(d[0], d[1], d[2]));
        return door == Entity.Null ? $"door missing at {F(d[0])},{F(d[2])}" : $"door at {F(d[0])},{F(d[2])}{(CastleDoors.IsOpen(door) ? " (open)" : " (shut)")}";
    }

    // ------------------------------------------------------------------ templates

    IEnumerable<Template> Visible(ulong steamId, bool isAdmin) =>
        _file.Templates.Values.Where(t => t.Owner == 0 || t.Owner == steamId || isAdmin);

    public string CreateTemplate(Entity character, bool isAdmin, string name)
    {
        if (!isAdmin && !Settings.CastleEvents_PlayerEvents.Value) return "Castle events are admin-only on this server (CastleEvents.PlayerEvents).";
        if (!ValidName(name)) return "A template name is 1-24 letters, digits, '-' or '_'.";
        if (_file.Templates.ContainsKey(name)) return $"A template named '{name}' already exists.";
        ulong owner = isAdmin ? 0 : character.GetSteamId();
        _file.Templates[name] = new Template { Name = name, Owner = owner };
        Save();
        Core.Log.LogInfo($"[Uriel EVENT] template-created name={name} owner={owner}");
        return $"Template '{name}' created ({(owner == 0 ? "server template, everyone can use it" : "yours")}). Fill it: '.uriel template set {name} <floor|*> <group|*> <mob|boss> <unit[,unit...]> [count] [level]'.";
    }

    Template EditableTemplate(Entity character, bool isAdmin, string name, out string message)
    {
        message = null;
        if (!_file.Templates.TryGetValue(name, out var t)) { message = $"No template '{name}'."; return null; }
        if (!isAdmin && t.Owner != character.GetSteamId()) { message = $"Template '{name}' isn't yours."; return null; }
        return t;
    }

    public string SetTemplate(Entity character, bool isAdmin, string name, string floor, string group, string slot, string units, int count, int level)
    {
        var t = EditableTemplate(character, isAdmin, name, out string message);
        if (t == null) return message;
        if (floor != "*" && (!int.TryParse(floor, out int f) || f < 1 || f > 20)) return "The floor is 1, 2, ... or * for every floor.";
        if (group != "*" && !ValidName(group)) return "The group is a group name or * for every group.";
        slot = slot.ToLowerInvariant();
        if (slot != "mob" && slot != "boss") return "The slot is mob or boss.";
        var spec = new UnitSpec { Count = Math.Clamp(count, 1, Math.Max(1, Settings.CastleEvents_MaxUnitsPerCommand.Value)), Level = Math.Max(0, level) };
        var names = new List<string>();
        foreach (var u in units.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!UnitSpawnService.TryResolveUnitPrefab(u, out int guid, out string unitName, out message)) return message;
            spec.Prefabs.Add(guid);
            names.Add(unitName);
        }
        if (spec.Prefabs.Count == 0) return "Name at least one unit.";
        if (!t.Floors.TryGetValue(floor, out var ft)) t.Floors[floor] = ft = new FloorTemplate();
        if (!ft.Groups.TryGetValue(group, out var ss)) ft.Groups[group] = ss = new SlotSet();
        if (slot == "boss") ss.Boss = spec; else ss.Mob = spec;
        Save();
        Core.Log.LogInfo($"[Uriel EVENT] template-set name={t.Name} floor={floor} group={group} slot={slot} units={string.Join(",", names)} count={spec.Count} level={spec.Level}");
        return $"Template '{t.Name}': floor {floor}, group {group}, {slot} = {string.Join(" or ", names)} x{spec.Count}{(spec.Level > 0 ? $" at level {spec.Level}" : "")}.";
    }

    public List<string> ShowTemplate(Entity character, bool isAdmin, string name)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(name))
        {
            var list = Visible(character.GetSteamId(), isAdmin).OrderBy(t => t.Owner).ThenBy(t => t.Name).ToList();
            lines.Add(list.Count == 0 ? "No templates yet. '.uriel template create <name>'." :
                "Templates: " + string.Join(", ", list.Select(t => $"{t.Name}{(t.Owner == 0 ? "" : " (player's)")}")));
            return lines;
        }
        if (!_file.Templates.TryGetValue(name, out var tp) || !Visible(character.GetSteamId(), isAdmin).Contains(tp)) { lines.Add($"No template '{name}'."); return lines; }
        lines.Add($"Template '{tp.Name}' ({(tp.Owner == 0 ? "server" : "player's")}):");
        foreach (var (floor, ft) in tp.Floors.OrderBy(k => k.Key == "*" ? "~" : k.Key))
            foreach (var (group, ss) in ft.Groups.OrderBy(k => k.Key == "*" ? "~" : k.Key))
            {
                string Spec(UnitSpec s) => s == null ? "-" : $"{string.Join("/", s.Prefabs.Select(p => new PrefabGUID(p).GetPrefabName()))} x{s.Count}{(s.Level > 0 ? $" L{s.Level}" : "")}";
                lines.Add($"floor {floor} · group {group} · mob {Spec(ss.Mob)} · boss {Spec(ss.Boss)}");
            }
        if (tp.Floors.Count == 0) lines.Add("(empty)");
        return lines;
    }

    public string DeleteTemplate(Entity character, bool isAdmin, string name)
    {
        var t = EditableTemplate(character, isAdmin, name, out string message);
        if (t == null) return message;
        _file.Templates.Remove(t.Name);
        Save();
        Core.Log.LogInfo($"[Uriel EVENT] template-deleted name={t.Name}");
        return $"Template '{t.Name}' deleted.";
    }

    /// <summary>What a marker spawns: its own unit, else the template's most specific entry for its floor, group and
    /// slot (floor+group, floor+*, *+group, *+*). Null when neither names one.</summary>
    static UnitSpec Resolve(Template t, Group g, Marker m)
    {
        if (m.Prefab != 0) return new UnitSpec { Prefabs = new() { m.Prefab }, Count = Math.Max(1, m.Count), Level = m.Level };
        if (t == null) return null;
        string f = g.Floor.ToString(CultureInfo.InvariantCulture);
        foreach (var (fk, gk) in new[] { (f, g.Name), (f, "*"), ("*", g.Name), ("*", "*") })
        {
            if (!t.Floors.TryGetValue(fk, out var ft) || !ft.Groups.TryGetValue(gk, out var ss)) continue;
            var spec = m.Slot == "boss" ? ss.Boss : ss.Mob;
            if (spec == null || spec.Prefabs.Count == 0) continue;
            return new UnitSpec { Prefabs = spec.Prefabs, Count = m.Count > 0 ? m.Count : spec.Count, Level = m.Level > 0 ? m.Level : spec.Level };
        }
        return null;
    }

    // ------------------------------------------------------------------ runs

    public string StartRun(Entity character, bool isAdmin, string templateName)
    {
        if (!Settings.CastleEvents_Enabled.Value) return "Castle events are disabled by the server admin (CastleEvents.Enabled).";
        if (!TryCallerPlot(character, isAdmin, out _, out var heart, out int territory, out string message)) return message;
        if (_runs.ContainsKey(territory)) return "An event is already running on this plot. '.uriel event stop' ends it.";
        var layout = LayoutFor(territory, create: false);
        if (layout == null || layout.Groups.All(g => g.Markers.Count == 0)) return "This plot has no event markers. Place some: '.uriel event marker <group>'.";

        Template template = null;
        if (!string.IsNullOrEmpty(templateName) && templateName != "-")
        {
            if (!_file.Templates.TryGetValue(templateName, out template) || !Visible(character.GetSteamId(), isAdmin).Contains(template))
                return $"No template '{templateName}'. '.uriel template list' shows the ones you can use.";
        }

        // Every marker must resolve, and everything must fit under the caps, before anything spawns or is charged.
        var plan = new List<(Group G, Marker M, UnitSpec S)>();
        var missing = new List<string>();
        foreach (var g in layout.Groups)
            foreach (var m in g.Markers)
            {
                var spec = Resolve(template, g, m);
                if (spec == null) missing.Add($"#{m.Id} ({g.Name}, floor {g.Floor}, {m.Slot})");
                else plan.Add((g, m, spec));
            }
        if (missing.Count > 0)
            return $"No units for marker(s) {string.Join(", ", missing.Take(6))}{(missing.Count > 6 ? ", ..." : "")}: give them a unit or pick a template that covers them.";
        int total = plan.Sum(p => p.S.Count);
        int room = Core.Units.RoomOnPlot(territory);
        if (total > room) return $"This event needs {total} units; only {Math.Max(0, room)} fit under the unit caps (CastleEvents.MaxUnitsPerPlot / MaxTrackedUnits).";

        int costItem = Settings.CastleEvents_RunCostItem.Value, costAmount = Settings.CastleEvents_RunCostAmount.Value;
        if (!isAdmin && costItem != 0 && costAmount > 0)
        {
            if (!ObjectSpawnService.HasInventoryItems(character, costItem, costAmount))
                return $"Starting an event costs {costAmount}x {new PrefabGUID(costItem).GetPrefabName()}; you don't have enough in your inventory.";
            if (!ObjectSpawnService.ChargeInventory(character, costItem, costAmount)) return "Payment failed; the event did not start.";
        }

        var run = new Run { Territory = territory, Heart = heart, Template = template?.Name, By = character.GetSteamId(), Started = DateTime.UtcNow };
        string defaultContain = Settings.CastleEvents_DefaultContain.Value;
        int queued = 0, failed = 0;
        foreach (var g in layout.Groups)
        {
            var rg = new RunGroup { Name = g.Name, Tag = Tag(territory, g.Name) };
            run.Groups.Add(rg);
            if (g.Door != null) rg.Door = Hold(territory, heart, g);
            foreach (var (_, m, spec) in plan.Where(p => p.G == g))
            {
                int guid = spec.Prefabs[Rng.Next(spec.Prefabs.Count)];
                string name = new PrefabGUID(guid).GetPrefabName();
                if (!UnitSpawnService.TryParseContain(m.Contain ?? defaultContain, out var contain, out _))
                    contain = new UnitSpawnService.Containment(UnitSpawnService.Contain.Plot, 0f, Entity.Null);
                if (Core.Units.EnqueueAt(new float3(m.X, m.Y, m.Z), run.By, guid, name, spec.Count, spec.Level, contain, rg.Tag, out string why))
                {
                    queued += spec.Count;
                    rg.Spawned += spec.Count;
                }
                else
                {
                    failed++;
                    Core.Log.LogWarning($"[Uriel EVENT] marker-failed territory={territory} group={g.Name} id={m.Id} reason={why}");
                }
            }
        }
        _runs[territory] = run;
        Save();
        int doors = run.Groups.Count(x => x.Door != null);
        Core.Log.LogInfo($"[Uriel EVENT] started territory={territory} template={run.Template ?? "-"} groups={run.Groups.Count} units={queued} doors={doors} failedMarkers={failed} by={run.By}");
        Announce(run, $"A castle event has begun: {queued} foes in {run.Groups.Count} group(s){(doors > 0 ? $"; {doors} door(s) are sealed until their guards fall" : "")}.");
        return $"Event started{(run.Template != null ? $" from template '{run.Template}'" : "")}: {queued} unit(s) in {run.Groups.Count} group(s), {doors} door(s) held." +
               (failed > 0 ? $" {failed} marker(s) could not spawn (see the server log)." : "");
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

    /// <summary>Once a second: a group with no unit left (alive or pending) is cleared and its door opens; a door of a
    /// live group that someone opened is shut again; all groups cleared completes the run.</summary>
    void Monitor()
    {
        if (!Core.IsReady || _runs.Count == 0) return;
        foreach (var run in _runs.Values.ToList())
        {
            try
            {
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
                    Core.Log.LogInfo($"[Uriel EVENT] group-cleared territory={run.Territory} group={g.Name} units={g.Spawned} door={(g.Door == null ? "none" : opened ? "opened" : "pending")}");
                    Announce(run, $"Group '{g.Name}' is defeated{(opened ? "; its door opens" : "")}.");
                }
                if (run.Groups.All(g => g.Cleared))
                {
                    _runs.Remove(run.Territory);
                    var secs = (int)(DateTime.UtcNow - run.Started).TotalSeconds;
                    Core.Log.LogInfo($"[Uriel EVENT] complete territory={run.Territory} groups={run.Groups.Count} seconds={secs}");
                    Announce(run, $"The castle event is complete ({secs / 60}m {secs % 60}s).");
                }
            }
            catch (Exception ex)
            {
                Core.Log.LogWarning($"[Uriel EVENT] monitor failed territory={run.Territory}: {ex.Message}");
            }
        }
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
        string prefix = $"ev{run.Territory}:";
        int units = Core.Units.DespawnTagged(t => t.StartsWith(prefix, StringComparison.Ordinal));
        int doors = 0;
        foreach (var g in run.Groups.Where(g => !g.Cleared && g.Door != null))
            if (Release(g.Door, restore: true)) doors++;
        Save();
        Core.Log.LogInfo($"[Uriel EVENT] {why} territory={run.Territory} units={units} doorsRestored={doors}");
        Announce(run, "The castle event was stopped.");
        return $"Event {why}: removing {units} unit(s), {doors} door(s) put back as they were.";
    }

    public List<string> Status(Entity character, bool isAdmin)
    {
        var lines = new List<string>();
        if (!TryCallerPlot(character, isAdmin, out _, out _, out int territory, out string message)) { lines.Add(message); return lines; }
        if (!_runs.TryGetValue(territory, out var run)) { lines.Add("No event is running on this plot."); return lines; }
        var secs = (int)(DateTime.UtcNow - run.Started).TotalSeconds;
        lines.Add($"Event running {secs / 60}m {secs % 60}s{(run.Template != null ? $", template '{run.Template}'" : "")}: {run.Groups.Count(g => g.Cleared)}/{run.Groups.Count} group(s) cleared.");
        foreach (var g in run.Groups)
        {
            int left = Core.Units.CountTagged(g.Tag);
            lines.Add($"{g.Name}: {(g.Cleared ? "cleared" : $"{left}/{g.Spawned} left")}{(g.Door != null ? (g.Cleared ? ", door open" : ", door held") : "")}");
            Core.Log.LogInfo($"[Uriel EVENT] status territory={territory} group={g.Name} left={left} spawned={g.Spawned} cleared={g.Cleared} door={(g.Door == null ? "none" : g.Cleared ? "open" : "held")}");
        }
        return lines;
    }

    /// <summary>A purge or a castle loss ends every run: its doors are put back.</summary>
    public void StopAll(string why)
    {
        foreach (var run in _runs.Values.ToList()) Stop(run, why);
    }

    /// <summary>A system message to every player standing on the run's plot.</summary>
    static void Announce(Run run, string text)
    {
        try
        {
            var query = Core.EntityManager.CreateEntityQuery(ComponentType.ReadOnly(Il2CppType.Of<PlayerCharacter>()), ComponentType.ReadOnly(Il2CppType.Of<Translation>()));
            try
            {
                var players = query.ToEntityArray(Allocator.Temp);
                try
                {
                    foreach (var p in players)
                        if (CastleFloorIndex.InTerritory(run.Territory, p.Read<Translation>().Value)) ChatNotify.ToCharacter(p, text);
                }
                finally { players.Dispose(); }
            }
            finally { query.Dispose(); }
        }
        catch (Exception ex) { Core.Log.LogWarning($"[Uriel EVENT] announce failed: {ex.Message}"); }
    }
}
