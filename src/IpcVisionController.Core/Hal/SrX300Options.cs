using System.Text;

namespace IpcVisionController.Core.Hal;

/// <summary>
/// SR-X300 讀碼器的連線與電文設定 / Connection and frame settings for the SR-X300 reader.
/// 設定軟體為 AutoID Network Navigator（操作模式 → 數據編輯 → 讀取數據格式）。
/// Configured from AutoID Network Navigator, under Operation mode, Data editing, Read data format.
///
/// 電文是兩層結構 / The frame has two levels:
/// 讀碼器的「讀取數據格式」是 〈讀取數據〉:〈附加數據〉:〈附加數據〉…,多筆條碼之間再以
/// 「內部分隔符」隔開。也就是:
/// The reader's read-data format is code, then each appended item, with multiple codes separated
/// again by the inter-code delimiter:
///
///   2132030090A0S6X10683:3,2132030090A0S6X10684:4
///   └──── 一筆記錄 ────┘ │└──── 一筆記錄 ────┘
///        碼    :   等級   內部分隔符 / record delimiter
///
/// 為什麼筆數不能寫成設定 / Why the number of codes is not configuration:
/// 一次觸發讀到幾筆由視野裡有幾張標籤決定,不由設定決定 —— 剛才是 1 筆,下一次可能是 6 筆。
/// 先前的版本要求事先列出固定的欄位索引清單,那假設了「電文是單層、位置固定」的格式,
/// 與讀碼器實際的輸出模型不符:多讀到的那幾筆會整批消失,而不會有任何錯誤訊息。
/// 筆數改由電文本身決定之後,配方的 ExpectedCodeCount 才真正在檢查「這次讀到幾筆」,
/// 那本來就是它的設計目的。
/// How many codes one trigger returns depends on how many labels are in the field of view, not on a
/// setting: one a moment ago, possibly six next time. An earlier version asked for a fixed list of
/// field indexes, which assumed a flat frame at fixed positions and did not match the reader's actual
/// output model — every code past the first would vanish with no error at all. With the count coming
/// from the frame, the recipe's ExpectedCodeCount finally checks what it was designed to check.
///
/// 讀碼器必須設成 TCP 伺服器 / The reader has to be a TCP server:
/// 本驅動是客戶端,主動連向讀碼器。設成客戶端的話它不會在任何埠等待,
/// 症狀是連線被拒絕且掃不到任何開著的資料埠。
/// This driver is the client and dials out. A reader configured as a TCP client listens on nothing,
/// and the symptom is a refused connection with no data port open anywhere.
/// </summary>
public sealed class SrX300Options : NonProtocolLinkOptions
{
    /// <summary>
    /// 預設值取自實機實測 / Defaults measured from the hardware.
    ///
    /// 分隔字元 / The delimiter:
    /// 基底類別預設逗號,而讀碼器用來隔開附加數據的是冒號。
    /// The base class defaults to a comma; the reader separates appended data with a colon.
    ///
    /// 為什麼停用 ErrorPrefix / Why ErrorPrefix is disabled:
    /// SR-X300 讀不到時輸出 "ERROR",而它以 "ER" 開頭。沿用基底的 ErrorPrefix,
    /// 「一張標籤漏貼」就會被判成設備異常而停線 —— 而那應該是判退後繼續生產。
    /// 實測遮住六張中的一張、與遮住全部六張,輸出都是 "ERROR",所以這是每批都會遇到的常態,
    /// 不是例外。停用之後 "ERROR" 由 EmptyTokens 接住,成為「這一格沒有結果」,
    /// 於是條碼不進清單,改由配方的筆數檢查判退。
    /// The SR-X300 emits "ERROR" on a failed read, and that starts with "ER". Keeping the base prefix would
    /// turn one missing label into an equipment fault and stop the line, when it should be a reject and
    /// carry on. In practice covering one of six labels and covering all six both produce "ERROR", so this
    /// is routine rather than exceptional. Disabled, "ERROR" is caught by EmptyTokens as "no result in this
    /// field", the code stays out of the list, and the recipe's count check does the rejecting.
    ///
    /// 代價 / The cost:
    /// 停用之後,讀碼器若另有一種真正的設備異常電文,那筆也會被當成「沒讀到」而判退,
    /// 不會停線。那是刻意的取捨:「漏貼標籤就停線」是每批都會發生的確定損失,
    /// 而「另有一種未知的異常字樣」目前只是推測。若日後量到那個字樣,把它填進 ErrorPrefix 即可。
    /// Disabled, a genuine device-fault frame — if the reader has one — would also read as a failed read and
    /// reject rather than stop. That is a deliberate trade: stopping the line on a missing label is a
    /// certain, recurring loss, while a distinct fault string is at this point only a supposition. Should
    /// one be measured later, putting it in ErrorPrefix restores the behaviour.
    /// </summary>
    public SrX300Options()
    {
        FieldDelimiter = ":";
        ErrorPrefix = string.Empty;
        EmptyTokens = ["ERROR", "NG", "NOREAD", "----"];
    }

    /// <inheritdoc />
    protected override string Model => "Keyence SR-X300";

    /// <summary>
    /// 多筆條碼之間的分隔字元 / Delimiter between codes.
    /// 對應設定軟體的「內部分隔符（讀取多個代碼）」。
    /// This is the reader's inter-code delimiter for reading multiple codes.
    /// </summary>
    public string RecordDelimiter { get; set; } = ",";

    /// <summary>
    /// 一筆記錄裡條碼內容的位置（由 0 起）/ Zero-based position of the code within one record.
    /// 讀取數據排在最前面,所以通常是 0。報頭設為「無」時尤然。
    /// The read data comes first, so this is normally 0 — certainly so with the header set to none.
    /// </summary>
    public int CodeField { get; set; }

    /// <summary>
    /// 一筆記錄裡品質等級的位置；null 表示不讀等級 /
    /// Position of the quality grade within one record, or null to not read a grade.
    ///
    /// 對應附加數據裡的 ISO/IEC 15415（二維）或 15416（一維）。兩項可以同時勾選以兼容不同
    /// 條碼類型,但一次讀取只會輸出其中一種,所以位置固定在條碼之後那一格。
    /// This is the appended ISO/IEC 15415 (2D) or 15416 (1D) grade. Both may be ticked to cover
    /// different symbol types, yet one read emits only one of them, so the position stays the single
    /// field after the code.
    ///
    /// 等級必須設定成數值輸出。字母（A/B/C/D）無法與配方的整數門檻比較,
    /// 解析時會得到 null,而配方若啟用等級檢查就會判退「未輸出品質等級」——
    /// 那是正確的訊息,但不是你想要的結果。
    /// The grade has to be configured as numeric output. Letters (A/B/C/D) cannot be compared with the
    /// recipe's integer threshold, parse to null, and make a recipe with grade checking enabled reject
    /// with "no grade reported" — a correct message, but not the outcome you wanted.
    ///
    /// null 與「指向一個不存在的位置」不同:前者是「本機台不讀等級」,後者是設定錯誤,
    /// 而兩者在判定上都會讓等級檢查失效。刻意分開,讓設定錯誤在啟動時就被擋下。
    /// A null differs from pointing at a position that does not exist: the first says this machine does
    /// not read a grade, the second is a mistake, and both would disable the grade check. Keeping them
    /// apart is what lets the mistake be refused at startup.
    /// </summary>
    public int? GradeField { get; set; }

    /// <inheritdoc />
    protected override void ValidateFields()
    {
        if (string.IsNullOrEmpty(RecordDelimiter))
        {
            throw new ArgumentException(
                "RecordDelimiter 不可為空 / must not be empty.", nameof(RecordDelimiter));
        }

        if (CodeField < 0)
        {
            throw new ArgumentException(
                $"CodeField 為 {CodeField},位置不可為負 / must not be negative.", nameof(CodeField));
        }

        if (GradeField is int grade)
        {
            if (grade < 0)
            {
                throw new ArgumentException(
                    $"GradeField 為 {grade},位置不可為負 / must not be negative.", nameof(GradeField));
            }

            // 條碼與等級指向同一格,表示其中一個填錯了。真接上去的話,等級會被解析成條碼內容
            // 的數字形式,而條碼長度檢查同時失效 —— 兩個檢查一起壞掉,卻不會有任何錯誤訊息。
            // The code and the grade pointing at one position means one of them is wrong. Left in place,
            // the grade would parse from the code text while the length check reads the same field:
            // two checks broken at once, with no error anywhere.
            if (grade == CodeField)
            {
                throw new ArgumentException(
                    $"GradeField 與 CodeField 不可為同一格（皆為 {grade}）/ must not share a position.",
                    nameof(GradeField));
            }
        }

        // 分隔字元與內部分隔符相同,兩層就塌成一層:碼與等級會被當成兩筆條碼,
        // 筆數變成兩倍,而等級整批消失。
        // The same character for both delimiters collapses the two levels into one: a code and its grade
        // become two codes, the count doubles, and every grade disappears.
        if (string.Equals(RecordDelimiter, FieldDelimiter, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"RecordDelimiter 與 FieldDelimiter 不可相同（皆為 '{RecordDelimiter}'）/ must differ.",
                nameof(RecordDelimiter));
        }
    }

    /// <inheritdoc />
    protected override void DescribeFields(StringBuilder builder)
    {
        builder.Append(", 記錄分隔 / record delimiter '").Append(RecordDelimiter).Append('\'');
        builder.Append(", 條碼位置 / code at ").Append(CodeField);
        builder.Append(", 等級位置 / grade at ")
            .Append(GradeField?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "off");
    }
}
