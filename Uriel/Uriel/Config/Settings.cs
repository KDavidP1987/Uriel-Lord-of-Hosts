using BepInEx.Configuration;

namespace Uriel.Config;

/// <summary>
/// BepInEx config bindings — generated to BepInEx/config/kdpen.Uriel.cfg on the server.
/// Every feature gets a master enable switch so server admins can opt in/out of each
/// capability independently (Uriel is a host of features, not all-or-nothing).
/// </summary>
internal static class Settings
{
    // ---- Feature: stair hot-swap ----
    public static ConfigEntry<bool> StairSwap_Enabled { get; private set; }
    public static ConfigEntry<float> StairSwap_MaxTargetDistance { get; private set; }
    public static ConfigEntry<bool> StairSwap_ExperimentalLiveRefresh { get; private set; }
    public static ConfigEntry<int> StairSwap_RespawnGapFrames { get; private set; }

    // ---- Feature: object spawning (extended build palette / world-object decor) ----
    public static ConfigEntry<bool> ObjectSpawn_Enabled { get; private set; }
    public static ConfigEntry<bool> ObjectSpawn_AdminOnly { get; private set; }
    public static ConfigEntry<float> ObjectSpawn_MaxTargetDistance { get; private set; }
    public static ConfigEntry<bool> ObjectSpawn_Indestructible { get; private set; }
    public static ConfigEntry<bool> ObjectSpawn_PreventOverlap { get; private set; }
    public static ConfigEntry<float> ObjectSpawn_OverlapMinDistance { get; private set; }
    public static ConfigEntry<bool> ObjectSpawn_PurgeOrphansOnBoot { get; private set; }
    public static ConfigEntry<bool> ObjectSpawn_AutoPurgeOrphans { get; private set; }
    public static ConfigEntry<int> ObjectSpawn_OrphanPollSeconds { get; private set; }
    public static ConfigEntry<bool> ObjectSpawn_RespawnEnabled { get; private set; }
    public static ConfigEntry<int> ObjectSpawn_RespawnPollSeconds { get; private set; }
    public static ConfigEntry<bool> ObjectSpawn_IncludeCastleBuildables { get; private set; }
    // Phase 2 — player access (mode + discovery + cost)
    public static ConfigEntry<bool> ObjectSpawn_CollectionEnabled { get; private set; }
    public static ConfigEntry<string> ObjectSpawn_PlayerAccessMode { get; private set; }
    public static ConfigEntry<int> ObjectSpawn_DiscoveryChancePercent { get; private set; }
    public static ConfigEntry<bool> ObjectSpawn_DiscoveryNotify { get; private set; }
    public static ConfigEntry<int> ObjectSpawn_PrefabCostItem { get; private set; }
    public static ConfigEntry<int> ObjectSpawn_PrefabCostStack { get; private set; }
    public static ConfigEntry<bool> ObjectSpawn_RefundOnRemove { get; private set; }
    // Phase 3 — unlocking non-destructible objects
    public static ConfigEntry<string> ObjectSpawn_NonDestructibleUnlock { get; private set; }
    public static ConfigEntry<bool> ObjectSpawn_BossUnlocksEnabled { get; private set; }

    // ---- Feature: public storage (per-container opt-in) ----
    public static ConfigEntry<bool> PublicStorage_Enabled { get; private set; }
    public static ConfigEntry<bool> PublicPrison_Enabled { get; private set; }
    public static ConfigEntry<float> PublicStorage_MaxTargetDistance { get; private set; }

    // ---- Diagnostics ----
    public static ConfigEntry<bool> VerboseLogging { get; private set; }

    public static void Initialize(ConfigFile config)
    {
        StairSwap_Enabled = config.Bind(
            "StairSwap", "Enabled", true,
            "Allow players to hot-swap placed stairs to another cosmetic style of the same shape " +
            "via '.uriel stairswap' without demolishing them first. DLC styles require the player " +
            "to own that DLC (same rule as the build menu).");

        StairSwap_MaxTargetDistance = config.Bind(
            "StairSwap", "MaxTargetDistance", 6f,
            "How close (meters) your aim point must be to a stair for '.uriel stairswap'/'.uriel stairstyles' " +
            "to target it.");

        StairSwap_RespawnGapFrames = config.Bind(
            "StairSwap", "RespawnGapFrames", 5,
            "EXPERIMENTAL ('.uriel stairrespawn'): frames to wait between destroying the old " +
            "staircase and spawning the new-style one, so server + clients register the removal " +
            "before the replacement appears. Higher = longer visible gap but more reliable.");

        StairSwap_ExperimentalLiveRefresh = config.Bind(
            "StairSwap", "ExperimentalLiveRefresh", false,
            "EXPERIMENTAL / ADMIN ONLY. When true, '.uriel stairrefresh' will ATTEMPT a live " +
            "mega-static visual refresh by appending to the per-chunk MegaStaticManager's " +
            "snapshot-replicated destroyed-instance buffer. The durable identity swap is never " +
            "affected by this. Leave false unless you are actively testing: a placed staircase " +
            "renders from a per-chunk static batch baked at load, and dropping an instance with " +
            "no verified live re-add path can make the staircase render INVISIBLE until the next " +
            "server restart (which renders it correctly in the swapped style). With the flag off, " +
            "'.uriel stairrefresh' only dumps diagnostics to the server log and changes nothing.");

        ObjectSpawn_Enabled = config.Bind(
            "ObjectSpawn", "Enabled", true,
            "Allow spawning game objects into a castle via '.uriel spawn' (extended build palette / " +
            "world-object decoration). Objects are adopted by the castle you're standing in.");

        ObjectSpawn_AdminOnly = config.Bind(
            "ObjectSpawn", "AdminOnly", true,
            "When true, only admins may use '.uriel spawn'/'.uriel despawn' (the Phase-1 test-bed default). " +
            "A curated, cost-gated player-facing path ('.uriel decor') is a later phase.");

        ObjectSpawn_MaxTargetDistance = config.Bind(
            "ObjectSpawn", "MaxTargetDistance", 8f,
            "How close (meters) your aim point must be to a Uriel-spawned object for '.uriel despawn'/" +
            "'.uriel spawninfo' to target it.");

        ObjectSpawn_Indestructible = config.Bind(
            "ObjectSpawn", "Indestructible", true,
            "Make spawned objects indestructible (Immortal) AND immune to castle decay by default. " +
            "Turn OFF on PvP servers (so spawned objects obey normal raid rules), or per-spawn with " +
            "'.uriel spawn <prefab> breakable'. Objects meant to be harvested (veins/trees) should be breakable.");

        ObjectSpawn_IncludeCastleBuildables = config.Bind(
            "ObjectSpawn", "IncludeCastleBuildables", false,
            "Whether the catalog includes the inherent CASTLE build-menu pieces (anything with a " +
            "BlueprintData component — ~1200 walls/floors/stations/decor players already build normally). " +
            "Default FALSE: the catalog focuses on WORLD objects players can't otherwise get (the point of " +
            "the feature). Set TRUE to also expose the standard buildables through '.uriel spawn'/catalog. " +
            "(Identified by component, not the word 'Castle' — reliable. Admins can still GUID-spawn any " +
            "prefab regardless.)");

        ObjectSpawn_PreventOverlap = config.Bind(
            "ObjectSpawn", "PreventOverlap", true,
            "Prevent a spawned object from being placed (or moved) onto a tile already occupied by a " +
            "wall, crafting station, native prop, or another spawned object — i.e. it can't be dropped " +
            "INSIDE other build pieces. FLOORS are the exception (you place decor on top of floors). This " +
            "guards against the overlapping pile-ups that can destabilise/crash a server when many objects " +
            "are spawned into the same spot. The check is limited to roughly one building level (objects on " +
            "the floor above/below don't count), so multi-storey castles are unaffected. Set FALSE to allow " +
            "free stacking/overlap (e.g. a candle directly on a table) at your own risk.");

        ObjectSpawn_OverlapMinDistance = config.Bind(
            "ObjectSpawn", "OverlapMinDistance", 0.5f,
            "When PreventOverlap is on, the minimum spacing (meters, center-to-center) Uriel keeps between a " +
            "spawned/moved object and a nearby NON-wall object — the proximity backstop that stops two pieces " +
            "from near-stacking when their tile cells differ by a fraction. Default 0.5 (~one tile). LOWER it " +
            "(toward 0) to let admins/players place décor closer together / tighter around furniture; 0 disables " +
            "this distance check entirely (only the exact same-tile-cell block remains). Raise it to space objects " +
            "out more. NOTE: walls are already exempt, so this does NOT affect how close you can place to a wall — " +
            "that's governed only by the same-cell test. Clamped to a 0 minimum.");

        ObjectSpawn_PurgeOrphansOnBoot = config.Bind(
            "ObjectSpawn", "PurgeOrphansOnBoot", true,
            "On server start, automatically remove any Uriel-spawned object whose castle is gone " +
            "(the castle heart no longer exists — destroyed or fully decayed). This keeps a destroyed " +
            "castle from leaving permanent, indestructible orphan objects floating in the world. Turn " +
            "OFF to keep such objects until an admin removes them manually ('.uriel purgeplot').");

        ObjectSpawn_AutoPurgeOrphans = config.Bind(
            "ObjectSpawn", "AutoPurgeOrphans", true,
            "Periodically scan the map mid-session and remove Uriel-spawned objects whose castle is gone " +
            "(the heart no longer exists — castle abandoned/destroyed/decayed), instead of waiting for the " +
            "next server boot (PurgeOrphansOnBoot). Registry-driven, so ONLY Uriel's own objects are ever " +
            "touched — native world objects are never affected. Turn OFF to clean orphans only at boot or " +
            "via the manual '.uriel purgeorphans'.");

        ObjectSpawn_OrphanPollSeconds = config.Bind(
            "ObjectSpawn", "OrphanPollSeconds", 120,
            "How often (seconds, approximate) the mid-session orphan sweep runs when AutoPurgeOrphans is on. " +
            "The sweep is a whole-map scan, so keep it relatively infrequent; 120s is a good balance. " +
            "Clamped to a 30s minimum.");

        ObjectSpawn_RespawnEnabled = config.Bind(
            "ObjectSpawn", "RespawnEnabled", true,
            "Master switch for AUTO-RESPAWN: objects spawned with the 'respawn' flag " +
            "('.uriel spawn <prefab> breakable respawn') automatically come back after they're destroyed " +
            "(by a raid, decay, or — if 'smashable' — the player), as long as the castle still stands and " +
            "the object wasn't '.uriel despawn'ed. Turn OFF to disable the respawn loop server-wide " +
            "(existing respawn-flagged objects simply stay gone once destroyed).");

        ObjectSpawn_RespawnPollSeconds = config.Bind(
            "ObjectSpawn", "RespawnPollSeconds", 30,
            "How often (seconds, approximate) the auto-respawn loop checks for destroyed respawn-flagged " +
            "objects and brings them back. Lower = snappier respawns but more frequent territory scans; " +
            "30 is a good balance for decor. Clamped to a 5s minimum.");

        ObjectSpawn_CollectionEnabled = config.Bind(
            "ObjectSpawn", "CollectionEnabled", true,
            "Master switch for the prefab-COLLECTION feature (discovery-by-destruction). When false, " +
            "players never unlock objects by destroying them and get no discovery messages — servers that " +
            "don't want a collection mechanic can turn it off. (Admin '.uriel grant' and Full access mode " +
            "still work independently.)");

        ObjectSpawn_PlayerAccessMode = config.Bind(
            "ObjectSpawn", "PlayerAccessMode", "Discovery",
            "How NON-admin players choose what to spawn (only relevant when AdminOnly=false). " +
            "'Full' = any object in the placeable catalog. 'Discovery' = only objects the player has " +
            "personally UNLOCKED by destroying them in the world (see DiscoveryChancePercent), plus any " +
            "an admin granted ('.uriel grant'). Admins always have full access. Non-destroyable objects " +
            "can only ever be obtained via '.uriel grant'.");

        ObjectSpawn_DiscoveryChancePercent = config.Bind(
            "ObjectSpawn", "DiscoveryChancePercent", 25,
            "Discovery mode only: percent chance (0-100) that destroying an eligible world object " +
            "(a destroyable, spawnable object) unlocks it for that player to build. 0 disables " +
            "discovery-by-destruction (grants still work).");

        ObjectSpawn_DiscoveryNotify = config.Bind(
            "ObjectSpawn", "DiscoveryNotify", true,
            "Discovery mode only: send the player a chat message when they unlock a new object by destroying it.");

        ObjectSpawn_PrefabCostItem = config.Bind(
            "ObjectSpawn", "PrefabCostItem", 0,
            "Item PrefabGUID a NON-admin player must pay to build ANY object (e.g. 123456789). " +
            "0 = building is free. Find item ids with '.uriel finditem <name>'. (v1 charges from the " +
            "player's own inventory; castle shared-stash payment is a planned enhancement.)");

        ObjectSpawn_PrefabCostStack = config.Bind(
            "ObjectSpawn", "PrefabCostStack", 0,
            "How many of PrefabCostItem a NON-admin player must have and spend to build one object " +
            "(e.g. 100). Ignored when PrefabCostItem=0.");

        ObjectSpawn_RefundOnRemove = config.Bind(
            "ObjectSpawn", "RefundOnRemove", false,
            "When true, removing your OWN spawned object with '.uriel despawn' refunds the item cost " +
            "you paid for it. Admin spawns and granted-free spawns refund nothing.");

        ObjectSpawn_NonDestructibleUnlock = config.Bind(
            "ObjectSpawn", "NonDestructibleUnlock", "Off",
            "Discovery mode: how a player earns the NON-destructible objects (the ~half that discovery " +
            "can't reach). 'Off' = never (only '.uriel grant'/'grantall'). 'Collection' = on collecting " +
            "100% of the discoverable set. 'FinalBoss' = on defeating Dracula (game completion — easiest, " +
            "recommended). 'AllBosses' = on defeating every main V-blood. (FinalBoss/AllBosses share the " +
            "V-blood kill detection used by BossUnlocksEnabled.)");

        ObjectSpawn_BossUnlocksEnabled = config.Bind(
            "ObjectSpawn", "BossUnlocksEnabled", false,
            "Discovery mode: when a player defeats a V-blood listed in boss_unlocks.json (under " +
            "BepInEx/config/Uriel/), grant them the objects mapped to that boss — a tier-gated way to " +
            "earn otherwise non-destructible objects by beating an area's boss. Build the map in-game " +
            "with '.uriel bossmap add <vblood> <object>' or edit the JSON. Empty by default.");

        PublicStorage_Enabled = config.Bind(
            "PublicStorage", "Enabled", true,
            "Allow a container's owner to mark a SPECIFIC chest/stash as publicly accessible to " +
            "all players on the server. Per-container opt-in; nothing is shared unless its owner shares it.");

        PublicPrison_Enabled = config.Bind(
            "PublicStorage", "PrisonEnabled", true,
            "Allow a prison cell's owner to mark a SPECIFIC cell as publicly accessible: others may " +
            "feed the prisoner, extract blood, or charm the prisoner out as their own subdued follower. " +
            "Separate switch from chest sharing — prison cells are a different container type and are " +
            "governed independently. (Requires PublicStorage.Enabled as the master switch.)");

        PublicStorage_MaxTargetDistance = config.Bind(
            "PublicStorage", "MaxTargetDistance", 5f,
            "How close (meters) your aim point must be to a container for '.uriel share'/'.uriel unshare' " +
            "to target it.");

        VerboseLogging = config.Bind(
            "Diagnostics", "VerboseLogging", false,
            "Emit detailed per-action log lines (useful when testing; noisy in production).");
    }
}
