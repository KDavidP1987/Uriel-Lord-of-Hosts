using System;
using System.Collections.Generic;

namespace Uriel.Services;

/// <summary>
/// Runtime catalog of every item prefab (name + GUID), built from
/// PrefabCollectionSystem once game data is loaded. Backs `.uriel finditem`
/// (players look up an item's numeric ID for `.uriel share cost`) and
/// validates cost-item GUIDs.
/// </summary>
internal sealed class ItemCatalogService
{
    readonly List<(string Name, int Guid)> _items = new();
    readonly HashSet<int> _guids = new();

    public int Count => _items.Count;

    public void Build()
    {
        _items.Clear();
        _guids.Clear();
        try
        {
            foreach (var kvp in Core.PrefabCollectionSystem.SpawnableNameToPrefabGuidDictionary)
            {
                string name = kvp.Key.ToString();
                if (!name.StartsWith("Item_", StringComparison.OrdinalIgnoreCase)) continue;
                int guid = kvp.Value._Value;
                _items.Add((name, guid));
                _guids.Add(guid);
            }
            _items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            Core.Log.LogInfo($"[Uriel ITEMS] catalog built: {_items.Count} item prefab(s).");
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[Uriel ITEMS] catalog build failed: {ex}");
        }
    }

    public bool IsKnownItem(int guid) => _guids.Contains(guid);

    public string NameOf(int guid)
    {
        foreach (var (name, g) in _items)
            if (g == guid) return name;
        return $"PrefabGuid({guid})";
    }

    /// <summary>Case-insensitive substring search; returns up to <paramref name="max"/> matches.</summary>
    public List<(string Name, int Guid)> Search(string fragment, int max, out int totalMatches)
    {
        var results = new List<(string, int)>();
        totalMatches = 0;
        if (string.IsNullOrWhiteSpace(fragment)) return results;
        foreach (var (name, guid) in _items)
        {
            if (!name.Contains(fragment, StringComparison.OrdinalIgnoreCase)) continue;
            totalMatches++;
            if (results.Count < max) results.Add((name, guid));
        }
        return results;
    }
}
