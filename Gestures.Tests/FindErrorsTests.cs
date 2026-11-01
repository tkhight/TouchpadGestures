using Gestures;
using Xunit;

namespace Gestures.Tests;

public class FindErrorsTests
{
    static List<BindingEntry> Entries(params string[] bindings)
        => GestureConfig.ParseEntries("{ \"bindings\": [ " + string.Join(",", bindings) + " ] }");

    const string Left3 = "\"fingers\": 3, \"slot\": 0, \"direction\": \"Left\"";

    [Fact]
    public void ValidEntries_HaveNoErrors()
    {
        var e = Entries(
            "{ " + Left3 + ", \"action\": { \"hotkey\": \"A\" } }",
            "{ " + Left3 + ", \"app\": \"chrome\", \"action\": { \"hotkey\": \"B\" } }");
        Assert.Empty(BindingEditing.FindErrors(e));
    }

    [Fact]
    public void Duplicate_IsReportedOnTheLaterEntry_WithoutTheLabelPrefix()
    {
        var e = Entries(
            "{ " + Left3 + ", \"action\": { \"hotkey\": \"A\" } }",
            "{ \"name\": \"x\", " + Left3 + ", \"action\": { \"hotkey\": \"B\" } }");
        var r = BindingEditing.FindErrors(e);
        Assert.Single(r);
        Assert.Contains("重複", Assert.Single(r[e[1]]));
        Assert.DoesNotContain("bindings[", r[e[1]][0]);
    }

    [Fact]
    public void IncompleteEntry_IsReported_AndMatchesSaveErrors()
    {
        var e = Entries("{ \"fingers\": 3 }", "{ " + Left3 + ", \"action\": { \"hotkey\": \"A\" } }");
        var r = BindingEditing.FindErrors(e);
        Assert.True(r.ContainsKey(e[0]));
        Assert.False(r.ContainsKey(e[1]));
        var ex = Assert.Throws<GestureConfigException>(() => GestureConfig.Build(e));
        Assert.Equal(ex.Errors.Count, r.Values.Sum(v => v.Count));
    }

    [Fact]
    public void DisabledDuplicate_IsNotAnError()
    {
        var e = Entries(
            "{ " + Left3 + ", \"action\": { \"hotkey\": \"A\" } }",
            "{ " + Left3 + ", \"enabled\": false, \"action\": { \"hotkey\": \"B\" } }");
        Assert.Empty(BindingEditing.FindErrors(e));
    }
}
