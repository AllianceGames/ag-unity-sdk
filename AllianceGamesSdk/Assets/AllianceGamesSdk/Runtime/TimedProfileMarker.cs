using System;
using System.Diagnostics;
using Unity.Profiling;
using System.Collections.Generic;

public sealed class TimedProfilerMarker
{
    private static Dictionary<string, TimedProfilerMarker> timedProfileMarkers = new();
    private readonly ProfilerMarker _marker;

    // stats
    private string _name;
    private double _sumMs;
    private double _minMs = double.MaxValue;
    private double _maxMs;
    private long _count;

    public TimedProfilerMarker(ProfilerCategory category, string name)
    {
        _marker = new ProfilerMarker(category, name);
        _name = name;
        timedProfileMarkers.Add(name, this);
    }

    // Disposable scope struct
    public readonly struct Scope : IDisposable
    {
        private readonly TimedProfilerMarker _owner;
        private readonly Stopwatch _sw;

        public Scope(TimedProfilerMarker owner)
        {
            _owner = owner;
            _owner._marker.Begin();
            _sw = Stopwatch.StartNew();
        }

        public void Dispose()
        {
            _sw.Stop();
            _owner._marker.End();

            double ms = _sw.Elapsed.TotalMilliseconds;
            _owner._count++;
            _owner._sumMs += ms;
            if (ms < _owner._minMs) _owner._minMs = ms;
            if (ms > _owner._maxMs) _owner._maxMs = ms;
        }
    }

    /// <summary> Creates an AutoScope like ProfilerMarker.Auto() </summary>
    public Scope Auto() => new Scope(this);

    public void PrintSummary()
    {
        if (_count == 0) return;
        double avg = _sumMs / _count;
        UnityEngine.Debug.Log($"[{_name}] avg {avg:F3} ms  min {_minMs:F3} ms  max {_maxMs:F3} ms  (n={_count})");
    }

    public static void PrintAllSummaries()
    {
        foreach (var (_, marker) in timedProfileMarkers)
        {
            marker.PrintSummary();
        }
    }

    public void ResetStats()
    {
        _sumMs = 0;
        _minMs = double.MaxValue;
        _maxMs = 0;
        _count = 0;
    }
}
