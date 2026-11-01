namespace Gestures;

/// <summary>
/// 「FingerCount 本の指が、ほぼ同時に着地して、ほぼ同時に離れる」(同時タップ) を TapCount 回。
/// 触れている間は、指を動かさない。FingerCount 本より多く触れたときは数えない。
/// </summary>
/// <param name="MaxTapMs">最初の指が着地してから、全員が離れるまでの上限。これより長いと長押しとして数えない。</param>
/// <param name="MaxGapMs">TapCount が 2 以上のとき、全員が離れてから次の着地までの上限。</param>
/// <param name="MaxTapMove">触れている間に、指が着地した位置から動いてよい最大量 (動いたらスワイプなので数えない)。</param>
/// <param name="MaxSpreadMs">「同時」とみなす幅。最初の指の着地から最後の指の着地まで、最初の指が離れてから最後の指が離れるまで、それぞれの上限。</param>
public sealed record MultiTapSpec(
    string Name,
    int FingerCount,
    int TapCount = 1,
    long MaxTapMs = 250,
    long MaxGapMs = 300,
    double MaxTapMove = 0.03,
    long MaxSpreadMs = 100) : IGestureSpec
{
    public string Key => $"multitap|{FingerCount}|{TapCount}";
}

/// <summary>
/// 1 つの MultiTapSpec を判定する。全員が離れたフレームで、条件を満たしていれば 1 回と数える。
/// 失敗 (本数の超過・動いた・遅い・着地や離れ方が揃わない) したら、全員が離れるまで判定をやめる。
/// </summary>
public sealed class MultiTapRecognizer : IGestureRecognizer
{
    enum State { Idle, Landing, Held, Lifting, Failed }

    readonly Dictionary<int, (double X, double Y)> _land = new();
    State _state = State.Idle;
    long _firstLand, _firstLift, _lastLiftEnd;
    int _taps;

    public MultiTapSpec Spec { get; }
    IGestureSpec IGestureRecognizer.Spec => Spec;
    public int LastFireCount { get; private set; }

    public MultiTapRecognizer(MultiTapSpec spec)
    {
        if (spec.FingerCount < 1) throw new ArgumentOutOfRangeException(nameof(spec), "FingerCount must be >= 1");
        if (spec.TapCount < 1) throw new ArgumentOutOfRangeException(nameof(spec), "TapCount must be >= 1");
        if (spec.MaxTapMs <= 0 || spec.MaxGapMs <= 0 || spec.MaxSpreadMs < 0)
            throw new ArgumentOutOfRangeException(nameof(spec), "time limits must be positive");
        if (spec.MaxTapMove <= 0) throw new ArgumentOutOfRangeException(nameof(spec), "MaxTapMove must be > 0");
        Spec = spec;
    }

    public bool Feed(Frame frame)
    {
        LastFireCount = 0;
        var cs = frame.Contacts;

        if (cs.Count == 0) return AllLifted(frame.TimeMs);
        if (_state == State.Failed) return false;

        if (_state == State.Idle)
        {
            // 前のタップから間が空きすぎていたら、数え直す
            if (_taps > 0 && frame.TimeMs - _lastLiftEnd > Spec.MaxGapMs) _taps = 0;
            _state = State.Landing;
            _firstLand = frame.TimeMs;
            _land.Clear();
        }

        if (cs.Count > Spec.FingerCount) return Fail();
        if (frame.TimeMs - _firstLand > Spec.MaxTapMs) return Fail();

        foreach (var c in cs)
        {
            if (_land.ContainsKey(c.Id)) continue;
            // 離れ始めたあとの着地、揃う前に時間が経ちすぎた着地は、同時ではない
            if (_state == State.Lifting || frame.TimeMs - _firstLand > Spec.MaxSpreadMs) return Fail();
            _land[c.Id] = (c.X, c.Y);
        }

        bool anyLifted = _land.Keys.Any(id => !cs.Any(c => c.Id == id));
        if (anyLifted)
        {
            if (_land.Count < Spec.FingerCount) return Fail();   // 揃う前に離れた
            if (_state != State.Lifting)
            {
                _state = State.Lifting;
                _firstLift = frame.TimeMs;
            }
            if (frame.TimeMs - _firstLift > Spec.MaxSpreadMs) return Fail();
        }

        foreach (var c in cs)
        {
            var b = _land[c.Id];
            double dx = c.X - b.X, dy = c.Y - b.Y;
            if (Math.Sqrt(dx * dx + dy * dy) > Spec.MaxTapMove) return Fail();
        }

        if (_state == State.Landing && _land.Count == Spec.FingerCount) _state = State.Held;
        return false;
    }

    bool AllLifted(long timeMs)
    {
        bool complete = _state is State.Held or State.Lifting;
        long firstLift = _state == State.Lifting ? _firstLift : timeMs;
        bool ok = complete && timeMs - firstLift <= Spec.MaxSpreadMs && timeMs - _firstLand <= Spec.MaxTapMs;

        _land.Clear();
        if (_state == State.Idle) return false;     // 何も触れていなかった: 数え途中の回数は残す
        _state = State.Idle;

        if (!ok)
        {
            _taps = 0;
            return false;
        }
        if (++_taps >= Spec.TapCount)
        {
            _taps = 0;
            LastFireCount = 1;
            return true;
        }
        _lastLiftEnd = timeMs;
        return false;
    }

    bool Fail()
    {
        _state = State.Failed;
        _taps = 0;
        return false;
    }
}
