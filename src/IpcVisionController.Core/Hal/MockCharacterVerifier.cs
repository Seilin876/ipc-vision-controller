using IpcVisionController.Core.Models;

namespace IpcVisionController.Core.Hal;

/// <summary>
/// 標籤字符檢測器的模擬實作 / Mock for the character verifier (Keyence IV4 AI OCR).
///
/// 換成實機時要替換的位置 / What to replace for real hardware:
/// - ConnectAsync：開啟 EtherNet/IP 連線,或以 TcpClient 連 8500 埠走無協定通訊,
///   並確認感測器已載入正確的程式編號（換線時最常出錯的一步）。
///   ConnectAsync: open the EtherNet/IP link, or a TcpClient to port 8500 for the
///   non-protocol channel, and confirm the sensor has the right program number loaded —
///   the step most often missed at a changeover.
/// - TriggerAsync：下觸發後等待「檢測完成」位元,逐一讀出各 OCR 區域的
///   辨識字串與 PASS/FAIL,一個區域一筆。
///   TriggerAsync: trigger, wait for the inspection-complete bit, then read each OCR
///   region's recognised string and its PASS/FAIL — one entry per region.
///
/// 注意：實機讀取務必用 NetworkStream.ReadAsync(..., cancellationToken),
/// 不要用 ReceiveTimeout —— 同步逾時會擋住執行緒集區。
/// NOTE: on real hardware always read with NetworkStream.ReadAsync(..., token); do not
/// rely on ReceiveTimeout, which blocks a thread-pool thread.
/// </summary>
public sealed class MockCharacterVerifier : ICharacterVerifier
{
    private readonly Random _random;

    /// <param name="seed">固定種子讓測試可重現 / A fixed seed makes tests reproducible.</param>
    public MockCharacterVerifier(int? seed = null)
    {
        _random = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    public string Name => "Keyence IV4 (MOCK)";

    public bool IsConnected { get; private set; }

    /// <summary>模擬曝光與判別耗時的下限 / Lower bound of simulated capture + judge time.</summary>
    public TimeSpan MinLatency { get; init; } = TimeSpan.FromMilliseconds(120);

    /// <summary>模擬耗時的上限 / Upper bound of simulated capture + judge time.</summary>
    public TimeSpan MaxLatency { get; init; } = TimeSpan.FromMilliseconds(320);

    /// <summary>
    /// 已啟用的 OCR 區域數 / Number of enabled OCR regions.
    /// IV4 最多 10 個區域;預設與 <see cref="RecipeModel.ExpectedCharacterRegionCount"/> 一致。
    /// The IV4 supports up to 10; the default matches
    /// <see cref="RecipeModel.ExpectedCharacterRegionCount"/>.
    /// </summary>
    public int RegionsPerTrigger { get; set; } = 1;

    /// <summary>模擬辨識出的字串 / The string the mock claims to have recognised.</summary>
    public string RecognisedText { get; set; } = "LOT26A";

    /// <summary>模擬單一區域的判退比率 (0.0–1.0) / Simulated per-region FAIL rate.</summary>
    public double FailRate { get; init; }

    /// <summary>
    /// 測試用：強制回傳這些區域字串 / Test hook: return exactly these region strings.
    /// 元素為 null 代表該區域辨識失敗 / A null element means that region failed to recognise.
    /// </summary>
    public IReadOnlyList<string?>? ForcedTexts { get; set; }

    /// <summary>
    /// 被觸發過幾次 / How many times this has been triggered.
    ///
    /// 「有沒有被觸發」有時比「回了什麼」更重要:兩站相隔數格時,標籤還沒走到檢測站的那幾個
    /// 週期刻意不觸發它 —— 那時檢測站底下躺的是無法歸屬的標籤。而「沒有觸發」這件事無法從
    /// 回傳值觀察,只能由計數器證明。
    /// Whether it fired matters more than what it returned, sometimes: while the stations are several
    /// pitches apart, the cycles before a label reaches the verifier deliberately do not trigger it,
    /// because what sits under it then belongs to no known label. Not firing cannot be observed from a
    /// return value; only a counter can show it.
    /// </summary>
    public int TriggerCount { get; private set; }

    /// <summary>測試用：強制沒有任何區域回報 / Test hook: force an empty result list.</summary>
    public bool ForceNoResult { get; set; }

    /// <summary>測試用：強制感測器回報自身異常 / Test hook: force a device-level fault.</summary>
    public bool SimulateFault { get; set; }

    /// <summary>測試用：強制延遲以觸發上層逾時 / Test hook: stall to trip the caller's timeout.</summary>
    public TimeSpan? ForcedLatency { get; set; }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        IsConnected = true;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<CharacterResult>> TriggerAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException($"{Name} 尚未連線 / is not connected.");
        }

        TriggerCount++;

        var latency = ForcedLatency ?? RandomLatency();
        await Task.Delay(latency, cancellationToken).ConfigureAwait(false);

        if (SimulateFault)
        {
            throw new DeviceFaultException($"{Name} 回報內部錯誤 / reported an internal error.");
        }

        if (ForceNoResult)
        {
            return [];
        }

        if (ForcedTexts is not null)
        {
            return [.. ForcedTexts.Select((text, index) => ToResult(index, text))];
        }

        if (RegionsPerTrigger < 0)
        {
            throw new InvalidOperationException(
                $"RegionsPerTrigger 不可為負,目前為 {RegionsPerTrigger} / must not be negative, got {RegionsPerTrigger}.");
        }

        var results = new List<CharacterResult>(RegionsPerTrigger);
        for (var index = 0; index < RegionsPerTrigger; index++)
        {
            // 判退的區域仍然回報 —— 它有辨識到字,只是與主文字不符。
            // 這與「區域根本沒被觸發到」（不出現在清單裡）是不同的不良。
            // A failing region still reports: it recognised something, it just did not
            // match the master text. That differs from a region that was never triggered
            // at all, which simply does not appear in the list.
            results.Add(_random.NextDouble() < FailRate
                ? new CharacterResult(index, RecognisedText, Verdict.Fail)
                : new CharacterResult(index, RecognisedText, Verdict.Pass));
        }

        return results;
    }

    private static CharacterResult ToResult(int index, string? text) => text is null
        ? new CharacterResult(index, Text: null, Judge: Verdict.Fail)
        : new CharacterResult(index, text, Verdict.Pass);

    private TimeSpan RandomLatency()
    {
        var min = MinLatency.TotalMilliseconds;
        var max = Math.Max(min, MaxLatency.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(min + (_random.NextDouble() * (max - min)));
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}
