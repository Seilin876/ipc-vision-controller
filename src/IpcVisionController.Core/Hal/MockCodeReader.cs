using System.Text;
using IpcVisionController.Core.Models;

namespace IpcVisionController.Core.Hal;

/// <summary>
/// 條碼讀取器的模擬實作 / Mock for the code reader (Keyence SR series over EtherNet/IP).
///
/// 換成實機時要替換的位置 / What to replace for real hardware:
/// - ConnectAsync：開啟 EtherNet/IP 連線並建立循環通訊（Implicit Messaging）的
///   輸入/輸出組合,或以 TcpClient 連 9004 埠走無協定通訊。
///   ConnectAsync: open the EtherNet/IP link and set up the cyclic (implicit messaging)
///   input/output assemblies, or connect a TcpClient to port 9004 for the
///   non-protocol channel.
/// - TriggerAsync：對輸出組合下觸發位元,等待輸入組合的「讀取完成」位元,
///   再把整個結果區塊逐筆解出 —— SR 系列會在同一個區塊裡回傳本次讀到的全部條碼。
///   TriggerAsync: raise the trigger bit in the output assembly, wait for the
///   read-complete bit in the input assembly, then decode the whole result block. The
///   SR series returns every code from that read in the same block.
///
/// 三種結果的分野 / The three outcomes, kept distinct:
/// - 讀到 → 有內容的 <see cref="CodeResult"/>。 A decode → a populated CodeResult.
/// - 完全沒讀到 → 空清單。工件問題,判退後繼續生產。
///   Nothing read → an empty list. A part problem: reject and keep running.
/// - 感測器自身異常 → <see cref="DeviceFaultException"/>。設備問題,停線。
///   The sensor itself is unwell → DeviceFaultException. An equipment problem: stop.
/// </summary>
public sealed class MockCodeReader : ICodeReader
{
    /// <summary>模擬條碼的字元集（實務條碼多為大寫英數）/ Charset for generated codes (field codes are upper alphanumeric).</summary>
    private const string Charset = "ABCDEFGHJKLMNPQRSTUVWXYZ0123456789";

    private readonly Random _random;

    /// <param name="seed">固定種子讓測試可重現 / A fixed seed makes tests reproducible.</param>
    public MockCodeReader(int? seed = null)
    {
        _random = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    public string Name => "Keyence SR-1000 (MOCK)";

    public bool IsConnected { get; private set; }

    /// <summary>模擬讀取耗時的下限 / Lower bound of simulated read time.</summary>
    public TimeSpan MinLatency { get; init; } = TimeSpan.FromMilliseconds(60);

    /// <summary>模擬讀取耗時的上限 / Upper bound of simulated read time.</summary>
    public TimeSpan MaxLatency { get; init; } = TimeSpan.FromMilliseconds(180);

    /// <summary>
    /// 每次觸發視野內的條碼張數 / Codes in the field of view per trigger.
    /// 預設與 <see cref="RecipeModel.ExpectedCodeCount"/> 的預設值一致;
    /// 調低即可模擬「漏貼一張標籤」。
    /// Defaults to match <see cref="RecipeModel.ExpectedCodeCount"/>; lowering it
    /// simulates a missing label.
    /// </summary>
    public int CodesPerTrigger { get; set; } = 1;

    /// <summary>
    /// 產生的條碼長度 / Length of generated codes.
    /// 預設與 <see cref="RecipeModel.BarcodeLength"/> 的預設值一致,
    /// 調高或調低即可模擬「掛錯機種」。
    /// Defaults to match <see cref="RecipeModel.BarcodeLength"/>; changing it simulates
    /// running the wrong product model against the loaded recipe.
    /// </summary>
    public int CodeLength { get; set; } = 12;

    /// <summary>
    /// 回報的讀取餘裕度 / Reported matching level.
    /// SR 系列的刻度是 0–100,愈高愈好;調低即可模擬印刷品質劣化。
    /// The SR-series scale is 0–100, higher is better; lowering it simulates print decay.
    /// </summary>
    public int Grade { get; set; } = 90;

    /// <summary>模擬單張標籤的 NOREAD 比率 (0.0–1.0) / Simulated per-label NOREAD rate.</summary>
    public double NoReadRate { get; init; }

    /// <summary>
    /// 測試用：強制回傳這些條碼 / Test hook: return exactly these codes.
    /// 元素為 null 代表「該格位有標籤但解不出來」,對應 <see cref="CodeResult.Data"/> 為 null;
    /// 這與「整批沒讀到」（空清單）是不同的不良,兩者都要測得到。
    /// A null element means "a slot was present but undecodable", i.e. a null
    /// <see cref="CodeResult.Data"/>. That is a different defect from "nothing read at
    /// all" (an empty list), and both need to be reachable from a test.
    /// </summary>
    public IReadOnlyList<string?>? ForcedCodes { get; set; }

    /// <summary>測試用：強制整批沒讀到 / Test hook: force a whole-trigger NOREAD (empty list).</summary>
    public bool ForceNoRead { get; set; }

    /// <summary>測試用：強制感測器回報自身異常 / Test hook: force a device-level fault.</summary>
    public bool SimulateFault { get; set; }

    /// <summary>測試用：強制延遲以觸發上層逾時 / Test hook: stall to trip the caller's timeout.</summary>
    public TimeSpan? ForcedLatency { get; set; }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(80), cancellationToken).ConfigureAwait(false);
        IsConnected = true;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<CodeResult>> TriggerAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException($"{Name} 尚未連線 / is not connected.");
        }

        var latency = ForcedLatency ?? RandomLatency();
        await Task.Delay(latency, cancellationToken).ConfigureAwait(false);

        if (SimulateFault)
        {
            throw new DeviceFaultException($"{Name} 回報內部錯誤 / reported an internal error.");
        }

        if (ForceNoRead)
        {
            return [];
        }

        if (ForcedCodes is not null)
        {
            return [.. ForcedCodes.Select((code, index) => ToResult(index, code))];
        }

        if (CodesPerTrigger < 0)
        {
            throw new InvalidOperationException(
                $"CodesPerTrigger 不可為負,目前為 {CodesPerTrigger} / must not be negative, got {CodesPerTrigger}.");
        }

        var results = new List<CodeResult>(CodesPerTrigger);
        for (var i = 0; i < CodesPerTrigger; i++)
        {
            // 沒讀到的標籤根本不會出現在結果區塊裡,而不是回一筆空的 ——
            // 上層的「筆數不足」檢查就是為了接住這種情況。
            // A label that did not decode simply never appears in the result block; it is
            // not reported as an empty slot. The caller's count check exists to catch that.
            if (_random.NextDouble() < NoReadRate)
            {
                continue;
            }

            results.Add(new CodeResult(
                Index: results.Count,
                Data: GenerateCode(),
                Grade: Grade,
                Judge: Verdict.Pass));
        }

        return results;
    }

    private CodeResult ToResult(int index, string? code) => code is null
        ? new CodeResult(index, Data: null, Grade: null, Judge: Verdict.Fail)
        : new CodeResult(index, code, Grade, Verdict.Pass);

    private string GenerateCode()
    {
        if (CodeLength <= 0)
        {
            throw new InvalidOperationException(
                $"CodeLength 必須為正整數,目前為 {CodeLength} / CodeLength must be positive, got {CodeLength}.");
        }

        var builder = new StringBuilder(CodeLength);
        for (var i = 0; i < CodeLength; i++)
        {
            builder.Append(Charset[_random.Next(Charset.Length)]);
        }

        return builder.ToString();
    }

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
