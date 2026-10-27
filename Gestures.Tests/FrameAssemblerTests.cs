using Gestures;
using Xunit;

namespace Gestures.Tests;

public class FrameAssemblerTests
{
    static TouchSlot S(int id, int x, int y, bool tip = true) => new(id, x, y, tip);
    static readonly TouchSlot Pad = new(0, 0, 0, false);

    static PtpReport R(int? count, params TouchSlot[] slots) => new(count, null, slots);

    static int[] Ids(PtpFrameAssembler a) => a.Fingers.Select(f => f.Id).ToArray();

    [Fact]
    public void SingleReport_CompletesImmediately()
    {
        var a = new PtpFrameAssembler();
        Assert.True(a.Feed(R(2, S(1, 100, 200), S(2, 300, 400), Pad, Pad, Pad)));
        Assert.Equal(new[] { 1, 2 }, Ids(a));
        Assert.Equal(2, a.ContactCount);
    }

    [Fact]
    public void Hybrid_FiveFingersSplitOverThreeReports_CompletesOnTheLastOnly()
    {
        // 1 レポートに 2 スロット → 5 本は 3 レポートに分割される
        var a = new PtpFrameAssembler();
        Assert.False(a.Feed(R(5, S(1, 10, 10), S(2, 20, 20))));
        Assert.False(a.Feed(R(0, S(3, 30, 30), S(4, 40, 40))));
        Assert.True(a.Feed(R(0, S(5, 50, 50), Pad)));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, Ids(a));
        Assert.Equal(5, a.ContactCount);
    }

    [Fact]
    public void Hybrid_ExactMultipleOfSlots_CompletesOnTheLastFullReport()
    {
        var a = new PtpFrameAssembler();
        Assert.False(a.Feed(R(4, S(1, 1, 1), S(2, 2, 2))));
        Assert.True(a.Feed(R(0, S(3, 3, 3), S(4, 4, 4))));
        Assert.Equal(4, a.Fingers.Count);
    }

    [Fact]
    public void PaddingSlotsAreIgnored_EvenIfTheyLookTouched()
    {
        // 末尾の未使用スロットにゴミ (Tip=true) が入っていても、Contact Count を超えて読まない
        var a = new PtpFrameAssembler();
        Assert.True(a.Feed(R(1, S(7, 70, 70), S(0, 999, 999, tip: true), S(0, 888, 888, tip: true))));
        Assert.Equal(new[] { 7 }, Ids(a));
    }

    [Fact]
    public void LiftedFinger_IsCountedButNotReturned()
    {
        // 指を離すフレーム: Contact Count には含まれるが Tip=false
        var a = new PtpFrameAssembler();
        Assert.True(a.Feed(R(2, S(1, 10, 10), S(2, 20, 20, tip: false), Pad)));
        Assert.Equal(new[] { 1 }, Ids(a));
        Assert.Equal(2, a.ContactCount);
    }

    [Fact]
    public void LiftedFingerInContinuationReport_ShiftsNothing()
    {
        var a = new PtpFrameAssembler();
        Assert.False(a.Feed(R(3, S(1, 10, 10), S(2, 20, 20))));
        Assert.True(a.Feed(R(0, S(3, 30, 30, tip: false), Pad)));
        Assert.Equal(new[] { 1, 2 }, Ids(a));
    }

    [Fact]
    public void EmptyFrame_ClearsFingersAndCompletes()
    {
        var a = new PtpFrameAssembler();
        a.Feed(R(1, S(1, 10, 10), Pad));
        Assert.True(a.Feed(R(0, Pad, Pad)));
        Assert.Empty(a.Fingers);
        Assert.Equal(0, a.ContactCount);
    }

    [Fact]
    public void PreviousFrame_StaysVisibleWhileNextFrameIsIncomplete()
    {
        var a = new PtpFrameAssembler();
        a.Feed(R(1, S(1, 10, 10), Pad));
        Assert.False(a.Feed(R(3, S(4, 40, 40), S(5, 50, 50))));
        Assert.Equal(new[] { 1 }, Ids(a));            // まだ前のフレーム
        Assert.True(a.Feed(R(0, S(6, 60, 60), Pad)));
        Assert.Equal(new[] { 4, 5, 6 }, Ids(a));
    }

    [Fact]
    public void LostContinuationReport_NewFrameDiscardsTheIncompleteOne()
    {
        var a = new PtpFrameAssembler();
        Assert.False(a.Feed(R(5, S(1, 10, 10), S(2, 20, 20))));    // 続きが届かない
        Assert.True(a.Feed(R(2, S(8, 80, 80), S(9, 90, 90))));     // 次のフレームが始まる
        Assert.Equal(new[] { 8, 9 }, Ids(a));                      // 古い 1, 2 が混ざらない
    }

    [Fact]
    public void FingerCountChangesBetweenFrames()
    {
        var a = new PtpFrameAssembler();
        a.Feed(R(2, S(1, 10, 10), S(2, 20, 20)));
        a.Feed(R(1, S(2, 25, 25), Pad));
        Assert.Equal(new[] { 2 }, Ids(a));
        Assert.Equal(25, a.Fingers[0].X);
    }

    [Fact]
    public void NoContactCountInReport_EachReportIsOneFrame()
    {
        var a = new PtpFrameAssembler();
        Assert.True(a.Feed(R(null, S(1, 10, 10), S(2, 20, 20, tip: false))));
        Assert.Equal(new[] { 1 }, Ids(a));
        Assert.True(a.Feed(R(null, S(1, 11, 11), S(2, 21, 21))));
        Assert.Equal(new[] { 1, 2 }, Ids(a));
    }

    [Fact]
    public void ScanTime_IsTakenFromTheFirstReportOfAFrame()
    {
        var a = new PtpFrameAssembler();
        a.Feed(new PtpReport(3, 1000u, new[] { S(1, 1, 1), S(2, 2, 2) }));
        a.Feed(new PtpReport(0, 1234u, new[] { S(3, 3, 3), Pad }));   // 続きレポートの値は採用しない
        Assert.Equal(1000u, a.ScanTime);
    }

    [Fact]
    public void PositionsAreKeptPerSlot()
    {
        var a = new PtpFrameAssembler();
        a.Feed(R(2, S(3, 111, 222), S(5, 333, 444)));
        Assert.Equal(new TouchSlot(3, 111, 222, true), a.Fingers[0]);
        Assert.Equal(new TouchSlot(5, 333, 444, true), a.Fingers[1]);
    }
}
