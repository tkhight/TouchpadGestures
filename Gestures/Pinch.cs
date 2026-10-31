namespace Gestures;

public enum PinchDirection { In, Out }

/// <summary>
/// 「FingerCount 本の指が、全体として開く(Out)/ 閉じる(In)」というジェスチャーの定義。
/// 開き具合は、各指の「全員の重心からの距離」の平均で測る。指が揃った時点を基準に、その平均が
/// PinchDistance だけ増えた(Out)/ 減った(In)ら発火する。
/// 全員が同じ向きに動くスワイプや、回転では平均は変わらないので、発火しない。
/// </summary>
/// <param name="PinchDistance">開き具合が変わるべき量 (パッド幅 = 1.0)。各指がだいたいこの距離だけ中心へ寄る/離れる。</param>
/// <param name="CentroidTolerance">
/// 重心が基準から動いてよい最大量。これを超えるとスワイプとみなして発火しない。既定は 0.10。
/// 実機の記録では、親指側だけが大きく動くため、ピンチの最中に重心も開き具合の変化量とほぼ同じだけ動いた
/// (中央値 0.08)。0.04 だと、自然なピンチの約 1 割しか届かなかった。
/// </param>
/// <param name="SettleMs">指が揃ってから基準を取るまでの待ち時間。</param>
/// <param name="RepeatDistance">null なら 1 回の接触につき 1 回だけ。値があれば、開き(閉じ)続ける間、この量ごとに連続で発火する。</param>
public sealed record PinchSpec(
    string Name,
    int FingerCount,
    PinchDirection Direction,
    double PinchDistance = 0.05,
    double CentroidTolerance = 0.10,
    long SettleMs = 0,
    double? RepeatDistance = null) : IGestureSpec
{
    public string Key => $"pinch|{FingerCount}|{Direction}";
}

/// <summary>1 つの PinchSpec を判定する。指の増減(ID 集合の変化)があれば判定をやり直す。</summary>
public sealed class PinchRecognizer : IGestureRecognizer
{
    enum State { Idle, Settling, Tracking, Repeating, Done }

    public const int MaxFiresPerFrame = 20;

    readonly HashSet<int> _ids = new();
    State _state = State.Idle;
    long _settleStart;
    double _baseSpread;
    (double X, double Y) _baseCentroid;
    double _anchor;

    public PinchSpec Spec { get; }
    IGestureSpec IGestureRecognizer.Spec => Spec;
    public int LastFireCount { get; private set; }

    public PinchRecognizer(PinchSpec spec)
    {
        if (spec.FingerCount < 2) throw new ArgumentOutOfRangeException(nameof(spec), "FingerCount must be >= 2");
        if (spec.PinchDistance <= 0) throw new ArgumentOutOfRangeException(nameof(spec), "PinchDistance must be > 0");
        if (spec.RepeatDistance <= 0) throw new ArgumentOutOfRangeException(nameof(spec), "RepeatDistance must be > 0");
        Spec = spec;
    }

    public bool Feed(Frame frame)
    {
        LastFireCount = 0;
        var cs = frame.Contacts;

        if (_state != State.Idle && !SameIds(cs)) Reset();
        if (cs.Count != Spec.FingerCount) return false;

        switch (_state)
        {
            case State.Idle:
                foreach (var c in cs) _ids.Add(c.Id);
                _settleStart = frame.TimeMs;
                _state = State.Settling;
                if (Spec.SettleMs > 0) return false;
                TakeBaseline(cs);
                return false;

            case State.Settling:
                if (frame.TimeMs - _settleStart >= Spec.SettleMs) TakeBaseline(cs);
                return false;

            case State.Tracking:
            case State.Repeating:
                return Track(cs);

            default:
                return false;
        }
    }

    static ((double X, double Y) Centroid, double Spread) Measure(IReadOnlyList<Contact> cs)
    {
        double cx = cs.Average(c => c.X), cy = cs.Average(c => c.Y);
        double spread = cs.Average(c => Math.Sqrt((c.X - cx) * (c.X - cx) + (c.Y - cy) * (c.Y - cy)));
        return ((cx, cy), spread);
    }

    void TakeBaseline(IReadOnlyList<Contact> cs)
    {
        var (centroid, spread) = Measure(cs);
        _baseCentroid = centroid;
        _baseSpread = spread;
        _state = State.Tracking;
    }

    bool Track(IReadOnlyList<Contact> cs)
    {
        var (centroid, spread) = Measure(cs);
        double dx = centroid.X - _baseCentroid.X, dy = centroid.Y - _baseCentroid.Y;
        if (Math.Sqrt(dx * dx + dy * dy) > Spec.CentroidTolerance)
        {
            _state = State.Done; // 全体が動いた: ピンチではなくスワイプ
            return false;
        }

        // 発火の向きに進んだ量 (開く方向が正)
        double along = Spec.Direction == PinchDirection.Out ? spread - _baseSpread : _baseSpread - spread;

        if (_state == State.Tracking)
        {
            if (along < Spec.PinchDistance) return false;

            int n = 1;
            if (Spec.RepeatDistance is double step)
                n = Math.Min(1 + (int)Math.Floor((along - Spec.PinchDistance) / step), MaxFiresPerFrame);
            _anchor = Spec.PinchDistance + (n - 1) * (Spec.RepeatDistance ?? 0);
            _state = Spec.RepeatDistance is null ? State.Done : State.Repeating;
            LastFireCount = n;
            return true;
        }

        double s = Spec.RepeatDistance!.Value;
        if (along < _anchor)
        {
            _anchor = Math.Max(Spec.PinchDistance, along); // 戻した: 折り返した位置から数え直す
            return false;
        }
        if (along < _anchor + s) return false;

        int count = Math.Min((int)Math.Floor((along - _anchor) / s), MaxFiresPerFrame);
        _anchor += count * s;
        LastFireCount = count;
        return true;
    }

    bool SameIds(IReadOnlyList<Contact> cs)
    {
        if (cs.Count != _ids.Count) return false;
        foreach (var c in cs) if (!_ids.Contains(c.Id)) return false;
        return true;
    }

    void Reset()
    {
        _state = State.Idle;
        _ids.Clear();
    }
}
