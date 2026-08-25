using System.Text;

namespace IpcVisionController.Core.Hal;

/// <summary>
/// SR-X300 讀碼器的連線與電文設定 / Connection and frame settings for the SR-X300 reader.
/// 設定軟體為 AutoID Network Navigator（乙太網頁籤 → 設定小幫手 / 設定清單）。
/// Configured from AutoID Network Navigator, on the Ethernet tab.
///
/// 讀碼器必須設成 TCP 伺服器 / The reader has to be a TCP server:
/// 本驅動是客戶端,主動連向讀碼器。若讀碼器被設成 TCP 客戶端,它不會在任何埠上等待,
/// 症狀是連線被拒絕且掃不到任何開著的資料埠 —— 那不是本程式能修的。
/// This driver is the client and dials out to the reader. A reader configured as a TCP client
/// listens on nothing, and the symptom is a refused connection with no data port open anywhere;
/// that is not something this program can fix.
/// </summary>
public sealed class SrX300Options : NonProtocolLinkOptions
{
    /// <inheritdoc />
    protected override string Model => "Keyence SR-X300";

    /// <summary>條碼內容所在的欄位索引（由 0 起）/ Zero-based field indexes holding decoded code text.</summary>
    public IReadOnlyList<int> CodeDataFields { get; set; } = [];

    /// <summary>
    /// 條碼等級所在的欄位索引 / Zero-based field indexes holding each code's quality level.
    /// 與 <see cref="CodeDataFields"/> 逐位對應;留空表示讀碼器沒有輸出等級,
    /// 此時 <see cref="Models.CodeResult.Grade"/> 為 null,配方的等級檢查自然失效。
    /// Positionally paired with <see cref="CodeDataFields"/>. Leave empty when the reader emits
    /// no grade: <see cref="Models.CodeResult.Grade"/> is then null and the recipe's grade check
    /// has nothing to compare against.
    /// </summary>
    public IReadOnlyList<int> CodeGradeFields { get; set; } = [];

    /// <inheritdoc />
    protected override void ValidateFields()
    {
        EnsureNonNegative(CodeDataFields, nameof(CodeDataFields));
        EnsureNonNegative(CodeGradeFields, nameof(CodeGradeFields));

        // 等級欄位比內容欄位多,表示對應關係已經錯位,解出來的等級會掛到別筆條碼上
        // More grade fields than data fields means the pairing is already out of step and a grade
        // would be attached to the wrong code.
        if (CodeGradeFields.Count > CodeDataFields.Count)
        {
            throw new ArgumentException(
                $"CodeGradeFields ({CodeGradeFields.Count} 項) 不可多於 CodeDataFields "
                + $"({CodeDataFields.Count} 項) / cannot outnumber CodeDataFields.",
                nameof(CodeGradeFields));
        }

        // 沒有任何條碼欄位,每次觸發都回空結果,配方的筆數檢查會讓每一張標籤都判退。
        // 讀碼器接上了卻沒有指定任何欄位,一定是設定漏填,不會是意圖。
        // With no code fields every trigger returns nothing and the recipe's count check rejects
        // every label. A reader that is attached but has no field mapped is an unfinished
        // configuration, never an intent.
        if (CodeDataFields.Count == 0)
        {
            throw new ArgumentException(
                "CodeDataFields 不可為空,否則每張標籤都會判退 / must not be empty, or every label rejects.",
                nameof(CodeDataFields));
        }
    }

    /// <inheritdoc />
    protected override void DescribeFields(StringBuilder builder)
    {
        builder.Append(", 條碼欄位 / code fields [").AppendJoin(' ', CodeDataFields).Append(']');
        builder.Append(", 等級欄位 / grade fields [").AppendJoin(' ', CodeGradeFields).Append(']');
    }
}
