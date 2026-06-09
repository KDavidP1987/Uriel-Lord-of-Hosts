using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Uriel.Services;

/// <summary>
/// Per-player object-discovery registry (docs/features/OBJECT_SPAWNING.md, Phase 2). In
/// Discovery access mode a player may only spawn objects they have UNLOCKED — by destroying
/// the object in the world (a % roll on the death event) or by an admin '.uriel grant'.
///
/// Stored as player_unlocks.json under BepInEx/config/Uriel/ (steamId -> set of prefab GUID
/// ints), mirroring the PublicStorageService JSON-persistence pattern: load on boot, save
/// synchronously on every change (changes happen at kill/command frequency).
/// </summary>
internal sealed class PlayerUnlockService
{
    sealed class SaveFile
    {
        public int SchemaVersion { get; set; } = 1;
        // steamId (string for JSON keys) -> list of unlocked prefab GUID ints.
        public Dictionary<string, List<int>> Unlocks { get; set; } = new();
        // steamId -> explicit discovery-notification preference (absent = use server default).
        public Dictionary<string, bool> NotifyPrefs { get; set; } = new();
        // steamId -> V-blood GUIDs the player has defeated (for the AllBosses unlock trigger).
        public Dictionary<string, List<int>> DefeatedBosses { get; set; } = new();
    }

    // steamId -> set of unlocked prefab GUIDs (in-memory working copy).
    readonly Dictionary<ulong, HashSet<int>> _unlocks = new();
    // steamId -> explicit notify preference (only players who set one with '.uriel notify').
    readonly Dictionary<ulong, bool> _notifyPref = new();
    // steamId -> set of defeated V-blood GUIDs (AllBosses progression).
    readonly Dictionary<ulong, HashSet<int>> _defeatedBosses = new();

    static string SaveDir => Path.Combine(BepInEx.Paths.ConfigPath, "Uriel");
    static string SavePath => Path.Combine(SaveDir, "player_unlocks.json");

    public void Load()
    {
        try
        {
            if (!File.Exists(SavePath)) return;
            var file = JsonSerializer.Deserialize<SaveFile>(File.ReadAllText(SavePath));
            if (file?.Unlocks is null) return;
            _unlocks.Clear();
            foreach (var kvp in file.Unlocks)
                if (ulong.TryParse(kvp.Key, out ulong steamId))
                    _unlocks[steamId] = new HashSet<int>(kvp.Value);
            _notifyPref.Clear();
            if (file.NotifyPrefs is not null)
                foreach (var kvp in file.NotifyPrefs)
                    if (ulong.TryParse(kvp.Key, out ulong steamId)) _notifyPref[steamId] = kvp.Value;
            _defeatedBosses.Clear();
            if (file.DefeatedBosses is not null)
                foreach (var kvp in file.DefeatedBosses)
                    if (ulong.TryParse(kvp.Key, out ulong steamId)) _defeatedBosses[steamId] = new HashSet<int>(kvp.Value);
            Core.Log.LogInfo($"[Uriel SPAWN] loaded object unlocks for {_unlocks.Count} player(s).");
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
            var file = new SaveFile();
            foreach (var kvp in _unlocks)
                file.Unlocks[kvp.Key.ToString()] = new List<int>(kvp.Value);
            foreach (var kvp in _notifyPref)
                file.NotifyPrefs[kvp.Key.ToString()] = kvp.Value;
            foreach (var kvp in _defeatedBosses)
                file.DefeatedBosses[kvp.Key.ToString()] = new List<int>(kvp.Value);
            File.WriteAllText(SavePath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[Uriel SPAWN] failed saving {SavePath}: {ex}");
        }
    }

    public bool IsUnlocked(ulong steamId, int prefabGuid) =>
        _unlocks.TryGetValue(steamId, out var set) && set.Contains(prefabGuid);

    /// <summary>Unlock a prefab for a player. Returns true if it was newly added.</summary>
    public bool Unlock(ulong steamId, int prefabGuid)
    {
        if (!_unlocks.TryGetValue(steamId, out var set))
            _unlocks[steamId] = set = new HashSet<int>();
        if (!set.Add(prefabGuid)) return false;
        SaveSync();
        return true;
    }

    /// <summary>Remove a prefab from a player's unlocks. Returns true if it was present.</summary>
    public bool Revoke(ulong steamId, int prefabGuid)
    {
        if (!_unlocks.TryGetValue(steamId, out var set) || !set.Remove(prefabGuid)) return false;
        SaveSync();
        return true;
    }

    /// <summary>Drop any unlocked GUIDs that fail <paramref name="keep"/> — used to scrub stale
    /// unlocks that are no longer valid placeable objects (e.g. CHAR_/V Blood GUIDs persisted by an
    /// older, looser catalog filter). Returns the number removed; persists only if something changed.</summary>
    public int PruneUnlocked(ulong steamId, Func<int, bool> keep)
    {
        if (!_unlocks.TryGetValue(steamId, out var set) || set.Count == 0) return 0;
        int before = set.Count;
        set.RemoveWhere(g => !keep(g));
        int removed = before - set.Count;
        if (removed > 0) SaveSync();
        return removed;
    }

    public IReadOnlyCollection<int> GetUnlocked(ulong steamId) =>
        _unlocks.TryGetValue(steamId, out var set) ? set : (IReadOnlyCollection<int>)Array.Empty<int>();

    public int Count(ulong steamId) =>
        _unlocks.TryGetValue(steamId, out var set) ? set.Count : 0;

    // ---- discovery-notification preference ----

    /// <summary>Whether to send this player discovery messages: their explicit preference if set,
    /// otherwise the server default.</summary>
    public bool WantsNotify(ulong steamId, bool serverDefault) =>
        _notifyPref.TryGetValue(steamId, out bool pref) ? pref : serverDefault;

    /// <summary>True if the player has set an explicit preference (vs. following the server default).</summary>
    public bool HasNotifyPref(ulong steamId) => _notifyPref.ContainsKey(steamId);

    public void SetNotify(ulong steamId, bool on)
    {
        _notifyPref[steamId] = on;
        SaveSync();
    }

    // ---- defeated-boss tracking (AllBosses unlock) ----

    public bool HasDefeatedBoss(ulong steamId, int vbloodGuid) =>
        _defeatedBosses.TryGetValue(steamId, out var set) && set.Contains(vbloodGuid);

    /// <summary>Record a V-blood defeat. Returns true if newly recorded.</summary>
    public bool RecordBossDefeat(ulong steamId, int vbloodGuid)
    {
        if (!_defeatedBosses.TryGetValue(steamId, out var set))
            _defeatedBosses[steamId] = set = new HashSet<int>();
        if (!set.Add(vbloodGuid)) return false;
        SaveSync();
        return true;
    }
}
