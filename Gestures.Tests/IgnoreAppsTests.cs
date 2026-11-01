using Gestures;
using Xunit;

namespace Gestures.Tests;

public class IgnoreAppsTests
{
    sealed class Fake : IInputSender
    {
        public readonly List<string> Log = new();
        public void KeyDown(ushort vk) => Log.Add("down:" + vk);
        public void KeyUp(ushort vk) { }
        public void Wheel(int delta, bool horizontal) { }
    }

    const string Json = """
        { "ignoreApps": ["CLaunch.exe", "  ", "claunch", "Notepad"],
          "bindings": [
          { "fingers": 3, "slot": 0, "direction": "Left", "action": { "hotkey": "A" } }
        ] }
        """;

    [Fact]
    public void ParseIgnoreApps_NormalizesAndDedupes()
        => Assert.Equal(new[] { "claunch", "notepad" }, GestureConfig.ParseIgnoreApps(Json));

    [Fact]
    public void ParseIgnoreApps_MissingIsEmpty()
        => Assert.Empty(GestureConfig.ParseIgnoreApps("""{ "bindings": [] }"""));

    [Fact]
    public void ToJson_RoundTripsIgnoreApps_AndOmitsWhenEmpty()
    {
        var entries = GestureConfig.ParseEntries(Json);
        var again = GestureConfig.ToJson(entries, GestureConfig.ParseIgnoreApps(Json));
        Assert.Equal(new[] { "claunch", "notepad" }, GestureConfig.ParseIgnoreApps(again));
        Assert.Single(GestureConfig.ParseEntries(again));
        Assert.DoesNotContain("ignoreApps", GestureConfig.ToJson(entries));
    }

    [Theory]
    [InlineData("CLaunch", true)]
    [InlineData("notepad.exe", true)]
    [InlineData("chrome", false)]
    [InlineData(null, false)]
    public void Dispatcher_SuppressesOnlyIgnoredApps(string? app, bool suppressed)
    {
        var f = new Fake();
        var d = new GestureDispatcher(GestureConfig.Parse(Json), f, () => app, ignoreApps: GestureConfig.ParseIgnoreApps(Json));
        bool sawSuppressed = false;
        var fired = new List<IGestureSpec>();
        double x = 0.3;
        for (long t = 0; t < 500; t += 10)
        {
            fired.AddRange(d.Feed(new Frame(t, new[] { new Contact(1, x, 0.5), new Contact(2, 0.5, 0.5), new Contact(3, 0.8, 0.5) })));
            sawSuppressed |= d.LastSuppressed;
            x -= 0.02;
        }
        Assert.Equal(suppressed, sawSuppressed);
        Assert.Equal(suppressed, fired.Count == 0 && f.Log.Count == 0);
        if (!suppressed) Assert.NotEmpty(f.Log);
    }
}
