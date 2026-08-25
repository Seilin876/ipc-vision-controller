using System.Text;

namespace IpcVisionController.Core.Hal;

/// <summary>
/// 無協定 TCP 通訊的共用設定 / Settings shared by every non-protocol TCP link.
///
/// 為什麼 SR-X300 與 IV4 共用同一個基底 /
/// Why the SR-X300 and the IV4 share one base:
/// 兩台在電氣上做的是同一件事 —— 開一條 TCP、送出觸發命令加結束字元、讀回一筆以分隔字元
/// 分格的電文。差異只在「哪一格是什麼」。共用連線與組框,兩台就不會各自長出一套逾時語意、
/// 一套錯誤處理、一套組框錯誤;而那三件事只要有一台做錯,現場看到的都是偶發的怪症狀。
/// Electrically the two do the same thing: open a TCP link, send a trigger command plus its
/// terminator, read back one delimited frame. Only the meaning of each field differs. Sharing
/// the link and the framing keeps the two from growing separate timeout semantics, separate
/// error handling and separate framing bugs — and any one of those, wrong on either device,
/// surfaces on the line as an intermittent oddity.
///
/// 為什麼欄位位置是設定而不是寫死 / Why the field positions are configuration:
/// 兩台裝置輸出哪些欄位、順序、分隔字元,都由裝置端的設定軟體決定,換一組設定就變了。
/// 寫死在解析器裡的話,現場每改一次就要重新編譯、重新發佈。
/// Which fields each device emits, in what order, with what delimiter, is decided in its own
/// setup software and changes with the loaded settings. Hard-coded, every change on the line
/// would mean a rebuild and a redeploy.
/// </summary>
public abstract class NonProtocolLinkOptions
{
    /// <summary>裝置 IP / Device IP address.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>無協定通訊埠 / Non-protocol communication port.</summary>
    public int Port { get; set; }

    /// <summary>
    /// 觸發命令,不含結束字元 / Trigger command, without its terminator.
    ///
    /// 刻意沒有預設值 / Deliberately without a default:
    /// 命令由裝置端的通訊規格決定,程式無從猜測。給一個看似合理的預設值,只會讓錯誤的命令
    /// 悄悄沿用下去 —— 而命令錯誤的表徵是「裝置完全不回應」,那與網路不通、與裝置沒開機
    /// 分不出來。空值在啟動時就被 <see cref="Validate"/> 擋下,比較誠實。
    /// The command follows the device's own communication spec and the program cannot guess it.
    /// A plausible-looking default would only let a wrong command persist quietly, and a wrong
    /// command presents as "the device never answers" — indistinguishable from a dead link or an
    /// unpowered device. An empty value refused at startup by <see cref="Validate"/> is honest.
    /// </summary>
    public string TriggerCommand { get; set; } = string.Empty;

    /// <summary>命令與回應的結束字元 / Terminator for commands and responses.</summary>
    public FrameTerminator Terminator { get; set; } = FrameTerminator.Cr;

    /// <summary>欄位分隔字元 / Field delimiter.</summary>
    public string FieldDelimiter { get; set; } = ",";

    /// <summary>連線逾時 / Connect timeout.</summary>
    public int ConnectTimeoutMs { get; set; } = 5_000;

    /// <summary>
    /// 單次觸發的回應逾時 / Per-trigger response timeout.
    ///
    /// 要比 <see cref="Machine.SequencerOptions.CodeReadTimeout"/> 明顯短。兩層逾時同時到期時,
    /// 現場會隨機拿到兩種訊息之一,而只有裝置層那句說得出「是哪一台沒回應」。
    /// Keep this comfortably shorter than the sequencer's own read timeout. When both expire
    /// together the line gets one of two messages at random, and only the device-level one can
    /// say which device went silent.
    /// </summary>
    public int ResponseTimeoutMs { get; set; } = 2_000;

    /// <summary>
    /// 裝置回報自身異常的電文開頭 / Frame prefix that means the device reported its own fault.
    /// 比對到此開頭就轉成 <see cref="DeviceFaultException"/> 停線,而不是當成一張不良標籤。
    /// A frame starting with this becomes a <see cref="DeviceFaultException"/> and stops the
    /// line, rather than being recorded as one bad label.
    /// </summary>
    public string ErrorPrefix { get; set; } = "ER";

    /// <summary>
    /// 視為「該欄位沒有結果」的字樣 / Tokens that mean "this field carries no result".
    /// 比對不分大小寫。空字串一律視為沒有結果,不必列在這裡。
    /// Matched case-insensitively. An empty field always counts as no result and need not be
    /// listed here.
    /// </summary>
    public IReadOnlyList<string> EmptyTokens { get; set; } = ["NG", "NOREAD", "----"];

    /// <summary>結束字元的實際字串 / The terminator's actual characters.</summary>
    public string TerminatorText => Terminator switch
    {
        FrameTerminator.Cr => "\r",
        FrameTerminator.Lf => "\n",
        FrameTerminator.CrLf => "\r\n",
        _ => throw new InvalidOperationException($"未知的結束字元 / unknown terminator: {Terminator}."),
    };

    /// <summary>
    /// 這台裝置在記錄裡的稱呼 / What this device is called in the log.
    /// 由子類別提供型號,基底補上位址 —— 兩台裝置同時導入時,沒有位址的訊息無法歸屬。
    /// The subclass supplies the model and the base appends the address: with two devices being
    /// commissioned together, a message without an address cannot be attributed.
    /// </summary>
    protected abstract string Model { get; }

    /// <summary>裝置代號 / Device tag.</summary>
    public string DeviceName => $"{Model} @ {Host}:{Port}";

    /// <summary>
    /// 驗證設定 / Validate the options.
    /// 這些錯誤若不在啟動時擋下,現場看到的會是「連不上」或「都判退」,
    /// 兩者都會被誤判成硬體問題而白拆機台。
    /// Left unchecked at startup, these surface on the line as "cannot connect" or "everything
    /// rejects" — both of which get misread as a hardware problem and send someone to strip the
    /// machine down for nothing.
    /// </summary>
    /// <exception cref="ArgumentException">設定不可用 / A value is unusable.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new ArgumentException($"{Model}: Host 不可為空 / must not be empty.", nameof(Host));
        }

        if (Port is < 1 or > 65535)
        {
            throw new ArgumentException(
                $"{Model}: Port 必須在 1–65535,目前為 {Port} / out of range.", nameof(Port));
        }

        if (string.IsNullOrEmpty(TriggerCommand))
        {
            throw new ArgumentException(
                $"{Model}: TriggerCommand 不可為空,請填入裝置通訊規格書上的觸發命令 / "
                + "must not be empty; use the command from the device's communication spec.",
                nameof(TriggerCommand));
        }

        if (string.IsNullOrEmpty(FieldDelimiter))
        {
            throw new ArgumentException(
                $"{Model}: FieldDelimiter 不可為空 / must not be empty.", nameof(FieldDelimiter));
        }

        if (ConnectTimeoutMs <= 0)
        {
            throw new ArgumentException(
                $"{Model}: ConnectTimeoutMs 必須為正,目前為 {ConnectTimeoutMs} / must be positive.",
                nameof(ConnectTimeoutMs));
        }

        if (ResponseTimeoutMs <= 0)
        {
            throw new ArgumentException(
                $"{Model}: ResponseTimeoutMs 必須為正,目前為 {ResponseTimeoutMs} / must be positive.",
                nameof(ResponseTimeoutMs));
        }

        ValidateFields();
    }

    /// <summary>
    /// 驗證本裝置專屬的欄位索引 / Validate this device's own field indexes.
    /// </summary>
    protected abstract void ValidateFields();

    /// <summary>
    /// 設定摘要,供開機時記錄 / A summary line for the startup log.
    /// 這行字是操作員唯一能拿來與原始電文對照的東西,所以索引一定要印出來。
    /// This line is the only thing an operator can hold against a raw frame, so the indexes have
    /// to appear in it.
    /// </summary>
    public string Describe()
    {
        var builder = new StringBuilder();
        builder.Append(DeviceName);
        builder.Append(" 命令 / command ").Append(TriggerCommand);
        builder.Append(", 結束字元 / terminator ").Append(Terminator);
        builder.Append(", 分隔字元 / delimiter '").Append(FieldDelimiter).Append('\'');
        DescribeFields(builder);
        return builder.ToString();
    }

    /// <summary>本裝置專屬欄位的摘要 / Summarise this device's own fields.</summary>
    protected abstract void DescribeFields(StringBuilder builder);

    /// <summary>
    /// 索引不可為負 / Field indexes must not be negative.
    /// 負索引會在解析時被當成「超出範圍」而靜默回 null,表徵是那一項永遠讀不到。
    /// A negative index would be treated as out of range during parsing and quietly yield null,
    /// so the symptom is that one item never reads.
    /// </summary>
    protected void EnsureNonNegative(IReadOnlyList<int> indexes, string name)
    {
        ArgumentNullException.ThrowIfNull(indexes, name);

        for (var i = 0; i < indexes.Count; i++)
        {
            if (indexes[i] < 0)
            {
                throw new ArgumentException(
                    $"{Model}: {name} 的第 {i} 項為 {indexes[i]},欄位索引不可為負 / "
                    + "field indexes must not be negative.",
                    name);
            }
        }
    }
}

/// <summary>電文結束字元 / Frame terminator.</summary>
public enum FrameTerminator
{
    /// <summary>CR (0x0D)</summary>
    Cr,

    /// <summary>LF (0x0A)</summary>
    Lf,

    /// <summary>CR + LF (0x0D 0x0A)</summary>
    CrLf,
}
