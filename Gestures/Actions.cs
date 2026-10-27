namespace Gestures;

/// <summary>キー・ホイール入力の送信先。実機では Win32InputSender、テストでは偽物を差し込む。</summary>
public interface IInputSender
{
    void KeyDown(ushort vk);
    void KeyUp(ushort vk);
    void Wheel(int delta, bool horizontal);
}

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
        ["MediaNext"] = 0xB0, ["MediaPrev"] = 0xB1, ["MediaStop"] = 0xB2, ["MediaPlayPause"] = 0xB3,
    };

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

/// <summary>ジェスチャー定義と動作の対応。</summary>
public sealed record GestureBinding(SlotSwipeSpec Spec, IGestureAction Action);

/// <summary>判定結果に応じて、対応する動作を実行する。</summary>
public sealed class GestureDispatcher
{
    readonly GestureEngine _engine;
    readonly Dictionary<SlotSwipeSpec, IGestureAction> _actions = new();
    readonly IInputSender _sender;

    /// <exception cref="ArgumentException">同じ定義に複数の動作を割り当てたとき。</exception>
    public GestureDispatcher(IEnumerable<GestureBinding> bindings, IInputSender sender)
    {
        var list = bindings.ToList();
        foreach (var b in list) _actions.Add(b.Spec, b.Action);
        _engine = new GestureEngine(list.Select(b => b.Spec));
        _sender = sender;
    }

    /// <summary>フレームを与え、発火した定義の動作を実行して、その定義を返す。</summary>
    public IReadOnlyList<SlotSwipeSpec> Feed(Frame frame)
    {
        var fired = _engine.Feed(frame);
        foreach (var spec in fired) _actions[spec].Execute(_sender);
        return fired;
    }
}
