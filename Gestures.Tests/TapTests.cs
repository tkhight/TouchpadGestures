using Gestures;
using Xunit;

namespace Gestures.Tests;

public class TapTests
{
    // 3本指・左(スロット0)を 2 回タップ。中と右は触れたまま
    static readonly TapSpec LeftDouble = new("left-x2", 3, 0, TapCount: 2);
    static readonly TapSpec LeftSingle = new("left-x1", 3, 0);

    static Frame F(long t, params (int id, double x, double y)[] cs)
        => new(t, cs.Select(c => new Contact(c.id, c.x, c.y)).ToList());

    // 触れたままの中(ID 2, x=0.4)と右(ID 3, x=0.7)
    static Frame Rest(long t) => F(t, (2, 0.4, 0.5), (3, 0.7, 0.5));

    // 左の指が着地している (ID は毎回変えられる)
    static Frame Tap(long t, int id = 1, double x = 0.2) => F(t, (id, x, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5));

    static List<int> Run(TapSpec spec, IEnumerable<Frame> frames)
    {
        var r = new TapRecognizer(spec);
        return frames.Select(f => r.Feed(f) ? r.LastFireCount : 0).ToList();
    }

    [Fact]
    public void SingleTap_Fires_OnLift()
    {
        var c = Run(LeftSingle, new[] { Rest(0), Rest(50), Tap(100), Tap(150), Rest(200) });
        Assert.Equal(new[] { 0, 0, 0, 0, 1 }, c);
    }

    [Fact]
    public void DoubleTap_FiresOnlyAfterTheSecondLift()
    {
        var c = Run(LeftDouble, new[]
        {
            Rest(0), Tap(60, 1), Tap(100, 1), Rest(150),     // 1 回目
            Tap(250, 4), Tap(300, 4), Rest(350),              // 2 回目 (ID は変わってよい)
        });
        Assert.Equal(new[] { 0, 0, 0, 0, 0, 0, 1 }, c);
    }

    [Fact]
    public void DoubleTap_GapTooLong_DoesNotFire()
    {
        var c = Run(LeftDouble, new[]
        {
            Rest(0), Tap(60), Rest(120),
            Rest(500), Tap(560, 4), Rest(620),               // 1 回目から 0.3 秒を超えて 2 回目が来た
        });
        Assert.DoesNotContain(1, c);
    }

    [Fact]
    public void HoldTooLong_IsNotATap()
    {
        var c = Run(LeftSingle, new[] { Rest(0), Tap(100), Tap(300), Tap(500), Rest(600) });
        Assert.DoesNotContain(1, c);
    }

    [Fact]
    public void FingerSlidesWhileDown_IsNotATap()
    {
        var c = Run(LeftSingle, new[] { Rest(0), Tap(100), Tap(150, 1, 0.30), Rest(200) });
        Assert.DoesNotContain(1, c);
    }

    [Fact]
    public void TapInTheWrongSlot_IsIgnored()
    {
        // 左ではなく、中と右の間に着地 → スロット 1
        var between = F(100, (1, 0.55, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5));
        var c = Run(LeftSingle, new[] { Rest(0), between, F(150, (1, 0.55, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5)), Rest(200) });
        Assert.DoesNotContain(1, c);

        var middle = new TapSpec("mid", 3, 1);
        Assert.Contains(1, Run(middle, new[] { Rest(0), between, Rest(200) }));
    }

    [Fact]
    public void RestingFingerMoves_CancelsTheTap()
    {
        var c = Run(LeftSingle, new[]
        {
            Rest(0), Tap(100),
            F(150, (1, 0.2, 0.5), (2, 0.55, 0.5), (3, 0.7, 0.5)),  // 中の指が動いた
            Rest(200),
        });
        Assert.DoesNotContain(1, c);
    }

    [Fact]
    public void TappingRepeatedlyWhileResting_FiresEachTime()
    {
        var c = Run(LeftSingle, new[]
        {
            Rest(0), Tap(100), Rest(150),
            Rest(400), Tap(500, 5), Rest(550),
        });
        Assert.Equal(2, c.Count(x => x == 1));
    }

    [Fact]
    public void ThirdTapInADoubleTapSpec_StartsOver()
    {
        var c = Run(LeftDouble, new[]
        {
            Rest(0), Tap(60), Rest(100), Tap(150, 4), Rest(200),   // 2 回で発火
            Tap(250, 5), Rest(300),                                 // 3 回目: 新しい 1 回目
            Tap(350, 6), Rest(400),                                 // その 2 回目
        });
        Assert.Equal(2, c.Count(x => x == 1));
    }

    [Fact]
    public void WithoutTheRestingFingers_NothingFires()
    {
        // 左の指だけの 1 本指タップは、3 本指のタップ定義には当たらない
        var c = Run(LeftSingle, new[] { F(0), F(50, (1, 0.2, 0.5)), F(100, (1, 0.2, 0.5)), F(150) });
        Assert.DoesNotContain(1, c);
    }

    [Fact]
    public void OneFingerTap_Works()
    {
        var one = new TapSpec("one", 1, 0);
        var c = Run(one, new[] { F(0), F(50, (1, 0.5, 0.5)), F(100, (1, 0.5, 0.5)), F(150) });
        Assert.Equal(new[] { 0, 0, 0, 1 }, c);
    }

    [Fact]
    public void Engine_And_Dispatcher_HandleTapSpecs()
    {
        var sent = new List<string>();
        var d = new GestureDispatcher(new[]
        {
            new GestureBinding(LeftSingle, new HotkeyAction("A")),
            new GestureBinding(new SlotSwipeSpec("s", 3, 0, SwipeDirection.Right), new HotkeyAction("B")),
        }, new FakeSender(sent));
        var fired = new List<string>();
        foreach (var f in new[] { Rest(0), Tap(100), Rest(150) })
            fired.AddRange(d.Feed(f).Select(s => s.Name));
        Assert.Equal(new[] { "left-x1" }, fired);
        Assert.Equal(new[] { "down:41", "up:41" }, sent);
    }

    sealed class FakeSender : IInputSender
    {
        readonly List<string> _log;
        public FakeSender(List<string> log) => _log = log;
        public void KeyDown(ushort vk) => _log.Add($"down:{vk:X2}");
        public void KeyUp(ushort vk) => _log.Add($"up:{vk:X2}");
        public void Wheel(int delta, bool horizontal) => _log.Add($"wheel:{delta}");
    }

    [Theory]
    [InlineData(3, 3, 1)]
    [InlineData(0, 3, 0)]
    [InlineData(0, 0, 1)]
    public void InvalidSpec_Throws(int slot, int fingers, int count)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new TapRecognizer(new TapSpec("x", fingers, slot, count)));

    // ---- JSON 設定 ----

    [Fact]
    public void Config_ParsesTap()
    {
        var b = GestureConfig.Parse("""
            { "bindings": [
              { "fingers": 3, "tapSlot": 0, "tapCount": 2, "action": { "hotkey": "A" } },
              { "fingers": 3, "tapSlot": 2, "maxTapMs": 200, "maxGapMs": 250, "maxTapMove": 0.02,
                "action": { "hotkey": "B" } } ] }
            """);
        Assert.Equal(new TapSpec("3本指・左から1番目を2回タップ", 3, 0, 2), b[0].Tap);
        Assert.Equal(new TapSpec("3本指・左から3番目をタップ", 3, 2, 1, 200, 250, 0.02), b[1].Tap);
        Assert.Null(b[0].Swipe);
    }

    [Fact]
    public void Config_TapAndSwipeCanShareTheSameFinger()
    {
        var b = GestureConfig.Parse("""
            { "bindings": [
              { "fingers": 3, "tapSlot": 0, "action": { "hotkey": "A" } },
              { "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "B" } } ] }
            """);
        Assert.Equal(2, b.Count);
    }

    [Fact]
    public void Config_DuplicateTap_IsReported()
    {
        var ex = Assert.Throws<GestureConfigException>(() => GestureConfig.Parse("""
            { "bindings": [
              { "fingers": 3, "tapSlot": 0, "tapCount": 2, "action": { "hotkey": "A" } },
              { "fingers": 3, "tapSlot": 0, "tapCount": 2, "action": { "hotkey": "B" } } ] }
            """));
        Assert.Contains("重複", ex.Errors.Single());
    }

    [Theory]
    [InlineData("""{ "fingers": 3, "tapCount": 2 }""", "tapSlot が必要")]
    [InlineData("""{ "fingers": 3, "tapSlot": 3 }""", "tapSlot は 0 〜 2")]
    [InlineData("""{ "fingers": 3, "tapSlot": 0, "tapCount": 0 }""", "tapCount は 1 以上")]
    [InlineData("""{ "fingers": 3, "tapSlot": 0, "direction": "Up" }""", "タップには")]
    [InlineData("""{ "fingers": 3, "slot": 0, "direction": "Up", "maxTapMs": 100 }""", "tapSlot と一緒にだけ")]
    [InlineData("""{ "fingers": 3, "tapSlot": 0, "maxGapMs": 0 }""", "maxGapMs は 0 より大きい")]
    public void Config_BadTap_IsReported(string binding, string expected)
    {
        var json = "{ \"bindings\": [ " + binding.TrimEnd('}', ' ') + ", \"action\": { \"hotkey\": \"A\" } } ] }";
        var ex = Assert.Throws<GestureConfigException>(() => GestureConfig.Parse(json));
        Assert.Contains(expected, string.Join("\n", ex.Errors));
    }
}
