namespace IpcVisionController.Core.Hal;

/// <summary>
/// 標籤紙進給軸的模擬實作 / Mock for the label-web feed axis (Delta R1-EC5500D1).
///
/// 換成實機時要替換的位置 / What to replace for real hardware:
/// - ConnectAsync：初始化 EtherCAT 主站（例如 SOEM / Delta DIALink SDK）,
///   掃描從站、設定 PDO 對應、切至 OP 狀態。
///   ConnectAsync: bring up the EtherCAT master (SOEM / Delta DIALink SDK), scan the
///   slaves, map the PDOs, transition to OP.
/// - FeedAsync：CiA402 Profile Position 模式,控制字 0x6040 bit 6 設為「相對」,
///   寫入 0x607A(進給量) 與 0x6081(速度),下 New Set-point,
///   再輪詢狀態字 0x6041 bit 10 (Target Reached) 判定到位。
///   FeedAsync: CiA402 Profile Position with control word 0x6040 bit 6 set for a
///   relative target; write 0x607A (distance) and 0x6081 (velocity), raise the
///   new-set-point bit, then poll status word 0x6041 bit 10 (Target Reached).
/// - HomeAsync：實機是「進給到下一個定位標記」,由光電感測器的邊緣訊號觸發
///   Touch Probe (0x60B8) latch —— 不是 CiA402 原點復歸模式,
///   那個模式假設有機械原點,而料帶沒有。
///   HomeAsync: on real hardware this is "feed until the next registration mark",
///   latched from the mark sensor's edge via touch probe (0x60B8). It is *not* CiA402
///   homing mode, which presumes a mechanical home the web does not have.
///
/// 模擬方式：以固定週期累加進給量,讓 UI 看得到料帶在走,且每個 await 都吃取消權杖。
/// The mock accumulates feed on a fixed cycle so the UI sees the web moving, and every
/// await honours the cancellation token.
/// </summary>
public sealed class MockMotorController : IMotorController
{
    /// <summary>模擬的伺服週期 / Simulated servo cycle (a typical 2 ms EtherCAT cycle × 5).</summary>
    private static readonly TimeSpan CycleTime = TimeSpan.FromMilliseconds(10);

    private readonly SemaphoreSlim _motionGate = new(1, 1);

    /// <summary>
    /// 以 Interlocked 存取而非 Volatile / Accessed through Interlocked rather than Volatile.
    /// long 在 32 位元執行階段上的讀寫不是原子操作,Volatile.Read 保護不到撕裂。
    /// A long is not read or written atomically on a 32-bit runtime, and Volatile.Read
    /// does not guard against tearing there.
    /// </summary>
    private long _currentPosition;

    private bool _isInPosition = true;

    public string Name => "Delta R1-EC5500D1 (MOCK)";

    public bool IsConnected { get; private set; }

    public long CurrentPosition => Interlocked.Read(ref _currentPosition);

    public bool IsInPosition => Volatile.Read(ref _isInPosition);

    public bool IsEnabled { get; private set; }

    /// <summary>模擬連線耗時 / Simulated link-up latency.</summary>
    public TimeSpan ConnectLatency { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// 模擬「下一個定位標記還有多遠」/ Simulated distance to the next registration mark.
    /// 實機由標記感測器決定,此處固定值只是讓對標動作有事可做且會結束。
    /// On real hardware the mark sensor decides; a fixed value here merely gives the
    /// align a finite amount of work to do.
    /// </summary>
    public int PulsesToNextMark { get; init; } = 2_000;

    /// <summary>對標速度（脈波/秒）/ Registration-align velocity in pulses per second.</summary>
    public int AlignSpeedPulsePerSecond { get; init; } = 5_000;

    /// <summary>
    /// 模擬「料帶卡住,永遠到不了位」的故障,用於驗證逾時保護 /
    /// Simulate a jammed web that never reaches its target, to exercise the timeout guard.
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
        // 實機此處為 CiA402 狀態轉移,需等待狀態字回應
        // On real hardware this is the CiA402 transition, gated on the status word.
        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
        IsEnabled = true;
    }

    /// <summary>
    /// 捲到下一個定位標記並歸零累計量 / Wind to the next registration mark and zero the count.
    /// 歸零放在進給之後：進給被取消時累計量必須保留原值,
    /// 否則「對標到一半被停機」會讓帳面歸零而料帶其實已經走掉一段。
    /// The zeroing follows the feed deliberately: if the feed is cancelled the count must
    /// keep its old value, or a half-finished align would report zero while the web has
    /// in fact advanced.
    /// </summary>
    public async Task HomeAsync(CancellationToken cancellationToken)
    {
        EnsureEnabled();
        await FeedAsync(PulsesToNextMark, AlignSpeedPulsePerSecond, cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _currentPosition, 0);
    }

    public async Task FeedAsync(int stepPulses, int speedPulsePerSecond, CancellationToken cancellationToken)
    {
        EnsureEnabled();

        // 進給量為 0 或負數在料帶上沒有意義：倒轉會把已檢測過的標籤再送回鏡頭前
        // A zero or negative feed is meaningless on a web; reversing would push already
        // inspected labels back under the sensors.
        if (stepPulses <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stepPulses), stepPulses,
                "進給量必須為正 / Feed distance must be positive.");
        }

        if (speedPulsePerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(speedPulsePerSecond), speedPulsePerSecond,
                "速度必須為正 / Velocity must be positive.");
        }

        // 單軸不可同時接受兩個進給命令 / A single axis must not accept two feeds at once.
        await _motionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _isInPosition, false);

            var pulsesPerCycle = Math.Max(1, (int)(speedPulsePerSecond * CycleTime.TotalSeconds));
            var remaining = stepPulses;

            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!SimulateStall)
                {
                    var step = Math.Min(remaining, pulsesPerCycle);
                    Interlocked.Add(ref _currentPosition, step);
                    remaining -= step;
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
