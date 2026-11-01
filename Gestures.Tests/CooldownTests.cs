using Gestures;
using Xunit;

namespace Gestures.Tests;

public class CooldownTests
{
    const string Json = """
        { "bindings": [
          { "fingers": 3, "slot": 0, "direction": "Left", "cooldownMs": 500, "action": { "hotkey": "A" } },
          { "fingers": 3, "slot": 0, "direction": "Right", "action": { "hotkey": "B" } }
        ] }
        """;

    [Fact]
    public void CooldownMs_ParsesCopiesAndRoundTrips()
    {
        var e = GestureConfig.ParseEntries(Json);
        Assert.Equal(500, e[0].CooldownMs);
        Assert.Null(e[1].CooldownMs);
        Assert.Equal(500, e[0].Clone().CooldownMs);
        var json = GestureConfig.ToJson(e);
        Assert.Contains("\"cooldownMs\":500", json);
        var again = GestureConfig.ParseEntries(json);
        Assert.Equal(500, again[0].CooldownMs);
        Assert.Null(again[1].CooldownMs);
    }

    [Fact]
    public void CooldownMs_IsPassedToTheBinding_AndDefaultsToZero()
    {
        var b = GestureConfig.Parse(Json);
        Assert.Equal(500, b[0].CooldownMs);
        Assert.Equal(0, b[1].CooldownMs);
    }

    [Fact]
    public void CooldownMs_Negative_IsAnError()
    {
        var ex = Assert.Throws<GestureConfigException>(() => GestureConfig.Parse("""
            { "bindings": [ { "fingers": 3, "slot": 0, "direction": "Left", "cooldownMs": -1, "action": { "hotkey": "A" } } ] }
            """));
        Assert.Contains(ex.Errors, m => m.Contains("cooldownMs"));
    }

    sealed class Fake : IInputSender
    {
        public int Presses;
        public void KeyDown(ushort vk) => Presses++;
        public void KeyUp(ushort vk) { }
        public void Wheel(int delta, bool horizontal) { }
    }

    // 3 本指の左端の指を、10ms ごとに 0.02 ずつ左へ動かす (repeatDistance 0.03 で、約 15ms ごとに連続発火する)
    static int Presses(string cooldown)
    {
        var json = "{ \"bindings\": [ { \"fingers\": 3, \"slot\": 0, \"direction\": \"Left\", \"repeatDistance\": 0.03" + cooldown
            + ", \"action\": { \"hotkey\": \"A\" } } ] }";
        var f = new Fake();
        var d = new GestureDispatcher(GestureConfig.Parse(json), f);
        double x = 0.9;
        for (long t = 0; t < 600; t += 10)
        {
            d.Feed(new Frame(t, new[] { new Contact(1, x, 0.5), new Contact(2, 0.95, 0.5), new Contact(3, 0.97, 0.5) }));
            x -= 0.02;
        }
        return f.Presses;
    }

    [Fact]
    public void Dispatcher_WithoutCooldown_FiresRepeatedly()
        => Assert.True(Presses("") > 3);

    [Fact]
    public void Dispatcher_Cooldown_ThinsOutRepeatedFiring()
    {
        int free = Presses("");
        int limited = Presses(", \"cooldownMs\": 300");
        Assert.InRange(limited, 1, 2);   // 600ms の間に、300ms 以上あけて、多くて 2 回
        Assert.True(limited < free);
    }

    [Fact]
    public void CooldownMs_SampleStillParses()
        => Assert.NotEmpty(GestureConfig.Parse(GestureConfig.SampleJson));
}
