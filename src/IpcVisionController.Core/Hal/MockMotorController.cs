namespace IpcVisionController.Core.Hal;

/// <summary>
/// 標籤紙進給軸的模擬實作 / Mock for the label-web feed axis.
///
/// 實機拓樸 / The real topology:
///   工控機 → PCI-L221B1D0（EtherCAT 主站卡）→ R1-EC5500D1（EtherCAT 耦合器）
///          → R1-EC5621D1（脈波輸出模組）→ Misumi DR57A（步進驅動器）→ C-57STM04（步進馬達）
///   IPC → PCI-L221B1D0 master card → R1-EC5500D1 coupler → R1-EC5621D1 pulse-output module
///        → Misumi DR57A stepper driver → C-57STM04 stepper motor.
///
/// 這不是伺服軸,是開環脈波輸出 / This is not a servo axis but open-loop pulse output:
/// 模組只知道自己送出了幾個脈波,不知道馬達實際走了多少 —— 沒有編碼器回授。
/// 因此「位置」的意思是「已命令的脈波數」,而不是「量到的位置」。
/// 失步、料帶滑動、印刷間距與設定值的差異,都不會被任何回授修正。
/// The module knows how many pulses it emitted, not how far the motor turned: there is no encoder
/// feedback. "Position" therefore means pulses commanded rather than distance measured, and lost steps, web
/// slip and any mismatch between the printed pitch and the configured pulse count go uncorrected.
///
/// 換成實機時的對應 / What the real implementation calls (Delta EtherCAT SDK, CS_ECAT_* 前綴):
/// - ConnectAsync：CS_ECAT_Master_Open → Master_Get_CardSeq → Master_Initial,
///   再輪詢 Master_Check_Initial_Done,最後 Master_Get_SlaveNum 確認從站在線。
///   軸的定位方式是 (CardNo, NodeID, SlotNo) —— NodeID 是耦合器,SlotNo 是脈波模組的插槽。
///   ConnectAsync: Master_Open, Get_CardSeq, Master_Initial, poll Check_Initial_Done, then
///   Get_SlaveNum. An axis is addressed by card, node and slot: the node is the coupler and the slot is
///   the pulse module.
/// - EnableAsync：CS_ECAT_Slave_Motion_Set_Svon(On_Off = 1),必要時先 Slave_Motion_Ralm 清警報。
///   步進驅動器沒有 CiA402 狀態機,這一步實際上是讓模組把致能訊號輸出給 DR57A。
///   EnableAsync: Set_Svon, preceded by Ralm to clear an alarm if needed. A stepper driver has no CiA402
///   state machine; this drives the module's enable output to the DR57A.
/// - FeedAsync：CS_ECAT_Slave_PP_Start_Move(..., TargetPos, ConstVel, Acceleration, Deceleration,
///   Abs_Rel = 相對),再輪詢 Slave_Motion_Get_Mdone 判定到位。
///   FeedAsync: PP_Start_Move with the relative flag, then poll Get_Mdone for completion.
/// - HomeAsync：本機構「沒有」定位標記感測器,所以不做對齊,只以
///   CS_ECAT_Slave_Motion_Set_Position(0) 把脈波計數歸零。
///   HomeAsync: this mechanism has no registration-mark sensor, so nothing is aligned; it only zeroes the
///   pulse counter with Set_Position(0).
/// - StopAsync：CS_ECAT_Slave_Motion_Sd_Stop（減速停止),不是 Emg_Stop。
///   開環步進被立即停止會失步,而失步之後脈波計數與料帶實際位置就不再一致。
///   StopAsync: Sd_Stop, decelerating, rather than Emg_Stop. An immediate stop makes an open-loop stepper
///   lose steps, and once steps are lost the pulse count no longer matches where the web actually is.
/// - CurrentPosition：CS_ECAT_Slave_Motion_Get_Position。
///   注意 SDK 的位置是 int（32 位元),而本介面宣告 long —— 沒有定位標記可週期性歸零時,
///   計數只會單向累加,約 21 億脈波後溢位。實機實作必須在軟體端以 long 累計並處理模組計數繞回。
///   CurrentPosition: Get_Position. Note the SDK's position is a 32-bit int while this interface exposes a
///   long: with no mark sensor to re-zero against, the count only ever grows and wraps after about 2.1
///   billion pulses, so a real implementation has to accumulate in software and handle the wrap.
///
/// 介面沒有表達的東西 / What the interface does not carry:
/// PP_Start_Move 需要加減速度,而 FeedAsync 只收「脈波數」與「速度」。
/// 加減速對開環步進是關鍵參數 —— 加速太急就失步 —— 但它屬於「這台機構長怎樣」,
/// 不屬於每次呼叫。實機實作應由自己的設定（device.json 的 FeedAxis 段）提供,
/// 而不是加進介面。
/// PP_Start_Move needs acceleration and deceleration, which FeedAsync does not take. They matter greatly
/// for an open-loop stepper — too aggressive and it loses steps — but they describe the mechanism rather
/// than any single call, so a real implementation should take them from its own settings section rather
/// than widening the interface.
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

    public string Name => "進給軸 / feed axis (MOCK)";

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
