using System;
using Uriel.Config;
using VampireCommandFramework;

namespace Uriel.Commands;

/// <summary>
/// Object spawning commands (docs/features/OBJECT_SPAWNING.md). Phase 1.5 admin
/// test-bed: spawn a prefab INSIDE a castle plot you own (admins: any plot), make it
/// indestructible by default, move/rotate/remove it, list or purge a whole plot, and
/// search the placeable-object catalog. A curated, cost-gated player path ('.uriel decor')
/// is a later phase.
/// </summary>
[CommandGroup("uriel")]
internal static class ObjectCommands
{
    static bool Ready(ChatCommandContext ctx)
    {
        if (!Core.IsReady) { ctx.Reply("Uriel is not yet initialized."); return false; }
        if (!Settings.ObjectSpawn_Enabled.Value)
        {
            ctx.Reply("Object spawning is disabled by the server admin (ObjectSpawn.Enabled).");
            return false;
        }
        if (Settings.ObjectSpawn_AdminOnly.Value && !ctx.Event.User.IsAdmin)
        {
            ctx.Reply("Object spawning is admin-only on this server (ObjectSpawn.AdminOnly).");
            return false;
        }
        return true;
    }

    [Command("spawn", description: "Spawn a prefab INSIDE the castle plot you're in, at your aim point. Usage: .uriel spawn <name|guid> [rot 0-3] [breakable]. Indestructible by default. Players: own plot only; admins: any plot.")]
    public static void Spawn(ChatCommandContext ctx, string prefab, int rotation = 0, string flag = null)
    {
        if (!Ready(ctx)) return;
        bool? breakable = null;
        if ("breakable".Equals(flag?.Trim(), StringComparison.OrdinalIgnoreCase)) breakable = true;
        else if ("indestructible".Equals(flag?.Trim(), StringComparison.OrdinalIgnoreCase)) breakable = false;
        Core.ObjectSpawn.Spawn(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin, prefab, rotation, breakable, out string message);
        ctx.Reply(message);
    }

    [Command("despawn", description: "Remove the nearest object you spawned (aim at it or stand near it). Usage: .uriel despawn")]
    public static void Despawn(ChatCommandContext ctx)
    {
        if (!Ready(ctx)) return;
        Core.ObjectSpawn.Despawn(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin, out string message);
        ctx.Reply(message);
    }

    [Command("move", description: "Move the nearest object you spawned to your aim point. Stand near the object, aim where you want it, then run. Usage: .uriel move")]
    public static void Move(ChatCommandContext ctx)
    {
        if (!Ready(ctx)) return;
        Core.ObjectSpawn.Move(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin, out string message);
        ctx.Reply(message);
    }

    [Command("rotate", description: "Rotate the nearest object you spawned. No arg turns it 90 degrees; 0-3 sets that tile rotation. Usage: .uriel rotate [0-3]")]
    public static void Rotate(ChatCommandContext ctx, int rotation = -1)
    {
        if (!Ready(ctx)) return;
        int? rot = rotation < 0 ? (int?)null : rotation;
        Core.ObjectSpawn.Rotate(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin, rot, out string message);
        ctx.Reply(message);
    }

    [Command("spawnlist", description: "List the Uriel-spawned objects on the castle plot you're standing in. Usage: .uriel spawnlist")]
    public static void SpawnList(ChatCommandContext ctx)
    {
        if (!Ready(ctx)) return;
        ctx.Reply(Core.ObjectSpawn.ListOnPlot(ctx.Event.SenderCharacterEntity));
    }

    [Command("purgeplot", description: "Remove ALL Uriel-spawned objects on the castle plot you're standing in. Usage: .uriel purgeplot", adminOnly: true)]
    public static void PurgePlot(ChatCommandContext ctx)
    {
        if (!Ready(ctx)) return;
        Core.ObjectSpawn.PurgePlot(ctx.Event.SenderCharacterEntity, out string message);
        ctx.Reply(message);
    }

    [Command("spawninfo", description: "Inspect the nearest object you spawned (prefab, ownership, indestructible/decay flags, plot).")]
    public static void SpawnInfo(ChatCommandContext ctx)
    {
        if (!Ready(ctx)) return;
        ctx.Reply(Core.ObjectSpawn.DescribeNearest(ctx.Event.SenderCharacterEntity));
    }

    [Command("unlocks", description: "List the objects you've unlocked for building. Admins can pass a player name to view theirs. Usage: .uriel unlocks [player]")]
    public static void Unlocks(ChatCommandContext ctx, string player = null)
    {
        if (!Ready(ctx)) return;
        if (string.IsNullOrWhiteSpace(player))
        {
            ctx.Reply(Core.ObjectSpawn.DescribeUnlocks(ctx.Event.User.PlatformId));
            return;
        }
        if (!ctx.Event.User.IsAdmin) { ctx.Reply("Only admins can view another player's unlocks."); return; }
        if (!EntityExtensions.TryResolvePlayer(player, out ulong steamId, out string name, out string err)) { ctx.Reply(err); return; }
        ctx.Reply($"{name}: {Core.ObjectSpawn.DescribeUnlocks(steamId)}");
    }

    [Command("grant", description: "Admin: unlock an object for a player to build — covers non-destroyable objects discovery can't reach. Usage: .uriel grant <player> <name|guid>", adminOnly: true)]
    public static void Grant(ChatCommandContext ctx, string player, string prefab)
    {
        if (!Ready(ctx)) return;
        if (!EntityExtensions.TryResolvePlayer(player, out ulong steamId, out string name, out string err)) { ctx.Reply(err); return; }
        Core.ObjectSpawn.GrantUnlock(steamId, prefab, out string message);
        ctx.Reply($"{name}: {message}");
    }

    [Command("revoke", description: "Admin: remove an object from a player's unlocks. Usage: .uriel revoke <player> <name|guid>", adminOnly: true)]
    public static void Revoke(ChatCommandContext ctx, string player, string prefab)
    {
        if (!Ready(ctx)) return;
        if (!EntityExtensions.TryResolvePlayer(player, out ulong steamId, out string name, out string err)) { ctx.Reply(err); return; }
        Core.ObjectSpawn.RevokeUnlock(steamId, prefab, out string message);
        ctx.Reply($"{name}: {message}");
    }

    [Command("grantall", description: "Admin: grant a player ALL placeable objects (or a subset). Usage: .uriel grantall <player> [all|destructible|indestructible]", adminOnly: true)]
    public static void GrantAll(ChatCommandContext ctx, string player, string mode = "all")
    {
        if (!Ready(ctx)) return;
        if (!EntityExtensions.TryResolvePlayer(player, out ulong steamId, out string name, out string err)) { ctx.Reply(err); return; }
        Core.ObjectSpawn.GrantAll(steamId, mode, out string message);
        ctx.Reply($"{name}: {message}");
    }

    [Command("findprefab", description: "Search spawnable placeable objects for '.uriel spawn'. Usage: .uriel findprefab <text> [page]")]
    public static void FindPrefab(ChatCommandContext ctx, string text, int page = 1)
    {
        if (!Ready(ctx)) return;
        ctx.Reply(Core.ObjectSpawn.FindPrefabs(text, page));
    }

    [Command("catalog", description: "Browse the full placeable-object catalog (paged) and see totals (total + discoverable). Usage: .uriel catalog [page]")]
    public static void Catalog(ChatCommandContext ctx, int page = 1)
    {
        if (!Ready(ctx)) return;
        ctx.Reply(Core.ObjectSpawn.BrowseCatalog(page));
    }

    [Command("notify", description: "Turn your object-discovery chat messages on or off. Usage: .uriel notify <on|off>")]
    public static void Notify(ChatCommandContext ctx, string state)
    {
        if (!Ready(ctx)) return;
        string s = state?.Trim().ToLowerInvariant();
        if (s is not ("on" or "off")) { ctx.Reply("Usage: .uriel notify <on|off>"); return; }
        Core.PlayerUnlock.SetNotify(ctx.Event.User.PlatformId, s == "on");
        ctx.Reply(s == "on"
            ? "Discovery messages ON — you'll be told when you unlock a new object."
            : "Discovery messages OFF — you'll no longer be notified about objects you unlock.");
    }

    [Command("block", description: "Admin: block a prefab (by GUID or name) so it can't be spawned/discovered/granted. Usage: .uriel block <guid|name>", adminOnly: true)]
    public static void Block(ChatCommandContext ctx, string prefab)
    {
        if (!Ready(ctx)) return;
        Core.ObjectSpawn.Block(prefab, out string message);
        ctx.Reply(message);
    }

    [Command("unblock", description: "Admin: allow a previously blocked prefab again (use its GUID from '.uriel blocklist'). Usage: .uriel unblock <guid|name>", adminOnly: true)]
    public static void Unblock(ChatCommandContext ctx, string prefab)
    {
        if (!Ready(ctx)) return;
        Core.ObjectSpawn.Unblock(prefab, out string message);
        ctx.Reply(message);
    }

    [Command("blocklist", description: "Admin: list the blocked prefabs. Usage: .uriel blocklist", adminOnly: true)]
    public static void BlockList(ChatCommandContext ctx)
    {
        if (!Ready(ctx)) return;
        ctx.Reply(Core.ObjectSpawn.DescribeBlocked());
    }

    [Command("bossmap", description: "Admin: edit the boss->object unlock map (defeating the V-blood grants the objects). Usage: .uriel bossmap <add|remove|list> [vblood] [object]", adminOnly: true)]
    public static void BossMap(ChatCommandContext ctx, string action, string vblood = null, string obj = null)
    {
        if (!Ready(ctx)) return;
        switch (action?.Trim().ToLowerInvariant())
        {
            case "list":
                ctx.Reply(Core.ObjectSpawn.DescribeBossMap());
                break;
            case "add":
                if (vblood is null || obj is null) { ctx.Reply("Usage: .uriel bossmap add <vblood> <object>"); return; }
                Core.ObjectSpawn.BossMapAdd(vblood, obj, out string addMsg);
                ctx.Reply(addMsg);
                break;
            case "remove":
                if (vblood is null || obj is null) { ctx.Reply("Usage: .uriel bossmap remove <vblood> <object>"); return; }
                Core.ObjectSpawn.BossMapRemove(vblood, obj, out string rmMsg);
                ctx.Reply(rmMsg);
                break;
            default:
                ctx.Reply("Usage: .uriel bossmap <add|remove|list> [vblood] [object]");
                break;
        }
    }
}
