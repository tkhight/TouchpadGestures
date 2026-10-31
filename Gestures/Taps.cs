namespace Gestures;

/// <summary>ジェスチャー定義の共通部分。設定・動作の割り当て・画面表示がこれを通して扱う。</summary>
public interface IGestureSpec
{
    string Name { get; }

    /// <summary>同じ指の組み合わせ・動きかを見分けるキー (名前は設定で変えられるため含めない)。</summary>
    string Key { get; }
}

/// <summary>1 つの定義を判定する器。発火したフレームだけ Feed が true を返し、回数は LastFireCount に入る。</summary>
public interface IGestureRecognizer
{
    IGestureSpec Spec { get; }
    int LastFireCount { get; }
    bool Feed(Frame frame);
}

/// <summary>
/// 「FingerCount 本のうち、左から TapSlot 番目(0 始まり)の指で TapCount 回タップ」。
/// 残りの FingerCount-1 本は触れたまま動かさない。タップする指は、触れて(着地して)すぐ離す。
/// 着地した位置が、左から TapSlot 番目になっていないタップは数えない。
/// FingerCount=1 なら、他の指なしの 1 本指タップになる (Windows のタップと重なるので注意)。
/// </summary>
/// <param name="MaxTapMs">着地から離すまでの上限。これより長いと長押しとして数えない。</param>
/// <param name="MaxGapMs">TapCount が 2 以上のとき、離してから次の着地までの上限。</param>
/// <param name="MaxTapMove">タップの間に指が動いてよい最大量 (動いたらスワイプなので数えない)。</param>
/// <param name="FixedTolerance">触れたままの指に許す最大移動量。</param>
public sealed record TapSpec(
    string Name,
    int FingerCount,
    int TapSlot,
    int TapCount = 1,
    long MaxTapMs = 250,
    long MaxGapMs = 300,
    double MaxTapMove = 0.03,
    double FixedTolerance = 0.03) : IGestureSpec
{
    public string Key => $"tap|{FingerCount}|{TapSlot}|{TapCount}";
}

/// <summary>
/// 1 つの TapSpec を判定する。
/// 触れたままの指 (FingerCount-1 本) が揃ったらその位置を基準にし、そこへもう 1 本が
/// 着地して離れるたびに 1 回と数える。指が増減した・基準から動いた・遅すぎた、などで
/// 回数は 0 に戻り、また触れたままの指が揃うのを待つ。
/// </summary>
public sealed class TapRecognizer : IGestureRecognizer
{
    enum State { Idle, Resting, Down, Gap }

    readonly Dictionary<int, (double X, double Y)> _rest = new();
    State _state = State.Idle;
    int _tapId;
    (double X, double Y) _landPos;
    long _landTime, _liftTime;
    int _taps;

    public TapSpec Spec { get; }
    IGestureSpec IGestureRecognizer.Spec => Spec;

    public int LastFireCount { get; private set; }

    public TapRecognizer(TapSpec spec)
    {
        if (spec.FingerCount < 1) throw new ArgumentOutOfRangeException(nameof(spec), "FingerCount must be >= 1");
        if (spec.TapSlot < 0 || spec.TapSlot >= spec.FingerCount)
            throw new ArgumentOutOfRangeException(nameof(spec), "TapSlot must be in [0, FingerCount)");
        if (spec.TapCount < 1) throw new ArgumentOutOfRangeException(nameof(spec), "TapCount must be >= 1");
        Spec = spec;
    }

    public bool Feed(Frame frame)
    {
        LastFireCount = 0;
        if (_state != State.Idle)
        {
            if (Step(frame, out bool fired)) return fired;
            Reset(); // 続けられない状況になった: やり直して、このフレームも改めて見る
        }
        if (frame.Contacts.Count == Spec.FingerCount - 1) TakeRest(frame.Contacts);
        return false;
    }

    /// <summary>現在の状態でフレームを処理する。続けられないとき false。</summary>
    bool Step(Frame frame, out bool fired)
    {
        fired = false;
        var cs = frame.Contacts;
        int n = Spec.FingerCount;

        bool restOnly = cs.Count == n - 1 && cs.All(c => _rest.ContainsKey(c.Id));
        bool withTap = cs.Count == n && _rest.Keys.All(id => cs.Any(c => c.Id == id));

        if (!restOnly && !withTap) return false;
        if (!RestStaysPut(cs)) return false;

        switch (_state)
        {
            case State.Resting:
                if (restOnly) { TakeRest(cs); return true; }  // 基準を今の位置に更新し続ける
                return Land(frame, cs);

            case State.Down:
                if (withTap)
                {
                    var t = cs.First(c => c.Id == _tapId);
                    return frame.TimeMs - _landTime <= Spec.MaxTapMs && Moved(t) <= Spec.MaxTapMove
                           && cs.Count(c => !_rest.ContainsKey(c.Id)) == 1;
                }
                // 離れた
                if (frame.TimeMs - _landTime > Spec.MaxTapMs) return false;
                if (++_taps >= Spec.TapCount)
                {
                    fired = true;
                    LastFireCount = 1;
                    _taps = 0;
                    _state = State.Resting;
                    TakeRest(cs);
                }
                else
                {
                    _liftTime = frame.TimeMs;
                    _state = State.Gap;
                }
                return true;

            default: // Gap
                if (frame.TimeMs - _liftTime > Spec.MaxGapMs) return false;
                if (restOnly) return true;
                return Land(frame, cs);
        }
    }

    bool Land(Frame frame, IReadOnlyList<Contact> cs)
    {
        var tap = cs.Where(c => !_rest.ContainsKey(c.Id)).ToList();
        if (tap.Count != 1) return false;

        // 着地した指が、左から TapSlot 番目の位置であること
        int index = cs.OrderBy(c => c.X).ToList().FindIndex(c => c.Id == tap[0].Id);
        if (index != Spec.TapSlot) return false;

        _tapId = tap[0].Id;
        _landPos = (tap[0].X, tap[0].Y);
        _landTime = frame.TimeMs;
        _state = State.Down;
        return true;
    }

    double Moved(Contact c)
    {
        double dx = c.X - _landPos.X, dy = c.Y - _landPos.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    bool RestStaysPut(IReadOnlyList<Contact> cs)
    {
        foreach (var c in cs)
        {
            if (!_rest.TryGetValue(c.Id, out var b)) continue;
            double dx = c.X - b.X, dy = c.Y - b.Y;
            if (Math.Sqrt(dx * dx + dy * dy) > Spec.FixedTolerance) return false;
        }
        return true;
    }

    void TakeRest(IReadOnlyList<Contact> cs)
    {
        _rest.Clear();
        foreach (var c in cs) _rest[c.Id] = (c.X, c.Y);
        _state = State.Resting;
    }

    void Reset()
    {
        _state = State.Idle;
        _rest.Clear();
        _taps = 0;
    }
}
