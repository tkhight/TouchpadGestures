using Gestures;
using Xunit;

namespace Gestures.Tests;

public class MultiTapTests
{
    static readonly MultiTapSpec Three = new("three", 3);
    static readonly MultiTapSpec ThreeDouble = new("three-x2", 3, TapCount: 2);

    static Frame F(long t, params int[] ids)
        => new(t, ids.Select(id => new Contact(id, 0.2 + 0.2 * id, 0.5)).ToList());

    static Frame Moved(long t, int movedId, double dx, params int[] ids)
        => new(t, ids.Select(id => new Contact(id, 0.2 + 0.2 * id + (id == movedId ? dx : 0), 0.5)).ToList());

    static List<int> Run(MultiTapSpec spec, IEnumerable<Frame> frames)
    {
        var r = new MultiTapRecognizer(spec);
        return frames.Select(f => r.Feed(f) ? r.LastFireCount : 0).ToList();
    }

    // 3 本が少しずつずれて着地し、少しずれて離れる、ふつうのタップ
    static IEnumerable<Frame> OneTap(long t0) => new[]
    {
        F(t0, 1), F(t0 + 20, 1, 2), F(t0 + 40, 1, 2, 3),
        F(t0 + 100, 1, 2, 3),
        F(t0 + 130, 2, 3), F(t0 + 150, 3), F(t0 + 170),
    };

    [Fact]
    public void ThreeFingersLandingAndLiftingTogether_FireWhenAllAreUp()
    {
        var c = Run(Three, OneTap(0));
        Assert.Equal(new[] { 0, 0, 0, 0, 0, 0, 1 }, c);
    }

    [Fact]
    public void AllLandingAndLiftingInOneFrame_Fires()
    {
        var c = Run(Three, new[] { F(0, 1, 2, 3), F(80, 1, 2, 3), F(100) });
        Assert.Equal(new[] { 0, 0, 1 }, c);
    }

    [Fact]
    public void LandingSpreadOutTooMuch_DoesNotFire()
    {
        // 最後の指が、最初の指から 100ms より遅れて着地した (着地順などの別の動き)
        var c = Run(Three, new[] { F(0, 1), F(60, 1, 2), F(150, 1, 2, 3), F(200, 1, 2, 3), F(220) });
        Assert.DoesNotContain(1, c);
    }

    [Fact]
    public void LiftingSpreadOutTooMuch_DoesNotFire()
    {
        var c = Run(Three, new[] { F(0, 1, 2, 3), F(50, 1, 2, 3), F(60, 2, 3), F(190, 3), F(200) });
        Assert.DoesNotContain(1, c);
    }

    [Fact]
    public void HeldTooLong_IsNotATap()
    {
        var c = Run(Three, new[] { F(0, 1, 2, 3), F(200, 1, 2, 3), F(300, 1, 2, 3), F(310) });
        Assert.DoesNotContain(1, c);
    }

    [Fact]
    public void FingersThatMove_AreASwipe_NotATap()
    {
        var c = Run(Three, new[]
        {
            F(0, 1, 2, 3), Moved(40, 1, 0.01, 1, 2, 3), Moved(80, 1, 0.08, 1, 2, 3), F(120),
        });
        Assert.DoesNotContain(1, c);
    }

    [Fact]
    public void SmallMovement_IsTolerated()
    {
        var c = Run(Three, new[] { F(0, 1, 2, 3), Moved(50, 2, 0.02, 1, 2, 3), F(100) });
        Assert.Equal(new[] { 0, 0, 1 }, c);
    }

    [Fact]
    public void MoreFingersThanTheSpec_DoesNotFire()
    {
        var c = Run(Three, new[] { F(0, 1, 2, 3), F(20, 1, 2, 3, 4), F(60, 1, 2, 3, 4), F(90) });
        Assert.DoesNotContain(1, c);
    }

    [Fact]
    public void FewerFingers_DoNotFire()
    {
        Assert.DoesNotContain(1, Run(Three, new[] { F(0, 1, 2), F(60, 1, 2), F(90) }));
    }

    [Fact]
    public void LiftingBeforeEveryoneHasLanded_DoesNotFire()
    {
        var c = Run(Three, new[] { F(0, 1, 2), F(30, 1), F(50, 1, 3), F(70, 1, 2, 3), F(100) });
        Assert.DoesNotContain(1, c);
    }

    [Fact]
    public void TouchingAgainWhileLifting_DoesNotFire()
    {
        var c = Run(Three, new[] { F(0, 1, 2, 3), F(40, 2, 3), F(60, 2, 3, 4), F(80, 2, 3, 4), F(100) });
        Assert.DoesNotContain(1, c);
    }

    [Fact]
    public void AfterAFailure_TheNextCleanTapStillWorks()
    {
        var frames = new List<Frame> { F(0, 1, 2), F(60, 1, 2), F(90) };   // 2 本だけ
        frames.AddRange(OneTap(1000));
        var c = Run(Three, frames);
        Assert.Equal(1, c.Sum());
        Assert.Equal(1, c[^1]);
    }

    [Fact]
    public void TwoFingerSpec_Works()
    {
        var c = Run(new MultiTapSpec("two", 2), new[] { F(0, 1, 2), F(60, 1, 2), F(90) });
        Assert.Equal(new[] { 0, 0, 1 }, c);
    }

    // ---- 複数回 ----

    [Fact]
    public void DoubleTap_FiresOnTheSecondTap()
    {
        var frames = OneTap(0).Concat(OneTap(400)).ToList();     // 1 回目は 170ms で離れ、2 回目は 230ms 後に着地
        var c = Run(ThreeDouble, frames);
        Assert.Equal(1, c.Sum());
        Assert.Equal(1, c[^1]);
    }

    [Fact]
    public void DoubleTap_WithTooLongAGap_StartsOver()
    {
        var frames = OneTap(0).Concat(OneTap(700)).ToList();     // 間 530ms (既定の 300ms より長い)
        Assert.DoesNotContain(1, Run(ThreeDouble, frames));
        // そのあとにもう 1 回、続けてタップすれば、2 回として数える
        var more = frames.Concat(OneTap(1100)).ToList();
        Assert.Equal(1, Run(ThreeDouble, more).Sum());
    }

    [Fact]
    public void ThirdTapInADoubleTapSpec_StartsOver()
    {
        var frames = OneTap(0).Concat(OneTap(400)).Concat(OneTap(800)).ToList();
        Assert.Equal(1, Run(ThreeDouble, frames).Sum());
    }

    [Fact]
    public void ASingleTapInBetween_IsKeptWhenNothingTouches()
    {
        // 触れていない間のフレームが続いても、間の時間が収まっていれば 2 回として数える
        var frames = OneTap(0).Concat(new[] { F(200), F(250) }).Concat(OneTap(400)).ToList();
        Assert.Equal(1, Run(ThreeDouble, frames).Sum());
    }

    [Fact]
    public void ThresholdsCanBeChanged()
    {
        var slow = Three with { MaxTapMs = 600, MaxSpreadMs = 300 };
        var c = Run(slow, new[] { F(0, 1), F(150, 1, 2), F(250, 1, 2, 3), F(400, 1, 2, 3), F(500, 3), F(550) });
        Assert.Equal(1, c.Sum());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 0)]
    public void BadSpecs_Throw(int fingers, int count)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new MultiTapRecognizer(new MultiTapSpec("x", fingers, count)));

    // ---- 設定・画面 ----

    const string Json = """
        { "bindings": [
          { "fingers": 3, "tapAll": true, "action": { "hotkey": "Ctrl+Shift+T" } },
          { "fingers": 4, "tapAll": true, "tapCount": 2, "maxSpreadMs": 150, "maxTapMs": 300,
            "action": { "hotkey": "Ctrl+W" } }
        ] }
        """;

    [Fact]
    public void Config_ParsesMultiTaps()
    {
        var b = GestureConfig.Parse(Json);
        Assert.Equal(new MultiTapSpec("3本指で同時にタップ", 3), b[0].Spec);
        Assert.Equal(new MultiTapSpec("4本指で同時に2回タップ", 4, 2, MaxTapMs: 300, MaxSpreadMs: 150), b[1].Spec);
    }

    [Theory]
    [InlineData("\"tapSlot\": 0, \"tapAll\": true", "一緒に指定できません")]
    [InlineData("\"pinch\": \"In\", \"tapAll\": true", "一緒に指定できません")]
    [InlineData("\"slot\": 0, \"direction\": \"Left\", \"tapAll\": true", "同時タップには")]
    [InlineData("\"tapAll\": false", "true のときだけ")]
    [InlineData("\"tapAll\": true, \"maxSpreadMs\": -1", "0 以上")]
    [InlineData("\"maxSpreadMs\": 100, \"slot\": 0, \"direction\": \"Left\"", "tapAll と一緒にだけ")]
    public void Config_RejectsBadCombinations(string fields, string message)
    {
        var json = "{ \"bindings\": [ { \"fingers\": 3, " + fields + ", \"action\": { \"hotkey\": \"A\" } } ] }";
        var ex = Assert.Throws<GestureConfigException>(() => GestureConfig.Parse(json));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Config_RejectsOneFinger()
    {
        var ex = Assert.Throws<GestureConfigException>(() =>
            GestureConfig.Parse("{ \"bindings\": [ { \"fingers\": 1, \"tapAll\": true, \"action\": { \"hotkey\": \"A\" } } ] }"));
        Assert.Contains("2 本以上", ex.Message);
    }

    [Fact]
    public void Config_TapAndMultiTapWithTheSameFingerCount_AreDifferentGestures()
    {
        var b = GestureConfig.Parse("""
            { "bindings": [
              { "fingers": 3, "tapAll": true, "action": { "hotkey": "A" } },
              { "fingers": 3, "tapSlot": 0, "action": { "hotkey": "B" } }
            ] }
            """);
        Assert.Equal(2, b.Count);
    }

    [Fact]
    public void Config_DuplicateMultiTaps_AreRejected()
    {
        var ex = Assert.Throws<GestureConfigException>(() => GestureConfig.Parse("""
            { "bindings": [
              { "fingers": 3, "tapAll": true, "action": { "hotkey": "A" } },
              { "fingers": 3, "tapAll": true, "action": { "hotkey": "B" } }
            ] }
            """));
        Assert.Contains("重複", ex.Message);
    }

    [Fact]
    public void Editing_KindClearAndRoundTrip()
    {
        var entries = GestureConfig.ParseEntries(Json);
        Assert.Equal(GestureKind.MultiTap, BindingEditing.KindOf(entries[0]));

        var again = GestureConfig.ParseEntries(GestureConfig.ToJson(entries));
        Assert.Equal(2, GestureConfig.Build(again).Count);
        Assert.True(again[1].TapAll);
        Assert.Equal(150, again[1].MaxSpreadMs);

        var copy = entries[1].Clone();
        Assert.True(copy.TapAll);
        Assert.Equal(150, copy.MaxSpreadMs);
        BindingEditing.ClearGesture(copy);
        Assert.Null(copy.TapAll);
        Assert.Null(copy.MaxSpreadMs);
        Assert.Null(copy.TapCount);
        Assert.Equal(GestureKind.Swipe, BindingEditing.KindOf(copy));
    }

    sealed class Rec : IInputSender
    {
        public readonly List<string> Log = new();
        public void KeyDown(ushort vk) => Log.Add($"down:{vk:X2}");
        public void KeyUp(ushort vk) => Log.Add($"up:{vk:X2}");
        public void Wheel(int delta, bool horizontal) { }
    }

    [Fact]
    public void Dispatcher_FiresTheAssignedAction()
    {
        var r = new Rec();
        var d = new GestureDispatcher(GestureConfig.Parse(Json), r);
        foreach (var f in OneTap(0)) d.Feed(f);
        Assert.Equal(new[] { "down:11", "down:10", "down:54", "up:54", "up:10", "up:11" }, r.Log);
    }
}
