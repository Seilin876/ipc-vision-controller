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
    /// 讀碼站到字符檢測站的距離（脈波)；未導入字符檢測時可省略 /
    /// Pulses from the code-reading station to the verification station; may be omitted with no verifier.
    ///
    /// 為什麼記距離而不是記格數 / Why a distance rather than a pitch count:
    /// 字符檢測器鎖在固定位置,這個距離一輩子不變 —— 它屬於 device.json 描述的「這台機器長怎樣」。
    /// 而「相隔幾格」會隨標籤長度改變,所以它屬於配方,而且程式自己算得出來
    /// （距離 ÷ 配方的一格脈波數),不需要現場動手換算。少一步人工換算,就少一種
    /// 「算錯而且不會報錯」的失誤。
    /// The verifier is bolted in one place, so this distance never changes and belongs to what device.json
    /// describes: the shape of this machine. A pitch count does change with label length, so it belongs to
    /// the recipe — and the program can work it out itself by dividing, sparing the line a manual conversion
    /// and with it one more way to be wrong without being told.
    ///
    /// 為什麼是可為 null 而不是預設 0 / Why nullable rather than defaulting to zero:
    /// 0 是合法的值,它表示「兩台感測器瞄同一個位置」。若把「漏填」也當成 0,一台真的有下游
    /// 檢測站的機器就會安靜地把不同標籤的兩半湊成一筆紀錄 —— 那不會報錯,良率也正常。
    /// 因此:有 CharacterVerifier 就必須明確填寫;沒有的話這個值無關,可以省略。
    /// Zero is a legal value meaning both sensors look at the same position. Treating an omission as zero
    /// would let a machine that genuinely has a downstream station quietly pair halves of different labels,
    /// without an error and with a normal-looking yield. So: required whenever CharacterVerifier is present,
    /// and irrelevant — omittable — when it is not.
    /// </summary>
    public int? InspectionStationDistancePulses { get; set; }

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

        // 有字符檢測站就必須說明它裝在下游多遠。漏填而被當成 0 的後果是每一筆紀錄都把
        // 讀碼站看到的第 N 張與檢測站看到的第 N−k 張湊在一起 —— 不報錯、良率正常、
        // 每一筆看起來都完整。這是本檔最不能默認的一個值。
        // A verification station has to say how far downstream it sits. An omission taken as zero would pair
        // the reading station's label N with the verification station's label N−k in every record, without an
        // error, with a normal yield and with every row looking complete. It is the one value in this file
        // that must not have a silent default.
        if (CharacterVerifier is not null && InspectionStationDistancePulses is null)
        {
            throw new ArgumentException(
                "有 CharacterVerifier 時必須填寫 InspectionStationDistancePulses"
                + "（兩台瞄同一位置就填 0）/ is required whenever CharacterVerifier is present; use 0 when "
                + "both sensors look at the same position.",
                nameof(InspectionStationDistancePulses));
        }

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
