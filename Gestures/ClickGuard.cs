using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Gestures;

/// <summary>
/// ジェスチャー (suppressClick) の直前後に出る、Windows 標準のタップによるクリック (左・右) を捨てる。
/// Raw Input は OS の処理を止められないので、マウスの低レベルフックでクリックを握りつぶす。
/// OS のクリックは、こちらが発火を判定するより先に届くことがある。そのため、対象の指の本数が触れていた直後のクリックは、
/// いったん保留する。発火したら捨て、HoldMs たっても発火しなければ、あとから送り直す (遅れるだけで、失われない)。
/// このアプリ自身が SendInput で送ったクリック (injected) は、捨ても保留もしない。
/// </summary>
public sealed class ClickGuard : IDisposable
{
    /// <summary>発火から、クリックを捨てる時間 (ms)。</summary>
    public const int WindowMs = 250;
    /// <summary>対象の本数が触れていたあと、クリックを保留する時間 (ms)。</summary>
    public const int HoldWindowMs = 350;
    /// <summary>保留したクリックを、発火がないまま送り直すまでの時間 (ms)。</summary>
    public const int HoldMs = 120;

    const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205;
    const int WH_MOUSE_LL = 14;
    const uint LLMHF_INJECTED = 0x01;

    long _untilMs = long.MinValue;
    // 捨てた/保留した押下に対応する「離す」も同じ扱いにする (押しっぱなしの状態を作らない)
    bool _leftSwallowed, _rightSwallowed;

    // 保留中のクリック (届いた順)
    readonly List<(bool Right, bool Down)> _held = new();
    long _heldSince = long.MinValue;
    long _lastFullMs = long.MinValue;
    HashSet<int> _holdFingers = new();

    IntPtr _hook;
    HookProc? _proc;

    public bool IsInstalled => _hook != IntPtr.Zero;

    /// <summary>保留したクリックを送り直す。(右ボタンか, 押すか離すか)。</summary>
    public Action<bool, bool>? Reinject { get; set; }

    /// <summary>suppressClick の割り当ての指の本数 (2 以上)。この本数が触れていた直後のクリックを保留する。</summary>
    public void SetHoldFingers(IEnumerable<int> fingers) => _holdFingers = new HashSet<int>(fingers.Where(n => n >= 2));

    /// <summary>今触れている指の本数を教える (フレームごと)。</summary>
    public void NoteContacts(int count, long nowMs)
    {
        if (_holdFingers.Contains(count)) _lastFullMs = nowMs;
    }

    /// <summary>nowMs から WindowMs の間、クリックを捨てる。保留中のクリックは捨てる。</summary>
    public void Arm(long nowMs)
    {
        _untilMs = nowMs + WindowMs;
        _held.Clear();
        _heldSince = long.MinValue;
        // 保留した押下に対する「離す」は、_leftSwallowed / _rightSwallowed が立ったままなので、届いたら捨てられる
    }

    /// <summary>保留が HoldMs を超えたクリックを送り直す。定期的に呼ぶ。</summary>
    public void Tick(long nowMs)
    {
        if (_held.Count == 0 || nowMs - _heldSince < HoldMs) return;
        var pending = _held.ToArray();
        _held.Clear();
        _heldSince = long.MinValue;
        foreach (var (right, down) in pending)
            Reinject?.Invoke(right, down);
        // 送り直した押下に対する「離す」はまだ来ていないなら、そのまま通す
        foreach (var right in new[] { false, true })
        {
            bool had = pending.Any(p => p.Right == right);
            bool upQueued = pending.Any(p => p.Right == right && !p.Down);
            if (had && !upQueued)
            {
                if (right) _rightSwallowed = false; else _leftSwallowed = false;
            }
        }
    }

    /// <summary>マウスのメッセージを捨てるか。フックから呼ぶ (単体テストできるように、フックと分けている)。</summary>
    public bool ShouldSwallow(int message, bool injected, long nowMs)
    {
        if (injected) return false;
        switch (message)
        {
            case WM_LBUTTONDOWN:
                return _leftSwallowed = Decide(false, true, nowMs);
            case WM_RBUTTONDOWN:
                return _rightSwallowed = Decide(true, true, nowMs);
            case WM_LBUTTONUP:
                return Release(false);
            case WM_RBUTTONUP:
                return Release(true);
            default:
                return false;
        }
    }

    // 押下: 発火直後なら捨てる。対象の本数が触れていた直後なら保留する (どちらも握りつぶす)
    bool Decide(bool right, bool down, long nowMs)
    {
        if (nowMs <= _untilMs) return true;
        if (_lastFullMs != long.MinValue && nowMs - _lastFullMs <= HoldWindowMs)
        {
            if (_held.Count == 0) _heldSince = nowMs;
            _held.Add((right, down));
            return true;
        }
        return false;
    }

    // 離す: 押下を握りつぶしていたなら握りつぶす。保留中なら、押下の続きとして保留に入れる
    bool Release(bool right)
    {
        ref bool swallowed = ref (right ? ref _rightSwallowed : ref _leftSwallowed);
        if (!swallowed) return false;
        swallowed = false;
        if (_held.Contains((right, true))) _held.Add((right, false));
        return true;
    }

    /// <summary>フックを入れる。UI スレッド (メッセージループのあるスレッド) から呼ぶ。入っていれば何もしない。</summary>
    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = Callback;
        using var module = Process.GetCurrentProcess().MainModule!;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(module.ModuleName), 0);
        if (_hook == IntPtr.Zero)
        {
            _proc = null;
            throw new InvalidOperationException("SetWindowsHookEx failed: " + Marshal.GetLastWin32Error());
        }
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
        // 保留中のクリックは、失わないように送り直す
        var pending = _held.ToArray();
        _held.Clear();
        foreach (var (right, down) in pending) Reinject?.Invoke(right, down);
        _leftSwallowed = _rightSwallowed = false;
    }

    public void Dispose() => Uninstall();

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (ShouldSwallow(wParam.ToInt32(), (info.flags & LLMHF_INJECTED) != 0, Environment.TickCount64))
                return (IntPtr)1;
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT
    {
        public int x, y;
        public uint mouseData, flags, time;
        public IntPtr extraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? name);
}
