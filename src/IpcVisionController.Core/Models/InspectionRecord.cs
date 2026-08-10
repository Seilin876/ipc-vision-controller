namespace IpcVisionController.Core.Models;

/// <summary>
/// 一筆檢測紀錄 / One inspection record.
/// 注意：刻意不含 ID 欄位。ID 由 SQLite 的 AUTOINCREMENT 嚴格維護，
/// 但不外洩到應用層或 UI（需求：ID 在資料庫層嚴格維護、對 UI 隱藏）。
/// NOTE: deliberately carries no ID. The ID is maintained strictly by SQLite's
/// AUTOINCREMENT and never surfaces to the application layer or the UI.
/// </summary>
/// <param name="Timestamp">檢測時間 (UTC) / Inspection time in UTC.</param>
/// <param name="ModelName">機種名稱 / Product model name.</param>
/// <param name="BarcodeData">條碼內容；讀取失敗為 null / Barcode payload; null when the read failed.</param>
/// <param name="Iv4Result">Keyence IV4 判定 ("OK"/"NG")；失敗為 null / IV4 verdict; null on failure.</param>
/// <param name="FinalJudge">最終判定 / Final verdict, see <see cref="Verdict"/>.</param>
public sealed record InspectionRecord(
    DateTime Timestamp,
    string ModelName,
    string? BarcodeData,
    string? Iv4Result,
    string FinalJudge);

/// <summary>判定字串常數 / Verdict string constants.</summary>
public static class Verdict
{
    public const string Pass = "PASS";
    public const string Fail = "FAIL";
}

/// <summary>Keyence 視覺回傳值常數 / Keyence vision reply constants.</summary>
public static class VisionResult
{
    public const string Ok = "OK";
    public const string Ng = "NG";
}
