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

    [Command("share", description: "Make the castle container you're aiming at PUBLIC (anyone can use it).")]
    public static void Share(ChatCommandContext ctx)
    {
        if (!Ready(ctx)) return;
        var character = ctx.Event.SenderCharacterEntity;
        var container = Core.PublicStorage.ResolveTargetContainer(character, out string err);
        if (container == Unity.Entities.Entity.Null) { ctx.Reply(err); return; }

        Core.PublicStorage.Share(character, container, out string message);
        ctx.Reply(message);
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

/// <summary>
/// Share-policy modifiers (v0.3.0). Each can be issued on an already-shared
/// container to adjust it, or on an unshared one (it shares first, then applies):
///   .uriel share permission take|give|givetake
///   .uriel share limithours 6
///   .uriel share limitwithdrawal 2
///   .uriel share cost <itemId> <amount>     (itemId 0 = free; find ids: .uriel finditem)
/// </summary>
[CommandGroup("uriel share")]
internal static class SharePolicyCommands
{
    static bool ReadyAndTarget(ChatCommandContext ctx, out Unity.Entities.Entity character, out Unity.Entities.Entity container)
    {
        character = default;
        container = Unity.Entities.Entity.Null;
        if (!Core.IsReady) { ctx.Reply("Uriel is not yet initialized."); return false; }
        if (!Settings.PublicStorage_Enabled.Value) { ctx.Reply("Public storage is disabled by the server admin."); return false; }
        character = ctx.Event.SenderCharacterEntity;
        container = Core.PublicStorage.ResolveTargetContainer(character, out string err);
        if (container == Unity.Entities.Entity.Null) { ctx.Reply(err); return false; }
        return true;
    }

    [Command("permission", description: "Set who-can-do-what on the aimed container: take | give | givetake.")]
    public static void Permission(ChatCommandContext ctx, string mode)
    {
        if (!ReadyAndTarget(ctx, out var character, out var container)) return;
        Core.PublicStorage.SetPermission(character, container, mode, out string message);
        ctx.Reply(message);
    }

    [Command("limithours", description: "Set the rolling window (hours) for the per-player withdrawal limit. 0 removes the limit.")]
    public static void LimitHours(ChatCommandContext ctx, float hours)
    {
        if (!ReadyAndTarget(ctx, out var character, out var container)) return;
        Core.PublicStorage.SetLimitHours(character, container, hours, out string message);
        ctx.Reply(message);
    }

    [Command("limitwithdrawal", description: "Set how many stacks a player may take per window. 0 removes the limit.")]
    public static void LimitWithdrawal(ChatCommandContext ctx, int stacks)
    {
        if (!ReadyAndTarget(ctx, out var character, out var container)) return;
        Core.PublicStorage.SetLimitWithdrawal(character, container, stacks, out string message);
        ctx.Reply(message);
    }

    [Command("cost", description: "Charge per stack withdrawn. Usage: .uriel share cost <itemId> <amount> (0 0 = free).")]
    public static void Cost(ChatCommandContext ctx, int itemId, int amount)
    {
        if (!ReadyAndTarget(ctx, out var character, out var container)) return;
        Core.PublicStorage.SetCost(character, container, itemId, amount, out string message);
        ctx.Reply(message);
    }
}
