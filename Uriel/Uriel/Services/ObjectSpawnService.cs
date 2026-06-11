using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Network;
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

    // ============================================================ persistence registry

    internal sealed class SpawnRecord
    {
        public int PrefabGuid { get; set; }
        public int TileX { get; set; }
        public int TileY { get; set; }
        // World position at spawn (schema v2). A save/load round-trip can re-quantize an object's
        // TilePosition.Tile slightly; this lets LiveIndex.Resolve fall back to nearest-by-position
        // (same prefab, within ~2m) so an object stays recognizable as Uriel-spawned even if its
        // tile drifts. Backfilled for old records on the first boot that resolves them.
        public float PosX { get; set; }
        public float PosY { get; set; }
        public float PosZ { get; set; }
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
        // Schema v3: tile rotation (so a destroyed object can be re-spawned the same way), auto-respawn,
        // and player-breakable mode (the object skipped castle adoption so the OWNER can destroy it).
        // Old records load these as 0/false — correct (no rotation stored, no respawn, normal adoption).
        public int Rot { get; set; }
        public bool RespawnOnDestroy { get; set; }
        public bool PlayerBreakable { get; set; }
    }

    sealed class SaveFile
    {
        public int SchemaVersion { get; set; } = 3;
        public List<SpawnRecord> Objects { get; set; } = new();
    }

    readonly List<SpawnRecord> _records = new();
    // Live spawn marker (in-session): the set of entities Uriel has spawned this run. Populated on every
    // spawn/respawn (RegisterRecord) and refreshed from the persistent registry on boot (ReapplySpawned),
    // cleared per-entity on DestroySpawned. It is the LIGHT-purge identifier — a direct "this entity is
    // ours" check with no tile-resolution and zero risk of matching a native object. NOT a persisted ECS
    // component on purpose: V Rising's save drops mod-added components on restart (Bloodcraft & co. keep
    // per-entity state in external JSON for the same reason), so the cross-restart source of truth stays
    // the JSON registry (`_records`) — which the STRONG purge resolves. Entity equality includes Version,
    // so a recycled entity slot never false-matches a stale handle left in this set.
    readonly HashSet<Entity> _liveSpawns = new();
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
                        int paidItem = 0, int paidAmount = 0,
                        int rot = 0, bool respawnOnDestroy = false, bool playerBreakable = false)
    {
        _liveSpawns.Add(e); // mark as ours even if it ends up session-only (no TilePosition below)
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
            Rot = rot & 3,
            RespawnOnDestroy = respawnOnDestroy,
            PlayerBreakable = playerBreakable,
        };
        if (e.TryGetComponent<Translation>(out var tr))
        {
            rec.PosX = tr.Value.x;
            rec.PosY = tr.Value.y;
            rec.PosZ = tr.Value.z;
        }
        if (heart.Exists() && heart.TryGetComponent<TilePosition>(out var ht))
        {
            rec.HasHeart = true;
            rec.HeartTileX = ht.Tile.x;
            rec.HeartTileY = ht.Tile.y;
        }
        _records.Add(rec);
        SaveSync();
    }

    // ---- live-entity resolution (on demand, registry-driven — no stale entity cache) ----
    //
    // The engine recreates a castle object's entity (new Entity handle) whenever the castle streams
    // out and back in: a player relogging, leaving and returning to the territory, or a server
    // restart all do it. A cached Entity handle therefore goes stale and stops resolving, which is
    // why management used to "lose" objects across a relog/restart. Instead we keep ONLY the JSON
    // registry as the source of truth and re-resolve each record to its CURRENT live entity on
    // demand — by (prefab GUID + tile), with a world-position fallback for tile drift.

    /// <summary>One-shot index of every placed tile object, used to re-resolve registry records to
    /// their live entities. Built fresh per command (spawns/edits are infrequent). Disabled-included
    /// (castle objects sit Disabled when no player is near); Prefab entities are excluded by the
    /// query (no IncludePrefab), so a record never resolves to a template.</summary>
    sealed class LiveIndex
    {
        public readonly Dictionary<(int, int, int), Entity> ByTile = new();
        public readonly Dictionary<int, List<Entity>> ByGuid = new();

        public Entity Resolve(SpawnRecord r)
        {
            if (ByTile.TryGetValue((r.PrefabGuid, r.TileX, r.TileY), out var e) && e.Exists())
                return e;
            // Position fallback — the tile re-quantized across a save/load, but the same prefab is
            // still sitting at (about) the recorded world position. Keeps the object recognizable.
            if ((r.PosX != 0f || r.PosY != 0f || r.PosZ != 0f)
                && ByGuid.TryGetValue(r.PrefabGuid, out var sameGuid))
            {
                Entity best = Entity.Null;
                float bestSq = 4f; // within 2m
                foreach (var c in sameGuid)
                {
                    if (!c.TryGetComponent<Translation>(out var t)) continue;
                    float dx = t.Value.x - r.PosX, dy = t.Value.y - r.PosY, dz = t.Value.z - r.PosZ;
                    float dsq = dx * dx + dy * dy + dz * dz;
                    if (dsq < bestSq) { bestSq = dsq; best = c; }
                }
                if (best != Entity.Null) return best;
            }
            return Entity.Null;
        }
    }

    LiveIndex BuildLiveIndex()
    {
        var index = new LiveIndex();
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
                index.ByTile[(g._Value, tp.Tile.x, tp.Tile.y)] = e;
                if (!index.ByGuid.TryGetValue(g._Value, out var list))
                    index.ByGuid[g._Value] = list = new List<Entity>();
                list.Add(e);
            }
        }
        finally { entities.Dispose(); }
        return index;
    }

    /// <summary>Find the registry record whose live entity is nearest to <paramref name="pos"/>
    /// (within <paramref name="maxDist"/>), re-resolving each record to its current entity. Registry-
    /// driven, so it keeps targeting objects across relog/restart — not just this session's spawns.</summary>
    (Entity Entity, SpawnRecord Record) NearestSpawnedRecord(float3 pos, float maxDist)
    {
        if (_records.Count == 0) return (Entity.Null, null);
        var index = BuildLiveIndex();
        float bestSq = maxDist * maxDist;
        Entity best = Entity.Null;
        SpawnRecord bestRec = null;
        foreach (var r in _records)
        {
            Entity e = index.Resolve(r);
            if (e == Entity.Null || !e.TryGetComponent<Translation>(out var t)) continue;
            float dx = t.Value.x - pos.x, dy = t.Value.y - pos.y, dz = t.Value.z - pos.z;
            float dsq = dx * dx + dy * dy + dz * dz;
            if (dsq < bestSq) { bestSq = dsq; best = e; bestRec = r; }
        }
        return (best, bestRec);
    }

    /// <summary>
    /// Boot re-apply: re-assert Immortal/decay on spawned objects and (config-gated) destroy
    /// confirmed "orphans" whose castle heart no longer exists. NON-DESTRUCTIVE to the registry:
    /// a record that does not resolve this boot is KEPT, never deleted — the world may still be
    /// streaming in, and an object that is merely streamed out must not be forgotten (the old
    /// behavior deleted such records and wrote the emptied file, permanently losing them). Mirrors
    /// PublicStorageService.ReapplyAll, which keeps unresolved entries and logs them.
    /// </summary>
    public void ReapplySpawned()
    {
        if (_records.Count == 0) return;

        var index = BuildLiveIndex();
        bool purgeOrphans = Settings.ObjectSpawn_PurgeOrphansOnBoot.Value;
        int restored = 0, orphaned = 0, unresolved = 0, hazardDropped = 0;
        bool changed = false;
        foreach (var r in new List<SpawnRecord>(_records))
        {
            // A prefab that a newer build has BLOCKED as a genuine NON-OBJECT or crash-hazard (e.g. the
            // "_Full" containers, characters, abilities) must not be re-applied or re-resolved — forget its
            // record. We deliberately do NOT touch any live entity here (don't risk re-triggering the hazard);
            // the game manages whatever exists, and the player can remove it normally. This self-heals records
            // left over from before the block landed. NOTE: uses IsTileModelObject (NOT IsRealPlaceableObject)
            // so a merely NON-NETWORKED object an admin 'force'-spawned (an invisible effect zone) KEEPS its
            // record and stays manageable across restarts — only true non-objects/hazards are dropped.
            if (!IsTileModelObject(r.PrefabGuid))
            {
                _records.Remove(r);
                hazardDropped++;
                changed = true;
                continue;
            }
            Entity e = index.Resolve(r);
            if (e == Entity.Null)
            {
                unresolved++; // not loaded yet / streamed out — KEEP the record (do not forget it)
                continue;
            }
            // Backfill world position for pre-v2 records now that we have the live entity.
            if (r.PosX == 0f && r.PosY == 0f && r.PosZ == 0f && e.TryGetComponent<Translation>(out var tr))
            {
                r.PosX = tr.Value.x; r.PosY = tr.Value.y; r.PosZ = tr.Value.z;
                changed = true;
            }
            // Orphan check: the object resolved (so the world IS loaded here), but its owning castle
            // heart is confirmed gone (disabled-included query) — the castle was destroyed/decayed.
            if (purgeOrphans && r.HasHeart && !HeartExistsByTile(r.HeartTileX, r.HeartTileY))
            {
                DestroySpawned(e);
                _records.Remove(r);
                orphaned++;
                changed = true;
                continue;
            }
            _liveSpawns.Add(e); // refresh the live marker from the persistent registry after a restart
            if (r.Indestructible)
            {
                e.AddOrSet(new Immortal { IsImmortal = true });
                if (e.Has<CastleDecayAndRegen>()) e.With((ref CastleDecayAndRegen d) => d.CanDieFromDecay = false);
                else e.AddOrSet(new CastleDecayAndRegen { CanDieFromDecay = false });
            }
            restored++;
        }
        if (changed) SaveSync();
        Core.Log.LogInfo($"[Uriel SPAWN] re-applied {restored} object(s); {orphaned} orphan(s) purged (castle gone); " +
                         $"{hazardDropped} now-blocked record(s) dropped; {unresolved} not resolved this boot (KEPT — may be streaming in).");
    }

    // ============================================================ auto-respawn loop

    /// <summary>Start the periodic auto-respawn poll (called once at boot, after ReapplySpawned).
    /// No-op when disabled by config or when the tick driver isn't running.</summary>
    public void StartRespawnLoop()
    {
        if (!Settings.ObjectSpawn_RespawnEnabled.Value) return;
        if (!Tick.IsRunning) { Core.Log.LogWarning("[Uriel SPAWN] auto-respawn loop NOT started (tick driver unavailable)."); return; }
        int seconds = Math.Max(5, Settings.ObjectSpawn_RespawnPollSeconds.Value);
        Tick.RunRepeating(seconds * 60, RespawnTick); // frames ≈ seconds × server fps; approximate cadence is fine
        Core.Log.LogInfo($"[Uriel SPAWN] auto-respawn loop started (~{seconds}s cadence).");
    }

    /// <summary>One poll: re-spawn every respawn-flagged object that is currently DESTROYED but whose
    /// castle still stands. The "destroyed vs merely streamed-out" distinction is the safety crux — the
    /// LiveIndex query is Disabled-included, so a streamed-out object still resolves; only a genuinely
    /// gone entity resolves to Null. And we only respawn when the castle HEART still exists (region is
    /// loaded / castle not destroyed), so a streamed-out region (heart also gone) never triggers a
    /// duplicate, and a destroyed castle never resurrects its objects.</summary>
    void RespawnTick()
    {
        if (!Settings.ObjectSpawn_RespawnEnabled.Value) return;
        bool any = false;
        foreach (var r in _records) if (r.RespawnOnDestroy) { any = true; break; }
        if (!any) return;

        var index = BuildLiveIndex();
        int respawned = 0;
        foreach (var r in new List<SpawnRecord>(_records))
        {
            if (!r.RespawnOnDestroy) continue;
            if (index.Resolve(r) != Entity.Null) continue;                         // still present (incl. Disabled) → nothing to do
            if (!r.HasHeart || !HeartExistsByTile(r.HeartTileX, r.HeartTileY)) continue; // castle gone / region not loaded → don't respawn
            if (TryRespawnRecord(r)) respawned++;
        }
        if (respawned > 0) Core.Log.LogInfo($"[Uriel SPAWN] auto-respawned {respawned} destroyed object(s).");
    }

    /// <summary>Re-instantiate a destroyed respawn-record at its stored position/rotation/mode, re-using
    /// the SAME record (so it keeps respawning and never duplicates). Returns false (and keeps the
    /// record) if the prefab/plot can't be resolved this cycle — it simply retries next poll.</summary>
    bool TryRespawnRecord(SpawnRecord r)
    {
        try
        {
            var pos = new float3(r.PosX, r.PosY, r.PosZ);
            if (pos.Equals(default(float3))) return false; // pre-v2 record without a stored position — can't place
            if (!TryResolvePlot(pos, out Entity heart, out int territory)) return false;
            var guidMap = Core.PrefabCollectionSystem._PrefabLookupMap.GuidToEntityMap;
            if (!guidMap.TryGetValue(new PrefabGUID(r.PrefabGuid), out Entity prefab) || !prefab.Exists()) return false;

            Entity e = ExecuteSpawn(prefab, pos, r.Rot, r.Indestructible, heart, out _, r.PlayerBreakable);
            if (e == Entity.Null) return false;

            // Re-point the record at the fresh entity's tile (position is identical so it usually matches,
            // but keep it exact); flags + ownership on the record are preserved.
            if (e.TryGetComponent<TilePosition>(out var tp)) { r.TileX = tp.Tile.x; r.TileY = tp.Tile.y; }
            r.TerritoryIndex = territory;
            SaveSync();
            Core.Log.LogInfo($"[Uriel SPAWN] respawned {new PrefabGUID(r.PrefabGuid).GetPrefabName()} at ({pos.x:F1},{pos.y:F1},{pos.z:F1}).");
            return true;
        }
        catch (Exception ex)
        {
            Core.Log.LogWarning($"[Uriel SPAWN] respawn of {new PrefabGUID(r.PrefabGuid).GetPrefabName()} failed: {ex.Message}");
            return false;
        }
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

    // How far in front of the player a `here`/at-player spawn lands, in metres. ~1.5m clears the
    // character's own body (~1 tile footprint) so the object drops onto the floor in front of them
    // instead of inside them. Tiles are 0.5m, so this is ~3 cells forward.
    const float HerePlacementForward = 1.5f;

    /// <summary>A point <see cref="HerePlacementForward"/> metres in front of the character's body
    /// facing, kept at the character's feet height. Used for `here`/at-player spawns so the object
    /// lands in FRONT of the player rather than on top of them. Falls back to the raw feet position
    /// if the facing can't be read.</summary>
    static float3 InFrontOf(Entity character, float3 feet)
    {
        if (character.TryGetComponent<Rotation>(out var rot))
        {
            float3 fwd = math.mul(rot.Value, new float3(0f, 0f, 1f));
            fwd.y = 0f;
            if (math.lengthsq(fwd) > 1e-4f)
            {
                fwd = math.normalize(fwd);
                return new float3(feet.x + fwd.x * HerePlacementForward, feet.y, feet.z + fwd.z * HerePlacementForward);
            }
        }
        return feet;
    }

    /// <summary>World position → tile-grid cell (KindredCommands ConvertPosToTileGrid). Shared by the
    /// transform writer and the overlap guard so both speak the same coordinate space.</summary>
    static int2 ConvertPosToTile(float3 pos) =>
        new int2((int)math.floor(pos.x * 2) + TileGridOffset, (int)math.floor(pos.z * 2) + TileGridOffset);

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

    /// <summary>
    /// Is this castle heart a LIVE, CLAIMED castle (vs. abandoned / fully-decayed / unclaimed)?
    /// Used by the placement gate so a player can't keep placing objects on a plot they abandoned —
    /// and so nobody places into a derelict plot at all (there's no owner to adopt the object into).
    /// Two signals, mirroring KindredCommands' own definitions:
    ///   - DECAYED: the heart is out of fuel AND its protection time has passed
    ///     (<c>FuelEndTime - ServerTime &lt;= 0 &amp;&amp; FuelQuantity &lt;= 0</c>). Admin-protected hearts use
    ///     <c>FuelEndTime = +∞</c>, so they always pass; a normally-fueled castle passes too.
    ///   - UNCLAIMED: the heart has a <c>UserOwner</c> whose owner User no longer resolves on the server
    ///     (relinquished). A heart whose entity was destroyed outright never reaches here — its territory
    ///     resolves no heart, so <see cref="TryResolvePlot"/> already fails.
    /// </summary>
    static bool CastleClaimedAndAlive(Entity heart)
    {
        if (!heart.Exists()) return false;
        if (heart.TryGetComponent<CastleHeart>(out var ch))
        {
            double remaining = ch.FuelEndTime - Core.ServerGameManager.ServerTime;
            if (remaining <= 0 && ch.FuelQuantity <= 0) return false; // fully decayed → treat as abandoned
        }
        if (heart.TryGetComponent<UserOwner>(out var uo) && !uo.Owner.GetEntityOnServer().Exists())
            return false; // relinquished / no owner
        return true;
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
        // Abandoned / fully-decayed / unclaimed plot — refuse for EVERYONE (incl. admins): there's no live
        // owner to adopt the object into, and a player must not keep building on a plot they relinquished.
        if (!CastleClaimedAndAlive(heart))
        {
            error = $"That castle plot has been abandoned or is decaying — objects can't be {verb} there.";
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
    ///
    /// INVISIBLE-WHEN-GRAFTED families (added 2026-06-09 after a live report that some spawns appear but
    /// never render, suspected in a server crash):
    ///  - MicroPOI* — ALL 85 are world-gen Point-of-Interest / territory SPAWNER controllers (the dump:
    ///    73 carry MicroPOIConfig + MicroPOIUnitSpawnerElement, the other 12 are MicroPOISpawner_* /
    ///    MicroPOIEmptySpawner_*). NONE carry a render component; their visuals live on LinkedEntityGroup
    ///    children the POI system materialises during world-gen. Grafted bare into a castle you get an
    ///    invisible logic entity whose UNIT SPAWNER can tick inside a player plot — exactly the kind of
    ///    malformed state that can throw in a server system update (DEV_REMINDERS). The earlier
    ///    "MicroPOI = tree/flower decor clusters, keep them" note was WRONG — verified none render.
    ///  - *InvisibleObject* — TM_InvisibleObject_* AI/POI position markers (fishing spots, boss positions):
    ///    invisible by design, but carry TM_/TilePosition so they pass IsPlaceableObject.
    ///
    /// CONTEXT-ONLY building pieces — networked (so NetworkId can't catch them) but only render in their
    /// proper structural context, NOT at ground level (added 2026-06-09 after a live report):
    ///  - ROOF TILES — TM_CastleRoof_Type0-14 + TM_RusticHouse_Roofing_Type0-14 (30 total). They carry
    ///    ProjectM.CastleBuilding.CastleRoofOrnaments + ProjectM.Roofs.RoofTileData; the roof system places
    ///    them at a HEIGHT above walls. Dropped at the floor they have nowhere to sit and render invisibly.
    ///    Caught by the CastleRoofOrnaments component (exactly those 30 — verified identical to the
    ///    RoofTileData set; NOT the broader ProjectM.Roofs namespace, which also tags castle FLOORS).
    /// </summary>
    static readonly string[] NonObjectPrefixes =
        { "CHAR_", "AB_", "GM_", "Liquid_", "Summon", "USB_", "PrefabVariant", "MicroPOI" };

    // Substring tokens for invisible/non-render position-marker families (matched case-insensitively;
    // these lead with TM_ so it's a substring test, not a prefix). Add new confirmed marker families here.
    static readonly string[] InvisibleMarkerFragments = { "InvisibleObject", "IdleInteractionLocation" };

    static bool IsNonObject(string name, Entity prefab)
    {
        foreach (var p in NonObjectPrefixes)
            if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        // Invisible / non-render position markers — networked (so NetworkId passes) but no mesh:
        //  - TM_InvisibleObject_*  : AI / boss position markers (fishing spots, Dracula/Morgana positions).
        //    NB: deliberately NOT "Invisible" alone — invisible castle walls/floors (TM_Castle_*_Invisible)
        //    are legit build pieces filtered elsewhere; only the *Object* markers are caught here.
        //  - TM_IdleInteractionLocation_* : NPC idle-animation spots (Tinker/Fishing/Digging/…, 16; reported
        //    invisible 2026-06-09). Matches "...Location" specifically — the *_IdleInteraction SUFFIX props
        //    (braziers, target dummies, mine cart) are REAL visible objects and lack the "Location" token.
        foreach (var frag in InvisibleMarkerFragments)
            if (name.IndexOf(frag, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        // Component backstop — catches units the name filter could miss. EVERY character/NPC carries
        // Movement (verified across the CHAR_* dump: all 532 have it, alongside TilePosition which
        // otherwise lets them pass IsPlaceableObject), and V Bloods additionally carry VBloodConsumeSource.
        // MicroPOI controllers carry MicroPOIInstance; roof tiles carry CastleRoofOrnaments (render only at
        // height, invisible at ground). A real placeable world object has none of these — robust to renames.
        //
        // CRASH HAZARD — `DropInInventoryOnSpawn` is the CAUSAL component: the container drops its start
        // items into its inventory THE MOMENT IT SPAWNS. Grafted in outside the normal placement pipeline,
        // that spawn-time inventory/chain logic runs in a Burst job and ABORTS THE SERVER
        // (`AppendRemovedComponentRecordError`, observed 2026-06-09 spawning TM_Bookshelf_01_Full — crashes
        // in BOTH the adopted and playerBreakable paths, so it's the spawn-time logic, not adoption).
        // Filtering by this single component covers the WHOLE class structurally — all 60 "_Full" /
        // loot containers (open bookshelves, shelves, drawers, cabinets, carriage/world chests, sarcophagi,
        // grape barrels), present and future, without enumerating names.
        // Deliberately NOT `ExternalInventoryStartItems`: that is on ~376 prefabs incl. EVERY crafting
        // station, wardrobe, research station, and prison cell — they DEFER generation (recipe/on-use) and
        // spawn fine, so blocking it would gut the catalog. (It is ALSO an unregistered IL2CPP generic/buffer
        // type whose `Has<T>()` THROWS — which once aborted the whole catalog build and spammed BCH's version
        // probe, 2026-06-09.) Resource-node templates carry neither; empty containers stay placeable.
        return prefab.Has<Movement>() || prefab.Has<VBloodConsumeSource>()
            || prefab.Has<MicroPOIInstance>() || prefab.Has<CastleRoofOrnaments>()
            || HasDropInInventoryOnSpawn(prefab);
    }

    // Guarded probe for the container crash-filter. Some V Rising components (esp. generic/buffer types)
    // are NOT registered in the IL2CPP TypeManager, so `Has<T>()` THROWS — and an unhandled throw inside the
    // catalog build aborts the WHOLE catalog (it did, 2026-06-09: ExternalInventoryStartItems left the
    // catalog empty and spammed `.uriel api version`). DropInInventoryOnSpawn is verified usable, but we
    // probe it ONCE and, if it ever throws, disable just this one check (logged) rather than break the
    // catalog. Any future component check added here should use the same guarded pattern.
    static bool _dropInInvUsable = true;
    static bool HasDropInInventoryOnSpawn(Entity prefab)
    {
        if (!_dropInInvUsable) return false;
        try { return prefab.Has<DropInInventoryOnSpawn>(); }
        catch (Exception ex)
        {
            _dropInInvUsable = false;
            Core.Log.LogWarning($"[Uriel SPAWN] DropInInventoryOnSpawn check unavailable ({ex.Message}); " +
                                "container crash-filter disabled this session (catalog still builds).");
            return false;
        }
    }

    /// <summary>Structural "is this a real placeable WORLD object?" test for an arbitrary GUID — the
    /// same families EnsureCatalog keeps (not a character/V Blood/ability/chain/debug/internal, and it
    /// carries a placeable component). Deliberately IGNORES the reversible admin blocklist, so blocking
    /// a prefab never deletes a player's unlock. Used to (a) refuse spawning a non-object that lingers in
    /// an unlock list and (b) prune such stale unlocks.</summary>
    bool IsRealPlaceableObject(int guid)
    {
        if (!IsTileModelObject(guid)) return false;
        // ...AND it must be NETWORKED so it renders client-side (catalog / player path; see IsPlaceableObject).
        var guidMap = Core.PrefabCollectionSystem._PrefabLookupMap.GuidToEntityMap;
        return guidMap.TryGetValue(new PrefabGUID(guid), out Entity prefab) && prefab.Has<NetworkId>();
    }

    /// <summary>Same structural test as <see cref="IsRealPlaceableObject"/> but WITHOUT the NetworkId
    /// (rendering) requirement: "is this a genuine tile-model object — not a character / V Blood / ability /
    /// chain controller / debug / internal / crash-hazard — even if it's a non-networked static model that
    /// would spawn INVISIBLE?" An object that passes this but fails IsRealPlaceableObject is a real object
    /// that simply won't render (e.g. a gameplay-effect ZONE: garlic/holy/cursed area, dynamic cloud). The
    /// admin '<c>force</c>' spawn path uses this so such objects can still be placed for testing — the strict
    /// IsRealPlaceableObject still governs the catalog and the normal player path, so players never get one.</summary>
    bool IsTileModelObject(int guid)
    {
        var pg = new PrefabGUID(guid);
        var guidMap = Core.PrefabCollectionSystem._PrefabLookupMap.GuidToEntityMap;
        if (!guidMap.TryGetValue(pg, out Entity prefab) || !prefab.Exists()) return false;
        string name = pg.GetPrefabName();
        if (IsDebugPrefab(name)) return false;
        if (IsSpawnChainController(prefab)) return false;
        if (IsNonObject(name, prefab)) return false;
        if (!Settings.ObjectSpawn_IncludeCastleBuildables.Value && prefab.Has<BlueprintData>()) return false;
        return HasTileComponent(prefab);
    }

    /// <summary>Scrub a player's unlocks of GUIDs that aren't real placeable objects (stale CHAR_/V Blood
    /// entries from an older catalog filter). Cheap no-op once clean; logs when it removes anything.</summary>
    void PruneStaleUnlocks(ulong steamId)
    {
        int removed = Core.PlayerUnlock.PruneUnlocked(steamId, IsRealPlaceableObject);
        if (removed > 0)
            Core.Log.LogInfo($"[Uriel SPAWN] pruned {removed} stale non-object unlock(s) (characters/V Bloods/etc.) for {steamId}.");
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
        PruneStaleUnlocks(steamId);
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

    // Each wire line is emitted as its OWN ctx.Reply (one System-chat message per line) — BCH's
    // [URIEL:*] reader (and the proven Beelzebub pattern it mirrors) treats one chat message as one
    // wire line and does NOT split on '\n'. So these methods return a LIST of lines, never a single
    // '\n'-joined block. (The old single-Reply block was why `api catalog`/`api unlocked` returned
    // "nothing" while the single-line `api version` worked — BCH-handoff §6 P0, 2026-06-08.)
    // Page size only has to keep EACH line under VCF's 509-char Reply cap (trivially true at any size,
    // labels are short prefab-derived tokens); 20 rows/page keeps the full-catalog browse — hundreds
    // of objects — to a sane number of round-trips (mirrors Beelzebub's 40-row catalog pages).
    const int ApiPageSize = 20;

    /// <summary>`.uriel api catalog &lt;page&gt;` — the total prefab list available in-game (paged).
    /// Returns one wire line per list entry (header + rows + end), each sent via its own ctx.Reply.
    /// Row: [URIEL:object] guid= disc=0|1 label=&lt;humanized,wire-safe&gt; cat=&lt;category&gt;.</summary>
    public List<string> ApiCatalogPage(int page)
    {
        EnsureCatalog();
        int total = _placeable.Count;
        int pages = Math.Max(1, (total + ApiPageSize - 1) / ApiPageSize);
        page = Math.Clamp(page, 1, pages);
        var lines = new List<string>
        {
            $"[URIEL:catalog] page={page}/{pages} total={total} discoverable={_discoverableGuids.Count}"
        };
        int count = 0;
        for (int i = (page - 1) * ApiPageSize; i < Math.Min(page * ApiPageSize, total); i++)
        {
            var c = _placeable[i];
            lines.Add($"[URIEL:object] guid={c.Guid._Value} disc={(_discoverableGuids.Contains(c.Guid._Value) ? 1 : 0)} label={c.Label} cat={c.Cat}");
            count++;
        }
        lines.Add($"[URIEL:end] cmd=catalog page={page}/{pages} count={count}");
        return lines;
    }

    /// <summary>`.uriel api unlocked &lt;steamId&gt; &lt;page&gt;` — a player's unlocked prefabs + collection %.
    /// Returns one wire line per list entry (header + rows + end), each sent via its own ctx.Reply.</summary>
    public List<string> ApiUnlockedPage(ulong steamId, int page)
    {
        EnsureCatalog();
        PruneStaleUnlocks(steamId);
        var guids = new List<int>(Core.PlayerUnlock.GetUnlocked(steamId));
        guids.Sort();
        int discoverableTotal = _discoverableGuids.Count;
        int unlockedDiscoverable = 0;
        foreach (int g in guids) if (_discoverableGuids.Contains(g)) unlockedDiscoverable++;
        int pct = discoverableTotal > 0 ? (int)Math.Round(100.0 * unlockedDiscoverable / discoverableTotal) : 0;

        int total = guids.Count;
        int pages = Math.Max(1, (total + ApiPageSize - 1) / ApiPageSize);
        page = Math.Clamp(page, 1, pages);
        var lines = new List<string>
        {
            $"[URIEL:unlocked] page={page}/{pages} steam={steamId} n={total} discoverable={discoverableTotal} pct={pct}"
        };
        int count = 0;
        for (int i = (page - 1) * ApiPageSize; i < Math.Min(page * ApiPageSize, total); i++)
        {
            int g = guids[i];
            string label, cat; int disc;
            if (_byGuid.TryGetValue(g, out var ce)) { label = ce.Label; cat = ce.Cat; disc = _discoverableGuids.Contains(g) ? 1 : 0; }
            else { label = SafeToken(Humanize(new PrefabGUID(g).GetPrefabName())); cat = "other"; disc = 0; }
            lines.Add($"[URIEL:object] guid={g} disc={disc} label={label} cat={cat}");
            count++;
        }
        lines.Add($"[URIEL:end] cmd=unlocked page={page}/{pages} count={count}");
        return lines;
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
    // A real placeable object must (a) carry a tile/placement component AND (b) be NETWORKED. The
    // network requirement is what makes a runtime-spawned object actually RENDER on the client: a
    // networked entity is replicated via NetworkSnapshot, so the client is told it exists and draws it.
    // A non-networked tile model is baked world-static geometry — it exists only on the server, the
    // client is never notified, and the spawn is INVISIBLE (you only know it's there when you despawn
    // it). Live report (2026-06-09): TM_*_Original world-gen source clusters and MicroPOI controllers
    // spawned invisibly; a prefab-dump audit confirmed those (and only ~36 of 3778 TM_ prefabs) lack
    // NetworkId, while every visible category (furniture, lights, containers, resource nodes, stairs)
    // has it. Requiring NetworkId is the principled fix for the whole invisible-spawn class — far more
    // robust than chasing name families one at a time.
    /// <summary>Carries a tile/placement component (tile model, castle buildable, or castle-owned object) —
    /// the "is it a placeable shape at all?" half of <see cref="IsPlaceableObject"/>, split out so the admin
    /// 'force' path can accept a genuine tile model that is merely non-networked (would spawn invisible).</summary>
    static bool HasTileComponent(Entity prefab) =>
        prefab.Has<TilePosition>() || prefab.Has<EditableTileModel>() || prefab.Has<CastleHeartConnection>();

    static bool IsPlaceableObject(Entity prefab) => HasTileComponent(prefab) && prefab.Has<NetworkId>();

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

    // ============================================================ overlap guard

    // Vertical tolerance (~one castle storey) for the overlap test. Two tile objects within this much
    // height of each other count as the SAME building level; further apart they're treated as different
    // floors and don't collide. This keeps the guard strict within the level you're decorating while not
    // false-positiving on a multi-storey castle (an upper-floor cell shares the (x,y) tile of the wall
    // below it). A castle wall/storey is ~2.5m tall.
    const float OverlapHeightBand = 2.5f;

    // Horizontal proximity floor for the overlap test — now admin-configurable via
    // ObjectSpawn.OverlapMinDistance (default 0.5m ≈ one tile). The integer tile-cell test only trips when
    // two anchor cells are IDENTICAL, so a free-aim MOVE that lands a fraction of a tile away (straddling a
    // cell boundary) visually overlaps but slips through; this center-to-center distance catches that
    // near-stacking. Admins can LOWER it (toward 0) to place décor closer together, or 0 to disable the
    // distance check entirely (only the exact-cell block remains). NOT applied to walls — a wall sits on a
    // tile boundary and décor placed flush against it is legitimate; only the exact-cell test bites a drop
    // INTO a wall's own cell.

    /// <summary>
    /// Strict placement guard (config <c>ObjectSpawn.PreventOverlap</c>): would a spawn at
    /// <paramref name="pos"/> land on a tile cell already occupied by a NON-floor tile model — a wall,
    /// crafting station, native prop, the castle heart, or another spawned object? Floors are the explicit
    /// exception (decor sits on floors). The candidate occupies its single anchor cell (placed objects use
    /// a 1×1 footprint); each existing tile model is tested as its <c>TilePosition.Tile</c> expanded by any
    /// runtime <c>TileBounds</c> extent, within <see cref="OverlapHeightBand"/> of the candidate's height.
    /// <paramref name="ignore"/> excludes the object being moved (so a short move doesn't collide with
    /// itself). Returns true (with the blocker's name) when placement should be REFUSED.
    /// </summary>
    bool WouldOverlap(float3 pos, Entity ignore, out string blockerName)
    {
        blockerName = null;
        if (!Settings.ObjectSpawn_PreventOverlap.Value) return false;

        float minDist = math.max(0f, Settings.ObjectSpawn_OverlapMinDistance.Value);
        int2 cell = ConvertPosToTile(pos);
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(new(Il2CppType.Of<PrefabGUID>(), ComponentType.AccessMode.ReadOnly))
            .AddAll(new(Il2CppType.Of<TilePosition>(), ComponentType.AccessMode.ReadOnly))
            .AddAll(new(Il2CppType.Of<Translation>(), ComponentType.AccessMode.ReadOnly))
            .WithOptions(EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludeSpawnTag);
        var query = Core.EntityManager.CreateEntityQuery(ref builder);
        var entities = query.ToEntityArray(Allocator.Temp);
        bool overlap = false;
        try
        {
            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                if (e == ignore) continue;
                if (e.Has<Movement>()) continue;                         // units/players move — a character
                                                                         // (incl. the caster's own body, which
                                                                         // carries TilePosition) never blocks a
                                                                         // build placement
                if (e.Has<CastleFloor>()) continue;                      // floors are allowed under decor
                if (!e.TryGetComponent<TilePosition>(out var tp)) continue;
                if (!e.TryGetComponent<Translation>(out var tr)) continue;
                if (math.abs(tr.Value.y - pos.y) > OverlapHeightBand) continue; // different building level

                int2 oMin = tp.Tile, oMax = tp.Tile;
                if (e.TryGetComponent<TileBounds>(out var tb))
                {
                    int2 ext = new int2(math.abs(tb.Value.Max.x - tb.Value.Min.x),
                                        math.abs(tb.Value.Max.y - tb.Value.Min.y));
                    oMax = new int2(tp.Tile.x + ext.x, tp.Tile.y + ext.y);
                }
                // The candidate's single anchor cell falls inside the existing object's tile box?
                bool hit = cell.x >= oMin.x && cell.x <= oMax.x && cell.y >= oMin.y && cell.y <= oMax.y;
                // Proximity backstop for sub-cell straddles (the move gap): centers too close. Walls are
                // exempt so decor can sit flush against them (the cell test still guards drops into a wall).
                if (!hit && minDist > 0f && !e.Has<CastleWall>())
                {
                    float dx = tr.Value.x - pos.x, dz = tr.Value.z - pos.z;
                    if (dx * dx + dz * dz < minDist * minDist) hit = true;
                }
                if (hit)
                {
                    blockerName = e.GetPrefabGuid().GetPrefabName();
                    overlap = true;
                    break;
                }
            }
        }
        finally { entities.Dispose(); }
        return overlap;
    }

    // ============================================================ spawn

    /// <summary>
    /// Spawn <paramref name="prefabRef"/> at the player's aim point (or feet), INSIDE the
    /// castle plot at that spot. Players may only place in a plot they own; admins, any plot;
    /// open world is refused. <paramref name="rotation"/> is tile rotation 0–3.
    /// <paramref name="atFeet"/> forces placement at the PLAYER'S position instead of the aim
    /// point — required when the command is fired from a BCH UI button, where the cursor sits on
    /// the panel and the aim ray points outside the plot (the "can only place in a castle plot"
    /// error). See the `here`/`nearest` token in ObjectCommands.Spawn.
    /// </summary>
    public bool Spawn(Entity character, bool isAdmin, string prefabRef, int rotation, bool? breakable,
                      bool playerBreakable, bool respawn, bool atFeet, bool force, out string message)
    {
        if (!TryResolvePrefab(prefabRef, out PrefabGUID guid, out Entity prefab, out string name, out string err))
        {
            message = err;
            return false;
        }
        bool forcedInvisible = false; // admin 'force'-spawned a non-networked (likely invisible) object

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

        // Structural safety net: never spawn a character / V Blood / ability / internal prefab as an
        // object, even if it lingers in the player's unlock list from an older catalog filter. (The
        // spawned unit wouldn't persist as a placeable object anyway — it just vanishes.) Scrub the
        // stale unlock so it stops showing in the list too.
        if (!IsRealPlaceableObject(guid._Value))
        {
            // Two distinct cases: (a) a GENUINE tile-model object that's merely NON-NETWORKED — it would
            // spawn invisible (e.g. a gameplay-effect zone: garlic/holy/cursed area, dynamic cloud). An
            // admin can opt in with 'force' to test it. (b) Not a placeable object at all (character / V
            // Blood / ability / internal) — always refused, and any stale unlock is scrubbed.
            if (IsTileModelObject(guid._Value))
            {
                if (!(isAdmin && force))
                {
                    message = Clamp($"{name} is a NON-NETWORKED object — it would spawn INVISIBLE (you'd feel its effect, " +
                        "e.g. an area buff/debuff zone, but not see a model), so it's kept out of the normal catalog. " +
                        (isAdmin
                            ? $"To place it anyway for testing, add 'force': '.uriel spawn {prefabRef} force'."
                            : "Ask an admin to spawn it for testing."));
                    return false;
                }
                forcedInvisible = true; // admin opted in — allow, and warn on success
            }
            else
            {
                if (Core.PlayerUnlock.Revoke(character.GetSteamId(), guid._Value))
                    Core.Log.LogInfo($"[Uriel SPAWN] removed stale non-object unlock {name}({guid._Value}) for {character.GetSteamId()}.");
                message = $"{name} can't be spawned — it's a character, V Blood, ability, or internal prefab, " +
                          "not a placeable world object. (If it was in your unlock list from an older version, it's now been removed.)";
                return false;
            }
        }

        // Position: the player's feet when atFeet (UI button / explicit `here`), otherwise the
        // aim point, falling back to the player's feet when no aim is available.
        float3 pos;
        if (atFeet)
        {
            if (!PublicStorageService.TryGetCharacterPosition(character, out var feet))
            { message = "Could not read your position."; return false; }
            // Place it in front of the player, not inside their body (the `here`/UI path has no aim ray).
            pos = InFrontOf(character, feet);
        }
        else if (character.TryGetComponent<EntityAimData>(out var aim) && !aim.AimPosition.Equals(default(float3)))
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

        // OVERLAP GATE (everyone, incl. admins): don't drop the object inside a wall/station/prop or onto
        // another spawned object — only floors may sit under it. Guards against the pile-ups that can
        // destabilise the server. Config ObjectSpawn.PreventOverlap (default on) governs it.
        if (WouldOverlap(pos, Entity.Null, out string blocker))
        {
            message = Clamp($"Can't place {name} there — it would overlap {blocker}. Objects can't be placed " +
                            "inside walls, stations, or other spawned objects (only floors may sit under them). " +
                            "Aim at a clear spot. (Admins can disable ObjectSpawn.PreventOverlap to allow stacking.)");
            return false;
        }

        // PLAYER GATES (non-admins): Discovery access + per-object/global conditions + affordability.
        // Admins bypass all of it. Conditions (MaxPerPlot / Cost / Permit*) are the admin-managed rules in
        // object_conditions.json — per-object overrides global (see ObjectConditionsService).
        var cond = Core.ObjectConditions.Resolve(guid._Value);
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
            // MAX-PER-PLOT cap (e.g. an admin allows at most 3 of this object on a plot). Counts Uriel's
            // own records of this prefab on the territory.
            if (cond.MaxPerPlot > 0)
            {
                int onPlot = 0;
                foreach (var r in _records) if (r.PrefabGuid == guid._Value && r.TerritoryIndex == territory) onPlot++;
                if (onPlot >= cond.MaxPerPlot)
                {
                    message = $"You can have at most {cond.MaxPerPlot}x {name} on this plot ({onPlot} already placed). Remove one first ('.uriel despawn').";
                    return false;
                }
            }
            // COST — a per-object/global condition overrides the server-wide cost config when set.
            if (cond.CostSpecified) { costItem = cond.CostItem; costAmount = cond.CostAmount; }
            else { costItem = Settings.ObjectSpawn_PrefabCostItem.Value; costAmount = Settings.ObjectSpawn_PrefabCostStack.Value; }
            if (costItem != 0 && costAmount > 0 && !HasInventoryItems(character, costItem, costAmount))
            {
                message = $"Building {name} costs {costAmount}x {new PrefabGUID(costItem).GetPrefabName()} — you don't have enough in your inventory.";
                return false;
            }
        }

        bool indestructible = !(breakable ?? !Settings.ObjectSpawn_Indestructible.Value);
        // PERMISSION CONDITIONS (non-admins): an admin can deny a player the right to spawn a given object
        // indestructible and/or with auto-respawn (per-object or global). Admins are never restricted.
        if (!isAdmin)
        {
            if (indestructible && !cond.PermitIndestructible)
            {
                if (breakable == false) // player explicitly asked for indestructible
                { message = $"{name} can't be spawned indestructible on this server — try '.uriel spawn {prefabRef} breakable'."; return false; }
                indestructible = false; // default was indestructible → quietly downgrade to breakable
            }
            if (respawn && !cond.PermitRespawn)
            { message = $"{name} can't be spawned with auto-respawn on this server."; return false; }
        }
        // 'smashable'/'respawn' only make sense for a breakable object: an indestructible one can't be
        // destroyed (so nothing to respawn) and stays castle-adopted (so player-breakable is moot).
        if (indestructible) { playerBreakable = false; respawn = false; }
        if (respawn && !Settings.ObjectSpawn_RespawnEnabled.Value)
        { message = "Auto-respawn is disabled on this server (ObjectSpawn.RespawnEnabled)."; return false; }

        try
        {
            Entity e = ExecuteSpawn(prefab, pos, rotation & 3, indestructible, heart, out string adoptNote, playerBreakable);
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

            RegisterRecord(e, indestructible, territory, heart, character.GetSteamId(), paidItem, paidAmount,
                           rotation & 3, respawn, playerBreakable);
            Core.Log.LogInfo($"[Uriel SPAWN] {name}({guid._Value}) at ({pos.x:F1},{pos.y:F1},{pos.z:F1}) rot={rotation & 3} immortal={indestructible} playerBreakable={playerBreakable} respawn={respawn} territory={territory} cost={paidAmount}x{paidItem}. {adoptNote}");
            string costNote = paidAmount > 0 ? $" Paid {paidAmount}x {new PrefabGUID(paidItem).GetPrefabName()}." : "";
            string whereNote = atFeet ? " at your location" : "";
            string mode = indestructible ? "indestructible"
                        : playerBreakable ? "breakable by anyone (you included)"
                        : "breakable by raid/decay";
            string respawnNote = respawn ? " Auto-respawns when destroyed (until the castle is gone or you '.uriel despawn' it)." : "";
            string invisibleNote = forcedInvisible ? " NOTE: non-networked — it likely renders INVISIBLE; you'll feel its effect but won't see a model. '.uriel despawn' still removes it." : "";
            message = $"Spawned {name}{whereNote} ({mode}, rot {rotation & 3}).{costNote}{respawnNote}{invisibleNote} {adoptNote} " +
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

    Entity ExecuteSpawn(Entity prefab, float3 pos, int rot, bool indestructible, Entity heart, out string adoptNote, bool playerBreakable = false)
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
        //
        // playerBreakable SKIPS adoption on purpose: copying the castle Team makes the object friendly
        // to its owner, which is exactly what stops the owner weapon-smashing it (vanilla castle
        // protection). Leaving it un-adopted keeps it a plain destructible world object the owner CAN
        // hit. Ownership/management still work — '.uriel despawn'/'move' resolve ownership from the
        // object's POSITION (the territory's heart), not its components — and the spawn RECORD still
        // stores the heart tile for orphan-purge + auto-respawn.
        if (heart.Exists() && !playerBreakable)
        {
            if (!e.Has<CastleHeartConnection>()) Core.EntityManager.AddComponent<CastleHeartConnection>(e);
            e.With((ref CastleHeartConnection c) => c.CastleHeartEntity = heart);
            if (heart.TryGetComponent<Team>(out var team)) e.AddOrSet(team);
            if (heart.TryGetComponent<TeamReference>(out var teamRef)) e.AddOrSet(teamRef);
            if (heart.TryGetComponent<UserOwner>(out var owner)) e.AddOrSet(owner);
            adoptNote = "Adopted into the castle.";
        }
        else if (playerBreakable)
        {
            adoptNote = "Placed un-owned so you can break it yourself.";
        }

        // Indestructibility is governed by TWO independent mechanisms, and a world object can use
        // EITHER — so we must drive BOTH (audited 2026-06-08): furniture (TM_GloomRot_Laboratory_*)
        // responds to `Immortal`, while world chests / resource objects have NO native `Immortal` and
        // are governed by the HEALTH path — `Health` + `HealthConstants.DestroyOnDeath` + a
        // `DestroyAfterDuration` auto-despawn timer (~1200s). Toggling only `Immortal` was a no-op on
        // those (the cause of "breakable still invulnerable" AND "indestructible chest vanished").
        if (indestructible)
        {
            e.AddOrSet(new Immortal { IsImmortal = true });
            if (e.Has<CastleDecayAndRegen>()) e.With((ref CastleDecayAndRegen d) => d.CanDieFromDecay = false);
            else e.AddOrSet(new CastleDecayAndRegen { CanDieFromDecay = false });
            // Health path: stop death-on-zero and the auto-despawn timer so it truly persists.
            if (e.Has<HealthConstants>()) e.With((ref HealthConstants hc) => hc.DestroyOnDeath = false);
            StripAutoDestroyTimers(e);
        }
        else
        {
            // Breakable: clear any Immortal we (or the prefab) set, and let the Health path destroy it
            // on death. Also strip the prefab auto-despawn timer so a "breakable" object doesn't simply
            // vanish on its own ~1200s clock — it follows vanilla raid/decay rules instead, and the
            // owner removes it with '.uriel despawn'. NOTE: while ADOPTED into the castle (owner team),
            // V Rising blocks the OWNER from weapon-smashing it — that's vanilla castle protection, not
            // an Immortal flag (raiders/enemies and decay can still destroy it).
            if (e.Has<Immortal>()) e.With((ref Immortal im) => im.IsImmortal = false);
            if (e.Has<HealthConstants>()) e.With((ref HealthConstants hc) => hc.DestroyOnDeath = true);
            StripAutoDestroyTimers(e);
        }

        PublicStorageService.ForceResync(e);
        return e;
    }

    /// <summary>Remove the prefab's auto-despawn timers so a spawned object doesn't vanish on its own.
    /// World pickups/chests ship a <c>DestroyAfterDuration</c> (~1200s) and sometimes a <c>LifeTime</c>;
    /// neither belongs on a placed castle object (indestructible OR breakable — breakable still follows
    /// raid/decay rules, it just shouldn't evaporate on a hidden clock).</summary>
    static void StripAutoDestroyTimers(Entity e)
    {
        if (e.Has<DestroyAfterDuration>()) Core.EntityManager.RemoveComponent<DestroyAfterDuration>(e);
        if (e.Has<LifeTime>()) Core.EntityManager.RemoveComponent<LifeTime>(e);
    }

    /// <summary>Write position + tile rotation onto an object. World→tile is
    /// ConvertPosToTileGrid; height stays in Translation.y. Shared by spawn and respawn.</summary>
    static void ApplyTransform(Entity e, float3 pos, int rot)
    {
        rot &= 3;
        var tileRot = (TileRotation)rot;
        quaternion q = quaternion.RotateY(math.radians(90f * rot));
        int2 tile = ConvertPosToTile(pos);

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
        var (target, rec) = NearestSpawnedRecord(pos, Settings.ObjectSpawn_MaxTargetDistance.Value);
        if (target == Entity.Null)
        {
            message = $"No Uriel-spawned object within {Settings.ObjectSpawn_MaxTargetDistance.Value:F0}m. " +
                      "(despawn/move/rotate target objects spawned by Uriel; '.uriel purgeplot' clears a whole plot; " +
                      "admins can '.uriel forcedespawn' an untracked object.)";
            return false;
        }
        if (!isAdmin && !CallerOwnsObject(character, target))
        { message = "You can only remove objects in your own castle plot."; return false; }

        string name = target.GetPrefabGuid().GetPrefabName();

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

        DestroySpawned(target);
        Core.Log.LogInfo($"[Uriel SPAWN] despawned {name}.");
        message = $"Removed {name}.{refundNote}";
        return true;
    }

    /// <summary>Move the nearest spawned object to your aim point — or to your own location when
    /// <paramref name="toFeet"/> (the `here`/`nearest` token; required from a BCH UI button, where
    /// the cursor is on the panel and the aim ray points outside the plot). Respawn-based — these
    /// objects render from baked static batches; an in-place edit risks the stair
    /// invisible-until-restart bug. Destination must be inside a plot you own (admins: any plot).</summary>
    public bool Move(Entity character, bool isAdmin, bool toFeet, out string message)
    {
        if (!PublicStorageService.TryGetCharacterPosition(character, out float3 feet))
        { message = "Could not read your position."; return false; }
        var (target, rec) = NearestSpawnedRecord(feet, Settings.ObjectSpawn_MaxTargetDistance.Value);
        if (target == Entity.Null)
        {
            message = $"No Uriel-spawned object within {Settings.ObjectSpawn_MaxTargetDistance.Value:F0}m to move. " +
                      "(Stand near it.)";
            return false;
        }

        float3 dest;
        if (toFeet)
            dest = feet;
        else if (character.TryGetComponent<EntityAimData>(out var aim) && !aim.AimPosition.Equals(default(float3)))
            dest = aim.AimPosition;
        else
        { message = "Aim where you want it, then run '.uriel move' — or use '.uriel move here' to bring it to your location."; return false; }

        if (!CheckPlacement(character, isAdmin, dest, "moved", out Entity heart, out int territory, out string gateErr))
        { message = gateErr; return false; }

        string name = target.GetPrefabGuid().GetPrefabName();
        // Overlap gate (excluding the object itself, so a short move doesn't collide with its own cell).
        if (WouldOverlap(dest, target, out string blocker))
        { message = Clamp($"Can't move {name} there — it would overlap {blocker}. Aim at a clear spot."); return false; }
        if (!RespawnAt(target, rec, dest, CurrentRot(target), heart, territory, out _, out string err)) { message = err; return false; }
        Core.Log.LogInfo($"[Uriel SPAWN] moved {name} to ({dest.x:F1},{dest.y:F1},{dest.z:F1}){(toFeet ? " [at player]" : "")}.");
        message = toFeet ? $"Moved {name} to your location." : $"Moved {name} to your aim point.";
        return true;
    }

    /// <summary>Rotate the nearest spawned object. No arg turns it 90°; 0-3 sets that tile
    /// rotation. Position preserved; respawn-based. Owned-plot only for non-admins.</summary>
    public bool Rotate(Entity character, bool isAdmin, int? rotation, out string message)
    {
        if (!TryGetTargetPosition(character, out float3 pos)) { message = "Could not read your position."; return false; }
        var (target, rec) = NearestSpawnedRecord(pos, Settings.ObjectSpawn_MaxTargetDistance.Value);
        if (target == Entity.Null)
        { message = $"No Uriel-spawned object within {Settings.ObjectSpawn_MaxTargetDistance.Value:F0}m to rotate."; return false; }
        if (!target.TryGetComponent<Translation>(out var t)) { message = "Object has no position to preserve."; return false; }

        if (!CheckPlacement(character, isAdmin, t.Value, "moved", out Entity heart, out int territory, out string gateErr))
        { message = gateErr; return false; }

        int newRot = (rotation ?? CurrentRot(target) + 1) & 3;
        string name = target.GetPrefabGuid().GetPrefabName();
        if (!RespawnAt(target, rec, t.Value, newRot, heart, territory, out _, out string err)) { message = err; return false; }
        Core.Log.LogInfo($"[Uriel SPAWN] rotated {name} to rot {newRot}.");
        message = $"Rotated {name} to rotation {newRot}.";
        return true;
    }

    /// <summary>Destroy a spawned object and re-spawn the same prefab at a new transform,
    /// preserving its indestructible state, re-adopting into <paramref name="heart"/>, and
    /// keeping the registry + live cache in sync.</summary>
    bool RespawnAt(Entity target, SpawnRecord old, float3 pos, int rot, Entity heart, int territory, out Entity result, out string error)
    {
        result = Entity.Null;
        error = null;
        PrefabGUID guid = target.GetPrefabGuid();
        var guidMap = Core.PrefabCollectionSystem._PrefabLookupMap.GuidToEntityMap;
        if (!guidMap.TryGetValue(guid, out Entity prefab) || !prefab.Exists())
        { error = "Could not re-resolve the object's prefab."; return false; }

        // Preserve the object's mode across a move/rotate re-spawn (fall back to a live Immortal probe
        // only for a record-less legacy object).
        bool playerBreakable = old?.PlayerBreakable ?? false;
        bool respawn = old?.RespawnOnDestroy ?? false;
        bool indestructible = old?.Indestructible ?? (target.TryGetComponent<Immortal>(out var im) && im.IsImmortal);
        ulong by = old?.SpawnedBySteamId ?? 0;
        int paidItem = old?.PaidCostItem ?? 0;       // a move/rotate re-spawns — never re-charge
        int paidAmount = old?.PaidCostAmount ?? 0;

        try
        {
            if (old != null) _records.Remove(old);
            DestroySpawned(target);
            Entity e = ExecuteSpawn(prefab, pos, rot, indestructible, heart, out _, playerBreakable);
            if (e == Entity.Null) { error = "Re-spawn failed (prefab did not resolve)."; SaveSync(); return false; }
            RegisterRecord(e, indestructible, territory, heart, by, paidItem, paidAmount, rot, respawn, playerBreakable);
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

    /// <summary>
    /// Shared plot-purge engine for the LIGHT (`.uriel purgeplot`) and STRONG (`.uriel forcepurgeplot`)
    /// commands. Removes Uriel's objects on a plot using ONLY signals that are unambiguously ours — it
    /// never touches a native object — applied cheapest/safest first:
    ///   (1) LIVE MARKER (`_liveSpawns`): entities Uriel spawned this run, plot-scoped by territory
    ///       blocks. Direct hit, no tile-resolution; catches an object whose registry record drifted or
    ///       was lost this session.
    ///   (2) REGISTRY (`_records`): records on this plot resolved to their live entity — the persistent
    ///       source of truth, so this is what works after a restart (when the live marker is empty until
    ///       a re-apply repopulates it). Records are dropped whether or not the entity currently resolves.
    /// Both tiers act ONLY on objects Uriel actually spawned — NEVER a broad world scan. The previously-
    /// removed heuristics (Immortal / CastleHeartConnection==heart) and the `SpawnChainChild` sweep are all
    /// gone: each matched NATIVE objects. (`SpawnChainChild` in particular is the GAME's resource-respawn
    /// marker — a `forcepurgeplot` sweep on it deleted 315 native resource nodes/trees on a tester's plot,
    /// 2026-06-09. There is no safe structural marker for an UNtracked Uriel object, so we no longer try;
    /// per-object recovery is `.uriel forcedespawn`.)
    /// </summary>
    (int marker, int tracked) PurgePlotCore(Entity heart, int territory)
    {
        var blocks = GetPlotBlocks(heart);
        int marker = 0, tracked = 0;

        // (1) Live marker set — direct, in-session. Needs territory blocks to scope to THIS plot.
        if (blocks.Count > 0)
            foreach (var e in new List<Entity>(_liveSpawns))
            {
                if (!e.Exists()) { _liveSpawns.Remove(e); continue; }
                if (!e.TryGetComponent<Translation>(out var tr)) continue;
                if (!blocks.Contains(BlockKey(ConvertPosToBlockCoord(tr.Value)))) continue;
                DestroySpawned(e); // removes it from _liveSpawns
                marker++;
            }

        // (2) Registry records on this plot — resolve + destroy (persistent; survives restart). Anything
        // already destroyed in pass 1 won't re-resolve (fresh index), so no double count.
        var index = BuildLiveIndex();
        foreach (var r in new List<SpawnRecord>(_records))
        {
            if (r.TerritoryIndex != territory) continue;
            Entity e = index.Resolve(r);
            if (e != Entity.Null && e.Exists()) { DestroySpawned(e); tracked++; }
            _records.Remove(r);
        }

        SaveSync();
        return (marker, tracked);
    }

    /// <summary>LIGHT purge — remove Uriel's objects on the plot you're standing in via the live marker +
    /// the persistent registry. Precise and safe; never touches native objects. If a stray survives (e.g.
    /// a legacy chain spawn), escalate to '.uriel forcepurgeplot'.</summary>
    public bool PurgePlot(Entity character, out string message)
    {
        if (!PublicStorageService.TryGetCharacterPosition(character, out var pos))
        { message = "Could not read your position."; return false; }
        if (!TryResolvePlot(pos, out Entity heart, out int territory))
        { message = "You're not standing in a castle plot."; return false; }

        var (marker, tracked) = PurgePlotCore(heart, territory);
        int removed = marker + tracked;
        Core.Log.LogInfo($"[Uriel SPAWN] purged {removed} object(s) ({marker} marker + {tracked} record) from territory {territory}.");
        message = removed == 0
            ? "No Uriel-spawned objects on this plot to purge. (For one specific untracked object, aim at it and use '.uriel forcedespawn'.)"
            : $"Purged {removed} Uriel-spawned object(s) from this plot. Native objects were left untouched.";
        return true;
    }

    // ============================================================ info / helpers

    public string DescribeNearest(Entity character)
    {
        if (!TryGetTargetPosition(character, out float3 pos)) return "Could not read your position.";
        var (target, rec) = NearestSpawnedRecord(pos, Settings.ObjectSpawn_MaxTargetDistance.Value);
        if (target == Entity.Null)
            return $"No Uriel-spawned object within {Settings.ObjectSpawn_MaxTargetDistance.Value:F0}m ({_records.Count} persisted). " +
                   "If you spawned it in an older build it may be untracked — admins can '.uriel forcedespawn' it.";
        var sb = new StringBuilder($"{target.GetPrefabGuid().GetPrefabName()} ({target.GetPrefabGuid()._Value})");
        if (target.TryGetComponent<Immortal>(out var im)) sb.Append($" | immortal={im.IsImmortal}");
        if (target.TryGetComponent<CastleDecayAndRegen>(out var d)) sb.Append($" | canDecay={d.CanDieFromDecay}");
        if (target.TryGetComponent<CastleHeartConnection>(out var c))
            sb.Append($" | heart={(c.CastleHeartEntity.GetEntityOnServer().Exists() ? "owned" : "unowned")}");
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

    // ============================================================ admin force-purge (records ignored)

    /// <summary>Pending '.uriel forcedespawn' confirmation (one per admin). Force-despawn ignores
    /// Uriel ownership/records entirely, so it asks for an explicit confirm naming the exact prefab
    /// first — recovering objects no record tracks (e.g. chain-era spawns) without a careless nuke.</summary>
    sealed class PendingForce
    {
        public int Guid;
        public int TileX, TileY;
        public float PosX, PosY, PosZ;
        public string Name;
        public DateTime ExpiresUtc;
    }

    readonly Dictionary<ulong, PendingForce> _pendingForce = new();
    static readonly TimeSpan ForceConfirmWindow = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Destroy a spawned object AND any spawn-chain controller that is looping it. Objects spawned
    /// in the chain-controller era are the *child* of a `Chain_*` controller whose
    /// `SpawnChainInstance.LoopOnEndOfChain` re-spawns the child the instant it dies — so destroying
    /// the child alone makes it "flash and reappear" (owner live-test, 2026-06-08). The runtime child
    /// carries `SpawnChainChild.SpawnChain` → its controller; we kill the controller first (which also
    /// tears the child down), then the child if anything remains. A normally-spawned Uriel object has
    /// no `SpawnChainChild`, so this is just a plain destroy for it.
    /// </summary>
    void DestroySpawned(Entity e)
    {
        _liveSpawns.Remove(e); // keep the live marker in sync no matter which path removes the object
        if (!e.Exists()) return;
        try
        {
            if (e.TryGetComponent<SpawnChainChild>(out var scc) && scc.SpawnChain.Exists())
            {
                Core.Log.LogInfo($"[Uriel SPAWN] destroying spawn-chain controller {scc.SpawnChain} that loops {e.GetPrefabGuid().GetPrefabName()} (stops the respawn).");
                DestroyUtility.Destroy(Core.EntityManager, scc.SpawnChain);
            }
        }
        catch (Exception ex) { Core.Log.LogWarning($"[Uriel SPAWN] chain-controller teardown failed: {ex.Message}"); }
        if (e.Exists()) DestroyUtility.Destroy(Core.EntityManager, e);
    }

    static long BlockKey(int2 b) => ((long)b.x << 32) ^ (uint)b.y;

    /// <summary>The set of territory block coords for a castle plot (via the heart's CastleTerritory),
    /// so an object's world position can be tested for plot membership without a per-object territory
    /// query. Empty set = could not resolve (caller should treat as "no members").</summary>
    HashSet<long> GetPlotBlocks(Entity heart)
    {
        var set = new HashSet<long>();
        if (!heart.TryGetComponent<CastleHeart>(out var hd)) return set;
        Entity territory = hd.CastleTerritoryEntity;
        if (!territory.Exists() || !Core.EntityManager.HasComponent<CastleTerritoryBlocks>(territory)) return set;
        var blocks = Core.EntityManager.GetBuffer<CastleTerritoryBlocks>(territory);
        for (int b = 0; b < blocks.Length; b++)
            set.Add(BlockKey(blocks[b].BlockCoordinate));
        return set;
    }

    /// <summary>Nearest placed tile object to a point, regardless of Uriel tracking. Excludes the
    /// castle heart itself (never let force-despawn nuke the heart). Disabled-included.</summary>
    Entity NearestTileObject(float3 pos, float maxDist)
    {
        float bestSq = maxDist * maxDist;
        Entity best = Entity.Null;
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(new(Il2CppType.Of<PrefabGUID>(), ComponentType.AccessMode.ReadOnly))
            .AddAll(new(Il2CppType.Of<TilePosition>(), ComponentType.AccessMode.ReadOnly))
            .AddAll(new(Il2CppType.Of<Translation>(), ComponentType.AccessMode.ReadOnly))
            .WithOptions(EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludeSpawnTag);
        var query = Core.EntityManager.CreateEntityQuery(ref builder);
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                if (e.Has<CastleHeart>()) continue; // never the heart
                if (!e.TryGetComponent<Translation>(out var t)) continue;
                float dx = t.Value.x - pos.x, dy = t.Value.y - pos.y, dz = t.Value.z - pos.z;
                float dsq = dx * dx + dy * dy + dz * dz;
                if (dsq < bestSq) { bestSq = dsq; best = e; }
            }
        }
        finally { entities.Dispose(); }
        return best;
    }

    /// <summary>
    /// Admin: force-remove the object you're aiming at / nearest you, IGNORING Uriel records and
    /// ownership — recovers objects no registry tracks (e.g. a chain-era spawn). Two-step: the first
    /// call names the exact prefab and arms a 30s confirm; <paramref name="confirm"/>=true within the
    /// window destroys it. Any matching Uriel record is also cleaned up.
    /// </summary>
    public bool ForceDespawn(Entity character, bool confirm, out string message)
    {
        ulong steamId = character.GetSteamId();
        if (!TryGetTargetPosition(character, out float3 pos)) { message = "Could not read your position."; return false; }
        float maxDist = Settings.ObjectSpawn_MaxTargetDistance.Value;

        if (confirm)
        {
            if (!_pendingForce.TryGetValue(steamId, out var pend) || pend.ExpiresUtc < DateTime.UtcNow)
            {
                _pendingForce.Remove(steamId);
                message = "Nothing armed (or it expired). Run '.uriel forcedespawn' first to target an object.";
                return false;
            }
            _pendingForce.Remove(steamId);
            // Re-resolve the exact armed object from the live world (its entity may have re-created).
            var index = BuildLiveIndex();
            Entity target = index.Resolve(new SpawnRecord
            {
                PrefabGuid = pend.Guid, TileX = pend.TileX, TileY = pend.TileY,
                PosX = pend.PosX, PosY = pend.PosY, PosZ = pend.PosZ,
            });
            if (target == Entity.Null) { message = $"The armed {pend.Name} is no longer there."; return false; }

            // Drop any Uriel record for it too, so the registry stays consistent.
            _records.RemoveAll(r => r.PrefabGuid == pend.Guid && r.TileX == pend.TileX && r.TileY == pend.TileY);
            SaveSync();
            DestroySpawned(target); // also tears down any looping spawn-chain controller
            Core.Log.LogInfo($"[Uriel SPAWN] force-despawned {pend.Name} ({pend.Guid}) at tile ({pend.TileX},{pend.TileY}) by admin {steamId}.");
            message = $"Force-removed {pend.Name}.";
            return true;
        }

        Entity nearest = NearestTileObject(pos, maxDist);
        if (nearest == Entity.Null)
        {
            message = $"No object within {maxDist:F0}m to force-remove. Aim directly at it and try again.";
            return false;
        }
        string name = nearest.GetPrefabGuid().GetPrefabName();
        nearest.TryGetComponent<TilePosition>(out var tp);
        nearest.TryGetComponent<Translation>(out var tr);
        _pendingForce[steamId] = new PendingForce
        {
            Guid = nearest.GetPrefabGuid()._Value,
            TileX = tp.Tile.x, TileY = tp.Tile.y,
            PosX = tr.Value.x, PosY = tr.Value.y, PosZ = tr.Value.z,
            Name = name,
            ExpiresUtc = DateTime.UtcNow + ForceConfirmWindow,
        };
        message = $"About to FORCE-REMOVE {name} (ignores Uriel ownership; irreversible). " +
                  "Run '.uriel forcedespawn confirm' within 30s to delete it. Re-aim and re-run to retarget.";
        return true;
    }

    /// <summary>
    /// Admin plot purge — removes Uriel's objects on the plot via the live marker + the persistent
    /// registry, exactly like '.uriel purgeplot'. (Retained as a separate command for muscle memory; the
    /// old "STRONG" `SpawnChainChild` sweep was REMOVED — that component is the GAME's resource-respawn
    /// marker, not a Uriel tag, and the sweep destroyed 315 native resource nodes/trees on a tester's plot,
    /// 2026-06-09. There is no safe way to bulk-remove an UNtracked Uriel object; aim at one specific object
    /// and use '.uriel forcedespawn' instead.)
    /// </summary>
    public bool ForcePurgePlot(Entity character, out string message)
    {
        if (!PublicStorageService.TryGetCharacterPosition(character, out var pos))
        { message = "Could not read your position."; return false; }
        if (!TryResolvePlot(pos, out Entity heart, out int territory))
        { message = "You're not standing in a castle plot."; return false; }

        var (marker, tracked) = PurgePlotCore(heart, territory);
        int removed = marker + tracked;
        Core.Log.LogInfo($"[Uriel SPAWN] force-purged {removed} object(s) ({marker} marker + {tracked} record) from territory {territory} by admin {character.GetSteamId()}.");
        message = removed == 0
            ? "No Uriel objects found on this plot (nothing native was touched). For one specific untracked object, aim at it and use '.uriel forcedespawn'."
            : $"Removed {removed} Uriel object(s) on this plot ({marker} marker + {tracked} record). Native objects, plants, trees, and build pieces were left untouched.";
        return true;
    }

    /// <summary>
    /// Admin SERVER-WIDE cleanup: scan EVERY Uriel-spawned object (registry-driven) and remove any that is
    /// ORPHANED — sitting where no LIVING castle heart governs it (its castle was destroyed/decayed, or it's
    /// otherwise in open world / on a plot with no heart). This is the manual, on-demand backup for the
    /// automatic boot-time orphan purge (`ObjectSpawn.PurgeOrphansOnBoot`), for an admin to run anytime.
    ///
    /// Safe by construction: it iterates only Uriel's own records, so native world objects are never
    /// touched. A streamed-out object (entity not resolvable right now) is LEFT ALONE with its record kept —
    /// it may simply not be loaded — exactly like the boot purge; re-run after regions load if needed. As a
    /// guard against a query glitch nuking everything, it ABORTS if it can't resolve any living castle plots.
    /// </summary>
    public bool PurgeOrphans(Entity character, out string message)
    {
        if (!PurgeOrphansCore(out int removed, out int loaded, out int unresolved, out int heartCount, out int livingBlockCount))
        {
            message = $"Aborted — couldn't resolve any living castle plots ({heartCount} heart(s) seen). " +
                      "Nothing was removed (safety guard). Try again once castles are loaded.";
            return false;
        }
        Core.Log.LogInfo($"[Uriel SPAWN] server-wide orphan scan by admin {character.GetSteamId()}: removed {removed} orphan(s) of {loaded} loaded object(s); {unresolved} not loaded (kept). {livingBlockCount} living plot block(s), {heartCount} heart(s).");
        string tail = unresolved > 0 ? $" {unresolved} object(s) weren't loaded and were skipped — re-run after they load if needed." : "";
        message = removed == 0
            ? $"Orphan scan complete — no orphaned Uriel objects found ({loaded} checked).{tail}"
            : $"Orphan scan complete — removed {removed} orphaned Uriel object(s) (castle gone / no living heart governing them); {loaded} checked. Native objects were never touched.{tail}";
        return true;
    }

    /// <summary>
    /// Shared orphan-scan engine for the manual command (<c>.uriel purgeorphans</c>) and the periodic
    /// mid-session sweep (<see cref="OrphanSweepTick"/>). Registry-driven, so ONLY Uriel's own objects can
    /// ever be removed — native world objects are never at risk. Returns false (and removes nothing) if the
    /// living-plot set comes back empty (a query glitch — a server with Uriel objects always has ≥1 heart),
    /// the same safety abort the manual command relied on.
    /// </summary>
    bool PurgeOrphansCore(out int removed, out int loaded, out int unresolved, out int heartCount, out int livingBlockCount)
    {
        removed = loaded = unresolved = 0;
        // Collect the block coords of every plot that still has a LIVING heart. Disabled-included, so a
        // streamed-out-but-alive castle still counts (its objects are NOT orphans). A genuinely destroyed
        // heart is absent from the query, so its plot's blocks won't be in the set.
        var livingBlocks = new HashSet<long>();
        var heartBuilder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(new(Il2CppType.Of<CastleHeart>(), ComponentType.AccessMode.ReadOnly))
            .WithOptions(EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludeSpawnTag);
        var heartQuery = Core.EntityManager.CreateEntityQuery(ref heartBuilder);
        var hearts = heartQuery.ToEntityArray(Allocator.Temp);
        try
        {
            heartCount = hearts.Length;
            for (int i = 0; i < hearts.Length; i++)
                foreach (var b in GetPlotBlocks(hearts[i])) livingBlocks.Add(b);
        }
        finally { hearts.Dispose(); }

        livingBlockCount = livingBlocks.Count;
        if (livingBlocks.Count == 0) return false; // safety abort — treat everything as NOT orphaned

        var index = BuildLiveIndex();
        foreach (var r in new List<SpawnRecord>(_records))
        {
            Entity e = index.Resolve(r);
            if (e == Entity.Null) { unresolved++; continue; } // not loaded right now — keep, may stream in
            loaded++;
            float3 pos = e.TryGetComponent<Translation>(out var tr) ? tr.Value : new float3(r.PosX, r.PosY, r.PosZ);
            if (!livingBlocks.Contains(BlockKey(ConvertPosToBlockCoord(pos)))) // no living heart governs it
            {
                DestroySpawned(e);
                _records.Remove(r);
                removed++;
            }
        }
        if (removed > 0) SaveSync();
        return true;
    }

    // ============================================================ periodic orphan sweep

    /// <summary>Start the mid-session orphan sweep (called once at boot). No-op when disabled by config or
    /// when the tick driver isn't running. Cleans up objects on a castle that is abandoned/destroyed
    /// DURING a session, rather than waiting for the next boot's <c>PurgeOrphansOnBoot</c>.</summary>
    public void StartOrphanSweepLoop()
    {
        if (!Settings.ObjectSpawn_AutoPurgeOrphans.Value) return;
        if (!Tick.IsRunning) { Core.Log.LogWarning("[Uriel SPAWN] orphan sweep NOT started (tick driver unavailable)."); return; }
        int seconds = Math.Max(30, Settings.ObjectSpawn_OrphanPollSeconds.Value);
        Tick.RunRepeating(seconds * 60, OrphanSweepTick); // frames ≈ seconds × server fps; approximate cadence is fine
        Core.Log.LogInfo($"[Uriel SPAWN] mid-session orphan sweep started (~{seconds}s cadence).");
    }

    /// <summary>One periodic sweep — purge orphaned objects whose castle is gone. Quiet unless it removes
    /// something (so it doesn't spam the log every cycle).</summary>
    void OrphanSweepTick()
    {
        if (!Settings.ObjectSpawn_AutoPurgeOrphans.Value) return;
        if (_records.Count == 0) return;
        if (PurgeOrphansCore(out int removed, out _, out _, out _, out _) && removed > 0)
            Core.Log.LogInfo($"[Uriel SPAWN] mid-session orphan sweep removed {removed} object(s) on abandoned/destroyed castle(s).");
    }

    // ============================================================ admin spawn conditions (request 2)

    /// <summary>Admin: set/clear a PER-OBJECT spawn condition (resolved prefab). Backs '.uriel objcfg'.</summary>
    public bool ConfigureObject(string prefabRef, string field, string v1, string v2, out string message)
    {
        if (!TryResolvePrefab(prefabRef, out PrefabGUID guid, out _, out string name, out string err))
        { message = err; return false; }
        return ApplyCondition(guid._Value, name, field, v1, v2, out message);
    }

    /// <summary>Admin: set/clear a GLOBAL default spawn condition (applies to every object unless that
    /// object overrides the field). Backs '.uriel objcfgglobal'.</summary>
    public bool ConfigureGlobal(string field, string v1, string v2, out string message)
        => ApplyCondition(null, "Global default", field, v1, v2, out message);

    public string DescribeAllConditions() => Clamp(Core.ObjectConditions.DescribeAll());

    /// <summary>Parse + apply one condition field for a layer (guid==null ⇒ global). Shared by both commands.</summary>
    bool ApplyCondition(int? guid, string label, string field, string v1, string v2, out string message)
    {
        switch (field?.Trim().ToLowerInvariant())
        {
            case null:
            case "":
            case "show":
                message = Core.ObjectConditions.DescribeLayer(guid, label);
                return true;

            case "max":
            case "maxbaseunits":
            case "maxperplot":
                if (!int.TryParse(v1?.Trim(), out int max)) { message = "Usage: max <number> (0 = unlimited)."; return false; }
                Core.ObjectConditions.SetMax(guid, max);
                message = max > 0 ? $"{label}: max {max} per plot." : $"{label}: per-plot limit cleared (unlimited).";
                return true;

            case "cost":
                // cost <amount> <itemGuid>   (amount 0 OR item 0 ⇒ free / cleared)
                if (!int.TryParse(v1?.Trim(), out int amount)) { message = "Usage: cost <amount> <itemGuid> (amount 0 = free)."; return false; }
                int item = 0;
                if (amount > 0)
                {
                    if (!int.TryParse(v2?.Trim(), out item) || item == 0)
                    { message = "Usage: cost <amount> <itemGuid> — give the item's PrefabGUID (e.g. '.uriel finditem ...')."; return false; }
                }
                Core.ObjectConditions.SetCost(guid, amount > 0 ? item : (int?)null, amount);
                message = amount > 0
                    ? $"{label}: costs {amount}x {new PrefabGUID(item).GetPrefabName()} ({item})."
                    : $"{label}: spawning is free (cost cleared).";
                return true;

            case "indestructible":
            case "permitindestructible":
                if (!TryParseBoolOrClear(v1, out bool? pi)) { message = "Usage: indestructible <true|false|clear>."; return false; }
                Core.ObjectConditions.SetPermitIndestructible(guid, pi);
                message = $"{label}: indestructible {(pi is null ? "uses default (allowed)" : (pi.Value ? "allowed" : "denied — forced breakable"))}.";
                return true;

            case "respawn":
            case "permitrespawn":
                if (!TryParseBoolOrClear(v1, out bool? pr)) { message = "Usage: respawn <true|false|clear>."; return false; }
                Core.ObjectConditions.SetPermitRespawn(guid, pr);
                message = $"{label}: respawn {(pr is null ? "uses default (allowed)" : (pr.Value ? "allowed" : "denied"))}.";
                return true;

            case "clear":
            case "reset":
                Core.ObjectConditions.Clear(guid);
                message = $"{label}: all conditions cleared (back to defaults).";
                return true;

            default:
                message = "Fields: max <n> | cost <amount> <itemGuid> | indestructible <true|false> | respawn <true|false> | clear | show.";
                return false;
        }
    }

    static bool TryParseBoolOrClear(string s, out bool? value)
    {
        value = null;
        s = s?.Trim().ToLowerInvariant();
        switch (s)
        {
            case "clear": case "reset": case "default": case "unset": value = null; return true;
            case "true": case "on": case "yes": case "1": case "allow": case "allowed": value = true; return true;
            case "false": case "off": case "no": case "0": case "deny": case "denied": value = false; return true;
            default: return false;
        }
    }
}
