using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Gestures;

/// <summary>Win32 SendInput によるキー・ホイール入力の送信。</summary>
[SupportedOSPlatform("windows")]
public sealed class Win32InputSender : IInputSender
{
    const uint INPUT_MOUSE = 0;
    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
    const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
    const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
    const uint MOUSEEVENTF_WHEEL = 0x0800;
    const uint MOUSEEVENTF_HWHEEL = 0x1000;

    // テンキーと区別が必要な拡張キー
    static readonly HashSet<ushort> ExtendedKeys = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // PgUp PgDn End Home ← ↑ → ↓
        0x2D, 0x2E, 0x2C,                               // Insert Delete PrintScreen
        0x5B, 0x5C,                                     // Win
        0xA3, 0xA5,                                     // 右 Ctrl / 右 Alt
    };

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder sb, int max);
    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    const uint GA_ROOT = 2;
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_ASYNCWINDOWPOS = 0x4000;

    // つかんだときの位置と大きさ。以降は、ここからの合計の変化量で位置・大きさを決める
    // (ウィンドウの現在の大きさを毎回読むと、非同期に反映する間に、読んだ値が古くなってずれる)
    RECT _grabRect;
    int _accX, _accY;

    public long GrabWindow()
    {
        if (!GetCursorPos(out var pt)) return 0;
        var h = WindowFromPoint(pt);
        if (h == IntPtr.Zero) return 0;
        h = GetAncestor(h, GA_ROOT);
        if (h == IntPtr.Zero || IsZoomed(h)) return 0;     // 最大化中のウィンドウは動かせない
        var sb = new System.Text.StringBuilder(64);
        GetClassName(h, sb, sb.Capacity);
        // デスクトップ・タスクバーは動かさない
        if (sb.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return 0;
        if (!GetWindowRect(h, out _grabRect)) return 0;
        _accX = _accY = 0;
        return h.ToInt64();
    }

    public void MoveWindowBy(long window, int dx, int dy)
    {
        var h = new IntPtr(window);
        if (!IsWindow(h)) return;
        _accX += dx;
        _accY += dy;
        SetWindowPos(h, IntPtr.Zero, _grabRect.Left + _accX, _grabRect.Top + _accY, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
    }

    public void ResizeWindowBy(long window, int dw, int dh)
    {
        var h = new IntPtr(window);
        if (!IsWindow(h)) return;
        _accX += dw;
        _accY += dh;
        SetWindowPos(h, IntPtr.Zero, 0, 0, Math.Max(1, _grabRect.Right - _grabRect.Left + _accX), Math.Max(1, _grabRect.Bottom - _grabRect.Top + _accY),
            SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
    }

    public void KeyDown(ushort vk) => SendKey(vk, false);

    public void KeyUp(ushort vk) => SendKey(vk, true);

    public void Wheel(int delta, bool horizontal)
    {
        var input = new INPUT { type = INPUT_MOUSE };
        input.u.mi.mouseData = unchecked((uint)delta);
        input.u.mi.dwFlags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL;
        Send(input);
    }

    const uint MOUSEEVENTF_MOVE = 0x0001;

    public void MouseButtonEvent(MouseButton button, bool down)
    {
        uint flag = (button, down) switch
        {
            (MouseButton.Left, true) => MOUSEEVENTF_LEFTDOWN,
            (MouseButton.Left, false) => MOUSEEVENTF_LEFTUP,
            (MouseButton.Right, true) => MOUSEEVENTF_RIGHTDOWN,
            (MouseButton.Right, false) => MOUSEEVENTF_RIGHTUP,
            (_, true) => MOUSEEVENTF_MIDDLEDOWN,
            _ => MOUSEEVENTF_MIDDLEUP,
        };
        Send(new INPUT { type = INPUT_MOUSE, u = { mi = { dwFlags = flag } } });
    }

    public void MoveCursor(int dx, int dy)
        => Send(new INPUT { type = INPUT_MOUSE, u = { mi = { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE } } });

    public void Click(MouseButton button, int count)
    {
        var (down, up) = button switch
        {
            MouseButton.Left => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
            MouseButton.Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            _ => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
        };
        for (int i = 0; i < count; i++)
        {
            Send(new INPUT { type = INPUT_MOUSE, u = { mi = { dwFlags = down } } });
            Send(new INPUT { type = INPUT_MOUSE, u = { mi = { dwFlags = up } } });
        }
    }

    void SendKey(ushort vk, bool up)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.u.ki.wVk = vk;
        input.u.ki.dwFlags = (ExtendedKeys.Contains(vk) ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0);
        Send(input);
    }

    static void Send(INPUT input)
    {
        if (SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>()) != 1)
            throw new InvalidOperationException("SendInput failed: " + Marshal.GetLastWin32Error());
    }

    public static int InputSize => Marshal.SizeOf<INPUT>();
}
