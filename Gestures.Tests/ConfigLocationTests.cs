using Gestures;
using Xunit;

namespace Gestures.Tests;

public sealed class ConfigLocationTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "tg_cfgloc_" + Guid.NewGuid().ToString("N"));
    const string Default = @"C:\Users\someone\AppData\Roaming\TouchpadGestures\gestures.json";
    static readonly string[] NoArgs = Array.Empty<string>();

    public ConfigLocationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void NothingSpecified_UsesTheDefault()
    {
        var r = ConfigLocation.Resolve(NoArgs, _dir, Default);
        Assert.Equal(Default, r.Path);
        Assert.Equal(ConfigLocation.SourceDefault, r.Source);
    }

    [Theory]
    [InlineData("--config", @"D:\OneDrive\KT_Tools\TouchpadGestures\gestures.json")]
    [InlineData("--CONFIG", @"D:\OneDrive\KT_Tools\TouchpadGestures\gestures.json")]
    public void ConfigArgument_WithSeparateValue(string flag, string path)
    {
        var r = ConfigLocation.Resolve(new[] { "--tray", flag, path }, _dir, Default);
        Assert.Equal(path, r.Path);
        Assert.Equal(ConfigLocation.SourceArgument, r.Source);
    }

    [Fact]
    public void ConfigArgument_WithEquals_AndQuotes()
    {
        var r = ConfigLocation.Resolve(new[] { "--config=\"D:\\My Folder\\g.json\"" }, _dir, Default);
        Assert.Equal(@"D:\My Folder\g.json", r.Path);
    }

    [Fact]
    public void JapanesePathAndSpaces_AreKept()
    {
        string p = @"D:\OneDrive\設定 フォルダ\gestures.json";
        Assert.Equal(p, ConfigLocation.Resolve(new[] { "--config", p }, _dir, Default).Path);
    }

    [Theory]
    [InlineData("--tray")]
    [InlineData("--config")]        // 値が無い
    [InlineData("--config=")]       // 値が空
    public void UnusableArguments_FallBackToTheDefault(string arg)
    {
        var r = ConfigLocation.Resolve(new[] { arg }, _dir, Default);
        Assert.Equal(Default, r.Path);
        Assert.Equal(ConfigLocation.SourceDefault, r.Source);
    }

    [Fact]
    public void SavedLocation_IsUsedNextTime()
    {
        ConfigLocation.Save(_dir, @"D:\OneDrive\KT_Tools\TouchpadGestures\gestures.json");
        var r = ConfigLocation.Resolve(NoArgs, _dir, Default);
        Assert.Equal(@"D:\OneDrive\KT_Tools\TouchpadGestures\gestures.json", r.Path);
        Assert.Equal(ConfigLocation.SourceSettings, r.Source);
        Assert.True(File.Exists(Path.Combine(_dir, ConfigLocation.SettingsFileName)));
    }

    [Fact]
    public void SavedLocation_WithJapanesePath_RoundTrips()
    {
        string p = @"D:\OneDrive\KT_Tools\ツール履歴\gestures.json";
        ConfigLocation.Save(_dir, p);
        Assert.Equal(p, ConfigLocation.Resolve(NoArgs, _dir, Default).Path);
        // 日本語が \u エスケープされず、人が読める形で保存される
        Assert.Contains("ツール履歴", File.ReadAllText(Path.Combine(_dir, ConfigLocation.SettingsFileName)));
    }

    [Fact]
    public void Argument_BeatsTheSavedLocation()
    {
        ConfigLocation.Save(_dir, @"D:\saved\g.json");
        var r = ConfigLocation.Resolve(new[] { "--config", @"E:\arg\g.json" }, _dir, Default);
        Assert.Equal(@"E:\arg\g.json", r.Path);
        Assert.Equal(ConfigLocation.SourceArgument, r.Source);
    }

    [Fact]
    public void Clear_GoesBackToTheDefault_AndIsSafeWhenNothingSaved()
    {
        ConfigLocation.Clear(_dir); // 何も保存されていなくても例外にならない
        ConfigLocation.Save(_dir, @"D:\saved\g.json");
        ConfigLocation.Clear(_dir);
        Assert.Equal(Default, ConfigLocation.Resolve(NoArgs, _dir, Default).Path);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{}")]
    [InlineData("{ \"configPath\": \"\" }")]
    [InlineData("{ \"configPath\": 123 }")]
    [InlineData("[]")]
    public void BrokenOrEmptySettings_AreIgnored(string content)
    {
        File.WriteAllText(Path.Combine(_dir, ConfigLocation.SettingsFileName), content);
        var r = ConfigLocation.Resolve(NoArgs, _dir, Default);
        Assert.Equal(Default, r.Path);
        Assert.Equal(ConfigLocation.SourceDefault, r.Source);
    }

    [Fact]
    public void RelativeSavedPath_IsResolvedAgainstTheExeFolder()
    {
        ConfigLocation.Save(_dir, @"cfg\gestures.json");
        var r = ConfigLocation.Resolve(NoArgs, _dir, Default);
        Assert.Equal(Path.Combine(_dir, @"cfg\gestures.json"), r.Path);
    }
}
