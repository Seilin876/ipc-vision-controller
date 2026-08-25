namespace IpcVisionController.Core.Hal;

/// <summary>
/// 會吐出原始電文的裝置 / A device that can surface its raw frames.
///
/// 為什麼是獨立介面而不是掛在 <see cref="IDevice"/> 上 /
/// Why this is separate rather than folded into <see cref="IDevice"/>:
/// 模擬裝置沒有電文可言,進給軸也沒有。把它塞進 IDevice 會逼出一堆永不觸發的空事件,
/// 而畫面就無法用「有沒有實作這個介面」來判斷哪些裝置值得開一個電文視窗。
/// Mock devices have no frames, and neither does the feed axis. Folding this into IDevice
/// would force a crowd of events that never fire, and the UI would lose the ability to ask
/// "which devices are worth showing a frame log for" simply by testing for this interface.
/// </summary>
public interface IRawFrameSource
{
    /// <summary>裝置代號 / Device tag.</summary>
    string Name { get; }

    /// <summary>
    /// 目前生效的設定摘要 / A summary of the settings now in force.
    /// 與 <see cref="RawFrameReceived"/> 成對:電文告訴你裝置實際送了什麼,
    /// 這一行告訴你程式實際在讀哪幾格。少了任何一半都無法判斷索引對不對。
    /// The other half of <see cref="RawFrameReceived"/>: the frame says what the device actually
    /// sent, this line says which fields the program actually reads. Neither half alone can tell
    /// you whether the indexes are right.
    /// </summary>
    string Configuration { get; }

    /// <summary>
    /// 每收到一筆原始電文就引發 / Raised for every raw frame received.
    ///
    /// 實機導入時最有用的一件事:電文格式由裝置端的設定決定,只有看到真正的電文才能確認
    /// 欄位索引對不對。沒有它,設定錯誤的表徵是「每張標籤都判退」,而那與印刷不良、
    /// 與感測器沒對焦看起來一模一樣。
    /// The single most useful thing when commissioning: the layout is decided on the device and
    /// only a real frame confirms whether the configured indexes are right. Without it a
    /// misconfiguration presents as "every label rejects", indistinguishable from bad print and
    /// from a sensor out of focus.
    /// </summary>
    event EventHandler<RawFrameEventArgs>? RawFrameReceived;
}

/// <summary>原始電文事件 / Raw frame notification.</summary>
public sealed class RawFrameEventArgs(string deviceName, string frame) : EventArgs
{
    /// <summary>
    /// 發出電文的裝置 / The device the frame came from.
    /// 兩台裝置同時導入時,不標明來源的電文記錄毫無用處 ——
    /// 讀碼器與字符檢測器的電文長得很像,而欄位索引是各自獨立設定的。
    /// With two devices being commissioned at once, an unattributed frame log is useless: the
    /// reader's frames and the verifier's look alike, and their field indexes are configured
    /// independently.
    /// </summary>
    public string DeviceName { get; } = deviceName;

    /// <summary>已去除結束字元的電文 / The frame with its terminator stripped.</summary>
    public string Frame { get; } = frame;
}
