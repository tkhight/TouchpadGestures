using Gestures;
using Xunit;

namespace Gestures.Tests;

public sealed class ExecuteMemoryTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "execmem-" + Guid.NewGuid().ToString("N"));

    public ExecuteMemoryTests() => Directory.CreateDirectory(dir);
    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    [Fact]
    public void Load_WithNothingSaved_IsOff()
        => Assert.False(ExecuteMemory.Load(dir));

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        ExecuteMemory.Save(dir, true);
        Assert.True(ExecuteMemory.Load(dir));
        ExecuteMemory.Save(dir, false);
        Assert.False(ExecuteMemory.Load(dir));
    }

    [Fact]
    public void Load_BrokenFile_IsOff()
    {
        File.WriteAllText(Path.Combine(dir, ExecuteMemory.FileName), "{ not json");
        Assert.False(ExecuteMemory.Load(dir));
        File.WriteAllText(Path.Combine(dir, ExecuteMemory.FileName), "[true]");
        Assert.False(ExecuteMemory.Load(dir));
    }

    [Fact]
    public void Save_MissingFolder_DoesNotThrow()
        => ExecuteMemory.Save(Path.Combine(dir, "nope", "deeper"), true);
}
