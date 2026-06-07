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
