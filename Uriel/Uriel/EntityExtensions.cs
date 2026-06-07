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
