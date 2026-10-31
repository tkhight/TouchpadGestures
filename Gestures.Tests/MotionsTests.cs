using Gestures;
using Xunit;

namespace Gestures.Tests;

public class MotionsTests
{
    static Frame F(long t, params (int id, double x, double y)[] cs)
        => new(t, cs.Select(c => new Contact(c.id, c.x, c.y)).ToList());

    /// <summary>左 0.2 / 中 0.4 / 右 0.7、y=0.5 に着地して落ち着く。</summary>
    static IEnumerable<Frame> Landing()
    {
        foreach (var t in new[] { 0, 50, 100 })
            yield return F(t, (1, 0.2, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5));
    }

    static Frame Y(long t, double l, double m, double r)
        => F(t, (1, 0.2, l), (2, 0.4, m), (3, 0.7, r));

    static int Run(SlotSwipeSpec spec, IEnumerable<Frame> frames)
    {
        var r = new SlotSwipeRecognizer(spec);
        return frames.Count(r.Feed);
    }

    static readonly SlotSwipeSpec MiddleRightDown = new("mr-down", 3, 1, SwipeDirection.Down,
        Motions: new SwipeDirection?[] { null, SwipeDirection.Down, SwipeDirection.Down });

    static readonly SlotSwipeSpec AllUp = new("all-up", 3, 0, SwipeDirection.Up,
        Motions: new SwipeDirection?[] { SwipeDirection.Up, SwipeDirection.Up, SwipeDirection.Up });

    [Fact]
    public void MiddleAndRightDown_LeftFixed_Fires()
    {
        var frames = Landing().Append(Y(150, 0.5, 0.65, 0.65));
        Assert.Equal(1, Run(MiddleRightDown, frames));
    }

    [Fact]
    public void OnlyOneOfTheTwoMoves_DoesNotFire()
    {
        var frames = Landing().Append(Y(150, 0.5, 0.65, 0.5));
        Assert.Equal(0, Run(MiddleRightDown, frames));
    }

    [Fact]
    public void BothMoveButBelowTheDistance_DoesNotFire()
    {
        // 片方は届いたが、もう片方が 0.10 に足りない
        var frames = Landing().Append(Y(150, 0.5, 0.65, 0.55));
        Assert.Equal(0, Run(MiddleRightDown, frames));
    }

    [Fact]
    public void LeftFingerMoves_WhileMiddleAndRightDown_DoesNotFire()
    {
        var frames = Landing().Append(Y(150, 0.65, 0.65, 0.65));
        Assert.Equal(0, Run(MiddleRightDown, frames));
    }

    [Fact]
    public void MovingFingerInWrongDirection_DoesNotFire()
    {
        var frames = Landing().Append(Y(150, 0.5, 0.65, 0.35));
        Assert.Equal(0, Run(MiddleRightDown, frames));
    }

    [Fact]
    public void AllThreeUp_Fires()
    {
        var frames = Landing().Append(Y(150, 0.35, 0.35, 0.35));
        Assert.Equal(1, Run(AllUp, frames));
    }

    [Fact]
    public void AllThreeUp_TwoOnly_DoesNotFire()
    {
        var frames = Landing().Append(Y(150, 0.35, 0.35, 0.5));
        Assert.Equal(0, Run(AllUp, frames));
    }

    [Fact]
    public void AllThreeUp_DifferentSpeeds_FiresWhenTheSlowestArrives()
    {
        var frames = Landing().Concat(new[] { Y(150, 0.30, 0.45, 0.45), Y(200, 0.30, 0.38, 0.35) });
        Assert.Equal(1, Run(AllUp, frames));
    }

    [Fact]
    public void Repeat_UsesTheSlowestFinger()
    {
        var spec = AllUp with { MinDistance = 0.10, RepeatDistance = 0.05 };
        var r = new SlotSwipeRecognizer(spec);
        var counts = new List<int>();
        foreach (var f in Landing().Concat(new[]
                 {
                     Y(150, 0.38, 0.38, 0.38),   // 0.12 → 1 回
                     Y(200, 0.20, 0.38, 0.38),   // 左だけ先へ進んでも、遅い指が基準 → 0
                     Y(250, 0.20, 0.32, 0.32),   // 遅い方 0.18 → +0.05 を越えて 1 回
                 }))
        {
            r.Feed(f);
            counts.Add(r.LastFireCount);
        }
        Assert.Equal(new[] { 0, 0, 0, 1, 0, 1 }, counts);
    }

    [Fact]
    public void SingleSlotSpec_StillWorksAsBefore_AndHasNoMotionsList()
    {
        var old = new SlotSwipeSpec("x", 3, 0, SwipeDirection.Right);
        Assert.Null(old.Motions);
        Assert.Equal(new SwipeDirection?[] { SwipeDirection.Right, null, null }, old.EffectiveMotions());
    }

    [Fact]
    public void Key_DistinguishesMotions()
    {
        var a = new SlotSwipeSpec("a", 3, 0, SwipeDirection.Up);
        Assert.NotEqual(AllUp.Key, a.Key);
        Assert.Equal(a.Key, (a with { Name = "other" }).Key);
    }

    [Fact]
    public void InvalidMotions_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlotSwipeRecognizer(
            new SlotSwipeSpec("n", 3, 0, SwipeDirection.Up, Motions: new SwipeDirection?[] { SwipeDirection.Up })));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlotSwipeRecognizer(
            new SlotSwipeSpec("n", 3, 0, SwipeDirection.Up, Motions: new SwipeDirection?[] { null, null, null })));
    }

    // ---- JSON 設定 ----

    [Fact]
    public void Config_ParsesMotions()
    {
        var b = GestureConfig.Parse("""
            { "bindings": [
              { "fingers": 3, "motions": ["none", "Down", "down"], "action": { "hotkey": "A" } },
              { "fingers": 3, "motions": ["Up", "Up", "Up"], "action": { "hotkey": "B" } } ] }
            """);
        Assert.Equal(new SwipeDirection?[] { null, SwipeDirection.Down, SwipeDirection.Down }, b[0].Swipe!.EffectiveMotions());
        Assert.Equal(new SwipeDirection?[] { SwipeDirection.Up, SwipeDirection.Up, SwipeDirection.Up }, b[1].Swipe!.EffectiveMotions());
        Assert.Equal("3本指・・↓↓", b[0].Swipe!.Name);
    }

    [Fact]
    public void Config_MotionsEqualToSlotDirection_IsADuplicate()
    {
        var ex = Assert.Throws<GestureConfigException>(() => GestureConfig.Parse("""
            { "bindings": [
              { "fingers": 3, "slot": 0, "direction": "Up", "action": { "hotkey": "A" } },
              { "fingers": 3, "motions": ["Up", "none", "none"], "action": { "hotkey": "B" } } ] }
            """));
        Assert.Contains("重複", ex.Errors.Single());
    }

    [Theory]
    [InlineData("""{ "fingers": 3, "motions": ["Up", "Up"] }""", "3 個")]
    [InlineData("""{ "fingers": 3, "motions": ["none", "none", "none"] }""", "1 本もありません")]
    [InlineData("""{ "fingers": 3, "motions": ["Up", "sideways", "none"] }""", "motions[1]")]
    [InlineData("""{ "fingers": 3, "motions": ["Up", "none", "none"], "slot": 0 }""", "一緒に指定できません")]
    public void Config_BadMotions_AreReported(string binding, string expected)
    {
        var json = "{ \"bindings\": [ " + binding.TrimEnd('}', ' ') + ", \"action\": { \"hotkey\": \"A\" } } ] }";
        var ex = Assert.Throws<GestureConfigException>(() => GestureConfig.Parse(json));
        Assert.Contains(expected, string.Join("\n", ex.Errors));
    }
}
