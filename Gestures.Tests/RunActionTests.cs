using Gestures;
using Xunit;

namespace Gestures.Tests;

public class RunActionTests
{
    sealed class Fake : IInputSender
    {
        public readonly List<string> Log = new();
        public void KeyDown(ushort vk) { }
        public void KeyUp(ushort vk) { }
        public void Wheel(int delta, bool horizontal) { }
        public void Run(string path, string? args) => Log.Add($"run:{path}|{args}");
    }

    [Fact]
    public void Execute_RunsThePathWithArguments()
    {
        var f = new Fake();
        new RunAction(@"C:\Tools\a.exe", "--x 1").Execute(f);
        new RunAction("notepad").Execute(f);
        Assert.Equal(new[] { @"run:C:\Tools\a.exe|--x 1", "run:notepad|" }, f.Log);
    }

    [Fact]
    public void EmptyPath_Throws()
        => Assert.Throws<ArgumentException>(() => new RunAction("  "));

    const string Json = """
        { "bindings": [
          { "fingers": 3, "slot": 0, "direction": "Left", "action": { "run": "C:\\Tools\\a.exe", "args": "--x 1" } },
          { "fingers": 3, "slot": 0, "direction": "Right", "action": { "run": "notepad" } }
        ] }
        """;

    [Fact]
    public void Config_ParsesRun_AndRoundTrips()
    {
        var b = GestureConfig.Parse(Json);
        Assert.IsType<RunAction>(b[0].Action);
        var e = GestureConfig.ParseEntries(Json);
        Assert.Equal(@"起動: C:\Tools\a.exe --x 1", BindingEditing.DescribeAction(e[0]));
        var again = GestureConfig.ParseEntries(GestureConfig.ToJson(e));
        Assert.Equal(@"C:\Tools\a.exe", again[0].Action!.Run);
        Assert.Equal("--x 1", again[0].Action!.Args);
        Assert.Equal(@"C:\Tools\a.exe", e[0].Clone().Action!.Run);
    }

    [Theory]
    [InlineData("\"run\": \"a\", \"hotkey\": \"A\"")]
    [InlineData("\"run\": \"a\", \"click\": \"left\"")]
    [InlineData("\"run\": \"a\", \"moveWindow\": true")]
    [InlineData("\"run\": \"a\", \"speed\": 2")]
    [InlineData("\"run\": \"  \"")]
    [InlineData("\"args\": \"x\", \"hotkey\": \"A\"")]
    public void Config_RejectsBadCombinations(string action)
    {
        var json = "{ \"bindings\": [ { \"fingers\": 3, \"slot\": 0, \"direction\": \"Left\", \"action\": { " + action + " } } ] }";
        Assert.Throws<GestureConfigException>(() => GestureConfig.Parse(json));
    }

    [Fact]
    public void Dispatcher_RunsOnFire()
    {
        var f = new Fake();
        var d = new GestureDispatcher(GestureConfig.Parse(Json), f);
        double x = 0.3;
        for (long t = 0; t < 500 && f.Log.Count == 0; t += 10)
        {
            d.Feed(new Frame(t, new[] { new Contact(1, x, 0.5), new Contact(2, 0.5, 0.5), new Contact(3, 0.8, 0.5) }));
            x -= 0.02;
        }
        Assert.Equal(new[] { @"run:C:\Tools\a.exe|--x 1" }, f.Log);
    }
}
