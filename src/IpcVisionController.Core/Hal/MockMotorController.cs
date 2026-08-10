namespace IpcVisionController.Core.Hal;

/// <summary>
/// 台達 R1-EC5500D1 的模擬實作 / Mock for the Delta R1-EC5500D1 stepper drive.
///
/// 換成實機時要替換的位置 / What to replace for real hardware:
/// - ConnectAsync：初始化 EtherCAT 主站（例如 SOEM / Delta DIALink SDK），
///   掃描從站、設定 PDO 對應、切至 OP 狀態。
///   ConnectAsync: bring up the EtherCAT master (SOEM / Delta DIALink SDK),
///   scan slaves, map the PDOs, transition to OP.
/// - MoveToAsync：寫入 0x607A(目標位置)、0x6081(速度)，控制字 0x6040 下 New Set-point，
///   再輪詢狀態字 0x6041 bit 10 (Target Reached) 判定到位。
///   MoveToAsync: write 0x607A (target) and 0x6081 (velocity), raise the new-set-point
///   bit in control word 0x6040, then poll status word 0x6041 bit 10 (Target Reached).
///
/// 模擬方式：以固定週期遞增位置，讓 UI 能看到位置變化，且每個 await 都吃取消權杖。
/// The mock steps the position on a fixed cycle so the UI sees motion, and every
/// await honours the cancellation token.
/// </summary>
public sealed class MockMotorController : IMotorController
{
    /// <summary>模擬的伺服週期 / Simulated servo cycle (matches a typical 2 ms EtherCAT cycle × 5).</summary>
    private static readonly TimeSpan CycleTime = TimeSpan.FromMilliseconds(10);

    private readonly SemaphoreSlim _motionGate = new(1, 1);
    private int _currentPosition;
    private bool _isInPosition = true;

    public string Name => "Delta R1-EC5500D1 (MOCK)";

    public bool IsConnected { get; private set; }

    public int CurrentPosition => Volatile.Read(ref _currentPosition);

    public bool IsInPosition => Volatile.Read(ref _isInPosition);

    public bool IsEnabled { get; private set; }

    /// <summary>模擬連線耗時 / Simulated link-up latency.</summary>
    public TimeSpan ConnectLatency { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// 模擬「永不到位」的故障，用於驗證逾時保護 / Simulate a never-in-position fault,
    /// to exercise the timeout guard.
    /// </summary>
    public bool SimulateStall { get; set; }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(ConnectLatency, cancellationToken).ConfigureAwait(false);
        IsConnected = true;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        IsConnected = false;
        IsEnabled = false;
        return Task.CompletedTask;
    }

    public async Task EnableAsync(CancellationToken cancellationToken)
    {
        EnsureConnected();
        // 實機此處為 CiA402 狀態轉移，需等待狀態字回應
        // On real hardware this is the CiA402 transition, gated on the status word.
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        IsEnabled = true;
    }

    public async Task HomeAsync(CancellationToken cancellationToken)
    {
        EnsureEnabled();
        await MoveToAsync(0, speedPulsePerSecond: 5_000, cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveToAsync(int targetPulse, int speedPulsePerSecond, CancellationToken cancellationToken)
    {
        EnsureEnabled();

        if (speedPulsePerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(speedPulsePerSecond), speedPulsePerSecond,
                "速度必須為正 / Velocity must be positive.");
        }

        // 單軸不可同時接受兩個定位命令 / A single axis must not accept two moves at once.
        await _motionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _isInPosition, false);

            var pulsesPerCycle = Math.Max(1, (int)(speedPulsePerSecond * CycleTime.TotalSeconds));

            while (CurrentPosition != targetPulse)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!SimulateStall)
                {
                    var remaining = targetPulse - CurrentPosition;
                    var step = Math.Sign(remaining) * Math.Min(Math.Abs(remaining), pulsesPerCycle);
                    Volatile.Write(ref _currentPosition, CurrentPosition + step);
                }

                // 模擬輪詢狀態字的週期 / Stands in for polling the status word each cycle.
                await Task.Delay(CycleTime, cancellationToken).ConfigureAwait(false);
            }

            Volatile.Write(ref _isInPosition, true);
        }
        finally
        {
            _motionGate.Release();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _isInPosition, true);
        return Task.CompletedTask;
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException($"{Name} 尚未連線 / is not connected.");
        }
    }

    private void EnsureEnabled()
    {
        EnsureConnected();
        if (!IsEnabled)
        {
            throw new InvalidOperationException($"{Name} 伺服未致能 / servo is not enabled.");
        }
    }

    public ValueTask DisposeAsync()
    {
        _motionGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
