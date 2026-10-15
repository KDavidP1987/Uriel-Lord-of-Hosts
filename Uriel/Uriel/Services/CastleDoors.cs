using System;
using System.Globalization;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.CastleBuilding;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Uriel.Services;

/// <summary>
/// Castle Events gates (docs/features/CASTLE_EVENTS.md, spike 7): finds a castle's doors and drives them from the
/// server. <see cref="Door.OpenState"/> is what the game's DoorSystem acts on (it swaps the open and closed collision
/// pieces and plays the sequence); <see cref="Interactable.Disabled"/> keeps players, the owner included, from opening
/// or closing it by hand while an event holds it.
/// </summary>
internal static class CastleDoors
{
    static string F(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>The castle door of <paramref name="heart"/> nearest to <paramref name="pos"/> within
    /// <paramref name="radius"/> m (vertical distance counts, so a door one storey up is not "near"); any castle's
    /// door when <paramref name="heart"/> is Entity.Null. Entity.Null when there is none.</summary>
    public static Entity Nearest(Entity heart, float3 pos, float radius)
    {
        Entity best = Entity.Null;
        float bestD = radius;
        // Placed castle objects are Disabled while no player is in range (DisableWhenNoPlayersInRange, see
        // docs/DEV_REMINDERS.md); a default query skips them, so a door far from every player would not be found.
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(new(Il2CppType.Of<Door>(), ComponentType.AccessMode.ReadOnly))
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
                    if (heart != Entity.Null && e.Read<CastleHeartConnection>().CastleHeartEntity.GetEntityOnServer() != heart) continue;
                    float d = math.distance(e.Read<Translation>().Value, pos);
                    if (d <= bestD) { bestD = d; best = e; }
                }
            }
            finally { entities.Dispose(); }
        }
        finally { query.Dispose(); }
        return best;
    }

    /// <summary>The door at a stored position (within 1 m), for a door bound to a group and saved to disk: entities
    /// don't survive a restart, positions do. No two doors share a spot, so the heart is not checked.</summary>
    public static Entity At(Entity heart, float3 pos) => Nearest(Entity.Null, pos, 1f);

    /// <summary>Why a door lookup at <paramref name="pos"/> misses: how many Door entities exist, how many carry the
    /// components <see cref="Nearest"/> needs, and the distance to the nearest Door by any position it has.</summary>
    public static string Diagnose(float3 pos)
    {
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(new(Il2CppType.Of<Door>(), ComponentType.AccessMode.ReadOnly))
            .WithOptions(EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludeSpawnTag);
        var query = Core.EntityManager.CreateEntityQuery(ref builder);
        try
        {
            var entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                int withHeart = 0, withTranslation = 0;
                float best = float.MaxValue, bestLtw = float.MaxValue;
                foreach (var e in entities)
                {
                    if (e.Has<CastleHeartConnection>()) withHeart++;
                    if (e.TryGetComponent<Translation>(out var t)) { withTranslation++; best = math.min(best, math.distance(t.Value, pos)); }
                    if (e.TryGetComponent<LocalToWorld>(out var l)) bestLtw = math.min(bestLtw, math.distance(l.Position, pos));
                }
                string D(float d) => d < float.MaxValue ? F(d) : "-";
                return $"doors={entities.Length} withHeart={withHeart} withTranslation={withTranslation} nearest={D(best)} nearestLtw={D(bestLtw)}";
            }
            finally { entities.Dispose(); }
        }
        finally { query.Dispose(); }
    }

    public static bool IsOpen(Entity door) => door.TryGetComponent<Door>(out var d) && d.OpenState;

    public static bool IsLocked(Entity door) => door.TryGetComponent<Interactable>(out var i) && i.Disabled;

    /// <summary>Open or close the door. Returns false when it isn't a door.</summary>
    public static bool SetOpen(Entity door, bool open)
    {
        if (!door.TryGetComponent<Door>(out var d)) return false;
        if (d.OpenState == open) return true;
        d.OpenState = open;
        door.Write(d);
        return true;
    }

    /// <summary>Lock (players can't use it) or unlock the door. Returns false when it has no Interactable.</summary>
    public static bool SetLocked(Entity door, bool locked)
    {
        if (!door.TryGetComponent<Interactable>(out var i)) return false;
        if (i.Disabled == locked) return true;
        i.Disabled = locked;
        door.Write(i);
        return true;
    }

    public static string Describe(Entity door)
    {
        if (!door.Exists()) return "none";
        var p = door.Read<Translation>().Value;
        string name = door.GetPrefabGuid().GetPrefabName();
        return $"{name} x={F(p.x)} y={F(p.y)} z={F(p.z)} open={IsOpen(door)} locked={IsLocked(door)} interactable={door.Has<Interactable>()}";
    }
}
