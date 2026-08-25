using IpcVisionController.Core.Models;

namespace IpcVisionController.Core.Hal;

/// <summary>
/// 實機 Keyence SR-X300 讀碼器 / Real Keyence SR-X300 code reader, over non-protocol TCP.
///
/// 只實作 <see cref="ICodeReader"/> / Implements <see cref="ICodeReader"/> only:
/// 它是讀碼器,不做字符檢測。先前的版本讓一個類別同時扮演兩個角色,那是把「同一顆感測器
/// 兼兩職」的假設寫進了型別;現場其實是兩台獨立裝置,那個假設一旦寫進型別,
/// 第二台裝置就沒有地方可以接上來。
/// It reads codes and does not verify characters. An earlier version had one class play both
/// roles, which wrote the assumption "one sensor does both jobs" into the type system. The line
/// actually has two separate devices, and once that assumption is in a type there is nowhere for
/// the second device to attach.
///
/// 本類別只剩「連線層 + 欄位對應」的接合 —— 兩邊都各自可測:
/// 連線與組框在 <see cref="NonProtocolSensor"/>,欄位對應在 <see cref="CodeFrameReader"/>。
/// What remains here is only the joint between the link layer and the field mapping, each of which
/// is testable on its own: the link and framing in <see cref="NonProtocolSensor"/>, the mapping in
/// <see cref="CodeFrameReader"/>.
/// </summary>
public sealed class SrX300CodeReader(SrX300Options options) : NonProtocolSensor(options), ICodeReader
{
    private readonly SrX300Options _options = options;

    /// <summary>
    /// 觸發一次讀取 / Trigger one read.
    /// </summary>
    public async Task<IReadOnlyList<CodeResult>> TriggerAsync(CancellationToken cancellationToken)
    {
        var fields = await TriggerAndSplitAsync(cancellationToken).ConfigureAwait(false);
        return CodeFrameReader.Read(fields, _options);
    }
}
