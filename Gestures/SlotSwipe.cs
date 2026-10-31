namespace Gestures;

/// <summary>
/// 1本の接触点。X/Y の単位は呼び出し側で統一する(既定のしきい値は
/// 「パッド幅 = 1.0」に正規化した値を想定。Y も X の最大値で割ると縦横の縮尺が揃う)。
/// Y は下向きが正。
/// </summary>
public readonly record struct Contact(int Id, double X, double Y);

/// <summary>Tip Switch が ON の接触点だけを並べた 1 フレーム。</summary>
public sealed record Frame(long TimeMs, IReadOnlyList<Contact> Contacts);

public enum SwipeDirection { Left, Right, Up, Down }

/// <summary>
/// 「FingerCount 本のうち、左から MovingSlot 番目(0 始まり)の指だけが Direction へ動き、
/// 残りは固定」というジェスチャーの定義。
/// </summary>
/// <param name="MinDistance">
/// 動く指が進むべき距離。既定は 0.06。実機(小さなパッド)の記録では、0.15 だと自然なスワイプの約 1 割しか
/// 届かなかったため下げ、実際に使って 0.06 が扱いやすかった。
/// </param>
/// <param name="FixedTolerance">固定すべき指に許す最大移動量(ジッタ吸収)。</param>
/// <param name="SettleMs">
/// 指が揃ってから基準位置を取るまでの待ち時間。既定は 0 (揃った瞬間を基準にする)。着地と同時に動き出す癖で
/// 取りこぼすのを避けるため。着地直後のブレを無視したいときは 40 程度にする。
/// </param>
/// <param name="RepeatDistance">
/// null なら 1 回の接触につき 1 回だけ発火する。値があれば、最初の発火のあと
/// 同じ方向にこの距離だけ進むたびに再び発火する(音量やスクロールの連続操作用)。
/// 戻してから進み直した場合は、折り返した位置から数え直す。
/// </param>
public sealed record SlotSwipeSpec(
    string Name,
    int FingerCount,
    int MovingSlot,
    SwipeDirection Direction,
    double MinDistance = 0.06,
    double FixedTolerance = 0.03,
    long SettleMs = 0,
    double? RepeatDistance = null,
    IReadOnlyList<SwipeDirection?>? Motions = null) : IGestureSpec
{
    /// <summary>
    /// 指ごとの動き (左から順、null = 固定)。Motions が無ければ MovingSlot / Direction から作る。
    /// Motions を指定したときは、MovingSlot / Direction は無視される (表示用に最初に動く指を入れておく)。
    /// </summary>
    public IReadOnlyList<SwipeDirection?> EffectiveMotions()
    {
        if (Motions is not null) return Motions;
        var m = new SwipeDirection?[FingerCount];
        if (MovingSlot >= 0 && MovingSlot < FingerCount) m[MovingSlot] = Direction;
        return m;
    }

    /// <summary>同じ指の組み合わせ・動きかを見分けるキー (名前は設定で変えられるため含めない)。</summary>
    public string Key => FingerCount + "|" + string.Join(",", EffectiveMotions().Select(d => d?.ToString() ?? "-"));
}

/// <summary>
/// 1 つの SlotSwipeSpec を判定する。
/// 指の順位(スロット)は基準位置を取った瞬間の X 昇順で確定し、その後は Contact ID で追跡する。
/// 動く指が他の指を追い越しても、スロットは入れ替わらない。
/// 接触の ID 集合が変わった(指が増減した)ら判定をやり直す。
/// </summary>
public sealed class SlotSwipeRecognizer : IGestureRecognizer
{
    enum State { Idle, Settling, Tracking, Repeating, Done }

    /// <summary>1 フレームで連続発火できる回数の上限 (極端に小さい RepeatDistance への備え)。</summary>
    public const int MaxFiresPerFrame = 20;

    readonly Dictionary<int, int> _slotById = new();
    readonly Dictionary<int, (double X, double Y)> _baseline = new();
    readonly HashSet<int> _ids = new();
    readonly IReadOnlyList<SwipeDirection?> _motions;
    State _state = State.Idle;
    long _settleStart;
    double _anchor; // 連続発火: 直近に発火した位置 (動く指の進行距離)

    public SlotSwipeSpec Spec { get; }
    IGestureSpec IGestureRecognizer.Spec => Spec;

    /// <summary>直前の Feed で発火した回数。Feed が false のときは 0。</summary>
    public int LastFireCount { get; private set; }

    public SlotSwipeRecognizer(SlotSwipeSpec spec)
    {
        if (spec.FingerCount < 1) throw new ArgumentOutOfRangeException(nameof(spec), "FingerCount must be >= 1");
        if (spec.Motions is null)
        {
            if (spec.MovingSlot < 0 || spec.MovingSlot >= spec.FingerCount)
                throw new ArgumentOutOfRangeException(nameof(spec), "MovingSlot must be in [0, FingerCount)");
        }
        else
        {
            if (spec.Motions.Count != spec.FingerCount)
                throw new ArgumentOutOfRangeException(nameof(spec), "Motions must have FingerCount entries");
            if (spec.Motions.All(m => m is null))
                throw new ArgumentOutOfRangeException(nameof(spec), "Motions must have at least one moving finger");
        }
        _motions = spec.EffectiveMotions();
        if (spec.RepeatDistance <= 0)
            throw new ArgumentOutOfRangeException(nameof(spec), "RepeatDistance must be > 0");
        Spec = spec;
    }

    /// <summary>
    /// フレームを 1 つ与える。発火したフレームだけ true(回数は LastFireCount)。
    /// RepeatDistance が null なら 1 回の接触につき 1 回だけ。
    /// </summary>
    public bool Feed(Frame frame)
    {
        LastFireCount = 0;
        var contacts = frame.Contacts;

        if (_state != State.Idle && !SameIds(contacts)) Reset();
        if (contacts.Count != Spec.FingerCount) return false;

        switch (_state)
        {
            case State.Idle:
                foreach (var c in contacts) _ids.Add(c.Id);
                _settleStart = frame.TimeMs;
                _state = State.Settling;
                return false;

            case State.Settling:
                if (frame.TimeMs - _settleStart >= Spec.SettleMs) TakeBaseline(contacts);
                return false;

            case State.Tracking:
            case State.Repeating:
                return Track(contacts);

            default:
                return false;
        }
    }

    void TakeBaseline(IReadOnlyList<Contact> contacts)
    {
        var sorted = contacts.OrderBy(c => c.X).ToList();
        for (int slot = 0; slot < sorted.Count; slot++)
        {
            _slotById[sorted[slot].Id] = slot;
            _baseline[sorted[slot].Id] = (sorted[slot].X, sorted[slot].Y);
        }
        _state = State.Tracking;
    }

    bool Track(IReadOnlyList<Contact> contacts)
    {
        // 動く指が複数あるときは、全員が方向に合っていて、進行距離は一番短い指を基準にする
        double along = double.MaxValue;
        bool inDirection = true;
        foreach (var c in contacts)
        {
            var b = _baseline[c.Id];
            double dx = c.X - b.X, dy = c.Y - b.Y;
            if (_motions[_slotById[c.Id]] is not SwipeDirection dir)
            {
                if (Math.Sqrt(dx * dx + dy * dy) > Spec.FixedTolerance)
                {
                    _state = State.Done; // 固定のはずの指が動いた: この接触では発火しない
                    return false;
                }
                continue;
            }

            (double a, double perp) = dir switch
            {
                SwipeDirection.Right => (dx, dy),
                SwipeDirection.Left => (-dx, dy),
                SwipeDirection.Down => (dy, dx),
                _ => (-dy, dx),
            };
            along = Math.Min(along, a);
            if (Math.Abs(perp) > a) inDirection = false; // 進行方向から 45° を超えて逸れた
        }

        if (_state == State.Tracking)
        {
            if (along < Spec.MinDistance || !inDirection) return false;

            // 最初の発火。一気に大きく動いたときは、超えた分の連続発火も同じフレームで行う
            int n = 1;
            if (Spec.RepeatDistance is double first)
                n = Math.Min(1 + (int)Math.Floor((along - Spec.MinDistance) / first), MaxFiresPerFrame);
            _anchor = Spec.MinDistance + (n - 1) * (Spec.RepeatDistance ?? 0);
            _state = Spec.RepeatDistance is null ? State.Done : State.Repeating;
            LastFireCount = n;
            return true;
        }

        // 連続発火中: 直近の発火位置から RepeatDistance 進むたびに発火する
        double step = Spec.RepeatDistance!.Value;
        if (along < _anchor)
        {
            // 戻した: 折り返した位置から数え直す (ただし最初の発火位置より手前には戻さない)
            _anchor = Math.Max(Spec.MinDistance, along);
            return false;
        }
        if (!inDirection || along < _anchor + step) return false;

        int count = Math.Min((int)Math.Floor((along - _anchor) / step), MaxFiresPerFrame);
        _anchor += count * step;
        LastFireCount = count;
        return true;
    }

    bool SameIds(IReadOnlyList<Contact> contacts)
    {
        if (contacts.Count != _ids.Count) return false;
        foreach (var c in contacts) if (!_ids.Contains(c.Id)) return false;
        return true;
    }

    void Reset()
    {
        _state = State.Idle;
        _ids.Clear();
        _slotById.Clear();
        _baseline.Clear();
    }
}

/// <summary>複数の定義をまとめて判定する。</summary>
public sealed class GestureEngine
{
    readonly List<IGestureRecognizer> _recognizers;

    public GestureEngine(IEnumerable<IGestureSpec> specs)
        => _recognizers = specs.Select(Create).ToList();

    internal static IGestureRecognizer Create(IGestureSpec spec) => spec switch
    {
        SlotSwipeSpec s => new SlotSwipeRecognizer(s),
        TapSpec t => new TapRecognizer(t),
        PinchSpec p => new PinchRecognizer(p),
        LandOrderSpec l => new LandOrderRecognizer(l),
        _ => throw new ArgumentException("Unknown gesture spec: " + spec.GetType().Name, nameof(spec)),
    };

    /// <summary>
    /// このフレームで発火したジェスチャー定義を返す。連続発火で複数回発火したときは、
    /// その回数ぶん同じ定義が並ぶ。
    /// </summary>
    public IReadOnlyList<IGestureSpec> Feed(Frame frame)
    {
        var fired = new List<IGestureSpec>();
        foreach (var r in _recognizers)
            if (r.Feed(frame))
                for (int i = 0; i < r.LastFireCount; i++) fired.Add(r.Spec);
        return fired;
    }
}
