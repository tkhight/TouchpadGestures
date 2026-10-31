using Gestures;
using Xunit;

namespace Gestures.Tests;

public class LandOrderTests
{
    // 左 → 中 → 右の順に置く (3 本)
    static readonly LandOrderSpec LeftMidRight = new("l-m-r", 3, new[] { 0, 1, 2 });

    static Frame F(long t, params (int id, double x)[] cs)
        => new(t, cs.Select(c => new Contact(c.id, c.x, 0.5)).ToList());

    // ID 1 = 左 (x 0.2)、ID 2 = 中 (0.5)、ID 3 = 右 (0.8)
    const double L = 0.2, M = 0.5, R = 0.8;

    static List<int> Run(LandOrderSpec spec, IEnumerable<Frame> frames)
    {
        var r = new LandOrderRecognizer(spec);
        return frames.Select(f => r.Feed(f) ? r.LastFireCount : 0).ToList();
    }

    [Fact]
    public void LandingLeftMiddleRight_FiresWhenTheLastFingerLands()
    {
        var c = Run(LeftMidRight, new[]
        {
            F(0, (1, L)),
            F(150, (1, L), (2, M)),
            F(300, (1, L), (2, M), (3, R)),
            F(350, (1, L), (2, M), (3, R)),
        });
        Assert.Equal(new[] { 0, 0, 1, 0 }, c);
    }

    [Fact]
    public void WrongOrder_DoesNotFire()
    {
        var c = Run(LeftMidRight, new[]
        {
            F(0, (3, R)),
            F(150, (3, R), (2, M)),
            F(300, (3, R), (2, M), (1, L)),   // 右 → 中 → 左
        });
        Assert.DoesNotContain(1, c);
    }

    [Fact]
    public void OtherOrders_CanBeDefined()
    {
        var rightLeftMid = new LandOrderSpec("r-l-m", 3, new[] { 2, 0, 1 });
        var c = Run(rightLeftMid, new[]
        {
            F(0, (3, R)),
            F(150, (3, R), (1, L)),
            F(300, (3, R), (1, L), (2, M)),
        });
        Assert.Equal(new[] { 0, 0, 1 }, c);
    }

    [Fact]
    public void LandingTogether_IsNotAnOrder()
    {
        // 同じフレームに複数、または間隔が短すぎる (普通に 3 本を置いただけ) は数えない
        Assert.DoesNotContain(1, Run(LeftMidRight, new[] { F(0, (1, L), (2, M), (3, R)) }));
        Assert.DoesNotContain(1, Run(LeftMidRight, new[]
        {
            F(0, (1, L)), F(20, (1, L), (2, M)), F(40, (1, L), (2, M), (3, R)),
        }));
    }

    [Fact]
    public void TooSlow_DoesNotFire()
    {
        var c = Run(LeftMidRight, new[]
        {
            F(0, (1, L)), F(150, (1, L), (2, M)), F(900, (1, L), (2, M), (3, R)),
        });
        Assert.DoesNotContain(1, c);
    }

    [Fact]
    public void FingerMoving_DoesNotFire()
    {
        var c = Run(LeftMidRight, new[]
        {
            F(0, (1, L)),
            F(150, (1, L + 0.1), (2, M)),     // 左の指が動いた
            F(300, (1, L + 0.1), (2, M), (3, R)),
        });
        Assert.DoesNotContain(1, c);
    }

    [Fact]
    public void LiftingAFinger_DoesNotFire_UntilEveryoneLeaves()
    {
        var c = Run(LeftMidRight, new[]
        {
            F(0, (1, L)),
            F(150, (1, L), (2, M)),
            F(200, (2, M)),                              // 左が離れた
            F(350, (2, M), (3, R)),
            F(500, (2, M), (3, R), (1, L)),              // 途中で失敗しているので数えない
            F(600),                                      // 全員離れる
            F(1000, (4, L)), F(1150, (4, L), (5, M)), F(1300, (4, L), (5, M), (6, R)),   // やり直し
        });
        Assert.Equal(new[] { 0, 0, 0, 0, 0, 0, 0, 0, 1 }, c);
    }

    [Fact]
    public void FiresOnlyOncePerTouch()
    {
        var c = Run(LeftMidRight, new[]
        {
            F(0, (1, L)), F(150, (1, L), (2, M)), F(300, (1, L), (2, M), (3, R)),
            F(350, (1, L), (2, M), (3, R)), F(400, (1, L), (2, M), (3, R)),
        });
        Assert.Equal(1, c.Count(x => x == 1));
    }

    [Fact]
    public void ExtraFinger_FailsTheGesture()
    {
        var c = Run(LeftMidRight, new[]
        {
            F(0, (1, L)), F(150, (1, L), (2, M)), F(300, (1, L), (2, M), (3, R), (4, 0.65)),
        });
        Assert.DoesNotContain(1, c);
    }

    [Fact]
    public void TheOrderIsTakenFromTheFinalPositions()
    {
        // 左の指が着地したあと、中の指が左の指を追い越して左側になっても、「最後の位置」の X 順で数える
        var c = Run(new LandOrderSpec("x", 2, new[] { 0, 1 }), new[]
        {
            F(0, (1, 0.5)),
            F(150, (1, 0.5), (2, 0.45)),     // 2 本目は左側に着地: 着地順は (右にいる指 → 左にいる指) = 1, 0
        });
        Assert.DoesNotContain(1, c);
        var c2 = Run(new LandOrderSpec("y", 2, new[] { 1, 0 }), new[] { F(0, (1, 0.5)), F(150, (1, 0.5), (2, 0.45)) });
        Assert.Equal(new[] { 0, 1 }, c2);
    }

    [Theory]
    [InlineData(1, new[] { 0 })]
    [InlineData(3, new[] { 0, 1 })]
    [InlineData(3, new[] { 0, 0, 1 })]
    [InlineData(3, new[] { 0, 1, 3 })]
    public void InvalidSpec_Throws(int fingers, int[] order)
        => Assert.ThrowsAny<ArgumentException>(() => new LandOrderRecognizer(new LandOrderSpec("x", fingers, order)));

    [Fact]
    public void Engine_And_Dispatcher_HandleLandOrderSpecs()
    {
        var log = new List<string>();
        var d = new GestureDispatcher(new[] { new GestureBinding(LeftMidRight, new HotkeyAction("A")) }, new Fake(log));
        var fired = new List<string>();
        foreach (var f in new[] { F(0, (1, L)), F(150, (1, L), (2, M)), F(300, (1, L), (2, M), (3, R)) })
            fired.AddRange(d.Feed(f).Select(s => s.Name));
        Assert.Equal(new[] { "l-m-r" }, fired);
        Assert.Equal(new[] { "down:41", "up:41" }, log);
    }

    sealed class Fake : IInputSender
    {
        readonly List<string> _log;
        public Fake(List<string> log) => _log = log;
        public void KeyDown(ushort vk) => _log.Add($"down:{vk:X2}");
        public void KeyUp(ushort vk) => _log.Add($"up:{vk:X2}");
        public void Wheel(int delta, bool horizontal) => _log.Add($"wheel:{delta}");
    }

    // ---- JSON 設定 ----

    [Fact]
    public void Config_ParsesLandOrder()
    {
        var b = GestureConfig.Parse("""
            { "bindings": [
              { "fingers": 3, "landOrder": [0, 1, 2], "action": { "hotkey": "A" } },
              { "fingers": 3, "landOrder": [2, 0, 1], "landMinGapMs": 50, "landMaxGapMs": 400, "landMaxMove": 0.02,
                "action": { "hotkey": "B" } } ] }
            """);
        var s0 = (LandOrderSpec)b[0].Spec;
        Assert.Equal("3本指・左→中→右の順に着地", s0.Name);
        Assert.Equal(new[] { 0, 1, 2 }, s0.Order);
        Assert.Equal((80L, 500L, 0.03), (s0.MinGapMs, s0.MaxGapMs, s0.MaxMove));
        var s1 = (LandOrderSpec)b[1].Spec;
        Assert.Equal("3本指・右→左→中の順に着地", s1.Name);
        Assert.Equal((50L, 400L, 0.02), (s1.MinGapMs, s1.MaxGapMs, s1.MaxMove));
    }

    [Fact]
    public void Config_DefaultNames_ForOtherFingerCounts()
    {
        var b = GestureConfig.Parse("""
            { "bindings": [
              { "fingers": 2, "landOrder": [1, 0], "action": { "hotkey": "A" } },
              { "fingers": 4, "landOrder": [0, 1, 2, 3], "action": { "hotkey": "B" } } ] }
            """);
        Assert.Equal("2本指・右→左の順に着地", b[0].Spec.Name);
        Assert.Equal("4本指・1→2→3→4の順に着地", b[1].Spec.Name);
    }

    [Fact]
    public void Config_DuplicateLandOrder_IsReported_ButDifferentOrdersAreNot()
    {
        Assert.Throws<GestureConfigException>(() => GestureConfig.Parse("""
            { "bindings": [
              { "fingers": 3, "landOrder": [0, 1, 2], "action": { "hotkey": "A" } },
              { "fingers": 3, "landOrder": [0, 1, 2], "action": { "hotkey": "B" } } ] }
            """));
        Assert.Equal(2, GestureConfig.Parse("""
            { "bindings": [
              { "fingers": 3, "landOrder": [0, 1, 2], "action": { "hotkey": "A" } },
              { "fingers": 3, "landOrder": [2, 1, 0], "action": { "hotkey": "B" } } ] }
            """).Count);
    }

    [Theory]
    [InlineData("""{ "fingers": 3, "landOrder": [0, 1] }""", "landOrder は")]
    [InlineData("""{ "fingers": 3, "landOrder": [0, 1, 1] }""", "landOrder は")]
    [InlineData("""{ "fingers": 3, "landOrder": [0, 1, 3] }""", "landOrder は")]
    [InlineData("""{ "fingers": 1, "landOrder": [0] }""", "2 本以上")]
    [InlineData("""{ "fingers": 3, "landOrder": [0, 1, 2], "slot": 0, "direction": "Up" }""", "着地順には")]
    [InlineData("""{ "fingers": 3, "landOrder": [0, 1, 2], "tapSlot": 0 }""", "一緒に指定できません")]
    [InlineData("""{ "fingers": 3, "landOrder": [0, 1, 2], "pinch": "In" }""", "一緒に指定できません")]
    [InlineData("""{ "fingers": 3, "landOrder": [0, 1, 2], "landMinGapMs": -1 }""", "landMinGapMs は 0 以上")]
    [InlineData("""{ "fingers": 3, "landOrder": [0, 1, 2], "landMinGapMs": 600 }""", "以下にしてください")]
    [InlineData("""{ "fingers": 3, "landOrder": [0, 1, 2], "landMaxMove": 0 }""", "landMaxMove は 0 より大きい")]
    [InlineData("""{ "fingers": 3, "slot": 0, "direction": "Up", "landMaxMove": 0.02 }""", "landOrder と一緒にだけ")]
    public void Config_BadLandOrder_IsReported(string binding, string expected)
    {
        var json = "{ \"bindings\": [ " + binding.TrimEnd('}', ' ') + ", \"action\": { \"hotkey\": \"A\" } } ] }";
        var ex = Assert.Throws<GestureConfigException>(() => GestureConfig.Parse(json));
        Assert.Contains(expected, string.Join("\n", ex.Errors));
    }

    [Fact]
    public void Editing_KindClearAndJsonRoundTrip()
    {
        var entries = GestureConfig.ParseEntries("""
            { "bindings": [ { "fingers": 3, "landOrder": [0, 2, 1], "landMinGapMs": 60, "action": { "hotkey": "A" } } ] }
            """);
        Assert.Equal(GestureKind.LandOrder, BindingEditing.KindOf(entries[0]));
        var again = GestureConfig.ParseEntries(GestureConfig.ToJson(entries));
        Assert.Equal(new[] { 0, 2, 1 }, again[0].LandOrder);
        Assert.Equal(60, again[0].LandMinGapMs);
        var c = entries[0].Clone();
        c.LandOrder![0] = 2;
        Assert.Equal(0, entries[0].LandOrder![0]);
        BindingEditing.ClearGesture(c);
        Assert.Null(c.LandOrder);
        Assert.Null(c.LandMinGapMs);
    }
}
