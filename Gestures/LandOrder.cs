namespace Gestures;

/// <summary>
/// 「FingerCount 本の指を、Order の順に着地させる」というジェスチャーの定義。
/// Order は、着地した順に、その指が左から何番目か (0 始まり、全員が揃った時点の X 座標順) を並べたもの。
/// 例: 3 本で { 0, 1, 2 } は「左 → 中央 → 右の順に置く」、{ 2, 0, 1 } は「右 → 左 → 中央」。
/// 全員が触れたままで、最後の指が着地した時点で発火する。
/// </summary>
/// <param name="MinGapMs">着地と着地の間隔の下限。これより短いと「同時に置いた」とみなして数えない (普通に 3 本を置いただけで発火しないため)。</param>
/// <param name="MaxGapMs">着地と着地の間隔の上限。</param>
/// <param name="MaxMove">着地した指が、着地した位置から動いてよい最大量 (動いたらスワイプなので数えない)。</param>
public sealed record LandOrderSpec(
    string Name,
    int FingerCount,
    IReadOnlyList<int> Order,
    long MinGapMs = 80,
    long MaxGapMs = 500,
    double MaxMove = 0.03) : IGestureSpec
{
    public string Key => $"land|{FingerCount}|{string.Join(",", Order)}";
}

/// <summary>1 つの LandOrderSpec を判定する。指が離れる、順番や間隔が合わない、動くと、全員が離れるまで判定をやめる。</summary>
public sealed class LandOrderRecognizer : IGestureRecognizer
{
    enum State { Idle, Landing, Done, Failed }

    readonly List<(int Id, long TimeMs, double X, double Y)> _landed = new();
    State _state = State.Idle;

    public LandOrderSpec Spec { get; }
    IGestureSpec IGestureRecognizer.Spec => Spec;
    public int LastFireCount { get; private set; }

    public LandOrderRecognizer(LandOrderSpec spec)
    {
        if (spec.FingerCount < 2) throw new ArgumentOutOfRangeException(nameof(spec), "FingerCount must be >= 2");
        if (spec.Order.Count != spec.FingerCount || !spec.Order.OrderBy(x => x).SequenceEqual(Enumerable.Range(0, spec.FingerCount)))
            throw new ArgumentException("Order must be a permutation of 0..FingerCount-1", nameof(spec));
        if (spec.MinGapMs < 0 || spec.MaxGapMs < spec.MinGapMs)
            throw new ArgumentOutOfRangeException(nameof(spec), "gap range is invalid");
        if (spec.MaxMove <= 0) throw new ArgumentOutOfRangeException(nameof(spec), "MaxMove must be > 0");
        Spec = spec;
    }

    public bool Feed(Frame frame)
    {
        LastFireCount = 0;
        var cs = frame.Contacts;

        if (cs.Count == 0)
        {
            Reset();
            return false;
        }
        if (_state is State.Done or State.Failed) return false;

        // 着地済みの指が離れたら失敗
        foreach (var l in _landed)
            if (!cs.Any(c => c.Id == l.Id)) return Fail();

        var fresh = cs.Where(c => !_landed.Any(l => l.Id == c.Id)).ToList();
        if (fresh.Count > 1) return Fail(); // 同じフレームに複数: 同時に置いた
        if (fresh.Count == 1)
        {
            if (_landed.Count > 0)
            {
                long gap = frame.TimeMs - _landed[^1].TimeMs;
                if (gap < Spec.MinGapMs || gap > Spec.MaxGapMs) return Fail();
            }
            if (_landed.Count >= Spec.FingerCount) return Fail();
            _landed.Add((fresh[0].Id, frame.TimeMs, fresh[0].X, fresh[0].Y));
            _state = State.Landing;
        }

        foreach (var l in _landed)
        {
            var c = cs.First(x => x.Id == l.Id);
            double dx = c.X - l.X, dy = c.Y - l.Y;
            if (Math.Sqrt(dx * dx + dy * dy) > Spec.MaxMove) return Fail();
        }

        if (_landed.Count < Spec.FingerCount) return false;

        // 全員が揃った: 着地の順に、その指が左から何番目かを並べて、定義と比べる
        var byX = cs.OrderBy(c => c.X).Select(c => c.Id).ToList();
        var sequence = _landed.Select(l => byX.IndexOf(l.Id));
        if (!sequence.SequenceEqual(Spec.Order)) return Fail();

        _state = State.Done;
        LastFireCount = 1;
        return true;
    }

    bool Fail()
    {
        _state = State.Failed;
        return false;
    }

    void Reset()
    {
        _state = State.Idle;
        _landed.Clear();
    }
}
