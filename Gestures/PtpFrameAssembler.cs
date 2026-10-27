namespace Gestures;

/// <summary>PTP レポートの 1 スロット分。パディング用の空きスロットも含まれる。</summary>
public readonly record struct TouchSlot(int Id, int X, int Y, bool Tip);

/// <summary>
/// 解読済みの PTP 入力レポート 1 つ。
/// ContactCount / ScanTime は HID の Contact Count / Scan Time。レポートに無い構成では null。
/// Slots は、そのレポートが持つ指スロットすべて(使われていない末尾スロットも含む)。
/// </summary>
public sealed record PtpReport(int? ContactCount, uint? ScanTime, IReadOnlyList<TouchSlot> Slots);

/// <summary>
/// PTP の Hybrid モードでは、指の数がレポートのスロット数を超えると
/// 1 フレームが複数レポートに分割される。先頭レポートだけ Contact Count に総数が入り、
/// 続きのレポートは 0 になる。これを 1 フレームに組み立てる。
/// HID API に依存しないので、合成したレポートで単体テストできる。
/// </summary>
public sealed class PtpFrameAssembler
{
    List<TouchSlot> _current = new();
    List<TouchSlot> _pending = new();
    int _remaining;

    /// <summary>直近に完成したフレームで、指が触れている(Tip=true)スロット。</summary>
    public IReadOnlyList<TouchSlot> Fingers => _current;

    /// <summary>直近に始まったフレームの Contact Count。</summary>
    public int ContactCount { get; private set; }

    public uint ScanTime { get; private set; }

    /// <summary>レポートを 1 つ与える。1 フレームが揃った瞬間だけ true。</summary>
    public bool Feed(PtpReport report)
    {
        var slots = report.Slots;

        if (report.ContactCount is null)
        {
            // Contact Count が無い構成: 1 レポート = 1 フレーム、全スロット有効
            _pending.Clear();
            _remaining = slots.Count;
            ContactCount = slots.Count;
            if (report.ScanTime is uint st) ScanTime = st;
        }
        else if (report.ContactCount > 0)
        {
            // 新しいフレームの先頭。前のフレームの取りこぼし(未完)があれば捨てる
            _pending.Clear();
            _remaining = report.ContactCount.Value;
            ContactCount = _remaining;
            if (report.ScanTime is uint st) ScanTime = st;
        }

        // 先頭から数えて「残り件数」ぶんだけが有効。末尾のパディングスロットは読まない
        int valid = Math.Min(slots.Count, _remaining);
        for (int i = 0; i < valid; i++)
            if (slots[i].Tip) _pending.Add(slots[i]);
        _remaining -= valid;

        if (valid == 0 && _remaining == 0)
        {
            // 全員離れた空フレーム
            _current = new List<TouchSlot>();
            ContactCount = 0;
            return true;
        }
        if (_remaining == 0)
        {
            (_current, _pending) = (_pending, _current);
            return true;
        }
        return false;
    }
}
