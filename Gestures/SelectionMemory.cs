using System.Text.Json;

namespace Gestures;

/// <summary>
/// 設定画面で最後に選んでいた割り当てを覚えておき、次に開いたとき、そこから始められるようにする。
/// exe の隣の <see cref="FileName"/> に、設定ファイルごとではなく、直近の 1 件だけを保存する。
/// 保存・読み込みに失敗しても、設定画面の動作には影響させない (覚えられないだけ)。
/// </summary>
public static class SelectionMemory
{
    public const string FileName = "touchpadgestures.state.json";

    /// <summary>選んでいた割り当ての位置 (index) と、見分けるための印 (key) を保存する。</summary>
    public static void Save(string baseDir, string configPath, int index, string key)
    {
        try
        {
            string json = JsonSerializer.Serialize(
                new Dictionary<string, object> { ["configPath"] = configPath, ["index"] = index, ["key"] = key },
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            File.WriteAllText(System.IO.Path.Combine(baseDir, FileName), json, new System.Text.UTF8Encoding(false));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 覚えていた割り当てが、keys のどこにあるか。同じ印が同じ位置にあればそこ、位置が変わっていれば印の一致する最初のもの、
    /// 無くなっていれば、覚えていた位置に近いもの。何も覚えていない (別の設定ファイル、壊れた保存、空の一覧) ときは null。
    /// </summary>
    public static int? Find(string baseDir, string configPath, IReadOnlyList<string> keys)
    {
        if (keys.Count == 0) return null;
        try
        {
            string file = System.IO.Path.Combine(baseDir, FileName);
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("configPath", out var p) || p.ValueKind != JsonValueKind.String
                || !string.Equals(p.GetString(), configPath, StringComparison.OrdinalIgnoreCase)) return null;
            if (!root.TryGetProperty("index", out var i) || !i.TryGetInt32(out int index)) return null;
            string? key = root.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;

            if (index >= 0 && index < keys.Count && keys[index] == key) return index;
            int found = key is null ? -1 : keys.ToList().IndexOf(key);
            if (found >= 0) return found;
            return Math.Min(Math.Max(index, 0), keys.Count - 1);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
