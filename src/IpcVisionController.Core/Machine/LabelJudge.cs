using System.Globalization;
using IpcVisionController.Core.Models;

namespace IpcVisionController.Core.Machine;

/// <summary>
/// 把感測器結果與配方比對成一個判定 / Turns sensor results plus a recipe into one verdict.
///
/// 為什麼獨立成一個類別 / Why this is its own class:
/// 判定規則是整台機器唯一「答錯就出貨不良品」的地方,值得能被單獨、窮舉地測試,
/// 而不必為了測一條規則就啟動狀態機、模擬裝置與資料庫。
/// The judging rules are the one place in the machine where being wrong ships a bad
/// part. They deserve to be tested directly and exhaustively, without standing up a
/// state machine, mock devices and a database to exercise a single rule.
///
/// 設計取捨：回傳「全部原因」而非「第一個原因」。
/// 印刷調機時需要一次看到所有不符項,逐條試錯會拖長停機時間。
/// 原因數量的上限就是條碼筆數加區域數（IV4 最多 10 區）,不會失控。
/// A deliberate trade-off: every reason is returned, not just the first. When dialling
/// in a print job the operator needs to see all the mismatches at once; discovering them
/// one reject at a time lengthens the stoppage. The count is bounded by the code count
/// plus the region count (the IV4 caps at 10), so it cannot run away.
/// </summary>
public static class LabelJudge
{
    /// <summary>多個判退原因的分隔字串 / Separator between reject reasons.</summary>
    private const string ReasonSeparator = "; ";

    /// <summary>
    /// 判定一張標籤 / Judge one label.
    /// </summary>
    /// <returns>
    /// 合格時為 null；不合格時為所有原因串接而成的字串 /
    /// Null when the label passes; otherwise every reason, joined.
    /// </returns>
    public static string? Evaluate(
        RecipeModel recipe,
        IReadOnlyList<CodeResult> codeResults,
        IReadOnlyList<CharacterResult> characterResults)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(codeResults);
        ArgumentNullException.ThrowIfNull(characterResults);

        var reasons = new List<string>();

        AppendCodeReasons(recipe, codeResults, reasons);
        AppendCharacterReasons(recipe, characterResults, reasons);

        return reasons.Count == 0 ? null : string.Join(ReasonSeparator, reasons);
    }

    private static void AppendCodeReasons(
        RecipeModel recipe,
        IReadOnlyList<CodeResult> results,
        List<string> reasons)
    {
        // 筆數不足代表有標籤漏印、漏貼,或根本沒進到視野內 ——
        // 只問「有沒有讀到東西」的檢查會整批放行這種不良。
        // Too few means a label was unprinted, unapplied, or never reached the field of
        // view. A bare "did we read anything?" check passes that defect straight through.
        if (results.Count < recipe.ExpectedCodeCount)
        {
            reasons.Add(Format(
                $"讀碼筆數不足：讀到 {results.Count},配方要求 {recipe.ExpectedCodeCount} / code count {results.Count} < expected {recipe.ExpectedCodeCount}"));
        }

        foreach (var result in results)
        {
            if (result.Judge != Verdict.Pass)
            {
                reasons.Add(Format($"條碼 #{result.Index} 感測器判退 / code #{result.Index} rejected by the sensor"));
            }

            if (result.Data is null)
            {
                reasons.Add(Format($"條碼 #{result.Index} 未解出內容 / code #{result.Index} decoded to nothing"));

                // 內容為 null 時,長度與等級都無從談起 / With no payload there is no length or grade to judge.
                continue;
            }

            if (recipe.BarcodeLength != RecipeModel.NoCheck && result.Data.Length != recipe.BarcodeLength)
            {
                reasons.Add(Format(
                    $"條碼 #{result.Index} 長度 {result.Data.Length} ≠ 配方 {recipe.BarcodeLength} / code #{result.Index} length {result.Data.Length} != expected {recipe.BarcodeLength}"));
            }

            AppendGradeReason(recipe, result, reasons);
        }
    }

    private static void AppendGradeReason(RecipeModel recipe, CodeResult result, List<string> reasons)
    {
        if (recipe.MinimumCodeGrade is not int minimum)
        {
            return;
        }

        // 配方要求檢查等級,感測器卻沒輸出 —— 這是感測器設定漏了,
        // 不是合格。當成合格會讓「已啟用品質管制」變成一句空話。
        // The recipe asks for a grade check and the sensor did not output one: that is a
        // missing sensor setting, not a pass. Treating it as a pass would make "grade
        // checking is enabled" a claim with nothing behind it.
        if (result.Grade is not int grade)
        {
            reasons.Add(Format(
                $"條碼 #{result.Index} 未輸出品質等級,無法檢查下限 {minimum} / code #{result.Index} reported no grade, cannot check the minimum of {minimum}"));
            return;
        }

        if (grade < minimum)
        {
            reasons.Add(Format(
                $"條碼 #{result.Index} 等級 {grade} < 下限 {minimum} / code #{result.Index} grade {grade} < minimum {minimum}"));
        }
    }

    private static void AppendCharacterReasons(
        RecipeModel recipe,
        IReadOnlyList<CharacterResult> results,
        List<string> reasons)
    {
        // 區域沒被觸發到就不會回報,而「沒回報」不等於「合格」
        // An untriggered region simply reports nothing, and nothing reported is not a pass.
        if (results.Count < recipe.ExpectedCharacterRegionCount)
        {
            reasons.Add(Format(
                $"字符區域數不足：回報 {results.Count},配方要求 {recipe.ExpectedCharacterRegionCount} / character regions {results.Count} < expected {recipe.ExpectedCharacterRegionCount}"));
        }

        foreach (var result in results)
        {
            if (result.Text is null)
            {
                reasons.Add(Format($"區域 #{result.Index} 未辨識出字符 / region #{result.Index} recognised nothing"));
            }

            if (result.Judge != Verdict.Pass)
            {
                reasons.Add(Format($"區域 #{result.Index} 字符比對不符 / region #{result.Index} did not match the master text"));
            }
        }
    }

    /// <summary>
    /// 以不變文化格式化 / Format with the invariant culture.
    /// 判退原因會寫進追溯資料庫,不能隨機台的地區設定改變寫法。
    /// Reject reasons are written to the traceability database and must not change
    /// wording with the machine's locale.
    /// </summary>
    private static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
