using Gestures;
using Xunit;

namespace Gestures.Tests;

public class ModifierTests
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
    }

    // 「A」「B」のどちらが押されたかを、ログで見る
    static string Fire(KeyModifiers held, string? app, params GestureBinding[] bindings)
    {
        var f = new Fake();
        Swipe(new GestureDispatcher(bindings, f, () => app, () => held));
        return Letters(f);
    }

    // 動作として押されたキー (文字キー)。離した修飾キーの押し直し (0x80 以上) は含めない
    static string Letters(Fake f)
        => string.Concat(f.Log.Where(l => l.StartsWith("down:")).Select(l => Convert.ToInt32(l[5..], 16)).Where(c => c < 0x80).Select(c => (char)c));

    static GestureBinding Bind(string key, string? app = null, KeyModifiers? mods = null)
        => new(LeftRight, new HotkeyAction(key), app, mods);

    // ---- 名前の読み書き ----

    [Theory]
    [InlineData("Ctrl", KeyModifiers.Ctrl)]
    [InlineData("control", KeyModifiers.Ctrl)]
    [InlineData("Ctrl+Shift", KeyModifiers.Ctrl | KeyModifiers.Shift)]
    [InlineData("shift + ctrl", KeyModifiers.Ctrl | KeyModifiers.Shift)]
    [InlineData("Alt", KeyModifiers.Alt)]
    [InlineData("Win", KeyModifiers.Win)]
    [InlineData("Windows+Alt", KeyModifiers.Win | KeyModifiers.Alt)]
    [InlineData("none", KeyModifiers.None)]
    [InlineData("None", KeyModifiers.None)]
    public void Parse(string text, KeyModifiers expected) => Assert.Equal(expected, KeyModifierNames.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Banana")]
    [InlineData("none+Ctrl")]
    public void Parse_Invalid_Throws(string text) => Assert.Throws<FormatException>(() => KeyModifierNames.Parse(text));

    [Fact]
    public void Format_UsesAFixedOrder_AndRoundTrips()
    {
        Assert.Equal("Ctrl+Alt+Shift+Win", KeyModifierNames.Format(KeyModifiers.Win | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Ctrl));
        Assert.Equal("none", KeyModifierNames.Format(KeyModifiers.None));
        foreach (var m in Enum.GetValues<KeyModifiers>().Concat(Enumerable.Range(0, 16).Select(i => (KeyModifiers)i)))
            Assert.Equal(m, KeyModifierNames.Parse(KeyModifierNames.Format(m)));
    }

    [Fact]
    public void ParseOrNull()
    {
        Assert.Null(KeyModifierNames.ParseOrNull(null));
        Assert.Null(KeyModifierNames.ParseOrNull("  "));
        Assert.Equal(KeyModifiers.Ctrl, KeyModifierNames.ParseOrNull("Ctrl"));
    }

    // ---- 選び方 ----

    [Fact]
    public void WithoutModifiers_FiresWhateverIsHeld()
    {
        foreach (var held in new[] { KeyModifiers.None, KeyModifiers.Ctrl, KeyModifiers.Ctrl | KeyModifiers.Shift })
            Assert.Equal("A", Fire(held, null, Bind("A")));
    }

    [Fact]
    public void WithModifiers_FiresOnlyWhenExactlyThoseAreHeld()
    {
        var b = Bind("A", mods: KeyModifiers.Ctrl);
        Assert.Equal("A", Fire(KeyModifiers.Ctrl, null, b));
        Assert.Equal("", Fire(KeyModifiers.None, null, b));
        Assert.Equal("", Fire(KeyModifiers.Ctrl | KeyModifiers.Shift, null, b));   // 余計な修飾キーがあるときは働かない
        Assert.Equal("", Fire(KeyModifiers.Shift, null, b));
    }

    [Fact]
    public void None_FiresOnlyWhenNothingIsHeld()
    {
        var b = Bind("A", mods: KeyModifiers.None);
        Assert.Equal("A", Fire(KeyModifiers.None, null, b));
        Assert.Equal("", Fire(KeyModifiers.Ctrl, null, b));
    }

    [Fact]
    public void ModifierBinding_OverridesThePlainOne_OnlyWhenItMatches()
    {
        GestureBinding[] bs = { Bind("A"), Bind("B", mods: KeyModifiers.Ctrl) };
        Assert.Equal("B", Fire(KeyModifiers.Ctrl, null, bs));
        Assert.Equal("A", Fire(KeyModifiers.None, null, bs));
        Assert.Equal("A", Fire(KeyModifiers.Shift, null, bs));
        Assert.Equal("A", Fire(KeyModifiers.Ctrl | KeyModifiers.Shift, null, bs));   // Ctrl だけの条件には合わない
    }

    [Fact]
    public void DifferentModifierSets_CanBeBoundSeparately()
    {
        GestureBinding[] bs =
        {
            Bind("A", mods: KeyModifiers.Ctrl),
            Bind("B", mods: KeyModifiers.Shift),
            Bind("C", mods: KeyModifiers.Ctrl | KeyModifiers.Shift),
        };
        Assert.Equal("A", Fire(KeyModifiers.Ctrl, null, bs));
        Assert.Equal("B", Fire(KeyModifiers.Shift, null, bs));
        Assert.Equal("C", Fire(KeyModifiers.Ctrl | KeyModifiers.Shift, null, bs));
        Assert.Equal("", Fire(KeyModifiers.None, null, bs));
    }

    [Fact]
    public void AppAndModifiers_Priority()
    {
        GestureBinding[] bs =
        {
            Bind("A"),
            Bind("B", mods: KeyModifiers.Ctrl),
            Bind("C", app: "chrome"),
            Bind("D", app: "chrome", mods: KeyModifiers.Ctrl),
        };
        Assert.Equal("D", Fire(KeyModifiers.Ctrl, "chrome", bs));   // 両方が合う: 最優先
        Assert.Equal("C", Fire(KeyModifiers.None, "chrome", bs));   // アプリだけ
        Assert.Equal("B", Fire(KeyModifiers.Ctrl, "notepad", bs));  // 修飾キーだけ
        Assert.Equal("A", Fire(KeyModifiers.None, "notepad", bs));  // どちらも合わない: 条件なし
    }

    [Fact]
    public void ModifiersBeatApp_WhenBothAreIndividuallySpecific()
    {
        // 共通の Ctrl 割り当てと、修飾キー不問のアプリ別割り当て: Ctrl を押していれば Ctrl 側
        GestureBinding[] bs = { Bind("B", mods: KeyModifiers.Ctrl), Bind("C", app: "chrome") };
        Assert.Equal("B", Fire(KeyModifiers.Ctrl, "chrome", bs));
        Assert.Equal("C", Fire(KeyModifiers.None, "chrome", bs));
    }

    [Fact]
    public void Modifiers_AreOnlyLookedUp_WhenSomethingFires_AndAreReported()
    {
        int calls = 0;
        var d = new GestureDispatcher(new[] { Bind("A") }, new Fake(), null, () => { calls++; return KeyModifiers.Alt; });
        d.Feed(F(0, 0.2));
        d.Feed(F(50, 0.2));
        Assert.Equal(0, calls);
        d.Feed(F(100, 0.4));
        Assert.Equal(1, calls);
        Assert.Equal(KeyModifiers.Alt, d.LastModifiers);
    }

    [Fact]
    public void WithoutAModifierProvider_NothingIsConsideredHeld()
    {
        var f = new Fake();
        Swipe(new GestureDispatcher(new[] { Bind("A", mods: KeyModifiers.Ctrl), Bind("B", mods: KeyModifiers.None) }, f));
        Assert.Equal("B", Letters(f));
    }

    [Fact]
    public void SameGestureWithDifferentModifiers_IsNotADuplicate_ButTheSameOneIs()
    {
        _ = new GestureDispatcher(new[] { Bind("A", mods: KeyModifiers.Ctrl), Bind("B", mods: KeyModifiers.Alt), Bind("C") }, new Fake());
        Assert.Throws<ArgumentException>(() =>
            new GestureDispatcher(new[] { Bind("A", mods: KeyModifiers.Ctrl), Bind("B", mods: KeyModifiers.Ctrl) }, new Fake()));
    }

    // ---- JSON 設定 ----

    [Fact]
    public void Config_ParsesModifiers()
    {
        var b = GestureConfig.Parse("""
            { "bindings": [
              { "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "A" } },
              { "modifiers": "Ctrl", "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "B" } },
              { "modifiers": "shift+ctrl", "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "C" } },
              { "modifiers": "none", "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "D" } } ] }
            """);
        Assert.Equal(new KeyModifiers?[] { null, KeyModifiers.Ctrl, KeyModifiers.Ctrl | KeyModifiers.Shift, KeyModifiers.None },
            b.Select(x => x.Modifiers));
    }

    [Fact]
    public void Config_ModifiersWorkWithAppAndEveryKind()
    {
        var b = GestureConfig.Parse("""
            { "bindings": [
              { "app": "chrome", "modifiers": "Alt", "fingers": 3, "tapSlot": 0, "action": { "hotkey": "A" } },
              { "modifiers": "Alt", "fingers": 4, "pinch": "Out", "action": { "hotkey": "B" } },
              { "modifiers": "Alt", "fingers": 3, "landOrder": [0, 1, 2], "action": { "hotkey": "C" } } ] }
            """);
        Assert.All(b, x => Assert.Equal(KeyModifiers.Alt, x.Modifiers));
        Assert.Equal("chrome", b[0].App);
    }

    [Fact]
    public void Config_DuplicateIsPerModifierSet()
    {
        Assert.Equal(2, GestureConfig.Parse("""
            { "bindings": [
              { "modifiers": "Ctrl", "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "A" } },
              { "modifiers": "Alt", "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "B" } } ] }
            """).Count);
        var ex = Assert.Throws<GestureConfigException>(() => GestureConfig.Parse("""
            { "bindings": [
              { "modifiers": "Ctrl+Shift", "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "A" } },
              { "modifiers": "shift+Ctrl", "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "B" } } ] }
            """));
        Assert.Contains("重複", ex.Errors.Single());
    }

    [Theory]
    [InlineData("Banana", "Unknown modifier")]
    [InlineData("", "modifiers が空")]
    [InlineData("Ctrl+", "Unknown modifier")]
    public void Config_BadModifiers_IsReported(string text, string expected)
    {
        var json = "{ \"bindings\": [ { \"modifiers\": \"" + text + "\", \"fingers\": 3, \"slot\": 0, \"direction\": \"Left\", \"action\": { \"hotkey\": \"A\" } } ] }";
        var ex = Assert.Throws<GestureConfigException>(() => GestureConfig.Parse(json));
        Assert.Contains(expected, string.Join("\n", ex.Errors));
    }

    [Fact]
    public void Editing_RoundTrip_CloneAndLabel()
    {
        var entries = GestureConfig.ParseEntries("""
            { "bindings": [ { "modifiers": "Ctrl+Shift", "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "A" } },
                            { "fingers": 3, "slot": 1, "direction": "Left", "action": { "hotkey": "A" } },
                            { "modifiers": "none", "fingers": 3, "slot": 2, "direction": "Left", "action": { "hotkey": "A" } },
                            { "modifiers": "Nope", "fingers": 3, "slot": 2, "direction": "Up", "action": { "hotkey": "A" } } ] }
            """);
        Assert.Equal("(Ctrl+Shift) ", BindingEditing.ModifiersLabel(entries[0]));
        Assert.Equal("", BindingEditing.ModifiersLabel(entries[1]));
        Assert.Equal("(none) ", BindingEditing.ModifiersLabel(entries[2]));
        Assert.Equal("(Nope?) ", BindingEditing.ModifiersLabel(entries[3]));
        Assert.Equal("Ctrl+Shift", entries[0].Clone().Modifiers);
        Assert.Equal("Ctrl+Shift", GestureConfig.ParseEntries(GestureConfig.ToJson(entries))[0].Modifiers);
    }
}
