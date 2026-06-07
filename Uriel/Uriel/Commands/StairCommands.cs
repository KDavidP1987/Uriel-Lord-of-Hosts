using Uriel.Config;
using VampireCommandFramework;

namespace Uriel.Commands;

/// <summary>
/// Stair hot-swap commands (docs/features/STAIR_HOTSWAP.md). Aim at a placed
/// staircase; the archetype (Single / CW / CCW / Double) is auto-detected and
/// only same-archetype cosmetics are offered. DLC styles require owning the DLC
/// (exactly the build-menu rule).
/// </summary>
[CommandGroup("uriel")]
internal static class StairCommands
{
    static bool Ready(ChatCommandContext ctx)
    {
        if (!Core.IsReady) { ctx.Reply("Uriel is not yet initialized."); return false; }
        if (!Settings.StairSwap_Enabled.Value)
        {
            ctx.Reply("Stair swapping is disabled by the server admin.");
            return false;
        }
        return true;
    }

    [Command("stairswap", description: "Swap the aimed stair to another style of the same shape. Usage: .uriel stairswap <stone1|stone2|stone3|gloomrot|projectk|strongblade|next>")]
    public static void StairSwap(ChatCommandContext ctx, string style, string mode = null)
    {
        if (!Ready(ctx)) return;
        bool nearest = "nearest".Equals(mode?.Trim(), System.StringComparison.OrdinalIgnoreCase);
        Core.StairSwap.Swap(ctx.Event.SenderCharacterEntity, ctx.Event.SenderUserEntity, style, out string message, nearest);
        ctx.Reply(message);
    }

    [Command("stairstyles", description: "Show the aimed stair's shape, current style, and the styles available to you.")]
    public static void StairStyles(ChatCommandContext ctx, string mode = null)
    {
        if (!Ready(ctx)) return;
        bool nearest = "nearest".Equals(mode?.Trim(), System.StringComparison.OrdinalIgnoreCase);
        ctx.Reply(Core.StairSwap.DescribeStyles(ctx.Event.SenderCharacterEntity, ctx.Event.SenderUserEntity, nearest));
    }
}
