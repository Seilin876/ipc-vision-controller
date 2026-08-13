using IpcVisionController.Core.Models;

namespace IpcVisionController.Core.Data;

/// <summary>
/// 檢測紀錄的寫入面 / The write surface for inspection records.
///
/// 只包含產線迴圈會用到的兩個方法。查詢 API（GetRecentAsync / GetTallyAsync）
/// 是 UI 專用,刻意不放進來 —— 讓「誰能寫紀錄」這件事的相依面越小越好。
/// Contains only the two methods the line loop needs. The query APIs
/// (GetRecentAsync / GetTallyAsync) are UI-only and deliberately excluded, keeping
/// the surface that can write traceability data as small as possible.
///
/// 存在的理由 / Why this exists:
/// 「寫入失敗必須停線」是安全需求,而要測它就得讓寫入可控地失敗。
/// 直接相依具體的 <see cref="DatabaseManager"/> 會讓這條路徑無法測試。
/// "A failed write must stop the line" is a safety requirement, and testing it means
/// making the write fail on demand. Depending on the concrete
/// <see cref="DatabaseManager"/> would leave that path untestable.
/// </summary>
public interface IInspectionStore
{
    /// <summary>建立資料表與索引（可重複呼叫）/ Create the table and index; safe to call repeatedly.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>寫入一筆檢測紀錄 / Insert one inspection record.</summary>
    Task<long> InsertAsync(InspectionRecord record, CancellationToken cancellationToken = default);
}
