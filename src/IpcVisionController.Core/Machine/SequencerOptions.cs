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
    /// 讀碼站到字符檢測站的距離（脈波)/ Pulses from the code-reading station to the verification station.
    ///
    /// 0 表示兩個感測器瞄同一個位置,同一張標籤在一次停頓中被兩台都看過。
    /// Zero means both sensors look at the same position and one label is seen by both in a single dwell.
    ///
    /// 為什麼記距離而不是記格數 / Why a distance rather than a pitch count:
    /// 字符檢測器鎖在固定位置,所以這個距離一輩子不變 —— 它是不折不扣的機構參數。
    /// 而「相隔幾格」會隨標籤長度改變:同一個距離,標籤短就是多格,標籤長就是少格。
    /// 若設定記的是格數,換機種時現場得自己重算,而算錯不會報錯,只會讓追溯紀錄把不同標籤的
    /// 兩半湊在一起。記距離、讓程式除以配方裡的一格脈波數,那一步人工換算就從流程裡消失。
    /// The verifier is bolted in one place, so this distance never changes — it is mechanical in the
    /// strictest sense. A pitch count is not: the same distance is more pitches with short labels and fewer
    /// with long ones. Storing the count would leave the line to recompute it at every changeover, where a
    /// mistake never announces itself and merely pairs halves of different labels in the traceability
    /// record. Storing the distance and dividing by the recipe's pitch removes that conversion.
    ///
    /// 為什麼這件事非做不可 / Why this matters at all:
    /// 檢測週期是「進給一格 → 讀碼 → 字符檢測 → 合成一筆紀錄」。若兩站相隔 k 格而程式當成 0,
    /// 那筆紀錄會把讀碼站看到的第 N 張與檢測站看到的第 N−k 張湊在一起。它不會報錯,良率也正常,
    /// 但條碼與字符不屬於同一張標籤 —— 而且每一筆看起來都完整,事後從資料裡看不出來。
    /// The cycle is feed one pitch, read the code, verify the characters, write one record. If the stations
    /// are k pitches apart and the program assumes zero, that record pairs the reading station's label N
    /// with the verification station's label N−k. Nothing errors, the yield looks normal, and the code and
    /// characters simply do not belong together — while every row looks complete and nothing in the data
    /// reveals it.
    /// </summary>
    public int InspectionStationDistancePulses { get; init; }

    /// <summary>
    /// 換算成相隔幾格 / The distance expressed as a pitch count.
    /// </summary>
    /// <param name="feedPitchPulses">配方裡的一格脈波數 / The recipe's pulses per feed.</param>
    /// <returns>
    /// 四捨五入後的格數。除不盡代表感測器沒有落在標籤邊界上,那幾乎一定是有個數字填錯了,
    /// 但取整仍取最接近的一格 —— 讓機台跑得起來,再由初始化訊息把不整齊說出來。
    /// The pitch count, rounded. A remainder means the sensor does not sit on a label boundary, which is
    /// almost certainly a mistyped number; the nearest whole pitch is still used so the machine runs, with
    /// the initialise log saying that it did not divide evenly.
    /// </returns>
    public int OffsetPitchesFor(int feedPitchPulses)
    {
        if (feedPitchPulses <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(feedPitchPulses),
                feedPitchPulses,
                "一格脈波數必須為正 / pulses per feed must be positive.");
        }

        return (int)Math.Round(
            InspectionStationDistancePulses / (double)feedPitchPulses,
            MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// 距離是否剛好是整數格 / Whether the distance is a whole number of pitches.
    /// 除不盡時感測器停在兩張標籤之間,那個位置不可重現 —— 初始化時要說出來。
    /// A remainder leaves the sensor between two labels, a position that does not repeat, and the initialise
    /// log has to say so.
    /// </summary>
    public bool DistanceDividesEvenlyBy(int feedPitchPulses)
        => feedPitchPulses > 0 && InspectionStationDistancePulses % feedPitchPulses == 0;

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

        if (InspectionStationDistancePulses < 0)
        {
            throw new ArgumentException(
                $"InspectionStationDistancePulses 不可為負,目前為 {InspectionStationDistancePulses}"
                + "（0 表示兩台感測器瞄同一個位置）/ must not be negative (0 means both sensors look at "
                + "the same position).",
                nameof(InspectionStationDistancePulses));
        }

        // 距離的上限無法在此判斷:合不合理取決於一格有多長,而一格的長度在配方裡。
        // 因此上限檢查放在協調器初始化時 —— 那裡兩個數字都在手上。
        // A cap cannot be judged here, because whether a distance is reasonable depends on how long a pitch
        // is and that lives in the recipe. The check therefore sits in the sequencer's initialise, where
        // both numbers are in hand.
    }

    /// <summary>
    /// 換算後格數的合理上限 / Sanity cap on the derived pitch count.
    ///
    /// 距離除以一格脈波數之後才檢查。誤植的表徵是「前 N 張標籤只掃碼、不留紀錄」,
    /// 而畫面上看起來就是「按了單次觸發卻什麼都沒發生」—— 那會被當成程式壞了。
    /// 現場機構為 4 格以內;上限留寬是為了讓「打錯」與「機構真的改了」分得開。
    /// Checked after the distance is divided by the pulses per feed. A mistyped value presents as the first
    /// N labels being scanned without a record, which on screen reads as "Trigger once does nothing" and
    /// gets taken for a broken program. The line's mechanism is within four pitches; the cap is left loose
    /// so that a typo and a genuine change of mechanism stay distinguishable.
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
