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
          // 着地順は landOrder (着地した順に、その指が左から何番目か。0 始まり)。fingers 本が全部触れたときに発火する
          //   例: { "fingers": 3, "landOrder": [0, 1, 2], "action": { ... } } (左→中→右の順に置く)
          //   landMinGapMs (既定 80) / landMaxGapMs (既定 500) / landMaxMove (既定 0.03) は省略可
          // 同時タップは tapAll: true。fingers 本がほぼ同時に着地して、ほぼ同時に離れる。tapCount (回数) も使える
          //   maxSpreadMs (既定 100。「同時」とみなす幅) / maxTapMs / maxGapMs / maxTapMove は省略可
          //   例: { "fingers": 3, "tapAll": true, "action": { ... } } (Windows の 3 本指タップは、設定で「何もしない」に)
          // タップは tapSlot (左から数えた番号) と tapCount (回数、省略で 1)。他の指は触れたまま、その指だけが
          //   触れて離れるのを数える。maxTapMs / maxGapMs / maxTapMove は省略可
          //   例: { "fingers": 3, "tapSlot": 0, "tapCount": 2, "action": { ... } }
          // 動作は hotkey (キー) / wheel (ホイール) / click (Left / Right / Middle。count で回数) のどれか
          //   hotkey と click を一緒に書くと、そのキーを押したままクリックする: "action": { "hotkey": "Win", "click": "Left" }
          //   click に "hold": true を足すと、押したままにして、全部の指が離れたら離す (指の動きでカーソルも動く。"follow": false で止める)
          //   hold と count を一緒に書くと、count 回目を押したままにする (count: 2 = 1 回タップして、2 回目を押したまま = ダブルタップして押したまま)
          //   例: PowerToys の「つかんで移動」: "action": { "hotkey": "Win", "click": "Left", "count": 2, "hold": true }
          // modifiers を付けると、その修飾キーをキーボードで押しているときだけ有効になる。"Ctrl" / "Ctrl+Shift" (Ctrl / Alt / Shift / Win)
          //   ちょうどその組み合わせのときだけ。"none" = どの修飾キーも押していないとき。省略 = 押していても押していなくても有効
          //   同じジェスチャーに修飾キー付きと省略のものがあれば、押している修飾キーに合う付きのほうが優先される
          //   例: { "modifiers": "Ctrl", "fingers": 3, "slot": 0, "direction": "Left", "action": { ... } }
          // "enabled": false を付けると、その割り当てだけを一時的に無効にできる (省略 = 有効)
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
            KeyModifiers? mods = null;
            if (b.Modifiers is not null)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(b.Modifiers)) throw new FormatException("modifiers が空です (問わないなら項目ごと省く。どれも押さないなら none)");
                    mods = KeyModifierNames.Parse(b.Modifiers);
                }
                catch (FormatException ex)
                {
                    errors.Add($"{label}: {ex.Message}");
                }
            }
            SwipeDirection dir = default;
            SwipeDirection?[]? motions = null;
            bool isMulti = b.TapAll == true;
            bool isTap = b.TapSlot is not null || (b.TapCount is not null && !isMulti);
            bool isPinch = b.Pinch is not null;
            bool isLand = b.LandOrder is not null;
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
            if (isMulti)
            {
                if (isTap || isPinch || isLand) errors.Add($"{label}: tapAll と tapSlot / pinch / landOrder は一緒に指定できません");
                if (b.Slot is not null || b.Direction is not null || b.Motions is not null || b.MinDistance is not null
                    || b.FixedTolerance is not null || b.SettleMs is not null || b.RepeatDistance is not null)
                    errors.Add($"{label}: 同時タップには slot / direction / motions / minDistance / fixedTolerance / settleMs / repeatDistance は使えません");
                if (b.Fingers < 2) errors.Add($"{label}: tapAll は 2 本以上の指で指定してください");
                if (b.TapCount < 1) errors.Add($"{label}: tapCount は 1 以上にしてください");
                if (b.MaxTapMs <= 0) errors.Add($"{label}: maxTapMs は 0 より大きい値にしてください");
                if (b.MaxGapMs <= 0) errors.Add($"{label}: maxGapMs は 0 より大きい値にしてください");
                if (b.MaxTapMove <= 0) errors.Add($"{label}: maxTapMove は 0 より大きい値にしてください");
                if (b.MaxSpreadMs < 0) errors.Add($"{label}: maxSpreadMs は 0 以上にしてください");
            }
            else
            {
                if (b.TapAll is not null) errors.Add($"{label}: tapAll は true のときだけ指定できます (使わないなら項目ごと省く)");
                if (b.MaxSpreadMs is not null) errors.Add($"{label}: maxSpreadMs は tapAll と一緒にだけ指定できます");
            }
            if (isLand)
            {
                if (isTap || isPinch) errors.Add($"{label}: landOrder と tapSlot / tapCount / pinch は一緒に指定できません");
                if (b.Slot is not null || b.Direction is not null || b.Motions is not null || b.MinDistance is not null
                    || b.FixedTolerance is not null || b.SettleMs is not null || b.RepeatDistance is not null)
                    errors.Add($"{label}: 着地順には slot / direction / motions / minDistance / fixedTolerance / settleMs / repeatDistance は使えません");
                if (b.MaxTapMs is not null || b.MaxGapMs is not null || b.MaxTapMove is not null)
                    errors.Add($"{label}: maxTapMs / maxGapMs / maxTapMove はタップにだけ使えます");
                if (b.Fingers < 2) errors.Add($"{label}: landOrder は 2 本以上の指で指定してください");
                else if (b.Fingers >= 2 && (b.LandOrder!.Count != b.Fingers
                         || !b.LandOrder.OrderBy(x => x).SequenceEqual(Enumerable.Range(0, b.Fingers.Value))))
                    errors.Add($"{label}: landOrder は 0 〜 {b.Fingers - 1} を 1 つずつ使った、fingers と同じ {b.Fingers} 個の並びで指定してください (指定値: {string.Join(", ", b.LandOrder!)})");
                if (b.LandMinGapMs < 0) errors.Add($"{label}: landMinGapMs は 0 以上にしてください");
                if (b.LandMaxGapMs <= 0) errors.Add($"{label}: landMaxGapMs は 0 より大きい値にしてください");
                if ((b.LandMinGapMs ?? 80) > (b.LandMaxGapMs ?? 500))
                    errors.Add($"{label}: landMinGapMs は landMaxGapMs 以下にしてください");
                if (b.LandMaxMove <= 0) errors.Add($"{label}: landMaxMove は 0 より大きい値にしてください");
            }
            else if (b.LandMinGapMs is not null || b.LandMaxGapMs is not null || b.LandMaxMove is not null)
            {
                errors.Add($"{label}: landMinGapMs / landMaxGapMs / landMaxMove は landOrder と一緒にだけ指定できます");
            }
            if (isLand || isMulti)
            {
            }
            else if (isPinch)
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
            if (isMulti)
            {
                int mc = b.TapCount ?? 1;
                var multi = new MultiTapSpec(
                    string.IsNullOrEmpty(b.Name) ? DefaultMultiTapName(fingers, mc) : b.Name, fingers, mc);
                if (b.MaxTapMs is long xt) multi = multi with { MaxTapMs = xt };
                if (b.MaxGapMs is long xg) multi = multi with { MaxGapMs = xg };
                if (b.MaxTapMove is double xm) multi = multi with { MaxTapMove = xm };
                if (b.MaxSpreadMs is long xs) multi = multi with { MaxSpreadMs = xs };
                spec = multi;
            }
            else if (isLand)
            {
                var land = new LandOrderSpec(
                    string.IsNullOrEmpty(b.Name) ? DefaultLandName(fingers, b.LandOrder!) : b.Name,
                    fingers, b.LandOrder!.ToArray());
                if (b.LandMinGapMs is long lmin) land = land with { MinGapMs = lmin };
                if (b.LandMaxGapMs is long lmax) land = land with { MaxGapMs = lmax };
                if (b.LandMaxMove is double lmv) land = land with { MaxMove = lmv };
                spec = land;
            }
            else if (isPinch)
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
            // 無効な割り当ては、内容だけ確かめて、重複の判定にも結果にも入れない (同じジェスチャーの別の動作と切り替えて使える)
            if (b.Enabled == false) continue;

            string seenKey = (app ?? "") + "|" + (mods is null ? "" : KeyModifierNames.Format(mods.Value)) + "|" + spec.Key;
            if (seen.TryGetValue(seenKey, out var other))
            {
                errors.Add($"{label}: {other} と同じ指の組み合わせ・方向が重複しています" + (app is null ? "" : $" (app: {app})") + (mods is null ? "" : $" (modifiers: {KeyModifierNames.Format(mods.Value)})"));
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

            result.Add(new GestureBinding(spec, action!, app, mods));
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
        bool hasHotkey = a.Hotkey is not null, hasWheel = a.Wheel is not null, hasClick = a.Click is not null;
        if (hasWheel && (hasHotkey || hasClick))
        {
            errors.Add($"{label}: wheel は hotkey / click と一緒に指定できません");
            return null;
        }
        if (!hasHotkey && !hasWheel && !hasClick)
        {
            errors.Add($"{label}: action には hotkey か wheel か click のどれかを指定してください");
            return null;
        }
        if (a.Count is not null && !hasClick) errors.Add($"{label}: count は click と一緒にだけ指定できます");
        if (a.Hold == true && !hasClick) errors.Add($"{label}: hold は click と一緒にだけ指定できます");
        if ((a.Follow is not null || a.Speed is not null) && a.Hold != true)
            errors.Add($"{label}: follow / speed は hold と一緒にだけ指定できます");
        if (a.Speed <= 0) errors.Add($"{label}: speed は 0 より大きい値にしてください");
        if (hasClick)
        {
            if (a.Horizontal is not null) errors.Add($"{label}: horizontal は wheel と一緒にだけ指定できます");
            MouseButton button;
            switch (a.Click!.Trim().ToLowerInvariant())
            {
                case "left": button = MouseButton.Left; break;
                case "right": button = MouseButton.Right; break;
                case "middle": button = MouseButton.Middle; break;
                default:
                    errors.Add($"{label}: click は Left / Right / Middle のどれかです (指定値: {a.Click})");
                    return null;
            }
            if (a.Count is < 1 or > 5)
            {
                errors.Add($"{label}: count は 1 〜 5 にしてください (指定値: {a.Count})");
                return null;
            }
            try
            {
                if (a.Hold == true) return new HoldAction(button, a.Hotkey, a.Follow ?? true, a.Speed > 0 ? a.Speed.Value : 1.0, a.Count ?? 1);
                return new ClickAction(button, a.Count ?? 1, a.Hotkey);
            }
            catch (FormatException ex) { errors.Add($"{label}: {ex.Message}"); return null; }
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

    static string DefaultLandName(int fingers, IReadOnlyList<int> order)
    {
        string[]? names = fingers == 2 ? new[] { "左", "右" } : fingers == 3 ? new[] { "左", "中", "右" } : null;
        string seq = string.Join("→", order.Select(o => names is null ? (o + 1).ToString() : names[o]));
        return $"{fingers}本指・{seq}の順に着地";
    }

    static string DefaultMultiTapName(int fingers, int count)
        => $"{fingers}本指で同時に{(count == 1 ? "" : count + "回")}タップ";

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
    /// <summary>"Ctrl" / "Ctrl+Shift" など。その修飾キーが、ちょうど押されているときだけ有効。"none" = どれも押していない。省略 = 問わない。</summary>
    public string? Modifiers { get; set; }
    /// <summary>false なら、この割り当ては無効 (内容の検証だけして、判定には使わない)。省略 = 有効。</summary>
    public bool? Enabled { get; set; }
    public int? Fingers { get; set; }
    public int? Slot { get; set; }
    public string? Direction { get; set; }
    public List<string?>? Motions { get; set; }
    public string? Pinch { get; set; }
    public double? PinchDistance { get; set; }
    public double? CentroidTolerance { get; set; }
    public List<int>? LandOrder { get; set; }
    public long? LandMinGapMs { get; set; }
    public long? LandMaxGapMs { get; set; }
    public double? LandMaxMove { get; set; }
    /// <summary>true なら、同時タップ (fingers 本がほぼ同時に着地して離れる)。tapCount で回数。</summary>
    public bool? TapAll { get; set; }
    /// <summary>同時タップの「同時」とみなす幅 (ms)。</summary>
    public long? MaxSpreadMs { get; set; }
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
        Name = Name, App = App, Modifiers = Modifiers, Enabled = Enabled, Fingers = Fingers, Slot = Slot, Direction = Direction,
        Motions = Motions is null ? null : new List<string?>(Motions),
        Pinch = Pinch, PinchDistance = PinchDistance, CentroidTolerance = CentroidTolerance,
        LandOrder = LandOrder is null ? null : new List<int>(LandOrder), LandMinGapMs = LandMinGapMs, LandMaxGapMs = LandMaxGapMs, LandMaxMove = LandMaxMove,
        TapAll = TapAll, MaxSpreadMs = MaxSpreadMs,
        TapSlot = TapSlot, TapCount = TapCount, MaxTapMs = MaxTapMs, MaxGapMs = MaxGapMs, MaxTapMove = MaxTapMove,
        MinDistance = MinDistance, FixedTolerance = FixedTolerance, SettleMs = SettleMs, RepeatDistance = RepeatDistance,
        Action = Action is null ? null : new ActionEntry { Hotkey = Action.Hotkey, Wheel = Action.Wheel, Horizontal = Action.Horizontal, Click = Action.Click, Count = Action.Count, Hold = Action.Hold, Follow = Action.Follow, Speed = Action.Speed },
    };
}

public sealed class ActionEntry
{
    public string? Hotkey { get; set; }
    public int? Wheel { get; set; }
    public bool? Horizontal { get; set; }
    /// <summary>Left / Right / Middle。hotkey と一緒に書くと、そのキーを押したままクリックする。</summary>
    public string? Click { get; set; }
    public int? Count { get; set; }
    /// <summary>true なら、click を押したままにして、全部の指が離れたら離す (hotkey のキーも押したまま)。</summary>
    public bool? Hold { get; set; }
    /// <summary>hold のとき、指の動きに合わせてカーソルを動かすか (省略 = する)。</summary>
    public bool? Follow { get; set; }
    /// <summary>hold + follow のときの、カーソルの動く速さの倍率 (省略 = 1)。</summary>
    public double? Speed { get; set; }
}
