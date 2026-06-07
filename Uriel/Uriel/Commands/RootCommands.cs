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
    [Command("uriel", description: "Uriel overview — what the mod does and how to start.")]
    public static void Uriel(ChatCommandContext ctx)
    {
        ctx.Reply("Uriel, Lord of Hosts — a host of server enhancements.");
        ctx.Reply($"Version {MyPluginInfo.PLUGIN_VERSION}. Features arrive incrementally; each is admin-toggleable in config.");
        ctx.Reply("Public storage: aim at your chest → .uriel share / .uriel unshare / .uriel shared");
        ctx.Reply("Use .help uriel for the full command list.");
    }
}
