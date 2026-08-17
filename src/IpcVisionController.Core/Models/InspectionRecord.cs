namespace IpcVisionController.Core.Models;

/// <summary>
/// 一次讀碼觸發中的單一條碼結果 / One decoded code from a single code-reader trigger.
///
/// 為什麼是「一觸發多筆」/ Why one trigger yields many:
/// 讀碼器視野內可能同時有多張標籤,或一張標籤上有多個碼。實機（Keyence SR 系列）
/// 會在同一個循環通訊區塊裡回傳全部結果,因此本層必須原生支援多筆,
/// 而不是只留最後一筆 —— 只留一筆等於默默丟掉不良品的證據。
/// The reader's field of view may hold several labels, or one label may carry several
/// codes. Real hardware (Keyence SR series) returns them all in the same cyclic block,
/// so this layer models many natively. Keeping only the last one would silently discard
/// the evidence for a reject.
/// </summary>
/// <param name="Index">同一次觸發內的序號,由 0 起 / Zero-based ordinal within the trigger.</param>
/// <param name="Data">解碼內容；該筆為 NOREAD 時為 null / Decoded payload; null when this slot was a NOREAD.</param>
/// <param name="Grade">
/// 感測器回報的品質等級；未輸出時為 null / Sensor-reported quality level; null when not output.
/// 注意單位由感測器決定（SR 系列的「讀取餘裕度」為 0–100,條碼驗證機的 ISO 等級為 0–4）,
/// 判定門檻由 <see cref="RecipeModel.MinimumCodeGrade"/> 給定,本層不假設刻度。
/// The unit is the sensor's own (SR-series matching level is 0–100; an ISO verifier grade
/// is 0–4). The threshold comes from <see cref="RecipeModel.MinimumCodeGrade"/>; this
/// layer assumes no particular scale.
/// </param>
/// <param name="Judge">感測器自身的判定 / The sensor's own verdict, see <see cref="Verdict"/>.</param>
public sealed record CodeResult(
    int Index,
    string? Data,
    int? Grade,
    string Judge);

/// <summary>
/// 一次字符檢測觸發中的單一區域結果 / One region's result from a character-verification trigger.
///
/// 實機（Keyence IV4）最多可設 10 個 OCR 區域,每個區域各自比對主文字並輸出 PASS/FAIL,
/// 因此同樣是「一觸發多筆」。
/// Real hardware (Keyence IV4) supports up to 10 OCR regions, each compared against its
/// own master text and each emitting its own PASS/FAIL — again many results per trigger.
/// </summary>
/// <param name="Index">感測器上的區域編號,由 0 起 / Zero-based region number on the sensor.</param>
/// <param name="Text">辨識到的字串；辨識失敗為 null / Recognised string; null when recognition failed.</param>
/// <param name="Judge">感測器自身的判定 / The sensor's own verdict, see <see cref="Verdict"/>.</param>
public sealed record CharacterResult(
    int Index,
    string? Text,
    string Judge);

/// <summary>
/// 一次檢測（= 一次進給後的一次觸發）的完整紀錄 / One inspection: a single trigger after one feed.
///
/// 注意：刻意不含 ID 欄位。ID 由 SQLite 的 AUTOINCREMENT 嚴格維護,
/// 但不外洩到應用層或 UI。
/// NOTE: deliberately carries no ID. The ID is maintained strictly by SQLite's
/// AUTOINCREMENT and never surfaces to the application layer or the UI.
/// </summary>
/// <param name="Timestamp">檢測時間 (UTC) / Inspection time in UTC.</param>
/// <param name="ModelName">機種名稱 / Product model name.</param>
/// <param name="FinalJudge">最終判定 / Final verdict, see <see cref="Verdict"/>.</param>
/// <param name="CodeResults">本次讀碼的全部結果 / Every code result from this trigger.</param>
/// <param name="CharacterResults">本次字符檢測的全部結果 / Every character result from this trigger.</param>
/// <param name="RejectReason">
/// 判退原因；PASS 時為 null / Why it was rejected; null on a PASS.
/// 不良品的追溯價值有一半在「為什麼退」,沒有這欄現場只能回頭猜。
/// Half the traceability value of a reject is *why*; without this field the line is
/// left guessing after the fact.
/// </param>
public sealed record InspectionRecord(
    DateTime Timestamp,
    string ModelName,
    string FinalJudge,
    IReadOnlyList<CodeResult> CodeResults,
    IReadOnlyList<CharacterResult> CharacterResults,
    string? RejectReason);

/// <summary>判定字串常數 / Verdict string constants.</summary>
public static class Verdict
{
    public const string Pass = "PASS";
    public const string Fail = "FAIL";
}
