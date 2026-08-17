using IpcVisionController.Core.Models;

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
/// 標籤紙進給軸 / The label-web feed axis (Delta R1-EC5500D1 EtherCAT stepper drive).
///
/// 為什麼是相對進給而非絕對定位 / Why relative feed rather than absolute positioning:
/// 標籤紙是連續料帶,沒有「第 25000 脈波」這種有意義的絕對位置 —— 只有「再捲一格」。
/// 用絕對座標會讓位置無上限累加,而且換料後座標完全失去意義。
/// A label web is continuous stock; there is no meaningful absolute "pulse 25000",
/// only "advance one more pitch". Absolute targets make the coordinate grow without
/// bound and render it meaningless after a roll change.
/// </summary>
public interface IMotorController : IDevice
{
    /// <summary>
    /// 自上次對標以來的累計進給量（脈波）/ Pulses fed since the last registration align.
    /// 用 long：連續生產下 int 會在約兩億脈波後溢位,以每格 1 萬脈波計不到一天。
    /// A long, because at 10,000 pulses per index an int overflows in well under a day
    /// of continuous running.
    /// </summary>
    long CurrentPosition { get; }

    /// <summary>是否已到位 / Whether the axis reports in-position.</summary>
    bool IsInPosition { get; }

    /// <summary>
    /// 伺服致能 / Enable the drive.
    /// 實機：走 CiA402 狀態機 Shutdown → Switch On → Enable Operation。
    /// Real hardware: walk the CiA402 state machine Shutdown → Switch On → Enable Operation.
    /// </summary>
    Task EnableAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 對齊定位標記並歸零累計量 / Align to the registration mark and zero the feed counter.
    /// 料帶沒有機械原點,所謂復歸是「捲到下一個定位標記」;此處歸零只是讓累計量重新起算。
    /// A web has no mechanical home; "homing" means winding to the next registration mark.
    /// Zeroing here merely restarts the accumulated count.
    /// </summary>
    Task HomeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 相對進給並等待到位 / Advance by a relative amount, returning only once in-position.
    /// </summary>
    /// <param name="stepPulses">進給量（脈波）；須為正 / Pulses to advance; must be positive.</param>
    /// <param name="speedPulsePerSecond">速度（脈波/秒）/ Profile velocity in pulses per second (0x6081).</param>
    Task FeedAsync(int stepPulses, int speedPulsePerSecond, CancellationToken cancellationToken);

    /// <summary>立即停止 / Quick stop (control word bit 2).</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 條碼讀取器 / Code reader (Keyence SR series over EtherNet/IP).
/// </summary>
public interface ICodeReader : IDevice
{
    /// <summary>
    /// 觸發一次讀取並取回該次的全部結果 / Trigger one read and return every result from it.
    /// </summary>
    /// <returns>
    /// 本次讀到的每一筆條碼。空清單代表完全沒讀到（NOREAD）——
    /// 那是工件問題而非設備故障,所以不是例外。
    /// Every code decoded. An empty list means nothing was read (a NOREAD), which is a
    /// part problem rather than an equipment fault and therefore not an exception.
    /// </returns>
    /// <exception cref="DeviceFaultException">感測器回報自身異常 / The sensor reported its own fault.</exception>
    Task<IReadOnlyList<CodeResult>> TriggerAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 標籤字符檢測器 / Label character verifier (Keyence IV4 AI OCR over EtherNet/IP).
/// </summary>
public interface ICharacterVerifier : IDevice
{
    /// <summary>
    /// 觸發一次檢測並取回全部區域的結果 / Trigger one inspection and return every region's result.
    /// </summary>
    /// <returns>
    /// 每個已啟用區域一筆。空清單代表沒有任何區域回報 —— 判退,不是故障。
    /// One entry per enabled region. An empty list means no region reported, which is a
    /// reject rather than a fault.
    /// </returns>
    /// <exception cref="DeviceFaultException">感測器回報自身異常 / The sensor reported its own fault.</exception>
    Task<IReadOnlyList<CharacterResult>> TriggerAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 裝置回報自身異常 / The device reported an internal fault.
///
/// 與「沒讀到」的分野 / The line against a no-read:
/// 沒讀到是工件問題（判退後繼續生產）,以空結果清單表示;
/// 本例外專指設備層級的異常（感測器離線、程式未載入、內部錯誤代碼）,必須停線。
/// A no-read is a part problem — reject and keep running — and is signalled by an empty
/// result list. This exception is strictly for device-level trouble (sensor offline, no
/// program loaded, internal error code) and must stop the line.
/// </summary>
public sealed class DeviceFaultException(string message) : Exception(message);
