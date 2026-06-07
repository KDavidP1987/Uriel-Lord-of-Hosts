using System.Text;
using Uriel.Config;
using Uriel.Services;
using VampireCommandFramework;

namespace Uriel.Commands;

/// <summary>
/// Public-storage feature commands. Player aims at a placed castle container and
/// shares/unshares it. Per-container opt-in by design — never server-wide
/// (docs/features/PUBLIC_STORAGE.md).
/// </summary>
[CommandGroup("uriel")]
internal static class ShareCommands
{
    static bool Ready(ChatCommandContext ctx)
    {
        if (!Core.IsReady) { ctx.Reply("Uriel is not yet initialized."); return false; }
        if (!Settings.PublicStorage_Enabled.Value)
        {
            ctx.Reply("Public storage is disabled by the server admin.");
            return false;
        }
        return true;
    }

    // VCF 0.10.x has no rest-of-line ([Remainder]) parameter, so stacked modifiers
    // arrive as up to 10 optional tokens and are parsed by hand. Examples:
    //   .uriel share
    //   .uriel share limithours 6
    //   .uriel share LimitHours 6 Cost 123456789 100 Permission Take   (any order)
    [Command("share", description: "Share the aimed container. Optional stackable modifiers (any order): permission take|give|givetake, limithours <h>, limitwithdrawal <stacks>, cost <itemId> <amount>, nearest (target nearest container instead of aimed — for UI buttons).")]
    public static void Share(ChatCommandContext ctx,
        string m1 = null, string m2 = null, string m3 = null, string m4 = null, string m5 = null,
        string m6 = null, string m7 = null, string m8 = null, string m9 = null, string m10 = null)
    {
        if (!Ready(ctx)) return;
        var character = ctx.Event.SenderCharacterEntity;

        // Collect supplied tokens; "nearest" switches targeting (BCH UI buttons —
        // clicking a panel leaves the aim ray pointing anywhere).
        var tokens = new System.Collections.Generic.List<string>();
        bool nearest = false;
        foreach (var t in new[] { m1, m2, m3, m4, m5, m6, m7, m8, m9, m10 })
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            if (t.Trim().Equals("nearest", System.StringComparison.OrdinalIgnoreCase)) { nearest = true; continue; }
            tokens.Add(t.Trim());
        }

        var container = Core.PublicStorage.ResolveTargetContainer(character, out string err, nearest);
        if (container == Unity.Entities.Entity.Null) { ctx.Reply(err); return; }

        // ---- Parse ALL modifiers first; nothing is applied if any token is invalid. ----
        var actions = new System.Collections.Generic.List<(string Kind, string S, double D, int A, int B)>();
        const string usage = "Usage: .uriel share [permission take|give|givetake] [limithours <h>] [limitwithdrawal <stacks>] [cost <itemId> <amount>] — modifiers stack in any order.";
        for (int i = 0; i < tokens.Count; i++)
        {
            switch (tokens[i].ToLowerInvariant())
            {
                case "permission":
                    if (i + 1 >= tokens.Count) { ctx.Reply($"'permission' needs a value (take|give|givetake). {usage}"); return; }
                    actions.Add(("permission", tokens[++i], 0, 0, 0));
                    break;
                case "limithours":
                    if (i + 1 >= tokens.Count || !double.TryParse(tokens[i + 1], out double h))
                    { ctx.Reply($"'limithours' needs a number. {usage}"); return; }
                    i++;
                    actions.Add(("limithours", null, h, 0, 0));
                    break;
                case "limitwithdrawal":
                    if (i + 1 >= tokens.Count || !int.TryParse(tokens[i + 1], out int n))
                    { ctx.Reply($"'limitwithdrawal' needs a whole number. {usage}"); return; }
                    i++;
                    actions.Add(("limitwithdrawal", null, 0, n, 0));
                    break;
                case "cost":
                    if (i + 2 >= tokens.Count || !int.TryParse(tokens[i + 1], out int itemId) || !int.TryParse(tokens[i + 2], out int amount))
                    { ctx.Reply($"'cost' needs <itemId> <amount> (find ids: .uriel finditem <name>). {usage}"); return; }
                    i += 2;
                    actions.Add(("cost", null, 0, itemId, amount));
                    break;
                default:
                    ctx.Reply($"Unknown modifier '{tokens[i]}'. {usage}");
                    return;
            }
        }

        // ---- Share (if needed), then apply modifiers in the order given. ----
        // Admins may share/adjust ANY container (override); players only their own.
        bool isAdmin = ctx.User.IsAdmin;
        bool wasShared = Core.PublicStorage.IsShared(container);
        if (!wasShared)
        {
            if (!Core.PublicStorage.Share(character, container, out string shareMsg, isAdmin)) { ctx.Reply(shareMsg); return; }
            ctx.Reply(shareMsg);
        }
        else if (actions.Count == 0)
        {
            ctx.Reply("That container is already public. " + Core.PublicStorage.BuildInfoText(container));
            return;
        }

        foreach (var a in actions)
        {
            string message = a.Kind switch
            {
                "permission" => Msg(Core.PublicStorage.SetPermission(character, container, a.S, out var m, isAdmin), m),
                "limithours" => Msg(Core.PublicStorage.SetLimitHours(character, container, a.D, out var m, isAdmin), m),
                "limitwithdrawal" => Msg(Core.PublicStorage.SetLimitWithdrawal(character, container, a.A, out var m, isAdmin), m),
                "cost" => Msg(Core.PublicStorage.SetCost(character, container, a.A, a.B, out var m, isAdmin), m),
                _ => null,
            };
            if (message is not null) ctx.Reply(message);
        }

        static string Msg(bool _, string m) => m;
    }

    [Command("unshare", description: "Make the public container you're aiming at private again ((or '.uriel unshare nearest' for the closest one).")]
    public static void Unshare(ChatCommandContext ctx, string mode = null)
    {
        if (!Ready(ctx)) return;
        bool nearest = "nearest".Equals(mode?.Trim(), System.StringComparison.OrdinalIgnoreCase);
        var character = ctx.Event.SenderCharacterEntity;
        var container = Core.PublicStorage.ResolveTargetContainer(character, out string err, nearest);
        if (container == Unity.Entities.Entity.Null) { ctx.Reply(err); return; }

        Core.PublicStorage.Unshare(character, container, ctx.User.IsAdmin, out string message);
        ctx.Reply(message);
    }

    [Command("shared", description: "List every container YOU have made public, with its full sharing rules.")]
    public static void Shared(ChatCommandContext ctx)
    {
        if (!Ready(ctx)) return;
        ulong steamId = ctx.Event.SenderCharacterEntity.GetSteamId();
        var sb = new StringBuilder();
        int n = 0;
        foreach (var e in Core.PublicStorage.Entries)
        {
            if (e.SharedBySteamId != steamId) continue;
            n++;
            sb.AppendLine($"{n}. {Core.PublicStorage.DescribeEntry(e)}");
        }
        ctx.Reply(n == 0
            ? "You have no public containers. Aim at one of your chests and use '.uriel share'."
            : $"Your public containers ({n}) — bulk revert with '.uriel unsharemine':\n{sb}");
    }

    [Command("unsharemine", description: "Revert EVERY container you shared (or control) back to private, in bulk.")]
    public static void UnshareMine(ChatCommandContext ctx)
    {
        if (!Ready(ctx)) return;
        var (restored, purged) = Core.PublicStorage.UnshareMine(ctx.Event.SenderCharacterEntity);
        ctx.Reply(restored + purged == 0
            ? "You have no public containers to revert."
            : $"Reverted {restored} of your container(s) to private" + (purged > 0 ? $"; purged {purged} stale entry(ies)." : "."));
    }

    [Command("sharedall", adminOnly: true, description: "(admin) List ALL public containers, or one player's: .uriel sharedall [name|steamId]")]
    public static void SharedAll(ChatCommandContext ctx, string playerFilter = null)
    {
        if (!Core.IsReady) { ctx.Reply("Uriel is not yet initialized."); return; }
        ulong filterId = 0;
        if (!string.IsNullOrWhiteSpace(playerFilter))
        {
            if (!EntityExtensions.TryResolvePlayer(playerFilter, out filterId, out string name, out string err)) { ctx.Reply(err); return; }
            ctx.Reply($"Filtering by {name} ({filterId}).");
        }
        var sb = new StringBuilder();
        int n = 0;
        foreach (var e in Core.PublicStorage.Entries)
        {
            if (filterId != 0 && e.SharedBySteamId != filterId) continue;
            n++;
            sb.AppendLine($"{n}. {Core.PublicStorage.DescribeEntry(e)} by {e.SharedBySteamId}");
        }
        ctx.Reply(n == 0 ? "No matching public containers." : $"Public containers ({n}):\n{sb}");
    }

    [Command("unshareplayer", adminOnly: true, description: "(admin) Revert ALL of one player's public containers: .uriel unshareplayer <name|steamId>")]
    public static void UnsharePlayer(ChatCommandContext ctx, string nameOrId)
    {
        if (!Core.IsReady) { ctx.Reply("Uriel is not yet initialized."); return; }
        if (!EntityExtensions.TryResolvePlayer(nameOrId, out ulong steamId, out string name, out string err)) { ctx.Reply(err); return; }
        var (restored, purged) = Core.PublicStorage.UnshareBySteamId(steamId);
        ctx.Reply(restored + purged == 0
            ? $"{name} ({steamId}) has no public containers."
            : $"Reverted {restored} container(s) shared by {name} ({steamId})" + (purged > 0 ? $"; purged {purged} stale entry(ies)." : "."));
    }

    [Command("takeprisoner", description: "Take the prisoner from the SHARED cell you're aiming at — they're subdued and released to you (have Dominating Presence ready).")]
    public static void TakePrisoner(ChatCommandContext ctx, string mode = null)
    {
        if (!Core.IsReady) { ctx.Reply("Uriel is not yet initialized."); return; }
        if (!Settings.PublicStorage_Enabled.Value || !Settings.PublicPrison_Enabled.Value)
        {
            ctx.Reply("Prison-cell sharing is disabled by the server admin.");
            return;
        }
        bool nearest = "nearest".Equals(mode?.Trim(), System.StringComparison.OrdinalIgnoreCase);
        var character = ctx.Event.SenderCharacterEntity;
        var container = Core.PublicStorage.ResolveTargetContainer(character, out string err, nearest);
        if (container == Unity.Entities.Entity.Null) { ctx.Reply(err); return; }

        // Authorization: the cell must be SHARED (then anyone may take), or the
        // caller controls the castle / is an admin.
        var entry = Core.PublicStorage.IsShared(container) ? "shared" : null;
        bool allowed = entry is not null
            || ctx.User.IsAdmin
            || Services.PublicStorageService.CharacterControlsContainer(character, container);
        if (!allowed)
        {
            ctx.Reply("That cell isn't shared — only its owners (or admins) can take the prisoner.");
            return;
        }

        Services.PrisonerService.TakePrisoner(character, ctx.Event.SenderUserEntity, container, out string message);
        ctx.Reply(message);
    }

    [Command("sharedebug", adminOnly: true, description: "(admin) Dump the aimed container's live sharing state (team, castle link, prisoner) for diagnostics.")]
    public static void ShareDebug(ChatCommandContext ctx)
    {
        if (!Core.IsReady) { ctx.Reply("Uriel is not yet initialized."); return; }
        var container = Core.PublicStorage.ResolveTargetContainer(ctx.Event.SenderCharacterEntity, out string err);
        if (container == Unity.Entities.Entity.Null) { ctx.Reply(err); return; }
        string text = Core.PublicStorage.BuildDebugText(container);
        Core.Log.LogInfo($"[Uriel SHARE][debug]\n{text}");
        ctx.Reply(text);
    }

    [Command("unshareall", adminOnly: true, description: "(admin) Revert EVERY public container to private and clear the registry.")]
    public static void UnshareAll(ChatCommandContext ctx)
    {
        if (!Core.IsReady) { ctx.Reply("Uriel is not yet initialized."); return; }
        int restored = Core.PublicStorage.UnshareAll(out int unresolved);
        ctx.Reply($"Reverted {restored} container(s) to private; purged {unresolved} stale entry(ies).");
    }

    [Command("info", description: "Show the sharing rules of the aimed container (or '.uriel info nearest' for the closest one).")]
    public static void Info(ChatCommandContext ctx, string mode = null)
    {
        if (!Core.IsReady) { ctx.Reply("Uriel is not yet initialized."); return; }
        bool nearest = "nearest".Equals(mode?.Trim(), System.StringComparison.OrdinalIgnoreCase);
        var container = Core.PublicStorage.ResolveTargetContainer(ctx.Event.SenderCharacterEntity, out string err, nearest);
        if (container == Unity.Entities.Entity.Null) { ctx.Reply(err); return; }
        ctx.Reply(Core.PublicStorage.BuildInfoText(container));
    }

    [Command("paychest", description: "Designate the aimed PRIVATE chest to receive cost payments (or '.uriel paychest nearest').")]
    public static void PayChest(ChatCommandContext ctx, string mode = null)
    {
        if (!Ready(ctx)) return;
        bool nearest = "nearest".Equals(mode?.Trim(), System.StringComparison.OrdinalIgnoreCase);
        var character = ctx.Event.SenderCharacterEntity;
        var container = Core.PublicStorage.ResolveTargetContainer(character, out string err, nearest);
        if (container == Unity.Entities.Entity.Null) { ctx.Reply(err); return; }
        Core.PublicStorage.SetPayChest(character, container, out string message);
        ctx.Reply(message);
    }

    [Command("finditem", description: "Search the item catalog by name. Usage: .uriel finditem <name fragment>")]
    public static void FindItem(ChatCommandContext ctx, string fragment)
    {
        if (!Core.IsReady) { ctx.Reply("Uriel is not yet initialized."); return; }
        if (Core.ItemCatalog is null || Core.ItemCatalog.Count == 0) { ctx.Reply("Item catalog not available."); return; }
        var results = Core.ItemCatalog.Search(fragment, max: 8, out int total);
        if (total == 0) { ctx.Reply($"No items match '{fragment}'."); return; }
        var sb = new StringBuilder();
        sb.AppendLine($"Items matching '{fragment}' ({(total > 8 ? $"showing 8 of {total} — refine your search" : $"{total} match(es)")}):");
        foreach (var (name, guid) in results) sb.AppendLine($"{name} → {guid}");
        ctx.Reply(sb.ToString());
    }
}

// NOTE: share modifiers are parsed inside the `share` command itself (stacked,
// any order) rather than as a "uriel share" subcommand group — VCF 0.10.x can't
// mix a group with a variadic sibling command without ambiguity.
