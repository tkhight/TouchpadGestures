using Gestures;
using Xunit;

namespace Gestures.Tests;

public sealed class SelectionMemoryTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "sm-" + Guid.NewGuid().ToString("N"));

    public SelectionMemoryTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, true);

    static readonly string[] Keys = { "|a", "chrome|b", "|c" };

    [Fact]
    public void NothingSaved_ReturnsNull()
        => Assert.Null(SelectionMemory.Find(dir, "x.json", Keys));

    [Fact]
    public void SameKeyAtSameIndex_IsFound()
    {
        SelectionMemory.Save(dir, "x.json", 1, "chrome|b");
        Assert.Equal(1, SelectionMemory.Find(dir, "x.json", Keys));
    }

    [Fact]
    public void MovedEntry_IsFoundByKey()
    {
        SelectionMemory.Save(dir, "x.json", 0, "|c");
        Assert.Equal(2, SelectionMemory.Find(dir, "x.json", Keys));
    }

    [Fact]
    public void RemovedEntry_FallsBackToTheNearestIndex()
    {
        SelectionMemory.Save(dir, "x.json", 7, "|gone");
        Assert.Equal(2, SelectionMemory.Find(dir, "x.json", Keys));
        SelectionMemory.Save(dir, "x.json", 1, "|gone");
        Assert.Equal(1, SelectionMemory.Find(dir, "x.json", Keys));
    }

    [Fact]
    public void OtherConfigFile_IsIgnored()
    {
        SelectionMemory.Save(dir, "x.json", 1, "chrome|b");
        Assert.Null(SelectionMemory.Find(dir, "y.json", Keys));
    }

    [Fact]
    public void EmptyList_AndBrokenFile_ReturnNull()
    {
        SelectionMemory.Save(dir, "x.json", 0, "|a");
        Assert.Null(SelectionMemory.Find(dir, "x.json", Array.Empty<string>()));
        File.WriteAllText(Path.Combine(dir, SelectionMemory.FileName), "{ not json");
        Assert.Null(SelectionMemory.Find(dir, "x.json", Keys));
    }

    [Fact]
    public void SaveToAMissingFolder_DoesNotThrow()
        => SelectionMemory.Save(Path.Combine(dir, "nope", "deeper"), "x.json", 0, "|a");
}
