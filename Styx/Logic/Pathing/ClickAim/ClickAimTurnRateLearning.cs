using System;

namespace Styx.Logic.Pathing;

// Per-mover or per-chase adaptation. Candidate facing deltas are not proof of
// input delivery; require two similar, plausible in-place samples before use.
// Never modify the shared turn calibration or learn from a moving character.
internal sealed class ClickAimTurnRateLearning
{
    private object? _memory, _reader;
    private IntPtr _window;
    private int _generation, _posKey, _negKey;
    private ulong _playerRecord;
    private string? _playerGuid;
    private double _basePos, _baseNeg;
    private double _posRate, _negRate, _posCandidate, _negCandidate;

    internal void Bind(object? memory, object? reader, IntPtr window, int generation,
        ulong playerRecord, string playerGuid, int posKey, int negKey, double posRate, double negRate)
    {
        if (ReferenceEquals(_memory, memory) && ReferenceEquals(_reader, reader) && _window == window &&
            _generation == generation && _playerRecord == playerRecord && _playerGuid == playerGuid &&
            _posKey == posKey && _negKey == negKey && _basePos == posRate && _baseNeg == negRate) return;
        _memory = memory; _reader = reader; _window = window; _generation = generation;
        _playerRecord = playerRecord; _playerGuid = playerGuid;
        _posKey = posKey; _negKey = negKey; _basePos = posRate; _baseNeg = negRate;
        _posRate = _negRate = _posCandidate = _negCandidate = 0;
    }

    internal double Rate(bool positive, double calibrated)
    {
        double learned = positive ? _posRate : _negRate;
        return learned > 0 ? learned : calibrated;
    }

    internal void ForgetCandidate(bool positive)
    {
        if (positive) _posCandidate = 0; else _negCandidate = 0;
    }

    internal void Observe(bool positive, double deltaDeg, double durationMs, double displacementYd)
    {
        double baseline = positive ? _basePos : _baseNeg;
        if (!double.IsFinite(deltaDeg) || !double.IsFinite(durationMs) || !double.IsFinite(displacementYd) ||
            !double.IsFinite(baseline) || baseline <= 0 || durationMs < 40 || durationMs > 600 ||
            displacementYd < 0 || displacementYd > 0.2 ||
            (positive ? deltaDeg < 2 : deltaDeg > -2) || Math.Abs(deltaDeg) > 90)
        { ForgetCandidate(positive); return; }
        double measured = Math.Abs(deltaDeg) / durationMs;
        if (measured < 0.02 || measured > 2.0 || measured < baseline * 0.25 || measured > baseline * 4.0)
        { ForgetCandidate(positive); return; }
        double candidate = positive ? _posCandidate : _negCandidate;
        if (candidate == 0 || Math.Abs(measured - candidate) > Math.Max(candidate, measured) * 0.35)
        {
            if (positive) _posCandidate = measured; else _negCandidate = measured;
            return;
        }
        // A single drift or stale sample cannot change the rate. Even a
        // consistent pair is bounded to 0.5-2x the measured calibration.
        double estimate = Math.Max(baseline * 0.5, Math.Min(baseline * 2.0, (candidate + measured) * 0.5));
        if (positive) { _posRate = estimate; _posCandidate = measured; }
        else { _negRate = estimate; _negCandidate = measured; }
    }
}
