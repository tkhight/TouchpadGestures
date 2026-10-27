using Gestures;
using Xunit;

namespace Gestures.Tests;

public class ActionTests
{
    /// <summary>実際には送信せず、呼び出しを記録する。</summary>
    sealed class FakeSender : IInputSender
    {
        public List<string> Log { get; } = new();
        public int ThrowOnKeyDownCall { get; set; }
        int _keyDownCalls;

        public void KeyDown(ushort vk)
        {
            if (++_keyDownCalls == ThrowOnKeyDownCall) throw new InvalidOperationException("boom");
            Log.Add($"down:{vk:X2}");
        }

        public void KeyUp(ushort vk) => Log.Add($"up:{vk:X2}");

        public void Wheel(int delta, bool horizontal) => Log.Add($"{(horizontal ? "hwheel" : "wheel")}:{delta}");
    }

    static Frame F(long t, params (int id, double x, double y)[] cs)
        => new(t, cs.Select(c => new Contact(c.id, c.x, c.y)).ToList());

    [Fact]
    public void Hotkey_PressesInOrder_ReleasesInReverse()
    {
        var s = new FakeSender();
        new HotkeyAction("Ctrl+Alt+Right").Execute(s);
        Assert.Equal(new[] { "down:11", "down:12", "down:27", "up:27", "up:12", "up:11" }, s.Log);
    }

    [Fact]
    public void Hotkey_ParsingIsCaseAndSpaceInsensitive()
    {
        var s = new FakeSender();
        new HotkeyAction("ctrl + shift + t").Execute(s);
        Assert.Equal(new[] { "down:11", "down:10", "down:54", "up:54", "up:10", "up:11" }, s.Log);
    }

    [Theory]
    [InlineData("F1", 0x70)]
    [InlineData("f12", 0x7B)]
    [InlineData("F24", 0x87)]
    [InlineData("a", 0x41)]
    [InlineData("5", 0x35)]
    [InlineData("VolumeUp", 0xAF)]
    public void KeyNames_Parse(string name, int expected)
        => Assert.Equal((ushort)expected, KeyNames.Parse(name));

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Banana")]
    [InlineData("F25")]
    public void Hotkey_InvalidChord_Throws(string chord)
        => Assert.Throws<FormatException>(() => new HotkeyAction(chord));

    [Fact]
    public void Hotkey_ReleasesPressedKeys_EvenWhenSendFails()
    {
        var s = new FakeSender { ThrowOnKeyDownCall = 3 };
        Assert.Throws<InvalidOperationException>(() => new HotkeyAction("Ctrl+Alt+Right").Execute(s));
        Assert.Equal(new[] { "down:11", "down:12", "up:12", "up:11" }, s.Log);
    }

    [Fact]
    public void Wheel_UsesNotchesTimes120()
    {
        var s = new FakeSender();
        new WheelAction(3).Execute(s);
        new WheelAction(-1, horizontal: true).Execute(s);
        Assert.Equal(new[] { "wheel:360", "hwheel:-120" }, s.Log);
    }

    [Fact]
    public void Dispatcher_ExecutesOnlyTheBoundGesture()
    {
        var s = new FakeSender();
        var leftRight = new SlotSwipeSpec("left-right", 3, 0, SwipeDirection.Right);
        var rightUp = new SlotSwipeSpec("right-up", 3, 2, SwipeDirection.Up);
        var d = new GestureDispatcher(new[]
        {
            new GestureBinding(leftRight, new HotkeyAction("Alt+Right")),
            new GestureBinding(rightUp, new HotkeyAction("VolumeUp")),
        }, s);

        var fired = new List<string>();
        foreach (var f in new[]
        {
            F(0,   (1, 0.2, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5)),
            F(50,  (1, 0.2, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5)),
            F(100, (1, 0.2, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5)),
            F(150, (1, 0.4, 0.5), (2, 0.4, 0.5), (3, 0.7, 0.5)),
        })
            fired.AddRange(d.Feed(f).Select(x => x.Name));

        Assert.Equal(new[] { "left-right" }, fired);
        Assert.Equal(new[] { "down:12", "down:27", "up:27", "up:12" }, s.Log);
    }

    [Fact]
    public void Dispatcher_NoGesture_SendsNothing()
    {
        var s = new FakeSender();
        var d = new GestureDispatcher(new[]
        {
            new GestureBinding(new SlotSwipeSpec("x", 3, 0, SwipeDirection.Right), new HotkeyAction("A")),
        }, s);
        d.Feed(F(0, (1, 0.2, 0.5)));
        Assert.Empty(s.Log);
    }

    [Fact]
    public void Dispatcher_DuplicateSpec_Throws()
    {
        var spec = new SlotSwipeSpec("x", 3, 0, SwipeDirection.Right);
        Assert.Throws<ArgumentException>(() => new GestureDispatcher(new[]
        {
            new GestureBinding(spec, new HotkeyAction("A")),
            new GestureBinding(spec, new HotkeyAction("B")),
        }, new FakeSender()));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Win32_InputStructHasNativeSize()
    {
        // SendInput は sizeof(INPUT) が合わないと失敗する (x64: 40, x86: 28)
        Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Win32InputSender.InputSize);
    }
}
