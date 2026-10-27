using System.Runtime.Versioning;
using Gestures;
using Microsoft.Win32;
using Xunit;

namespace Gestures.Tests;

/// <summary>本物の Run キーには触れず、テスト専用の一時キーで検証する。</summary>
[SupportedOSPlatform("windows")]
public sealed class AutoStartTests : IDisposable
{
    readonly string _root = @"Software\TouchpadGesturesTests\" + Guid.NewGuid().ToString("N");
    readonly AutoStart _auto;

    public AutoStartTests() => _auto = new AutoStart("TestEntry", _root + @"\Run");

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
        // 親も空なら片付ける
        using var parent = Registry.CurrentUser.OpenSubKey(@"Software\TouchpadGesturesTests");
        if (parent is { SubKeyCount: 0, ValueCount: 0 })
            Registry.CurrentUser.DeleteSubKey(@"Software\TouchpadGesturesTests", throwOnMissingSubKey: false);
    }

    [Fact]
    public void InitiallyNotEnabled()
    {
        Assert.Null(_auto.Command);
        Assert.False(_auto.IsEnabledFor(@"C:\App\TouchpadViewer.exe"));
    }

    [Fact]
    public void Enable_WritesQuotedCommandWithArgs()
    {
        _auto.Enable(@"C:\Program Files\App\TouchpadViewer.exe", "--tray");
        Assert.Equal("\"C:\\Program Files\\App\\TouchpadViewer.exe\" --tray", _auto.Command);
        Assert.True(_auto.IsEnabledFor(@"C:\Program Files\App\TouchpadViewer.exe"));
    }

    [Fact]
    public void IsEnabledFor_IgnoresCase_ButNotAnotherPath()
    {
        _auto.Enable(@"C:\App\TouchpadViewer.exe", "--tray");
        Assert.True(_auto.IsEnabledFor(@"c:\app\touchpadviewer.exe"));
        Assert.False(_auto.IsEnabledFor(@"D:\Moved\TouchpadViewer.exe"));
    }

    [Fact]
    public void Enable_Twice_OverwritesWithTheLatestPath()
    {
        _auto.Enable(@"C:\Old\TouchpadViewer.exe", "--tray");
        _auto.Enable(@"C:\New\TouchpadViewer.exe", "--tray");
        Assert.False(_auto.IsEnabledFor(@"C:\Old\TouchpadViewer.exe"));
        Assert.True(_auto.IsEnabledFor(@"C:\New\TouchpadViewer.exe"));
    }

    [Fact]
    public void Disable_RemovesTheEntry_AndIsSafeWhenMissing()
    {
        _auto.Disable(); // 未登録でも例外にならない
        _auto.Enable(@"C:\App\TouchpadViewer.exe");
        _auto.Disable();
        Assert.Null(_auto.Command);
    }

    [Fact]
    public void BuildCommand_OmitsTrailingSpaceWhenNoArgs()
        => Assert.Equal("\"C:\\a b\\x.exe\"", AutoStart.BuildCommand(@"C:\a b\x.exe", ""));
}
