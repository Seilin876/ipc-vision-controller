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
    }

    private static void EnsurePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentException($"{name} 必須為正,目前為 {value} / must be positive, got {value}.", name);
        }
    }
}
