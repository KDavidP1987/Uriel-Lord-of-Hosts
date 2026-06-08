using VampireCommandFramework;

namespace Uriel.Commands;

/// <summary>
/// Top-level `.uriel` command (no subcommand) prints a short overview so players
/// who don't know the syntax aren't dropped into a wall of help text. Lives outside
/// any [CommandGroup] so VCF resolves bare `.uriel` to this method while
/// `.uriel <subcommand>` still resolves into the feature command groups.
/// </summary>
internal static class RootCommands
{
    [Command("uriel", description: "Uriel overview — what the mod does and how to get help.")]
    public static void Uriel(ChatCommandContext ctx)
    {
        ctx.Reply(
            $"Uriel, Lord of Hosts - a host of server enhancements (v{MyPluginInfo.PLUGIN_VERSION}). " +
            "Each feature is admin-toggleable in config.\n" +
            "Features: OBJECTS (collect & build world objects), STORAGE (share chests), STAIRS (restyle stairs).\n" +
            "Type '.uriel help' for the command menu, or jump in: '.uriel help objects | storage | stairs | admin'.");
    }
}
