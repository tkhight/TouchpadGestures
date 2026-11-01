using Gestures;
using Xunit;

namespace Gestures.Tests;

public class WindowMoveTests
{
    sealed class Fake : IInputSender
    {
        public readonly List<string> Log = new();
        public long Window = 42;
        public void KeyDown(ushort vk) { }
        public void KeyUp(ushort vk) { }
        public void Wheel(int delta, bool horizontal) { }
        public void MoveCursor(int dx, int dy) => Log.Add($"cursor:{dx},{dy}");
        public long GrabWindow() { Log.Add("grab"); return Window; }
        public void MoveWindowBy(long window, int dx, int dy) => Log.Add($"win{window}:{dx},{dy}");
        public void ResizeWindowBy(long window, int dw, int dh) => Log.Add($"size{window}:{dw},{dh}");
    }

    static Frame Three(long t, double dx = 0, double dy = 0)
        => new(t, new[] { new Contact(1, 0.2 + dx, 0.5 + dy), new Contact(2, 0.5 + dx, 0.5 + dy), new Contact(3, 0.8 + dx, 0.5 + dy) });

    [Fact]
    public void GrabsAtTheStart_ThenMovesWindowAndCursorByTheCentroidMovement()
    {
        var f = new Fake();
        var a = new WindowMoveAction();
        a.Begin(Three(0), f);
        a.Update(Three(0), f);                  // 基準になるフレーム
        a.Update(Three(10, 0.01, -0.02), f);    // 1500px × (0.01, -0.02) = (15, -30)
        Assert.Equal(new[] { "grab", "win42:15,-30", "cursor:15,-30" }, f.Log);
    }

    [Fact]
    public void Speed_Scales_AndSubPixelsAreCarried()
    {
        var f = new Fake();
        var a = new WindowMoveAction(0.5);
        a.Begin(Three(0), f);
        a.Update(Three(0), f);
        a.Update(Three(10, 0.001), f);          // 0.75px: まだ動かさない
        a.Update(Three(20, 0.002), f);          // 合計 1.5px → 1px
        Assert.Equal(new[] { "grab", "win42:1,0", "cursor:1,0" }, f.Log);
    }

    [Fact]
    public void NothingToGrab_DoesNothing()
    {
        var f = new Fake { Window = 0 };
        var a = new WindowMoveAction();
        a.Begin(Three(0), f);
        a.Update(Three(0), f);
        a.Update(Three(10, 0.05, 0.05), f);
        Assert.Equal(new[] { "grab" }, f.Log);
    }

    [Fact]
    public void FingerCountChange_DoesNotJump()
    {
        var f = new Fake();
        var a = new WindowMoveAction();
        a.Begin(Three(0), f);
        a.Update(Three(0), f);
        a.Update(new Frame(10, new[] { new Contact(1, 0.9, 0.9), new Contact(2, 0.95, 0.9) }), f);
        Assert.Equal(new[] { "grab" }, f.Log);
    }

    [Fact]
    public void End_StopsMoving_AndLimitEndsTheHold()
    {
        var f = new Fake();
        var a = new WindowMoveAction();
        a.Begin(Three(0), f);
        Assert.True(a.Update(Three(1000), f));
        Assert.False(a.Update(Three(61_000), f));
        a.End(f);
        Assert.False(a.IsHeld);
        Assert.False(a.Update(Three(62_000, 0.1), f));
    }

    [Fact]
    public void Resize_ChangesTheSizeInsteadOfMoving()
    {
        var f = new Fake();
        var a = new WindowMoveAction(1.0, resize: true);
        a.Begin(Three(0), f);
        a.Update(Three(0), f);
        a.Update(Three(10, 0.01, 0.02), f);
        Assert.Equal(new[] { "grab", "size42:15,30", "cursor:15,30" }, f.Log);
    }

    [Fact]
    public void Config_ResizeWindow()
    {
        var json = "{ \"bindings\": [ { \"fingers\": 3, \"slot\": 1, \"direction\": \"Up\", \"action\": { \"resizeWindow\": true, \"speed\": 2 } } ] }";
        Assert.IsType<WindowMoveAction>(GestureConfig.Parse(json)[0].Action);
        var e = GestureConfig.ParseEntries(json);
        Assert.Equal("ウィンドウの大きさを変える", BindingEditing.DescribeAction(e[0]));
        Assert.True(GestureConfig.ParseEntries(GestureConfig.ToJson(e))[0].Action!.ResizeWindow);
        Assert.Throws<GestureConfigException>(() => GestureConfig.Parse(json.Replace("\"speed\": 2", "\"moveWindow\": true")));
        Assert.Throws<GestureConfigException>(() => GestureConfig.Parse(json.Replace("\"speed\": 2", "\"hotkey\": \"A\"")));
        Assert.Throws<GestureConfigException>(() => GestureConfig.Parse(json.Replace("true", "false")));
    }

    [Fact]
    public void BadSpeed_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new WindowMoveAction(0));

    // ---- 設定 ----

    const string Json = """
        { "bindings": [
          { "fingers": 3, "slot": 1, "direction": "Up", "action": { "moveWindow": true } },
          { "fingers": 4, "slot": 1, "direction": "Up", "action": { "moveWindow": true, "speed": 1.5 } }
        ] }
        """;

    [Fact]
    public void Config_ParsesMoveWindow_AndDescribesIt()
    {
        var b = GestureConfig.Parse(Json);
        Assert.IsType<WindowMoveAction>(b[0].Action);
        var e = GestureConfig.ParseEntries(Json);
        Assert.True(e[1].Action!.MoveWindow);
        Assert.Equal(1.5, e[1].Action!.Speed);
        Assert.Equal("ウィンドウをつかんで移動", BindingEditing.DescribeAction(e[0]));
        Assert.True(e[1].Clone().Action!.MoveWindow);
        var again = GestureConfig.ParseEntries(GestureConfig.ToJson(e));
        Assert.True(again[0].Action!.MoveWindow);
    }

    [Theory]
    [InlineData("\"moveWindow\": true, \"hotkey\": \"A\"")]
    [InlineData("\"moveWindow\": true, \"click\": \"left\"")]
    [InlineData("\"moveWindow\": true, \"hold\": true")]
    [InlineData("\"moveWindow\": true, \"follow\": false")]
    [InlineData("\"moveWindow\": true, \"speed\": 0")]
    [InlineData("\"moveWindow\": false, \"hotkey\": \"A\"")]
    public void Config_RejectsBadCombinations(string action)
    {
        var json = "{ \"bindings\": [ { \"fingers\": 3, \"slot\": 1, \"direction\": \"Up\", \"action\": { " + action + " } } ] }";
        Assert.Throws<GestureConfigException>(() => GestureConfig.Parse(json));
    }

    [Fact]
    public void Dispatcher_GrabsOnFire_MovesWhileTouching_ReleasesWhenAllLift()
    {
        var f = new Fake();
        var d = new GestureDispatcher(GestureConfig.Parse(
            "{ \"bindings\": [ { \"fingers\": 3, \"slot\": 1, \"direction\": \"Up\", \"action\": { \"moveWindow\": true } } ] }"), f);
        long t = 0;
        double dy = 0;
        while (!f.Log.Contains("grab") && t < 600) { d.Feed(Two3(t, dy)); dy -= 0.01; t += 10; }
        Assert.Contains("grab", f.Log);
        f.Log.Clear();
        d.Feed(Two3(t, dy - 0.02));
        Assert.Contains(f.Log, l => l.StartsWith("win42:"));
        f.Log.Clear();
        d.Feed(new Frame(t + 10, new List<Contact>()));
        d.Feed(Two3(t + 20, 0));                // 触れ直しても、前のつかみは続かない
        Assert.DoesNotContain(f.Log, l => l.StartsWith("win"));
    }

    // 1 本目と 3 本目は固定、2 本目 (スロット 1) が上へ動く
    static Frame Two3(long t, double dy)
        => new(t, new[] { new Contact(1, 0.2, 0.5), new Contact(2, 0.5, 0.5 + dy), new Contact(3, 0.8, 0.5) });
}
