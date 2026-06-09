using BepInEx.Logging;
using ProjectM;
using ProjectM.Scripting;
using Unity.Entities;
using Uriel.Services;

namespace Uriel;

/// <summary>
/// Deferred-initialization hub. V Rising's ECS systems (TypeManager, PrefabCollectionSystem)
/// are NOT ready at Plugin.Load — anything that touches Il2CppType.Of&lt;T&gt; or prefab data
/// must wait until the server world exists and prefabs are populated. Patches call
/// <see cref="TryInitialize"/>; it no-ops until the world is actually ready.
/// (Pattern proven in Beelzebub / KindredCommands.)
/// </summary>
internal static class Core
{
    public static World Server { get; private set; }
    public static EntityManager EntityManager { get; private set; }
    public static PrefabCollectionSystem PrefabCollectionSystem { get; private set; }
    public static ServerScriptMapper ServerScriptMapper { get; private set; }
    public static DebugEventsSystem DebugEventsSystem { get; private set; }
    public static ServerGameManager ServerGameManager => ServerScriptMapper.GetServerGameManager();

    public static PublicStorageService PublicStorage { get; private set; }
    public static ItemCatalogService ItemCatalog { get; private set; }
    public static StairSwapService StairSwap { get; private set; }
    public static ObjectSpawnService ObjectSpawn { get; private set; }
    public static PlayerUnlockService PlayerUnlock { get; private set; }

    public static ManualLogSource Log => Plugin.PluginLog;
    public static bool IsReady { get; private set; }

    static bool _initInProgress;
    static int _initAttempts;

    internal static void TryInitialize(string trigger)
    {
        if (IsReady || _initInProgress) return;
        _initInProgress = true;
        _initAttempts++;
        try
        {
            var server = FindServerWorld();
            if (server is null)
            {
                if (_initAttempts == 1)
                    Log.LogInfo($"Uriel init ({trigger}): Server world not yet present; will retry.");
                return;
            }

            var prefabSystem = server.GetExistingSystemManaged<PrefabCollectionSystem>();
            if (prefabSystem is null || prefabSystem.SpawnableNameToPrefabGuidDictionary.Count == 0)
            {
                if (_initAttempts == 1)
                    Log.LogInfo($"Uriel init ({trigger}): PrefabCollectionSystem not yet populated; will retry.");
                return;
            }

            Server = server;
            EntityManager = server.EntityManager;
            PrefabCollectionSystem = prefabSystem;
            ServerScriptMapper = server.GetExistingSystemManaged<ServerScriptMapper>();
            DebugEventsSystem = server.GetExistingSystemManaged<DebugEventsSystem>();

            // Feature services initialize here (after game data is loaded), in dependency order.
            Tick.StartDriver(); // per-frame driver (deferred actions, e.g. the share-resync blink)
            ItemCatalog = new ItemCatalogService();
            ItemCatalog.Build();
            StairSwap = new StairSwapService();
            PlayerUnlock = new PlayerUnlockService();
            PlayerUnlock.Load();
            ObjectSpawn = new ObjectSpawnService();
            ObjectSpawn.Load();
            // Restore spawned-object state: re-apply Immortal/decay, rebuild the live cache,
            // and purge orphans whose castle is gone (config-gated). Query-heavy, so guarded.
            try { ObjectSpawn.ReapplySpawned(); }
            catch (System.Exception ex) { Log.LogWarning($"[Uriel SPAWN] boot re-apply failed: {ex}"); }
            // Periodic auto-respawn for objects spawned with the 'respawn' flag (config-gated).
            try { ObjectSpawn.StartRespawnLoop(); }
            catch (System.Exception ex) { Log.LogWarning($"[Uriel SPAWN] respawn loop start failed: {ex}"); }
            PublicStorage = new PublicStorageService();
            PublicStorage.Load();
            // Placement teams are restored from the game save; our share state lives only
            // in the registry — re-assert the neutral team on registered containers.
            try { PublicStorage.ReapplyAll(); }
            catch (System.Exception ex) { Log.LogWarning($"[Uriel SHARE] re-apply failed: {ex}"); }

            IsReady = true;
            Log.LogInfo($"Uriel initialized via {trigger} (attempt #{_initAttempts}). Prefab map has {prefabSystem.SpawnableNameToPrefabGuidDictionary.Count} entries.");
        }
        catch (System.Exception ex)
        {
            Log.LogError($"Uriel init ({trigger}) FAILED on attempt #{_initAttempts}: {ex}");
        }
        finally
        {
            _initInProgress = false;
        }
    }

    static World FindServerWorld()
    {
        foreach (var world in World.s_AllWorlds)
        {
            if (world.Name == "Server") return world;
        }
        return null;
    }
}
