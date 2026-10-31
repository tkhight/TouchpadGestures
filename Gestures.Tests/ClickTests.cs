using Gestures;
using Xunit;

namespace Gestures.Tests;

public class ClickTests
{
    sealed class Fake : IInputSender
    {
        public readonly List<string> Log = new();
        public bool FailOnClick;
        public void KeyDown(ushort vk) => Log.Add($"down:{vk:X2}");
        public void KeyUp(ushort vk) => Log.Add($"up:{vk:X2}");
        public void Wheel(int delta, bool horizontal) => Log.Add($"wheel:{delta}");
        public void Click(MouseButton button, int count)
        {
            if (FailOnClick) throw new InvalidOperationException("boom");
            Log.Add($"click:{button}:{count}");
        }
    }

    [Fact]
    public void Click_Alone()
    {
        var f = new Fake();
        new ClickAction(MouseButton.Left).Execute(f);
        Assert.Equal(new[] { "click:Left:1" }, f.Log);
    }

    [Fact]
    public void Click_WhileHoldingAKey_PressesBeforeAndReleasesAfter()
    {
        var f = new Fake();
        new ClickAction(MouseButton.Left, 1, "Win").Execute(f);
        Assert.Equal(new[] { "down:5B", "click:Left:1", "down:E8", "up:E8", "up:5B" }, f.Log); // E8 = スタートメニューを開かせないための印
    }

    [Fact]
    public void Click_WithSeveralHeldKeys_ReleasesInReverse()
    {
        var f = new Fake();
        new ClickAction(MouseButton.Right, 2, "Ctrl+Shift").Execute(f);
        Assert.Equal(new[] { "down:11", "down:10", "click:Right:2", "up:10", "up:11" }, f.Log);
    }

    [Theory]
    [InlineData("Alt", "down:12", "up:12")]
    [InlineData("Ctrl+Win", "down:11", "up:11")]
    public void Click_MasksWinAndAlt_ButNotOtherKeys(string chord, string firstDown, string lastUp)
    {
        var f = new Fake();
        new ClickAction(MouseButton.Left, 1, chord).Execute(f);
        Assert.Contains("down:E8", f.Log);
        Assert.True(f.Log.IndexOf("down:E8") > f.Log.IndexOf("click:Left:1"));
        Assert.True(f.Log.IndexOf("up:E8") < f.Log.LastIndexOf(lastUp));
    }

    [Fact]
    public void Click_WithoutWinOrAlt_DoesNotSendTheMaskKey()
    {
        var f = new Fake();
        new ClickAction(MouseButton.Left, 1, "Ctrl+Shift").Execute(f);
        Assert.DoesNotContain("down:E8", f.Log);
    }

    [Fact]
    public void Click_ReleasesHeldKeys_EvenWhenTheClickFails()
    {
        var f = new Fake { FailOnClick = true };
        Assert.Throws<InvalidOperationException>(() => new ClickAction(MouseButton.Left, 1, "Win").Execute(f));
        Assert.Equal(new[] { "down:5B", "up:5B" }, f.Log);
    }

    [Fact]
    public void Description()
    {
        Assert.Equal("Win + 左クリック", new ClickAction(MouseButton.Left, 1, "Win").Description);
        Assert.Equal("ダブル右クリック", new ClickAction(MouseButton.Right, 2).Description);
        Assert.Equal("3回中クリック", new ClickAction(MouseButton.Middle, 3).Description);
    }

    [Fact]
    public void Invalid_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClickAction(MouseButton.Left, 0));
        Assert.Throws<FormatException>(() => new ClickAction(MouseButton.Left, 1, "Nope"));
    }

    [Fact]
    public void SendersWithoutClickSupport_Fail_InsteadOfSilentlyDoingNothing()
    {
        IInputSender s = new NoClick();
        Assert.Throws<NotSupportedException>(() => s.Click(MouseButton.Left, 1));
    }

    sealed class NoClick : IInputSender
    {
        public void KeyDown(ushort vk) { }
        public void KeyUp(ushort vk) { }
        public void Wheel(int delta, bool horizontal) { }
    }

    // ---- JSON 設定 ----

    [Fact]
    public void Config_ParsesClickActions()
    {
        var b = GestureConfig.Parse("""
            { "bindings": [
              { "fingers": 3, "slot": 0, "direction": "Left", "action": { "click": "Left" } },
              { "fingers": 3, "slot": 1, "direction": "Left", "action": { "hotkey": "Win", "click": "left" } },
              { "fingers": 3, "slot": 2, "direction": "Left", "action": { "click": "Right", "count": 2 } } ] }
            """);
        var f = new Fake();
        foreach (var x in b) x.Action.Execute(f);
        Assert.Equal(new[] { "click:Left:1", "down:5B", "click:Left:1", "down:E8", "up:E8", "up:5B", "click:Right:2" }, f.Log);
    }

    [Theory]
    [InlineData("""{ "click": "Up" }""", "click は Left / Right / Middle")]
    [InlineData("""{ "click": "Left", "count": 0 }""", "count は 1 〜 5")]
    [InlineData("""{ "click": "Left", "count": 9 }""", "count は 1 〜 5")]
    [InlineData("""{ "hotkey": "A", "count": 2 }""", "count は click と一緒にだけ")]
    [InlineData("""{ "wheel": 1, "click": "Left" }""", "一緒に指定できません")]
    [InlineData("""{ "click": "Left", "horizontal": true }""", "horizontal は wheel と一緒にだけ")]
    [InlineData("""{ "click": "Left", "hotkey": "Nope" }""", "Unknown key name")]
    [InlineData("""{ }""", "hotkey か wheel か click")]
    public void Config_BadAction_IsReported(string action, string expected)
    {
        var json = "{ \"bindings\": [ { \"fingers\": 3, \"slot\": 0, \"direction\": \"Left\", \"action\": " + action + " } ] }";
        var ex = Assert.Throws<GestureConfigException>(() => GestureConfig.Parse(json));
        Assert.Contains(expected, string.Join("\n", ex.Errors));
    }

    [Fact]
    public void Editing_DescribeAction_AndJsonRoundTrip()
    {
        var entries = GestureConfig.ParseEntries("""
            { "bindings": [ { "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "Win", "click": "Left", "count": 2 } } ] }
            """);
        Assert.Equal("Win + クリック(Left ×2)", BindingEditing.DescribeAction(entries[0]));
        var again = GestureConfig.ParseEntries(GestureConfig.ToJson(entries))[0].Action!;
        Assert.Equal(("Win", "Left", 2), (again.Hotkey, again.Click, again.Count));
        Assert.Equal(2, entries[0].Clone().Action!.Count);
    }
}
