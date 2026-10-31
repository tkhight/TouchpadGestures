using Gestures;
using Xunit;

namespace Gestures.Tests;

public class RepeatTests
{
    sealed class FakeSender : IInputSender
    {
        public List<string> Log { get; } = new();
        public void KeyDown(ushort vk) => Log.Add($"down:{vk:X2}");
        public void KeyUp(ushort vk) => Log.Add($"up:{vk:X2}");
        public void Wheel(int delta, bool horizontal) => Log.Add($"wheel:{delta}");
    }

    // 3本指・左(スロット0)だけ右へ。最初の発火は 0.15、以後 0.05 進むごとに発火
    // (既定の minDistance は 0.10 だが、ここでは数値を読みやすくするため 0.15 を明示する)
    static readonly SlotSwipeSpec Repeating =
        new("left-right", 3, 0, SwipeDirection.Right, MinDistance: 0.15, RepeatDistance: 0.05);

    static Frame F(long t, params (int id, double x, double y)[] cs)
        => new(t, cs.Select(c => new Contact(c.id, c.x, c.y)).ToList());

    /// <summary>左の指が x に居て、中と右は固定。</summary>
    static Frame Left(long t, double x) => F(t, (1, x, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5));

    /// <summary>左の指が 0.2 に着地して落ち着くまで (進行距離 0 の基準位置)。</summary>
    static IEnumerable<Frame> Landing(double x = 0.2)
    {
        yield return Left(0, x);
        yield return Left(50, x);
        yield return Left(100, x);
    }

    /// <summary>進行距離 along の位置へ動いたフレーム (基準 x=0.2)。</summary>
    static Frame At(long t, double along) => Left(t, 0.2 + along);

    static List<int> Counts(SlotSwipeSpec spec, IEnumerable<Frame> frames)
    {
        var r = new SlotSwipeRecognizer(spec);
        var counts = new List<int>();
        foreach (var f in frames)
        {
            r.Feed(f);
            counts.Add(r.LastFireCount);
        }
        return counts;
    }

    [Fact]
    public void FiresAtMinDistance_ThenEveryRepeatDistance()
    {
        var frames = Landing().Concat(new[]
        {
            At(150, 0.10),  // まだ
            At(200, 0.16),  // 最初の発火
            At(250, 0.19),  // まだ
            At(300, 0.22),  // +0.05 で 2 回目
            At(350, 0.26),  // 3 回目
        });
        Assert.Equal(new[] { 0, 0, 0, 0, 1, 0, 1, 1 }, Counts(Repeating, frames));
    }

    [Fact]
    public void WithoutRepeatDistance_StillFiresOnlyOnce()
    {
        var oneShot = new SlotSwipeSpec("one", 3, 0, SwipeDirection.Right);
        var frames = Landing().Concat(new[] { At(150, 0.16), At(200, 0.30), At(250, 0.50) });
        Assert.Equal(new[] { 0, 0, 0, 1, 0, 0 }, Counts(oneShot, frames));
    }

    [Fact]
    public void BigJump_FiresTheFirstAndTheCatchUpRepeatsInOneFrame()
    {
        // 0.32 進む: 最初の 1 回 + (0.32-0.15)/0.05 = 3 回 → 4 回
        var frames = Landing().Concat(new[] { At(150, 0.32) });
        Assert.Equal(4, Counts(Repeating, frames).Last());
    }

    [Fact]
    public void MovingBackThenForward_CountsFromTheTurningPoint()
    {
        var frames = Landing().Concat(new[]
        {
            At(150, 0.16),  // 発火 (基準 0.15)
            At(200, 0.26),  // 0.20, 0.25 を越えて発火
            At(250, 0.18),  // 戻した: 数え直しの基準が 0.18 に下がる。発火しない
            At(300, 0.22),  // 0.18 + 0.05 = 0.23 に未達
            At(350, 0.24),  // 0.23 を越えて発火
        });
        var counts = Counts(Repeating, frames);
        Assert.Equal(new[] { 0, 0, 0, 1, 2, 0, 0, 1 }, counts);
    }

    [Fact]
    public void MovingBackPastTheFirstFirePoint_DoesNotLowerTheAnchorBelowMinDistance()
    {
        var frames = Landing().Concat(new[]
        {
            At(150, 0.16),  // 発火
            At(200, 0.02),  // ほぼ元の位置まで戻す (基準は 0.15 で止まる)
            At(250, 0.19),  // 0.15 + 0.05 = 0.20 に未達
            At(300, 0.21),  // 越えて発火
        });
        Assert.Equal(new[] { 0, 0, 0, 1, 0, 0, 1 }, Counts(Repeating, frames));
    }

    [Fact]
    public void FixedFingerMovesDuringRepeat_StopsFiring()
    {
        var frames = Landing().Concat(new[]
        {
            At(150, 0.16),                                              // 発火
            F(200, (1, 0.2 + 0.22, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5)), // 発火
            F(250, (1, 0.2 + 0.30, 0.5), (2, 0.5, 0.5), (3, 0.7, 0.5)), // 中の指が動いた
            F(300, (1, 0.2 + 0.40, 0.5), (2, 0.5, 0.5), (3, 0.7, 0.5)), // 以後は発火しない
        });
        Assert.Equal(new[] { 0, 0, 0, 1, 1, 0, 0 }, Counts(Repeating, frames));
    }

    [Fact]
    public void SidewaysDriftDuringRepeat_DoesNotFire()
    {
        var frames = Landing().Concat(new[]
        {
            At(150, 0.16),                                              // 発火
            F(200, (1, 0.2 + 0.22, 0.9), (2, 0.4, 0.5), (3, 0.7, 0.5)), // 進んだが 45° を超えて縦に逸れた
        });
        Assert.Equal(new[] { 0, 0, 0, 1, 0 }, Counts(Repeating, frames));
    }

    [Fact]
    public void RepeatsAreCapped_PerFrame()
    {
        var tiny = Repeating with { RepeatDistance = 0.001 };
        var frames = Landing(0.05).Concat(new[] { Left(150, 0.95) });
        Assert.Equal(SlotSwipeRecognizer.MaxFiresPerFrame, Counts(tiny, frames).Last());
    }

    [Fact]
    public void NewTouch_AfterLiftingAll_StartsOver()
    {
        var frames = Landing().Concat(new[] { At(150, 0.16), F(200) })
            .Concat(Landing().Select(f => f with { TimeMs = f.TimeMs + 1000 }))
            .Concat(new[] { At(1150, 0.16) });
        Assert.Equal(2, Counts(Repeating, frames).Count(c => c > 0));
    }

    [Fact]
    public void Engine_ListsTheSpecOncePerFire()
    {
        var engine = new GestureEngine(new[] { Repeating });
        var fired = new List<string>();
        foreach (var f in Landing().Concat(new[] { At(150, 0.32) }))
            fired.AddRange(engine.Feed(f).Select(s => s.Name));
        Assert.Equal(4, fired.Count);
    }

    [Fact]
    public void Dispatcher_ExecutesTheActionOncePerFire()
    {
        var sender = new FakeSender();
        var d = new GestureDispatcher(new[] { new GestureBinding(Repeating, new HotkeyAction("A")) }, sender);
        foreach (var f in Landing().Concat(new[] { At(150, 0.32) })) d.Feed(f);

        Assert.Equal(8, sender.Log.Count); // 4 回 × (down + up)
        Assert.All(sender.Log.Where(l => l.StartsWith("down")), l => Assert.Equal("down:41", l));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.05)]
    public void InvalidRepeatDistance_Throws(double bad)
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SlotSwipeRecognizer(Repeating with { RepeatDistance = bad }));

    // ---- JSON 設定 ----

    [Fact]
    public void Config_ParsesRepeatDistance()
    {
        var b = GestureConfig.Parse("""
            { "bindings": [ { "fingers": 3, "slot": 2, "direction": "Up", "repeatDistance": 0.04,
              "action": { "hotkey": "VolumeUp" } } ] }
            """);
        Assert.Equal(0.04, b[0].Swipe!.RepeatDistance);
    }

    [Fact]
    public void Config_RepeatDistanceOmitted_MeansOneShot()
    {
        var b = GestureConfig.Parse("""
            { "bindings": [ { "fingers": 3, "slot": 2, "direction": "Up",
              "action": { "hotkey": "VolumeUp" } } ] }
            """);
        Assert.Null(b[0].Swipe!.RepeatDistance);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Config_RepeatDistanceMustBePositive(string value)
    {
        var ex = Assert.Throws<GestureConfigException>(() => GestureConfig.Parse($$"""
            { "bindings": [ { "fingers": 3, "slot": 2, "direction": "Up", "repeatDistance": {{value}},
              "action": { "hotkey": "VolumeUp" } } ] }
            """));
        Assert.Contains("repeatDistance", ex.Errors.Single());
    }

    [Fact]
    public void Sample_UsesRepeatForVolumeAndScroll()
    {
        var b = GestureConfig.Parse(GestureConfig.SampleJson);
        Assert.Equal(4, b.Count(x => x.Swipe!.RepeatDistance is not null));
        Assert.Null(b.First(x => x.Swipe!.Name == "3本指・左だけ←").Swipe!.RepeatDistance);
    }
}
