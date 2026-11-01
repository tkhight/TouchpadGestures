using Gestures;
using Xunit;

namespace Gestures.Tests;

public class ProtectedTests
{
    const string Json = """
        { "bindings": [
          { "fingers": 3, "slot": 0, "direction": "Left", "protected": true, "action": { "hotkey": "A" } },
          { "fingers": 3, "slot": 0, "direction": "Right", "action": { "hotkey": "B" } }
        ] }
        """;

    [Fact]
    public void Protected_ParsesCopiesAndRoundTrips()
    {
        var e = GestureConfig.ParseEntries(Json);
        Assert.True(e[0].Protected);
        Assert.Null(e[1].Protected);
        Assert.True(e[0].Clone().Protected);
        var json = GestureConfig.ToJson(e);
        Assert.Contains("\"protected\":true", json);
        var again = GestureConfig.ParseEntries(json);
        Assert.True(again[0].Protected);
        Assert.Null(again[1].Protected);
    }

    [Fact]
    public void Protected_DoesNotAffectBuilding()
        => Assert.Equal(2, GestureConfig.Parse(Json).Count);
}
