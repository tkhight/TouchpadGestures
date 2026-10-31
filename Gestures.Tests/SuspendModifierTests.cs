using Gestures;
using Xunit;

namespace Gestures.Tests;

// 修飾キーを条件にした割り当ては、動作を送る間だけ、押している修飾キーを離す
public class SuspendModifierTests
{
    static readonly SlotSwipeSpec LeftRight = new("sw", 3, 0, SwipeDirection.Right, MinDistance: 0.10, SettleMs: 0);

    static Frame F(long t, double leftX) => new(t, new List<Contact>
    {
        new(1, leftX, 0.5), new(2, 0.5, 0.5), new(3, 0.8, 0.5),
    });

    static void Swipe(GestureDispatcher d)
    {
        d.Feed(F(0, 0.2));
        d.Feed(F(50, 0.2));
        d.Feed(F(100, 0.4));
    }

    sealed class Fake : IInputSender
    {
        public readonly List<string> Log = new();
        public void KeyDown(ushort vk) => Log.Add($"down:{vk:X2}");
        public void KeyUp(ushort vk) => Log.Add($"up:{vk:X2}");
        public void Wheel(int delta, bool horizontal) => Log.Add($"wheel:{delta}");
        public void MouseButtonEvent(MouseButton button, bool down) => Log.Add($"{(down ? "mousedown" : "mouseup")}:{button}");
        public void MoveCursor(int dx, int dy) { }
    }

    static List<string> Run(KeyModifiers held, KeyModifiers? bindingMods, string chord,
        Func<IReadOnlyList<ushort>>? heldKeys = null)
    {
        var f = new Fake();
        var d = new GestureDispatcher(new[] { new GestureBinding(LeftRight, new HotkeyAction(chord), null, bindingMods) },
            f, null, () => held, heldKeys);
        Swipe(d);
        return f.Log;
    }

    [Fact]
    public void Ctrl_IsReleasedWhileTheActionIsSent_AndPressedAgain()
    {
        var log = Run(KeyModifiers.Ctrl, KeyModifiers.Ctrl, "Win+Up");
        Assert.Equal(new[] { "up:A2", "down:5B", "down:26", "up:26", "up:5B", "down:A2" }, log);
    }

    [Fact]
    public void Shift_AndCtrl_AreBothReleasedAndRestored_InOrder()
    {
        var log = Run(KeyModifiers.Ctrl | KeyModifiers.Shift, KeyModifiers.Ctrl | KeyModifiers.Shift, "A");
        Assert.Equal(new[] { "up:A2", "up:A0", "down:41", "up:41", "down:A2", "down:A0" }, log);
    }

    [Fact]
    public void Win_And_Alt_AreMasked_BeforeBeingReleased()
    {
        var win = Run(KeyModifiers.Win, KeyModifiers.Win, "A");
        Assert.Equal(new[] { "down:E8", "up:E8", "up:5B", "down:41", "up:41", "down:5B" }, win);

        var alt = Run(KeyModifiers.Alt, KeyModifiers.Alt, "A");
        Assert.Equal(new[] { "down:E8", "up:E8", "up:A4", "down:41", "up:41", "down:A4" }, alt);
    }

    [Fact]
    public void Ctrl_AndShift_AreNotMasked()
    {
        var log = Run(KeyModifiers.Ctrl, KeyModifiers.Ctrl, "A");
        Assert.DoesNotContain("down:E8", log);
    }

    [Fact]
    public void ThePhysicalKeysThatWereHeld_AreUsed_WhenAProviderIsGiven()
    {
        var log = Run(KeyModifiers.Ctrl, KeyModifiers.Ctrl, "A", () => new ushort[] { 0xA3 });   // 右 Ctrl
        Assert.Equal(new[] { "up:A3", "down:41", "up:41", "down:A3" }, log);
    }

    [Fact]
    public void NoModifierCondition_LeavesTheHeldKeysAlone()
    {
        // 修飾キーを問わない割り当ては、これまでどおり、押している修飾キーはそのまま (離さない)
        var log = Run(KeyModifiers.Ctrl, null, "A");
        Assert.Equal(new[] { "down:41", "up:41" }, log);
    }

    [Fact]
    public void NoneCondition_HasNothingToRelease()
    {
        var log = Run(KeyModifiers.None, KeyModifiers.None, "A");
        Assert.Equal(new[] { "down:41", "up:41" }, log);
    }

    [Fact]
    public void ModifiersArePressedAgain_EvenWhenTheActionFails()
    {
        var f = new FailingKeys();
        var d = new GestureDispatcher(new[] { new GestureBinding(LeftRight, new HotkeyAction("A"), null, KeyModifiers.Ctrl) },
            f, null, () => KeyModifiers.Ctrl);
        Assert.Throws<InvalidOperationException>(() => Swipe(d));
        Assert.Equal("down:A2", f.Log[^1]);
    }

    sealed class FailingKeys : IInputSender
    {
        public readonly List<string> Log = new();
        public void KeyDown(ushort vk)
        {
            if (vk == 0x41) throw new InvalidOperationException("boom");
            Log.Add($"down:{vk:X2}");
        }
        public void KeyUp(ushort vk) => Log.Add($"up:{vk:X2}");
        public void Wheel(int delta, bool horizontal) { }
    }

    [Fact]
    public void AClickAction_Also_GetsTheModifiersReleased()
    {
        var f = new Fake();
        var d = new GestureDispatcher(new[] { new GestureBinding(LeftRight, new WheelAction(1), null, KeyModifiers.Ctrl) },
            f, null, () => KeyModifiers.Ctrl);
        Swipe(d);
        Assert.Equal(new[] { "up:A2", "wheel:120", "down:A2" }, f.Log);   // Ctrl + ホイール (ズーム) にならない
    }

    [Fact]
    public void HoldAction_ReleasesModifiersForTheWholeHold_AndRestoresAtTheEnd()
    {
        var f = new Fake();
        var hold = new HoldAction(MouseButton.Left, null, follow: false);
        var d = new GestureDispatcher(new[] { new GestureBinding(LeftRight, hold, null, KeyModifiers.Ctrl) },
            f, null, () => KeyModifiers.Ctrl);
        Swipe(d);
        Assert.Equal(new[] { "up:A2", "mousedown:Left" }, f.Log);   // 押している間は、Ctrl は離したまま
        d.Feed(new Frame(200, new List<Contact>()));                  // 指が全部離れた
        Assert.Equal(new[] { "up:A2", "mousedown:Left", "mouseup:Left", "down:A2" }, f.Log);
    }

    [Fact]
    public void ReleaseAll_RestoresTheModifiersToo()
    {
        var f = new Fake();
        var hold = new HoldAction(MouseButton.Left, null, follow: false);
        var d = new GestureDispatcher(new[] { new GestureBinding(LeftRight, hold, null, KeyModifiers.Ctrl) },
            f, null, () => KeyModifiers.Ctrl);
        Swipe(d);
        d.ReleaseAll();
        Assert.Equal("down:A2", f.Log[^1]);
    }
}
