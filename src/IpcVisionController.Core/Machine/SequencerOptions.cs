namespace IpcVisionController.Core.Machine;

/// <summary>
/// 站別設定 / Station configuration.
///
/// 刻意與 <see cref="Models.RecipeModel"/> 分開：
/// 配方是「這個機種怎麼判定」（換線會變）,
/// 本設定是「這台機構長怎樣」（同一台機器一輩子不太會變）。
/// 混在一起的話,換線就會誤改機構參數。
/// Deliberately separate from <see cref="Models.RecipeModel"/>: the recipe says how
/// *this product model* is judged (changes at every changeover), while these options
/// describe *this machine's mechanics* (essentially fixed for the machine's life).
/// Merging the two invites a changeover to clobber mechanical parameters.
/// </summary>
public sealed class SequencerOptions
{
    /// <summary>
    /// 一格標籤的進給量（脈波）/ Pulses in one label pitch.
    /// 由標籤間距與傳動比決定,屬於機構參數;標籤規格改變時才需要重算。
    /// Derived from the label pitch and the drive ratio — a mechanical parameter,
    /// recomputed only when the label stock itself changes.
    /// </summary>
    public int FeedPitchPulses { get; init; } = 10_000;

    /// <summary>進給速度（脈波/秒）/ Feed velocity in pulses per second.</summary>
    public int FeedSpeedPulsePerSecond { get; init; } = 20_000;

    /// <summary>單次進給逾時 / Per-feed timeout.</summary>
    public TimeSpan FeedTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>讀碼逾時 / Code-read timeout.</summary>
    public TimeSpan CodeReadTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>字符檢測逾時 / Character-verification timeout.</summary>
    public TimeSpan CharacterVerifyTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>各裝置連線逾時 / Per-device connect timeout.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 讀碼站到字符檢測站相隔幾格 / Pitches from the code-reading station to the verification station.
    ///
    /// 0 表示兩個感測器瞄同一個位置,同一張標籤在一次停頓中被兩台都看過。
    /// 大於 0 表示標籤先在讀碼站被掃,再前進這麼多格才抵達字符檢測站 ——
    /// 現場機構是「先 SR-X300 掃碼,再觸發 IV4 檢測」,因此正常值為正。
    /// Zero means both sensors look at the same position and one label is seen by both in a single
    /// dwell. A positive value means the label is scanned at the reading station and then advances
    /// this many pitches before reaching the verification station, which is what the line's mechanism
    /// does: scan on the SR-X300 first, then trigger the IV4.
    ///
    /// 為什麼這個值必須存在,而不是預設兩台看同一張 /
    /// Why this has to exist rather than assuming both see the same label:
    /// 檢測週期是「進給一格 → 讀碼 → 字符檢測 → 合成一筆紀錄」。若兩站相隔 k 格而程式當成 0,
    /// 那筆紀錄會把讀碼站看到的第 N 張與檢測站看到的第 N−k 張湊在一起。
    /// 它不會報錯,良率也正常,但條碼與字符對不上 —— 而且每一筆看起來都完整,
    /// 事後從追溯資料裡看不出來。那是這支程式能造成的最嚴重錯誤。
    /// The cycle is feed one pitch, read the code, verify the characters, write one record. If the two
    /// stations are k pitches apart and the program assumes zero, that record pairs the reading
    /// station's label N with the verification station's label N−k. Nothing errors, the yield looks
    /// normal, and the code and the characters simply do not belong to each other — while every row
    /// looks complete and nothing in the traceability data reveals it. That is the worst thing this
    /// program can do.
    ///
    /// 為什麼是機構參數而不是配方參數 / Why this is mechanical rather than per-product:
    /// 它由兩個感測器的安裝距離決定。若日後標籤間距隨機種改變而安裝距離不變,格數就會隨機種變動,
    /// 那時這個值要改成由「安裝距離 ÷ 標籤間距」推算,或移進配方 —— 目前 FeedPitchPulses
    /// 同樣是機構參數,兩者放在一起才一致。
    /// It follows from how far apart the two sensors are mounted. If the label pitch later varies by
    /// product while the mounting distance does not, the pitch count becomes product-dependent and
    /// this should be derived from mounting distance over pitch, or moved into the recipe. For now
    /// FeedPitchPulses is mechanical too, and keeping them together is what makes them consistent.
    /// </summary>
    public int InspectionOffsetPitches { get; init; }

    /// <summary>
    /// 兩格標籤之間的間隔 / Dwell between label pitches.
    /// 實機應改為等待上游的「料帶到位」訊號,而非固定延遲。
    /// On real hardware, replace this with a wait on the upstream web-in-position signal
    /// rather than a fixed delay.
    /// </summary>
    public TimeSpan CycleInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 驗證設定 / Validate the options.
    /// 逾時值為 0 或負數會讓每個動作立刻「逾時」,是最容易漏掉的設定錯誤。
    /// A zero or negative timeout makes every step fail instantly — the easiest
    /// configuration mistake to miss.
    /// </summary>
    /// <exception cref="ArgumentException">設定不合法 / A value is unusable.</exception>
    public void Validate()
    {
        // 進給量為 0 代表料帶不動,同一張標籤會被反覆檢測並反覆寫入追溯紀錄
        // A zero pitch leaves the web still, so one label is inspected — and logged —
        // over and over.
        if (FeedPitchPulses <= 0)
        {
            throw new ArgumentException(
                $"FeedPitchPulses 必須為正整數,目前為 {FeedPitchPulses} / must be positive.",
                nameof(FeedPitchPulses));
        }

        if (FeedSpeedPulsePerSecond <= 0)
        {
            throw new ArgumentException(
                $"FeedSpeedPulsePerSecond 必須為正整數,目前為 {FeedSpeedPulsePerSecond} / must be positive.",
                nameof(FeedSpeedPulsePerSecond));
        }

        EnsurePositive(FeedTimeout, nameof(FeedTimeout));
        EnsurePositive(CodeReadTimeout, nameof(CodeReadTimeout));
        EnsurePositive(CharacterVerifyTimeout, nameof(CharacterVerifyTimeout));
        EnsurePositive(ConnectTimeout, nameof(ConnectTimeout));

        if (CycleInterval < TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"CycleInterval 不可為負,目前為 {CycleInterval} / must not be negative.",
                nameof(CycleInterval));
        }

        if (InspectionOffsetPitches < 0)
        {
            throw new ArgumentException(
                $"InspectionOffsetPitches 不可為負,目前為 {InspectionOffsetPitches}"
                + "（0 表示兩台感測器瞄同一個位置）/ must not be negative (0 means both sensors "
                + "look at the same position).",
                nameof(InspectionOffsetPitches));
        }

        // 上限是防打錯,不是機構限制。誤植成 40 的後果是:前 40 張標籤全部只掃碼不留紀錄,
        // 而畫面上看起來就是「按了單次觸發卻什麼都沒發生」—— 那會被當成程式壞了。
        // 真的需要超過這個距離,代表機構改了,那時連同這個上限一起改才是誠實的做法。
        // The cap guards against a typo rather than the mechanism. A stray 40 would leave the first
        // forty labels scanned but unrecorded, which on screen reads as "Trigger once does nothing" and
        // gets taken for a broken program. If a machine genuinely needs more travel than this, the
        // mechanism changed, and raising the cap alongside it is the honest way to say so.
        if (InspectionOffsetPitches > MaxInspectionOffsetPitches)
        {
            throw new ArgumentException(
                $"InspectionOffsetPitches 為 {InspectionOffsetPitches},超過上限 {MaxInspectionOffsetPitches}"
                + $" / exceeds the sanity cap of {MaxInspectionOffsetPitches}.",
                nameof(InspectionOffsetPitches));
        }
    }

    /// <summary>
    /// 兩站相隔格數的合理上限 / Sanity cap on the station offset.
    /// 現場機構為 4 格以內;上限留寬是為了讓「打錯」與「機構真的改了」分得開。
    /// The line's mechanism is within four pitches; the cap is left loose so that a typo and a genuine
    /// change of mechanism remain distinguishable.
    /// </summary>
    public const int MaxInspectionOffsetPitches = 16;

    private static void EnsurePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentException($"{name} 必須為正,目前為 {value} / must be positive, got {value}.", name);
        }
    }
}
