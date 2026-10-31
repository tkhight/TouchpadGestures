using System.Text;
using System.Text.Json;

namespace Gestures;

/// <summary>設定ファイルの内容に問題があるとき。Errors に全件の問題点が入る。</summary>
public sealed class GestureConfigException : Exception
{
    public IReadOnlyList<string> Errors { get; }

    public GestureConfigException(IReadOnlyList<string> errors)
        : base(string.Join(Environment.NewLine, errors))
    {
        Errors = errors;
    }
}

/// <summary>JSON 設定 → GestureBinding の一覧。</summary>
public static class GestureConfig
{
    /// <summary>初回起動時に書き出す見本。パースできることをテストで保証している。</summary>
    public const string SampleJson = """
        {
          // slot は左から数えた番号 (0 始まり)。direction は Left / Right / Up / Down
          // minDistance / fixedTolerance / settleMs / repeatDistance は省略可 (パッド幅 = 1.0 の値)
          // repeatDistance を付けると、スワイプし続ける間 その距離ごとに連続で発火する
          "bindings": [
            { "name": "3本指・左だけ←", "fingers": 3, "slot": 0, "direction": "Left",
              "action": { "hotkey": "Alt+Left" } },
            { "name": "3本指・左だけ→", "fingers": 3, "slot": 0, "direction": "Right",
              "action": { "hotkey": "Alt+Right" } },
            { "name": "3本指・中だけ↑", "fingers": 3, "slot": 1, "direction": "Up",
              "repeatDistance": 0.03, "action": { "wheel": 1 } },
            { "name": "3本指・中だけ↓", "fingers": 3, "slot": 1, "direction": "Down",
              "repeatDistance": 0.03, "action": { "wheel": -1 } },
            { "name": "3本指・右だけ↑", "fingers": 3, "slot": 2, "direction": "Up",
              "repeatDistance": 0.05, "action": { "hotkey": "VolumeUp" } },
            { "name": "3本指・右だけ↓", "fingers": 3, "slot": 2, "direction": "Down",
              "repeatDistance": 0.05, "action": { "hotkey": "VolumeDown" } }
          ]
        }
        """;

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // 項目名のタイプミス ("fingres" など) を黙って無視しない
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    sealed class FileDto
    {
        public List<BindingDto>? Bindings { get; set; }
    }

    sealed class BindingDto
    {
        public string? Name { get; set; }
        public int? Fingers { get; set; }
        public int? Slot { get; set; }
        public string? Direction { get; set; }
        public double? MinDistance { get; set; }
        public double? FixedTolerance { get; set; }
        public long? SettleMs { get; set; }
        public double? RepeatDistance { get; set; }
        public ActionDto? Action { get; set; }
    }

    sealed class ActionDto
    {
        public string? Hotkey { get; set; }
        public int? Wheel { get; set; }
        public bool? Horizontal { get; set; }
    }

    /// <exception cref="GestureConfigException">形式・内容に問題があるとき。</exception>
    public static IReadOnlyList<GestureBinding> Load(string path)
        => Parse(File.ReadAllText(path, Encoding.UTF8));

    /// <exception cref="GestureConfigException">形式・内容に問題があるとき(問題点は全件まとめて返す)。</exception>
    public static IReadOnlyList<GestureBinding> Parse(string json)
    {
        FileDto? file;
        try
        {
            file = JsonSerializer.Deserialize<FileDto>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new GestureConfigException(new[] { "JSON の形式が不正です: " + ex.Message });
        }

        if (file?.Bindings is null)
            throw new GestureConfigException(new[] { "\"bindings\" 配列がありません" });

        var errors = new List<string>();
        var result = new List<GestureBinding>();
        var seen = new Dictionary<(int, int, SwipeDirection), string>();

        for (int i = 0; i < file.Bindings.Count; i++)
        {
            var b = file.Bindings[i];
            string label = $"bindings[{i}]" + (string.IsNullOrEmpty(b.Name) ? "" : $" \"{b.Name}\"");
            int before = errors.Count;

            if (b.Fingers is null || b.Fingers < 1) errors.Add($"{label}: fingers は 1 以上の整数で指定してください");
            if (b.Slot is null) errors.Add($"{label}: slot が必要です");
            else if (b.Fingers >= 1 && (b.Slot < 0 || b.Slot >= b.Fingers))
                errors.Add($"{label}: slot は 0 〜 {b.Fingers - 1} の範囲で指定してください (指定値: {b.Slot})");

            if (!TryParseDirection(b.Direction, out var dir))
                errors.Add($"{label}: direction は Left / Right / Up / Down のどれかです (指定値: {b.Direction ?? "なし"})");

            if (b.MinDistance <= 0) errors.Add($"{label}: minDistance は 0 より大きい値にしてください");
            if (b.FixedTolerance < 0) errors.Add($"{label}: fixedTolerance は 0 以上にしてください");
            if (b.SettleMs < 0) errors.Add($"{label}: settleMs は 0 以上にしてください");
            if (b.RepeatDistance <= 0) errors.Add($"{label}: repeatDistance は 0 より大きい値にしてください");

            var action = ParseAction(b.Action, label, errors);

            if (errors.Count != before) continue;

            int fingers = b.Fingers!.Value, slot = b.Slot!.Value;
            if (seen.TryGetValue((fingers, slot, dir), out var other))
            {
                errors.Add($"{label}: {other} と同じ指の組み合わせ・方向が重複しています");
                continue;
            }
            seen[(fingers, slot, dir)] = label;

            var spec = new SlotSwipeSpec(
                string.IsNullOrEmpty(b.Name) ? DefaultName(fingers, slot, dir) : b.Name,
                fingers, slot, dir);
            if (b.MinDistance is double md) spec = spec with { MinDistance = md };
            if (b.FixedTolerance is double ft) spec = spec with { FixedTolerance = ft };
            if (b.SettleMs is long sm) spec = spec with { SettleMs = sm };
            if (b.RepeatDistance is double rd) spec = spec with { RepeatDistance = rd };

            result.Add(new GestureBinding(spec, action!));
        }

        if (errors.Count > 0) throw new GestureConfigException(errors);
        return result;
    }

    static IGestureAction? ParseAction(ActionDto? a, string label, List<string> errors)
    {
        if (a is null)
        {
            errors.Add($"{label}: action が必要です");
            return null;
        }
        bool hasHotkey = a.Hotkey is not null, hasWheel = a.Wheel is not null;
        if (hasHotkey == hasWheel)
        {
            errors.Add($"{label}: action には hotkey か wheel のどちらか一方だけを指定してください");
            return null;
        }
        if (hasHotkey)
        {
            if (a.Horizontal is not null) errors.Add($"{label}: horizontal は wheel と一緒にだけ指定できます");
            try { return new HotkeyAction(a.Hotkey!); }
            catch (FormatException ex) { errors.Add($"{label}: {ex.Message}"); return null; }
        }
        if (a.Wheel == 0)
        {
            errors.Add($"{label}: wheel は 0 以外のノッチ数にしてください");
            return null;
        }
        return new WheelAction(a.Wheel!.Value, a.Horizontal ?? false);
    }

    static bool TryParseDirection(string? s, out SwipeDirection dir)
    {
        switch (s?.Trim().ToLowerInvariant())
        {
            case "left": dir = SwipeDirection.Left; return true;
            case "right": dir = SwipeDirection.Right; return true;
            case "up": dir = SwipeDirection.Up; return true;
            case "down": dir = SwipeDirection.Down; return true;
            default: dir = default; return false;
        }
    }

    static string DefaultName(int fingers, int slot, SwipeDirection dir)
    {
        string arrow = dir switch
        {
            SwipeDirection.Left => "←",
            SwipeDirection.Right => "→",
            SwipeDirection.Up => "↑",
            _ => "↓",
        };
        return $"{fingers}本指・左から{slot + 1}番目だけ{arrow}";
    }
}
