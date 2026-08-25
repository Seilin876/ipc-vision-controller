namespace IpcVisionController.Core.Hal;

/// <summary>
/// 這次執行實際接上的裝置 / The devices this run actually attached.
///
/// 為什麼要有這個型別 / Why this type exists:
/// 組裝根是唯一知道「接了實機還是模擬、字符檢測有沒有導入」的地方,而畫面必須把這件事
/// 顯示出來。用一個明確的集合傳遞,畫面就不必靠型別判斷去反推 ——
/// 「用 is MockCodeReader 猜現在是不是模擬」這種推論會在新增第三種裝置的那天悄悄猜錯。
/// The composition root is the only place that knows whether the run is live or mocked and
/// whether character verification is installed, and the UI has to show it. Passing an explicit
/// set means the UI never has to infer it: guessing the mode with `is MockCodeReader` is the kind
/// of inference that starts quietly guessing wrong the day a third device appears.
/// </summary>
/// <param name="Motor">進給軸 / The feed axis.</param>
/// <param name="CodeReader">讀碼器 / The code reader.</param>
/// <param name="Verifier">
/// 字符檢測器；未導入時為 <see cref="AbsentCharacterVerifier"/> /
/// The character verifier, or <see cref="AbsentCharacterVerifier"/> when not installed.
/// </param>
/// <param name="AreMocks">
/// 是否為模擬裝置 / Whether these are mocks.
/// 由組裝根明示,不由型別推論 —— 這個值會決定畫面上「非出貨依據」的警示,不能猜。
/// Stated by the composition root rather than inferred: it drives the "not a shipping record"
/// warning on screen, which is not something to guess at.
/// </param>
public sealed record DeviceSet(
    IMotorController Motor,
    ICodeReader CodeReader,
    ICharacterVerifier Verifier,
    bool AreMocks)
{
    /// <summary>
    /// 全部裝置,依相依順序 / Every device, in dependency order.
    /// 進給軸排最後處置：先關感測器再關軸,反過來會讓仍在跑的週期對已關閉的軸下命令。
    /// The axis is disposed last: closing the sensors first means a cycle still in flight cannot
    /// issue a command to an axis that is already gone.
    /// </summary>
    public IReadOnlyList<IDevice> All => [CodeReader, Verifier, Motor];

    /// <summary>
    /// 會吐出原始電文的裝置 / The devices that surface raw frames.
    /// 模擬裝置與未導入的字符檢測都沒有電文,所以模擬模式下這份清單是空的。
    /// Mocks and an uninstalled verifier have no frames, so this list is empty on mocks.
    /// </summary>
    public IReadOnlyList<IRawFrameSource> FrameSources =>
        [.. All.OfType<IRawFrameSource>()];
}
