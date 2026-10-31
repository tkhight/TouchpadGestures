using Gestures;
using Xunit;

namespace Gestures.Tests;

public class ConfigTests
{
    sealed class FakeSender : IInputSender
    {
        public List<string> Log { get; } = new();
        public void KeyDown(ushort vk) => Log.Add($"down:{vk:X2}");
        public void KeyUp(ushort vk) => Log.Add($"up:{vk:X2}");
        public void Wheel(int delta, bool horizontal) => Log.Add($"{(horizontal ? "hwheel" : "wheel")}:{delta}");
    }

    static string[] Errors(string json)
        => Assert.Throws<GestureConfigException>(() => GestureConfig.Parse(json)).Errors.ToArray();

    [Fact]
    public void SampleJson_Parses()
    {
        var b = GestureConfig.Parse(GestureConfig.SampleJson);
        Assert.Equal(6, b.Count);
        Assert.Equal("3本指・左だけ←", b[0].Swipe!.Name);
    }

    [Fact]
    public void Parses_HotkeyAndWheel_WithDefaults()
    {
        var b = GestureConfig.Parse("""
            { "bindings": [
              { "fingers": 3, "slot": 0, "direction": "right", "action": { "hotkey": "Alt+Right" } },
              { "fingers": 4, "slot": 3, "direction": "Down",  "action": { "wheel": -2, "horizontal": true } }
            ] }
            """);

        Assert.Equal(new SlotSwipeSpec("3本指・左から1番目だけ→", 3, 0, SwipeDirection.Right), b[0].Spec);
        Assert.Equal(new SlotSwipeSpec("4本指・左から4番目だけ↓", 4, 3, SwipeDirection.Down), b[1].Spec);

        var s = new FakeSender();
        b[0].Action.Execute(s);
        b[1].Action.Execute(s);
        Assert.Equal(new[] { "down:12", "down:27", "up:27", "up:12", "hwheel:-240" }, s.Log);
    }

    [Fact]
    public void CustomThresholds_AreApplied()
    {
        var b = GestureConfig.Parse("""
            { "bindings": [ { "fingers": 3, "slot": 1, "direction": "Up",
              "minDistance": 0.3, "fixedTolerance": 0.05, "settleMs": 80,
              "action": { "hotkey": "F5" } } ] }
            """);
        Assert.Equal(0.3, b[0].Swipe!.MinDistance);
        Assert.Equal(0.05, b[0].Swipe!.FixedTolerance);
        Assert.Equal(80, b[0].Swipe!.SettleMs);
    }

    [Fact]
    public void CommentsAndTrailingCommas_AreAllowed()
    {
        var b = GestureConfig.Parse("""
            {
              // コメント
              "bindings": [
                { "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "A", }, },
              ],
            }
            """);
        Assert.Single(b);
    }

    [Fact]
    public void EmptyBindings_IsValid()
        => Assert.Empty(GestureConfig.Parse("""{ "bindings": [] }"""));

    [Fact]
    public void BrokenJson_Throws()
        => Assert.Contains("JSON", Errors("{ not json")[0]);

    [Fact]
    public void MissingBindings_Throws()
        => Assert.Single(Errors("{}"));

    [Fact]
    public void UnknownProperty_IsRejected()
        => Assert.Contains("JSON", Errors("""
            { "bindings": [ { "fingres": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "A" } } ] }
            """)[0]);

    [Fact]
    public void ReportsAllProblems_AtOnce_WithIndex()
    {
        var errs = Errors("""
            { "bindings": [
              { "name": "a", "fingers": 3, "slot": 3, "direction": "Left", "action": { "hotkey": "A" } },
              { "name": "b", "fingers": 3, "slot": 0, "direction": "Sideways", "action": { "hotkey": "A" } },
              { "name": "c", "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "Ctrl+Banana" } },
              { "name": "d", "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "A", "wheel": 1 } },
              { "name": "e", "fingers": 3, "slot": 0, "direction": "Left" },
              { "name": "f", "fingers": 3, "slot": 0, "direction": "Left", "action": { "wheel": 0 } },
              { "name": "g", "fingers": 3, "slot": 0, "direction": "Left", "minDistance": 0, "action": { "wheel": 1 } }
            ] }
            """);

        Assert.Equal(7, errs.Length);
        Assert.Contains("bindings[0]", errs[0]);
        Assert.Contains("slot", errs[0]);
        Assert.Contains("bindings[1]", errs[1]);
        Assert.Contains("direction", errs[1]);
        Assert.Contains("Banana", errs[2]);
        Assert.Contains("一緒に指定できません", errs[3]);
        Assert.Contains("action が必要", errs[4]);
        Assert.Contains("wheel", errs[5]);
        Assert.Contains("minDistance", errs[6]);
    }

    [Fact]
    public void DuplicateFingerSlotDirection_IsRejected()
    {
        var errs = Errors("""
            { "bindings": [
              { "name": "x", "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "A" } },
              { "name": "y", "fingers": 3, "slot": 0, "direction": "left", "action": { "hotkey": "B" } }
            ] }
            """);
        Assert.Single(errs);
        Assert.Contains("重複", errs[0]);
    }

    [Fact]
    public void Load_ReadsUtf8File()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, GestureConfig.SampleJson, new System.Text.UTF8Encoding(false));
            Assert.Equal(6, GestureConfig.Load(path).Count);
        }
        finally { File.Delete(path); }
    }
}
