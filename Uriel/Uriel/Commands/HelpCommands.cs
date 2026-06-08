using VampireCommandFramework;

namespace Uriel.Commands;

/// <summary>
/// Nested in-game help (`.uriel help [topic]`) — a clean, paginated alternative to VCF's flat
/// `.help uriel` dump, so players without the BloodCraftHub UI can discover features without being
/// overwhelmed. The top level lists feature topics; each topic prints its commands. Every reply is
/// kept well under VCF's 512-byte chat cap; multi-command topics split across replies.
/// </summary>
[CommandGroup("uriel")]
internal static class HelpCommands
{
    [Command("help", description: "Uriel help menu. Usage: .uriel help [objects|storage|stairs|admin]")]
    public static void Help(ChatCommandContext ctx, string topic = null)
    {
        switch (topic?.Trim().ToLowerInvariant())
        {
            case null or "":
                ctx.Reply(
                    "Uriel help - type one of these for that topic's commands:\n" +
                    "  .uriel help objects  - spawn & collect world objects for your castle\n" +
                    "  .uriel help storage  - share chests/stashes with other players\n" +
                    "  .uriel help stairs   - restyle placed stairs without rebuilding\n" +
                    "  .uriel help admin    - admin-only controls");
                break;

            case "objects" or "object" or "spawn" or "spawning" or "decor":
                ctx.Reply(
                    "OBJECTS - build world objects inside your own castle plot:\n" +
                    "  .uriel spawn <name|guid> [rot 0-3] - place an object you've unlocked\n" +
                    "  .uriel move | rotate [0-3] | despawn - move/turn/remove the nearest\n" +
                    "  .uriel unlocks - your collection + %;  .uriel catalog [page] - browse all\n" +
                    "  .uriel findprefab <text> - search by name;  .uriel notify on|off - messages\n" +
                    "  Unlock objects by DESTROYING them in the world (trees/chests/nodes) for a chance.");
                break;

            case "storage" or "share" or "sharing" or "chest":
                ctx.Reply(
                    "STORAGE - make one of your containers public to everyone:\n" +
                    "  Aim at YOUR chest, then .uriel share   (.uriel unshare to revert)\n" +
                    "  share modifiers: permission take|give|givetake, limithours <h>, limitwithdrawal <stacks>, cost <itemId> <amt>\n" +
                    "  .uriel info - shared?;  .uriel shared - your shares;  .uriel unsharemine - revert all\n" +
                    "  .uriel paychest - set a payment chest;  .uriel finditem <name> - item ids;  .uriel takeprisoner - prison cells");
                break;

            case "stairs" or "stair":
                ctx.Reply(
                    "STAIRS - restyle a placed staircase without rebuilding it:\n" +
                    "  Aim at a staircase, then .uriel stairswap <style> - swap its style\n" +
                    "  .uriel stairstyles - list the styles available for that stair\n" +
                    "  .uriel removestairs - remove the staircase cleanly");
                break;

            case "admin" or "admins":
                ctx.Reply(
                    "ADMIN - objects (admin-only):\n" +
                    "  .uriel grant | revoke <player> <obj> - (un)lock an object for a player\n" +
                    "  .uriel grantall <player> [all|destructible|indestructible]\n" +
                    "  .uriel block | unblock <guid>;  .uriel blocklist - forbid/allow prefabs\n" +
                    "  .uriel spawnlist | purgeplot - list/clear objects on your plot\n" +
                    "  .uriel bossmap add|remove|list <vblood> <obj> - boss-defeat unlocks");
                ctx.Reply(
                    "ADMIN - storage/stairs/tools (admin-only):\n" +
                    "  .uriel unshareall | unshareplayer <name> | sharedall | sharedebug\n" +
                    "  .uriel stairpurge | stairrefresh (experimental)\n" +
                    "  .uriel api version|catalog|unlocked - machine API for BloodCraftHub");
                break;

            default:
                ctx.Reply($"Unknown help topic '{topic}'. Try: .uriel help objects | storage | stairs | admin");
                break;
        }
    }
}
