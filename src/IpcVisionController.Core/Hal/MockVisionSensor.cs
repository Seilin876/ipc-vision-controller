using IpcVisionController.Core.Models;

namespace IpcVisionController.Core.Hal;

/// <summary>
/// Keyence IV4 的模擬實作 / Mock for the Keyence IV4 vision sensor.
///
/// 換成實機時要替換的位置 / What to replace for real hardware:
/// - ConnectAsync：TcpClient.ConnectAsync(ip, 8500) —— IV4 預設 Ethernet 埠。
///   ConnectAsync: TcpClient.ConnectAsync(ip, 8500), the IV4's default Ethernet port.
/// - TriggerAsync：送出觸發指令（例如 "T1\r"），讀回以 CR 結尾的回應並解析 OK / NG。
///   TriggerAsync: send the trigger command (e.g. "T1\r"), read the CR-terminated
///   reply and parse OK / NG.
///
/// 注意：實機讀取務必用 NetworkStream.ReadAsync(..., cancellationToken)，
/// 不要用 ReceiveTimeout —— 同步逾時會擋住執行緒集區。
/// NOTE: on real hardware always read with NetworkStream.ReadAsync(..., token);
/// do not rely on ReceiveTimeout, which blocks a thread-pool thread.
/// </summary>
public sealed class MockVisionSensor : IVisionSensor
{
    private readonly Random _random;

    /// <param name="seed">固定種子讓測試可重現 / A fixed seed makes tests reproducible.</param>
    public MockVisionSensor(int? seed = null)
    {
        _random = seed.HasValue ? new Random(seed.Value) : new Random();
    }

    public string Name => "Keyence IV4 (MOCK)";

    public bool IsConnected { get; private set; }

    /// <summary>模擬曝光與判別耗時的下限 / Lower bound of simulated capture + judge time.</summary>
    public TimeSpan MinLatency { get; init; } = TimeSpan.FromMilliseconds(120);

    /// <summary>模擬耗時的上限 / Upper bound of simulated capture + judge time.</summary>
    public TimeSpan MaxLatency { get; init; } = TimeSpan.FromMilliseconds(320);

    /// <summary>模擬 NG 比率 (0.0–1.0) / Simulated NG rate.</summary>
    public double NgRate { get; init; } = 0.10;

    /// <summary>
    /// 測試用：強制回傳固定結果 / Test hook: force a fixed verdict.
    /// </summary>
    public string? ForcedResult { get; set; }

    /// <summary>
    /// 測試用：強制超過此延遲，以觸發上層逾時 / Test hook: stall for this long to trip the caller's timeout.
    /// </summary>
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

    public async Task<string> TriggerAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException($"{Name} 尚未連線 / is not connected.");
        }

        var latency = ForcedLatency ?? RandomLatency();
        await Task.Delay(latency, cancellationToken).ConfigureAwait(false);

        if (ForcedResult is not null)
        {
            return ForcedResult;
        }

        return _random.NextDouble() < NgRate ? VisionResult.Ng : VisionResult.Ok;
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
