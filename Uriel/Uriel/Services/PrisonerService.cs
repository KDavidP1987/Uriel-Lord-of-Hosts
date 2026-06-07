using System;
using ProjectM;
using ProjectM.Network;
using ProjectM.Shared;
using Stunlock.Core;
using Unity.Entities;

namespace Uriel.Services;

/// <summary>
/// Server-side prisoner extraction from a SHARED cell (v0.10.0). Research verdict
/// (2026-06-07): the native subdue button is hard-gated CLIENT-side by team match
/// (PalacePrivileges treats a cross-team charm event as cheat detection — a real
/// client never sends one for a foreign cell), so no replicated state can reveal
/// it. This command does it server-side instead, in two layers:
///   A. synthesize the vanilla InteractWithPrisonerEvent(Charm) — if the system
///      accepts it (cell is neutral while shared), behavior is 100% vanilla;
///   B. fallback: manual charm — apply AB_Charm_Active_Human_Buff (1303169868)
///      owned by the taker, strip Imprisoned + ImprisonedBuff (1603329680),
///      clear the cell's PrisonCell/Prisonstation links (PrisonerExchange's
///      component recipe).
/// </summary>
internal static class PrisonerService
{
    static readonly PrefabGUID CharmActiveHumanBuff = new(1303169868); // AB_Charm_Active_Human_Buff
    static readonly PrefabGUID ImprisonedBuffGuid = new(1603329680);   // ImprisonedBuff

    public static bool TakePrisoner(Entity character, Entity userEntity, Entity cell, out string message)
    {
        message = null;
        if (!cell.TryGetComponent<PrisonCell>(out var prisonCell))
        {
            message = "That's not a prison cell.";
            return false;
        }
        Entity prisoner = prisonCell.ImprisonedEntity.GetEntityOnServer();
        if (!prisoner.Exists())
        {
            message = "That cell has no prisoner.";
            return false;
        }
        if (!cell.TryGetComponent<NetworkId>(out var cellNetId))
        {
            message = "Cell has no network id (report this).";
            return false;
        }

        // ---- Strategy A: vanilla charm event (perfect behavior if accepted) ----
        try
        {
            Entity ev = Core.EntityManager.CreateEntity();
            Core.EntityManager.AddComponentData(ev, new FromCharacter { User = userEntity, Character = character });
            Core.EntityManager.AddComponentData(ev, new InteractWithPrisonerEvent
            {
                Prison = cellNetId,
                PrisonInteraction = EventHelper.PrisonInteraction.Charm,
            });
            Core.Log.LogInfo($"[Uriel PRISON] takeprisoner: vanilla Charm event fired for {prisoner.GetPrefabGuid().GetPrefabName()} (taker {character.GetSteamId()}).");
        }
        catch (Exception ex)
        {
            Core.Log.LogWarning($"[Uriel PRISON] vanilla charm event failed to create: {ex.Message}");
        }

        // ---- Verify; fall back to the manual charm if the prisoner is still bound ----
        Entity capturedCell = cell, capturedPrisoner = prisoner, capturedChar = character, capturedUser = userEntity;
        Tick.RunLater(20, () =>
        {
            try
            {
                if (!capturedPrisoner.Exists()) return; // gone (vanilla path may despawn/convert)
                if (!capturedPrisoner.Has<Imprisoned>())
                {
                    ChatNotify.ToUserEntity(capturedUser, "[Uriel] Prisoner released to you — escort them (Dominating Presence) before the charm wears off!");
                    Core.Log.LogInfo("[Uriel PRISON] takeprisoner: vanilla path succeeded.");
                    return;
                }
                Core.Log.LogInfo("[Uriel PRISON] takeprisoner: vanilla path didn't take — running manual charm fallback.");
                ManualCharm(capturedChar, capturedUser, capturedCell, capturedPrisoner);
            }
            catch (Exception ex)
            {
                Core.Log.LogError($"[Uriel PRISON] takeprisoner verify/fallback failed: {ex}");
            }
        });

        message = "Taking the prisoner… (have Dominating Presence ready to escort them)";
        return true;
    }

    static void ManualCharm(Entity character, Entity userEntity, Entity cell, Entity prisoner)
    {
        // 1. strip the imprisoned state (PrisonerExchange's component recipe, inverted)
        try
        {
            if (BuffUtility.TryGetBuff(Core.EntityManager, prisoner, ImprisonedBuffGuid, out Entity imprisonedBuff))
                DestroyUtility.Destroy(Core.EntityManager, imprisonedBuff, DestroyDebugReason.TryRemoveBuff);
        }
        catch (Exception ex) { Core.Log.LogWarning($"[Uriel PRISON] ImprisonedBuff strip failed: {ex.Message}"); }
        if (prisoner.Has<Imprisoned>())
            Core.EntityManager.RemoveComponent<Imprisoned>(prisoner);

        // 2. clear the cell's links
        cell.With((ref PrisonCell p) => p.ImprisonedEntity = Entity.Null);
        if (cell.Has<Prisonstation>())
            cell.With((ref Prisonstation s) => { s.HasPrisoner = false; s.IsWorking = false; });

        // 3. charm the unit for the taker (the subdued-following state)
        var applyBuff = new ApplyBuffDebugEvent { BuffPrefabGUID = CharmActiveHumanBuff };
        var fromCharacter = new FromCharacter { User = userEntity, Character = prisoner };
        Core.DebugEventsSystem.ApplyBuff(fromCharacter, applyBuff);

        // The buff spawns via the event pipeline — re-own it to the taker next frames
        // so the prisoner follows THEM (the charm script reads the buff's owner).
        Entity capturedPrisoner = prisoner, capturedChar = character, capturedUser = userEntity;
        Tick.RunLater(5, () =>
        {
            try
            {
                if (BuffUtility.TryGetBuff(Core.EntityManager, capturedPrisoner, CharmActiveHumanBuff, out Entity charmBuff)
                    && charmBuff.Has<EntityOwner>())
                {
                    charmBuff.With((ref EntityOwner eo) => eo.Owner = capturedChar);
                    ChatNotify.ToUserEntity(capturedUser, "[Uriel] Prisoner subdued and released to you — escort them home!");
                    Core.Log.LogInfo("[Uriel PRISON] takeprisoner: manual charm applied.");
                }
                else
                {
                    ChatNotify.ToUserEntity(capturedUser, "[Uriel] The prisoner was released from the cell, but the charm didn't take — they may turn hostile. Tell the admin to check the log.");
                    Core.Log.LogWarning("[Uriel PRISON] manual charm: buff not found on prisoner after apply.");
                }
            }
            catch (Exception ex)
            {
                Core.Log.LogError($"[Uriel PRISON] manual charm owner fix failed: {ex}");
            }
        });
    }
}
