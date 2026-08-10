namespace IpcVisionController.Core.Hal;

/// <summary>
/// 所有硬體裝置的共同介面 / Common surface for every hardware device.
/// 每個方法都收 <see cref="CancellationToken"/>：現場設備會沒有回應，
/// 沒有取消權杖的等待就是一個死鎖。
/// Every method takes a <see cref="CancellationToken"/>: field devices do go
/// unresponsive, and an un-cancellable wait on one is a deadlock.
/// </summary>
public interface IDevice : IAsyncDisposable
{
    /// <summary>裝置代號，用於記錄 / Device tag, used in log lines.</summary>
    string Name { get; }

    /// <summary>是否已連線 / Whether the link is up.</summary>
    bool IsConnected { get; }

    /// <summary>建立連線 / Open the link.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>中斷連線 / Close the link.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 台達 R1-EC5500D1 EtherCAT 步進驅動模組 / Delta R1-EC5500D1 EtherCAT stepper drive.
/// </summary>
public interface IMotorController : IDevice
{
    /// <summary>目前位置（脈波）/ Current position in pulses.</summary>
    int CurrentPosition { get; }

    /// <summary>是否已到位 / Whether the axis reports in-position.</summary>
    bool IsInPosition { get; }

    /// <summary>
    /// 伺服致能 / Enable the drive.
    /// 實機：走 CiA402 狀態機 Shutdown → Switch On → Enable Operation。
    /// Real hardware: walk the CiA402 state machine Shutdown → Switch On → Enable Operation.
    /// </summary>
    Task EnableAsync(CancellationToken cancellationToken);

    /// <summary>原點復歸 / Home the axis (CiA402 homing mode, 0x6098).</summary>
    Task HomeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 絕對定位並等待到位 / Absolute move, returning only once in-position.
    /// </summary>
    /// <param name="targetPulse">目標位置（脈波）/ Target position in pulses (0x607A).</param>
    /// <param name="speedPulsePerSecond">速度（脈波/秒）/ Profile velocity in pulses per second (0x6081).</param>
    Task MoveToAsync(int targetPulse, int speedPulsePerSecond, CancellationToken cancellationToken);

    /// <summary>立即停止 / Quick stop (control word bit 2).</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Keyence IV4 影像判別感測器 / Keyence IV4 vision sensor.
/// </summary>
public interface IVisionSensor : IDevice
{
    /// <summary>
    /// 觸發拍照並取回判定 / Trigger a capture and return the verdict.
    /// </summary>
    /// <returns>"OK" 或 "NG" / Either "OK" or "NG".</returns>
    Task<string> TriggerAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 條碼讀取器 / Barcode scanner (e.g. Keyence SR series over TCP).
/// </summary>
public interface IBarcodeScanner : IDevice
{
    /// <summary>
    /// 觸發讀取並取回條碼字串 / Trigger a read and return the decoded string.
    /// </summary>
    /// <exception cref="DeviceReadException">讀取失敗（NOREAD）/ Read failed (NOREAD).</exception>
    Task<string> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>裝置回報讀取失敗 / The device reported a failed read.</summary>
public sealed class DeviceReadException(string message) : Exception(message);
