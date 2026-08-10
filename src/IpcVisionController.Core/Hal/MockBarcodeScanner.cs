using System.Text;

namespace IpcVisionController.Core.Hal;

/// <summary>
/// 條碼讀取器的模擬實作 / Mock for the barcode scanner (Keyence SR series over TCP).
///
/// 換成實機時要替換的位置 / What to replace for real hardware:
/// - ConnectAsync：TcpClient.ConnectAsync(ip, 9004) —— SR 系列預設 Ethernet 埠。
///   ConnectAsync: TcpClient.ConnectAsync(ip, 9004), the SR series' default Ethernet port.
/// - ReadAsync：送出 "LON\r" 開啟讀取,讀回 CR 結尾的字串,收到 "ERROR" 或
///   逾時未回應即視為 NOREAD,最後務必送 "LOFF\r" 關閉讀取。
///   ReadAsync: send "LON\r" to start reading, read the CR-terminated reply; an
///   "ERROR" payload or a silent timeout is a NOREAD. Always send "LOFF\r" after.
///
/// 注意：NOREAD 是「工件問題」而非「設備故障」,因此以 <see cref="DeviceReadException"/>
/// 表達,由上層判定為 FAIL 並繼續生產;連線失敗才是設備故障。
/// NOTE: a NOREAD is a part problem, not an equipment fault. It surfaces as
/// <see cref="DeviceReadException"/> so the caller can reject the part and keep
/// running; only a link failure is an equipment fault.
/// </summary>
public sealed class MockBarcodeScanner : IBarcodeScanner
{
    /// <summary>模擬條碼的字元集（實務條碼多為大寫英數）/ Charset for generated codes (field codes are upper alphanumeric).</summary>
    private const string Charset = "ABCDEFGHJKLMNPQRSTUVWXYZ0123456789";

    private readonly Random _random;

    /// <param name="seed">固定種子讓測試可重現 / A fixed seed makes tests reproducible.</param>
    public MockBarcodeScanner(int? seed = null)
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
    /// 產生的條碼長度 / Length of generated codes.
    /// 預設與 <see cref="Models.RecipeModel.BarcodeLength"/> 的預設值一致,
    /// 調高或調低即可模擬「掛錯機種」的情境。
    /// Defaults to match <see cref="Models.RecipeModel.BarcodeLength"/>; changing it
    /// simulates running the wrong product model against the loaded recipe.
    /// </summary>
    public int CodeLength { get; set; } = 12;

    /// <summary>模擬 NOREAD 比率 (0.0–1.0) / Simulated NOREAD rate.</summary>
    public double NoReadRate { get; init; }

    /// <summary>測試用：強制回傳固定條碼 / Test hook: force a fixed code.</summary>
    public string? ForcedCode { get; set; }

    /// <summary>測試用：強制回傳 NOREAD / Test hook: force a NOREAD.</summary>
    public bool ForceNoRead { get; set; }

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

    public async Task<string> ReadAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException($"{Name} 尚未連線 / is not connected.");
        }

        var latency = ForcedLatency ?? RandomLatency();
        await Task.Delay(latency, cancellationToken).ConfigureAwait(false);

        if (ForceNoRead || (ForcedCode is null && _random.NextDouble() < NoReadRate))
        {
            throw new DeviceReadException($"{Name} 回報 NOREAD / reported NOREAD.");
        }

        return ForcedCode ?? GenerateCode();
    }

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
