using System.Text.Json;

namespace Gestures;

/// <summary>
/// 「ジェスチャーでキー操作を実行する」のオン / オフを覚えておき、次の起動でも同じ状態から始められるようにする。
/// exe の隣の <see cref="FileName"/> に保存する。保存・読み込みに失敗しても、動作には影響させない
/// (覚えられない / 覚えていない場合は、オフから始まる)。
/// </summary>
public static class ExecuteMemory
{
    public const string FileName = "touchpadgestures.execute.json";

    public static void Save(string baseDir, bool on)
    {
        try
        {
            File.WriteAllText(System.IO.Path.Combine(baseDir, FileName),
                JsonSerializer.Serialize(new Dictionary<string, object> { ["execute"] = on }),
                new System.Text.UTF8Encoding(false));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>覚えていた状態。何も覚えていない、壊れている、のときは false (オフ)。</summary>
    public static bool Load(string baseDir)
    {
        try
        {
            string file = System.IO.Path.Combine(baseDir, FileName);
            if (!File.Exists(file)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("execute", out var v)
                && v.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
