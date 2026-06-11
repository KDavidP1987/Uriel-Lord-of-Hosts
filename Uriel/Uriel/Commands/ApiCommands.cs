using Uriel.Config;
using VampireCommandFramework;

namespace Uriel.Commands;

/// <summary>
/// Structured chat output for Raphael (the client-side companion mod, formerly BloodCraftHub; "BCH"
/// in the internal handoff) — the [URIEL:*] wire API (handoff §6, mirroring Beelzebub's [BEELZ:*] pattern).
///
/// Wire format: every line begins "[URIEL:&lt;tag&gt;]" followed by space-separated
/// "key=value" tokens (bare; prefab names are [A-Za-z0-9_] by V Rising convention). Lists are
/// paged — one reply per page, kept under VCF's 512-byte cap — and terminated with
/// "[URIEL:end] cmd=&lt;name&gt; page=&lt;x&gt;/&lt;y&gt; count=&lt;n&gt;". Read-only and available to
/// players (BCH pulls per-player data) whenever object spawning is enabled.
///
/// Current ApiVersion = 1: object catalog (total prefab list) + per-player unlocks.
/// </summary>
[CommandGroup("uriel api")]
internal static class ApiCommands
{
    const int ApiVersion = 1;

    static bool Ready(ChatCommandContext ctx)
    {
        if (!Core.IsReady) { ctx.Reply("[URIEL:err] code=notready"); return false; }
        if (!Settings.ObjectSpawn_Enabled.Value) { ctx.Reply("[URIEL:err] code=disabled"); return false; }
        return true;
    }

    [Command("version", description: "Raphael: object-spawn API version, capabilities + catalog totals.")]
    public static void Version(ChatCommandContext ctx)
    {
        if (!Ready(ctx)) return;
        ctx.Reply(Core.ObjectSpawn.ApiVersion(ApiVersion));
    }

    [Command("catalog", description: "Raphael: the total prefab list available in-game (paged). Usage: .uriel api catalog [page]")]
    public static void Catalog(ChatCommandContext ctx, int page = 1)
    {
        if (!Ready(ctx)) return;
        // One ctx.Reply per wire line — BCH treats each System-chat message as a single [URIEL:*] line
        // and does NOT split on '\n' (mirrors Beelzebub). Sending the page as one '\n'-joined block left
        // the [URIEL:object]/[URIEL:end] rows unparsed → "no rows" (handoff §6 P0).
        foreach (var line in Core.ObjectSpawn.ApiCatalogPage(page)) ctx.Reply(line);
    }

    [Command("unlocked", description: "Raphael: your unlocked prefabs + collection percentage (paged). Usage: .uriel api unlocked [page]")]
    public static void Unlocked(ChatCommandContext ctx, int page = 1)
    {
        if (!Ready(ctx)) return;
        // One ctx.Reply per wire line (see Catalog above).
        foreach (var line in Core.ObjectSpawn.ApiUnlockedPage(ctx.Event.User.PlatformId, page)) ctx.Reply(line);
    }
}
