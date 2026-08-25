using System.Text.Json;
using System.Text.Json.Serialization;

namespace IpcVisionController.Core.Hal;

/// <summary>
/// device.json 的內容：這台機器接了哪些實機裝置 /
/// The contents of device.json: which real devices this machine has.
///
/// 為什麼分成兩段而不是一段 / Why two sections rather than one:
/// SR-X300 與 IV4 是兩台獨立裝置,各自有 IP、埠、觸發命令與欄位配置,而且會在不同時間導入 ——
/// 現在只有讀碼器,IV4 之後才上。分段讓「只接讀碼器」成為一個可以被表達的正常狀態,
/// 而不是一組半填的設定。
/// The SR-X300 and the IV4 are separate devices with their own address, port, command and field
/// layout, and they arrive at different times: today there is only the reader. Two sections make
/// "reader only" a state the file can actually express, rather than a half-filled one.
///
/// 缺一段代表什麼 / What a missing section means:
/// CodeReader 是必要的 —— 配方的條碼筆數下限為 1,沒有讀碼器就每張標籤都判退,
/// 那不是一種可以執行的組態。
/// CharacterVerifier 可以缺,代表這台機器還沒有字符檢測。此時配方的字符區域數要設 0,
/// 否則會以「區域數不足」判退每一張標籤。
/// CodeReader is required: the recipe's code count has a floor of one, so without a reader every
/// label rejects, which is not a runnable configuration. CharacterVerifier may be absent, meaning
/// this machine has no character verification yet; the recipe's region count must then be zero or
/// every label rejects on the count.
/// </summary>
public sealed class DeviceSettings
{
    /// <summary>SR-X300 讀碼器 / The SR-X300 reader.</summary>
    public SrX300Options? CodeReader { get; set; }

    /// <summary>IV4 字符檢測器；null 表示尚未導入 / The IV4 verifier; null when not yet installed.</summary>
    public Iv4Options? CharacterVerifier { get; set; }

    /// <summary>
    /// 驗證整份設定 / Validate the whole file.
    /// </summary>
    /// <exception cref="ArgumentException">設定不可用 / A value is unusable.</exception>
    public void Validate()
    {
        if (CodeReader is null)
        {
            throw new ArgumentException(
                "device.json 必須包含 CodeReader 這一段 / must contain a CodeReader section.",
                nameof(CodeReader));
        }

        CodeReader.Validate();
        CharacterVerifier?.Validate();

        // 兩台裝置設成同一個位址與埠,幾乎確定是複製設定時忘了改。
        // 真接上去的話,兩條連線會搶同一台裝置 —— 多數工業裝置只接受一條,
        // 於是其中一台在初始化時被拒絕,而錯誤訊息會指向無辜的那一台。
        // The same address and port on both devices is almost certainly a copy that was never
        // edited. Attached for real, two links would contend for one device — most industrial
        // devices accept only one — so one of them is refused during initialise and the error
        // names whichever lost the race rather than the misconfiguration.
        if (CharacterVerifier is not null
            && string.Equals(CodeReader.Host, CharacterVerifier.Host, StringComparison.OrdinalIgnoreCase)
            && CodeReader.Port == CharacterVerifier.Port)
        {
            throw new ArgumentException(
                $"CodeReader 與 CharacterVerifier 不可設為同一個位址與埠（{CodeReader.Host}:{CodeReader.Port}）"
                + " / must not share one address and port.",
                nameof(CharacterVerifier));
        }
    }

    /// <summary>
    /// 讀取設定檔；檔案不存在時回傳 null / Load the file, returning null when it does not exist.
    /// 「檔案不存在」交由呼叫端決定意義 —— 本組件不假設那代表要跑模擬。
    /// What a missing file means is the caller's decision; this assembly does not assume it means
    /// "run on mocks".
    /// </summary>
    public static async Task<DeviceSettings?> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        var settings = await JsonSerializer
            .DeserializeAsync<DeviceSettings>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ArgumentException($"設定檔內容為 null / file deserialised to null: {path}", nameof(path));

        // 舊版是平鋪格式（Host/Port 直接放在最外層,沒有 CodeReader 這一段）。
        // 反序列化不會失敗,只會得到兩段都是 null 的物件 —— 那會變成一句
        // 「必須包含 CodeReader」的訊息,而現場手上明明有一個看起來填好的檔案,
        // 於是排查方向完全錯掉。這裡直接指出格式已變。
        // The old file was flat, with Host and Port at the top level and no CodeReader section.
        // Deserialising it does not fail; it simply yields both sections null, which would produce
        // a "must contain a CodeReader" message while the line is holding a file that looks
        // filled in — sending the diagnosis in entirely the wrong direction. Name the change.
        if (settings is { CodeReader: null, CharacterVerifier: null })
        {
            throw new ArgumentException(
                $"設定檔格式已變更,請改用兩段式（CodeReader / CharacterVerifier）並參照 device.sample.json：{path}"
                + " / the file format changed to two sections; see device.sample.json.",
                nameof(path));
        }

        settings.Validate();
        return settings;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };
}
