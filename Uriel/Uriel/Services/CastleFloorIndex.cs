using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.CastleBuilding;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Uriel.Services;

/// <summary>
/// Castle Events containment (docs/features/CASTLE_EVENTS.md): which castle floor tile, and which of the game's own
/// rooms, a position stands on, and whether it is inside a territory. The game links every <see cref="CastleFloor"/> to a
/// room (<see cref="CastleRoomConnection"/>), and a room ends at its walls, doorways included; so "stay in your room" is
/// "stand on a floor of the same room". Floors and territory blocks are cached per castle and refreshed every
/// <see cref="RefreshSeconds"/> s, so the per-second containment check is a dictionary lookup, not a query.
/// </summary>
internal static class CastleFloorIndex
{
    const double RefreshSeconds = 15;

    /// <summary>One floor tile: its entity, its height and its room (Entity.Null when the game gave it none).</summary>
    internal readonly record struct Floor(Entity Entity, float3 Position, Entity Room);

    sealed class HeartFloors
    {
        public DateTime At;
        public Dictionary<int2, List<Floor>> ByBlock = new();
        public int Count;
    }

    sealed class Blocks
    {
        public DateTime At;
        public HashSet<int2> Set;
    }

    static readonly Dictionary<Entity, HeartFloors> _floors = new();
    static readonly Dictionary<int, Blocks> _blocks = new();

    /// <summary>Drop the caches (a new spawn on a plot re-reads it, so freshly built floors count at once).</summary>
    public static void Invalidate(Entity heart, int territory)
    {
        _floors.Remove(heart);
        _blocks.Remove(territory);
    }

    /// <summary>Is <paramref name="pos"/> inside territory <paramref name="territory"/>?</summary>
    public static bool InTerritory(int territory, float3 pos)
    {
        if (!_blocks.TryGetValue(territory, out var b) || (DateTime.UtcNow - b.At).TotalSeconds > RefreshSeconds)
            _blocks[territory] = b = new Blocks { At = DateTime.UtcNow, Set = ObjectSpawnService.TerritoryBlocks(territory) };
        return b.Set.Contains(ObjectSpawnService.ConvertPosToBlockCoord(pos));
    }

    /// <summary>The floor tile of <paramref name="heart"/>'s castle under <paramref name="pos"/>: in the same 5 m block, the
    /// highest one at or below the position (1.5 m of slack for a unit standing on stairs or mid-step). False when there is
    /// none: natural ground, or another castle.</summary>
    public static bool TryFloorAt(Entity heart, float3 pos, out Floor floor)
    {
        floor = default;
        var index = For(heart);
        if (!index.ByBlock.TryGetValue(ObjectSpawnService.ConvertPosToBlockCoord(pos), out var list)) return false;
        float best = float.NegativeInfinity;
        foreach (var f in list)
        {
            float y = f.Position.y;
            if (y > pos.y + 1.5f || y <= best) continue;
            best = y;
            floor = f;
        }
        return best > float.NegativeInfinity;
    }

    /// <summary>The storey a height is on, counted from 1: one more than the castle's floor levels (floor heights
    /// within 2 m of each other are one level) more than 2 m below it. Natural ground under no floor reads 1.</summary>
    public static int StoreyOf(Entity heart, float y)
    {
        var heights = new List<float>();
        foreach (var f in All(heart)) heights.Add(f.Position.y);
        heights.Sort();
        int below = 0;
        float last = float.NegativeInfinity;
        foreach (var h in heights)
        {
            if (h >= y - 2f) break;
            if (h - last > 2f) below++;
            last = h;
        }
        return below + 1;
    }

    /// <summary>The number of floor tiles cached for the castle (for the probe line).</summary>
    public static int FloorCount(Entity heart) => For(heart).Count;

    /// <summary>Every floor tile of the castle (for the rooms survey).</summary>
    public static IEnumerable<Floor> All(Entity heart)
    {
        foreach (var list in For(heart).ByBlock.Values)
            foreach (var f in list) yield return f;
    }

    static HeartFloors For(Entity heart)
    {
        if (_floors.TryGetValue(heart, out var cached) && (DateTime.UtcNow - cached.At).TotalSeconds <= RefreshSeconds) return cached;
        var index = new HeartFloors { At = DateTime.UtcNow };
        // Include Disabled: floors far from every player are Disabled (DisableWhenNoPlayersInRange), and a cache built
        // without them would miss the far side of the castle.
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(new(Il2CppType.Of<CastleFloor>(), ComponentType.AccessMode.ReadOnly))
            .AddAll(new(Il2CppType.Of<CastleHeartConnection>(), ComponentType.AccessMode.ReadOnly))
            .AddAll(new(Il2CppType.Of<Translation>(), ComponentType.AccessMode.ReadOnly))
            .WithOptions(EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludeSpawnTag);
        var query = Core.EntityManager.CreateEntityQuery(ref builder);
        try
        {
            var entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (var e in entities)
                {
                    if (e.Read<CastleHeartConnection>().CastleHeartEntity.GetEntityOnServer() != heart) continue;
                    var p = e.Read<Translation>().Value;
                    Entity room = e.TryGetComponent<CastleRoomConnection>(out var rc) ? rc.RoomEntity.GetEntityOnServer() : Entity.Null;
                    var block = ObjectSpawnService.ConvertPosToBlockCoord(p);
                    if (!index.ByBlock.TryGetValue(block, out var list)) index.ByBlock[block] = list = new List<Floor>(1);
                    list.Add(new Floor(e, p, room));
                    index.Count++;
                }
            }
            finally { entities.Dispose(); }
        }
        finally { query.Dispose(); }
        _floors[heart] = index;
        return index;
    }
}
