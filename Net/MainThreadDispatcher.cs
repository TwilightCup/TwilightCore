using System;
using System.Collections.Generic;
using UnityEngine;

namespace TwilightCore.Net;

/// <summary>
/// Marshals work from background threads (the WebSocket receive loop) onto the
/// Unity main thread. Unity APIs (UI, scenes, game objects, CollectionManager)
/// may only be touched from the main thread, so every inbound message is
/// enqueued here and drained during Update().
/// </summary>
public class MainThreadDispatcher : MonoBehaviour
{
    private static MainThreadDispatcher _instance;
    private readonly Queue<Action> _queue = new Queue<Action>();
    private readonly object _lock = new object();

    public static bool Exists => _instance != null;

    /// <summary>
    /// Enqueue an action to run on the main thread next Update(). Safe to call
    /// from any thread. If no dispatcher exists yet (very early init on the main
    /// thread) the action runs inline.
    /// </summary>
    public static void Enqueue(Action action)
    {
        if (action == null) return;
        var inst = _instance;
        if (inst == null)
        {
            try { action(); }
            catch (Exception e) { Plugin.Logger.LogError("[MainThreadDispatcher] inline run failed: " + e); }
            return;
        }
        lock (inst._lock)
            inst._queue.Enqueue(action);
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    private void Update()
    {
        Action[] batch;
        lock (_lock)
        {
            if (_queue.Count == 0) return;
            batch = new Action[_queue.Count];
            _queue.CopyTo(batch, 0);
            _queue.Clear();
        }
        foreach (var a in batch)
        {
            try { a(); }
            catch (Exception e) { Plugin.Logger.LogError("[MainThreadDispatcher] queued action threw: " + e); }
        }
    }
}
