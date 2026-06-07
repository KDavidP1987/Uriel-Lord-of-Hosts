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
    [Command("share", description: "Share the aimed container. Optional stackable modifiers (any order): permission take|give|givetake, limithours <h>, limitwithdrawal <stacks>, cost <itemId> <amount>.")]
    public static void Share(ChatCommandContext ctx,
        string m1 = null, string m2 = null, string m3 = null, string m4 = null, string m5 = null,
        string m6 = null, string m7 = null, string m8 = null, string m9 = null, string m10 = null)
    {
        if (!Ready(ctx)) return;
        var character = ctx.Event.SenderCharacterEntity;
        var container = Core.PublicStorage.ResolveTargetContainer(character, out string err);
        if (container == Unity.Entities.Entity.Null) { ctx.Reply(err); return; }

        // Collect supplied tokens.
        var tokens = new System.Collections.Generic.List<string>();
        foreach (var t in new[] { m1, m2, m3, m4, m5, m6, m7, m8, m9, m10 })
            if (!string.IsNullOrWhiteSpace(t)) tokens.Add(t.Trim());

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
        bool wasShared = Core.PublicStorage.IsShared(container);
        if (!wasShared)
        {
            if (!Core.PublicStorage.Share(character, container, out string shareMsg)) { ctx.Reply(shareMsg); return; }
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
                "permission" => Msg(Core.PublicStorage.SetPermission(character, container, a.S, out var m), m),
                "limithours" => Msg(Core.PublicStorage.SetLimitHours(character, container, a.D, out var m), m),
                "limitwithdrawal" => Msg(Core.PublicStorage.SetLimitWithdrawal(character, container, a.A, out var m), m),
                "cost" => Msg(Core.PublicStorage.SetCost(character, container, a.A, a.B, out var m), m),
                _ => null,
            };
            if (message is not null) ctx.Reply(message);
        }

        static string Msg(bool _, string m) => m;
    }

    [Command("unshare", description: "Make the public container you're aiming at private again.")]
    public static void Unshare(ChatCommandContext ctx)
    {
        if (!Ready(ctx)) return;
        var character = ctx.Event.SenderCharacterEntity;
        var container = Core.PublicStorage.ResolveTargetContainer(character, out string err);
        if (container == Unity.Entities.Entity.Null) { ctx.Reply(err); return; }

        Core.PublicStorage.Unshare(character, container, ctx.User.IsAdmin, out string message);
        ctx.Reply(message);
    }

    [Command("shared", description: "List the containers YOU have made public.")]
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
            sb.AppendLine($"{n}. {new Stunlock.Core.PrefabGUID(e.PrefabGuid).GetPrefabName()} @ tile ({e.TileX},{e.TileY}) since {e.SharedAtUtc}");
        }
        ctx.Reply(n == 0
            ? "You have no public containers. Aim at one of your chests and use '.uriel share'."
            : $"Your public containers:\n{sb}");
    }

    [Command("sharedall", adminOnly: true, description: "(admin) List ALL public containers on the server.")]
    public static void SharedAll(ChatCommandContext ctx)
    {
        if (!Core.IsReady) { ctx.Reply("Uriel is not yet initialized."); return; }
        var entries = Core.PublicStorage.Entries;
        if (entries.Count == 0) { ctx.Reply("No public containers on the server."); return; }
        var sb = new StringBuilder();
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            sb.AppendLine($"{i + 1}. {new Stunlock.Core.PrefabGUID(e.PrefabGuid).GetPrefabName()} @ tile ({e.TileX},{e.TileY}) by {e.SharedBySteamId} [{e.ContainerClass}]");
        }
        ctx.Reply($"Public containers ({entries.Count}):\n{sb}");
    }

    [Command("unshareall", adminOnly: true, description: "(admin) Revert EVERY public container to private and clear the registry.")]
    public static void UnshareAll(ChatCommandContext ctx)
    {
        if (!Core.IsReady) { ctx.Reply("Uriel is not yet initialized."); return; }
        int restored = Core.PublicStorage.UnshareAll(out int unresolved);
        ctx.Reply($"Reverted {restored} container(s) to private; purged {unresolved} stale entry(ies).");
    }

    [Command("info", description: "Show the sharing rules of the container you're aiming at (anyone can use this).")]
    public static void Info(ChatCommandContext ctx)
    {
        if (!Core.IsReady) { ctx.Reply("Uriel is not yet initialized."); return; }
        var container = Core.PublicStorage.ResolveTargetContainer(ctx.Event.SenderCharacterEntity, out string err);
        if (container == Unity.Entities.Entity.Null) { ctx.Reply(err); return; }
        ctx.Reply(Core.PublicStorage.BuildInfoText(container));
    }

    [Command("paychest", description: "Designate the PRIVATE chest you're aiming at to receive cost payments from your shared containers.")]
    public static void PayChest(ChatCommandContext ctx)
    {
        if (!Ready(ctx)) return;
        var character = ctx.Event.SenderCharacterEntity;
        var container = Core.PublicStorage.ResolveTargetContainer(character, out string err);
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
