using Gestures;
using Xunit;

namespace Gestures.Tests;

public class HoldTests
{
    sealed class Fake : IInputSender
    {
        public readonly List<string> Log = new();
        public bool FailOnMouseDown;
        public void KeyDown(ushort vk) => Log.Add($"down:{vk:X2}");
        public void KeyUp(ushort vk) => Log.Add($"up:{vk:X2}");
        public void Wheel(int delta, bool horizontal) => Log.Add($"wheel:{delta}");
        public void MouseButtonEvent(MouseButton button, bool down)
        {
            if (FailOnMouseDown && down) throw new InvalidOperationException("boom");
            Log.Add($"{(down ? "mousedown" : "mouseup")}:{button}");
        }
        public void MoveCursor(int dx, int dy) => Log.Add($"move:{dx},{dy}");
    }

    static Frame F(long t, params (int id, double x, double y)[] cs)
        => new(t, cs.Select(c => new Contact(c.id, c.x, c.y)).ToList());

    static Frame Three(long t, double dx = 0, double dy = 0)
        => F(t, (1, 0.2 + dx, 0.5 + dy), (2, 0.5 + dx, 0.5 + dy), (3, 0.8 + dx, 0.5 + dy));

    // ---- HoldAction 単体 ----

    [Fact]
    public void Begin_PressesKeysThenButton_AndEnd_ReleasesInReverse_WithMaskForWin()
    {
        var f = new Fake();
        var h = new HoldAction(MouseButton.Left, "Win", follow: false);
        h.Begin(Three(0), f);
        Assert.True(h.IsHeld);
        h.End(f);
        Assert.False(h.IsHeld);
        Assert.Equal(new[] { "down:5B", "mousedown:Left", "mouseup:Left", "down:E8", "up:E8", "up:5B" }, f.Log);
    }

    [Fact]
    public void Presses_ClickBeforeTheFinalHold_LikeADoubleTapAndHold()
    {
        var f = new Fake();
        var h = new HoldAction(MouseButton.Left, "Win", follow: false, speed: 1.0, presses: 2);
        h.Begin(Three(0), f);
        h.End(f);
        Assert.Equal(new[]
        {
            "down:5B",
            "mousedown:Left", "mouseup:Left",   // 1 回目: 押して離す
            "mousedown:Left",                   // 2 回目: 押したまま
            "mouseup:Left", "down:E8", "up:E8", "up:5B",
        }, f.Log);
        Assert.Equal("Win + 左ボタンを2回押して、最後を押し続ける", h.Description);
    }

    [Fact]
    public void NoMask_ForOtherKeys_OrNoKeys()
    {
        var f = new Fake();
        var h = new HoldAction(MouseButton.Right, "Ctrl+Shift");
        h.Begin(Three(0), f);
        h.End(f);
        Assert.Equal(new[] { "down:11", "down:10", "mousedown:Right", "mouseup:Right", "up:10", "up:11" }, f.Log);

        var g = new Fake();
        var bare = new HoldAction(MouseButton.Middle);
        bare.Begin(Three(0), g);
        bare.End(g);
        Assert.Equal(new[] { "mousedown:Middle", "mouseup:Middle" }, g.Log);
    }

    [Fact]
    public void BeginAndEnd_AreIdempotent()
    {
        var f = new Fake();
        var h = new HoldAction(MouseButton.Left);
        h.End(f);                       // 始めていないので何もしない
        h.Begin(Three(0), f);
        h.Begin(Three(10), f);          // 二重に押さない
        h.End(f);
        h.End(f);
        Assert.Equal(new[] { "mousedown:Left", "mouseup:Left" }, f.Log);
    }

    [Fact]
    public void Begin_Failure_ReleasesTheKeysAlreadyPressed()
    {
        var f = new Fake { FailOnMouseDown = true };
        var h = new HoldAction(MouseButton.Left, "Ctrl+Win");
        Assert.Throws<InvalidOperationException>(() => h.Begin(Three(0), f));
        Assert.False(h.IsHeld);
        Assert.Equal(new[] { "down:11", "down:5B", "up:5B", "up:11" }, f.Log);
    }

    [Fact]
    public void Follow_MovesTheCursorByTheCentroidMovement()
    {
        var f = new Fake();
        var h = new HoldAction(MouseButton.Left, null, follow: true, speed: 1.0);
        h.Begin(Three(0), f);
        Assert.True(h.Update(Three(0), f));           // 基準
        Assert.True(h.Update(Three(16, dx: 0.02, dy: -0.01), f));
        Assert.Contains("move:30,-15", f.Log);        // 0.02 * 1500 = 30, -0.01 * 1500 = -15
    }

    [Fact]
    public void Follow_Speed_Scales_AndSubPixelsAreCarried()
    {
        var f = new Fake();
        var h = new HoldAction(MouseButton.Left, null, true, 0.5);
        h.Begin(Three(0), f);
        h.Update(Three(0), f);
        // 0.001 * 1500 * 0.5 = 0.75 px: 1 回目は動かさず、2 回目で端数が合わさって 1 px
        h.Update(Three(16, dx: 0.001), f);
        Assert.DoesNotContain(f.Log, l => l.StartsWith("move"));
        h.Update(Three(32, dx: 0.002), f);
        Assert.Contains("move:1,0", f.Log);
    }

    [Fact]
    public void Follow_Off_DoesNotMove()
    {
        var f = new Fake();
        var h = new HoldAction(MouseButton.Left, null, follow: false);
        h.Begin(Three(0), f);
        h.Update(Three(0), f);
        h.Update(Three(16, dx: 0.1), f);
        Assert.DoesNotContain(f.Log, l => l.StartsWith("move"));
    }

    [Fact]
    public void Follow_FingerCountChange_DoesNotJumpTheCursor()
    {
        var f = new Fake();
        var h = new HoldAction(MouseButton.Left);
        h.Begin(Three(0), f);
        h.Update(Three(0), f);
        h.Update(F(16, (1, 0.2, 0.5), (2, 0.5, 0.5)), f);   // 1 本離れた: 重心が 0.15 飛ぶが、動かさない
        Assert.DoesNotContain(f.Log, l => l.StartsWith("move"));
    }

    [Fact]
    public void TooLong_AsksToStop()
    {
        var f = new Fake();
        var h = new HoldAction(MouseButton.Left);
        h.Begin(Three(0), f);
        Assert.True(h.Update(Three(59_000), f));
        Assert.False(h.Update(Three(61_000), f));
    }

    [Fact]
    public void InvalidArguments_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HoldAction(MouseButton.Left, null, true, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HoldAction(MouseButton.Left, null, true, 1.0, 0));
        Assert.Throws<FormatException>(() => new HoldAction(MouseButton.Left, "Nope"));
    }

    [Fact]
    public void Description() => Assert.Equal("Win + 左ボタンを押し続ける", new HoldAction(MouseButton.Left, "Win").Description);

    // ---- Dispatcher ----

    static readonly SlotSwipeSpec LeftRight = new("sw", 3, 0, SwipeDirection.Right, MinDistance: 0.10, SettleMs: 0);

    static GestureDispatcher Make(Fake f, HoldAction h)
        => new(new[] { new GestureBinding(LeftRight, h) }, f);

    // 3 本指で、左の指だけを右へ動かして発火させる。そのあと、全員を右へ動かし続ける
    static IEnumerable<Frame> Fire()
    {
        yield return F(0, (1, 0.2, 0.5), (2, 0.5, 0.5), (3, 0.8, 0.5));
        yield return F(50, (1, 0.2, 0.5), (2, 0.5, 0.5), (3, 0.8, 0.5));
        yield return F(100, (1, 0.4, 0.5), (2, 0.5, 0.5), (3, 0.8, 0.5));   // ここで発火
    }

    [Fact]
    public void Dispatcher_HoldsWhileFingersTouch_AndReleasesWhenTheyLeave()
    {
        var f = new Fake();
        var h = new HoldAction(MouseButton.Left, "Win");
        var d = Make(f, h);
        foreach (var fr in Fire()) d.Feed(fr);
        Assert.True(h.IsHeld);
        Assert.Equal(new[] { "down:5B", "mousedown:Left" }, f.Log);

        d.Feed(F(150, (1, 0.45, 0.5), (2, 0.55, 0.5), (3, 0.85, 0.5)));   // 触れたまま動く
        Assert.True(h.IsHeld);
        Assert.Contains(f.Log, l => l.StartsWith("move"));

        d.Feed(F(200));                                                     // 全員が離れた
        Assert.False(h.IsHeld);
        Assert.Equal(new[] { "mouseup:Left", "down:E8", "up:E8", "up:5B" }, f.Log.Skip(f.Log.Count - 4));
    }

    [Fact]
    public void Dispatcher_StaysHeld_WhenOnlySomeFingersLeave()
    {
        var f = new Fake();
        var h = new HoldAction(MouseButton.Left);
        var d = Make(f, h);
        foreach (var fr in Fire()) d.Feed(fr);
        d.Feed(F(150, (1, 0.4, 0.5)));          // 1 本だけ残った
        Assert.True(h.IsHeld);
    }

    [Fact]
    public void Dispatcher_DoesNotPressAgain_WhileHolding()
    {
        var f = new Fake();
        var h = new HoldAction(MouseButton.Left);
        var rep = new SlotSwipeSpec("rep", 3, 0, SwipeDirection.Right, MinDistance: 0.05, SettleMs: 0, RepeatDistance: 0.05);
        var d = new GestureDispatcher(new[] { new GestureBinding(rep, h) }, f);
        d.Feed(F(0, (1, 0.2, 0.5), (2, 0.5, 0.5), (3, 0.8, 0.5)));
        d.Feed(F(50, (1, 0.2, 0.5), (2, 0.5, 0.5), (3, 0.8, 0.5)));
        d.Feed(F(100, (1, 0.3, 0.5), (2, 0.5, 0.5), (3, 0.8, 0.5)));   // 発火 + 連続発火
        d.Feed(F(150, (1, 0.4, 0.5), (2, 0.5, 0.5), (3, 0.8, 0.5)));
        Assert.Equal(1, f.Log.Count(l => l == "mousedown:Left"));
    }

    [Fact]
    public void Dispatcher_ReleasesWhenExecutionIsTurnedOff()
    {
        var f = new Fake();
        var h = new HoldAction(MouseButton.Left);
        var d = Make(f, h);
        foreach (var fr in Fire()) d.Feed(fr);
        Assert.True(h.IsHeld);
        d.Feed(F(150, (1, 0.45, 0.5), (2, 0.5, 0.5), (3, 0.8, 0.5)), execute: false);
        Assert.False(h.IsHeld);
    }

    [Fact]
    public void Dispatcher_DoesNotStart_WhenExecutionIsOff()
    {
        var f = new Fake();
        var h = new HoldAction(MouseButton.Left);
        var d = Make(f, h);
        foreach (var fr in Fire()) d.Feed(fr, execute: false);
        Assert.False(h.IsHeld);
        Assert.Empty(f.Log);
    }

    [Fact]
    public void Dispatcher_ReleaseAll_ReleasesEverythingHeld()
    {
        var f = new Fake();
        var h = new HoldAction(MouseButton.Left, "Win");
        var d = Make(f, h);
        foreach (var fr in Fire()) d.Feed(fr);
        d.ReleaseAll();
        Assert.False(h.IsHeld);
        Assert.Equal("up:5B", f.Log[^1]);
        d.ReleaseAll();   // 二度目は何もしない
        Assert.Equal("up:5B", f.Log[^1]);
    }

    [Fact]
    public void Dispatcher_ReleasesWhenTheActionTimesOut()
    {
        var f = new Fake();
        var h = new HoldAction(MouseButton.Left);
        var d = Make(f, h);
        foreach (var fr in Fire()) d.Feed(fr);
        d.Feed(F(100 + 61_000, (1, 0.4, 0.5), (2, 0.5, 0.5), (3, 0.8, 0.5)));
        Assert.False(h.IsHeld);
    }

    // ---- JSON 設定 ----

    [Fact]
    public void Config_HoldWithCount_ClicksBeforeHolding()
    {
        var b = GestureConfig.Parse("""
            { "bindings": [ { "fingers": 3, "slot": 0, "direction": "Up",
                              "action": { "hotkey": "Win", "click": "Left", "count": 2, "hold": true } } ] }
            """);
        var f = new Fake();
        var h = (HoldAction)b[0].Action;
        h.Begin(Three(0), f);
        Assert.Equal(new[] { "down:5B", "mousedown:Left", "mouseup:Left", "mousedown:Left" }, f.Log);
        Assert.Equal("Win + 押し続ける(Left ×2)", BindingEditing.DescribeAction(GestureConfig.ParseEntries("""
            { "bindings": [ { "action": { "hotkey": "Win", "click": "Left", "count": 2, "hold": true } } ] }
            """)[0]));
    }

    [Fact]
    public void Config_ParsesHold()
    {
        var b = GestureConfig.Parse("""
            { "bindings": [
              { "fingers": 3, "slot": 0, "direction": "Up", "action": { "hotkey": "Win", "click": "Left", "hold": true } },
              { "fingers": 3, "slot": 1, "direction": "Up", "action": { "click": "Right", "hold": true, "follow": false, "speed": 2 } },
              { "fingers": 3, "slot": 2, "direction": "Up", "action": { "click": "Left", "hold": false } } ] }
            """);
        Assert.IsType<HoldAction>(b[0].Action);
        Assert.Equal("Win + 左ボタンを押し続ける", b[0].Action.Description);
        Assert.IsType<HoldAction>(b[1].Action);
        Assert.IsType<ClickAction>(b[2].Action);   // hold: false は普通のクリック
    }

    [Theory]
    [InlineData("""{ "hotkey": "A", "hold": true }""", "hold は click と一緒にだけ")]
    [InlineData("""{ "click": "Left", "hold": true, "count": 0 }""", "count は 1 〜 5")]
    [InlineData("""{ "click": "Left", "follow": false }""", "follow / speed は hold と一緒にだけ")]
    [InlineData("""{ "click": "Left", "speed": 2 }""", "follow / speed は hold と一緒にだけ")]
    [InlineData("""{ "click": "Left", "hold": true, "speed": 0 }""", "speed は 0 より大きい")]
    public void Config_BadHold_IsReported(string action, string expected)
    {
        var json = "{ \"bindings\": [ { \"fingers\": 3, \"slot\": 0, \"direction\": \"Left\", \"action\": " + action + " } ] }";
        var ex = Assert.Throws<GestureConfigException>(() => GestureConfig.Parse(json));
        Assert.Contains(expected, string.Join("\n", ex.Errors));
    }

    [Fact]
    public void Editing_DescribeAction_AndJsonRoundTrip()
    {
        var entries = GestureConfig.ParseEntries("""
            { "bindings": [ { "fingers": 3, "slot": 0, "direction": "Left",
                              "action": { "hotkey": "Win", "click": "Left", "hold": true, "follow": false, "speed": 1.5 } } ] }
            """);
        Assert.Equal("Win + 押し続ける(Left)", BindingEditing.DescribeAction(entries[0]));
        var a = GestureConfig.ParseEntries(GestureConfig.ToJson(entries))[0].Action!;
        Assert.Equal((true, false, 1.5), (a.Hold, a.Follow, a.Speed));
        var c = entries[0].Clone().Action!;
        Assert.Equal((true, false, 1.5), (c.Hold, c.Follow, c.Speed));
    }
}
