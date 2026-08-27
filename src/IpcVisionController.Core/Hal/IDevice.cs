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
/// 標籤紙進給軸 / The label-web feed axis.
///
/// 實機是開環脈波輸出,不是伺服軸 / Open-loop pulse output rather than a servo axis:
///   工控機 → PCI-L221B1D0 主站卡 → R1-EC5500D1 耦合器 → R1-EC5621D1 脈波輸出模組
///          → Misumi DR57A 步進驅動器 → C-57STM04 步進馬達
/// 沒有編碼器回授,所以本介面的「位置」一律指「已命令的脈波數」,不是量到的距離。
/// 失步與料帶滑動不會被任何回授修正 —— 誤差只會累加,見 README 的「位置偏移」一節。
/// With no encoder feedback, every position on this interface means pulses commanded rather than distance
/// measured. Lost steps and web slip go uncorrected and the error only accumulates; see the README.
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
    /// 致能驅動器 / Enable the drive.
    /// 實機是讓脈波模組把致能訊號輸出給步進驅動器（SDK 的 Set_Svon)——
    /// 步進驅動器沒有 CiA402 狀態機,所以這裡沒有狀態轉移要等。
    /// On real hardware this drives the pulse module's enable output to the stepper driver via the SDK's
    /// Set_Svon. A stepper driver has no CiA402 state machine, so there is no transition to wait on.
    /// </summary>
    Task EnableAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 歸零累計量 / Zero the feed counter.
    ///
    /// 料帶沒有機械原點,所以這裡不是「回到某個位置」,而只是讓累計量重新起算。
    /// 本機構「沒有」定位標記感測器,因此實機實作只會把脈波計數歸零,不會對齊任何東西 ——
    /// 訊息與文件都不該聲稱對齊發生了。
    /// A web has no mechanical home, so this is not a move to a position but a restart of the count. This
    /// mechanism has no registration-mark sensor, so a real implementation only zeroes the pulse counter and
    /// aligns nothing; neither the log nor the documentation should claim otherwise.
    ///
    /// 日後若加上標記感測器,這個方法才會變成「進給到下一個標記為止」,
    /// 屆時每一格都能歸零累積誤差。目前沒有,所以進給是純開環。
    /// Should a mark sensor be added later, this becomes "feed until the next mark" and each pitch can clear
    /// the accumulated error. Without one the feed is purely open-loop.
    /// </summary>
    Task HomeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 相對進給並等待到位 / Advance by a relative amount, returning only once in-position.
    /// </summary>
    /// <param name="stepPulses">進給量（脈波）；須為正 / Pulses to advance; must be positive.</param>
    /// <param name="speedPulsePerSecond">速度（脈波/秒）/ Profile velocity in pulses per second (0x6081).</param>
    Task FeedAsync(int stepPulses, int speedPulsePerSecond, CancellationToken cancellationToken);

    /// <summary>
    /// 停止進給 / Stop feeding.
    ///
    /// 實機用減速停止,不用立即停止 / Decelerating rather than immediate on real hardware:
    /// 開環步進被立即切斷脈波會失步,而失步之後脈波計數與料帶的實際位置就不再一致 ——
    /// 沒有回授可以發現這件事,也沒有標記感測器可以修正它。
    /// 減速停止慢一點,但停完之後計數仍然可信。
    /// Cutting the pulse train dead makes an open-loop stepper lose steps, and once steps are lost the count
    /// no longer matches where the web is — with no feedback to notice and no mark sensor to correct it. A
    /// decelerating stop is slower and leaves the count trustworthy.
    /// </summary>
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
