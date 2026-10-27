using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Gestures;

/// <summary>
/// 「Windows にサインインしたとき自動で起動する」設定 (HKCU の Run キー) の読み書き。
/// 現在のユーザー分だけを扱い、管理者権限は要らない。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AutoStart
{
    public const string DefaultRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    readonly string _valueName;
    readonly string _keyPath;

    /// <param name="keyPath">テストでは本物の Run キーを触らないよう、別のキーを渡す。</param>
    public AutoStart(string valueName = "TouchpadGestures", string keyPath = DefaultRunKey)
    {
        _valueName = valueName;
        _keyPath = keyPath;
    }

    /// <summary>登録されているコマンドライン。未登録なら null。</summary>
    public string? Command
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
            return key?.GetValue(_valueName) as string;
        }
    }

    /// <summary>この exe が登録されているか。exe を移動した後の古い登録は false になる。</summary>
    public bool IsEnabledFor(string exePath)
        => Command is { } c && c.StartsWith("\"" + exePath + "\"", StringComparison.OrdinalIgnoreCase);

    /// <summary>スペースを含むパスでも壊れないよう、exe を引用符で囲む。</summary>
    public static string BuildCommand(string exePath, string args)
        => args.Length == 0 ? $"\"{exePath}\"" : $"\"{exePath}\" {args}";

    public void Enable(string exePath, string args = "")
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath);
        key.SetValue(_valueName, BuildCommand(exePath, args));
    }

    /// <summary>未登録でも例外にならない。</summary>
    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
        key?.DeleteValue(_valueName, throwOnMissingValue: false);
    }
}
