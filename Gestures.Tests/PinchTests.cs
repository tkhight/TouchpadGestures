using Gestures;
using Xunit;

namespace Gestures.Tests;

public class PinchTests
{
    static readonly PinchSpec Out4 = new("out4", 4, PinchDirection.Out);
    static readonly PinchSpec In4 = new("in4", 4, PinchDirection.In);

    static Frame F(long t, params (int id, double x, double y)[] cs)
        => new(t, cs.Select(c => new Contact(c.id, c.x, c.y)).ToList());

    /// <summary>中心 (0.5, 0.5) から、4 本が上下左右に r だけ離れた形。</summary>
    static Frame Cross(long t, double r, double cx = 0.5, double cy = 0.5)
        => F(t, (1, cx - r, cy), (2, cx, cy - r), (3, cx + r, cy), (4, cx, cy + r));

    static List<int> Run(PinchSpec spec, IEnumerable<Frame> frames)
    {
        var r = new PinchRecognizer(spec);
        return frames.Select(f => r.Feed(f) ? r.LastFireCount : 0).ToList();
    }

    [Fact]
    public void FourFingersSpread_FiresOut_NotIn()
    {
        var frames = new[] { Cross(0, 0.10), Cross(50, 0.12), Cross(100, 0.16) };
        Assert.Equal(1, Run(Out4, frames).Count(c => c > 0));
        Assert.Equal(0, Run(In4, frames).Count(c => c > 0));
    }

    [Fact]
    public void FourFingersClose_FiresIn_NotOut()
    {
        var frames = new[] { Cross(0, 0.20), Cross(50, 0.17), Cross(100, 0.14) };
        Assert.Equal(1, Run(In4, frames).Count(c => c > 0));
        Assert.Equal(0, Run(Out4, frames).Count(c => c > 0));
    }

    [Fact]
    public void SmallChange_DoesNotFire()
    {
        var frames = new[] { Cross(0, 0.10), Cross(50, 0.13) };   // 0.03 しか開いていない
        Assert.Equal(0, Run(Out4, frames).Count(c => c > 0));
    }

    [Fact]
    public void WholeHandSwipes_DoesNotFire()
    {
        // 形はそのままで、右へ大きく動く (重心が動く)
        var frames = new[] { Cross(0, 0.10), Cross(50, 0.10, cx: 0.6), Cross(100, 0.10, cx: 0.7) };
        Assert.Equal(0, Run(Out4, frames).Count(c => c > 0));
        Assert.Equal(0, Run(In4, frames).Count(c => c > 0));
    }

    [Fact]
    public void AsymmetricPinch_WhereTheCentroidDrifts_StillFires()
    {
        // 実機の記録どおり、親指側だけが大きく動く: 開き具合は増えるが、重心も 0.05 ずれる (既定が 0.04 だった頃は発火しなかった)
        var frames = new[]
        {
            Cross(0, 0.10),
            F(50, (1, 0.20, 0.5), (2, 0.5, 0.4), (3, 0.6, 0.5), (4, 0.5, 0.6)),   // 左の指だけ 0.2 動く: 重心が 0.05 ずれる
        };
        var c = Run(Out4, frames);
        Assert.Equal(1, c.Count(x => x > 0));
    }

    [Fact]
    public void SpreadingWhileTheWholeHandTravelsFar_DoesNotFire()
    {
        // 開きながら、重心が 0.2 も動く: ピンチではなくスワイプ
        var frames = new[] { Cross(0, 0.10), Cross(50, 0.17, cx: 0.70) };
        Assert.Equal(0, Run(Out4, frames).Count(c => c > 0));
    }

    [Fact]
    public void Default_CentroidTolerance_IsTheTunedValue()
        => Assert.Equal(0.10, Out4.CentroidTolerance);

    [Fact]
    public void Rotation_DoesNotFire()
    {
        var frames = new[]
        {
            Cross(0, 0.10),
            F(50, (1, 0.5, 0.4), (2, 0.6, 0.5), (3, 0.5, 0.6), (4, 0.4, 0.5)),  // 90° 回転
        };
        Assert.Equal(0, Run(Out4, frames).Count(c => c > 0));
    }

    [Fact]
    public void WrongFingerCount_DoesNotFire()
    {
        var three = new[]
        {
            F(0, (1, 0.4, 0.5), (2, 0.5, 0.4), (3, 0.6, 0.5)),
            F(50, (1, 0.2, 0.5), (2, 0.5, 0.2), (3, 0.8, 0.5)),
        };
        Assert.Equal(0, Run(Out4, three).Count(c => c > 0));
    }

    [Fact]
    public void FingerAddedMidGesture_StartsOver()
    {
        var frames = new[]
        {
            Cross(0, 0.10),
            F(30, (1, 0.4, 0.5), (2, 0.5, 0.4), (3, 0.6, 0.5)),    // 1 本減った
            Cross(60, 0.10),                                        // また 4 本: 基準を取り直す
            Cross(90, 0.12),
        };
        Assert.Equal(0, Run(Out4, frames).Count(c => c > 0));
    }

    [Fact]
    public void FiresOncePerTouch_AndAgainAfterLifting()
    {
        var frames = new[]
        {
            Cross(0, 0.10), Cross(50, 0.17), Cross(100, 0.25), F(150),
            Cross(1000, 0.10), Cross(1050, 0.17),
        };
        Assert.Equal(2, Run(Out4, frames).Count(c => c > 0));
    }

    [Fact]
    public void Repeat_FiresEveryStep_AndCatchesUpOnBigJumps()
    {
        var spec = Out4 with { PinchDistance = 0.05, RepeatDistance = 0.03 };
        var c = Run(spec, new[]
        {
            Cross(0, 0.10), Cross(50, 0.14),    // 0.04: まだ
            Cross(100, 0.16),                    // 0.06: 最初
            Cross(150, 0.20),                    // 0.10: +0.03 を 1 回越えた → 1 回
            Cross(200, 0.30),                    // 0.20: さらに 3 回 (0.08, 0.11, 0.14, 0.17 のうち…)
        });
        Assert.Equal(0, c[1]);
        Assert.Equal(1, c[2]);
        Assert.Equal(1, c[3] );
        Assert.True(c[4] >= 3);
    }

    [Fact]
    public void Settle_UsesTheBaselineAfterTheWait()
    {
        var spec = Out4 with { SettleMs = 40 };
        var c = Run(spec, new[]
        {
            Cross(0, 0.05), Cross(20, 0.10),     // 着地直後のばらつき (基準ではない)
            Cross(45, 0.12),                      // ここで基準 (0.12)
            Cross(90, 0.15),                      // 0.03: まだ
        });
        Assert.Equal(0, c.Count(x => x > 0));
    }

    [Fact]
    public void InvalidSpec_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PinchRecognizer(new PinchSpec("x", 1, PinchDirection.In)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PinchRecognizer(Out4 with { PinchDistance = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PinchRecognizer(Out4 with { RepeatDistance = 0 }));
    }

    [Fact]
    public void Dispatcher_RunsThePinchAction()
    {
        var log = new List<string>();
        var d = new GestureDispatcher(new[] { new GestureBinding(Out4, new WheelAction(1, false)) }, new Fake(log));
        foreach (var f in new[] { Cross(0, 0.10), Cross(50, 0.17) }) d.Feed(f);
        Assert.Equal(new[] { "wheel:120" }, log);
    }

    sealed class Fake : IInputSender
    {
        readonly List<string> _l;
        public Fake(List<string> l) => _l = l;
        public void KeyDown(ushort vk) => _l.Add("down");
        public void KeyUp(ushort vk) => _l.Add("up");
        public void Wheel(int delta, bool horizontal) => _l.Add($"wheel:{delta}");
    }

    // ---- JSON 設定 ----

    [Fact]
    public void Config_ParsesPinch()
    {
        var b = GestureConfig.Parse("""
            { "bindings": [
              { "fingers": 4, "pinch": "Out", "repeatDistance": 0.03, "action": { "wheel": 1 } },
              { "fingers": 4, "pinch": "in", "pinchDistance": 0.07, "centroidTolerance": 0.05, "settleMs": 30,
                "action": { "wheel": -1 } } ] }
            """);
        Assert.Equal(new PinchSpec("4本指ピンチアウト", 4, PinchDirection.Out, RepeatDistance: 0.03), b[0].Pinch);
        Assert.Equal(new PinchSpec("4本指ピンチイン", 4, PinchDirection.In, 0.07, 0.05, 30), b[1].Pinch);
    }

    [Fact]
    public void Config_DuplicatePinch_IsReported()
    {
        var ex = Assert.Throws<GestureConfigException>(() => GestureConfig.Parse("""
            { "bindings": [
              { "fingers": 4, "pinch": "Out", "action": { "wheel": 1 } },
              { "fingers": 4, "pinch": "out", "action": { "wheel": 2 } } ] }
            """));
        Assert.Contains("重複", ex.Errors.Single());
    }

    [Theory]
    [InlineData("""{ "fingers": 4, "pinch": "Sideways" }""", "In / Out")]
    [InlineData("""{ "fingers": 1, "pinch": "In" }""", "2 本以上")]
    [InlineData("""{ "fingers": 4, "pinch": "In", "slot": 0 }""", "ピンチには")]
    [InlineData("""{ "fingers": 4, "pinch": "In", "tapSlot": 0 }""", "一緒に指定できません")]
    [InlineData("""{ "fingers": 4, "pinch": "In", "pinchDistance": 0 }""", "pinchDistance は 0 より大きい")]
    [InlineData("""{ "fingers": 4, "slot": 0, "direction": "Up", "pinchDistance": 0.1 }""", "pinch と一緒にだけ")]
    public void Config_BadPinch_IsReported(string binding, string expected)
    {
        var json = "{ \"bindings\": [ " + binding.TrimEnd('}', ' ') + ", \"action\": { \"hotkey\": \"A\" } } ] }";
        var ex = Assert.Throws<GestureConfigException>(() => GestureConfig.Parse(json));
        Assert.Contains(expected, string.Join("\n", ex.Errors));
    }
}
