using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ProjectM;
using ProjectM.CastleBuilding;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Uriel.Config;

namespace Uriel.Services;

/// <summary>
/// Per-container public sharing — the mod's first feature (see
/// docs/features/PUBLIC_STORAGE.md).
///
/// MECHANISM (team-swap): vanilla decides container access from the RUNTIME
/// Team/TeamReference assigned at placement (the castle's team). World chests
/// keep a neutral team, which is why anyone can open them — and Team replicates
/// to clients, so the open prompt follows automatically. Sharing copies a live
/// world chest's Team/TeamReference onto the container ("neutral donor");
/// unsharing restores the team from the container's own CastleHeartConnection →
/// castle heart. Nothing about the original team needs persisting — the heart
/// is always the authoritative restore source.
///
/// PERSISTENCE: registry entries are keyed by prefab GUID + TilePosition tile
/// coords (stable across save/load; entity ids and NetworkIds are not). The
/// neutral team is re-applied to registered containers at every server init.
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
    }

    sealed class SaveFile
    {
        public int SchemaVersion { get; set; } = 1;
        public List<PublicContainerEntry> Entries { get; set; } = new();
    }

    readonly List<PublicContainerEntry> _entries = new();

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
            Core.Log.LogInfo($"[Uriel SHARE] loaded {_entries.Count} public-container entry(ies).");
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
            var json = JsonSerializer.Serialize(new SaveFile { Entries = _entries },
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
    /// Find a live world chest and cache its Team/TeamReference as the neutral
    /// "public" team. Resolved lazily (entities must exist; init order safe).
    /// </summary>
    bool TryResolveDonorTeam()
    {
        if (_donorResolved && _donorTeamRefEntity.Exists()) return true;
        _donorResolved = false;

        var query = Core.EntityManager.CreateEntityQuery(
            ComponentType.ReadOnly<PrefabGUID>(),
            ComponentType.ReadOnly<Team>(),
            ComponentType.ReadOnly<TeamReference>(),
            ComponentType.ReadOnly<InventoryOwner>());
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
                if (Settings.VerboseLogging.Value)
                    Core.Log.LogInfo($"[Uriel SHARE] neutral-team donor: {guid.GetPrefabName()} (Team.Value={team.Value}).");
                return true;
            }
        }
        finally
        {
            entities.Dispose();
        }
        Core.Log.LogWarning("[Uriel SHARE] no world chest found to donate a neutral team — sharing unavailable this session.");
        return false;
    }

    void ApplyPublicTeam(Entity container)
    {
        var donorTeam = _donorTeam;
        var donorRef = _donorTeamRefEntity;
        container.With((ref Team t) => { t.Value = donorTeam.Value; t.FactionIndex = donorTeam.FactionIndex; });
        container.With((ref TeamReference tr) => tr.Value._Value = donorRef);
    }

    /// <summary>Restore the container's team from its castle heart (the authoritative owner team).</summary>
    bool RestoreCastleTeam(Entity container, out string error)
    {
        error = null;
        if (!container.TryGetComponent<CastleHeartConnection>(out var conn))
        {
            error = "Container has no castle heart connection; cannot restore its team.";
            return false;
        }
        Entity heart = conn.CastleHeartEntity.GetEntityOnServer();
        if (!heart.Exists() || !heart.TryGetComponent<Team>(out var heartTeam)
            || !heart.TryGetComponent<TeamReference>(out var heartRef))
        {
            error = "Castle heart not found or has no team; cannot restore.";
            return false;
        }
        var refEntity = heartRef.Value._Value;
        container.With((ref Team t) => { t.Value = heartTeam.Value; t.FactionIndex = heartTeam.FactionIndex; });
        container.With((ref TeamReference tr) => tr.Value._Value = refEntity);
        return true;
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

    public bool Share(Entity character, Entity container, out string message)
    {
        string cls = ClassifyContainer(container);
        if (cls == "prison")
        {
            message = "Prison cells are a separate feature (coming soon) — this command shares storage only.";
            return false;
        }
        if (cls == "coffin")
        {
            message = "Servant coffins can't be shared.";
            return false;
        }
        if (!CharacterControlsContainer(character, container))
        {
            message = "You don't control this container (its castle isn't yours/your clan's).";
            return false;
        }
        if (FindEntry(container) is not null)
        {
            message = "That container is already public.";
            return false;
        }
        if (!TryResolveDonorTeam())
        {
            message = "Sharing unavailable: no neutral team source found on this map (see server log).";
            return false;
        }
        if (!container.TryGetComponent<TilePosition>(out var tile))
        {
            message = "Container has no tile position; cannot register it.";
            return false;
        }

        ApplyPublicTeam(container);
        _entries.Add(new PublicContainerEntry
        {
            PrefabGuid = container.GetPrefabGuid()._Value,
            TileX = tile.Tile.x,
            TileY = tile.Tile.y,
            ContainerClass = cls,
            SharedBySteamId = character.GetSteamId(),
            SharedAtUtc = DateTime.UtcNow.ToString("u"),
        });
        SaveSync();
        message = $"{container.GetPrefabGuid().GetPrefabName()} is now PUBLIC — anyone on the server can use it. Aim at it and use '.uriel unshare' to revert.";
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
        if (!isAdmin && !CharacterControlsContainer(character, container)
            && character.GetSteamId() != entry.SharedBySteamId)
        {
            message = "Only the container's controllers (or the original sharer / an admin) can unshare it.";
            return false;
        }
        if (!RestoreCastleTeam(container, out string err))
        {
            message = $"Could not restore the container's team: {err}";
            return false;
        }
        _entries.Remove(entry);
        SaveSync();
        message = $"{container.GetPrefabGuid().GetPrefabName()} is private again.";
        return true;
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
            if (RestoreCastleTeam(container, out _)) restored++;
            _entries.Remove(entry);
        }
        SaveSync();
        return restored;
    }

    // ---------------------------------------------------------------- init re-apply

    /// <summary>
    /// Find the live entity for a registry entry (prefab GUID + tile coords).
    /// </summary>
    Entity ResolveEntry(PublicContainerEntry entry)
    {
        var query = Core.EntityManager.CreateEntityQuery(
            ComponentType.ReadOnly<InventoryOwner>(),
            ComponentType.ReadOnly<TilePosition>(),
            ComponentType.ReadOnly<PrefabGUID>());
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
        if (!TryResolveDonorTeam())
        {
            Core.Log.LogWarning($"[Uriel SHARE] cannot re-apply {_entries.Count} share(s): no neutral team donor.");
            return;
        }
        int applied = 0, missing = 0;
        foreach (var entry in _entries)
        {
            var container = ResolveEntry(entry);
            if (container == Entity.Null) { missing++; continue; }
            ApplyPublicTeam(container);
            applied++;
        }
        Core.Log.LogInfo($"[Uriel SHARE] re-applied public team to {applied} container(s); {missing} entry(ies) did not resolve (kept; '.uriel unshareall' to purge).");
    }
}
