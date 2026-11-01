namespace Gestures;

public enum GestureKind { Swipe, Tap, Pinch, LandOrder, MultiTap }

/// <summary>設定画面から BindingEntry を読み書きするための補助。画面の部品に依存しないので、単体テストできる。</summary>
public static class BindingEditing
{
    public static GestureKind KindOf(BindingEntry e)
        => e.Pinch is not null ? GestureKind.Pinch
         : e.LandOrder is not null ? GestureKind.LandOrder
         : e.TapAll == true ? GestureKind.MultiTap
         : e.TapSlot is not null || e.TapCount is not null ? GestureKind.Tap
         : GestureKind.Swipe;

    /// <summary>ジェスチャーに関わる項目 (種類・指・しきい値) を空にする。名前・アプリ・動作は残す。</summary>
    public static void ClearGesture(BindingEntry e)
    {
        e.Slot = null; e.Direction = null; e.Motions = null;
        e.Pinch = null; e.PinchDistance = null; e.CentroidTolerance = null;
        e.LandOrder = null; e.LandMinGapMs = null; e.LandMaxGapMs = null; e.LandMaxMove = null;
        e.TapAll = null; e.MaxSpreadMs = null;
        e.TapSlot = null; e.TapCount = null; e.MaxTapMs = null; e.MaxGapMs = null; e.MaxTapMove = null;
        e.MinDistance = null; e.FixedTolerance = null; e.SettleMs = null; e.RepeatDistance = null;
    }

    /// <summary>
    /// スワイプの、指ごとの動き (左から順。null = 固定)。slot / direction の書き方も motions の書き方も同じ形で返す。
    /// 読み取れない指は固定として扱う。
    /// </summary>
    public static SwipeDirection?[] GetMotions(BindingEntry e)
    {
        int n = Math.Max(e.Fingers ?? 0, 0);
        var result = new SwipeDirection?[n];
        if (e.Motions is not null)
        {
            for (int i = 0; i < n && i < e.Motions.Count; i++)
                if (GestureConfig.TryParseDirection(e.Motions[i], out var d)) result[i] = d;
        }
        else if (e.Slot is int slot && slot >= 0 && slot < n && GestureConfig.TryParseDirection(e.Direction, out var dir))
        {
            result[slot] = dir;
        }
        return result;
    }

    /// <summary>
    /// 指ごとの動きを書き込む。動く指が 1 本なら slot / direction、複数なら motions の書き方にそろえる
    /// (動く指が 0 本のときも motions で書き、検証で「動かす指がない」と報告させる)。
    /// </summary>
    public static void SetMotions(BindingEntry e, IReadOnlyList<SwipeDirection?> motions)
    {
        e.Slot = null; e.Direction = null; e.Motions = null;
        int moving = motions.Count(m => m is not null);
        if (moving == 1)
        {
            int slot = Array.FindIndex(motions.ToArray(), m => m is not null);
            e.Slot = slot;
            e.Direction = motions[slot]!.Value.ToString();
        }
        else
        {
            e.Motions = motions.Select(m => (string?)(m is SwipeDirection d ? d.ToString() : "none")).ToList();
        }
    }

    public static PinchDirection? GetPinchDirection(BindingEntry e)
        => e.Pinch?.Trim().ToLowerInvariant() switch
        {
            "in" => PinchDirection.In,
            "out" => PinchDirection.Out,
            _ => null,
        };

    /// <summary>一覧に出す名前。名前が空なら、設定から作った既定の名前。作れないとき (入力の途中) は "(未完成)"。</summary>
    public static string DisplayName(BindingEntry e)
    {
        if (!string.IsNullOrWhiteSpace(e.Name)) return e.Name!;
        try
        {
            var probe = e.Clone();
            probe.Enabled = null; // 無効でも名前は作る
            return GestureConfig.Build(new[] { probe })[0].Spec.Name;
        }
        catch (GestureConfigException)
        {
            return "(未完成)";
        }
    }

    /// <summary>
    /// item を、neighbor が今いる位置へ動かす (neighbor は 1 つ上/下に見えている行)。間にある、絞り込みで隠れている行の順番は変えない。
    /// </summary>
    public static void MoveTo(List<BindingEntry> all, BindingEntry item, BindingEntry neighbor)
    {
        int from = all.IndexOf(item), to = all.IndexOf(neighbor);
        if (from < 0 || to < 0 || from == to) return;
        all.RemoveAt(from);
        all.Insert(to, item);
    }

    /// <summary>一覧に出す、修飾キーの条件。条件がない (または読めない) ときは空。</summary>
    public static string ModifiersLabel(BindingEntry e)
    {
        try
        {
            var m = KeyModifierNames.ParseOrNull(e.Modifiers);
            return m is null ? "" : "(" + KeyModifierNames.Format(m.Value) + ") ";
        }
        catch (FormatException)
        {
            return "(" + e.Modifiers + "?) ";
        }
    }

    /// <summary>一覧に出す、動作の説明。</summary>
    public static string DescribeAction(BindingEntry e)
    {
        var a = e.Action;
        if (a is null) return "";
        if (a.Click is not null && a.Hold == true)
            return (a.Hotkey is null ? "" : a.Hotkey + " + ") + "押し続ける(" + a.Click + (a.Count > 1 ? " ×" + a.Count : "") + ")";
        if (a.Click is not null)
            return (a.Hotkey is null ? "" : a.Hotkey + " + ") + "クリック(" + a.Click + (a.Count > 1 ? " ×" + a.Count : "") + ")";
        if (a.Hotkey is not null) return a.Hotkey;
        if (a.Wheel is int w) return (a.Horizontal == true ? "横ホイール " : "ホイール ") + w;
        return "";
    }
}
