using System.Runtime.InteropServices;

namespace Gestures;

/// <summary>キーボードの修飾キー。ジェスチャーの条件 (「Ctrl を押しながら」など) に使う。</summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>修飾キーの名前 ("Ctrl+Shift" など) の読み書き。</summary>
public static class KeyModifierNames
{
    /// <summary>"Ctrl+Shift" → Ctrl | Shift。"none" は「どの修飾キーも押していない」。</summary>
    /// <exception cref="FormatException">空、または知らない名前のとき。</exception>
    public static KeyModifiers Parse(string text)
    {
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var result = KeyModifiers.None;
        bool sawNone = false;
        foreach (var p in parts)
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl": case "control": result |= KeyModifiers.Ctrl; break;
                case "alt": result |= KeyModifiers.Alt; break;
                case "shift": result |= KeyModifiers.Shift; break;
                case "win": case "windows": result |= KeyModifiers.Win; break;
                case "none": sawNone = true; break;
                default: throw new FormatException($"Unknown modifier: '{p}' (Ctrl / Alt / Shift / Win / none)");
            }
        }
        if (sawNone && (result != KeyModifiers.None || parts.Length > 1))
            throw new FormatException("'none' cannot be combined with other modifiers");
        return result;
    }

    /// <summary>空・null → null (どの状態でも有効)。それ以外は <see cref="Parse"/>。</summary>
    /// <exception cref="FormatException">知らない名前のとき。</exception>
    public static KeyModifiers? ParseOrNull(string? text)
        => string.IsNullOrWhiteSpace(text) ? null : Parse(text);

    /// <summary>Ctrl+Alt+Shift+Win の順に並べる。何も押さないときは "none"。</summary>
    public static string Format(KeyModifiers m)
    {
        if (m == KeyModifiers.None) return "none";
        var parts = new List<string>();
        if (m.HasFlag(KeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (m.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (m.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (m.HasFlag(KeyModifiers.Win)) parts.Add("Win");
        return string.Join("+", parts);
    }
}

/// <summary>実際のキーボードで、今押されている修飾キー。</summary>
public static class KeyboardState
{
    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    public static KeyModifiers GetModifiers()
    {
        var m = KeyModifiers.None;
        if (Down(VK_CONTROL)) m |= KeyModifiers.Ctrl;
        if (Down(VK_MENU)) m |= KeyModifiers.Alt;
        if (Down(VK_SHIFT)) m |= KeyModifiers.Shift;
        if (Down(VK_LWIN) || Down(VK_RWIN)) m |= KeyModifiers.Win;
        return m;
    }

    // 左右を区別した修飾キー (Ctrl / Shift / Alt) と Win。離す・押し直すときは、実際に押されている側を操作する
    static readonly ushort[] SideKeys = { 0xA2, 0xA3, 0xA0, 0xA1, 0xA4, 0xA5, 0x5B, 0x5C };

    /// <summary>今押されている修飾キーを、左右を区別した仮想キーコードで返す。</summary>
    public static ushort[] GetHeldModifierKeys()
        => SideKeys.Where(k => Down(k)).ToArray();

    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);
}
