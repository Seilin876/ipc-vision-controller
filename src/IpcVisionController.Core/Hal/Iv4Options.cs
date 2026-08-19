using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IpcVisionController.Core.Hal;

/// <summary>
/// 實機 IV4 的連線與電文設定 / Connection and message settings for a real IV4.
///
/// 為什麼欄位位置是設定而不是寫死在程式裡 /
/// Why the field positions are configuration rather than code:
/// IV4 輸出哪些欄位、順序、分隔字元,全由感測器端的設定軟體決定,同一台機器換個程式就變了。
/// 寫死在解析器裡的話,每次現場改 IV4 設定都要重新編譯、重新部署;寫成設定的話,
/// 對照 <see cref="Iv4VisionSensor.RawFrameReceived"/> 記下的原始電文改幾個索引即可。
/// Which fields the IV4 emits, in what order, with what delimiter, is decided entirely in
/// the sensor's own setup software and changes with the loaded program. Hard-coded in the
/// parser, every change on the line would mean a rebuild and a redeploy. As configuration,
/// it is a few indexes edited against the raw frame recorded by
/// <see cref="Iv4VisionSensor.RawFrameReceived"/>.
/// </summary>
public sealed class Iv4Options
{
    /// <summary>感測器 IP / Sensor IP address.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>無協定通訊埠 / Non-protocol communication port.</summary>
    public int Port { get; set; } = 8500;

    /// <summary>觸發命令,不含結束字元 / Trigger command, without its terminator.</summary>
    public string TriggerCommand { get; set; } = "T1";

    /// <summary>命令與回應的結束字元 / Terminator for commands and responses.</summary>
    public Iv4Terminator Terminator { get; set; } = Iv4Terminator.Cr;

    /// <summary>欄位分隔字元 / Field delimiter.</summary>
    public string FieldDelimiter { get; set; } = ",";

    /// <summary>連線逾時 / Connect timeout.</summary>
    public int ConnectTimeoutMs { get; set; } = 5_000;

    /// <summary>單次觸發的回應逾時 / Per-trigger response timeout.</summary>
    public int ResponseTimeoutMs { get; set; } = 3_000;

    /// <summary>
    /// 感測器回報自身異常的電文開頭 / Frame prefix that means the sensor reported its own fault.
    /// 比對到此開頭就轉成 <see cref="DeviceFaultException"/> 停線,而不是當成一張不良標籤。
    /// A frame starting with this becomes a <see cref="DeviceFaultException"/> and stops the
    /// line, rather than being recorded as one bad label.
    /// </summary>
    public string ErrorPrefix { get; set; } = "ER";

    /// <summary>
    /// 視為「該欄位沒有結果」的字樣 / Tokens that mean "this field carries no result".
    /// 比對不分大小寫。空字串一律視為沒有結果,不必列在這裡。
    /// Matched case-insensitively. An empty field always counts as no result and need not
    /// be listed here.
    /// </summary>
    public IReadOnlyList<string> EmptyTokens { get; set; } = ["NG", "NOREAD", "----"];

    /// <summary>條碼內容所在的欄位索引（由 0 起）/ Zero-based field indexes holding decoded code text.</summary>
    public IReadOnlyList<int> CodeDataFields { get; set; } = [];

    /// <summary>
    /// 條碼等級所在的欄位索引 / Zero-based field indexes holding each code's quality level.
    /// 與 <see cref="CodeDataFields"/> 逐位對應;留空表示感測器沒有輸出等級,
    /// 此時 <see cref="Models.CodeResult.Grade"/> 為 null,配方的等級檢查自然失效。
    /// Positionally paired with <see cref="CodeDataFields"/>. Leave empty when the sensor
    /// emits no grade: <see cref="Models.CodeResult.Grade"/> is then null and the recipe's
    /// grade check has nothing to compare against.
    /// </summary>
    public IReadOnlyList<int> CodeGradeFields { get; set; } = [];

    /// <summary>字符檢測各區域文字所在的欄位索引 / Zero-based field indexes holding each OCR region's text.</summary>
    public IReadOnlyList<int> CharacterTextFields { get; set; } = [];

    /// <summary>結束字元的實際字串 / The terminator's actual characters.</summary>
    public string TerminatorText => Terminator switch
    {
        Iv4Terminator.Cr => "\r",
        Iv4Terminator.Lf => "\n",
        Iv4Terminator.CrLf => "\r\n",
        _ => throw new InvalidOperationException($"未知的結束字元 / unknown terminator: {Terminator}."),
    };

    /// <summary>
    /// 驗證設定 / Validate the options.
    /// 這些錯誤若不在啟動時擋下,現場看到的會是「連不上」或「都判退」,
    /// 兩者都會被誤判成硬體問題而白拆機台。
    /// Left unchecked at startup, these surface on the line as "cannot connect" or
    /// "everything rejects" — both of which get misread as a hardware problem and send
    /// someone to strip the machine down for nothing.
    /// </summary>
    /// <exception cref="ArgumentException">設定不可用 / A value is unusable.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            throw new ArgumentException("Host 不可為空 / must not be empty.", nameof(Host));
        }

        if (Port is < 1 or > 65535)
        {
            throw new ArgumentException($"Port 必須在 1–65535,目前為 {Port} / out of range.", nameof(Port));
        }

        if (string.IsNullOrEmpty(TriggerCommand))
        {
            throw new ArgumentException("TriggerCommand 不可為空 / must not be empty.", nameof(TriggerCommand));
        }

        if (string.IsNullOrEmpty(FieldDelimiter))
        {
            throw new ArgumentException("FieldDelimiter 不可為空 / must not be empty.", nameof(FieldDelimiter));
        }

        if (ConnectTimeoutMs <= 0)
        {
            throw new ArgumentException(
                $"ConnectTimeoutMs 必須為正,目前為 {ConnectTimeoutMs} / must be positive.", nameof(ConnectTimeoutMs));
        }

        if (ResponseTimeoutMs <= 0)
        {
            throw new ArgumentException(
                $"ResponseTimeoutMs 必須為正,目前為 {ResponseTimeoutMs} / must be positive.", nameof(ResponseTimeoutMs));
        }

        EnsureNonNegative(CodeDataFields, nameof(CodeDataFields));
        EnsureNonNegative(CodeGradeFields, nameof(CodeGradeFields));
        EnsureNonNegative(CharacterTextFields, nameof(CharacterTextFields));

        // 等級欄位比內容欄位多,表示對應關係已經錯位,解出來的等級會掛到別筆條碼上
        // More grade fields than data fields means the pairing is already out of step and a
        // grade would be attached to the wrong code.
        if (CodeGradeFields.Count > CodeDataFields.Count)
        {
            throw new ArgumentException(
                $"CodeGradeFields ({CodeGradeFields.Count} 項) 不可多於 CodeDataFields ({CodeDataFields.Count} 項) / " +
                "cannot outnumber CodeDataFields.",
                nameof(CodeGradeFields));
        }

        // 兩份索引都空的話,每次觸發都回空結果,配方的筆數檢查會讓每一張標籤都判退
        // With both index lists empty every trigger returns nothing, and the recipe's count
        // check then rejects every single label.
        if (CodeDataFields.Count == 0 && CharacterTextFields.Count == 0)
        {
            throw new ArgumentException(
                "CodeDataFields 與 CharacterTextFields 不可同時為空,否則每張標籤都會判退 / " +
                "cannot both be empty, or every label rejects.",
                nameof(CodeDataFields));
        }
    }

    /// <summary>
    /// 讀取設定檔；檔案不存在時回傳 null / Load the file, returning null when it does not exist.
    /// 「檔案不存在」是有意義的狀態而非錯誤 —— 那代表這台機器要跑模擬裝置。
    /// A missing file is a meaningful state rather than an error: it means this machine runs
    /// on mock devices.
    /// </summary>
    public static async Task<Iv4Options?> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        var options = await JsonSerializer
            .DeserializeAsync<Iv4Options>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ArgumentException($"設定檔內容為 null / file deserialised to null: {path}", nameof(path));

        options.Validate();
        return options;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };

    private static void EnsureNonNegative(IReadOnlyList<int> indexes, string name)
    {
        ArgumentNullException.ThrowIfNull(indexes, name);

        for (var i = 0; i < indexes.Count; i++)
        {
            if (indexes[i] < 0)
            {
                throw new ArgumentException(
                    $"{name} 的第 {i} 項為 {indexes[i]},欄位索引不可為負 / field indexes must not be negative.", name);
            }
        }
    }

    /// <summary>設定摘要,供啟動時記錄 / A summary line for the startup log.</summary>
    public string Describe()
    {
        var builder = new StringBuilder();
        builder.Append(Host).Append(':').Append(Port);
        builder.Append(" 命令 / command ").Append(TriggerCommand);
        builder.Append(", 條碼欄位 / code fields [").AppendJoin(' ', CodeDataFields).Append(']');
        builder.Append(", 等級欄位 / grade fields [").AppendJoin(' ', CodeGradeFields).Append(']');
        builder.Append(", 字符欄位 / character fields [").AppendJoin(' ', CharacterTextFields).Append(']');
        return builder.ToString();
    }
}

/// <summary>電文結束字元 / Message terminator.</summary>
public enum Iv4Terminator
{
    /// <summary>CR (0x0D)</summary>
    Cr,

    /// <summary>LF (0x0A)</summary>
    Lf,

    /// <summary>CR + LF (0x0D 0x0A)</summary>
    CrLf,
}
