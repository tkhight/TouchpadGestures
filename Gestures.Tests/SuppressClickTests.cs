using Gestures;
using Xunit;

namespace Gestures.Tests;

public class SuppressClickTests
{
    const string Json = """
        { "bindings": [
          { "fingers": 3, "slot": 0, "direction": "Left", "suppressClick": true, "action": { "hotkey": "A" } },
          { "fingers": 3, "slot": 0, "direction": "Right", "action": { "hotkey": "B" } }
        ] }
        """;

    [Fact]
    public void SuppressClick_ParsesCopiesAndRoundTrips()
    {
        var e = GestureConfig.ParseEntries(Json);
        Assert.True(e[0].SuppressClick);
        Assert.Null(e[1].SuppressClick);
        Assert.True(e[0].Clone().SuppressClick);
        var json = GestureConfig.ToJson(e);
        Assert.Contains("\"suppressClick\":true", json);
        var again = GestureConfig.ParseEntries(json);
        Assert.True(again[0].SuppressClick);
        Assert.Null(again[1].SuppressClick);
    }

    [Fact]
    public void SuppressClick_IsPassedToTheBinding_AndDefaultsToFalse()
    {
        var b = GestureConfig.Parse(Json);
        Assert.True(b[0].SuppressClick);
        Assert.False(b[1].SuppressClick);
    }

    sealed class Fake : IInputSender
    {
        public void KeyDown(ushort vk) { }
        public void KeyUp(ushort vk) { }
        public void Wheel(int delta, bool horizontal) { }
    }

    // 3 本指の左端の指を左へ動かして、左スワイプを発火させる。発火したフレームの LastSuppressClick を返す
    static bool Fires(string json, bool execute)
    {
        var d = new GestureDispatcher(GestureConfig.Parse(json), new Fake());
        double x = 0.9;
        for (long t = 0; t < 600; t += 10)
        {
            var fired = d.Feed(new Frame(t, new[] { new Contact(1, x, 0.5), new Contact(2, 0.95, 0.5), new Contact(3, 0.97, 0.5) }), execute);
            if (fired.Count > 0) return d.LastSuppressClick;
            x -= 0.02;
        }
        throw new Xunit.Sdk.XunitException("did not fire");
    }

    [Fact]
    public void Dispatcher_RaisesLastSuppressClick_OnlyForFlaggedBinding_WhenExecuting()
    {
        Assert.True(Fires(Json, execute: true));
        Assert.False(Fires(Json.Replace("\"suppressClick\": true, ", ""), execute: true));
        Assert.False(Fires(Json, execute: false));
    }

    // --- ClickGuard (フックから切り離した判定) ---
    const int LDown = 0x0201, LUp = 0x0202, RDown = 0x0204, RUp = 0x0205, Move = 0x0200;

    [Fact]
    public void ClickGuard_SwallowsClicksInsideTheWindow_AndTheirReleases()
    {
        var g = new ClickGuard();
        g.Arm(1000);
        Assert.True(g.ShouldSwallow(LDown, false, 1010));
        Assert.True(g.ShouldSwallow(LUp, false, 1020 + ClickGuard.WindowMs));   // 押下を捨てたので、離すも捨てる
        Assert.True(g.ShouldSwallow(RDown, false, 1100));
        Assert.True(g.ShouldSwallow(RUp, false, 1110));
    }

    [Fact]
    public void ClickGuard_PassesClicksOutsideTheWindow_AndNeverArmed()
    {
        var g = new ClickGuard();
        Assert.False(g.ShouldSwallow(LDown, false, 0));      // 発火がなければ捨てない
        Assert.False(g.ShouldSwallow(LUp, false, 1));
        g.Arm(1000);
        Assert.False(g.ShouldSwallow(LDown, false, 1000 + ClickGuard.WindowMs + 1));
        Assert.False(g.ShouldSwallow(LUp, false, 1000 + ClickGuard.WindowMs + 50));  // 通した押下の「離す」は通す
    }

    static (ClickGuard Guard, List<(bool Right, bool Down)> Sent) HoldGuard()
    {
        var sent = new List<(bool, bool)>();
        var g = new ClickGuard { Reinject = (r, d) => sent.Add((r, d)) };
        g.SetHoldFingers(new[] { 2 });
        return (g, sent);
    }

    [Fact]
    public void ClickGuard_ClickBeforeTheFire_IsHeldThenDropped()
    {
        var (g, sent) = HoldGuard();
        g.NoteContacts(2, 1000);                       // 対象の本数が触れた
        Assert.True(g.ShouldSwallow(LDown, false, 1100));   // クリックが発火より先に届く → 保留
        Assert.True(g.ShouldSwallow(LUp, false, 1105));
        g.Arm(1110);                                    // そのあと発火 → 保留したクリックは捨てる
        g.Tick(1500);
        Assert.Empty(sent);
    }

    [Fact]
    public void ClickGuard_HeldClickWithoutFire_IsSentAgain()
    {
        var (g, sent) = HoldGuard();
        g.NoteContacts(2, 1000);
        Assert.True(g.ShouldSwallow(LDown, false, 1100));
        Assert.True(g.ShouldSwallow(LUp, false, 1105));
        g.Tick(1100 + ClickGuard.HoldMs - 1);
        Assert.Empty(sent);                             // まだ待つ
        g.Tick(1100 + ClickGuard.HoldMs);
        Assert.Equal(new[] { (false, true), (false, false) }, sent);
        Assert.False(g.ShouldSwallow(LDown, false, 3000));  // 送り直したあとは通常どおり
    }

    [Fact]
    public void ClickGuard_UpArrivingAfterTheReinjectedDown_PassesThrough()
    {
        var (g, sent) = HoldGuard();
        g.NoteContacts(2, 1000);
        Assert.True(g.ShouldSwallow(LDown, false, 1100));
        g.Tick(1100 + ClickGuard.HoldMs);               // 押下だけ送り直す
        Assert.Equal(new[] { (false, true) }, sent);
        Assert.False(g.ShouldSwallow(LUp, false, 1300)); // 離すは通す (押しっぱなしにしない)
    }

    [Fact]
    public void ClickGuard_NoHold_WhenTheTargetFingerCountWasNotTouching()
    {
        var (g, _) = HoldGuard();
        g.NoteContacts(1, 1000);                       // 1 本だけ: 対象ではない
        Assert.False(g.ShouldSwallow(LDown, false, 1010));
        g.NoteContacts(2, 2000);
        Assert.False(g.ShouldSwallow(LUp, false, 2000));    // 通した押下の「離す」は通す
        Assert.False(g.ShouldSwallow(LDown, false, 2000 + ClickGuard.HoldWindowMs + 1));  // 保留の時間を過ぎている
    }

    [Fact]
    public void ClickGuard_NeverSwallowsInjectedOrOtherMessages()
    {
        var g = new ClickGuard();
        g.Arm(1000);
        Assert.False(g.ShouldSwallow(LDown, true, 1010));    // このアプリ自身の SendInput
        Assert.False(g.ShouldSwallow(Move, false, 1010));
    }
}
