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
}
