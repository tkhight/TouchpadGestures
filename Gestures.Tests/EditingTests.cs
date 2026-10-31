using Gestures;
using Xunit;

namespace Gestures.Tests;

public class EditingTests
{
    // Motions が配列なので、記録型の等価比較は使えない。名前・キー・しきい値で比べる
    static string Same(GestureBinding b) => b.Spec switch
    {
        SlotSwipeSpec s => $"{s.Name}|{s.Key}|{s.MinDistance}|{s.FixedTolerance}|{s.SettleMs}|{s.RepeatDistance}",
        var o => o.Name + "|" + o.Key + "|" + o,
    };

    static BindingEntry Entry(string json) => GestureConfig.ParseEntries("{ \"bindings\": [ " + json + " ] }")[0];

    // ---- ToJson / ParseEntries ----

    [Fact]
    public void Json_RoundTrips_AllKinds()
    {
        const string src = """
            { "bindings": [
              { "name": "戻る", "app": "chrome", "fingers": 3, "slot": 0, "direction": "Left", "minDistance": 0.08,
                "action": { "hotkey": "Alt+Left" } },
              { "fingers": 3, "motions": ["none", "Down", "Down"], "repeatDistance": 0.03, "action": { "wheel": -1, "horizontal": true } },
              { "fingers": 3, "tapSlot": 0, "tapCount": 2, "maxTapMs": 200, "action": { "hotkey": "A" } },
              { "fingers": 4, "pinch": "Out", "pinchDistance": 0.04, "action": { "hotkey": "Ctrl+NumAdd" } } ] }
            """;
        var entries = GestureConfig.ParseEntries(src);
        var json = GestureConfig.ToJson(entries);

        var a = GestureConfig.Parse(src);
        var b = GestureConfig.Parse(json);
        Assert.Equal(a.Select(Same), b.Select(Same));
        Assert.Equal(a.Select(x => x.App), b.Select(x => x.App));
        Assert.Equal(a.Select(x => x.Action.Description), b.Select(x => x.Action.Description));
    }

    [Fact]
    public void ToJson_WritesOneBindingPerLine_WithJapaneseUnescaped_AndNoNulls()
    {
        var json = GestureConfig.ToJson(GestureConfig.ParseEntries("""
            { "bindings": [ { "name": "戻る", "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "A" } } ] }
            """));
        Assert.Contains("\"name\":\"戻る\"", json);
        Assert.DoesNotContain("null", json);
        Assert.Equal(1, json.Split('\n').Count(l => l.Contains("\"fingers\"")));
    }

    [Fact]
    public void ToJson_EmptyList_IsValid()
    {
        var json = GestureConfig.ToJson(new List<BindingEntry>());
        Assert.Empty(GestureConfig.Parse(json));
    }

    [Fact]
    public void ParseEntries_DoesNotValidateContent()
    {
        // 検証は Build で行う。編集の途中の不完全な内容も読める
        var e = GestureConfig.ParseEntries("""{ "bindings": [ { "fingers": 3 } ] }""");
        Assert.Single(e);
        Assert.Throws<GestureConfigException>(() => GestureConfig.Build(e));
    }

    [Fact]
    public void ParseEntries_StillRejectsBrokenJson()
        => Assert.Throws<GestureConfigException>(() => GestureConfig.ParseEntries("{ not json"));

    [Fact]
    public void Clone_IsIndependent()
    {
        var e = Entry("""{ "fingers": 3, "motions": ["none","Up","Up"], "action": { "hotkey": "A" } }""");
        var c = e.Clone();
        c.Motions![0] = "Left";
        c.Action!.Hotkey = "B";
        Assert.Equal("none", e.Motions![0]);
        Assert.Equal("A", e.Action!.Hotkey);
    }

    // ---- 種類・動き ----

    [Fact]
    public void KindOf()
    {
        Assert.Equal(GestureKind.Swipe, BindingEditing.KindOf(Entry("""{ "fingers": 3, "slot": 0, "direction": "Up" }""")));
        Assert.Equal(GestureKind.Tap, BindingEditing.KindOf(Entry("""{ "fingers": 3, "tapSlot": 0 }""")));
        Assert.Equal(GestureKind.Pinch, BindingEditing.KindOf(Entry("""{ "fingers": 4, "pinch": "In" }""")));
    }

    [Fact]
    public void GetMotions_ReadsBothStyles()
    {
        var one = BindingEditing.GetMotions(Entry("""{ "fingers": 3, "slot": 1, "direction": "Down" }"""));
        Assert.Equal(new SwipeDirection?[] { null, SwipeDirection.Down, null }, one);

        var many = BindingEditing.GetMotions(Entry("""{ "fingers": 3, "motions": ["none","Up","Up"] }"""));
        Assert.Equal(new SwipeDirection?[] { null, SwipeDirection.Up, SwipeDirection.Up }, many);
    }

    [Fact]
    public void GetMotions_ToleratesIncompleteEntries()
    {
        Assert.Empty(BindingEditing.GetMotions(Entry("""{ }""")));
        Assert.Equal(new SwipeDirection?[3], BindingEditing.GetMotions(Entry("""{ "fingers": 3, "slot": 9, "direction": "Up" }""")));
    }

    [Fact]
    public void SetMotions_OneMovingFinger_UsesSlotAndDirection()
    {
        var e = new BindingEntry { Fingers = 3, Motions = new() { "Up" } };
        BindingEditing.SetMotions(e, new SwipeDirection?[] { null, null, SwipeDirection.Left });
        Assert.Equal(2, e.Slot);
        Assert.Equal("Left", e.Direction);
        Assert.Null(e.Motions);
    }

    [Fact]
    public void SetMotions_SeveralMovingFingers_UsesMotions()
    {
        var e = new BindingEntry { Fingers = 3, Slot = 0, Direction = "Up" };
        BindingEditing.SetMotions(e, new SwipeDirection?[] { null, SwipeDirection.Down, SwipeDirection.Down });
        Assert.Null(e.Slot);
        Assert.Null(e.Direction);
        Assert.Equal(new[] { "none", "Down", "Down" }, e.Motions);
    }

    [Fact]
    public void SetMotions_NoMovingFinger_IsReportedByValidation()
    {
        var e = new BindingEntry { Fingers = 3, Action = new ActionEntry { Hotkey = "A" } };
        BindingEditing.SetMotions(e, new SwipeDirection?[3]);
        var ex = Assert.Throws<GestureConfigException>(() => GestureConfig.Build(new[] { e }));
        Assert.Contains("動かす指が 1 本もありません", ex.Errors.Single());
    }

    [Fact]
    public void SetMotions_ThenBuild_MatchesTheEquivalentJson()
    {
        var e = new BindingEntry { Fingers = 3, Action = new ActionEntry { Hotkey = "A" } };
        BindingEditing.SetMotions(e, new SwipeDirection?[] { null, SwipeDirection.Down, SwipeDirection.Down });
        var built = GestureConfig.Build(new[] { e })[0];
        var expected = GestureConfig.Parse("""{ "bindings": [ { "fingers": 3, "motions": ["none","Down","Down"], "action": { "hotkey": "A" } } ] }""")[0];
        Assert.Equal(Same(expected), Same(built));
    }

    [Fact]
    public void ClearGesture_KeepsNameAppAndAction()
    {
        var e = Entry("""{ "name": "x", "app": "chrome", "fingers": 3, "slot": 0, "direction": "Up", "minDistance": 0.1, "action": { "hotkey": "A" } }""");
        BindingEditing.ClearGesture(e);
        Assert.Equal("x", e.Name);
        Assert.Equal("chrome", e.App);
        Assert.Equal(3, e.Fingers);
        Assert.Equal("A", e.Action!.Hotkey);
        Assert.Null(e.Slot); Assert.Null(e.Direction); Assert.Null(e.MinDistance);
    }

    [Fact]
    public void GetPinchDirection()
    {
        Assert.Equal(PinchDirection.Out, BindingEditing.GetPinchDirection(Entry("""{ "pinch": " out " }""")));
        Assert.Null(BindingEditing.GetPinchDirection(Entry("""{ "pinch": "x" }""")));
        Assert.Null(BindingEditing.GetPinchDirection(Entry("""{ }""")));
    }

    // ---- 表示 ----

    [Fact]
    public void DisplayName_UsesNameOrDefaultOrPlaceholder()
    {
        Assert.Equal("戻る", BindingEditing.DisplayName(Entry("""{ "name": "戻る" }""")));
        Assert.Equal("3本指・左から1番目だけ←",
            BindingEditing.DisplayName(Entry("""{ "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "A" } }""")));
        Assert.Equal("(未完成)", BindingEditing.DisplayName(Entry("""{ "fingers": 3 }""")));
    }

    [Fact]
    public void DescribeAction()
    {
        Assert.Equal("Ctrl+B", BindingEditing.DescribeAction(Entry("""{ "action": { "hotkey": "Ctrl+B" } }""")));
        Assert.Equal("ホイール -1", BindingEditing.DescribeAction(Entry("""{ "action": { "wheel": -1 } }""")));
        Assert.Equal("横ホイール 2", BindingEditing.DescribeAction(Entry("""{ "action": { "wheel": 2, "horizontal": true } }""")));
        Assert.Equal("", BindingEditing.DescribeAction(Entry("""{ }""")));
    }

    // ---- KeyNames.NameOf ----

    [Theory]
    [InlineData(0x11, "Ctrl")]
    [InlineData(0x1B, "Esc")]
    [InlineData(0x08, "Backspace")]
    [InlineData(0x2E, "Delete")]
    [InlineData(0x42, "B")]
    [InlineData(0x35, "5")]
    [InlineData(0x73, "F4")]
    [InlineData(0x6B, "NumAdd")]
    [InlineData(0x27, "Right")]
    public void NameOf(int vk, string expected) => Assert.Equal(expected, KeyNames.NameOf((ushort)vk));

    [Fact]
    public void NameOf_UnknownKey_IsNull() => Assert.Null(KeyNames.NameOf(0xFF));

    [Fact]
    public void NameOf_RoundTripsThroughParse()
    {
        for (ushort vk = 1; vk < 0xFF; vk++)
        {
            var name = KeyNames.NameOf(vk);
            if (name is null) continue;
            Assert.Equal(vk, KeyNames.Parse(name));
        }
    }
}
