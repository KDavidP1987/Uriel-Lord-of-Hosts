using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Shared;
using ProjectM.Tiles;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Uriel.Config;

namespace Uriel.Services;

/// <summary>
/// Object spawning (docs/features/OBJECT_SPAWNING.md) — generalizes the proven
/// StairSwapService spawn recipe into a "place a prefab into the castle you're
/// standing in" engine.
///
///   resolve PrefabGUID (name or int) -> EntityManager.Instantiate -> strip
///   Disabled -> ApplyTransform (Translation/Rotation + tile grid) -> adopt into
///   the castle whose TERRITORY contains the spawn point (copy heart/team/owner) ->
///   optionally Immortal + decay-proof.
///
/// Phase 1.5 (this file):
///  - PLACEMENT GATE: a spawn point must be inside a castle plot (CastleTerritory).
///    Players may only place in a plot their team owns; admins may place in any plot.
///    Open-world placement is refused for everyone.
///  - PERSISTENCE: every spawned object is recorded in spawned_objects.json (keyed by
///    prefab GUID + tile coords, like PublicStorageService). On boot the records are
///    re-resolved so despawn/move/rotate work across sessions and Immortal/decay
///    re-applies. Records whose castle heart is gone are purged (orphan cleanup,
///    config-gated) — "the object disappears alongside the castle."
///  - MANAGEMENT: '.uriel spawnlist' / '.uriel purgeplot' (whole plot), '.uriel despawn'
///    (one object). Move/rotate are respawn-based (these objects render from baked
///    static batches; an in-place edit risks the stair "invisible until restart" bug).
/// </summary>
internal sealed class ObjectSpawnService
{
    // Tile-grid origin offset + territory block size (KindredCommands ConvertPosToGrid/Block).
    const int TileGridOffset = 6400;
    const float BlockSize = 10f;

    // Live cache of spawned entities (rebuilt from the registry on boot) — fast targeting.
    readonly List<Entity> _spawned = new();

    // ============================================================ persistence registry

    internal sealed class SpawnRecord
    {
        public int PrefabGuid { get; set; }
        public int TileX { get; set; }
        public int TileY { get; set; }
        public bool Indestructible { get; set; }
        public int TerritoryIndex { get; set; } = -1;
        public bool HasHeart { get; set; }
        public int HeartTileX { get; set; }
        public int HeartTileY { get; set; }
        public ulong SpawnedBySteamId { get; set; }
        public string SpawnedAtUtc { get; set; }
        // Item cost actually paid by a player at spawn (for '.uriel despawn' refund). 0 = free/admin.
        public int PaidCostItem { get; set; }
        public int PaidCostAmount { get; set; }
    }

    sealed class SaveFile
    {
        public int SchemaVersion { get; set; } = 1;
        public List<SpawnRecord> Objects { get; set; } = new();
    }

    readonly List<SpawnRecord> _records = new();
    // Admin blocklist: prefab GUIDs an admin has flagged ineligible/problematic. Blocked
    // prefabs are excluded from the catalog (so never discoverable/grantable/listed) AND
    // refused at spawn even by GUID. Persisted separately; changes invalidate the catalog.
    readonly HashSet<int> _blocked = new();
    // Boss-tier unlock map (Phase 3): V-blood prefab GUID -> the object GUIDs killing it grants.
    readonly Dictionary<int, List<int>> _bossUnlocks = new();

    static string SaveDir => Path.Combine(BepInEx.Paths.ConfigPath, "Uriel");
    static string SavePath => Path.Combine(SaveDir, "spawned_objects.json");
    static string SavePathBlocked => Path.Combine(SaveDir, "blocked_prefabs.json");
    static string SavePathBossMap => Path.Combine(SaveDir, "boss_unlocks.json");

    public void Load()
    {
        LoadRecords();
        LoadBlocked();
        LoadBossMap();
    }

    void LoadBossMap()
    {
        try
        {
            if (!File.Exists(SavePathBossMap)) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, List<int>>>(File.ReadAllText(SavePathBossMap));
            if (map is null) return;
            _bossUnlocks.Clear();
            foreach (var kvp in map)
                if (int.TryParse(kvp.Key, out int vblood)) _bossUnlocks[vblood] = new List<int>(kvp.Value);
            if (_bossUnlocks.Count > 0) Core.Log.LogInfo($"[Uriel SPAWN] loaded boss-unlock map for {_bossUnlocks.Count} V-blood(s).");
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[Uriel SPAWN] failed loading {SavePathBossMap}: {ex}");
        }
    }

    void SaveBossMap()
    {
        try
        {
            Directory.CreateDirectory(SaveDir);
            var map = new Dictionary<string, List<int>>();
            foreach (var kvp in _bossUnlocks) map[kvp.Key.ToString()] = kvp.Value;
            File.WriteAllText(SavePathBossMap, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[Uriel SPAWN] failed saving {SavePathBossMap}: {ex}");
        }
    }

    void LoadRecords()
    {
        try
        {
            if (!File.Exists(SavePath)) return;
            var file = JsonSerializer.Deserialize<SaveFile>(File.ReadAllText(SavePath));
            if (file?.Objects is null) return;
            _records.Clear();
            _records.AddRange(file.Objects);
            Core.Log.LogInfo($"[Uriel SPAWN] loaded {_records.Count} spawned-object record(s).");
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[Uriel SPAWN] failed loading {SavePath}: {ex}");
        }
    }

    void LoadBlocked()
    {
        try
        {
            if (!File.Exists(SavePathBlocked)) return;
            var list = JsonSerializer.Deserialize<List<int>>(File.ReadAllText(SavePathBlocked));
            if (list is null) return;
            _blocked.Clear();
            foreach (int g in list) _blocked.Add(g);
            if (_blocked.Count > 0) Core.Log.LogInfo($"[Uriel SPAWN] loaded {_blocked.Count} blocked prefab(s).");
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[Uriel SPAWN] failed loading {SavePathBlocked}: {ex}");
        }
    }

    void SaveBlocked()
    {
        try
        {
            Directory.CreateDirectory(SaveDir);
            File.WriteAllText(SavePathBlocked, JsonSerializer.Serialize(new List<int>(_blocked), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[Uriel SPAWN] failed saving {SavePathBlocked}: {ex}");
        }
    }

    // Registry changes happen at command frequency — save synchronously on every change.
    void SaveSync()
    {
        try
        {
            Directory.CreateDirectory(SaveDir);
            var json = JsonSerializer.Serialize(
                new SaveFile { Objects = _records },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SavePath, json);
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[Uriel SPAWN] failed saving {SavePath}: {ex}");
        }
    }

    void RegisterRecord(Entity e, bool indestructible, int territoryIndex, Entity heart, ulong bySteamId,
                        int paidItem = 0, int paidAmount = 0)
    {
        if (!e.TryGetComponent<TilePosition>(out var tp))
        {
            Core.Log.LogWarning($"[Uriel SPAWN] {e.GetPrefabGuid().GetPrefabName()} has no TilePosition; not persisted (session-only).");
            return;
        }
        var rec = new SpawnRecord
        {
            PrefabGuid = e.GetPrefabGuid()._Value,
            TileX = tp.Tile.x,
            TileY = tp.Tile.y,
            Indestructible = indestructible,
            TerritoryIndex = territoryIndex,
            SpawnedBySteamId = bySteamId,
            SpawnedAtUtc = DateTime.UtcNow.ToString("u"),
            PaidCostItem = paidItem,
            PaidCostAmount = paidAmount,
        };
        if (heart.Exists() && heart.TryGetComponent<TilePosition>(out var ht))
        {
            rec.HasHeart = true;
            rec.HeartTileX = ht.Tile.x;
            rec.HeartTileY = ht.Tile.y;
        }
        _records.Add(rec);
        SaveSync();
    }

    SpawnRecord FindRecordFor(Entity e)
    {
        if (!e.TryGetComponent<TilePosition>(out var tp)) return null;
        int guid = e.GetPrefabGuid()._Value;
        foreach (var r in _records)
            if (r.PrefabGuid == guid && r.TileX == tp.Tile.x && r.TileY == tp.Tile.y) return r;
        return null;
    }

    /// <summary>Re-resolve the live entity for a record (prefab GUID + tile coords) — must
    /// include disabled entities (castle objects sit Disabled when no player is near).</summary>
    Entity ResolveRecord(SpawnRecord r)
    {
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(new(Il2CppType.Of<PrefabGUID>(), ComponentType.AccessMode.ReadOnly))
            .AddAll(new(Il2CppType.Of<TilePosition>(), ComponentType.AccessMode.ReadOnly))
            .WithOptions(EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludeSpawnTag);
        var query = Core.EntityManager.CreateEntityQuery(ref builder);
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                if (!e.TryGetComponent<PrefabGUID>(out var g) || g._Value != r.PrefabGuid) continue;
                if (!e.TryGetComponent<TilePosition>(out var tp)) continue;
                if (tp.Tile.x == r.TileX && tp.Tile.y == r.TileY) return e;
            }
        }
        finally { entities.Dispose(); }
        return Entity.Null;
    }

    /// <summary>
    /// Boot re-apply: rebuild the live cache from the registry, re-assert Immortal/decay,
    /// drop records whose object is gone, and (config-gated) destroy "orphans" whose castle
    /// heart no longer exists — so spawned objects vanish with a destroyed/decayed castle.
    /// </summary>
    public void ReapplySpawned()
    {
        if (_records.Count == 0) return;

        // One sweep over all tile objects -> (guid,tileX,tileY) -> entity (avoids an N-record query storm).
        var index = new Dictionary<(int, int, int), Entity>();
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(new(Il2CppType.Of<PrefabGUID>(), ComponentType.AccessMode.ReadOnly))
            .AddAll(new(Il2CppType.Of<TilePosition>(), ComponentType.AccessMode.ReadOnly))
            .WithOptions(EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludeSpawnTag);
        var query = Core.EntityManager.CreateEntityQuery(ref builder);
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                if (!e.TryGetComponent<PrefabGUID>(out var g)) continue;
                if (!e.TryGetComponent<TilePosition>(out var tp)) continue;
                index[(g._Value, tp.Tile.x, tp.Tile.y)] = e;
            }
        }
        finally { entities.Dispose(); }

        bool purgeOrphans = Settings.ObjectSpawn_PurgeOrphansOnBoot.Value;
        int restored = 0, orphaned = 0, gone = 0;
        foreach (var r in new List<SpawnRecord>(_records))
        {
            if (!index.TryGetValue((r.PrefabGuid, r.TileX, r.TileY), out Entity e) || !e.Exists())
            {
                _records.Remove(r); // object no longer present (dismantled / removed)
                gone++;
                continue;
            }
            // Orphan check: did the owning castle heart disappear? (disabled-included, so not transient.)
            if (purgeOrphans && r.HasHeart && !HeartExistsByTile(r.HeartTileX, r.HeartTileY))
            {
                DestroyUtility.Destroy(Core.EntityManager, e);
                _records.Remove(r);
                orphaned++;
                continue;
            }
            if (r.Indestructible)
            {
                e.AddOrSet(new Immortal { IsImmortal = true });
                if (e.Has<CastleDecayAndRegen>()) e.With((ref CastleDecayAndRegen d) => d.CanDieFromDecay = false);
                else e.AddOrSet(new CastleDecayAndRegen { CanDieFromDecay = false });
            }
            _spawned.Add(e);
            restored++;
        }
        SaveSync();
        Core.Log.LogInfo($"[Uriel SPAWN] restored {restored} object(s); {orphaned} orphan(s) purged (castle gone); {gone} no longer present.");
    }

    // ============================================================ castle territory / ownership

    /// <summary>
    /// Resolve the castle plot that CONTAINS a world position (KindredCommands
    /// CastleTerritoryService pattern): position -> block coord -> territory index ->
    /// the CastleHeart whose CastleTerritoryEntity carries that index. Returns false for
    /// open world (no territory). Built fresh per call (spawns are infrequent; current beats cached).
    /// </summary>
    bool TryResolvePlot(float3 pos, out Entity heart, out int territoryIndex)
    {
        heart = Entity.Null;
        territoryIndex = GetTerritoryIndex(pos);
        if (territoryIndex < 0) return false;
        heart = GetHeartForTerritory(territoryIndex);
        return heart.Exists();
    }

    static int2 ConvertPosToBlockCoord(float3 pos)
    {
        int gridX = (int)math.floor(pos.x * 2) + TileGridOffset;
        int gridZ = (int)math.floor(pos.z * 2) + TileGridOffset;
        return new int2((int)math.floor(gridX / BlockSize), (int)math.floor(gridZ / BlockSize));
    }

    int GetTerritoryIndex(float3 pos)
    {
        int2 block = ConvertPosToBlockCoord(pos);
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(new(Il2CppType.Of<CastleTerritory>(), ComponentType.AccessMode.ReadOnly))
            .WithOptions(EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludeSpawnTag);
        var query = Core.EntityManager.CreateEntityQuery(ref builder);
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            for (int i = 0; i < entities.Length; i++)
            {
                var te = entities[i];
                if (!te.TryGetComponent<CastleTerritory>(out var ct)) continue;
                if (!Core.EntityManager.HasComponent<CastleTerritoryBlocks>(te)) continue;
                var blocks = Core.EntityManager.GetBuffer<CastleTerritoryBlocks>(te);
                for (int b = 0; b < blocks.Length; b++)
                {
                    if (blocks[b].BlockCoordinate.Equals(block))
                        return ct.CastleTerritoryIndex;
                }
            }
        }
        finally { entities.Dispose(); }
        return -1;
    }

    Entity GetHeartForTerritory(int territoryIndex)
    {
        if (territoryIndex < 0) return Entity.Null;
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(new(Il2CppType.Of<CastleHeart>(), ComponentType.AccessMode.ReadOnly))
            .WithOptions(EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludeSpawnTag);
        var query = Core.EntityManager.CreateEntityQuery(ref builder);
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            for (int i = 0; i < entities.Length; i++)
            {
                var h = entities[i];
                if (!h.TryGetComponent<CastleHeart>(out var hd)) continue;
                Entity territory = hd.CastleTerritoryEntity;
                if (!territory.TryGetComponent<CastleTerritory>(out var ct)) continue;
                if (ct.CastleTerritoryIndex == territoryIndex) return h;
            }
        }
        finally { entities.Dispose(); }
        return Entity.Null;
    }

    /// <summary>Does the character's team own this castle heart? (admins bypass this).</summary>
    static bool OwnsHeart(Entity character, Entity heart)
    {
        if (!heart.TryGetComponent<Team>(out var heartTeam)) return false;
        if (!character.TryGetComponent<Team>(out var charTeam)) return false;
        return charTeam.Value == heartTeam.Value;
    }

    /// <summary>Is a castle heart present at this tile? (disabled-included — a true absence
    /// means the castle is gone, not merely streamed out.)</summary>
    static bool HeartExistsByTile(int x, int y)
    {
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(new(Il2CppType.Of<CastleHeart>(), ComponentType.AccessMode.ReadOnly))
            .AddAll(new(Il2CppType.Of<TilePosition>(), ComponentType.AccessMode.ReadOnly))
            .WithOptions(EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludeSpawnTag);
        var query = Core.EntityManager.CreateEntityQuery(ref builder);
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            for (int i = 0; i < entities.Length; i++)
            {
                if (!entities[i].TryGetComponent<TilePosition>(out var tile)) continue;
                if (tile.Tile.x == x && tile.Tile.y == y) return true;
            }
        }
        finally { entities.Dispose(); }
        return false;
    }

    /// <summary>Gate helper: resolve the plot at a point and enforce inside-a-plot + ownership.</summary>
    bool CheckPlacement(Entity character, bool isAdmin, float3 pos, string verb, out Entity heart, out int territory, out string error)
    {
        error = null;
        if (!TryResolvePlot(pos, out heart, out territory))
        {
            error = $"Objects can only be {verb} inside a castle plot — stand/aim inside a castle.";
            return false;
        }
        if (!isAdmin && !OwnsHeart(character, heart))
        {
            error = $"You can only {verb.Replace("placed", "place").Replace("moved", "move")} objects in your own castle plot.";
            return false;
        }
        return true;
    }

    // ============================================================ object catalog

    readonly struct CatalogEntry
    {
        public readonly string Name;
        public readonly PrefabGUID Guid;
        public readonly string Label; // humanized, wire-safe (spaces -> _) display name for BCH
        public readonly string Cat;   // category tag for BCH grouping / fallback icons
        public CatalogEntry(string name, PrefabGUID guid, string label, string cat)
        { Name = name; Guid = guid; Label = label; Cat = cat; }
    }

    // Lazily-built, name-sorted catalog of REAL placeable objects, filtered from the raw
    // spawnable set by component signature: keep only things a player could place (TilePosition
    // / EditableTileModel / CastleHeartConnection); drop abilities/buffs, spawn-chain
    // controllers (Chain_*: SpawnChainData), and dev/debug prefabs.
    List<CatalogEntry> _placeable;
    HashSet<int> _placeableGuids;     // fast "is this GUID a spawnable object?" (discovery hook)
    HashSet<int> _discoverableGuids;  // placeable AND destroyable-by-a-player (eligible for discovery)
    Dictionary<int, CatalogEntry> _byGuid; // GUID -> entry (label/cat lookup for the unlocked API)

    void EnsureCatalog()
    {
        if (_placeable != null) return;
        var list = new List<CatalogEntry>();
        var placeableGuids = new HashSet<int>();
        var discoverableGuids = new HashSet<int>();
        int chains = 0, debug = 0, blocked = 0, units = 0, castle = 0, scanned = 0;
        bool includeCastle = Settings.ObjectSpawn_IncludeCastleBuildables.Value;
        // Resolve through the RAW GuidToEntityMap, not _PrefabLookupMap.TryGetValue — the
        // wrapper logs a "Prefab … in an unknown state" warning per miss, flooding the console.
        var guidMap = Core.PrefabCollectionSystem._PrefabLookupMap.GuidToEntityMap;
        foreach (var kvp in Core.PrefabCollectionSystem.SpawnableNameToPrefabGuidDictionary)
        {
            scanned++;
            string name = kvp.Key.ToString();
            if (IsDebugPrefab(name)) { debug++; continue; }
            if (_blocked.Contains(kvp.Value._Value)) { blocked++; continue; } // admin-blocked: not available at all
            if (!guidMap.TryGetValue(kvp.Value, out Entity prefab) || !prefab.Exists())
                continue;
            if (IsSpawnChainController(prefab)) { chains++; continue; }
            if (IsNonObject(name, prefab)) { units++; continue; }   // characters/abilities/internal — carry TilePosition too
            // Inherent castle build-menu pieces (BlueprintData) — excluded unless the admin opts in;
            // the feature is about WORLD objects, and these are already player-buildable.
            if (!includeCastle && prefab.Has<BlueprintData>()) { castle++; continue; }
            if (!IsPlaceableObject(prefab)) continue;
            list.Add(new CatalogEntry(name, kvp.Value, SafeToken(Humanize(name)), Categorize(name, prefab)));
            placeableGuids.Add(kvp.Value._Value);
            if (IsDiscoverable(prefab)) discoverableGuids.Add(kvp.Value._Value);
        }
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        _placeable = list;
        _placeableGuids = placeableGuids;
        _discoverableGuids = discoverableGuids;
        _byGuid = new Dictionary<int, CatalogEntry>(list.Count);
        foreach (var ce in list) _byGuid[ce.Guid._Value] = ce;
        Core.Log.LogInfo($"[Uriel SPAWN] object catalog built: {_placeable.Count} placeable objects " +
                         $"({_discoverableGuids.Count} discoverable by destruction; " +
                         $"{chains} chains, {units} units/NPCs, {castle} castle-buildables, {debug} debug, {blocked} blocked skipped) of {scanned} spawnables.");
    }

    /// <summary>
    /// Not a real placeable object, despite carrying TilePosition/TileModel like decor does.
    /// A prefab-dump audit (Session 8) found several families leak through the positive filter:
    ///  - CHAR_* characters/NPCs (~530);
    ///  - AB_* ability-effect objects (spike traps, boss hazard spinners, continuous-damage areas);
    ///  - GM_* gamemaster/debug props; Liquid_* placement-rule objects; Summon*/USB_/PrefabVariant internals.
    /// Excluded by name family plus a Movement backstop (no real placeable object has Movement — verified).
    /// Borderline-but-kept families: MicroPOI_* (tree/flower decor clusters) and EH_* (armor racks,
    /// cages) — real world decor; an admin can '.uriel block' specific ones if undesired.
    /// </summary>
    static readonly string[] NonObjectPrefixes =
        { "CHAR_", "AB_", "GM_", "Liquid_", "Summon", "USB_", "PrefabVariant" };

    static bool IsNonObject(string name, Entity prefab)
    {
        foreach (var p in NonObjectPrefixes)
            if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        return prefab.Has<Movement>();
    }

    /// <summary>
    /// A placeable object a player can DESTROY in normal play (so discovery-by-destruction can
    /// reach it): has Health that dies (HealthConstants.DestroyOnDeath) and isn't a castle build
    /// piece (BlueprintData — those are built, not destroyed) or natively Immortal. Objects that
    /// fail this can only be obtained via '.uriel grant' — answering "not all spawnables are
    /// equally destroyable" (roughly half of TM_* lack Health entirely).
    /// </summary>
    static bool IsDiscoverable(Entity prefab)
    {
        if (!prefab.Has<Health>()) return false;
        if (prefab.Has<BlueprintData>()) return false;
        if (prefab.TryGetComponent<Immortal>(out var im) && im.IsImmortal) return false;
        return prefab.TryGetComponent<HealthConstants>(out var hc) && hc.DestroyOnDeath;
    }

    public bool IsPlaceableGuid(int guid) { EnsureCatalog(); return _placeableGuids.Contains(guid); }
    public bool IsDiscoverableGuid(int guid) { EnsureCatalog(); return _discoverableGuids.Contains(guid); }

    // ============================================================ player access (Phase 2)

    static readonly System.Random _rng = new();

    static bool IsDiscoveryMode() =>
        string.Equals(Settings.ObjectSpawn_PlayerAccessMode.Value?.Trim(), "Discovery", StringComparison.OrdinalIgnoreCase);

    /// <summary>Should the death hook process player kills? (collection open to players in Discovery
    /// mode, with at least one unlock source active). Checked first so the hook is a no-op in the
    /// admin test-bed (AdminOnly=true), in Full mode, or when collection is disabled.</summary>
    public bool TracksKills =>
        Settings.ObjectSpawn_Enabled.Value
        && Settings.ObjectSpawn_CollectionEnabled.Value
        && !Settings.ObjectSpawn_AdminOnly.Value
        && IsDiscoveryMode()
        && (Settings.ObjectSpawn_DiscoveryChancePercent.Value > 0
            || Settings.ObjectSpawn_BossUnlocksEnabled.Value
            || NonDestructibleMode() is "collection" or "finalboss" or "allbosses");

    const int DraculaGuid = -327335305; // CHAR_Vampire_Dracula_VBlood — game-completion boss

    static string NonDestructibleMode() =>
        Settings.ObjectSpawn_NonDestructibleUnlock.Value?.Trim().ToLowerInvariant() ?? "off";

    static bool IsVBlood(Entity e) => e.Has<VBloodConsumeSource>();

    /// <summary>A player killed something. Three unlock sources: (B) boss-tier map (killed a mapped
    /// V-blood), discovery roll (destroyed a discoverable object), and (A) completion reward (just hit
    /// 100% of the discoverable set). Called from the death-event patch.</summary>
    public void HandleKill(Entity killer, Entity died)
    {
        if (!TracksKills) return;
        ulong steamId = killer.GetSteamId();
        if (steamId == 0) return;
        int guid = died.GetPrefabGuid()._Value;
        if (guid == 0) return;

        // (B) Boss-tier unlock — killing a mapped V-blood grants that boss's specific objects.
        if (Settings.ObjectSpawn_BossUnlocksEnabled.Value && _bossUnlocks.TryGetValue(guid, out var bossObjects))
            GrantBossObjects(killer, steamId, guid, bossObjects);

        // Non-destructible unlock via boss completion (FinalBoss = Dracula; AllBosses = every main V-blood).
        string mode = NonDestructibleMode();
        if ((mode == "finalboss" || mode == "allbosses") && IsVBlood(died))
            HandleBossCompletion(killer, steamId, guid, mode);

        // Discovery roll — destroyed a discoverable, not-yet-owned object.
        int chance = Settings.ObjectSpawn_DiscoveryChancePercent.Value;
        if (chance > 0 && IsDiscoverableGuid(guid) && !Core.PlayerUnlock.IsUnlocked(steamId, guid)
            && _rng.Next(100) < chance && Core.PlayerUnlock.Unlock(steamId, guid))
        {
            string objName = new PrefabGUID(guid).GetPrefabName();
            Core.Log.LogInfo($"[Uriel SPAWN] player {steamId} discovered {objName}.");
            if (Core.PlayerUnlock.WantsNotify(steamId, Settings.ObjectSpawn_DiscoveryNotify.Value))
                Notify(killer, $"Uriel: you can now build {objName}! Use '.uriel spawn {objName}'.");
            // Collection trigger — did that complete the discoverable set?
            if (mode == "collection" && IsDiscoverableComplete(steamId))
                GrantIndestructibles(killer, steamId, "collecting everything");
        }
    }

    void GrantBossObjects(Entity killer, ulong steamId, int vbloodGuid, List<int> objects)
    {
        int newly = 0;
        foreach (int g in objects) if (Core.PlayerUnlock.Unlock(steamId, g)) newly++;
        if (newly == 0) return; // player already had them all
        string bossName = new PrefabGUID(vbloodGuid).GetPrefabName();
        Core.Log.LogInfo($"[Uriel SPAWN] player {steamId} unlocked {newly} object(s) from defeating {bossName}.");
        if (Core.PlayerUnlock.WantsNotify(steamId, Settings.ObjectSpawn_DiscoveryNotify.Value))
            Notify(killer, $"Uriel: defeating {bossName} unlocked {newly} new object(s) to build!");
    }

    /// <summary>FinalBoss/AllBosses: a V-blood died to a player — grant the non-destructibles on
    /// the final-boss kill, or once every main boss is down.</summary>
    void HandleBossCompletion(Entity killer, ulong steamId, int vbloodGuid, string mode)
    {
        if (mode == "finalboss")
        {
            if (vbloodGuid == DraculaGuid) GrantIndestructibles(killer, steamId, "defeating Dracula");
            return;
        }
        // allbosses
        EnsureBossRoster();
        if (!_bossRoster.Contains(vbloodGuid)) return;     // a gate variant / non-roster V-blood
        Core.PlayerUnlock.RecordBossDefeat(steamId, vbloodGuid);
        foreach (int b in _bossRoster)
            if (!Core.PlayerUnlock.HasDefeatedBoss(steamId, b)) return; // not all down yet
        GrantIndestructibles(killer, steamId, "defeating every V-blood");
    }

    bool IsDiscoverableComplete(ulong steamId)
    {
        EnsureCatalog();
        foreach (int g in _discoverableGuids)
            if (!Core.PlayerUnlock.IsUnlocked(steamId, g)) return false;
        return true;
    }

    /// <summary>Bulk-grant the non-destructible placeables (those discovery can't reach). Idempotent —
    /// only grants/notifies the first time a trigger fires.</summary>
    void GrantIndestructibles(Entity killer, ulong steamId, string reason)
    {
        EnsureCatalog();
        int newly = 0;
        foreach (int g in _placeableGuids)
            if (!_discoverableGuids.Contains(g) && Core.PlayerUnlock.Unlock(steamId, g)) newly++;
        if (newly == 0) return; // already granted on a prior trigger
        Core.Log.LogInfo($"[Uriel SPAWN] player {steamId} unlocked {newly} non-destructible object(s) via {reason}.");
        if (Core.PlayerUnlock.WantsNotify(steamId, Settings.ObjectSpawn_DiscoveryNotify.Value))
            Notify(killer, $"Uriel: {reason} unlocked all {newly} remaining (non-destructible) objects to build!");
    }

    // V-blood roster for AllBosses (main bosses only — gate-fight duplicates excluded). Lazy.
    HashSet<int> _bossRoster;
    void EnsureBossRoster()
    {
        if (_bossRoster != null) return;
        var roster = new HashSet<int>();
        var guidMap = Core.PrefabCollectionSystem._PrefabLookupMap.GuidToEntityMap;
        foreach (var kvp in Core.PrefabCollectionSystem.SpawnableNameToPrefabGuidDictionary)
        {
            string name = kvp.Key.ToString();
            if (!name.StartsWith("CHAR_", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.IndexOf("VBlood", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (name.IndexOf("GateBoss", StringComparison.OrdinalIgnoreCase) >= 0) continue; // gate-fight duplicate
            if (!guidMap.TryGetValue(kvp.Value, out Entity p) || !p.Exists()) continue;
            if (!p.Has<VBloodConsumeSource>()) continue;
            roster.Add(kvp.Value._Value);
        }
        _bossRoster = roster;
        Core.Log.LogInfo($"[Uriel SPAWN] V-blood roster for AllBosses unlock: {_bossRoster.Count} main boss(es).");
    }

    /// <summary>Admin: bulk-grant a player the whole catalog or a subset
    /// (all | destructible | indestructible) — useful for testing and full-access gifting.</summary>
    public bool GrantAll(ulong steamId, string mode, out string message)
    {
        EnsureCatalog();
        mode = (mode ?? "all").Trim().ToLowerInvariant();
        var set = new List<int>();
        string label;
        switch (mode)
        {
            case "all":
                set.AddRange(_placeableGuids); label = "all"; break;
            case "destructible":
            case "discoverable":
                set.AddRange(_discoverableGuids); label = "destructible"; break;
            case "indestructible":
            case "nondestructible":
            case "non-destructible":
                foreach (int g in _placeableGuids) if (!_discoverableGuids.Contains(g)) set.Add(g);
                label = "non-destructible"; break;
            default:
                message = "Mode must be: all | destructible | indestructible."; return false;
        }
        int newly = 0;
        foreach (int g in set) if (Core.PlayerUnlock.Unlock(steamId, g)) newly++;
        Core.Log.LogInfo($"[Uriel SPAWN] grantall {label} to {steamId}: {newly} new of {set.Count}.");
        message = $"Granted {label} objects to {steamId}: {newly} newly unlocked ({set.Count} in that set).";
        return true;
    }

    // ---- boss-unlock map (admin) ----

    /// <summary>Resolve ANY prefab (units included) by GUID or name — the placeable catalog excludes
    /// units, so V-bloods need the full spawnable set.</summary>
    bool TryResolveAnyPrefab(string input, out int guid, out string name, out string error)
    {
        guid = 0; name = null; error = null;
        if (string.IsNullOrWhiteSpace(input)) { error = "Give a V-blood name or GUID."; return false; }
        input = input.Trim();
        if (int.TryParse(input, out int raw)) { guid = raw; name = new PrefabGUID(raw).GetPrefabName(); return true; }

        PrefabGUID exact = default; bool haveExact = false;
        var partial = new List<(string Name, int Guid)>();
        foreach (var kvp in Core.PrefabCollectionSystem.SpawnableNameToPrefabGuidDictionary)
        {
            string n = kvp.Key.ToString();
            if (string.Equals(n, input, StringComparison.OrdinalIgnoreCase)) { exact = kvp.Value; haveExact = true; break; }
            if (n.Contains(input, StringComparison.OrdinalIgnoreCase)) partial.Add((n, kvp.Value._Value));
        }
        if (haveExact) { guid = exact._Value; name = input; return true; }
        if (partial.Count == 1) { guid = partial[0].Guid; name = partial[0].Name; return true; }
        if (partial.Count == 0) { error = $"No prefab matches '{input}'."; return false; }
        error = $"'{input}' matches {partial.Count} prefabs — be more specific or use the GUID.";
        return false;
    }

    public bool BossMapAdd(string vbloodRef, string objectRef, out string message)
    {
        if (!TryResolveAnyPrefab(vbloodRef, out int vblood, out string vbloodName, out string err)) { message = err; return false; }
        if (!TryResolvePrefab(objectRef, out PrefabGUID obj, out _, out string objName, out string oerr)) { message = oerr; return false; }
        if (!_bossUnlocks.TryGetValue(vblood, out var listForBoss)) _bossUnlocks[vblood] = listForBoss = new List<int>();
        if (listForBoss.Contains(obj._Value)) { message = $"{objName} is already mapped to {vbloodName}."; return false; }
        listForBoss.Add(obj._Value);
        SaveBossMap();
        message = $"Boss map: defeating {vbloodName} now unlocks {objName} ({listForBoss.Count} object(s) total).";
        return true;
    }

    public bool BossMapRemove(string vbloodRef, string objectRef, out string message)
    {
        if (!TryResolveAnyPrefab(vbloodRef, out int vblood, out string vbloodName, out string err)) { message = err; return false; }
        int objGuid = int.TryParse(objectRef?.Trim(), out int raw) ? raw
                    : (TryResolvePrefab(objectRef, out PrefabGUID g, out _, out _, out _) ? g._Value : 0);
        if (!_bossUnlocks.TryGetValue(vblood, out var listForBoss) || !listForBoss.Remove(objGuid))
        { message = $"That object wasn't mapped to {vbloodName}."; return false; }
        if (listForBoss.Count == 0) _bossUnlocks.Remove(vblood);
        SaveBossMap();
        message = $"Boss map: removed an object from {vbloodName}.";
        return true;
    }

    public string DescribeBossMap()
    {
        if (_bossUnlocks.Count == 0) return "Boss-unlock map is empty. Add with '.uriel bossmap add <vblood> <object>'.";
        var sb = new StringBuilder($"Boss-unlock map ({_bossUnlocks.Count} boss(es)):");
        foreach (var kvp in _bossUnlocks)
        {
            string line = $"\n  {new PrefabGUID(kvp.Key).GetPrefabName()} -> {kvp.Value.Count} object(s)";
            if (sb.Length + line.Length > ReplyByteBudget - 40) { sb.Append("\n  ...(more)"); break; }
            sb.Append(line);
        }
        return Clamp(sb.ToString());
    }

    /// <summary>Admin: unlock a placeable object for a player (covers the non-destroyable objects
    /// discovery can't reach).</summary>
    public bool GrantUnlock(ulong steamId, string prefabRef, out string message)
    {
        if (!TryResolvePrefab(prefabRef, out PrefabGUID guid, out _, out string name, out string err))
        { message = err; return false; }
        bool added = Core.PlayerUnlock.Unlock(steamId, guid._Value);
        message = added ? $"Granted '{name}' to {steamId}." : $"{steamId} already had '{name}' unlocked.";
        return true;
    }

    /// <summary>Admin: remove an object from a player's unlocks.</summary>
    public bool RevokeUnlock(ulong steamId, string prefabRef, out string message)
    {
        if (!TryResolvePrefab(prefabRef, out PrefabGUID guid, out _, out string name, out string err))
        { message = err; return false; }
        bool removed = Core.PlayerUnlock.Revoke(steamId, guid._Value);
        message = removed ? $"Revoked '{name}' from {steamId}." : $"{steamId} did not have '{name}' unlocked.";
        return true;
    }

    /// <summary>List a player's unlocked objects with collection progress (byte-budgeted).</summary>
    public string DescribeUnlocks(ulong steamId)
    {
        EnsureCatalog();
        var guids = Core.PlayerUnlock.GetUnlocked(steamId);
        int discoverableTotal = _discoverableGuids.Count;
        int unlockedDiscoverable = 0;
        foreach (int g in guids) if (_discoverableGuids.Contains(g)) unlockedDiscoverable++;
        int pct = discoverableTotal > 0 ? (int)Math.Round(100.0 * unlockedDiscoverable / discoverableTotal) : 0;

        if (guids.Count == 0)
            return $"No objects unlocked yet — 0% of {discoverableTotal} discoverable. " +
                   "In Discovery mode, destroy world objects for a chance to unlock them.";

        var names = new List<string>();
        foreach (int g in guids) names.Add(new PrefabGUID(g).GetPrefabName());
        names.Sort(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder($"Unlocked {guids.Count} ({unlockedDiscoverable}/{discoverableTotal} discoverable = {pct}%):");
        foreach (var n in names)
        {
            string line = "\n  " + n;
            if (sb.Length + line.Length > ReplyByteBudget - 40) { sb.Append("\n  ...(more — .uriel unlocks shows all)"); break; }
            sb.Append(line);
        }
        return Clamp(sb.ToString());
    }

    /// <summary>Browse the full placeable-object catalog (paged) with totals — the "total
    /// prefab list" + collection denominators.</summary>
    public string BrowseCatalog(int page)
    {
        EnsureCatalog();
        const int pageSize = 6;
        int total = _placeable.Count;
        int pages = Math.Max(1, (total + pageSize - 1) / pageSize);
        page = Math.Clamp(page, 1, pages);
        var sb = new StringBuilder($"Catalog: {total} placeable ({_discoverableGuids.Count} discoverable), pg {page}/{pages}");
        for (int i = (page - 1) * pageSize; i < Math.Min(page * pageSize, total); i++)
        {
            var c = _placeable[i];
            string line = $"\n  {c.Name} ({c.Guid._Value})";
            if (sb.Length + line.Length > ReplyByteBudget - 40) break;
            sb.Append(line);
        }
        if (pages > 1 && page < pages) sb.Append($"\n(.uriel catalog {page + 1} -> more)");
        return Clamp(sb.ToString());
    }

    // ============================================================ admin blocklist

    public bool IsBlocked(int guid) => _blocked.Contains(guid);

    /// <summary>Admin: block a prefab (by GUID or name) so it can't be spawned, discovered, or
    /// granted. Excludes it from the catalog and refuses it at spawn (even by GUID).</summary>
    public bool Block(string prefabRef, out string message)
    {
        int guid; string name;
        if (int.TryParse(prefabRef?.Trim(), out int raw)) { guid = raw; name = new PrefabGUID(raw).GetPrefabName(); }
        else if (TryResolvePrefab(prefabRef, out PrefabGUID g, out _, out string n, out string err)) { guid = g._Value; name = n; }
        else { message = err; return false; }

        if (!_blocked.Add(guid)) { message = $"{name} ({guid}) is already blocked."; return false; }
        SaveBlocked();
        _placeable = null; // force a catalog rebuild without the blocked prefab
        Core.Log.LogInfo($"[Uriel SPAWN] blocked prefab {name} ({guid}).");
        message = $"Blocked {name} ({guid}) — it can no longer be spawned, discovered, or granted.";
        return true;
    }

    /// <summary>Admin: unblock a prefab (allow it again). GUID is most reliable — a blocked
    /// prefab's name no longer resolves through the (filtered) catalog.</summary>
    public bool Unblock(string prefabRef, out string message)
    {
        int guid = int.TryParse(prefabRef?.Trim(), out int raw) ? raw
                 : (TryResolvePrefab(prefabRef, out PrefabGUID g, out _, out _, out _) ? g._Value : 0);
        if (guid == 0) { message = "Give the blocked prefab's GUID (see '.uriel blocklist')."; return false; }
        if (!_blocked.Remove(guid)) { message = $"{guid} wasn't blocked."; return false; }
        SaveBlocked();
        _placeable = null;
        Core.Log.LogInfo($"[Uriel SPAWN] unblocked prefab {guid}.");
        message = $"Unblocked {new PrefabGUID(guid).GetPrefabName()} ({guid}).";
        return true;
    }

    public string DescribeBlocked()
    {
        if (_blocked.Count == 0) return "No prefabs are blocked.";
        var sb = new StringBuilder($"{_blocked.Count} blocked prefab(s):");
        foreach (int g in _blocked)
        {
            string line = $"\n  {new PrefabGUID(g).GetPrefabName()} ({g})";
            if (sb.Length + line.Length > ReplyByteBudget - 40) { sb.Append("\n  ...(more)"); break; }
            sb.Append(line);
        }
        return Clamp(sb.ToString());
    }

    // ============================================================ BCH wire API ([URIEL:*])

    /// <summary>`.uriel api version` — capabilities + catalog totals for BCH.</summary>
    public string ApiVersion(int apiVersion)
    {
        EnsureCatalog();
        return $"[URIEL:version] api={apiVersion} plugin={MyPluginInfo.PLUGIN_VERSION} ready=1 " +
               $"objectspawn={(Settings.ObjectSpawn_Enabled.Value ? 1 : 0)} adminonly={(Settings.ObjectSpawn_AdminOnly.Value ? 1 : 0)} " +
               $"collection={(Settings.ObjectSpawn_CollectionEnabled.Value ? 1 : 0)} mode={(IsDiscoveryMode() ? "Discovery" : "Full")} " +
               $"chance={Settings.ObjectSpawn_DiscoveryChancePercent.Value} total={_placeable.Count} discoverable={_discoverableGuids.Count} blocked={_blocked.Count}";
    }

    // 3 rows/page keeps each reply under the 512-byte VCF cap WITH the label=/cat= fields.
    const int ApiPageSize = 3;

    /// <summary>`.uriel api catalog &lt;page&gt;` — the total prefab list available in-game (paged).
    /// Row: [URIEL:object] guid= name= disc=0|1 label=&lt;humanized,wire-safe&gt; cat=&lt;category&gt;.</summary>
    public string ApiCatalogPage(int page)
    {
        EnsureCatalog();
        int total = _placeable.Count;
        int pages = Math.Max(1, (total + ApiPageSize - 1) / ApiPageSize);
        page = Math.Clamp(page, 1, pages);
        var sb = new StringBuilder($"[URIEL:catalog] page={page}/{pages} total={total} discoverable={_discoverableGuids.Count}");
        int count = 0;
        for (int i = (page - 1) * ApiPageSize; i < Math.Min(page * ApiPageSize, total); i++)
        {
            var c = _placeable[i];
            sb.Append($"\n[URIEL:object] guid={c.Guid._Value} disc={(_discoverableGuids.Contains(c.Guid._Value) ? 1 : 0)} label={c.Label} cat={c.Cat}");
            count++;
        }
        sb.Append($"\n[URIEL:end] cmd=catalog page={page}/{pages} count={count}");
        return Clamp(sb.ToString());
    }

    /// <summary>`.uriel api unlocked &lt;steamId&gt; &lt;page&gt;` — a player's unlocked prefabs + collection %.</summary>
    public string ApiUnlockedPage(ulong steamId, int page)
    {
        EnsureCatalog();
        var guids = new List<int>(Core.PlayerUnlock.GetUnlocked(steamId));
        guids.Sort();
        int discoverableTotal = _discoverableGuids.Count;
        int unlockedDiscoverable = 0;
        foreach (int g in guids) if (_discoverableGuids.Contains(g)) unlockedDiscoverable++;
        int pct = discoverableTotal > 0 ? (int)Math.Round(100.0 * unlockedDiscoverable / discoverableTotal) : 0;

        int total = guids.Count;
        int pages = Math.Max(1, (total + ApiPageSize - 1) / ApiPageSize);
        page = Math.Clamp(page, 1, pages);
        var sb = new StringBuilder($"[URIEL:unlocked] page={page}/{pages} steam={steamId} n={total} discoverable={discoverableTotal} pct={pct}");
        int count = 0;
        for (int i = (page - 1) * ApiPageSize; i < Math.Min(page * ApiPageSize, total); i++)
        {
            int g = guids[i];
            string label, cat; int disc;
            if (_byGuid.TryGetValue(g, out var ce)) { label = ce.Label; cat = ce.Cat; disc = _discoverableGuids.Contains(g) ? 1 : 0; }
            else { label = SafeToken(Humanize(new PrefabGUID(g).GetPrefabName())); cat = "other"; disc = 0; }
            sb.Append($"\n[URIEL:object] guid={g} disc={disc} label={label} cat={cat}");
            count++;
        }
        sb.Append($"\n[URIEL:end] cmd=unlocked page={page}/{pages} count={count}");
        return Clamp(sb.ToString());
    }

    // ---- inventory cost helpers (charge the player's OWN inventory; castle shared-stash is a future enhancement) ----

    static bool HasInventoryItems(Entity character, int itemGuid, int amount)
    {
        if (!InventoryUtilities.TryGetInventoryEntity(Core.EntityManager, character, out Entity inv)) return false;
        return Core.ServerGameManager.GetInventoryItemCount(inv, new PrefabGUID(itemGuid)) >= amount;
    }

    static bool ChargeInventory(Entity character, int itemGuid, int amount)
    {
        if (!InventoryUtilities.TryGetInventoryEntity(Core.EntityManager, character, out Entity inv)) return false;
        return Core.ServerGameManager.TryRemoveInventoryItem(inv, new PrefabGUID(itemGuid), amount);
    }

    static void RefundInventory(Entity character, int itemGuid, int amount)
    {
        try { Core.ServerGameManager.TryAddInventoryItem(character, new PrefabGUID(itemGuid), amount); }
        catch (Exception ex) { Core.Log.LogWarning($"[Uriel SPAWN] refund failed: {ex.Message}"); }
    }

    static void Notify(Entity character, string text)
    {
        try
        {
            if (!character.TryGetComponent<PlayerCharacter>(out var pc)) return;
            if (!pc.UserEntity.TryGetComponent<ProjectM.Network.User>(out var user)) return;
            var msg = new FixedString512Bytes(text.Length > 500 ? text.Substring(0, 500) : text);
            ServerChatUtils.SendSystemMessageToClient(Core.EntityManager, user, ref msg);
        }
        catch (Exception ex) { Core.Log.LogWarning($"[Uriel SPAWN] notify failed: {ex.Message}"); }
    }

    // NOTE: not "Dummy" — that would wrongly drop legit TM_*_TargetDummy_* decor (training dummies).
    static readonly string[] DebugMarkers = { "Debug", "Benchmark", "ArtQuality", "Placeholder", "DELETE", "_TBD" };
    static bool IsDebugPrefab(string name)
    {
        foreach (var m in DebugMarkers)
            if (name.Contains(m, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>A Chain_* spawn-chain controller (SpawnChainData) — NOT a real object.</summary>
    static bool IsSpawnChainController(Entity prefab) =>
        prefab.Has<SpawnChainData.SpawnChainInstance>() || prefab.Has<SpawnChainData.SpawnChainConstants>();

    /// <summary>A prefab a player could place: tile model (TilePosition), castle buildable
    /// (EditableTileModel), or castle-owned object (CastleHeartConnection).</summary>
    static bool IsPlaceableObject(Entity prefab) =>
        prefab.Has<TilePosition>() || prefab.Has<EditableTileModel>() || prefab.Has<CastleHeartConnection>();

    /// <summary>The longest non-numeric token of a prefab name — a search hint for the real object.</summary>
    static string DiscoveryHint(string name)
    {
        string best = name; int bestLen = 0;
        foreach (var tok in name.Split('_'))
            if (tok.Length > bestLen && !int.TryParse(tok, out _)) { best = tok; bestLen = tok.Length; }
        return best;
    }

    // ---- BCH display metadata (label + category) ----

    /// <summary>Light humanization for a display label: drop the family prefix (TM_/DT_/BP_…),
    /// underscores->spaces, and split letter↔digit (Table04 -> Table 04). Not localized — BCH can
    /// resolve true localized names client-side by GUID; this is a convenience fallback.</summary>
    static string Humanize(string name)
    {
        int us = name.IndexOf('_');
        string s = (us >= 1 && us <= 3) ? name.Substring(us + 1) : name;
        var sb = new StringBuilder(s.Length + 8);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '_') { sb.Append(' '); continue; }
            if (i > 0 && char.IsDigit(c) && char.IsLetter(s[i - 1])) sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>Wire-safe token for the space-separated [URIEL:*] format (spaces -> _; empty -> '-').</summary>
    static string SafeToken(string s) => string.IsNullOrEmpty(s) ? "-" : s.Replace(' ', '_');

    /// <summary>Coarse category tag for BCH grouping + fallback icons (parsers treat unknown as 'decor').</summary>
    static string Categorize(string name, Entity prefab)
    {
        string n = name.ToLowerInvariant();
        if (prefab.Has<BlueprintData>()) return "buildable";
        if (n.Contains("chest") || n.Contains("container") || n.Contains("stash") || prefab.Has<InventoryOwner>()) return "container";
        if (n.Contains("tree") || n.Contains("plant") || n.Contains("flower") || n.Contains("bush") || n.Contains("shrub") || n.Contains("mushroom")) return "plant";
        if (n.Contains("rock") || n.Contains("ore") || n.Contains("vein") || n.Contains("stone") || n.Contains("mineral") || n.Contains("crystal") || n.Contains("gem")) return "ore";
        if (n.Contains("crate") || n.Contains("barrel") || n.Contains("urn") || n.Contains("vase") || n.Contains("pot") || n.Contains("sack") || n.Contains("box") || n.Contains("breakable")) return "breakable";
        if (n.Contains("chandelier") || n.Contains("candle") || n.Contains("lamp") || n.Contains("torch") || n.Contains("brazier") || n.Contains("lantern") || n.Contains("light")) return "light";
        if (n.Contains("bench") || n.Contains("chair") || n.Contains("table") || n.Contains("stool") || n.Contains("shelf") || n.Contains("desk") || n.Contains("cabinet") || n.Contains("bed")) return "furniture";
        if (prefab.Has<YieldResourcesOnDamageTaken>()) return "resource";
        return "decor";
    }

    // ------------------------------------------------------------ prefab resolve

    bool TryResolvePrefab(string input, out PrefabGUID guid, out Entity prefab, out string resolvedName, out string error)
    {
        guid = default;
        prefab = Entity.Null;
        resolvedName = null;
        error = null;
        if (string.IsNullOrWhiteSpace(input)) { error = "Give a prefab name or GUID. Try '.uriel findprefab <text>'."; return false; }
        input = input.Trim();

        // Numeric GUID — resolve ANY prefab (placeability/chain checks happen at spawn).
        if (int.TryParse(input, out int raw))
        {
            var g = new PrefabGUID(raw);
            if (!Core.PrefabCollectionSystem._PrefabLookupMap.TryGetValue(g, out Entity e) || !e.Exists())
            {
                error = $"No prefab with GUID {raw}.";
                return false;
            }
            guid = g;
            prefab = e;
            resolvedName = g.GetPrefabName();
            return true;
        }

        // Name — match against the placeable-object catalog (exact, then unique prefix, then unique fragment).
        EnsureCatalog();
        CatalogEntry exact = default; bool haveExact = false;
        var partial = new List<CatalogEntry>();
        foreach (var c in _placeable)
        {
            if (string.Equals(c.Name, input, StringComparison.OrdinalIgnoreCase)) { exact = c; haveExact = true; break; }
            if (c.Name.Contains(input, StringComparison.OrdinalIgnoreCase)) partial.Add(c);
        }

        CatalogEntry chosen;
        if (haveExact) chosen = exact;
        else if (partial.Count == 1) chosen = partial[0];
        else if (partial.Count == 0) { error = $"No placeable object matches '{input}'. Try '.uriel findprefab {input}'."; return false; }
        else
        {
            var prefixHits = partial.FindAll(c => c.Name.StartsWith(input, StringComparison.OrdinalIgnoreCase));
            if (prefixHits.Count == 1) chosen = prefixHits[0];
            else
            {
                partial.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                var sb = new StringBuilder($"'{input}' matches {partial.Count} objects — be more specific:");
                for (int i = 0; i < Math.Min(5, partial.Count); i++) sb.Append($"\n  {partial[i].Name}");
                if (partial.Count > 5) sb.Append($"\n  ...and {partial.Count - 5} more ('.uriel findprefab {input}').");
                error = Clamp(sb.ToString());
                return false;
            }
        }

        guid = chosen.Guid;
        resolvedName = chosen.Name;
        if (!Core.PrefabCollectionSystem._PrefabLookupMap.TryGetValue(guid, out prefab) || !prefab.Exists())
        {
            error = $"'{resolvedName}' did not resolve to a live prefab.";
            return false;
        }
        return true;
    }

    // VCF replies are FixedString512Bytes — keep every reply well under 512 bytes.
    const int ReplyByteBudget = 480;

    /// <summary>`.uriel findprefab` — page through REAL placeable objects by fragment, ranked
    /// exact > prefix > substring. Short fragments are rejected (they match noise like 'urn'->'Return').</summary>
    public string FindPrefabs(string fragment, int page = 1)
    {
        if (string.IsNullOrWhiteSpace(fragment)) return "Usage: .uriel findprefab <text> [page]";
        fragment = fragment.Trim();
        if (fragment.Length < 3) return "Search needs at least 3 characters (shorter fragments match too much noise).";
        EnsureCatalog();
        const int pageSize = 6;
        var matches = new List<(int Rank, string Name, int Guid)>();
        foreach (var c in _placeable)
        {
            int idx = c.Name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            int rank = string.Equals(c.Name, fragment, StringComparison.OrdinalIgnoreCase) ? 0 : idx == 0 ? 1 : 2;
            matches.Add((rank, c.Name, c.Guid._Value));
        }
        if (matches.Count == 0) return $"No placeable object matches '{fragment}'.";
        matches.Sort((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        int pages = (matches.Count + pageSize - 1) / pageSize;
        page = Math.Clamp(page, 1, pages);
        var sb = new StringBuilder($"'{fragment}': {matches.Count} object(s), pg {page}/{pages}");
        for (int i = (page - 1) * pageSize; i < Math.Min(page * pageSize, matches.Count); i++)
        {
            string line = $"\n  {matches[i].Name} ({matches[i].Guid})";
            if (sb.Length + line.Length > ReplyByteBudget - 40) break;
            sb.Append(line);
        }
        if (pages > 1 && page < pages) sb.Append($"\n(.uriel findprefab {fragment} {page + 1} -> more)");
        return Clamp(sb.ToString());
    }

    static string Clamp(string s) =>
        s.Length <= ReplyByteBudget ? s : s.Substring(0, ReplyByteBudget - 3) + "...";

    // ============================================================ spawn

    /// <summary>
    /// Spawn <paramref name="prefabRef"/> at the player's aim point (or feet), INSIDE the
    /// castle plot at that spot. Players may only place in a plot they own; admins, any plot;
    /// open world is refused. <paramref name="rotation"/> is tile rotation 0–3.
    /// </summary>
    public bool Spawn(Entity character, bool isAdmin, string prefabRef, int rotation, bool? breakable, out string message)
    {
        if (!TryResolvePrefab(prefabRef, out PrefabGUID guid, out Entity prefab, out string name, out string err))
        {
            message = err;
            return false;
        }

        if (IsSpawnChainController(prefab))
        {
            message = Clamp($"{name} is a spawn-chain controller, not a placeable object — Uriel can't make it " +
                            $"indestructible or stable (edits hit the invisible controller, not what you see). " +
                            $"Spawn the real object instead: '.uriel findprefab {DiscoveryHint(name)}'.");
            return false;
        }

        if (_blocked.Contains(guid._Value))
        {
            message = $"{name} ({guid._Value}) is blocked by an admin and can't be spawned.";
            return false;
        }

        // Position: aim point, falling back to the player's feet.
        float3 pos;
        if (character.TryGetComponent<EntityAimData>(out var aim) && !aim.AimPosition.Equals(default(float3)))
            pos = aim.AimPosition;
        else if (PublicStorageService.TryGetCharacterPosition(character, out var cp))
            pos = cp;
        else { message = "Could not read your position."; return false; }

        // PLACEMENT GATE: inside a castle plot (everyone); owned by the caller (non-admins).
        if (!CheckPlacement(character, isAdmin, pos, "placed", out Entity heart, out int territory, out string gateErr))
        {
            message = gateErr;
            return false;
        }

        // PLAYER GATES (non-admins): Discovery access + affordability. Admins bypass both.
        int costItem = 0, costAmount = 0;
        if (!isAdmin)
        {
            ulong steamId = character.GetSteamId();
            if (IsDiscoveryMode() && !Core.PlayerUnlock.IsUnlocked(steamId, guid._Value))
            {
                message = IsDiscoverableGuid(guid._Value)
                    ? $"You haven't discovered {name} yet — destroy one in the world for a chance to unlock it."
                    : $"You haven't unlocked {name}, and it isn't destroyable in the world — ask an admin to grant it ('.uriel grant').";
                return false;
            }
            costItem = Settings.ObjectSpawn_PrefabCostItem.Value;
            costAmount = Settings.ObjectSpawn_PrefabCostStack.Value;
            if (costItem != 0 && costAmount > 0 && !HasInventoryItems(character, costItem, costAmount))
            {
                message = $"Building {name} costs {costAmount}x {new PrefabGUID(costItem).GetPrefabName()} — you don't have enough in your inventory.";
                return false;
            }
        }

        bool indestructible = !(breakable ?? !Settings.ObjectSpawn_Indestructible.Value);

        try
        {
            Entity e = ExecuteSpawn(prefab, pos, rotation & 3, indestructible, heart, out string adoptNote);
            if (e == Entity.Null) { message = $"Failed to spawn {name} (prefab did not resolve)."; return false; }

            // Charge the player only AFTER a successful spawn (affordability was checked above).
            int paidItem = 0, paidAmount = 0;
            if (!isAdmin && costItem != 0 && costAmount > 0)
            {
                if (ChargeInventory(character, costItem, costAmount)) { paidItem = costItem; paidAmount = costAmount; }
                else
                {
                    DestroyUtility.Destroy(Core.EntityManager, e); // undo to avoid a free build
                    message = "Payment failed — nothing was built.";
                    return false;
                }
            }

            _spawned.Add(e);
            PruneSpawned();
            RegisterRecord(e, indestructible, territory, heart, character.GetSteamId(), paidItem, paidAmount);
            Core.Log.LogInfo($"[Uriel SPAWN] {name}({guid._Value}) at ({pos.x:F1},{pos.y:F1},{pos.z:F1}) rot={rotation & 3} immortal={indestructible} territory={territory} cost={paidAmount}x{paidItem}. {adoptNote}");
            string costNote = paidAmount > 0 ? $" Paid {paidAmount}x {new PrefabGUID(paidItem).GetPrefabName()}." : "";
            message = $"Spawned {name} ({(indestructible ? "indestructible" : "breakable")}, rot {rotation & 3}).{costNote} {adoptNote} " +
                      "Manage it with '.uriel move' / '.uriel rotate' / '.uriel despawn'.";
            return true;
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[Uriel SPAWN] spawn of {name} failed: {ex}");
            message = "Spawn failed unexpectedly — check the server log.";
            return false;
        }
    }

    Entity ExecuteSpawn(Entity prefab, float3 pos, int rot, bool indestructible, Entity heart, out string adoptNote)
    {
        adoptNote = "";
        if (!prefab.Exists())
            return Entity.Null;

        Entity e = Core.EntityManager.Instantiate(prefab);
        if (e.Has<Disabled>()) Core.EntityManager.RemoveComponent<Disabled>(e);

        ApplyTransform(e, pos, rot);

        // Adopt into the resolved castle heart (copy team/owner from the heart). World objects
        // often lack CastleHeartConnection/UserOwner — ADD them so the object is castle-owned
        // (decay immunity + ownership). NOTE: this does NOT make the object build-menu editable —
        // vanilla selection needs the heart to REGISTER the piece via the placement pipeline,
        // which component edits can't reproduce (confirmed live). World objects are managed by
        // '.uriel move'/'.uriel rotate'/'.uriel despawn' instead.
        if (heart.Exists())
        {
            if (!e.Has<CastleHeartConnection>()) Core.EntityManager.AddComponent<CastleHeartConnection>(e);
            e.With((ref CastleHeartConnection c) => c.CastleHeartEntity = heart);
            if (heart.TryGetComponent<Team>(out var team)) e.AddOrSet(team);
            if (heart.TryGetComponent<TeamReference>(out var teamRef)) e.AddOrSet(teamRef);
            if (heart.TryGetComponent<UserOwner>(out var owner)) e.AddOrSet(owner);
            adoptNote = "Adopted into the castle.";
        }

        // Indestructibility: Immortal + decay-proof, or explicitly breakable. (On a REAL object
        // — not a chain — Immortal holds, confirmed live on TM_GloomRot_Laboratory_Table04.)
        if (indestructible)
        {
            e.AddOrSet(new Immortal { IsImmortal = true });
            if (e.Has<CastleDecayAndRegen>()) e.With((ref CastleDecayAndRegen d) => d.CanDieFromDecay = false);
            else e.AddOrSet(new CastleDecayAndRegen { CanDieFromDecay = false });
        }
        else if (e.Has<Immortal>())
        {
            e.With((ref Immortal im) => im.IsImmortal = false);
        }

        PublicStorageService.ForceResync(e);
        return e;
    }

    /// <summary>Write position + tile rotation onto an object. World→tile is
    /// ConvertPosToTileGrid; height stays in Translation.y. Shared by spawn and respawn.</summary>
    static void ApplyTransform(Entity e, float3 pos, int rot)
    {
        rot &= 3;
        var tileRot = (TileRotation)rot;
        quaternion q = quaternion.RotateY(math.radians(90f * rot));
        int2 tile = new int2((int)math.floor(pos.x * 2) + TileGridOffset, (int)math.floor(pos.z * 2) + TileGridOffset);

        e.With((ref Translation t) => t.Value = pos);
        if (e.Has<Rotation>()) e.With((ref Rotation r) => r.Value = q);
        if (e.Has<TilePosition>())
            e.With((ref TilePosition tp) => { tp.Tile = tile; tp.TileRotation = tileRot; tp.CompressedHeight = 0; });
        if (e.Has<TileBounds>())
            e.With((ref TileBounds tb) => tb.Value = new BoundsMinMax { Min = tile, Max = tile });
        if (e.Has<StaticTransformCompatible>())
            e.With((ref StaticTransformCompatible s) =>
            {
                s.UseStaticTransform = false;
                s.NonStaticTransform_Pos = new float2(pos.x, pos.z);
                s.NonStaticTransform_Height = pos.y;
                s.NonStaticTransform_Rotation = tileRot;
            });
    }

    // ============================================================ remove / move / rotate

    /// <summary>Remove the nearest spawned object (cross-session via the registry). Non-admins
    /// may only remove objects in a plot they own.</summary>
    public bool Despawn(Entity character, bool isAdmin, out string message)
    {
        if (!TryGetTargetPosition(character, out float3 pos)) { message = "Could not read your position."; return false; }
        Entity target = NearestSpawned(pos, Settings.ObjectSpawn_MaxTargetDistance.Value);
        if (target == Entity.Null)
        {
            message = $"No Uriel-spawned object within {Settings.ObjectSpawn_MaxTargetDistance.Value:F0}m. " +
                      "(despawn/move/rotate target objects spawned by Uriel; '.uriel purgeplot' clears a whole plot.)";
            return false;
        }
        if (!isAdmin && !CallerOwnsObject(character, target))
        { message = "You can only remove objects in your own castle plot."; return false; }

        string name = target.GetPrefabGuid().GetPrefabName();
        var rec = FindRecordFor(target);

        // Optional refund: give the spawner back what they paid (their own object only).
        string refundNote = "";
        if (rec != null && Settings.ObjectSpawn_RefundOnRemove.Value
            && rec.PaidCostItem != 0 && rec.PaidCostAmount > 0
            && rec.SpawnedBySteamId == character.GetSteamId())
        {
            RefundInventory(character, rec.PaidCostItem, rec.PaidCostAmount);
            refundNote = $" Refunded {rec.PaidCostAmount}x {new PrefabGUID(rec.PaidCostItem).GetPrefabName()}.";
        }
        if (rec != null) { _records.Remove(rec); SaveSync(); }

        DestroyUtility.Destroy(Core.EntityManager, target);
        _spawned.Remove(target);
        Core.Log.LogInfo($"[Uriel SPAWN] despawned {name}.");
        message = $"Removed {name}.{refundNote}";
        return true;
    }

    /// <summary>Move the nearest spawned object to your aim point (respawn-based — these objects
    /// render from baked static batches; an in-place edit risks the stair invisible-until-restart
    /// bug). Destination must be inside a plot you own (admins: any plot).</summary>
    public bool Move(Entity character, bool isAdmin, out string message)
    {
        if (!PublicStorageService.TryGetCharacterPosition(character, out float3 feet))
        { message = "Could not read your position."; return false; }
        Entity target = NearestSpawned(feet, Settings.ObjectSpawn_MaxTargetDistance.Value);
        if (target == Entity.Null)
        {
            message = $"No Uriel-spawned object within {Settings.ObjectSpawn_MaxTargetDistance.Value:F0}m to move. " +
                      "(Stand near it.)";
            return false;
        }
        if (!(character.TryGetComponent<EntityAimData>(out var aim) && !aim.AimPosition.Equals(default(float3))))
        { message = "Aim where you want it, then run '.uriel move'."; return false; }

        if (!CheckPlacement(character, isAdmin, aim.AimPosition, "moved", out Entity heart, out int territory, out string gateErr))
        { message = gateErr; return false; }

        string name = target.GetPrefabGuid().GetPrefabName();
        if (!RespawnAt(target, aim.AimPosition, CurrentRot(target), heart, territory, out _, out string err)) { message = err; return false; }
        Core.Log.LogInfo($"[Uriel SPAWN] moved {name} to ({aim.AimPosition.x:F1},{aim.AimPosition.y:F1},{aim.AimPosition.z:F1}).");
        message = $"Moved {name} to your aim point.";
        return true;
    }

    /// <summary>Rotate the nearest spawned object. No arg turns it 90°; 0-3 sets that tile
    /// rotation. Position preserved; respawn-based. Owned-plot only for non-admins.</summary>
    public bool Rotate(Entity character, bool isAdmin, int? rotation, out string message)
    {
        if (!TryGetTargetPosition(character, out float3 pos)) { message = "Could not read your position."; return false; }
        Entity target = NearestSpawned(pos, Settings.ObjectSpawn_MaxTargetDistance.Value);
        if (target == Entity.Null)
        { message = $"No Uriel-spawned object within {Settings.ObjectSpawn_MaxTargetDistance.Value:F0}m to rotate."; return false; }
        if (!target.TryGetComponent<Translation>(out var t)) { message = "Object has no position to preserve."; return false; }

        if (!CheckPlacement(character, isAdmin, t.Value, "moved", out Entity heart, out int territory, out string gateErr))
        { message = gateErr; return false; }

        int newRot = (rotation ?? CurrentRot(target) + 1) & 3;
        string name = target.GetPrefabGuid().GetPrefabName();
        if (!RespawnAt(target, t.Value, newRot, heart, territory, out _, out string err)) { message = err; return false; }
        Core.Log.LogInfo($"[Uriel SPAWN] rotated {name} to rot {newRot}.");
        message = $"Rotated {name} to rotation {newRot}.";
        return true;
    }

    /// <summary>Destroy a spawned object and re-spawn the same prefab at a new transform,
    /// preserving its indestructible state, re-adopting into <paramref name="heart"/>, and
    /// keeping the registry + live cache in sync.</summary>
    bool RespawnAt(Entity target, float3 pos, int rot, Entity heart, int territory, out Entity result, out string error)
    {
        result = Entity.Null;
        error = null;
        PrefabGUID guid = target.GetPrefabGuid();
        var guidMap = Core.PrefabCollectionSystem._PrefabLookupMap.GuidToEntityMap;
        if (!guidMap.TryGetValue(guid, out Entity prefab) || !prefab.Exists())
        { error = "Could not re-resolve the object's prefab."; return false; }

        bool indestructible = target.TryGetComponent<Immortal>(out var im) && im.IsImmortal;
        var old = FindRecordFor(target);
        ulong by = old?.SpawnedBySteamId ?? 0;
        int paidItem = old?.PaidCostItem ?? 0;       // a move/rotate re-spawns — never re-charge
        int paidAmount = old?.PaidCostAmount ?? 0;

        try
        {
            if (old != null) _records.Remove(old);
            DestroyUtility.Destroy(Core.EntityManager, target);
            _spawned.Remove(target);
            Entity e = ExecuteSpawn(prefab, pos, rot, indestructible, heart, out _);
            if (e == Entity.Null) { error = "Re-spawn failed (prefab did not resolve)."; SaveSync(); return false; }
            _spawned.Add(e);
            PruneSpawned();
            RegisterRecord(e, indestructible, territory, heart, by, paidItem, paidAmount);
            result = e;
            return true;
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[Uriel SPAWN] move/rotate respawn failed: {ex}");
            error = "Move/rotate failed unexpectedly — check the server log.";
            return false;
        }
    }

    static int CurrentRot(Entity e) =>
        e.TryGetComponent<TilePosition>(out var tp) ? (int)tp.TileRotation & 3 : 0;

    // ============================================================ plot management (admin)

    /// <summary>List the Uriel-spawned objects on the plot the caller is standing in.</summary>
    public string ListOnPlot(Entity character)
    {
        if (!PublicStorageService.TryGetCharacterPosition(character, out var pos)) return "Could not read your position.";
        if (!TryResolvePlot(pos, out _, out int territory)) return "You're not standing in a castle plot.";
        var onPlot = _records.FindAll(r => r.TerritoryIndex == territory);
        if (onPlot.Count == 0) return "No Uriel-spawned objects on this plot.";

        var counts = new Dictionary<string, int>();
        foreach (var r in onPlot)
        {
            string n = new PrefabGUID(r.PrefabGuid).GetPrefabName();
            counts[n] = counts.TryGetValue(n, out int c) ? c + 1 : 1;
        }
        var sb = new StringBuilder($"{onPlot.Count} Uriel object(s) on this plot:");
        foreach (var kvp in counts)
        {
            string line = $"\n  {kvp.Value}x {kvp.Key}";
            if (sb.Length + line.Length > ReplyByteBudget - 60) { sb.Append("\n  ...(more)"); break; }
            sb.Append(line);
        }
        sb.Append("\n'.uriel purgeplot' clears them all.");
        return Clamp(sb.ToString());
    }

    /// <summary>Remove EVERY Uriel-spawned object on the plot the caller is standing in.</summary>
    public bool PurgePlot(Entity character, out string message)
    {
        if (!PublicStorageService.TryGetCharacterPosition(character, out var pos))
        { message = "Could not read your position."; return false; }
        if (!TryResolvePlot(pos, out _, out int territory))
        { message = "You're not standing in a castle plot."; return false; }

        int removed = 0;
        foreach (var r in new List<SpawnRecord>(_records))
        {
            if (r.TerritoryIndex != territory) continue;
            Entity e = ResolveRecord(r);
            if (e != Entity.Null)
            {
                DestroyUtility.Destroy(Core.EntityManager, e);
                _spawned.Remove(e);
            }
            _records.Remove(r);
            removed++;
        }
        SaveSync();
        Core.Log.LogInfo($"[Uriel SPAWN] purged {removed} object(s) from territory {territory}.");
        message = removed == 0
            ? "No Uriel-spawned objects on this plot to purge."
            : $"Purged {removed} Uriel-spawned object(s) from this plot.";
        return true;
    }

    // ============================================================ info / helpers

    public string DescribeNearest(Entity character)
    {
        if (!TryGetTargetPosition(character, out float3 pos)) return "Could not read your position.";
        PruneSpawned();
        Entity target = NearestSpawned(pos, Settings.ObjectSpawn_MaxTargetDistance.Value);
        if (target == Entity.Null)
            return $"No Uriel-spawned object within {Settings.ObjectSpawn_MaxTargetDistance.Value:F0}m. ({_spawned.Count} tracked this session, {_records.Count} persisted.)";
        var sb = new StringBuilder($"{target.GetPrefabGuid().GetPrefabName()} ({target.GetPrefabGuid()._Value})");
        if (target.TryGetComponent<Immortal>(out var im)) sb.Append($" | immortal={im.IsImmortal}");
        if (target.TryGetComponent<CastleDecayAndRegen>(out var d)) sb.Append($" | canDecay={d.CanDieFromDecay}");
        if (target.TryGetComponent<CastleHeartConnection>(out var c))
            sb.Append($" | heart={(c.CastleHeartEntity.GetEntityOnServer().Exists() ? "owned" : "unowned")}");
        var rec = FindRecordFor(target);
        if (rec != null) sb.Append($" | plot={rec.TerritoryIndex}");
        return Clamp(sb.ToString());
    }

    /// <summary>Does the caller own the plot the object sits in? (used to gate non-admin removal)</summary>
    bool CallerOwnsObject(Entity character, Entity obj)
    {
        if (!obj.TryGetComponent<Translation>(out var t)) return false;
        if (!TryResolvePlot(t.Value, out Entity heart, out _)) return false;
        return OwnsHeart(character, heart);
    }

    static bool TryGetTargetPosition(Entity character, out float3 pos)
    {
        if (character.TryGetComponent<EntityAimData>(out var aim) && !aim.AimPosition.Equals(default(float3)))
        {
            pos = aim.AimPosition;
            return true;
        }
        return PublicStorageService.TryGetCharacterPosition(character, out pos);
    }

    Entity NearestSpawned(float3 pos, float maxDist)
    {
        PruneSpawned();
        float bestSq = maxDist * maxDist;
        Entity best = Entity.Null;
        foreach (var e in _spawned)
        {
            if (!e.TryGetComponent<Translation>(out var t)) continue;
            float dx = t.Value.x - pos.x, dy = t.Value.y - pos.y, dz = t.Value.z - pos.z;
            float dsq = dx * dx + dy * dy + dz * dz;
            if (dsq < bestSq) { bestSq = dsq; best = e; }
        }
        return best;
    }

    void PruneSpawned() => _spawned.RemoveAll(e => !e.Exists());
}
