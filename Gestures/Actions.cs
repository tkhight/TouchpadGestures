namespace Gestures;

/// <summary>キー・ホイール入力の送信先。実機では Win32InputSender、テストでは偽物を差し込む。</summary>
public interface IInputSender
{
    void KeyDown(ushort vk);
    void KeyUp(ushort vk);
    void Wheel(int delta, bool horizontal);

    /// <summary>マウスのボタンを、現在のカーソル位置で count 回クリックする。</summary>
    void Click(MouseButton button, int count) => throw new NotSupportedException("この送信先はクリックに対応していません");

    /// <summary>マウスのボタンを押す / 離す (押し続ける動作用)。</summary>
    void MouseButtonEvent(MouseButton button, bool down) => throw new NotSupportedException("この送信先はマウスボタンに対応していません");

    /// <summary>マウスカーソルを、今の位置から (dx, dy) ピクセルだけ動かす。</summary>
    void MoveCursor(int dx, int dy) => throw new NotSupportedException("この送信先はカーソル移動に対応していません");
}

public enum MouseButton { Left, Right, Middle }

/// <summary>ジェスチャーが発火したときに行う動作。</summary>
public interface IGestureAction
{
    string Description { get; }
    void Execute(IInputSender sender);
}

/// <summary>"Ctrl+Alt+Right" のようなキーの組み合わせを押して離す。</summary>
public sealed class HotkeyAction : IGestureAction
{
    readonly ushort[] _keys;

    public string Description { get; }

    /// <exception cref="FormatException">未知のキー名、または空のとき。</exception>
    public HotkeyAction(string chord)
    {
        var parts = chord.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(p => p.Length == 0))
            throw new FormatException($"Invalid key chord: '{chord}'");
        _keys = parts.Select(KeyNames.Parse).ToArray();
        Description = chord;
    }

    public void Execute(IInputSender sender)
    {
        int pressed = 0;
        try
        {
            foreach (var k in _keys) { sender.KeyDown(k); pressed++; }
        }
        finally
        {
            // 途中で失敗しても、押したキーは必ず逆順に離す(修飾キーの押しっぱなし防止)
            for (int i = pressed - 1; i >= 0; i--) sender.KeyUp(_keys[i]);
        }
    }
}

/// <summary>
/// マウスボタンをクリックする。holdChord を指定すると、そのキーを押したままクリックする
/// (例: "Win" を押しながら左クリック)。位置は、そのときのマウスカーソルの位置。
/// </summary>
public sealed class ClickAction : IGestureAction
{
    // 割り当てのない仮想キー。Win / Alt を押したままクリックしたあと、離す前にこれを押して離すと、
    // 「Win / Alt だけを押して離した」とみなされず、スタートメニュー / メニューバーが開かない
    const ushort MaskKey = 0xE8;

    readonly ushort[] _keys;
    readonly MouseButton _button;
    readonly int _count;

    public string Description { get; }

    /// <exception cref="FormatException">未知のキー名のとき。</exception>
    public ClickAction(MouseButton button, int count = 1, string? holdChord = null)
    {
        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), "count must be >= 1");
        _button = button;
        _count = count;
        _keys = holdChord is null ? Array.Empty<ushort>() : holdChord.Split('+', StringSplitOptions.TrimEntries).Select(KeyNames.Parse).ToArray();
        Description = (holdChord is null ? "" : holdChord + " + ")
            + (count == 2 ? "ダブル" : count > 2 ? count + "回" : "") + button switch
            {
                MouseButton.Left => "左クリック",
                MouseButton.Right => "右クリック",
                _ => "中クリック",
            };
    }

    public void Execute(IInputSender sender)
    {
        int pressed = 0;
        try
        {
            foreach (var k in _keys) { sender.KeyDown(k); pressed++; }
            sender.Click(_button, _count);
            if (_keys.Any(k => k is 0x5B or 0x5C or 0x12))
            {
                sender.KeyDown(MaskKey);
                sender.KeyUp(MaskKey);
            }
        }
        finally
        {
            // 途中で失敗しても、押したキーは必ず逆順に離す
            for (int i = pressed - 1; i >= 0; i--) sender.KeyUp(_keys[i]);
        }
    }
}

/// <summary>
/// 発火したときに始まり、指がパッドに触れている間つづく動作 (押し続ける・ドラッグするなど)。
/// 始めた動作は、必ず End で終わらせる (押したキーやボタンを離す)。
/// </summary>
public interface IHoldAction : IGestureAction
{
    void Begin(Frame frame, IInputSender sender);

    /// <summary>触れている間、フレームごとに呼ばれる。続けるなら true、ここで終わらせるなら false。</summary>
    bool Update(Frame frame, IInputSender sender);

    void End(IInputSender sender);
}

/// <summary>
/// キー(holdChord)とマウスボタンを押したままにして、全部の指が離れたら離す。
/// follow が true なら、押している間、指の動きに合わせてカーソルを動かす
/// (3 本指などは、普通はカーソルが動かないため)。
/// presses が 2 以上なら、最後の 1 回の前に、押して離すのを (presses - 1) 回行う。
/// 例: PowerToys の「つかんで移動」は、Win を押しながらダブルタップして、2 回目を押したまま動かす (presses = 2)。
/// タッチパッドの「ダブルタップして押したまま」は、Windows からは、ボタンを押して離し、もう一度押したままに見える。
/// </summary>
public sealed class HoldAction : IHoldAction
{
    const ushort MaskKey = 0xE8;           // ClickAction と同じ: Win / Alt を離したときにメニューが開かないようにする
    const long MaxHoldMs = 60_000;         // 指が離れた通知が来なくても、押しっぱなしにならないようにする上限
    const double PixelsPerPadWidth = 1500; // パッド幅 1.0 の動きを、speed = 1 のときに何ピクセルにするか

    readonly ushort[] _keys;
    readonly MouseButton _button;
    readonly bool _follow;
    readonly double _speed;
    readonly int _presses;

    bool _held;
    long _startMs;
    (double X, double Y)? _prev;
    int _prevCount;
    double _remX, _remY;

    public string Description { get; }

    /// <exception cref="FormatException">未知のキー名のとき。</exception>
    public HoldAction(MouseButton button, string? holdChord = null, bool follow = true, double speed = 1.0, int presses = 1)
    {
        if (speed <= 0) throw new ArgumentOutOfRangeException(nameof(speed), "speed must be > 0");
        if (presses < 1) throw new ArgumentOutOfRangeException(nameof(presses), "presses must be >= 1");
        _presses = presses;
        _button = button;
        _follow = follow;
        _speed = speed;
        _keys = holdChord is null ? Array.Empty<ushort>() : holdChord.Split('+', StringSplitOptions.TrimEntries).Select(KeyNames.Parse).ToArray();
        Description = (holdChord is null ? "" : holdChord + " + ") + button switch
        {
            MouseButton.Left => "左ボタン",
            MouseButton.Right => "右ボタン",
            _ => "中ボタン",
        } + (presses == 1 ? "を押し続ける" : "を" + presses + "回押して、最後を押し続ける");
    }

    public bool IsHeld => _held;

    /// <summary>この動作は、始める・続ける・終わらせるの 3 つに分かれている。Dispatcher が Begin / Update / End を呼ぶ。</summary>
    public void Execute(IInputSender sender) => throw new NotSupportedException("HoldAction は Begin / Update / End で使う");

    public void Begin(Frame frame, IInputSender sender)
    {
        if (_held) return;
        int pressed = 0;
        try
        {
            foreach (var k in _keys) { sender.KeyDown(k); pressed++; }
            // 最後の 1 回の前は、押して離す (ダブルタップの 1 回目など)
            for (int i = 1; i < _presses; i++)
            {
                sender.MouseButtonEvent(_button, true);
                sender.MouseButtonEvent(_button, false);
            }
            sender.MouseButtonEvent(_button, true);
        }
        catch
        {
            for (int i = pressed - 1; i >= 0; i--) sender.KeyUp(_keys[i]);
            throw;
        }
        _held = true;
        _startMs = frame.TimeMs;
        _prev = null;
        _prevCount = 0;
        _remX = _remY = 0;
    }

    public bool Update(Frame frame, IInputSender sender)
    {
        if (!_held) return false;
        if (frame.TimeMs - _startMs > MaxHoldMs) return false;
        var cs = frame.Contacts;
        if (!_follow || cs.Count == 0) return true;

        double cx = cs.Average(c => c.X), cy = cs.Average(c => c.Y);
        // 指の本数が変わった瞬間は、重心が飛ぶので動かさない
        if (_prev is { } p && _prevCount == cs.Count)
        {
            double dx = (cx - p.X) * PixelsPerPadWidth * _speed + _remX;
            double dy = (cy - p.Y) * PixelsPerPadWidth * _speed + _remY;
            int ix = (int)Math.Truncate(dx), iy = (int)Math.Truncate(dy);
            _remX = dx - ix;
            _remY = dy - iy;
            if (ix != 0 || iy != 0) sender.MoveCursor(ix, iy);
        }
        _prev = (cx, cy);
        _prevCount = cs.Count;
        return true;
    }

    public void End(IInputSender sender)
    {
        if (!_held) return;
        _held = false;
        Exception? first = null;
        void Try(Action a)
        {
            try { a(); } catch (Exception ex) { first ??= ex; }
        }

        Try(() => sender.MouseButtonEvent(_button, false));
        if (_keys.Any(k => k is 0x5B or 0x5C or 0x12))
        {
            Try(() => sender.KeyDown(MaskKey));
            Try(() => sender.KeyUp(MaskKey));
        }
        // 1 つ失敗しても、残りのキーは必ず離す
        for (int i = _keys.Length - 1; i >= 0; i--)
        {
            ushort k = _keys[i];
            Try(() => sender.KeyUp(k));
        }
        if (first is not null) throw first;
    }
}

/// <summary>マウスホイールを回す。notches は 1 ノッチ = 120。正は上/右、負は下/左。</summary>
public sealed class WheelAction : IGestureAction
{
    const int WheelDelta = 120;
    readonly int _notches;
    readonly bool _horizontal;

    public string Description { get; }

    public WheelAction(int notches, bool horizontal = false)
    {
        _notches = notches;
        _horizontal = horizontal;
        Description = (horizontal ? "HWheel " : "Wheel ") + notches;
    }

    public void Execute(IInputSender sender) => sender.Wheel(_notches * WheelDelta, _horizontal);
}

/// <summary>キー名 → 仮想キーコード。</summary>
public static class KeyNames
{
    static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = 0x11, ["Control"] = 0x11, ["Alt"] = 0x12, ["Shift"] = 0x10, ["Win"] = 0x5B,
        ["Enter"] = 0x0D, ["Esc"] = 0x1B, ["Escape"] = 0x1B, ["Tab"] = 0x09, ["Space"] = 0x20,
        ["Backspace"] = 0x08, ["Back"] = 0x08, ["Delete"] = 0x2E, ["Del"] = 0x2E,
        ["Insert"] = 0x2D, ["Ins"] = 0x2D, ["Home"] = 0x24, ["End"] = 0x23,
        ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["PrintScreen"] = 0x2C,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["VolumeMute"] = 0xAD, ["VolumeDown"] = 0xAE, ["VolumeUp"] = 0xAF,
        // 記号キーは配列によって位置が違う (Plus / Minus は OEM キー)。ズームなら Ctrl+NumAdd が配列に依らず確実
        ["Plus"] = 0xBB, ["Minus"] = 0xBD, ["Comma"] = 0xBC, ["Period"] = 0xBE,
        ["NumAdd"] = 0x6B, ["NumSubtract"] = 0x6D, ["NumMultiply"] = 0x6A, ["NumDivide"] = 0x6F,
        ["MediaNext"] = 0xB0, ["MediaPrev"] = 0xB1, ["MediaStop"] = 0xB2, ["MediaPlayPause"] = 0xB3,
    };

    /// <summary>仮想キーコード → 代表のキー名 (Parse で同じコードに戻る名前)。知らないコードは null。</summary>
    public static string? NameOf(ushort vk)
    {
        foreach (var (name, code) in Named) // 先に登録した名前が代表 (Ctrl, Esc, Backspace, Delete, Insert)
            if (code == vk) return name;
        if ((vk >= 'A' && vk <= 'Z') || (vk >= '0' && vk <= '9')) return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);
        return null;
    }

    /// <exception cref="FormatException">未知のキー名のとき。</exception>
    public static ushort Parse(string name)
    {
        if (Named.TryGetValue(name, out var vk)) return vk;
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return c; // VK は ASCII と同じ
        }
        if ((name[0] == 'F' || name[0] == 'f') && int.TryParse(name.AsSpan(1), out int n) && n >= 1 && n <= 24)
            return (ushort)(0x70 + n - 1);
        throw new FormatException($"Unknown key name: '{name}'");
    }
}

/// <summary>アプリ名 (実行ファイル名) の比較用の正規化。大文字小文字と ".exe" の有無を無視する。</summary>
public static class AppNames
{
    /// <summary>空なら null (どのアプリでも有効)。</summary>
    public static string? Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var s = name.Trim().ToLowerInvariant();
        return s.EndsWith(".exe") && s.Length > 4 ? s[..^4] : s;
    }
}

/// <summary>
/// ジェスチャー定義と動作の対応。App が null ならどのアプリでも有効。
/// 値があれば、そのアプリ (プロセス名) が前面のときだけ有効で、同じジェスチャーの App なしの割り当てより優先される。
/// </summary>
public sealed record GestureBinding(IGestureSpec Spec, IGestureAction Action, string? App = null)
{
    /// <summary>スワイプの定義のとき。それ以外は null。</summary>
    public SlotSwipeSpec? Swipe => Spec as SlotSwipeSpec;

    /// <summary>タップの定義のとき。それ以外は null。</summary>
    public TapSpec? Tap => Spec as TapSpec;

    /// <summary>ピンチの定義のとき。それ以外は null。</summary>
    public PinchSpec? Pinch => Spec as PinchSpec;
}

/// <summary>判定結果に応じて、対応する動作を実行する。</summary>
public sealed class GestureDispatcher
{
    readonly List<(GestureBinding Binding, string? App, IGestureRecognizer Recognizer)> _entries;
    readonly IInputSender _sender;
    readonly Func<string?>? _foregroundApp;

    /// <summary>最後に発火したときの前面アプリ名 (正規化済み)。取れなかった/発火が無いときは null。</summary>
    public string? LastApp { get; private set; }

    /// <param name="foregroundApp">前面アプリ名を返す。発火したときだけ呼ばれる。null なら App 付きの割り当ては働かない。</param>
    /// <exception cref="ArgumentException">同じアプリ・同じ定義に複数の動作を割り当てたとき。</exception>
    public GestureDispatcher(IEnumerable<GestureBinding> bindings, IInputSender sender, Func<string?>? foregroundApp = null)
    {
        var seen = new HashSet<(string?, IGestureSpec)>();
        _entries = new();
        foreach (var b in bindings)
        {
            var app = AppNames.Normalize(b.App);
            if (!seen.Add((app, b.Spec)))
                throw new ArgumentException("同じ定義に複数の動作が割り当てられています: " + b.Spec.Name);
            _entries.Add((b, app, GestureEngine.Create(b.Spec)));
        }
        _sender = sender;
        _foregroundApp = foregroundApp;
    }

    /// <summary>
    /// フレームを与え、発火した定義を返す。execute が true のときは、その動作も実行する。
    /// execute が false でも判定は進む(画面表示や、しきい値の調整のため)。送信だけが行われない。
    /// 判定はアプリに関係なく常に進め、発火したときに前面アプリを見て、有効な割り当てだけを採用する。
    /// </summary>
    public IReadOnlyList<IGestureSpec> Feed(Frame frame, bool execute = true)
    {
        UpdateHolds(frame, execute);

        var hits = new List<(GestureBinding Binding, string? App, int Count)>();
        foreach (var (b, app, r) in _entries)
            if (r.Feed(frame)) hits.Add((b, app, r.LastFireCount));

        LastApp = null;
        var fired = new List<IGestureSpec>();
        if (hits.Count == 0) return fired;

        var current = AppNames.Normalize(_foregroundApp?.Invoke());
        LastApp = current;
        var run = new List<(IGestureAction Action, int Count)>();
        foreach (var (b, app, count) in hits)
        {
            if (!IsActive(b, app, current)) continue;
            for (int i = 0; i < count; i++) fired.Add(b.Spec);
            run.Add((b.Action, count));
        }
        if (execute)
            foreach (var (action, count) in run)
            {
                if (action is IHoldAction hold)
                {
                    if (_holding.Contains(hold)) continue; // 押している最中の再発火 (連続発火など) は無視
                    hold.Begin(frame, _sender);
                    _holding.Add(hold);
                    if (!hold.Update(frame, _sender)) ReleaseOne(hold);
                    continue;
                }
                for (int i = 0; i < count; i++) action.Execute(_sender);
            }
        return fired;
    }

    readonly List<IHoldAction> _holding = new();

    /// <summary>押し続けている動作を、すべて終わらせる (キー・ボタンを離す)。設定の読み直しや終了のときに呼ぶ。</summary>
    public void ReleaseAll()
    {
        Exception? first = null;
        foreach (var h in _holding.ToList())
        {
            try { ReleaseOne(h); } catch (Exception ex) { first ??= ex; }
        }
        if (first is not null) throw first;
    }

    void ReleaseOne(IHoldAction h)
    {
        _holding.Remove(h);
        h.End(_sender);
    }

    // 押し続けている動作に、このフレームを知らせる。全員の指が離れた・実行がオフになった・動作側が終わらせたいときは終わらせる
    void UpdateHolds(Frame frame, bool execute)
    {
        if (_holding.Count == 0) return;
        if (!execute || frame.Contacts.Count == 0)
        {
            ReleaseAll();
            return;
        }
        foreach (var h in _holding.ToList())
            if (!h.Update(frame, _sender)) ReleaseOne(h);
    }

    bool IsActive(GestureBinding b, string? app, string? current)
    {
        if (app is not null) return app == current;
        // App なしは、同じジェスチャーに現在のアプリ専用の割り当てがあれば、それに譲る
        if (current is null) return true;
        return !_entries.Any(e => e.App == current && e.Binding.Spec.Key == b.Spec.Key);
    }
}
