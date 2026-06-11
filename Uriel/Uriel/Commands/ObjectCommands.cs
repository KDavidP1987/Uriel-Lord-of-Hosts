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

    [Command("spawn", description: "Spawn a prefab in the castle plot you're in. After the name, the rotation (0-3) and any flags may appear IN ANY ORDER: 'breakable' (raid/decay can destroy it), 'smashable' (you can destroy it too), 'respawn' (auto-comes-back until the castle's gone or you despawn it), 'here'/'nearest' (place at YOUR location — use this from a UI button), 'indestructible'. Admin-only 'force' spawns a non-networked object (e.g. an effect zone) that would otherwise be refused — it may be INVISIBLE. Default indestructible. Usage: .uriel spawn <name|guid> [rot 0-3] [flags…]. Players: own plot only; admins: any plot.")]
    public static void Spawn(ChatCommandContext ctx, string prefab, string a1 = null, string a2 = null,
                             string a3 = null, string a4 = null, string a5 = null, string a6 = null)
    {
        if (!Ready(ctx)) return;
        int rotation = 0;
        bool rotSet = false;
        bool? breakable = null;
        bool playerBreakable = false, respawn = false, atFeet = false, force = false;
        // Tokens after the name are order-independent: the FIRST integer is the rotation; everything else is
        // a flag. (Previously the rotation was a fixed int parameter, so a flag typed right after the name —
        // e.g. ".uriel spawn <guid> force" — landed in the rotation slot, failed to parse, and the command
        // silently did nothing. Parsing all trailing tokens as strings fixes that.)
        foreach (var raw in new[] { a1, a2, a3, a4, a5, a6 })
        {
            string f = raw?.Trim();
            if (string.IsNullOrEmpty(f)) continue;
            if (!rotSet && int.TryParse(f, out int r)) { rotation = r & 3; rotSet = true; continue; }
            if ("breakable".Equals(f, StringComparison.OrdinalIgnoreCase)) breakable = true;
            else if ("indestructible".Equals(f, StringComparison.OrdinalIgnoreCase)) breakable = false;
            else if ("smashable".Equals(f, StringComparison.OrdinalIgnoreCase)
                  || "smash".Equals(f, StringComparison.OrdinalIgnoreCase)
                  || "playerbreakable".Equals(f, StringComparison.OrdinalIgnoreCase)) { breakable = true; playerBreakable = true; }
            else if ("respawn".Equals(f, StringComparison.OrdinalIgnoreCase)) respawn = true;
            else if ("force".Equals(f, StringComparison.OrdinalIgnoreCase)
                  || "allowinvisible".Equals(f, StringComparison.OrdinalIgnoreCase)) force = true;
            else if (IsAtFeetToken(f)) atFeet = true;
            // unknown tokens are ignored (forward-compatible)
        }
        Core.ObjectSpawn.Spawn(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin, prefab, rotation,
                               breakable, playerBreakable, respawn, atFeet, force, out string message);
        ctx.Reply(message);
    }

    // "Use my own position, not the cursor/aim" token — for UI-button relays (the cursor is on the
    // panel, so the aim ray points outside the plot). 'nearest' mirrors the storage/stair convention;
    // 'here'/'me' are friendlier aliases for chat.
    static bool IsAtFeetToken(string s)
        => "here".Equals(s, StringComparison.OrdinalIgnoreCase)
        || "nearest".Equals(s, StringComparison.OrdinalIgnoreCase)
        || "me".Equals(s, StringComparison.OrdinalIgnoreCase);

    [Command("despawn", description: "Remove the nearest object you spawned (aim at it or stand near it). Usage: .uriel despawn")]
    public static void Despawn(ChatCommandContext ctx)
    {
        if (!Ready(ctx)) return;
        Core.ObjectSpawn.Despawn(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin, out string message);
        ctx.Reply(message);
    }

    [Command("move", description: "Move the nearest object you spawned to your aim point — or to YOUR location with 'here' (use this from a UI button). Stand near the object; aim where you want it (or pass 'here'), then run. Usage: .uriel move [here]")]
    public static void Move(ChatCommandContext ctx, string flag = null)
    {
        if (!Ready(ctx)) return;
        bool toFeet = IsAtFeetToken(flag?.Trim());
        Core.ObjectSpawn.Move(ctx.Event.SenderCharacterEntity, ctx.Event.User.IsAdmin, toFeet, out string message);
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

    [Command("purgeplot", description: "Remove Uriel's objects on the castle plot you're standing in (live spawns + tracked records). Native objects are never touched. For one specific untracked object, aim at it and use '.uriel forcedespawn'. Usage: .uriel purgeplot", adminOnly: true)]
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

    [Command("forcedespawn", description: "Admin: force-remove the object you're aiming at, IGNORING Uriel ownership/records (recovers untracked objects, e.g. from older builds). Run once to arm (names it), then '.uriel forcedespawn confirm' within 30s. Usage: .uriel forcedespawn [confirm]", adminOnly: true)]
    public static void ForceDespawn(ChatCommandContext ctx, string confirm = null)
    {
        if (!Ready(ctx)) return;
        bool isConfirm = "confirm".Equals(confirm?.Trim(), StringComparison.OrdinalIgnoreCase);
        Core.ObjectSpawn.ForceDespawn(ctx.Event.SenderCharacterEntity, isConfirm, out string message);
        ctx.Reply(message);
    }

    [Command("forcepurgeplot", description: "Same as '.uriel purgeplot' — removes Uriel's objects on the plot (live spawns + records); native objects/plants/trees/build pieces are never touched. (Kept as a synonym; the old chain sweep was removed because it could delete native resources.) Usage: .uriel forcepurgeplot", adminOnly: true)]
    public static void ForcePurgePlot(ChatCommandContext ctx)
    {
        if (!Ready(ctx)) return;
        Core.ObjectSpawn.ForcePurgePlot(ctx.Event.SenderCharacterEntity, out string message);
        ctx.Reply(message);
    }

    [Command("purgeorphans", description: "Admin: scan the WHOLE map and remove orphaned Uriel objects — ones whose castle heart is gone (castle destroyed/decayed) or that sit where no living heart governs them. Only Uriel's own objects are touched; native objects are never affected. Server-wide on-demand backup for the automatic boot-time cleanup. Usage: .uriel purgeorphans", adminOnly: true)]
    public static void PurgeOrphans(ChatCommandContext ctx)
    {
        if (!Ready(ctx)) return;
        Core.ObjectSpawn.PurgeOrphans(ctx.Event.SenderCharacterEntity, out string message);
        ctx.Reply(message);
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

    [Command("objcfg", description: "Admin: set spawn conditions for ONE object (players only; admins bypass). Fields: max <n> (max per plot, 0=unlimited) | cost <amount> <itemGuid> (0=free) | indestructible <true|false> | respawn <true|false> | clear | show. Usage: .uriel objcfg <name|guid> <field> [v1] [v2]", adminOnly: true)]
    public static void ObjCfg(ChatCommandContext ctx, string prefab, string field = "show", string v1 = null, string v2 = null)
    {
        if (!Ready(ctx)) return;
        Core.ObjectSpawn.ConfigureObject(prefab, field, v1, v2, out string message);
        ctx.Reply(message);
    }

    [Command("objcfgglobal", description: "Admin: set the GLOBAL DEFAULT spawn condition applied to every object unless that object overrides it (players only). Same fields as objcfg. Usage: .uriel objcfgglobal <field> [v1] [v2]", adminOnly: true)]
    public static void ObjCfgGlobal(ChatCommandContext ctx, string field = "show", string v1 = null, string v2 = null)
    {
        if (!Ready(ctx)) return;
        Core.ObjectSpawn.ConfigureGlobal(field, v1, v2, out string message);
        ctx.Reply(message);
    }

    [Command("objcfglist", description: "Admin: list the global + per-object spawn conditions. Usage: .uriel objcfglist", adminOnly: true)]
    public static void ObjCfgList(ChatCommandContext ctx)
    {
        if (!Ready(ctx)) return;
        ctx.Reply(Core.ObjectSpawn.DescribeAllConditions());
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
