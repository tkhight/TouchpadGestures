using Gestures;
using Xunit;

namespace Gestures.Tests;

public class AppTests
{
    static readonly SlotSwipeSpec LeftRight = new("left-right", 3, 0, SwipeDirection.Right, MinDistance: 0.10, SettleMs: 0);

    static Frame F(long t, double leftX) => new(t, new List<Contact>
    {
        new(1, leftX, 0.5), new(2, 0.5, 0.5), new(3, 0.8, 0.5),
    });

    // 3本指で左の指だけ右へ動かし、発火させる
    static void Swipe(GestureDispatcher d)
    {
        d.Feed(F(0, 0.2));
        d.Feed(F(50, 0.2));
        d.Feed(F(100, 0.4));
    }

    sealed class Fake : IInputSender
    {
        public readonly List<string> Log = new();
        public void KeyDown(ushort vk) => Log.Add($"down:{vk:X2}");
        public void KeyUp(ushort vk) => Log.Add($"up:{vk:X2}");
        public void Wheel(int delta, bool horizontal) => Log.Add($"wheel:{delta}");
    }

    static GestureDispatcher Make(Fake f, string? foreground, params GestureBinding[] bindings)
        => new(bindings, f, () => foreground);

    [Theory]
    [InlineData("chrome", "chrome")]
    [InlineData("Chrome.EXE", "chrome")]
    [InlineData("  Code.exe ", "code")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData(".exe", ".exe")]
    public void Normalize(string? input, string? expected) => Assert.Equal(expected, AppNames.Normalize(input));

    [Fact]
    public void AppBinding_FiresOnlyInThatApp()
    {
        var f = new Fake();
        var d = Make(f, "chrome", new GestureBinding(LeftRight, new HotkeyAction("A"), "chrome"));
        Swipe(d);
        Assert.Equal(new[] { "down:41", "up:41" }, f.Log);

        var f2 = new Fake();
        Swipe(Make(f2, "notepad", new GestureBinding(LeftRight, new HotkeyAction("A"), "chrome")));
        Assert.Empty(f2.Log);
    }

    [Fact]
    public void AppBinding_MatchesRegardlessOfCaseAndExe()
    {
        var f = new Fake();
        Swipe(Make(f, "Chrome", new GestureBinding(LeftRight, new HotkeyAction("A"), "CHROME.exe")));
        Assert.NotEmpty(f.Log);
    }

    [Fact]
    public void GlobalBinding_FiresInAnyApp_AndWhenAppIsUnknown()
    {
        foreach (var fg in new string?[] { "chrome", "notepad", null })
        {
            var f = new Fake();
            Swipe(Make(f, fg, new GestureBinding(LeftRight, new HotkeyAction("A"))));
            Assert.Equal(2, f.Log.Count);
        }
    }

    [Fact]
    public void AppBinding_OverridesGlobal_ForTheSameGesture()
    {
        GestureBinding[] bs =
        {
            new(LeftRight, new HotkeyAction("A")),
            new(LeftRight, new HotkeyAction("B"), "chrome"),
        };

        var inChrome = new Fake();
        Swipe(Make(inChrome, "chrome", bs));
        Assert.Equal(new[] { "down:42", "up:42" }, inChrome.Log); // B だけ

        var elsewhere = new Fake();
        Swipe(Make(elsewhere, "notepad", bs));
        Assert.Equal(new[] { "down:41", "up:41" }, elsewhere.Log); // A だけ
    }

    [Fact]
    public void LastApp_IsReported_OnFire()
    {
        var f = new Fake();
        var d = Make(f, "Chrome.exe", new GestureBinding(LeftRight, new HotkeyAction("A")));
        d.Feed(F(0, 0.2));
        Assert.Null(d.LastApp);
        d.Feed(F(50, 0.2));
        d.Feed(F(100, 0.4));
        Assert.Equal("chrome", d.LastApp);
    }

    [Fact]
    public void ForegroundApp_IsOnlyLookedUp_WhenSomethingFires()
    {
        int calls = 0;
        var d = new GestureDispatcher(new[] { new GestureBinding(LeftRight, new HotkeyAction("A")) }, new Fake(),
            () => { calls++; return "x"; });
        d.Feed(F(0, 0.2));
        d.Feed(F(50, 0.2));
        Assert.Equal(0, calls);
        d.Feed(F(100, 0.4));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void ExecuteFalse_StillReportsFired_ForTheActiveBinding()
    {
        var f = new Fake();
        var d = Make(f, "chrome", new GestureBinding(LeftRight, new HotkeyAction("A"), "firefox"));
        d.Feed(F(0, 0.2)); d.Feed(F(50, 0.2));
        Assert.Empty(d.Feed(F(100, 0.4), execute: false));

        var d2 = Make(f, "firefox", new GestureBinding(LeftRight, new HotkeyAction("A"), "firefox"));
        d2.Feed(F(0, 0.2)); d2.Feed(F(50, 0.2));
        Assert.Single(d2.Feed(F(100, 0.4), execute: false));
        Assert.Empty(f.Log);
    }

    [Fact]
    public void SameGestureInDifferentApps_IsAllowed_ButSameAppTwiceIsNot()
    {
        var f = new Fake();
        _ = Make(f, null,
            new GestureBinding(LeftRight, new HotkeyAction("A"), "chrome"),
            new GestureBinding(LeftRight, new HotkeyAction("B"), "code"),
            new GestureBinding(LeftRight, new HotkeyAction("C")));

        Assert.Throws<ArgumentException>(() => Make(f, null,
            new GestureBinding(LeftRight, new HotkeyAction("A"), "chrome"),
            new GestureBinding(LeftRight, new HotkeyAction("B"), "Chrome.exe")));
    }

    // ---- JSON 設定 ----

    [Fact]
    public void Config_ParsesApp()
    {
        var b = GestureConfig.Parse("""
            { "bindings": [
              { "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "A" } },
              { "app": "Chrome.exe", "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "B" } },
              { "app": "code", "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "C" } } ] }
            """);
        Assert.Null(b[0].App);
        Assert.Equal("chrome", b[1].App);
        Assert.Equal("code", b[2].App);
    }

    [Fact]
    public void Config_SameGestureTwiceInTheSameApp_IsReported()
    {
        var ex = Assert.Throws<GestureConfigException>(() => GestureConfig.Parse("""
            { "bindings": [
              { "app": "chrome", "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "A" } },
              { "app": "CHROME.EXE", "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "B" } } ] }
            """));
        Assert.Contains("重複", ex.Errors.Single());
    }

    [Fact]
    public void Config_EmptyApp_IsReported()
    {
        var ex = Assert.Throws<GestureConfigException>(() => GestureConfig.Parse("""
            { "bindings": [
              { "app": "  ", "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "A" } } ] }
            """));
        Assert.Contains("app が空", ex.Errors.Single());
    }

    [Fact]
    public void Config_AppWorksForTapAndPinch()
    {
        var b = GestureConfig.Parse("""
            { "bindings": [
              { "app": "chrome", "fingers": 3, "tapSlot": 0, "action": { "hotkey": "A" } },
              { "app": "chrome", "fingers": 4, "pinch": "Out", "action": { "wheel": 1 } } ] }
            """);
        Assert.All(b, x => Assert.Equal("chrome", x.App));
    }

    // ---- 無効 (enabled: false) ----

    [Fact]
    public void Disabled_IsNotBuilt()
    {
        var b = GestureConfig.Parse("""
            { "bindings": [
              { "fingers": 3, "slot": 0, "direction": "Left", "enabled": false, "action": { "hotkey": "A" } },
              { "fingers": 3, "slot": 1, "direction": "Left", "enabled": true, "action": { "hotkey": "B" } },
              { "fingers": 3, "slot": 2, "direction": "Left", "action": { "hotkey": "C" } } ] }
            """);
        Assert.Equal(new[] { "B", "C" }, b.Select(x => x.Action.Description));
    }

    [Fact]
    public void Disabled_DoesNotConflictWithTheSameGesture()
    {
        var b = GestureConfig.Parse("""
            { "bindings": [
              { "fingers": 3, "slot": 0, "direction": "Left", "enabled": false, "action": { "hotkey": "A" } },
              { "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "B" } } ] }
            """);
        Assert.Equal("B", Assert.Single(b).Action.Description);
    }

    [Fact]
    public void Disabled_AppBinding_DoesNotOverrideTheGlobalOne()
    {
        var b = GestureConfig.Parse("""
            { "bindings": [
              { "fingers": 3, "slot": 0, "direction": "Right", "minDistance": 0.10, "settleMs": 0, "action": { "hotkey": "A" } },
              { "app": "chrome", "enabled": false, "fingers": 3, "slot": 0, "direction": "Right", "minDistance": 0.10, "settleMs": 0,
                "action": { "hotkey": "B" } } ] }
            """);
        var f = new Fake();
        Swipe(new GestureDispatcher(b, f, () => "chrome"));
        Assert.Equal(new[] { "down:41", "up:41" }, f.Log);
    }

    [Fact]
    public void Disabled_StillReportsContentErrors()
    {
        var ex = Assert.Throws<GestureConfigException>(() => GestureConfig.Parse("""
            { "bindings": [ { "fingers": 3, "slot": 0, "direction": "Left", "enabled": false, "action": { "hotkey": "Nope" } } ] }
            """));
        Assert.Contains("Nope", ex.Errors.Single());
    }

    [Fact]
    public void Enabled_RoundTripsThroughJson_AndDisplayNameWorksWhenDisabled()
    {
        var entries = GestureConfig.ParseEntries("""
            { "bindings": [ { "fingers": 3, "slot": 0, "direction": "Left", "enabled": false, "action": { "hotkey": "A" } } ] }
            """);
        var json = GestureConfig.ToJson(entries);
        Assert.Contains("\"enabled\":false", json);
        Assert.Equal(false, GestureConfig.ParseEntries(json)[0].Enabled);
        Assert.Equal("3本指・左から1番目だけ←", BindingEditing.DisplayName(entries[0]));
        Assert.Equal(false, entries[0].Clone().Enabled);
    }
}
