using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Behaviours;
using ProjectM.Network;
using ProjectM.Shared;
using ProjectM.Tiles;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using Uriel.Config;

namespace Uriel.Services;

/// <summary>
/// Castle Events Phase 1 (docs/features/CASTLE_EVENTS.md): spawns NPC units into a castle plot with the recipe
/// Nyarlathotep proved (its docs/lessons/SPAWNING_LESSONS_FOR_URIEL.md §1), tracks them in a ledger, and removes them
/// through budgeted queues. Every unit carries:
///   - a marker buff whose SpellLevel holds <see cref="MarkerValue"/>, so the boot sweep finds it after a restart;
///   - a counting LifeTime (Destroy at the end), so the game removes it even with Uriel uninstalled;
///   - DestroyWhenDisabled + CanPreventDisableWhenNoPlayersInRange.CanDisable = false.
/// Units are never persisted: the boot sweep removes every marked unit. Only units in the ledger or carrying our
/// marker are ever touched (a world-wide kill once removed 232 entities at a castle in Nyarlathotep).
/// Log lines are greppable for the vrclient scenarios: [Uriel UNIT] and [Uriel PROBE], key=value fields.
/// </summary>
internal sealed class UnitSpawnService
{
    /// <summary>AB_Consumable_PhysicalPowerPotion_T01_Buff: the marker buff prefab Nyarlathotep proved inert once its
    /// gameplay-event components are stripped.</summary>
    const int MarkerBuffGuid = -1954355403;

    /// <summary>ASCII "URIE". Distinct from Nyarlathotep's "NYAR"/"NYAE" so neither mod's sweep touches the other's
    /// units. SpellLevel.Level is a float: compared as the nearest float, as Nyarlathotep does.</summary>
    public const int MarkerValue = 1431456069;

    static readonly PrefabGUID MarkerBuff = new(MarkerBuffGuid);
    const float HereForward = 2.5f;   // metres in front of the caller for a "here" spawn
    const float SpreadRadius = 1.2f;  // ring radius when one command spawns several units
    const int PumpFrames = 5;         // queue drain cadence (about 6 a second)

    /// <summary>Name fragments that are never spawned (Nyarlathotep docs/GAME_ASSETS.md do-not-spawn list, plus
    /// player-like and castle-owned characters).</summary>
    static readonly string[] RefusedFragments =
    {
        "Mount_Horse", "CarriagePrisonerRelease", "MicroPOI", "Undead_ShadowSoldier",
        "VampireMale", "VampireFemale", "_Servant", "Servant_", "Prisoner", "_Summon", "Dummy", "Test",
    };

    sealed class Tracked
    {
        public Entity Unit;
        public int Guid;
        public string Name;
        public float3 Spot;
        public int Territory;
        public ulong By;
    }

    sealed class Order
    {
        public int Guid;
        public string Name;
        public float3 Spot;
        public int Territory;
        public ulong By;
        public int Level;
    }

    readonly Dictionary<long, Tracked> _ledger = new();
    readonly Queue<Order> _spawnQueue = new();
    readonly Queue<Entity> _despawnQueue = new();
    DateTime _purgeArmedUntil = DateTime.MinValue;
    bool _loopStarted;

    public bool TracksAny => _ledger.Count > 0;

    static long KeyOf(Entity e) => ((long)e.Index << 32) | (uint)e.Version;
    static string F(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);
    static string P(float3 v) => $"x={F(v.x)} y={F(v.y)} z={F(v.z)}";

    // ============================================================ lifecycle

    /// <summary>Boot: queue every unit carrying our marker for removal (units are never persisted), then start the
    /// queue pump.</summary>
    public void Start()
    {
        try
        {
            var marked = MarkedUnits();
            foreach (var u in marked) _despawnQueue.Enqueue(u);
            Core.Log.LogInfo($"[Uriel UNIT] boot sweep found={marked.Count} queued={marked.Count}");
        }
        catch (Exception ex)
        {
            Core.Log.LogWarning($"[Uriel UNIT] boot sweep failed: {ex.Message}; units still expire by LifeTime");
        }
        if (_loopStarted) return;
        _loopStarted = true;
        Tick.RunRepeating(PumpFrames, Pump);
    }

    /// <summary>One queue step: prune units the game removed, then despawn, then spawn, each under its budget.</summary>
    void Pump()
    {
        if (!Core.IsReady) return;

        if (_ledger.Count > 0)
        {
            List<long> gone = null;
            foreach (var (key, t) in _ledger)
            {
                bool dead = !t.Unit.Exists() || t.Unit.Has<Dead>()
                    || (t.Unit.TryGetComponent<Health>(out var hp) && hp.IsDead);
                if (dead) (gone ??= new()).Add(key);
            }
            if (gone != null)
                foreach (var key in gone)
                {
                    var t = _ledger[key];
                    _ledger.Remove(key);
                    Core.Log.LogInfo($"[Uriel UNIT] gone key={key} prefab={t.Name} reason=removed-or-dead tracked={_ledger.Count}");
                }
        }

        int budget = Math.Max(1, Settings.CastleEvents_MaxDespawnsPerTick.Value);
        while (budget-- > 0 && _despawnQueue.Count > 0)
        {
            var u = _despawnQueue.Dequeue();
            try
            {
                _ledger.Remove(KeyOf(u));
                if (DestroySafe(u))
                    Core.Log.LogInfo($"[Uriel UNIT] despawned key={KeyOf(u)} left={_despawnQueue.Count}");
            }
            catch (Exception ex)
            {
                Core.Log.LogWarning($"[Uriel UNIT] despawn failed key={KeyOf(u)}: {ex.Message}; LifeTime removes it");
            }
        }

        budget = Math.Max(1, Settings.CastleEvents_MaxSpawnsPerTick.Value);
        while (budget-- > 0 && _spawnQueue.Count > 0)
        {
            var o = _spawnQueue.Dequeue();
            try
            {
                var unit = Spawn(o, out string error);
                if (unit == Entity.Null)
                {
                    Core.Log.LogWarning($"[Uriel UNIT] spawn failed prefab={o.Name} {P(o.Spot)} error={error}");
                    continue;
                }
                long key = KeyOf(unit);
                _ledger[key] = new Tracked { Unit = unit, Guid = o.Guid, Name = o.Name, Spot = o.Spot, Territory = o.Territory, By = o.By };
                Core.Log.LogInfo($"[Uriel UNIT] spawned key={key} prefab={o.Name} {P(o.Spot)} hlevel={TileLayerUtility.GetHeightLevel(o.Spot.y)} territory={o.Territory} tracked={_ledger.Count} queued={_spawnQueue.Count}");
            }
            catch (Exception ex)
            {
                Core.Log.LogError($"[Uriel UNIT] spawn threw prefab={o.Name}: {ex}");
            }
        }
    }

    // ============================================================ the recipe

    /// <summary>Spawn one order with the full recipe. A unit that fails a step is destroyed.</summary>
    static Entity Spawn(Order o, out string error)
    {
        error = null;
        var unit = Core.ServerGameManager.InstantiateEntityImmediate(Entity.Null, new PrefabGUID(o.Guid));
        if (!unit.Exists()) { error = "the game returned no entity"; return Entity.Null; }
        try
        {
            // Marker first (the sweep's record), and the LifeTime backstop even when marking failed.
            bool marked = TryMark(unit, out string markError);
            bool timed = TryTime(unit, Settings.CastleEvents_UnitLifetimeSeconds.Value, out string timeError);
            if (!marked || !timed)
            {
                error = markError ?? timeError;
                DestroySafe(unit);
                return Entity.Null;
            }

            if (unit.Has<Translation>()) unit.Write(new Translation { Value = o.Spot });
            if (unit.Has<LastTranslation>()) unit.Write(new LastTranslation { Value = o.Spot });
            if (!Core.EntityManager.HasComponent<DestroyWhenDisabled>(unit))
                Core.EntityManager.AddComponent<DestroyWhenDisabled>(unit);
            if (Settings.CastleEvents_ClearDrops.Value && unit.Has<DropTableBuffer>())
                Core.EntityManager.GetBuffer<DropTableBuffer>(unit).Clear();

            // Make it fight: never disabled away from players (else DestroyWhenDisabled deletes it within seconds),
            // and its aggro on.
            unit.AddOrSet(new CanPreventDisableWhenNoPlayersInRange { CanDisable = new ModifiableBool(false) });
            if (unit.TryGetComponent<AggroConsumer>(out var aggro))
            {
                aggro.Active = new ModifiableBool(true);
                unit.Write(aggro);
            }
            if (o.Level > 0 && unit.TryGetComponent<UnitLevel>(out var lvl))
            {
                lvl.Level._Value = o.Level;
                unit.Write(lvl);
            }
            return unit;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            try { DestroySafe(unit); } catch (Exception) { }
            return Entity.Null;
        }
    }

    static bool TryTime(Entity unit, int seconds, out string error)
    {
        error = null;
        try
        {
            unit.AddOrSet(new LifeTime { Duration = Math.Max(60, seconds), EndAction = LifeTimeEndAction.Destroy });
            unit.AddOrSet(new Age { Value = 0f }); // without Age the LifeTime never counts down
            return true;
        }
        catch (Exception ex)
        {
            error = $"LifeTime could not be set: {ex.Message}";
            return false;
        }
    }

    static bool TryMark(Entity unit, out string error)
    {
        error = null;
        try
        {
            if (!Core.ServerGameManager.TryInstantiateBuffEntityImmediate(unit, unit, MarkerBuff, out Entity buff) || !buff.Exists())
            {
                error = "marker buff could not be applied";
                return false;
            }
            buff.AddOrSet(new SpellLevel { Level = MarkerValue });
            buff.RemoveIfPresent<CreateGameplayEventsOnSpawn>();
            buff.RemoveIfPresent<GameplayEventListeners>();
            buff.RemoveIfPresent<RemoveBuffOnGameplayEvent>();
            buff.RemoveIfPresent<RemoveBuffOnGameplayEventEntry>();
            buff.RemoveIfPresent<DestroyOnGameplayEvent>();
            // The potion's own stat bonus goes: the marker must change nothing about the unit.
            if (buff.Has<ModifyUnitStatBuff_DOTS>()) Core.EntityManager.GetBuffer<ModifyUnitStatBuff_DOTS>(buff).Clear();
            if (buff.Has<LifeTime>()) buff.Write(new LifeTime { Duration = 0f, EndAction = LifeTimeEndAction.None });
            return true;
        }
        catch (Exception ex)
        {
            error = $"marker could not be set: {ex.Message}";
            return false;
        }
    }

    /// <summary>Deferred destroy; never destroys a prefab or twice.</summary>
    static bool DestroySafe(Entity e)
    {
        if (!e.Exists() || e.Has<Prefab>()) return false;
        if (e.Has<DestroyTag>()) return true;
        DestroyUtility.Destroy(Core.EntityManager, e);
        return true;
    }

    /// <summary>Units whose marker buff carries our value, disabled and just-spawned included.</summary>
    static List<Entity> MarkedUnits()
    {
        var result = new List<Entity>();
        var query = Core.EntityManager.CreateEntityQuery(new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly(Il2CppType.Of<Buff>()), ComponentType.ReadOnly(Il2CppType.Of<SpellLevel>()) },
            Options = EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludeSpawnTag
        });
        try
        {
            var buffs = query.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (var b in buffs)
                {
                    if (b.Read<SpellLevel>().Level != (float)MarkerValue) continue;
                    var target = b.Read<Buff>().Target;
                    if (target.Exists() && !result.Contains(target)) result.Add(target);
                }
            }
            finally { buffs.Dispose(); }
        }
        finally { query.Dispose(); }
        return result;
    }

    // ============================================================ commands

    /// <summary>Validate and queue <paramref name="count"/> units of a prefab in front of the caller (or at their aim
    /// point). Phase 1 is admin-only; any claimed, alive castle plot.</summary>
    public bool Enqueue(Entity character, ulong steamId, string prefabArg, int count, int level, bool atAim, out string message)
    {
        if (!TryResolveUnitPrefab(prefabArg, out int guid, out string name, out message)) return false;

        if (!character.TryGetComponent<Translation>(out var feet)) { message = "Can't read your position."; return false; }
        float3 spot = atAim && character.TryGetComponent<EntityAimData>(out var aim) && !aim.AimPosition.Equals(default(float3))
            ? aim.AimPosition
            : Forward(character, feet.Value, HereForward);

        if (!Core.ObjectSpawn.TryResolvePlot(spot, out Entity heart, out int territory))
        {
            message = "Units can only be spawned inside a castle plot.";
            return false;
        }
        if (!ObjectSpawnService.CastleClaimedAndAlive(heart))
        {
            message = "That castle plot is abandoned or decaying.";
            return false;
        }

        int max = Math.Max(1, Settings.CastleEvents_MaxUnitsPerCommand.Value);
        string clamped = count > max ? $" (clamped from {count} by MaxUnitsPerCommand)" : "";
        count = Math.Clamp(count, 1, max);

        int onPlot = _ledger.Values.Count(t => t.Territory == territory) + _spawnQueue.Count(o => o.Territory == territory);
        int plotRoom = Settings.CastleEvents_MaxUnitsPerPlot.Value - onPlot;
        int serverRoom = Settings.CastleEvents_MaxTrackedUnits.Value - _ledger.Count - _spawnQueue.Count;
        int room = Math.Min(plotRoom, serverRoom);
        if (room <= 0)
        {
            message = $"Unit cap reached (plot {onPlot}/{Settings.CastleEvents_MaxUnitsPerPlot.Value}, server {_ledger.Count + _spawnQueue.Count}/{Settings.CastleEvents_MaxTrackedUnits.Value}).";
            Core.Log.LogInfo($"[Uriel UNIT] refused prefab={name} reason=cap plot={onPlot} server={_ledger.Count + _spawnQueue.Count}");
            return false;
        }
        if (count > room) { clamped = $" (clamped from {count} by the unit caps)"; count = room; }

        for (int i = 0; i < count; i++)
        {
            float3 at = spot;
            if (i > 0)
            {
                float angle = i * 2.39996f; // golden angle: an even spread for any count
                at = new float3(spot.x + math.cos(angle) * SpreadRadius, spot.y, spot.z + math.sin(angle) * SpreadRadius);
            }
            _spawnQueue.Enqueue(new Order { Guid = guid, Name = name, Spot = at, Territory = territory, By = steamId, Level = level });
        }
        Core.Log.LogInfo($"[Uriel UNIT] queued prefab={name} guid={guid} n={count} {P(spot)} hlevel={TileLayerUtility.GetHeightLevel(spot.y)} territory={territory} level={level} by={steamId}");
        message = $"Queued {count} x {name}{clamped}.";
        return true;
    }

    static float3 Forward(Entity character, float3 feet, float distance)
    {
        if (character.TryGetComponent<Rotation>(out var rot))
        {
            float3 fwd = math.mul(rot.Value, new float3(0f, 0f, 1f));
            fwd.y = 0f;
            if (math.lengthsq(fwd) > 1e-4f)
            {
                fwd = math.normalize(fwd);
                return new float3(feet.x + fwd.x * distance, feet.y, feet.z + fwd.z * distance);
            }
        }
        return feet;
    }

    /// <summary>A spawnable NPC: a CHAR_ prefab with Health, UnitLevel and AggroConsumer, not a player, not on the
    /// refused list, not one that drops into an inventory.</summary>
    static bool TryResolveUnitPrefab(string arg, out int guid, out string name, out string message)
    {
        guid = 0; name = null; message = null;
        var map = Core.PrefabCollectionSystem.SpawnableNameToPrefabGuidDictionary;
        if (int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int g))
        {
            guid = g;
            name = new PrefabGUID(g).GetPrefabName();
        }
        else
        {
            var matches = new List<(string Name, int Guid)>();
            foreach (var kv in map)
            {
                if (string.Equals(kv.Key, arg, StringComparison.OrdinalIgnoreCase)) { matches.Clear(); matches.Add((kv.Key, kv.Value.GuidHash)); break; }
                if (kv.Key.StartsWith("CHAR_", StringComparison.Ordinal) && kv.Key.IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0)
                    matches.Add((kv.Key, kv.Value.GuidHash));
            }
            if (matches.Count == 0) { message = $"No unit prefab matches '{arg}'."; return false; }
            if (matches.Count > 1)
            {
                message = $"'{arg}' matches {matches.Count} units: {string.Join(", ", matches.Take(5).Select(m => m.Name))}{(matches.Count > 5 ? ", ..." : "")}";
                return false;
            }
            (name, guid) = matches[0];
        }

        if (!Core.PrefabCollectionSystem._PrefabLookupMap.GuidToEntityMap.TryGetValue(new PrefabGUID(guid), out Entity prefab) || !prefab.Exists())
        {
            message = $"Unknown prefab {arg}.";
            return false;
        }
        string why = RefusalReason(name, prefab);
        if (why != null)
        {
            message = $"{name} can't be spawned as a unit: {why}.";
            Core.Log.LogInfo($"[Uriel UNIT] refused prefab={name} reason={why.Replace(' ', '-')}");
            return false;
        }
        return true;
    }

    static string RefusalReason(string name, Entity prefab)
    {
        if (name == null || !name.StartsWith("CHAR_", StringComparison.Ordinal)) return "not a character prefab";
        foreach (var f in RefusedFragments)
            if (name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0) return $"on the refused list ({f})";
        if (prefab.Has<PlayerCharacter>()) return "a player character";
        if (!prefab.Has<Health>() || !prefab.Has<UnitLevel>() || !prefab.Has<AggroConsumer>()) return "not a fighting unit";
        if (prefab.Has<DropInInventoryOnSpawn>()) return "drops into an inventory on spawn";
        return null;
    }

    /// <summary>Queue the nearest tracked unit within <paramref name="radius"/> for removal.</summary>
    public string DespawnNearest(Entity character, float radius)
    {
        if (!character.TryGetComponent<Translation>(out var me)) return "Can't read your position.";
        Tracked best = null;
        float bestD = radius;
        foreach (var t in _ledger.Values)
        {
            if (!t.Unit.TryGetComponent<Translation>(out var at)) continue;
            float d = math.distance(at.Value, me.Value);
            if (d <= bestD) { bestD = d; best = t; }
        }
        if (best == null) return $"No Uriel unit within {F(radius)} m.";
        _despawnQueue.Enqueue(best.Unit);
        Core.Log.LogInfo($"[Uriel UNIT] despawn-queued key={KeyOf(best.Unit)} prefab={best.Name} d={F(bestD)}");
        return $"Removing {best.Name} ({F(bestD)} m).";
    }

    /// <summary>Queue every tracked unit on the plot the caller stands in.</summary>
    public string ClearPlot(Entity character)
    {
        if (!character.TryGetComponent<Translation>(out var me)) return "Can't read your position.";
        if (!Core.ObjectSpawn.TryResolvePlot(me.Value, out _, out int territory)) return "You're not standing in a castle plot.";
        int n = 0;
        foreach (var t in _ledger.Values.Where(t => t.Territory == territory).ToList()) { _despawnQueue.Enqueue(t.Unit); n++; }
        int dropped = DropQueued(o => o.Territory == territory);
        Core.Log.LogInfo($"[Uriel UNIT] clear territory={territory} queued={n} unqueued={dropped}");
        return $"Removing {n} unit(s) on this plot{(dropped > 0 ? $"; cancelled {dropped} pending spawn(s)" : "")}.";
    }

    int DropQueued(Func<Order, bool> match)
    {
        int before = _spawnQueue.Count;
        var keep = _spawnQueue.Where(o => !match(o)).ToList();
        _spawnQueue.Clear();
        foreach (var o in keep) _spawnQueue.Enqueue(o);
        return before - keep.Count;
    }

    /// <summary>Two-step kill switch: arm, then confirm within 30 s. Confirm removes every tracked unit AND every
    /// unit carrying our marker (in case the ledger lost one), and cancels pending spawns.</summary>
    public string Purge(bool confirm)
    {
        if (!confirm)
        {
            _purgeArmedUntil = DateTime.UtcNow.AddSeconds(30);
            return $"This removes every Uriel unit on the server ({_ledger.Count} tracked, {_spawnQueue.Count} pending). Run '.uriel unit purge confirm' within 30 s.";
        }
        if (DateTime.UtcNow > _purgeArmedUntil) return "Not armed. Run '.uriel unit purge' first.";
        _purgeArmedUntil = DateTime.MinValue;
        int dropped = DropQueued(_ => true);
        var all = new HashSet<long>();
        foreach (var t in _ledger.Values) if (all.Add(KeyOf(t.Unit))) _despawnQueue.Enqueue(t.Unit);
        foreach (var u in MarkedUnits()) if (all.Add(KeyOf(u))) _despawnQueue.Enqueue(u);
        Core.Log.LogInfo($"[Uriel UNIT] purge queued={all.Count} unqueued={dropped}");
        return $"Purging {all.Count} unit(s); cancelled {dropped} pending spawn(s).";
    }

    /// <summary>Counts per plot, and the ten nearest tracked units.</summary>
    public string List(Entity character)
    {
        if (_ledger.Count == 0 && _spawnQueue.Count == 0) return "No Uriel units.";
        var sb = new StringBuilder($"Uriel units: {_ledger.Count} alive, {_spawnQueue.Count} pending, {_despawnQueue.Count} being removed.");
        foreach (var g in _ledger.Values.GroupBy(t => t.Territory).OrderBy(g => g.Key))
            sb.Append($" Plot {g.Key}: {g.Count()}.");
        return sb.ToString();
    }

    /// <summary>Spike reading: one log line per tracked unit within <paramref name="radius"/> (position, height level,
    /// health, behaviour state, aggro target, distance from its spawn spot).</summary>
    public string Status(Entity character, float radius)
    {
        if (!character.TryGetComponent<Translation>(out var me)) return "Can't read your position.";
        int n = 0, targeting = 0;
        foreach (var (key, t) in _ledger)
        {
            if (!t.Unit.TryGetComponent<Translation>(out var at)) continue;
            float d = math.distance(at.Value, me.Value);
            if (d > radius) continue;
            n++;
            string hl = t.Unit.TryGetComponent<Height>(out var h) ? h.ServerHeightLevel.ToString(CultureInfo.InvariantCulture) : "-";
            string hp = t.Unit.TryGetComponent<Health>(out var health) ? $"{F(health.Value)}/{F(health.MaxHealth._Value)}" : "-";
            string state = t.Unit.TryGetComponent<BehaviourTreeState>(out var bts) ? bts.Value.ToString() : "-";
            string target = "-";
            if (t.Unit.TryGetComponent<AggroConsumer>(out var ac))
            {
                Entity te = ac.AggroTarget._Entity;
                target = te == Entity.Null ? "none" : te == character ? "me" : te.Has<PlayerCharacter>() ? "player" : te.GetPrefabGuid().GetPrefabName();
                if (te != Entity.Null) targeting++;
            }
            string lvl = t.Unit.TryGetComponent<UnitLevel>(out var ul) ? ul.Level._Value.ToString(CultureInfo.InvariantCulture) : "-";
            Core.Log.LogInfo($"[Uriel UNIT] status key={key} prefab={t.Name} {P(at.Value)} hlevel={hl} level={lvl} hp={hp} state={state} target={target} d={F(d)} fromSpawn={F(math.distance(at.Value, t.Spot))} spawnY={F(t.Spot.y)}");
        }
        Core.Log.LogInfo($"[Uriel UNIT] status n={n} targeting={targeting} radius={F(radius)}");
        return $"{n} unit(s) within {F(radius)} m, {targeting} with a target (details in the server log).";
    }

    // ============================================================ tile-map probe (spike 1)

    /// <summary>Spike 1 reading: does the static tile map see castle-built floors, walls, doors and stairs? Reads the
    /// caller's position (or aim point): its height level, and for that level and its neighbours whether a 0.5 m
    /// circle is free and whether there is ground.</summary>
    public string Probe(Entity character, bool atAim, string label)
    {
        float3 at;
        if (atAim && character.TryGetComponent<EntityAimData>(out var aim) && !aim.AimPosition.Equals(default(float3))) at = aim.AimPosition;
        else if (character.TryGetComponent<Translation>(out var t)) at = t.Value;
        else return "Can't read your position.";

        if (LiveTileWorld() is not { } world || !world.ChunkAllocation.IsCreated || !world.WorldCells.IsCreated || world.WorldCells.Length == 0)
        {
            Core.Log.LogWarning($"[Uriel PROBE] label={label} {P(at)} error=no-tile-world");
            return "Tile world not available.";
        }
        int tx = (int)math.floor(at.x * 2) + 6400, tz = (int)math.floor(at.z * 2) + 6400;
        if (tx < 0 || tz < 0) return "Off the tile grid.";

        byte level = TileLayerUtility.GetHeightLevel(at.y);
        var polygons = TileCollisionHelper.CreateLinePolygon();
        var sb = new StringBuilder();
        try
        {
            var map = TileCollisionHelper.CreateMapData(polygons, world);
            for (int dl = -2; dl <= 2; dl++)
            {
                int l = level + dl;
                if (l < 0 || l > 255) continue;
                bool free = !TileMapCollisionMath.CheckStaticCircle(ref map, new float2(at.x, at.z), (byte)l, 0.5f, MapCollisionFlags.CollideNormalMovement);
                bool grounded = world.GetIsGrounded(new int2(tx, tz), (byte)l);
                sb.Append($" L{l}={(free ? "free" : "blocked")}/{(grounded ? "ground" : "air")}");
            }
        }
        finally { polygons.Dispose(); }
        bool plot = Core.ObjectSpawn.TryResolvePlot(at, out _, out int territory);
        Core.Log.LogInfo($"[Uriel PROBE] label={label} {P(at)} hlevel={level} plot={(plot ? territory : -1)}{sb}");
        return $"probe {label}: hlevel={level}{sb}";
    }

    /// <summary>Spike 1 map: samples a square grid (1 m steps) around the caller at their height level and the one
    /// above, logging one row per line: '#' blocked, '.' free with ground, ' ' free without ground, '@' the caller.
    /// North (+z) is the first row. Compared with a screenshot, it shows whether castle walls are in the map.</summary>
    public string ProbeMap(Entity character, int radius, string label)
    {
        if (!character.TryGetComponent<Translation>(out var t)) return "Can't read your position.";
        radius = Math.Clamp(radius, 2, 20);
        var at = t.Value;
        if (LiveTileWorld() is not { } world || !world.ChunkAllocation.IsCreated || !world.WorldCells.IsCreated || world.WorldCells.Length == 0)
            return "Tile world not available.";
        byte level = TileLayerUtility.GetHeightLevel(at.y);
        var polygons = TileCollisionHelper.CreateLinePolygon();
        try
        {
            var map = TileCollisionHelper.CreateMapData(polygons, world);
            for (int dl = 0; dl <= 1; dl++)
            {
                byte l = (byte)Math.Min(255, level + dl);
                Core.Log.LogInfo($"[Uriel PROBE] map label={label} {P(at)} level={l} radius={radius} step=1m north=up");
                int blocked = 0, open = 0;
                for (int dz = radius; dz >= -radius; dz--)
                {
                    var row = new StringBuilder(radius * 2 + 1);
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        if (dx == 0 && dz == 0) { row.Append('@'); continue; }
                        float x = at.x + dx, z = at.z + dz;
                        int tx = (int)math.floor(x * 2) + 6400, tz = (int)math.floor(z * 2) + 6400;
                        if (tx < 0 || tz < 0) { row.Append('?'); continue; }
                        bool free = !TileMapCollisionMath.CheckStaticCircle(ref map, new float2(x, z), l, 0.5f, MapCollisionFlags.CollideNormalMovement);
                        bool grounded = world.GetIsGrounded(new int2(tx, tz), l);
                        if (!free) blocked++; else open++;
                        row.Append(!free ? '#' : grounded ? '.' : ' ');
                    }
                    Core.Log.LogInfo($"[Uriel PROBE] row label={label} level={l} dz={dz.ToString(CultureInfo.InvariantCulture).PadLeft(3)} |{row}|");
                }
                Core.Log.LogInfo($"[Uriel PROBE] map-end label={label} level={l} blocked={blocked} free={open}");
            }
        }
        finally { polygons.Dispose(); }
        return $"probemap {label}: level {level} and {level + 1} written to the server log.";
    }

    /// <summary>The TileWorld singleton lives on a system entity a default query leaves out.</summary>
    static TileWorld? LiveTileWorld()
    {
        Entity mapped;
        try { mapped = Core.ServerScriptMapper.GetSingletonEntity<TileWorldSingleton>(); }
        catch (Exception) { mapped = Entity.Null; }
        if (mapped.Has<TileWorldSingleton>())
            return Core.EntityManager.GetComponentData<TileWorldSingleton>(mapped).GetTileWorld();

        var query = Core.EntityManager.CreateEntityQuery(new EntityQueryDesc
        {
            All = new[] { ComponentType.ReadOnly(Il2CppType.Of<TileWorldSingleton>()) },
            Options = EntityQueryOptions.IncludeSystems
        });
        try
        {
            var entities = query.ToEntityArray(Allocator.Temp);
            try { return entities.Length == 0 ? null : Core.EntityManager.GetComponentData<TileWorldSingleton>(entities[0]).GetTileWorld(); }
            finally { entities.Dispose(); }
        }
        finally { query.Dispose(); }
    }

    // ============================================================ deaths

    /// <summary>Called from the death postfix: log and forget tracked units that died, with the killer.</summary>
    public void HandleDeaths(NativeArray<DeathEvent> deaths)
    {
        for (int i = 0; i < deaths.Length; i++)
        {
            var d = deaths[i];
            long key = KeyOf(d.Died);
            if (!_ledger.TryGetValue(key, out var t)) continue;
            _ledger.Remove(key);
            Core.Log.LogInfo($"[Uriel UNIT] died key={key} prefab={t.Name} killer={KillerName(d.Killer)} tracked={_ledger.Count}");
        }
    }

    static string KillerName(Entity killer)
    {
        Entity e = killer;
        for (int hop = 0; hop < 3 && e.Exists(); hop++)
        {
            if (e.TryGetComponent<PlayerCharacter>(out var pc)) return pc.Name.ToString().Replace(' ', '_');
            if (!e.TryGetComponent<EntityOwner>(out var owner) || owner.Owner == Entity.Null || owner.Owner == e) break;
            e = owner.Owner;
        }
        return killer.Exists() ? killer.GetPrefabGuid().GetPrefabName() : "unknown";
    }
}
