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
    /// <summary>讀碼位置（脈波）/ Barcode-read position in pulses.</summary>
    public int ScanPositionPulse { get; init; } = 10_000;

    /// <summary>拍照位置（脈波）/ Vision-capture position in pulses.</summary>
    public int InspectPositionPulse { get; init; } = 25_000;

    /// <summary>移動速度（脈波/秒）/ Move velocity in pulses per second.</summary>
    public int MoveSpeedPulsePerSecond { get; init; } = 20_000;

    /// <summary>單次定位逾時 / Per-move timeout.</summary>
    public TimeSpan MoveTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>讀碼逾時 / Barcode read timeout.</summary>
    public TimeSpan BarcodeTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>拍照判別逾時 / Vision trigger timeout.</summary>
    public TimeSpan VisionTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>各裝置連線逾時 / Per-device connect timeout.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 兩個工件之間的間隔 / Dwell between parts.
    /// 實機應改為等待上游的「工件到位」訊號,而非固定延遲。
    /// On real hardware, replace this with a wait on the upstream part-present signal
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
        if (MoveSpeedPulsePerSecond <= 0)
        {
            throw new ArgumentException(
                $"MoveSpeedPulsePerSecond 必須為正整數,目前為 {MoveSpeedPulsePerSecond} / must be positive.",
                nameof(MoveSpeedPulsePerSecond));
        }

        EnsurePositive(MoveTimeout, nameof(MoveTimeout));
        EnsurePositive(BarcodeTimeout, nameof(BarcodeTimeout));
        EnsurePositive(VisionTimeout, nameof(VisionTimeout));
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
