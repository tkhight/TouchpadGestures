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
    const uint MOUSEEVENTF_WHEEL = 0x0800;
    const uint MOUSEEVENTF_HWHEEL = 0x1000;

    // テンキーと区別が必要な拡張キー
    static readonly HashSet<ushort> ExtendedKeys = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // PgUp PgDn End Home ← ↑ → ↓
        0x2D, 0x2E, 0x2C,                               // Insert Delete PrintScreen
        0x5B, 0x5C,                                     // Win
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

    public void KeyDown(ushort vk) => SendKey(vk, false);

    public void KeyUp(ushort vk) => SendKey(vk, true);

    public void Wheel(int delta, bool horizontal)
    {
        var input = new INPUT { type = INPUT_MOUSE };
        input.u.mi.mouseData = unchecked((uint)delta);
        input.u.mi.dwFlags = horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL;
        Send(input);
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
