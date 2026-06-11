using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Text.Json;
using Stunlock.Core;

namespace Uriel.Services;

/// <summary>
/// Admin-managed per-object / global SPAWN CONDITIONS for the object-spawning feature
/// (docs/features/OBJECT_SPAWNING.md). Two layers:
///   - GLOBAL defaults that apply to every object;
///   - PER-OBJECT overrides keyed by prefab GUID, which fully REPLACE the global value for the
///     field they set.
/// Resolution per field: per-object value (if set) → global value (if set) → built-in fallback.
///
/// Only NON-ADMIN players are gated by these — admins always spawn freely/unrestricted (matches the
/// existing AdminOnly / cost model). Persisted to <c>object_conditions.json</c> under
/// BepInEx/config/Uriel, edited live with <c>.uriel objcfg</c> / <c>.uriel objcfgglobal</c>.
///
/// Fields:
///   - <see cref="Cond.MaxPerPlot"/>: max number of THIS object a player may have on one castle plot
///     (0 / unset = unlimited).
///   - <see cref="Cond.CostItem"/> + <see cref="Cond.CostAmount"/>: item GUID + count a player must
///     pay to spawn it. When set, OVERRIDES the server-wide ObjectSpawn.PrefabCostItem/Stack config;
///     item 0 = free.
///   - <see cref="Cond.PermitIndestructible"/>: may a player spawn it indestructible? false ⇒ the spawn
///     is forced breakable (or refused if the player explicitly asked for indestructible).
///   - <see cref="Cond.PermitRespawn"/>: may a player spawn it with the 'respawn' flag? false ⇒ a
///     respawn request is refused.
/// </summary>
internal sealed class ObjectConditionsService
{
    /// <summary>One condition layer. Every field is nullable so "unset" (fall through to the next layer)
    /// is distinct from an explicit value (including an explicit 0/false).</summary>
    internal sealed class Cond
    {
        public int? MaxPerPlot { get; set; }
        public int? CostItem { get; set; }
        public int? CostAmount { get; set; }
        public bool? PermitIndestructible { get; set; }
        public bool? PermitRespawn { get; set; }

        public bool IsEmpty =>
            MaxPerPlot is null && CostItem is null && CostAmount is null
            && PermitIndestructible is null && PermitRespawn is null;
    }

    /// <summary>The flattened, ready-to-enforce result of resolving a GUID against per-object + global.</summary>
    internal readonly struct Effective
    {
        public readonly int MaxPerPlot;          // 0 = unlimited
        public readonly bool CostSpecified;      // a condition layer set the cost (else caller uses the server config)
        public readonly int CostItem;
        public readonly int CostAmount;
        public readonly bool PermitIndestructible;
        public readonly bool PermitRespawn;
        public Effective(int max, bool costSpec, int costItem, int costAmt, bool permitInd, bool permitResp)
        {
            MaxPerPlot = max; CostSpecified = costSpec; CostItem = costItem; CostAmount = costAmt;
            PermitIndestructible = permitInd; PermitRespawn = permitResp;
        }
    }

    sealed class SaveFile
    {
        public int SchemaVersion { get; set; } = 1;
        public Cond Global { get; set; } = new();
        public Dictionary<string, Cond> Objects { get; set; } = new();
    }

    readonly Cond _global = new();
    readonly Dictionary<int, Cond> _byGuid = new();

    static string SaveDir => Path.Combine(BepInEx.Paths.ConfigPath, "Uriel");
    static string SavePath => Path.Combine(SaveDir, "object_conditions.json");

    public void Load()
    {
        try
        {
            if (!File.Exists(SavePath)) return;
            var file = JsonSerializer.Deserialize<SaveFile>(File.ReadAllText(SavePath));
            if (file is null) return;
            CopyInto(file.Global ?? new Cond(), _global);
            _byGuid.Clear();
            if (file.Objects != null)
                foreach (var kvp in file.Objects)
                    if (int.TryParse(kvp.Key, out int guid) && kvp.Value is { } c && !c.IsEmpty)
                        _byGuid[guid] = c;
            if (_byGuid.Count > 0 || !_global.IsEmpty)
                Core.Log.LogInfo($"[Uriel SPAWN] loaded object conditions: {_byGuid.Count} per-object, global {(_global.IsEmpty ? "unset" : "set")}.");
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[Uriel SPAWN] failed loading {SavePath}: {ex}");
        }
    }

    void SaveSync()
    {
        try
        {
            Directory.CreateDirectory(SaveDir);
            var file = new SaveFile { Global = _global };
            foreach (var kvp in _byGuid) if (!kvp.Value.IsEmpty) file.Objects[kvp.Key.ToString()] = kvp.Value;
            File.WriteAllText(SavePath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[Uriel SPAWN] failed saving {SavePath}: {ex}");
        }
    }

    static void CopyInto(Cond src, Cond dst)
    {
        dst.MaxPerPlot = src.MaxPerPlot;
        dst.CostItem = src.CostItem;
        dst.CostAmount = src.CostAmount;
        dst.PermitIndestructible = src.PermitIndestructible;
        dst.PermitRespawn = src.PermitRespawn;
    }

    /// <summary>Resolve the effective condition for a prefab GUID (per-object over global over fallback).</summary>
    public Effective Resolve(int guid)
    {
        _byGuid.TryGetValue(guid, out var o);
        int max = o?.MaxPerPlot ?? _global.MaxPerPlot ?? 0;
        // Cost is resolved as a UNIT from whichever layer specifies the item (a layer always sets item+amount together).
        Cond costLayer = (o?.CostItem is not null) ? o : (_global.CostItem is not null ? _global : null);
        bool costSpec = costLayer is not null;
        int costItem = costLayer?.CostItem ?? 0;
        int costAmt = costLayer?.CostAmount ?? 0;
        bool permInd = o?.PermitIndestructible ?? _global.PermitIndestructible ?? true;
        bool permResp = o?.PermitRespawn ?? _global.PermitRespawn ?? true;
        return new Effective(max, costSpec, costItem, costAmt, permInd, permResp);
    }

    public bool HasAnyConditions => _byGuid.Count > 0 || !_global.IsEmpty;

    // ------------------------------------------------------------ setters (command-backing)

    Cond LayerFor(int? guid, bool create)
    {
        if (guid is null) return _global;
        if (_byGuid.TryGetValue(guid.Value, out var c)) return c;
        if (!create) return null;
        return _byGuid[guid.Value] = new Cond();
    }

    public void SetMax(int? guid, int? max)
    {
        var c = LayerFor(guid, create: true);
        c.MaxPerPlot = (max is > 0) ? max : null;   // 0 / negative clears it (= unlimited)
        Prune(guid);
        SaveSync();
    }

    public void SetCost(int? guid, int? item, int amount)
    {
        var c = LayerFor(guid, create: true);
        if (item is null or 0) { c.CostItem = null; c.CostAmount = null; }      // free / cleared
        else { c.CostItem = item; c.CostAmount = Math.Max(0, amount); }
        Prune(guid);
        SaveSync();
    }

    public void SetPermitIndestructible(int? guid, bool? permit)
    {
        var c = LayerFor(guid, create: true);
        c.PermitIndestructible = permit;
        Prune(guid);
        SaveSync();
    }

    public void SetPermitRespawn(int? guid, bool? permit)
    {
        var c = LayerFor(guid, create: true);
        c.PermitRespawn = permit;
        Prune(guid);
        SaveSync();
    }

    /// <summary>Clear every override for one object (or reset the global layer when guid is null).</summary>
    public void Clear(int? guid)
    {
        if (guid is null) { CopyInto(new Cond(), _global); }
        else _byGuid.Remove(guid.Value);
        SaveSync();
    }

    // Drop a per-object entry that has become empty so the file stays tidy.
    void Prune(int? guid)
    {
        if (guid is null) return;
        if (_byGuid.TryGetValue(guid.Value, out var c) && c.IsEmpty) _byGuid.Remove(guid.Value);
    }

    // ------------------------------------------------------------ describe

    public string DescribeLayer(int? guid, string label)
    {
        var c = LayerFor(guid, create: false);
        if (c is null || c.IsEmpty) return $"{label}: no conditions set (uses defaults — unlimited, server cost config, indestructible/respawn allowed).";
        return $"{label}: {Render(c)}";
    }

    static string Render(Cond c)
    {
        var parts = new List<string>();
        if (c.MaxPerPlot is int m) parts.Add($"max {m}/plot");
        if (c.CostItem is int ci) parts.Add(ci == 0 ? "free" : $"cost {c.CostAmount ?? 0}x {new PrefabGUID(ci).GetPrefabName()}");
        if (c.PermitIndestructible is bool pi) parts.Add($"indestructible={(pi ? "allowed" : "denied")}");
        if (c.PermitRespawn is bool pr) parts.Add($"respawn={(pr ? "allowed" : "denied")}");
        return parts.Count == 0 ? "(none)" : string.Join(", ", parts);
    }

    public string DescribeAll()
    {
        var sb = new StringBuilder();
        sb.Append($"Global: {(_global.IsEmpty ? "(unset)" : Render(_global))}");
        if (_byGuid.Count == 0) { sb.Append("\nNo per-object conditions set."); return sb.ToString(); }
        sb.Append($"\nPer-object ({_byGuid.Count}):");
        foreach (var kvp in _byGuid)
        {
            string line = $"\n  {new PrefabGUID(kvp.Key).GetPrefabName()} ({kvp.Key}): {Render(kvp.Value)}";
            if (sb.Length + line.Length > 460) { sb.Append("\n  ...(more — edit object_conditions.json to see all)"); break; }
            sb.Append(line);
        }
        return sb.ToString();
    }
}
