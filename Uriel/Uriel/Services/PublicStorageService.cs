using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Shared;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Uriel.Config;

namespace Uriel.Services;

/// <summary>
/// Per-container public sharing — the mod's first feature (see
/// docs/features/PUBLIC_STORAGE.md).
///
/// MECHANISM (v0.8.0 — full neutral recipe, after live testing proved a team
/// change alone is NOT enough): the client gates the interact prompt on the
/// container being an (enemy) CASTLE container, so with the typical
/// CanLootEnemyContainers=false server setting a team-swapped chest stayed
/// unclickable. Sharing now applies the complete KindredSchematics
/// public-build recipe: Team/TeamReference ← the game's NeutralTeam singleton
/// AND CastleHeartConnection ← Entity.Null. Because the heart link is severed
/// while shared, the heart is remembered in the registry entry by its TILE
/// (HasHeartTile/HeartTileX/Y) for ownership checks (IsController) and
/// unshare-restore (reconnect + sibling-team restore).
///
/// PERSISTENCE: registry entries are keyed by prefab GUID + TilePosition tile
/// coords (stable across save/load; entity ids and NetworkIds are not). The
/// neutral state is re-applied to registered containers at every server init.
/// Limitation (documented): moving a shared container via castle edit changes
/// its tile and strands the entry — it simply reverts to private on restart.
/// </summary>
internal sealed class PublicStorageService
{
    internal sealed class PublicContainerEntry
    {
        public int PrefabGuid { get; set; }
        public int TileX { get; set; }
        public int TileY { get; set; }
        public string ContainerClass { get; set; } = "storage"; // "storage" | "prison" (prison not yet implemented)
        public ulong SharedBySteamId { get; set; }
        public string SharedAtUtc { get; set; }

        // ---- castle heart anchor (v0.8.0) ----
        // While shared, the container's CastleHeartConnection is SEVERED (the
        // client gates the interact prompt on it), so the heart is remembered
        // here by its tile for ownership checks and unshare-restore.
        public bool HasHeartTile { get; set; }
        public int HeartTileX { get; set; }
        public int HeartTileY { get; set; }

        // ---- policy modifiers (schema v2) ----
        /// <summary>"take" (withdraw only) | "give" (donation box) | "givetake" (both, default).</summary>
        public string Permission { get; set; } = "givetake";
        /// <summary>Stacks a non-controller may withdraw per window; 0 = unlimited.</summary>
        public int LimitWithdrawStacks { get; set; }
        /// <summary>Rolling window length in hours for the withdrawal limit; 0 = no window.</summary>
        public double LimitHours { get; set; }
        /// <summary>Item required per stack withdrawn; 0 = free.</summary>
        public int CostItemGuid { get; set; }
        public int CostAmount { get; set; }
        /// <summary>Per-player usage tracking (key = steamId as string for JSON).</summary>
        public Dictionary<string, UsageRecord> Usage { get; set; } = new();

        public bool HasRestrictions =>
            Permission != "givetake" || LimitWithdrawStacks > 0 || LimitHours > 0 || CostItemGuid != 0;
    }

    internal sealed class UsageRecord
    {
        public string WindowStartUtc { get; set; }
        public int StacksTaken { get; set; }
    }

    /// <summary>Owner-designated private container that receives cost payments.</summary>
    internal sealed class PayChestRef
    {
        public int PrefabGuid { get; set; }
        public int TileX { get; set; }
        public int TileY { get; set; }
    }

    sealed class SaveFile
    {
        public int SchemaVersion { get; set; } = 2;
        public List<PublicContainerEntry> Entries { get; set; } = new();
        public Dictionary<string, PayChestRef> PayChests { get; set; } = new(); // key = owner steamId
    }

    readonly List<PublicContainerEntry> _entries = new();
    readonly Dictionary<string, PayChestRef> _payChests = new();

    // Known world-chest prefab GUIDs (from the prefab dump) — neutral-team donors.
    static readonly int[] WorldChestGuids =
    {
        -375592800,  // TM_WorldChest_Simple_01_Empty
        240964190,   // TM_WorldChest_Simple_01_Full
        -273515249,  // TM_WorldChest_Iron_01_Empty
        257686919,   // TM_WorldChest_Iron_01_Full
        -321007732,  // TM_WorldChest_Epic_01_Empty
        -1657744516, // TM_WorldChest_Epic_01_Full
        -241680783,  // TM_WorldChest_Simple_GloomRot_01_Empty
        -1576310588, // TM_WorldChest_Simple_GloomRot_01_Full
        866003444,   // TM_WorldChest_Simple_SludgePools_01_Empty
        -1203166929, // TM_WorldChest_Simple_SludgePools_01_Full
    };

    bool _donorResolved;
    Team _donorTeam;
    Entity _donorTeamRefEntity;
    Entity _neutralTeamEntity; // the game's NeutralTeam singleton (preferred public-team source, v0.8.0)

    static string SaveDir => Path.Combine(BepInEx.Paths.ConfigPath, "Uriel");
    static string SavePath => Path.Combine(SaveDir, "public_containers.json");

    public IReadOnlyList<PublicContainerEntry> Entries => _entries;

    // ---------------------------------------------------------------- persistence

    public void Load()
    {
        try
        {
            if (!File.Exists(SavePath)) return;
            var file = JsonSerializer.Deserialize<SaveFile>(File.ReadAllText(SavePath));
            if (file?.Entries is null) return;
            _entries.Clear();
            _entries.AddRange(file.Entries);
            _payChests.Clear();
            if (file.PayChests is not null)
                foreach (var kvp in file.PayChests) _payChests[kvp.Key] = kvp.Value;
            // v1 → v2 migration is implicit: missing policy fields deserialize to defaults
            // (givetake, no limits, no cost) and the next save writes schema v2.
            Core.Log.LogInfo($"[Uriel SHARE] loaded {_entries.Count} public-container entry(ies), {_payChests.Count} pay chest(s).");
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[Uriel SHARE] failed loading {SavePath}: {ex}");
        }
    }

    // Registry changes happen at command frequency — save synchronously on every change.
    public void SaveSync()
    {
        try
        {
            Directory.CreateDirectory(SaveDir);
            var json = JsonSerializer.Serialize(
                new SaveFile { Entries = _entries, PayChests = new Dictionary<string, PayChestRef>(_payChests) },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SavePath, json);
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[Uriel SHARE] failed saving {SavePath}: {ex}");
        }
    }

    // ---------------------------------------------------------------- targeting

    /// <summary>
    /// Resolve the placed castle container the player is aiming at: closest
    /// entity with InventoryOwner + TilePosition + CastleHeartConnection within
    /// MaxTargetDistance of the aim position (KindredCommands aim pattern).
    /// </summary>
    public Entity ResolveTargetContainer(Entity character, out string error)
    {
        error = null;
        if (!character.TryGetComponent<EntityAimData>(out var aimData))
        {
            error = "Could not read your aim position.";
            return Entity.Null;
        }
        var aimPos = aimData.AimPosition;
        float maxDist = Settings.PublicStorage_MaxTargetDistance.Value;
        float maxDistSq = maxDist * maxDist;

        var query = Core.EntityManager.CreateEntityQuery(
            ComponentType.ReadOnly<InventoryOwner>(),
            ComponentType.ReadOnly<TilePosition>(),
            ComponentType.ReadOnly<Unity.Transforms.Translation>());
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            Entity closest = Entity.Null;
            float closestSq = float.MaxValue;
            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                if (!e.Has<CastleHeartConnection>()) continue; // placed castle objects only
                if (!e.TryGetComponent<Unity.Transforms.Translation>(out var t)) continue;
                var p = t.Value;
                float dx = p.x - aimPos.x, dy = p.y - aimPos.y, dz = p.z - aimPos.z;
                float dsq = dx * dx + dy * dy + dz * dz;
                if (dsq < closestSq && dsq <= maxDistSq)
                {
                    closestSq = dsq;
                    closest = e;
                }
            }
            if (closest == Entity.Null)
                error = $"No castle container found within {maxDist:F0}m of where you're aiming. Stand close and aim at it.";
            return closest;
        }
        finally
        {
            entities.Dispose();
        }
    }

    /// <summary>Container classification — prison cells and coffins are NOT plain storage.</summary>
    public static string ClassifyContainer(Entity container)
    {
        if (container.Has<Prisonstation>()) return "prison";
        if (container.Has<ServantCoffinstation>()) return "coffin";
        return "storage";
    }

    // ---------------------------------------------------------------- team plumbing

    /// <summary>
    /// Find a world chest and cache its Team/TeamReference as the neutral
    /// "public" team. Resolved lazily (entities must exist; init order safe).
    ///
    /// v0.2.1: world chests carry DisableWhenNoPlayersInRange, so they sit
    /// Disabled whenever no player is nearby — and DEFAULT EntityQueries skip
    /// disabled entities, which made this lookup fail on a live server. The
    /// query now includes Disabled/SpawnTag entities (KindredCommands pattern),
    /// and if no placed world chest is found at all, we fall back to the
    /// world chest PREFAB entity from the prefab lookup map, which always
    /// exists and carries the same neutral Team/TeamReference defaults.
    /// </summary>
    bool TryResolveDonorTeam()
    {
        if (_donorResolved && (_donorTeamRefEntity == Entity.Null || _donorTeamRefEntity.Exists()))
            return true;
        _donorResolved = false;

        // Pass 1: a placed world chest in the live world (include disabled).
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(new(Il2CppType.Of<PrefabGUID>(), ComponentType.AccessMode.ReadOnly))
            .AddAll(new(Il2CppType.Of<Team>(), ComponentType.AccessMode.ReadOnly))
            .AddAll(new(Il2CppType.Of<TeamReference>(), ComponentType.AccessMode.ReadOnly))
            .WithOptions(EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludeSpawnTag);
        var query = Core.EntityManager.CreateEntityQuery(ref builder);
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            var donorGuids = new HashSet<int>(WorldChestGuids);
            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                if (!e.TryGetComponent<PrefabGUID>(out var guid)) continue;
                if (!donorGuids.Contains(guid._Value)) continue;
                if (!e.TryGetComponent<Team>(out var team)) continue;
                if (!e.TryGetComponent<TeamReference>(out var teamRef)) continue;
                _donorTeam = team;
                _donorTeamRefEntity = teamRef.Value._Value;
                _donorResolved = true;
                Core.Log.LogInfo($"[Uriel SHARE] neutral-team donor: live {guid.GetPrefabName()} (Team.Value={team.Value}, ref={_donorTeamRefEntity}).");
                return true;
            }
        }
        finally
        {
            entities.Dispose();
        }

        // Pass 2: the prefab entity itself (always present in the lookup map).
        foreach (int guidValue in WorldChestGuids)
        {
            var guid = new PrefabGUID(guidValue);
            if (Core.PrefabCollectionSystem._PrefabLookupMap.TryGetValue(guid, out Entity prefabEntity)
                && prefabEntity.TryGetComponent<Team>(out var team)
                && prefabEntity.TryGetComponent<TeamReference>(out var teamRef))
            {
                _donorTeam = team;
                _donorTeamRefEntity = teamRef.Value._Value;
                _donorResolved = true;
                Core.Log.LogInfo($"[Uriel SHARE] neutral-team donor: PREFAB {guid.GetPrefabName()} (Team.Value={team.Value}, ref={_donorTeamRefEntity}).");
                return true;
            }
        }

        Core.Log.LogWarning("[Uriel SHARE] no world chest found (live or prefab) to donate a neutral team — sharing unavailable this session.");
        return false;
    }

    /// <summary>The game's NeutralTeam singleton entity (KindredSchematics' public-build team source).</summary>
    bool TryResolveNeutralTeamEntity(out Entity neutralTeam)
    {
        if (_neutralTeamEntity.Exists()) { neutralTeam = _neutralTeamEntity; return true; }
        neutralTeam = Entity.Null;
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(new(Il2CppType.Of<NeutralTeam>(), ComponentType.AccessMode.ReadOnly))
            .WithOptions(EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludeSpawnTag);
        var query = Core.EntityManager.CreateEntityQuery(ref builder);
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            for (int i = 0; i < entities.Length; i++)
            {
                if (!entities[i].Has<TeamData>()) continue;
                _neutralTeamEntity = entities[i];
                neutralTeam = entities[i];
                if (Settings.VerboseLogging.Value)
                    Core.Log.LogInfo($"[Uriel SHARE] NeutralTeam singleton resolved: {entities[i]}.");
                return true;
            }
        }
        finally
        {
            entities.Dispose();
        }
        return false;
    }

    public bool CanResolvePublicTeam() =>
        TryResolveNeutralTeamEntity(out _) || TryResolveDonorTeam();

    /// <summary>The Team values + TeamReference entity to use for PUBLIC containers.</summary>
    bool TryGetPublicTeam(out int teamValue, out int factionIndex, out Entity teamRefEntity)
    {
        if (TryResolveNeutralTeamEntity(out Entity neutralTeam)
            && neutralTeam.TryGetComponent<TeamData>(out var teamData))
        {
            teamValue = teamData.TeamValue;
            factionIndex = -1;
            teamRefEntity = neutralTeam;
            return true;
        }
        if (TryResolveDonorTeam())
        {
            teamValue = _donorTeam.Value;
            factionIndex = _donorTeam.FactionIndex;
            teamRefEntity = _donorTeamRefEntity;
            return true;
        }
        teamValue = 0; factionIndex = 0; teamRefEntity = Entity.Null;
        return false;
    }

    /// <summary>
    /// Force connected clients to refresh this entity (v0.8.2). The v0.8.1
    /// UpToDateUserBitMask clear was NOT sufficient: the sharedebug dump proved
    /// the server state perfect while the stranger's client kept casting the
    /// DisabledDummy interact — clients only re-evaluate a container's
    /// interactability when the entity is (re)streamed to them, which is why
    /// boot-applied shares always worked. So: blink the entity through the
    /// game's own streaming path — Disabled now, re-enabled a few frames later
    /// (the proximity streamer may even re-enable it first; both paths are
    /// guarded). Clients drop the entity and re-receive it with fresh state.
    /// </summary>
    static void ForceResync(Entity entity)
    {
        if (entity.Has<ProjectM.Network.UpToDateUserBitMask>())
            entity.With((ref ProjectM.Network.UpToDateUserBitMask m) => m.Value = default);
        try
        {
            if (!entity.Has<Disabled>())
            {
                Core.EntityManager.AddComponent<Disabled>(entity);
                Entity captured = entity;
                Tick.RunLater(3, () =>
                {
                    if (captured.Exists() && captured.Has<Disabled>())
                        Core.EntityManager.RemoveComponent<Disabled>(captured);
                });
                if (Settings.VerboseLogging.Value)
                    Core.Log.LogInfo($"[Uriel SHARE] resync blink: {entity.GetPrefabGuid().GetPrefabName()}.");
            }
        }
        catch (Exception ex)
        {
            Core.Log.LogWarning($"[Uriel SHARE] resync blink failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Make the container PUBLIC (v0.8.0 — full KindredSchematics neutral recipe):
    ///  1. remember the castle heart by tile (entry) — connection is about to go;
    ///  2. Team/TeamReference ← the game's NeutralTeam singleton (fallback: a
    ///     world chest's team);
    ///  3. CastleHeartConnection ← Entity.Null. Live testing (2026-06-07) proved a
    ///     team change ALONE leaves the chest "an enemy castle container" — the
    ///     client refuses the interact prompt when CanLootEnemyContainers is off.
    /// </summary>
    /// <summary>Remember the container's castle heart (by tile) in the entry while its connection is still intact.</summary>
    void CaptureHeartAnchor(Entity container, PublicContainerEntry entry)
    {
        if (container.TryGetComponent<CastleHeartConnection>(out var conn))
        {
            Entity heart = conn.CastleHeartEntity.GetEntityOnServer();
            if (heart.Exists() && heart.TryGetComponent<TilePosition>(out var heartTile))
            {
                entry.HasHeartTile = true;
                entry.HeartTileX = heartTile.Tile.x;
                entry.HeartTileY = heartTile.Tile.y;
            }
        }
    }

    void ApplyPublicTeam(Entity container, PublicContainerEntry entry)
    {
        // 1. capture the heart anchor while the connection still exists
        CaptureHeartAnchor(container, entry);

        // 2. neutral team
        if (TryGetPublicTeam(out int teamValue, out int factionIndex, out Entity teamRefEntity))
        {
            container.With((ref Team t) => { t.Value = teamValue; t.FactionIndex = factionIndex; });
            container.With((ref TeamReference tr) => tr.Value._Value = teamRefEntity);
        }

        // 3. sever the castle link (the client-side gate)
        if (container.Has<CastleHeartConnection>())
            container.With((ref CastleHeartConnection c) => c.CastleHeartEntity = Entity.Null);

        // 4. prison cells: the subdue/charm interaction validates against the
        //    PRISONER's own team, not just the cell's (v0.8.1 live-test finding:
        //    a stranger could open a shared cell but not take the prisoner) —
        //    neutralize the imprisoned unit too.
        if (container.TryGetComponent<PrisonCell>(out var prisonCell))
        {
            Entity prisoner = prisonCell.ImprisonedEntity.GetEntityOnServer();
            if (prisoner.Exists() && prisoner.Has<Team>())
            {
                prisoner.With((ref Team t) => { t.Value = teamValue; t.FactionIndex = factionIndex; });
                if (prisoner.Has<TeamReference>())
                    prisoner.With((ref TeamReference tr) => tr.Value._Value = teamRefEntity);
                ForceResync(prisoner);
            }
        }

        // 5. push the new state to already-connected clients
        ForceResync(container);
    }

    /// <summary>
    /// The castle heart governing a container: via its live connection when
    /// present, else via the heart tile remembered in the registry entry
    /// (shared containers have a severed connection).
    /// </summary>
    Entity ResolveHeartFor(Entity container, PublicContainerEntry entry)
    {
        if (container.TryGetComponent<CastleHeartConnection>(out var conn))
        {
            Entity heart = conn.CastleHeartEntity.GetEntityOnServer();
            if (heart.Exists()) return heart;
        }
        if (entry is not null && entry.HasHeartTile)
            return FindHeartByTile(entry.HeartTileX, entry.HeartTileY);
        return Entity.Null;
    }

    static Entity FindHeartByTile(int x, int y)
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
                if (tile.Tile.x == x && tile.Tile.y == y) return entities[i];
            }
        }
        finally
        {
            entities.Dispose();
        }
        return Entity.Null;
    }

    /// <summary>Does this character's team control the container's castle (works for SHARED containers via the heart anchor)?</summary>
    public bool IsController(Entity character, Entity container, PublicContainerEntry entry)
    {
        Entity heart = ResolveHeartFor(container, entry);
        if (!heart.TryGetComponent<Team>(out var heartTeam)) return false;
        if (!character.TryGetComponent<Team>(out var charTeam)) return false;
        return charTeam.Value == heartTeam.Value;
    }

    /// <summary>Route to the right restore for the container class (v0.9.0): storage rebuilds (fresh entity → clients refresh); prison cells mutate + blink.</summary>
    bool RestoreToPrivate(Entity container, PublicContainerEntry entry, out string error)
    {
        if (entry.ContainerClass == "storage")
            return RebuildContainer(container, entry, makePublic: false, out error);
        return RestoreCastleTeam(container, entry, out error);
    }

    /// <summary>
    /// Restore the container on unshare (v0.8.0): RECONNECT the castle heart
    /// (resolved via the live connection or the registry's heart anchor), then
    /// restore the team — preferred donor: a sibling private container on the
    /// same heart; fallback: the heart itself. Logs what was restored.
    /// </summary>
    bool RestoreCastleTeam(Entity container, PublicContainerEntry entry, out string error)
    {
        error = null;
        Entity heart = ResolveHeartFor(container, entry);
        if (!heart.Exists())
        {
            error = "Castle heart not found (was the castle destroyed?); cannot restore.";
            return false;
        }

        // Reconnect the heart link severed while shared.
        if (container.Has<CastleHeartConnection>())
            container.With((ref CastleHeartConnection c) => c.CastleHeartEntity = heart);

        // Preferred donor: sibling private container on the same heart.
        Entity donor = FindSiblingTeamDonor(container, heart);
        if (donor == Entity.Null) donor = heart; // fallback

        if (!donor.TryGetComponent<Team>(out var donorTeam)
            || !donor.TryGetComponent<TeamReference>(out var donorRef))
        {
            error = "No team source found to restore from; cannot restore.";
            return false;
        }
        var refEntity = donorRef.Value._Value;
        container.With((ref Team t) => { t.Value = donorTeam.Value; t.FactionIndex = donorTeam.FactionIndex; });
        container.With((ref TeamReference tr) => tr.Value._Value = refEntity);

        // Prison cells: restore the prisoner's team alongside the cell's (it was
        // neutralized on share so strangers could subdue/charm them out).
        if (container.TryGetComponent<PrisonCell>(out var prisonCell))
        {
            Entity prisoner = prisonCell.ImprisonedEntity.GetEntityOnServer();
            if (prisoner.Exists() && prisoner.Has<Team>())
            {
                prisoner.With((ref Team t) => { t.Value = donorTeam.Value; t.FactionIndex = donorTeam.FactionIndex; });
                if (prisoner.Has<TeamReference>())
                    prisoner.With((ref TeamReference tr) => tr.Value._Value = refEntity);
                ForceResync(prisoner);
            }
        }

        ForceResync(container); // push restored state to connected clients
        Core.Log.LogInfo($"[Uriel SHARE] unshare: reconnected heart + restored team on {container.GetPrefabGuid().GetPrefabName()} from {(donor == heart ? "castle heart" : "sibling container")} (Team.Value={donorTeam.Value}).");
        return true;
    }

    /// <summary>A private (non-registered) container on the same castle heart, to copy an authentic placed-chest team from.</summary>
    Entity FindSiblingTeamDonor(Entity container, Entity heart)
    {
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(new(Il2CppType.Of<InventoryOwner>(), ComponentType.AccessMode.ReadOnly))
            .AddAll(new(Il2CppType.Of<TilePosition>(), ComponentType.AccessMode.ReadOnly))
            .AddAll(new(Il2CppType.Of<CastleHeartConnection>(), ComponentType.AccessMode.ReadOnly))
            .WithOptions(EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludeSpawnTag);
        var query = Core.EntityManager.CreateEntityQuery(ref builder);
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                if (e == container) continue;
                if (!e.TryGetComponent<CastleHeartConnection>(out var c)
                    || c.CastleHeartEntity.GetEntityOnServer() != heart) continue;
                if (FindEntry(e) is not null) continue; // shared siblings carry the neutral team — skip
                if (!e.Has<Team>() || !e.Has<TeamReference>()) continue;
                return e;
            }
        }
        finally
        {
            entities.Dispose();
        }
        return Entity.Null;
    }

    /// <summary>Does this character belong to the team that currently controls the container's castle?</summary>
    public static bool CharacterControlsContainer(Entity character, Entity container)
    {
        // Authoritative comparison is against the castle heart's team (the container's
        // own Team may already be the neutral donor team when shared).
        if (!container.TryGetComponent<CastleHeartConnection>(out var conn)) return false;
        Entity heart = conn.CastleHeartEntity.GetEntityOnServer();
        if (!heart.TryGetComponent<Team>(out var heartTeam)) return false;
        if (!character.TryGetComponent<Team>(out var charTeam)) return false;
        return charTeam.Value == heartTeam.Value;
    }

    // ---------------------------------------------------------------- registry ops

    PublicContainerEntry FindEntry(Entity container)
    {
        if (!container.TryGetComponent<TilePosition>(out var tile)) return null;
        int guid = container.GetPrefabGuid()._Value;
        foreach (var e in _entries)
        {
            if (e.PrefabGuid == guid && e.TileX == tile.Tile.x && e.TileY == tile.Tile.y)
                return e;
        }
        return null;
    }

    public bool IsShared(Entity container) => FindEntry(container) is not null;

    public bool Share(Entity character, Entity container, out string message, bool isAdmin = false)
    {
        string cls = ClassifyContainer(container);
        if (cls == "prison" && !Settings.PublicPrison_Enabled.Value)
        {
            message = "Prison-cell sharing is disabled by the server admin (PublicStorage.PrisonEnabled).";
            return false;
        }
        if (cls == "coffin")
        {
            message = "Servant coffins can't be shared.";
            return false;
        }
        if (!isAdmin && !CharacterControlsContainer(character, container))
        {
            message = "You don't control this container (its castle isn't yours/your clan's).";
            return false;
        }
        if (FindEntry(container) is not null)
        {
            message = "That container is already public.";
            return false;
        }
        if (!CanResolvePublicTeam())
        {
            message = "Sharing unavailable: no neutral team source found on this map (see server log).";
            return false;
        }
        if (!container.TryGetComponent<TilePosition>(out var tile))
        {
            message = "Container has no tile position; cannot register it.";
            return false;
        }

        var newEntry = new PublicContainerEntry
        {
            PrefabGuid = container.GetPrefabGuid()._Value,
            TileX = tile.Tile.x,
            TileY = tile.Tile.y,
            ContainerClass = cls,
            SharedBySteamId = character.GetSteamId(),
            SharedAtUtc = DateTime.UtcNow.ToString("u"),
        };
        CaptureHeartAnchor(container, newEntry);
        if (cls == "storage")
        {
            // v0.9.0: REBUILD as a fresh entity — the only thing that reliably makes
            // connected clients re-evaluate interactability (mutate+blink wasn't enough).
            if (!RebuildContainer(container, newEntry, makePublic: true, out string rbErr))
            {
                message = $"Sharing failed: {rbErr}";
                return false;
            }
        }
        else
        {
            ApplyPublicTeam(container, newEntry); // prison cells: mutate + blink (rebuild is unsafe with a prisoner bound)
        }
        _entries.Add(newEntry);
        SaveSync();
        Core.Log.LogInfo($"[Uriel SHARE] shared {container.GetPrefabGuid().GetPrefabName()} at ({tile.Tile.x},{tile.Tile.y}) class={cls} by {character.GetSteamId()}.");
        message = cls == "prison"
            ? $"{container.GetPrefabGuid().GetPrefabName()} is now PUBLIC — anyone can tend the prisoner (feed, extract blood) or charm them out as their own subdued follower. '.uriel unshare' to revert."
            : $"{container.GetPrefabGuid().GetPrefabName()} is now PUBLIC — anyone on the server can use it. Aim at it and use '.uriel unshare' to revert.";
        return true;
    }

    public bool Unshare(Entity character, Entity container, bool isAdmin, out string message)
    {
        var entry = FindEntry(container);
        if (entry is null)
        {
            message = "That container isn't currently public.";
            return false;
        }
        if (!isAdmin && !IsController(character, container, entry)
            && character.GetSteamId() != entry.SharedBySteamId)
        {
            message = "Only the container's controllers (or the original sharer / an admin) can unshare it.";
            return false;
        }
        if (!RestoreToPrivate(container, entry, out string err))
        {
            message = $"Could not restore the container: {err}";
            return false;
        }
        _entries.Remove(entry);
        SaveSync();
        message = $"{container.GetPrefabGuid().GetPrefabName()} is private again.";
        return true;
    }

    /// <summary>
    /// Player bulk shutdown: unshare every container the caller shared OR currently
    /// controls (their castle-heart team). Stale entries that no longer resolve are
    /// purged when they belong to the caller.
    /// </summary>
    public (int Restored, int Purged) UnshareMine(Entity character)
    {
        ulong steamId = character.GetSteamId();
        int restored = 0, purged = 0;
        foreach (var entry in new List<PublicContainerEntry>(_entries))
        {
            var container = ResolveEntry(entry);
            bool mine = entry.SharedBySteamId == steamId
                || (container != Entity.Null && IsController(character, container, entry));
            if (!mine) continue;
            if (container == Entity.Null) { _entries.Remove(entry); purged++; continue; }
            if (RestoreToPrivate(container, entry, out _)) restored++;
            _entries.Remove(entry);
        }
        SaveSync();
        return (restored, purged);
    }

    /// <summary>Admin bulk shutdown: unshare every container shared by the given steamId.</summary>
    public (int Restored, int Purged) UnshareBySteamId(ulong steamId)
    {
        int restored = 0, purged = 0;
        foreach (var entry in new List<PublicContainerEntry>(_entries))
        {
            if (entry.SharedBySteamId != steamId) continue;
            var container = ResolveEntry(entry);
            if (container == Entity.Null) { _entries.Remove(entry); purged++; continue; }
            if (RestoreToPrivate(container, entry, out _)) restored++;
            _entries.Remove(entry);
        }
        SaveSync();
        return (restored, purged);
    }

    /// <summary>Compact one-line summary of an entry (for list commands).</summary>
    public string DescribeEntry(PublicContainerEntry e)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"{new PrefabGUID(e.PrefabGuid).GetPrefabName()} @({e.TileX},{e.TileY}) [{e.ContainerClass}] {e.Permission}");
        if (e.LimitWithdrawStacks > 0)
            sb.Append($", limit {e.LimitWithdrawStacks}/{(e.LimitHours > 0 ? e.LimitHours : 24):0.#}h");
        if (e.CostItemGuid != 0)
            sb.Append($", cost {e.CostAmount}× {new PrefabGUID(e.CostItemGuid).GetPrefabName()}");
        return sb.ToString();
    }

    /// <summary>Admin: unshare everything, restoring each resolvable container.</summary>
    public int UnshareAll(out int unresolved)
    {
        unresolved = 0;
        int restored = 0;
        foreach (var entry in new List<PublicContainerEntry>(_entries))
        {
            var container = ResolveEntry(entry);
            if (container == Entity.Null) { unresolved++; _entries.Remove(entry); continue; }
            if (RestoreToPrivate(container, entry, out _)) restored++;
            _entries.Remove(entry);
        }
        SaveSync();
        return restored;
    }

    // ---------------------------------------------------------------- init re-apply

    /// <summary>
    /// Find the live entity for a registry entry (prefab GUID + tile coords).
    /// v0.2.1: must include Disabled entities — castle containers carry
    /// DisableWhenNoPlayersInRange and are disabled whenever nobody is near
    /// (which is ALWAYS true during the boot-time re-apply).
    /// </summary>
    Entity ResolveEntry(PublicContainerEntry entry)
    {
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(new(Il2CppType.Of<InventoryOwner>(), ComponentType.AccessMode.ReadOnly))
            .AddAll(new(Il2CppType.Of<TilePosition>(), ComponentType.AccessMode.ReadOnly))
            .AddAll(new(Il2CppType.Of<PrefabGUID>(), ComponentType.AccessMode.ReadOnly))
            .WithOptions(EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludeSpawnTag);
        var query = Core.EntityManager.CreateEntityQuery(ref builder);
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                if (!e.TryGetComponent<PrefabGUID>(out var guid) || guid._Value != entry.PrefabGuid) continue;
                if (!e.TryGetComponent<TilePosition>(out var tile)) continue;
                if (tile.Tile.x != entry.TileX || tile.Tile.y != entry.TileY) continue;
                return e;
            }
        }
        finally
        {
            entities.Dispose();
        }
        return Entity.Null;
    }

    /// <summary>
    /// Re-apply the neutral team to every registered container after server load
    /// (placement teams are restored from the save; our share state lives only in
    /// the registry). Unresolvable entries are kept and logged — they may belong
    /// to castles that failed to resolve this boot; an admin can clean them with
    /// '.uriel unshareall'.
    /// </summary>
    public void ReapplyAll()
    {
        if (_entries.Count == 0) return;
        if (!Settings.PublicStorage_Enabled.Value)
        {
            Core.Log.LogInfo($"[Uriel SHARE] PublicStorage disabled in config; {_entries.Count} share(s) NOT applied (containers stay private).");
            return;
        }
        if (!CanResolvePublicTeam())
        {
            Core.Log.LogWarning($"[Uriel SHARE] cannot re-apply {_entries.Count} share(s): no neutral team source.");
            return;
        }
        int applied = 0, missing = 0;
        foreach (var entry in _entries)
        {
            var container = ResolveEntry(entry);
            if (container == Entity.Null) { missing++; continue; }
            ApplyPublicTeam(container, entry); // also (re)captures the heart anchor when the save restored the connection
            applied++;
        }
        SaveSync(); // persist any newly-captured heart anchors (entries from pre-v0.8.0 builds)
        Core.Log.LogInfo($"[Uriel SHARE] re-applied public team to {applied} container(s); {missing} entry(ies) did not resolve (kept; '.uriel unshareall' to purge).");
    }

    // ================================================================ policy modifiers (v0.3.0)

    /// <summary>Find (or create by sharing) the entry for a container the caller controls (admins override).</summary>
    bool TryGetOrShare(Entity character, Entity container, bool isAdmin, out PublicContainerEntry entry, out string error)
    {
        entry = FindEntry(container);
        error = null;
        if (entry is not null)
        {
            // Policy edits require control (or being the original sharer); admins override.
            if (!isAdmin && !IsController(character, container, entry) && character.GetSteamId() != entry.SharedBySteamId)
            {
                entry = null;
                error = "You don't control this container, so you can't change its sharing policy.";
                return false;
            }
            return true;
        }
        if (!Share(character, container, out string shareMsg, isAdmin)) { error = shareMsg; return false; }
        entry = FindEntry(container);
        if (entry is null) { error = "Share succeeded but the entry could not be found (report this)."; return false; }
        return true;
    }

    public bool SetPermission(Entity character, Entity container, string permission, out string message, bool isAdmin = false)
    {
        permission = permission?.Trim().ToLowerInvariant();
        if (permission is not ("take" or "give" or "givetake"))
        {
            message = "Permission must be one of: take (withdraw only), give (donation box), givetake (both).";
            return false;
        }
        if (!TryGetOrShare(character, container, isAdmin, out var entry, out message)) return false;
        entry.Permission = permission;
        SaveSync();
        message = $"Container is public with permission '{permission}' " + permission switch
        {
            "take" => "(others can only take items out).",
            "give" => "(others can only put items in — donation box).",
            _ => "(others can take AND put items).",
        };
        return true;
    }

    public bool SetLimitHours(Entity character, Entity container, double hours, out string message, bool isAdmin = false)
    {
        if (hours < 0) { message = "Hours must be 0 (no window) or positive."; return false; }
        if (!TryGetOrShare(character, container, isAdmin, out var entry, out message)) return false;
        entry.LimitHours = hours;
        if (hours > 0 && entry.LimitWithdrawStacks <= 0) entry.LimitWithdrawStacks = 1; // sensible default: 1 stack per window
        if (hours == 0) { entry.LimitWithdrawStacks = 0; entry.Usage.Clear(); }
        SaveSync();
        message = hours == 0
            ? "Withdrawal limit removed — container has no per-period cap."
            : $"Withdrawal limit: {entry.LimitWithdrawStacks} stack(s) per {hours:0.#}h per player.";
        return true;
    }

    public bool SetLimitWithdrawal(Entity character, Entity container, int stacks, out string message, bool isAdmin = false)
    {
        if (stacks < 0) { message = "Stacks must be 0 (unlimited) or positive."; return false; }
        if (!TryGetOrShare(character, container, isAdmin, out var entry, out message)) return false;
        entry.LimitWithdrawStacks = stacks;
        if (stacks > 0 && entry.LimitHours <= 0) entry.LimitHours = 24; // sensible default window
        if (stacks == 0) { entry.LimitHours = 0; entry.Usage.Clear(); }
        SaveSync();
        message = stacks == 0
            ? "Withdrawal limit removed — container has no per-period cap."
            : $"Withdrawal limit: {stacks} stack(s) per {entry.LimitHours:0.#}h per player.";
        return true;
    }

    public bool SetCost(Entity character, Entity container, int itemGuid, int amount, out string message, bool isAdmin = false)
    {
        if (itemGuid != 0 && (amount <= 0))
        {
            message = "Cost amount must be positive (or use item id 0 to make it free).";
            return false;
        }
        if (itemGuid != 0 && Core.ItemCatalog is not null && !Core.ItemCatalog.IsKnownItem(itemGuid))
        {
            message = $"Unknown item id {itemGuid}. Find the right id with: .uriel finditem <name>";
            return false;
        }
        if (!TryGetOrShare(character, container, isAdmin, out var entry, out message)) return false;
        entry.CostItemGuid = itemGuid;
        entry.CostAmount = itemGuid == 0 ? 0 : amount;
        SaveSync();
        message = itemGuid == 0
            ? "Container is now free to access."
            : $"Access cost: {amount}× {new PrefabGUID(itemGuid).GetPrefabName()} per stack withdrawn. " +
              (GetPayChest(entry.SharedBySteamId) is null
                  ? "Payments go INTO this container (designate a private payment chest with '.uriel paychest')."
                  : "Payments go to your designated pay chest.");
        return true;
    }

    // ---------------------------------------------------------------- pay chest

    public PayChestRef GetPayChest(ulong steamId) =>
        _payChests.TryGetValue(steamId.ToString(), out var r) ? r : null;

    public bool SetPayChest(Entity character, Entity container, out string message)
    {
        if (!CharacterControlsContainer(character, container))
        {
            message = "You don't control this container — aim at one of YOUR private chests.";
            return false;
        }
        if (FindEntry(container) is not null)
        {
            message = "That container is public — payments must go to a PRIVATE chest. Aim at a private one.";
            return false;
        }
        if (ClassifyContainer(container) != "storage")
        {
            message = "Pay chests must be regular storage (not prison cells or coffins).";
            return false;
        }
        if (!IsGeneralStorage(container))
        {
            message = "Pay chests must be GENERAL storage — specialized stashes (lumber, seeds, …) only accept certain items and can't safely receive payments.";
            return false;
        }
        if (!container.TryGetComponent<TilePosition>(out var tile))
        {
            message = "Container has no tile position; cannot register it.";
            return false;
        }
        _payChests[character.GetSteamId().ToString()] = new PayChestRef
        {
            PrefabGuid = container.GetPrefabGuid()._Value,
            TileX = tile.Tile.x,
            TileY = tile.Tile.y,
        };
        SaveSync();
        message = $"Payment chest set: {container.GetPrefabGuid().GetPrefabName()}. Cost payments from your shared containers will be delivered here.";
        return true;
    }

    Entity ResolvePayChest(ulong ownerSteamId)
    {
        var r = GetPayChest(ownerSteamId);
        if (r is null) return Entity.Null;
        return ResolveEntry(new PublicContainerEntry { PrefabGuid = r.PrefabGuid, TileX = r.TileX, TileY = r.TileY });
    }

    // ---------------------------------------------------------------- info

    public string BuildInfoText(Entity container)
    {
        var entry = FindEntry(container);
        string name = container.GetPrefabGuid().GetPrefabName();
        if (entry is null) return $"{name}: private (not shared). Controllers can share it with '.uriel share'.";
        var sb = new System.Text.StringBuilder();
        sb.Append($"{name}: PUBLIC [{entry.Permission}]");
        if (entry.LimitWithdrawStacks > 0)
            sb.Append($", limit {entry.LimitWithdrawStacks} stack(s)/{entry.LimitHours:0.#}h per player");
        if (entry.CostItemGuid != 0)
            sb.Append($", cost {entry.CostAmount}× {new PrefabGUID(entry.CostItemGuid).GetPrefabName()} per stack");
        sb.Append($". Shared by {entry.SharedBySteamId} since {entry.SharedAtUtc}.");
        return sb.ToString();
    }

    /// <summary>
    /// Diagnostic dump of a container's live sharing-relevant state (v0.8.1) —
    /// turns "he can't click it" reports into facts. Admin command: .uriel sharedebug
    /// </summary>
    public string BuildDebugText(Entity container)
    {
        var sb = new System.Text.StringBuilder();
        var entry = FindEntry(container);
        sb.AppendLine($"{container.GetPrefabGuid().GetPrefabName()} [{ClassifyContainer(container)}] registered={(entry is not null ? "YES" : "no")}");
        if (container.TryGetComponent<Team>(out var team))
            sb.AppendLine($"Team.Value={team.Value} FactionIndex={team.FactionIndex}");
        if (container.TryGetComponent<TeamReference>(out var teamRef))
        {
            var refEnt = teamRef.Value._Value;
            sb.AppendLine($"TeamReference={refEnt} exists={refEnt.Exists()} isNeutralSingleton={refEnt.Exists() && refEnt.Has<NeutralTeam>()}");
        }
        if (container.TryGetComponent<CastleHeartConnection>(out var conn))
        {
            var heart = conn.CastleHeartEntity.GetEntityOnServer();
            sb.AppendLine($"CastleHeartConnection={(heart == Entity.Null ? "SEVERED (null)" : $"{heart} exists={heart.Exists()}")}");
        }
        else sb.AppendLine("CastleHeartConnection: component missing");
        if (entry is not null)
            sb.AppendLine($"Entry: heartAnchor={(entry.HasHeartTile ? $"({entry.HeartTileX},{entry.HeartTileY}) resolves={FindHeartByTile(entry.HeartTileX, entry.HeartTileY).Exists()}" : "NONE")} policy=[{entry.Permission}{(entry.CostItemGuid != 0 ? " cost" : "")}{(entry.LimitWithdrawStacks > 0 ? " limit" : "")}]");
        if (container.TryGetComponent<PrisonCell>(out var cell))
        {
            Entity prisoner = cell.ImprisonedEntity.GetEntityOnServer();
            if (prisoner.Exists())
            {
                prisoner.TryGetComponent<Team>(out var pTeam);
                sb.AppendLine($"Prisoner: {prisoner.GetPrefabGuid().GetPrefabName()} Team.Value={pTeam.Value}");
            }
            else sb.AppendLine("Prisoner: none/empty cell");
        }
        return sb.ToString().TrimEnd();
    }

    // ================================================================ container rebuild (v0.9.0)

    /// <summary>
    /// Rebuild a storage container as a FRESH entity with the desired sharing
    /// state (v0.9.0): live testing proved clients only re-evaluate a container's
    /// interactability when they receive a NEW entity — mutating the live one
    /// (even with a Disabled blink) left strangers locked out until they
    /// restarted their game. The rebuild: instantiate the same prefab, copy
    /// transform/tile data, apply the target team state, transfer every
    /// inventory slot (preserving item entities), destroy the old container.
    /// Prison cells are NOT rebuilt (the prisoner binding makes that unsafe) —
    /// they keep the mutate+blink path.
    /// </summary>
    bool RebuildContainer(Entity oldC, PublicContainerEntry entry, bool makePublic, out string error)
    {
        error = null;
        if (!oldC.TryGetComponent<Unity.Transforms.Translation>(out var translation)
            || !oldC.TryGetComponent<Unity.Transforms.Rotation>(out var rotation)
            || !oldC.TryGetComponent<TilePosition>(out var tilePos))
        {
            error = "Container is missing placement data and can't be rebuilt.";
            return false;
        }
        oldC.TryGetComponent<TileBounds>(out var tileBounds);
        var prefabGuid = oldC.GetPrefabGuid();
        if (!Core.PrefabCollectionSystem._PrefabLookupMap.TryGetValue(prefabGuid, out Entity prefab))
        {
            error = "Container prefab not found.";
            return false;
        }

        Entity heart = ResolveHeartFor(oldC, entry);
        Entity newC = Core.EntityManager.Instantiate(prefab);

        // transform/tile (dynamic-transform path — never copy a StaticTransform index)
        newC.With((ref Unity.Transforms.Translation t) => t.Value = translation.Value);
        newC.With((ref Unity.Transforms.Rotation r) => r.Value = rotation.Value);
        newC.With((ref TilePosition tp) => { tp.Tile = tilePos.Tile; tp.TileRotation = tilePos.TileRotation; tp.CompressedHeight = tilePos.CompressedHeight; });
        if (newC.Has<TileBounds>())
            newC.With((ref TileBounds tb) => tb.Value = tileBounds.Value);
        if (newC.Has<StaticTransformCompatible>())
            newC.With((ref StaticTransformCompatible s) =>
            {
                s.UseStaticTransform = false;
                s.NonStaticTransform_Pos = new Unity.Mathematics.float2(translation.Value.x, translation.Value.z);
                s.NonStaticTransform_Height = translation.Value.y;
                s.NonStaticTransform_Rotation = tilePos.TileRotation;
            });

        // team state
        if (makePublic)
        {
            if (TryGetPublicTeam(out int teamValue, out int factionIndex, out Entity teamRefEntity))
            {
                newC.With((ref Team t) => { t.Value = teamValue; t.FactionIndex = factionIndex; });
                newC.With((ref TeamReference tr) => tr.Value._Value = teamRefEntity);
            }
            if (newC.Has<CastleHeartConnection>())
                newC.With((ref CastleHeartConnection c) => c.CastleHeartEntity = Entity.Null);
            if (heart.Exists() && heart.TryGetComponent<UserOwner>(out var heartOwner) && newC.Has<UserOwner>())
                newC.With((ref UserOwner uo) => uo = heartOwner);
        }
        else
        {
            if (!heart.Exists())
            {
                DestroyUtility.Destroy(Core.EntityManager, newC);
                error = "Castle heart not found; cannot rebuild as private.";
                return false;
            }
            Entity donor = FindSiblingTeamDonor(oldC, heart);
            if (donor == Entity.Null) donor = heart;
            if (donor.TryGetComponent<Team>(out var donorTeam) && donor.TryGetComponent<TeamReference>(out var donorRef))
            {
                var refEntity = donorRef.Value._Value;
                newC.With((ref Team t) => { t.Value = donorTeam.Value; t.FactionIndex = donorTeam.FactionIndex; });
                newC.With((ref TeamReference tr) => tr.Value._Value = refEntity);
            }
            if (newC.Has<CastleHeartConnection>())
                newC.With((ref CastleHeartConnection c) => c.CastleHeartEntity = heart);
            if (heart.TryGetComponent<UserOwner>(out var hOwner) && newC.Has<UserOwner>())
                newC.With((ref UserOwner uo) => uo = hOwner);
        }

        // inventory transfer + old destroy — the new container's own inventory
        // entity may spawn a frame late, so try now and retry briefly if needed.
        if (!FinishRebuild(oldC, newC))
        {
            Entity capturedOld = oldC, capturedNew = newC;
            Tick.RunLater(3, () =>
            {
                if (!FinishRebuild(capturedOld, capturedNew))
                {
                    // Rollback: keep the old container, discard the new one. The
                    // old container keeps its previous state — caller's command
                    // already replied success, so log loudly.
                    Core.Log.LogError("[Uriel SHARE] rebuild FAILED (inventory never resolved) — rolled back; container state unchanged.");
                    if (capturedNew.Exists()) DestroyUtility.Destroy(Core.EntityManager, capturedNew);
                }
            });
        }
        return true;
    }

    /// <summary>Transfer the inventory old→new and destroy the old container. False if the new inventory isn't ready yet.</summary>
    bool FinishRebuild(Entity oldC, Entity newC)
    {
        try
        {
            if (!oldC.Exists() || !newC.Exists()) return true; // already settled
            Entity oldInv = ResolveInventoryEntity(oldC);
            Entity newInv = ResolveInventoryEntity(newC);
            if (newInv == Entity.Null) return false; // not spawned yet — retry
            if (oldInv != Entity.Null
                && Core.ServerGameManager.TryGetBuffer<InventoryBuffer>(oldInv, out var oldBuf)
                && Core.ServerGameManager.TryGetBuffer<InventoryBuffer>(newInv, out var newBuf))
            {
                int n = Math.Min(oldBuf.Length, newBuf.Length);
                int moved = 0;
                for (int i = 0; i < n; i++)
                {
                    var slot = oldBuf[i];
                    if (slot.ItemType._Value == 0 && slot.Amount <= 0) continue;
                    newBuf[i] = slot;
                    // Re-point the item entity at its new home, then clear the old
                    // slot so destroying the old container can't cascade into it.
                    Entity item = slot.ItemEntity.GetEntityOnServer();
                    if (item.Exists() && item.Has<InventoryItem>())
                        item.With((ref InventoryItem ii) => ii.ContainerEntity = newInv);
                    var empty = slot;
                    empty.ItemEntity = default;
                    empty.ItemType = default;
                    empty.Amount = 0;
                    oldBuf[i] = empty;
                    moved++;
                }
                if (Settings.VerboseLogging.Value)
                    Core.Log.LogInfo($"[Uriel SHARE] rebuild: moved {moved} slot(s) to the new container.");
            }
            DestroyUtility.Destroy(Core.EntityManager, oldC);
            return true;
        }
        catch (Exception ex)
        {
            Core.Log.LogError($"[Uriel SHARE] rebuild transfer failed: {ex}");
            return true; // don't loop forever on an exception
        }
    }

    // ================================================================ move-event enforcement (v0.3.0)

    /// <summary>
    /// Resolve the entity that actually CARRIES the InventoryBuffer. Placed
    /// containers keep their items on a separate attached external-inventory
    /// entity — passing the tile entity to TryAdd/RemoveInventoryItem fails
    /// (v0.4.0's deposit bug: every add reported "full").
    /// </summary>
    static Entity ResolveInventoryEntity(Entity entity)
    {
        if (entity == Entity.Null) return Entity.Null;
        if (Core.ServerGameManager.TryGetBuffer<InventoryBuffer>(entity, out _)) return entity;
        if (InventoryUtilities.TryGetInventoryEntity(Core.EntityManager, entity, out Entity inv)) return inv;
        return Entity.Null;
    }

    /// <summary>
    /// Map an inventory entity from a move event back to its container: the entity
    /// itself if registered, else via InventoryConnection.InventoryOwner.
    /// </summary>
    Entity ContainerOf(Entity inventoryEntity)
    {
        if (inventoryEntity == Entity.Null) return Entity.Null;
        if (FindEntry(inventoryEntity) is not null) return inventoryEntity;
        if (inventoryEntity.TryGetComponent<InventoryConnection>(out var conn)
            && conn.InventoryOwner.Exists())
            return conn.InventoryOwner;
        return inventoryEntity;
    }

    static Entity ResolveNetworkId(ProjectM.Network.NetworkId id)
    {
        // This assembly version exposes the NetworkId→Entity map as an ECS singleton
        // (NetworkIdSystem.Singleton._NetworkIdLookupMap), fetched via ServerScriptMapper.
        try
        {
            var singleton = Core.ServerScriptMapper.GetSingleton<ProjectM.Network.NetworkIdSystem.Singleton>();
            return singleton._NetworkIdLookupMap.TryGetValue(id, out Entity e) ? e : Entity.Null;
        }
        catch
        {
            return Entity.Null;
        }
    }

    void Deny(Entity eventEntity, Entity userEntity, string reason)
    {
        ChatNotify.ToUserEntity(userEntity, $"[Uriel] {reason}");
        Core.EntityManager.DestroyEntity(eventEntity);
    }

    /// <summary>
    /// Called from the MoveItemBetweenInventories prefix for every pending move
    /// event. Applies the sharing policy when either side is a public container;
    /// executes permitted deposits manually (vanilla refuses deposits into
    /// neutral-team containers — confirmed by live testing, v0.2.x).
    /// </summary>
    public void HandleMoveEvent(Entity eventEntity, ProjectM.Network.FromCharacter fromChar,
        ProjectM.Network.NetworkId fromInvId, ProjectM.Network.NetworkId toInvId, int fromSlot)
    {
        Entity fromInv = ResolveNetworkId(fromInvId);
        Entity toInv = ResolveNetworkId(toInvId);
        Entity fromContainer = ContainerOf(fromInv);
        Entity toContainer = ContainerOf(toInv);
        var fromEntry = fromContainer.Exists() ? FindEntry(fromContainer) : null;
        var toEntry = toContainer.Exists() ? FindEntry(toContainer) : null;
        if (fromEntry is null && toEntry is null) return;          // no public container involved
        if (fromContainer == toContainer) return;                  // intra-container shuffle

        Entity character = fromChar.Character;
        Entity userEntity = fromChar.User;
        bool verbose = Settings.VerboseLogging.Value;

        // ---- WITHDRAW from a public container ----
        if (fromEntry is not null)
        {
            if (IsController(character, fromContainer, fromEntry)) return; // controllers bypass
            if (fromEntry.Permission == "give")
            {
                Deny(eventEntity, userEntity, "This container is a DONATION BOX — you can put items in, not take them.");
                return;
            }
            if (!CheckWithdrawalWindow(fromEntry, character.GetSteamId(), out string limitMsg))
            {
                Deny(eventEntity, userEntity, limitMsg);
                return;
            }
            if (!TryCollectCost(fromEntry, character, userEntity, out string costMsg))
            {
                Deny(eventEntity, userEntity, costMsg);
                return;
            }
            ConsumeWithdrawal(fromEntry, character.GetSteamId());
            if (verbose) Core.Log.LogInfo($"[Uriel SHARE] {character.GetSteamId()} withdrew a stack from public {fromContainer.GetPrefabGuid().GetPrefabName()}.");
            return; // allow: vanilla executes the withdrawal (loot semantics already permit it)
        }

        // ---- DEPOSIT into a public container ----
        if (toEntry is not null)
        {
            bool controller = IsController(character, toContainer, toEntry);
            if (!controller && toEntry.Permission == "take")
            {
                Deny(eventEntity, userEntity, "This container is TAKE-ONLY — you can't put items into it.");
                return;
            }
            // Vanilla refuses deposits into neutral-team containers (world-chest
            // semantics) for EVERYONE, controllers included — execute it manually.
            ManualDeposit(eventEntity, character, userEntity, fromInv, fromSlot, toContainer, verbose);
        }
    }

    bool CheckWithdrawalWindow(PublicContainerEntry entry, ulong steamId, out string denyMsg)
    {
        denyMsg = null;
        if (entry.LimitWithdrawStacks <= 0) return true;
        double hours = entry.LimitHours > 0 ? entry.LimitHours : 24;
        string key = steamId.ToString();
        if (entry.Usage.TryGetValue(key, out var rec)
            && DateTime.TryParse(rec.WindowStartUtc, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out DateTime windowStart))
        {
            var elapsed = DateTime.UtcNow - windowStart;
            if (elapsed.TotalHours >= hours)
                return true; // window expired — ConsumeWithdrawal resets it
            if (rec.StacksTaken >= entry.LimitWithdrawStacks)
            {
                var remaining = TimeSpan.FromHours(hours) - elapsed;
                denyMsg = $"Withdrawal limit reached ({entry.LimitWithdrawStacks} stack(s) per {hours:0.#}h). Try again in {FormatSpan(remaining)}.";
                return false;
            }
        }
        return true;
    }

    void ConsumeWithdrawal(PublicContainerEntry entry, ulong steamId)
    {
        if (entry.LimitWithdrawStacks <= 0) return;
        double hours = entry.LimitHours > 0 ? entry.LimitHours : 24;
        string key = steamId.ToString();
        bool inWindow = entry.Usage.TryGetValue(key, out var rec)
            && DateTime.TryParse(rec.WindowStartUtc, null,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out DateTime windowStart)
            && (DateTime.UtcNow - windowStart).TotalHours < hours;
        if (!inWindow)
        {
            rec = new UsageRecord { WindowStartUtc = DateTime.UtcNow.ToString("u"), StacksTaken = 0 };
            entry.Usage[key] = rec;
        }
        rec.StacksTaken++;
        SaveSync();
    }

    static string FormatSpan(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m" : $"{span.Minutes}m";

    // ---------------------------------------------------------------- payment routing (v0.4.0)

    /// <summary>
    /// Is this a GENERAL storage container (no item-type restriction)? Specialized
    /// stashes (lumber, seeds, …) carry InventoryInstanceElement.RestrictedCategory != 0
    /// or a RestrictedType — forcing the wrong item into those must never happen.
    /// </summary>
    public static bool IsGeneralStorage(Entity container)
    {
        if (!Core.ServerGameManager.TryGetBuffer<InventoryInstanceElement>(container, out var elements))
            return false;
        for (int i = 0; i < elements.Length; i++)
        {
            var e = elements[i];
            if ((long)e.RestrictedCategory != 0) return false;
            if (e.RestrictedType._Value != 0) return false;
        }
        return true;
    }

    /// <summary>
    /// Does this item respect the container's inventory restriction (prison cells
    /// accept only feeding consumables; lumber stashes only wood; …)? Checked
    /// before manual deposits so the mod never forces a wrong-type item in.
    /// </summary>
    public static bool ItemFitsRestriction(Entity container, PrefabGUID itemGuid)
    {
        if (!Core.ServerGameManager.TryGetBuffer<InventoryInstanceElement>(container, out var elements))
            return true; // no restriction data — let the capacity/add path decide
        long itemCategory = 0;
        try
        {
            if (Core.ServerGameManager.ItemLookupMap.TryGetValue(itemGuid, out ItemData data))
                itemCategory = (long)data.ItemCategory;
        }
        catch { /* unknown item — fall through to the flag checks below */ }
        for (int i = 0; i < elements.Length; i++)
        {
            var e = elements[i];
            if (e.RestrictedType._Value != 0 && e.RestrictedType._Value != itemGuid._Value) return false;
            if ((long)e.RestrictedCategory != 0 && (itemCategory & (long)e.RestrictedCategory) == 0) return false;
        }
        return true;
    }

    /// <summary>
    /// How many of <paramref name="itemGuid"/> still FIT in this container
    /// (empty slots × max stack + headroom on same-item stacks)? Capacity is
    /// pre-checked BEFORE any payment is collected so a full destination can
    /// never produce a partial transfer or duplication.
    /// </summary>
    public static int CountFit(Entity container, PrefabGUID itemGuid)
    {
        if (!InventoryUtilities.TryGetInventoryEntity(Core.EntityManager, container, out Entity inv)) return 0;
        if (!Core.ServerGameManager.TryGetBuffer<InventoryBuffer>(inv, out var buffer)) return 0;
        int maxStack = 1;
        try
        {
            if (Core.ServerGameManager.ItemLookupMap.TryGetValue(itemGuid, out ItemData data) && data.MaxAmount > 0)
                maxStack = data.MaxAmount;
        }
        catch { /* unknown item: assume stack of 1 (conservative) */ }
        int fit = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            var slot = buffer[i];
            if (slot.ItemType._Value == 0) fit += maxStack;
            else if (slot.ItemType._Value == itemGuid._Value && slot.Amount < maxStack) fit += maxStack - slot.Amount;
        }
        return fit;
    }

    /// <summary>
    /// Choose where a cost payment goes, capacity- and restriction-checked:
    ///   1. the owner's designated pay chest;
    ///   2. the shared container itself;
    ///   3. the NEAREST general, non-shared storage container on the same castle
    ///      heart (KindredCommands-style proximity fallback);
    ///   4. nowhere → the withdrawal is denied gracefully (payer keeps everything).
    /// </summary>
    Entity PickPaymentDestination(PublicContainerEntry entry, PrefabGUID costItem, int amount)
    {
        var pay = ResolvePayChest(entry.SharedBySteamId);
        if (pay != Entity.Null && IsGeneralStorage(pay) && CountFit(pay, costItem) >= amount) return pay;

        var shared = ResolveEntry(entry);
        if (shared != Entity.Null && IsGeneralStorage(shared) && CountFit(shared, costItem) >= amount) return shared;

        Entity sharedHeart = shared != Entity.Null ? ResolveHeartFor(shared, entry) : Entity.Null;
        if (shared != Entity.Null
            && sharedHeart.Exists()
            && shared.TryGetComponent<Unity.Transforms.Translation>(out var sharedPos))
        {
            Entity heart = sharedHeart;
            var builder = new EntityQueryBuilder(Allocator.Temp)
                .AddAll(new(Il2CppType.Of<InventoryOwner>(), ComponentType.AccessMode.ReadOnly))
                .AddAll(new(Il2CppType.Of<TilePosition>(), ComponentType.AccessMode.ReadOnly))
                .AddAll(new(Il2CppType.Of<CastleHeartConnection>(), ComponentType.AccessMode.ReadOnly))
                .WithOptions(EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludeSpawnTag);
            var query = Core.EntityManager.CreateEntityQuery(ref builder);
            var entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                Entity best = Entity.Null;
                float bestSq = float.MaxValue;
                for (int i = 0; i < entities.Length; i++)
                {
                    var e = entities[i];
                    if (e == shared) continue;
                    if (!e.TryGetComponent<CastleHeartConnection>(out var c)
                        || c.CastleHeartEntity.GetEntityOnServer() != heart) continue;     // same castle only
                    if (FindEntry(e) is not null) continue;                                // never into a shared container
                    if (ClassifyContainer(e) != "storage") continue;                       // no prisons/coffins
                    if (!IsGeneralStorage(e)) continue;                                    // no specialized stashes
                    if (CountFit(e, costItem) < amount) continue;                          // must fit ENTIRELY
                    if (!e.TryGetComponent<Unity.Transforms.Translation>(out var t)) continue;
                    float dx = t.Value.x - sharedPos.Value.x, dy = t.Value.y - sharedPos.Value.y, dz = t.Value.z - sharedPos.Value.z;
                    float dsq = dx * dx + dy * dy + dz * dz;
                    if (dsq < bestSq) { bestSq = dsq; best = e; }
                }
                if (best != Entity.Null) return best;
            }
            finally
            {
                entities.Dispose();
            }
        }
        return Entity.Null;
    }

    /// <summary>
    /// Charge the per-stack access cost. Destination is capacity-checked FIRST;
    /// payment is only removed from the taker once a fitting destination exists,
    /// and refunded if the final add unexpectedly fails. Never throws upward.
    /// </summary>
    bool TryCollectCost(PublicContainerEntry entry, Entity character, Entity userEntity, out string denyMsg)
    {
        denyMsg = null;
        if (entry.CostItemGuid == 0) return true;
        var costItem = new PrefabGUID(entry.CostItemGuid);

        if (!InventoryUtilities.TryGetInventoryEntity(Core.EntityManager, character, out Entity playerInv)
            || Core.ServerGameManager.GetInventoryItemCount(playerInv, costItem) < entry.CostAmount)
        {
            denyMsg = $"This container costs {entry.CostAmount}× {costItem.GetPrefabName()} per stack — you don't have enough.";
            return false;
        }

        Entity payTarget = PickPaymentDestination(entry, costItem, entry.CostAmount);
        if (payTarget == Entity.Null)
        {
            denyMsg = "No payment destination has room (pay chest, this container, and the castle's other general storage are all full) — tell the owner. No payment was taken.";
            return false;
        }

        Entity payInv = ResolveInventoryEntity(payTarget); // the inventory CARRIER, not the tile entity
        if (payInv == Entity.Null)
        {
            denyMsg = "Payment destination has no resolvable inventory — tell the owner. No payment was taken.";
            return false;
        }
        if (!Core.ServerGameManager.TryRemoveInventoryItem(playerInv, costItem, entry.CostAmount))
        {
            denyMsg = "Payment could not be collected (inventory changed?). Try again.";
            return false;
        }
        if (!Core.ServerGameManager.TryAddInventoryItem(payInv, costItem, entry.CostAmount))
        {
            // Should not happen after the capacity check — refund defensively.
            Core.ServerGameManager.TryAddInventoryItem(character, costItem, entry.CostAmount);
            Core.Log.LogWarning($"[Uriel SHARE] payment: TryAddInventoryItem failed AFTER capacity check (target={payInv}, container={payTarget.GetPrefabGuid().GetPrefabName()}); refunded.");
            denyMsg = "Payment delivery failed unexpectedly — refunded. Try again.";
            return false;
        }
        ChatNotify.ToUserEntity(userEntity, $"[Uriel] Paid {entry.CostAmount}× {costItem.GetPrefabName()} for this withdrawal.");
        return true;
    }

    /// <summary>
    /// Execute a deposit ourselves, then destroy the vanilla event (which would
    /// have been refused for a neutral-team container). Refunds on failure.
    /// Failure branches always log — this path broke once (v0.4.0 passed the tile
    /// entity instead of the inventory carrier) and must stay diagnosable.
    /// </summary>
    void ManualDeposit(Entity eventEntity, Entity character, Entity userEntity,
        Entity fromInv, int fromSlot, Entity toContainer, bool verbose)
    {
        try
        {
            Entity sourceInv = ResolveInventoryEntity(fromInv);
            if (sourceInv == Entity.Null)
                sourceInv = ResolveInventoryEntity(character); // event id didn't resolve — the source is the depositor
            if (!Core.ServerGameManager.TryGetBuffer<InventoryBuffer>(sourceInv, out var buffer)
                || fromSlot < 0 || fromSlot >= buffer.Length)
            {
                Core.Log.LogWarning($"[Uriel SHARE] deposit: source inventory unreadable (fromInv={fromInv}, resolved={sourceInv}, slot={fromSlot}); leaving event to vanilla.");
                return;
            }
            var slot = buffer[fromSlot];
            PrefabGUID itemGuid = slot.ItemType;
            int amount = slot.Amount;
            if (itemGuid._Value == 0 || amount <= 0) return;

            Entity targetInv = ResolveInventoryEntity(toContainer);
            if (targetInv == Entity.Null)
            {
                Core.Log.LogWarning($"[Uriel SHARE] deposit: container {toContainer.GetPrefabGuid().GetPrefabName()} has no resolvable inventory entity; leaving event to vanilla.");
                return;
            }
            if (!ItemFitsRestriction(toContainer, itemGuid))
            {
                Deny(eventEntity, userEntity, "That container doesn't accept this type of item.");
                return;
            }
            if (CountFit(toContainer, itemGuid) < amount)
            {
                Deny(eventEntity, userEntity, "That container doesn't have room for this stack.");
                return;
            }
            if (!Core.ServerGameManager.TryRemoveInventoryItem(sourceInv, itemGuid, amount))
            {
                Core.Log.LogWarning($"[Uriel SHARE] deposit: TryRemoveInventoryItem failed (source={sourceInv}, item={itemGuid.GetPrefabName()}×{amount}).");
                Deny(eventEntity, userEntity, "Deposit failed (item could not be moved).");
                return;
            }
            if (!Core.ServerGameManager.TryAddInventoryItem(targetInv, itemGuid, amount))
            {
                Core.ServerGameManager.TryAddInventoryItem(character, itemGuid, amount); // refund
                Core.Log.LogWarning($"[Uriel SHARE] deposit: TryAddInventoryItem failed AFTER capacity check (target={targetInv}, container={toContainer.GetPrefabGuid().GetPrefabName()}, item={itemGuid.GetPrefabName()}×{amount}); refunded.");
                Deny(eventEntity, userEntity, "Deposit failed unexpectedly — items refunded. Tell the admin to check the server log.");
                return;
            }
            Core.EntityManager.DestroyEntity(eventEntity); // we did the move; don't let vanilla double-process
            if (verbose) Core.Log.LogInfo($"[Uriel SHARE] {character.GetSteamId()} deposited {amount}× {itemGuid.GetPrefabName()} into public {toContainer.GetPrefabGuid().GetPrefabName()}.");
        }
        catch (Exception ex)
        {
            Core.Log.LogWarning($"[Uriel SHARE] manual deposit failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Move-all ("take all" style) events can't be policy-accounted per stack —
    /// block them for non-controllers whenever the container has any restriction.
    /// Unrestricted containers let vanilla proceed (take-all works; deposit-all is
    /// refused by vanilla either way).
    /// </summary>
    public void HandleMoveAllEvent(Entity eventEntity, ProjectM.Network.FromCharacter fromChar,
        ProjectM.Network.NetworkId fromInvId, ProjectM.Network.NetworkId toInvId)
    {
        Entity fromContainer = ContainerOf(ResolveNetworkId(fromInvId));
        Entity toContainer = ContainerOf(ResolveNetworkId(toInvId));
        var entry = fromContainer.Exists() ? FindEntry(fromContainer) : null;
        entry ??= toContainer.Exists() ? FindEntry(toContainer) : null;
        if (entry is null) return;
        Entity publicContainer = FindEntry(fromContainer) is not null ? fromContainer : toContainer;
        if (IsController(fromChar.Character, publicContainer, entry)) return;
        if (!entry.HasRestrictions) return;
        Deny(eventEntity, fromChar.User, "This container has sharing rules — move items one at a time.");
    }
}
