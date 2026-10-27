using Gestures;
using Xunit;

namespace Gestures.Tests;

public class SlotSwipeTests
{
    // 3本指・左(スロット0)だけ右へスワイプ
    static readonly SlotSwipeSpec LeftRight = new("left-right", 3, 0, SwipeDirection.Right);

    static Frame F(long t, params (int id, double x, double y)[] cs)
        => new(t, cs.Select(c => new Contact(c.id, c.x, c.y)).ToList());

    /// <summary>フレームを順に食わせ、発火した回数を返す。</summary>
    static int Run(SlotSwipeSpec spec, IEnumerable<Frame> frames)
    {
        var r = new SlotSwipeRecognizer(spec);
        return frames.Count(r.Feed);
    }

    /// <summary>3本が揃って落ち着くまでの導入フレーム(ID は 1,2,3 = 左,中,右)。</summary>
    static IEnumerable<Frame> Landing(double l = 0.2, double m = 0.4, double r = 0.7)
    {
        yield return F(0, (1, l, 0.5), (2, m, 0.5), (3, r, 0.5));
        yield return F(50, (1, l, 0.5), (2, m, 0.5), (3, r, 0.5));
        yield return F(100, (1, l, 0.5), (2, m, 0.5), (3, r, 0.5));
    }

    [Fact]
    public void LeftFingerSwipesRight_OthersFixed_Fires()
    {
        var frames = Landing().Concat(new[]
        {
            F(150, (1, 0.28, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5)),
            F(200, (1, 0.40, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5)),
        });
        Assert.Equal(1, Run(LeftRight, frames));
    }

    [Fact]
    public void MiddleFingerAlsoMoves_DoesNotFire()
    {
        var frames = Landing().Concat(new[]
        {
            F(150, (1, 0.30, 0.5), (2, 0.50, 0.5), (3, 0.7, 0.5)),
            F(200, (1, 0.45, 0.5), (2, 0.60, 0.5), (3, 0.7, 0.5)),
        });
        Assert.Equal(0, Run(LeftRight, frames));
    }

    [Fact]
    public void FixedFingerJitterWithinTolerance_StillFires()
    {
        var frames = Landing().Concat(new[]
        {
            F(150, (1, 0.30, 0.5), (2, 0.41, 0.51), (3, 0.69, 0.5)),
            F(200, (1, 0.40, 0.5), (2, 0.42, 0.50), (3, 0.70, 0.49)),
        });
        Assert.Equal(1, Run(LeftRight, frames));
    }

    [Fact]
    public void MovingFingerCrossesAnotherFinger_SlotIsLatched_StillFires()
    {
        // 左の指が中の指(x=0.4)を追い越して 0.5 まで進む。X 順位は途中で入れ替わるが、
        // スロットは基準時点で確定しているので「左の指」のまま。
        var frames = Landing().Concat(new[]
        {
            F(150, (1, 0.35, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5)),
            F(200, (1, 0.50, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5)),
        });
        Assert.Equal(1, Run(LeftRight, frames));
    }

    [Fact]
    public void SlotIsDecidedByX_NotById()
    {
        // ID の並びがバラバラでも、X が最小の指が「左」
        var frames = new[]
        {
            F(0,   (9, 0.2, 0.5), (4, 0.4, 0.5), (7, 0.7, 0.5)),
            F(50,  (9, 0.2, 0.5), (4, 0.4, 0.5), (7, 0.7, 0.5)),
            F(100, (9, 0.2, 0.5), (4, 0.4, 0.5), (7, 0.7, 0.5)),
            F(150, (9, 0.4, 0.5), (4, 0.4, 0.5), (7, 0.7, 0.5)),
        };
        Assert.Equal(1, Run(LeftRight, frames));
    }

    [Fact]
    public void MiddleSlotMoving_DoesNotFireLeftSpec_ButFiresMiddleSpec()
    {
        var middleDown = new SlotSwipeSpec("middle-down", 3, 1, SwipeDirection.Down);
        var frames = Landing().Concat(new[]
        {
            F(150, (1, 0.2, 0.5), (2, 0.4, 0.58), (3, 0.7, 0.5)),
            F(200, (1, 0.2, 0.5), (2, 0.4, 0.72), (3, 0.7, 0.5)),
        }).ToList();
        Assert.Equal(0, Run(LeftRight, frames));
        Assert.Equal(1, Run(middleDown, frames));
    }

    [Fact]
    public void WrongDirection_DoesNotFire()
    {
        var frames = Landing().Concat(new[]
        {
            F(150, (1, 0.12, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5)),
            F(200, (1, 0.02, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5)),
        });
        Assert.Equal(0, Run(LeftRight, frames));
    }

    [Fact]
    public void TooShort_DoesNotFire()
    {
        var frames = Landing().Concat(new[]
        {
            F(150, (1, 0.25, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5)),
        });
        Assert.Equal(0, Run(LeftRight, frames));
    }

    [Fact]
    public void MostlyVertical_DoesNotFireHorizontalSpec()
    {
        var frames = Landing().Concat(new[]
        {
            F(150, (1, 0.36, 0.80), (2, 0.4, 0.5), (3, 0.7, 0.5)),
        });
        Assert.Equal(0, Run(LeftRight, frames));
    }

    [Fact]
    public void FiresOncePerTouch_AndAgainAfterLiftingAll()
    {
        var swipe = Landing().Concat(new[]
        {
            F(150, (1, 0.40, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5)),
            F(200, (1, 0.50, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5)), // 発火後もさらに進む
            F(250, (1, 0.60, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5)),
            F(300),                                               // 全員離れた
        });
        var second = Landing().Select(f => f with { TimeMs = f.TimeMs + 1000 }).Concat(new[]
        {
            F(1150, (1, 0.40, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5)),
        });
        Assert.Equal(2, Run(LeftRight, swipe.Concat(second)));
    }

    [Fact]
    public void FingerCountMismatch_DoesNotFire()
    {
        var four = new[]
        {
            F(0,   (1, 0.2, 0.5), (2, 0.4, 0.5), (3, 0.6, 0.5), (4, 0.8, 0.5)),
            F(50,  (1, 0.2, 0.5), (2, 0.4, 0.5), (3, 0.6, 0.5), (4, 0.8, 0.5)),
            F(100, (1, 0.2, 0.5), (2, 0.4, 0.5), (3, 0.6, 0.5), (4, 0.8, 0.5)),
            F(150, (1, 0.5, 0.5), (2, 0.4, 0.5), (3, 0.6, 0.5), (4, 0.8, 0.5)),
        };
        Assert.Equal(0, Run(LeftRight, four));
    }

    [Fact]
    public void FingerAddedMidGesture_CancelsAndRestarts()
    {
        var frames = Landing().Concat(new[]
        {
            F(150, (1, 0.30, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5)),
            F(200, (1, 0.30, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5), (4, 0.9, 0.5)), // 4本目
            F(250, (1, 0.50, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5), (4, 0.9, 0.5)),
        });
        Assert.Equal(0, Run(LeftRight, frames));
    }

    [Fact]
    public void LandingMotionBeforeSettle_IsIgnored()
    {
        // 着地直後(SettleMs=40 未満)に指が動いても、基準位置は落ち着いた後の位置
        var frames = new[]
        {
            F(0,  (1, 0.10, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5)),
            F(20, (1, 0.18, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5)),
            F(45, (1, 0.20, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5)), // ここで基準確定
            F(90, (1, 0.25, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5)), // 0.05 しか進んでいない
        };
        Assert.Equal(0, Run(LeftRight, frames));
    }

    [Fact]
    public void Engine_ReturnsOnlyMatchingSpecs()
    {
        var engine = new GestureEngine(new[]
        {
            LeftRight,
            new SlotSwipeSpec("right-left", 3, 2, SwipeDirection.Left),
        });
        var fired = new List<string>();
        foreach (var f in Landing().Concat(new[] { F(150, (1, 0.2, 0.5), (2, 0.4, 0.5), (3, 0.5, 0.5)) }))
            fired.AddRange(engine.Feed(f).Select(s => s.Name));
        Assert.Equal(new[] { "right-left" }, fired);
    }

    [Fact]
    public void InvalidSpec_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SlotSwipeRecognizer(new SlotSwipeSpec("bad", 3, 3, SwipeDirection.Right)));
    }
}
