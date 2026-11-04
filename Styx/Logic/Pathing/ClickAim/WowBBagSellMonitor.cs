using System;
using System.Threading.Tasks;
using GreenMagic;
using Styx.WoWInternals;
using BagSampleTask = System.Threading.Tasks.Task<(int ZeroTags, Styx.WoWInternals.WowBGuid Player)?>;

namespace Styx.Logic.Pathing;

// This is an experimental 70707 policy, not a verified inventory model.
internal sealed class WowBBagSellPolicy
{
    private int _consecutiveZero;
    private bool _awaitSpace;
    public void Reset() { _consecutiveZero = 0; _awaitSpace = false; }
    public void TripCompleted() { _consecutiveZero = 0; _awaitSpace = true; }
    public bool Observe(int? zeroTags)
    {
        if (!zeroTags.HasValue || zeroTags.Value < 0) { _consecutiveZero = 0; return false; }
        if (zeroTags.Value > 0) { _consecutiveZero = 0; _awaitSpace = false; return false; }
        if (_awaitSpace) return false;
        if (_consecutiveZero < 2) _consecutiveZero++;
        return _consecutiveZero >= 2;
    }
}

internal enum WowBStartupBagDecision { Hold, Proceed, Vendor, Timeout, DeferToSafety }
internal static class WowBStartupBagCheck
{
    internal static WowBStartupBagDecision Decide(bool enabled, bool timedOut, bool combatReadable,
        bool inCombat, bool vendorDue, bool freshSpace)
    {
        if (!enabled) return WowBStartupBagDecision.Proceed;
        if (timedOut) return WowBStartupBagDecision.Timeout;
        if (!combatReadable || inCombat) return WowBStartupBagDecision.DeferToSafety;
        if (vendorDue) return WowBStartupBagDecision.Vendor;
        return freshSpace ? WowBStartupBagDecision.Proceed : WowBStartupBagDecision.Hold;
    }
}

// Poll on the Grind thread. The bounded but potentially expensive pool scan is
// performed off-thread, so STOP never waits for it. Old run/attachment results
// and samples that took too long are discarded rather than used for selling.
internal sealed class WowBBagSellMonitor
{
    private const long IntervalMs = 5000, MaxAgeMs = 15000;
    private readonly WowBBagSellPolicy _policy = new WowBBagSellPolicy();
    private readonly Func<long> _clock;
    private readonly Func<Task<(int ZeroTags, WowBGuid Player)?>> _startSample;

    public WowBBagSellMonitor() : this(() => Environment.TickCount64,
        () => Task.Run(() => WowBInventoryProbe.TryCaptureSellingSubtotal(out int zeroTags, out WowBGuid player)
            ? ((int ZeroTags, WowBGuid Player)?)(zeroTags, player) : null)) { }

    internal WowBBagSellMonitor(Func<long> clock, Func<Task<(int ZeroTags, WowBGuid Player)?>> startSample)
    { _clock = clock; _startSample = startSample; }
    private BagSampleTask? _task;
    private WowBGuid _player;
    private Memory? _memory;
    private WowBReader? _reader;
    private long _started, _next, _observedAt;
    private int _generation, _taskGeneration;
    private bool _due;
    private int? _latest;
    private int _lastReport = int.MinValue;

    // The startup hold may release early only for a fresh, readable positive
    // sample. Unknown or a single zero is not evidence of free space.
    internal bool HasFreshSpace => _latest.HasValue && _latest.Value > 0 && _observedAt != 0 &&
        _clock() - _observedAt <= MaxAgeMs;

    public void Reset()
    {
        _generation++; _next = 0; _observedAt = 0; _due = false;
        _lastReport = int.MinValue; _latest = null; _player = default; _policy.Reset();
    }

    public void TripCompleted()
    {
        _generation++; _next = 0; _observedAt = 0; _due = false;
        _lastReport = int.MinValue; _latest = null; _policy.TripCompleted();
    }

    public bool Poll(Memory memory, WowBReader reader, out string? report)
    {
        report = null;
        long now = _clock();
        if (_reader != null && (_reader != reader || _memory != memory)) Reset();
        if (_observedAt != 0 && now - _observedAt > MaxAgeMs)
        { _policy.Observe(null); _observedAt = 0; _due = false; _latest = null; }
        if (_task != null && _task.IsCompleted)
        {
            (int ZeroTags, WowBGuid Player)? observation = null;
            if (_taskGeneration == _generation && _memory == memory && _reader == reader && now - _started <= MaxAgeMs &&
                _task.Status == TaskStatus.RanToCompletion)
                observation = _task.Result;
            _task = null;
            if (observation.HasValue && _player != observation.Value.Player)
            {
                if (!_player.IsEmpty) { _policy.Reset(); _due = false; }
                _player = observation.Value.Player;
            }
            int? value = observation?.ZeroTags;
            _latest = value;
            bool wasDue = _due;
            _observedAt = now;
            _due = _policy.Observe(value);
            if (_due && !wasDue) report = "[Grind] two experimental " + reader.ExperimentalBuildNumber + " zero-tag observations: vendor trip is due when safe.";
            int status = value ?? -1;
            if (status != _lastReport)
            {
                _lastReport = status;
                report = value.HasValue
                    ? "[Grind] experimental " + reader.ExperimentalBuildNumber + " bag candidate zero-tag subtotal=" + value.Value + "; auto-sell requires two consecutive zero observations."
                    : "[Grind] experimental " + reader.ExperimentalBuildNumber + " bag subtotal unavailable; automatic selling waits (Sell now remains available).";
            }
        }
        if (_task == null && now >= _next)
        {
            _memory = memory; _reader = reader; _started = now; _taskGeneration = _generation; _next = now + IntervalMs;
            _task = _startSample();
        }
        return _due && _observedAt != 0 && now - _observedAt <= MaxAgeMs;
    }
}
