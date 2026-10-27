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
/// <param name="MinDistance">動く指が進むべき距離。</param>
/// <param name="FixedTolerance">固定すべき指に許す最大移動量(ジッタ吸収)。</param>
/// <param name="SettleMs">指が揃ってから基準位置を取るまでの待ち時間。</param>
public sealed record SlotSwipeSpec(
    string Name,
    int FingerCount,
    int MovingSlot,
    SwipeDirection Direction,
    double MinDistance = 0.15,
    double FixedTolerance = 0.03,
    long SettleMs = 40);

/// <summary>
/// 1 つの SlotSwipeSpec を判定する。
/// 指の順位(スロット)は基準位置を取った瞬間の X 昇順で確定し、その後は Contact ID で追跡する。
/// 動く指が他の指を追い越しても、スロットは入れ替わらない。
/// 接触の ID 集合が変わった(指が増減した)ら判定をやり直す。
/// </summary>
public sealed class SlotSwipeRecognizer
{
    enum State { Idle, Settling, Tracking, Done }

    readonly Dictionary<int, int> _slotById = new();
    readonly Dictionary<int, (double X, double Y)> _baseline = new();
    readonly HashSet<int> _ids = new();
    State _state = State.Idle;
    long _settleStart;

    public SlotSwipeSpec Spec { get; }

    public SlotSwipeRecognizer(SlotSwipeSpec spec)
    {
        if (spec.FingerCount < 1) throw new ArgumentOutOfRangeException(nameof(spec), "FingerCount must be >= 1");
        if (spec.MovingSlot < 0 || spec.MovingSlot >= spec.FingerCount)
            throw new ArgumentOutOfRangeException(nameof(spec), "MovingSlot must be in [0, FingerCount)");
        Spec = spec;
    }

    /// <summary>フレームを 1 つ与える。発火した瞬間だけ true(1 回の接触につき 1 回)。</summary>
    public bool Feed(Frame frame)
    {
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
        Contact? moving = null;
        foreach (var c in contacts)
        {
            var b = _baseline[c.Id];
            double dx = c.X - b.X, dy = c.Y - b.Y;
            if (_slotById[c.Id] == Spec.MovingSlot)
            {
                moving = c;
            }
            else if (Math.Sqrt(dx * dx + dy * dy) > Spec.FixedTolerance)
            {
                _state = State.Done; // 固定のはずの指が動いた: この接触では発火しない
                return false;
            }
        }

        var m = moving!.Value;
        var mb = _baseline[m.Id];
        double mdx = m.X - mb.X, mdy = m.Y - mb.Y;
        (double along, double perp) = Spec.Direction switch
        {
            SwipeDirection.Right => (mdx, mdy),
            SwipeDirection.Left => (-mdx, mdy),
            SwipeDirection.Down => (mdy, mdx),
            _ => (-mdy, mdx),
        };

        if (along >= Spec.MinDistance && Math.Abs(perp) <= along) // 進行方向から 45° 以内
        {
            _state = State.Done;
            return true;
        }
        return false;
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
    readonly List<SlotSwipeRecognizer> _recognizers;

    public GestureEngine(IEnumerable<SlotSwipeSpec> specs)
        => _recognizers = specs.Select(s => new SlotSwipeRecognizer(s)).ToList();

    /// <summary>このフレームで発火したジェスチャー定義を返す。</summary>
    public IReadOnlyList<SlotSwipeSpec> Feed(Frame frame)
    {
        var fired = new List<SlotSwipeSpec>();
        foreach (var r in _recognizers)
            if (r.Feed(frame)) fired.Add(r.Spec);
        return fired;
    }
}
