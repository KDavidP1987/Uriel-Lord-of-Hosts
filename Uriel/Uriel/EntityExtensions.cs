using ProjectM;
using ProjectM.Network;
using Stunlock.Core;
using Unity.Entities;

namespace Uriel;

/// <summary>
/// IL2CPP-safe entity helpers. Mirrors the proven Beelzebub/Bloodcraft extension
/// patterns (Exists guard before every EntityManager call; With() borrow-mutate-write).
/// </summary>
internal static class EntityExtensions
{
    public static bool Exists(this Entity entity) =>
        entity != Entity.Null && Core.EntityManager.Exists(entity);

    public static bool Has<T>(this Entity entity) =>
        entity.Exists() && Core.EntityManager.HasComponent<T>(entity);

    public static T Read<T>(this Entity entity) where T : unmanaged =>
        Core.EntityManager.GetComponentData<T>(entity);

    public static bool TryGetComponent<T>(this Entity entity, out T component) where T : unmanaged
    {
        if (!entity.Exists() || !Core.EntityManager.HasComponent<T>(entity))
        {
            component = default;
            return false;
        }
        component = Core.EntityManager.GetComponentData<T>(entity);
        return true;
    }

    /// <summary>
    /// Borrow a component, mutate via callback, write it back (Bloodcraft's
    /// VExtensions.With pattern). No-op with a warning if the component is missing.
    /// </summary>
    public delegate void RefAction<T>(ref T value) where T : unmanaged;
    public static void With<T>(this Entity entity, RefAction<T> mutator) where T : unmanaged
    {
        if (!entity.Has<T>())
        {
            Core.Log.LogWarning($"[Uriel] Entity.With<{typeof(T).Name}>: component missing on {entity}");
            return;
        }
        T value = Core.EntityManager.GetComponentData<T>(entity);
        mutator(ref value);
        Core.EntityManager.SetComponentData(entity, value);
    }

    /// <summary>
    /// Write a component, adding it first if the entity doesn't already carry it
    /// (the spawn path grafts Immortal / CastleDecayAndRegen onto prefabs that may
    /// lack them). Unlike <see cref="With{T}"/> this never no-ops on a missing component.
    /// </summary>
    public static void AddOrSet<T>(this Entity entity, T value) where T : unmanaged
    {
        if (!entity.Exists()) return;
        if (!Core.EntityManager.HasComponent<T>(entity))
            Core.EntityManager.AddComponent<T>(entity);
        Core.EntityManager.SetComponentData(entity, value);
    }

    /// <summary>Remove a component if present (no-op otherwise).</summary>
    public static void RemoveIfPresent<T>(this Entity entity)
    {
        if (entity.Has<T>())
            Core.EntityManager.RemoveComponent<T>(entity);
    }

    public static ulong GetSteamId(this Entity playerCharacter)
    {
        if (playerCharacter.TryGetComponent<PlayerCharacter>(out var pc)
            && pc.UserEntity.TryGetComponent<User>(out var user))
        {
            return user.PlatformId;
        }
        return 0;
    }

    public static PrefabGUID GetPrefabGuid(this Entity entity) =>
        entity.TryGetComponent<PrefabGUID>(out var g) ? g : default;

    /// <summary>
    /// Resolve a player by character-name fragment OR literal steamId. Walks all
    /// User entities (online + persisted offline). Name matches must be unique.
    /// </summary>
    public static bool TryResolvePlayer(string nameOrId, out ulong steamId, out string resolvedName, out string error)
    {
        steamId = 0;
        resolvedName = null;
        error = null;
        if (string.IsNullOrWhiteSpace(nameOrId)) { error = "Provide a character name or steamId."; return false; }

        if (ulong.TryParse(nameOrId, out ulong literal) && literal > 1000)
        {
            steamId = literal;
            resolvedName = literal.ToString();
            return true;
        }

        var query = Core.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<User>());
        var users = query.ToEntityArray(Unity.Collections.Allocator.Temp);
        int matches = 0;
        try
        {
            for (int i = 0; i < users.Length; i++)
            {
                if (!users[i].TryGetComponent<User>(out var u)) continue;
                string name = u.CharacterName.ToString();
                if (string.IsNullOrEmpty(name)) continue;
                if (!name.Contains(nameOrId, StringComparison.OrdinalIgnoreCase)) continue;
                matches++;
                steamId = u.PlatformId;
                resolvedName = name;
                if (string.Equals(name, nameOrId, StringComparison.OrdinalIgnoreCase)) { matches = 1; break; } // exact match wins
            }
        }
        finally
        {
            users.Dispose();
        }
        if (matches == 0) { error = $"No player matches '{nameOrId}'."; return false; }
        if (matches > 1) { error = $"'{nameOrId}' matches multiple players — be more specific or use the steamId."; steamId = 0; return false; }
        return true;
    }

    /// <summary>
    /// Resolve a prefab GUID to its dev name via the runtime prefab lookup map
    /// (KindredCommands' LookupName pattern). Falls back to the raw hash.
    /// </summary>
    public static string GetPrefabName(this PrefabGUID prefabGuid)
    {
        try
        {
            var map = Core.PrefabCollectionSystem._PrefabLookupMap;
            if (map.GuidToEntityMap.ContainsKey(prefabGuid))
                return map.GetName(prefabGuid);
        }
        catch { /* lookup map shape can vary; raw hash fallback below */ }
        return $"PrefabGuid({prefabGuid._Value})";
    }
}
