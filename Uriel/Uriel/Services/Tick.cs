using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace Uriel.Services;

/// <summary>
/// Per-frame driver (Beelzebub Heartbeat pattern, v0.81.0 there): an IL2CPP-injected
/// MonoBehaviour whose Update() runs every frame on the dedicated server. Uriel uses
/// it for FRAME-DEFERRED actions — first consumer: the share-resync "blink"
/// (disable → re-enable an entity a few frames later so clients re-receive it).
/// </summary>
internal static class Tick
{
    sealed class Deferred
    {
        public int FramesLeft;
        public Action Action;
    }

    static readonly List<Deferred> _deferred = new();
    static readonly object _lock = new();
    static bool _started;

    public static void StartDriver()
    {
        if (_started) return;
        _started = true;
        try
        {
            ClassInjector.RegisterTypeInIl2Cpp<UrielTickBehaviour>();
            var go = new GameObject("Uriel_Tick");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<UrielTickBehaviour>();
            Core.Log.LogInfo("[Uriel] tick driver started (per-frame).");
        }
        catch (Exception ex)
        {
            _started = false;
            Core.Log.LogWarning($"[Uriel] tick driver failed to start (deferred actions will run immediately as fallback): {ex.Message}");
        }
    }

    /// <summary>True once the per-frame driver is live (so callers can avoid scheduling repeating work
    /// that would otherwise fire immediately/synchronously in a tight loop).</summary>
    public static bool IsRunning => _started;

    /// <summary>Run <paramref name="action"/> every <paramref name="frames"/> frames, rescheduling itself
    /// after each run. Safe if the driver is down: it runs once synchronously and then stops (never
    /// busy-loops, since the reschedule is gated on the driver being live).</summary>
    public static void RunRepeating(int frames, Action action)
    {
        if (action is null || frames <= 0) return;
        Action loop = null;
        loop = () =>
        {
            try { action(); }
            catch (Exception ex) { Core.Log.LogWarning($"[Uriel] repeating action failed: {ex.Message}"); }
            if (_started) RunLater(frames, loop);
        };
        RunLater(frames, loop);
    }

    /// <summary>Run <paramref name="action"/> after N frames (immediately if the driver is unavailable).</summary>
    public static void RunLater(int frames, Action action)
    {
        if (action is null) return;
        if (!_started || frames <= 0)
        {
            try { action(); } catch (Exception ex) { Core.Log.LogWarning($"[Uriel] deferred action failed: {ex.Message}"); }
            return;
        }
        lock (_lock)
        {
            _deferred.Add(new Deferred { FramesLeft = frames, Action = action });
        }
    }

    internal static void Drain()
    {
        List<Action> due = null;
        lock (_lock)
        {
            for (int i = _deferred.Count - 1; i >= 0; i--)
            {
                if (--_deferred[i].FramesLeft > 0) continue;
                (due ??= new List<Action>()).Add(_deferred[i].Action);
                _deferred.RemoveAt(i);
            }
        }
        if (due is null) return;
        foreach (var action in due)
        {
            try { action(); }
            catch (Exception ex) { Core.Log.LogWarning($"[Uriel] deferred action failed: {ex.Message}"); }
        }
    }
}

/// <summary>Host MonoBehaviour — Unity calls Update() every frame on the dedicated server.
/// The IntPtr ctor is required for an Il2Cpp-injected MonoBehaviour.</summary>
public class UrielTickBehaviour : MonoBehaviour
{
    public UrielTickBehaviour(IntPtr ptr) : base(ptr) { }
    void Update() => Tick.Drain();
}
