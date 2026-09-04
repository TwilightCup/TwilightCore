using System;
using UnityEngine;

namespace TwilightCore.Preload;

/// <summary>
/// Rolling frame-time window used by the chained-preload frame-spike
/// instrumentation (调查方案 §3.4). While enabled, each frame samples
/// <c>Time.unscaledDeltaTime</c> and keeps a small circular buffer; callers
/// attach the compact summary to the key preload stage lines so a low-end
/// machine can show whether the hitch comes from the additive scene load,
/// the probe freeze, or elsewhere.
/// </summary>
internal sealed class FrameSpikeMonitor
{
    public const int WindowSize = 60;

    private readonly float[] _samples = new float[WindowSize];
    private int _next;
    private int _count;
    private bool _enabled;

    public bool IsEnabled => _enabled;

    public void Begin()
    {
        _next = 0;
        _count = 0;
        _enabled = true;
    }

    public void End()
    {
        _enabled = false;
    }

    /// <summary>Record one frame's delta time. No-op while not in a sampled window.</summary>
    public void Sample(float deltaTime)
    {
        if (!_enabled) return;
        _samples[_next] = deltaTime;
        _next = (_next + 1) % WindowSize;
        if (_count < WindowSize) _count++;
    }

    /// <summary>Compact summary for stage log lines (empty when not collecting).</summary>
    public string Summary()
    {
        if (!_enabled || _count == 0) return string.Empty;

        long over50 = 0, over80 = 0, over120 = 0;
        float total = 0f, max = 0f;
        for (int i = 0; i < _count; i++)
        {
            float d = _samples[i];
            total += d;
            if (d > max) max = d;
            if (d > 0.050f) over50++;
            if (d > 0.080f) over80++;
            if (d > 0.120f) over120++;
        }

        float avg = total / _count;
        return $" frames={_count} avg={avg * 1000f:0.0}ms max={max * 1000f:0.0}ms >50={over50} >80={over80} >120={over120}";
    }
}
