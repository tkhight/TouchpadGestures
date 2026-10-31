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
          // 複数の指を動かすときは slot / direction の代わりに motions を使う (左から順、none = 固定)
          //   例: { "fingers": 3, "motions": ["none", "Down", "Down"], ... } / ["Up", "Up", "Up"]
          // ピンチは pinch ("In" / "Out") で、fingers 本の指全体が閉じる/開くのを判定する
          //   pinchDistance (既定 0.05) / centroidTolerance / settleMs / repeatDistance は省略可
          //   例: { "fingers": 4, "pinch": "Out", "repeatDistance": 0.03, "action": { ... } }
          // タップは tapSlot (左から数えた番号) と tapCount (回数、省略で 1)。他の指は触れたまま、その指だけが
          //   触れて離れるのを数える。maxTapMs / maxGapMs / maxTapMove は省略可
          //   例: { "fingers": 3, "tapSlot": 0, "tapCount": 2, "action": { ... } }
          // app を付けると、そのアプリ (プロセス名。"chrome" / "Code.exe" など) が前面のときだけ有効になる。
          //   同じジェスチャーが app なしにもあるときは、app 付きが優先される。プロセス名は画面のログに [ ] で出る
          //   例: { "app": "chrome", "fingers": 3, "slot": 0, "direction": "Left", "action": { ... } }
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
        public List<BindingEntry>? Bindings { get; set; }
    }

    /// <exception cref="GestureConfigException">形式・内容に問題があるとき。</exception>
    public static IReadOnlyList<GestureBinding> Load(string path)
        => Parse(File.ReadAllText(path, Encoding.UTF8));

    /// <exception cref="GestureConfigException">形式・内容に問題があるとき(問題点は全件まとめて返す)。</exception>
    public static IReadOnlyList<GestureBinding> Parse(string json) => Build(ParseEntries(json));

    /// <exception cref="GestureConfigException">形式・内容に問題があるとき。</exception>
    public static List<BindingEntry> LoadEntries(string path)
        => ParseEntries(File.ReadAllText(path, Encoding.UTF8));

    /// <summary>JSON を、中身を検証せずに編集用の一覧にする。形式が不正なときだけ失敗する。</summary>
    /// <exception cref="GestureConfigException">JSON の形式が不正、または "bindings" 配列が無いとき。</exception>
    public static List<BindingEntry> ParseEntries(string json)
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
        return file.Bindings;
    }

    static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>編集用の一覧を、設定ファイルの JSON にする。1 件を 1 行に書く。手書きのコメントは残らない。</summary>
    public static string ToJson(IEnumerable<BindingEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append("// このファイルは設定画面からも編集できます。設定画面で保存すると、手書きのコメントは消えます\n");
        sb.Append("{\n  \"bindings\": [");
        bool first = true;
        foreach (var e in entries)
        {
            sb.Append(first ? "\n    " : ",\n    ");
            sb.Append(JsonSerializer.Serialize(e, WriteOptions));
            first = false;
        }
        sb.Append(first ? "]\n}\n" : "\n  ]\n}\n");
        return sb.ToString();
    }

    /// <exception cref="GestureConfigException">内容に問題があるとき(問題点は全件まとめて返す)。</exception>
    public static IReadOnlyList<GestureBinding> Build(IReadOnlyList<BindingEntry> entries)
    {
        var errors = new List<string>();
        var result = new List<GestureBinding>();
        var seen = new Dictionary<string, string>();

        for (int i = 0; i < entries.Count; i++)
        {
            var b = entries[i];
            string label = $"bindings[{i}]" + (string.IsNullOrEmpty(b.Name) ? "" : $" \"{b.Name}\"");
            int before = errors.Count;

            if (b.Fingers is null || b.Fingers < 1) errors.Add($"{label}: fingers は 1 以上の整数で指定してください");
            string? app = AppNames.Normalize(b.App);
            if (b.App is not null && app is null) errors.Add($"{label}: app が空です (全アプリで有効にするなら項目ごと省く)");
            SwipeDirection dir = default;
            SwipeDirection?[]? motions = null;
            bool isTap = b.TapSlot is not null || b.TapCount is not null;
            bool isPinch = b.Pinch is not null;
            PinchDirection pinchDir = default;
            if (isPinch)
            {
                if (isTap) errors.Add($"{label}: pinch と tapSlot / tapCount は一緒に指定できません");
                if (b.Fingers < 2) errors.Add($"{label}: pinch は 2 本以上の指で指定してください");
                switch (b.Pinch!.Trim().ToLowerInvariant())
                {
                    case "in": pinchDir = PinchDirection.In; break;
                    case "out": pinchDir = PinchDirection.Out; break;
                    default: errors.Add($"{label}: pinch は In / Out のどちらかです (指定値: {b.Pinch})"); break;
                }
                if (b.Slot is not null || b.Direction is not null || b.Motions is not null || b.MinDistance is not null
                    || b.FixedTolerance is not null)
                    errors.Add($"{label}: ピンチには slot / direction / motions / minDistance / fixedTolerance は使えません");
                if (b.PinchDistance <= 0) errors.Add($"{label}: pinchDistance は 0 より大きい値にしてください");
                if (b.CentroidTolerance < 0) errors.Add($"{label}: centroidTolerance は 0 以上にしてください");
            }
            else if (b.PinchDistance is not null || b.CentroidTolerance is not null)
            {
                errors.Add($"{label}: pinchDistance / centroidTolerance は pinch と一緒にだけ指定できます");
            }
            if (isPinch)
            {
                if (b.MaxTapMs is not null || b.MaxGapMs is not null || b.MaxTapMove is not null)
                    errors.Add($"{label}: maxTapMs / maxGapMs / maxTapMove はタップにだけ使えます");
            }
            else if (isTap)
            {
                if (b.TapSlot is null) errors.Add($"{label}: tapSlot が必要です");
                else if (b.Fingers >= 1 && (b.TapSlot < 0 || b.TapSlot >= b.Fingers))
                    errors.Add($"{label}: tapSlot は 0 〜 {b.Fingers - 1} の範囲で指定してください (指定値: {b.TapSlot})");
                if (b.TapCount < 1) errors.Add($"{label}: tapCount は 1 以上にしてください");
                if (b.Slot is not null || b.Direction is not null || b.Motions is not null || b.MinDistance is not null
                    || b.SettleMs is not null || b.RepeatDistance is not null)
                    errors.Add($"{label}: タップには slot / direction / motions / minDistance / settleMs / repeatDistance は使えません");
                if (b.MaxTapMs <= 0) errors.Add($"{label}: maxTapMs は 0 より大きい値にしてください");
                if (b.MaxGapMs <= 0) errors.Add($"{label}: maxGapMs は 0 より大きい値にしてください");
                if (b.MaxTapMove <= 0) errors.Add($"{label}: maxTapMove は 0 より大きい値にしてください");
            }
            else if (b.MaxTapMs is not null || b.MaxGapMs is not null || b.MaxTapMove is not null)
            {
                errors.Add($"{label}: maxTapMs / maxGapMs / maxTapMove は tapSlot と一緒にだけ指定できます");
            }
            else if (b.Motions is not null)
            {
                if (b.Slot is not null || b.Direction is not null)
                    errors.Add($"{label}: motions と slot / direction は一緒に指定できません");
                motions = ParseMotions(b.Motions, b.Fingers, label, errors);
            }
            else
            {
                if (b.Slot is null) errors.Add($"{label}: slot が必要です (指ごとに指定するなら motions)");
                else if (b.Fingers >= 1 && (b.Slot < 0 || b.Slot >= b.Fingers))
                    errors.Add($"{label}: slot は 0 〜 {b.Fingers - 1} の範囲で指定してください (指定値: {b.Slot})");

                if (!TryParseDirection(b.Direction, out dir))
                    errors.Add($"{label}: direction は Left / Right / Up / Down のどれかです (指定値: {b.Direction ?? "なし"})");
            }

            if (b.MinDistance <= 0) errors.Add($"{label}: minDistance は 0 より大きい値にしてください");
            if (b.FixedTolerance < 0) errors.Add($"{label}: fixedTolerance は 0 以上にしてください");
            if (b.SettleMs < 0) errors.Add($"{label}: settleMs は 0 以上にしてください");
            if (b.RepeatDistance <= 0) errors.Add($"{label}: repeatDistance は 0 より大きい値にしてください");

            var action = ParseAction(b.Action, label, errors);

            if (errors.Count != before) continue;

            int fingers = b.Fingers!.Value;
            IGestureSpec spec;
            if (isPinch)
            {
                var pin = new PinchSpec(
                    string.IsNullOrEmpty(b.Name) ? $"{fingers}本指ピンチ{(pinchDir == PinchDirection.Out ? "アウト" : "イン")}" : b.Name,
                    fingers, pinchDir);
                if (b.PinchDistance is double pd) pin = pin with { PinchDistance = pd };
                if (b.CentroidTolerance is double ct) pin = pin with { CentroidTolerance = ct };
                if (b.SettleMs is long ps) pin = pin with { SettleMs = ps };
                if (b.RepeatDistance is double pr) pin = pin with { RepeatDistance = pr };
                spec = pin;
            }
            else if (isTap)
            {
                int ts = b.TapSlot!.Value, tc = b.TapCount ?? 1;
                var tap = new TapSpec(
                    string.IsNullOrEmpty(b.Name) ? DefaultTapName(fingers, ts, tc) : b.Name, fingers, ts, tc);
                if (b.MaxTapMs is long mt) tap = tap with { MaxTapMs = mt };
                if (b.MaxGapMs is long mg) tap = tap with { MaxGapMs = mg };
                if (b.MaxTapMove is double mm) tap = tap with { MaxTapMove = mm };
                if (b.FixedTolerance is double ftt) tap = tap with { FixedTolerance = ftt };
                spec = tap;
            }
            else if (motions is null)
            {
                int slot = b.Slot!.Value;
                spec = new SlotSwipeSpec(
                    string.IsNullOrEmpty(b.Name) ? DefaultName(fingers, slot, dir) : b.Name,
                    fingers, slot, dir);
            }
            else
            {
                int first = Array.FindIndex(motions, m => m is not null);
                spec = new SlotSwipeSpec(
                    string.IsNullOrEmpty(b.Name) ? DefaultName(motions) : b.Name,
                    fingers, first, motions[first]!.Value, Motions: motions);
            }
            string seenKey = (app ?? "") + "|" + spec.Key;
            if (seen.TryGetValue(seenKey, out var other))
            {
                errors.Add($"{label}: {other} と同じ指の組み合わせ・方向が重複しています" + (app is null ? "" : $" (app: {app})"));
                continue;
            }
            seen[seenKey] = label;

            if (spec is SlotSwipeSpec swipe)
            {
                if (b.MinDistance is double md) swipe = swipe with { MinDistance = md };
                if (b.FixedTolerance is double ft) swipe = swipe with { FixedTolerance = ft };
                if (b.SettleMs is long sm) swipe = swipe with { SettleMs = sm };
                if (b.RepeatDistance is double rd) swipe = swipe with { RepeatDistance = rd };
                spec = swipe;
            }

            result.Add(new GestureBinding(spec, action!, app));
        }

        if (errors.Count > 0) throw new GestureConfigException(errors);
        return result;
    }

    static IGestureAction? ParseAction(ActionEntry? a, string label, List<string> errors)
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

    internal static bool TryParseDirection(string? s, out SwipeDirection dir)
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

    /// <summary>motions の各要素 (左から順)。"none" = 固定、Left / Right / Up / Down = その方向へ動く。</summary>
    static SwipeDirection?[]? ParseMotions(List<string?> list, int? fingers, string label, List<string> errors)
    {
        if (fingers is >= 1 && list.Count != fingers)
        {
            errors.Add($"{label}: motions は fingers と同じ {fingers} 個で指定してください (指定数: {list.Count})");
            return null;
        }
        var result = new SwipeDirection?[list.Count];
        bool ok = true;
        for (int i = 0; i < list.Count; i++)
        {
            string? s = list[i]?.Trim().ToLowerInvariant();
            if (s is "none" or "fixed") continue;
            if (TryParseDirection(s, out var d)) result[i] = d;
            else
            {
                errors.Add($"{label}: motions[{i}] は none / Left / Right / Up / Down のどれかです (指定値: {list[i] ?? "null"})");
                ok = false;
            }
        }
        if (ok && result.All(m => m is null))
        {
            errors.Add($"{label}: motions に動かす指が 1 本もありません");
            return null;
        }
        return ok ? result : null;
    }

    static string Arrow(SwipeDirection dir) => dir switch
    {
        SwipeDirection.Left => "←",
        SwipeDirection.Right => "→",
        SwipeDirection.Up => "↑",
        _ => "↓",
    };

    static string DefaultName(int fingers, int slot, SwipeDirection dir)
        => $"{fingers}本指・左から{slot + 1}番目だけ{Arrow(dir)}";

    static string DefaultTapName(int fingers, int slot, int count)
        => $"{fingers}本指・左から{slot + 1}番目を{(count == 1 ? "" : count + "回")}タップ";

    static string DefaultName(SwipeDirection?[] motions)
        => $"{motions.Length}本指・" + string.Join("", motions.Select(m => m is SwipeDirection d ? Arrow(d) : "・"));
}

/// <summary>
/// 設定ファイルの 1 件分 (JSON の 1 つの割り当て) をそのまま持つ編集用の型。
/// 項目の意味と省略時の既定は、見本 (GestureConfig.SampleJson) のコメントを参照。
/// </summary>
public sealed class BindingEntry
{
    public string? Name { get; set; }
    public string? App { get; set; }
    public int? Fingers { get; set; }
    public int? Slot { get; set; }
    public string? Direction { get; set; }
    public List<string?>? Motions { get; set; }
    public string? Pinch { get; set; }
    public double? PinchDistance { get; set; }
    public double? CentroidTolerance { get; set; }
    public int? TapSlot { get; set; }
    public int? TapCount { get; set; }
    public long? MaxTapMs { get; set; }
    public long? MaxGapMs { get; set; }
    public double? MaxTapMove { get; set; }
    public double? MinDistance { get; set; }
    public double? FixedTolerance { get; set; }
    public long? SettleMs { get; set; }
    public double? RepeatDistance { get; set; }
    public ActionEntry? Action { get; set; }

    public BindingEntry Clone() => new()
    {
        Name = Name, App = App, Fingers = Fingers, Slot = Slot, Direction = Direction,
        Motions = Motions is null ? null : new List<string?>(Motions),
        Pinch = Pinch, PinchDistance = PinchDistance, CentroidTolerance = CentroidTolerance,
        TapSlot = TapSlot, TapCount = TapCount, MaxTapMs = MaxTapMs, MaxGapMs = MaxGapMs, MaxTapMove = MaxTapMove,
        MinDistance = MinDistance, FixedTolerance = FixedTolerance, SettleMs = SettleMs, RepeatDistance = RepeatDistance,
        Action = Action is null ? null : new ActionEntry { Hotkey = Action.Hotkey, Wheel = Action.Wheel, Horizontal = Action.Horizontal },
    };
}

public sealed class ActionEntry
{
    public string? Hotkey { get; set; }
    public int? Wheel { get; set; }
    public bool? Horizontal { get; set; }
}
