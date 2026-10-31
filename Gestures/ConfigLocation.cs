using System.Text.Json;

namespace Gestures;

/// <summary>決まった設定ファイルの場所と、どこから決まったか(画面に出す用)。</summary>
public sealed record ConfigLocationInfo(string Path, string Source);

/// <summary>
/// 設定ファイル (gestures.json) の場所の決め方。優先順位は次のとおり。
/// 1. コマンドライン <c>--config &lt;path&gt;</c>(または <c>--config=&lt;path&gt;</c>)
/// 2. exe の隣の <see cref="SettingsFileName"/> に保存された場所(メニューで選んだもの)
/// 3. 既定の場所
/// どこに置いても、起動のしかた(エクスプローラー、ショートカット、自動実行)に関わらず同じファイルを読める。
/// </summary>
public static class ConfigLocation
{
    public const string SettingsFileName = "touchpadgestures.settings.json";

    public const string SourceArgument = "コマンドライン";
    public const string SourceSettings = "設定";
    public const string SourceDefault = "既定";

    public static ConfigLocationInfo Resolve(IReadOnlyList<string> args, string baseDir, string defaultPath)
    {
        string? fromArg = FindArgument(args);
        if (fromArg is not null)
            return new ConfigLocationInfo(System.IO.Path.GetFullPath(fromArg), SourceArgument);

        string? saved = ReadSaved(baseDir);
        if (saved is not null)
            return new ConfigLocationInfo(System.IO.Path.GetFullPath(saved, baseDir), SourceSettings);

        return new ConfigLocationInfo(defaultPath, SourceDefault);
    }

    /// <summary>選んだ場所を、exe の隣に保存する。次回以降の起動で使われる。</summary>
    public static void Save(string baseDir, string configPath)
    {
        string json = JsonSerializer.Serialize(
            new Dictionary<string, string> { ["configPath"] = configPath },
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(System.IO.Path.Combine(baseDir, SettingsFileName), json, new System.Text.UTF8Encoding(false));
    }

    /// <summary>保存した場所を消して、既定に戻す。保存が無くても例外にならない。</summary>
    public static void Clear(string baseDir)
    {
        string file = System.IO.Path.Combine(baseDir, SettingsFileName);
        if (File.Exists(file)) File.Delete(file);
    }

    static string? FindArgument(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (string.Equals(a, "--config", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1])) return args[i + 1];
                return null; // 値が無い: 無視して、次の優先順位へ
            }
            if (a.StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
            {
                string v = a.Substring("--config=".Length).Trim('"');
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
        }
        return null;
    }

    static string? ReadSaved(string baseDir)
    {
        string file = System.IO.Path.Combine(baseDir, SettingsFileName);
        if (!File.Exists(file)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("configPath", out var p)
                && p.ValueKind == JsonValueKind.String)
            {
                string? s = p.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
        }
        catch (JsonException) { /* 壊れた設定は無視して、既定に戻す */ }
        catch (IOException) { }
        return null;
    }
}
