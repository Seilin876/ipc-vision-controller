using System.Globalization;
using IpcVisionController.Core.Models;

namespace IpcVisionController.Core.Hal;

/// <summary>
/// 把讀碼器電文的欄位對應成條碼結果 / Maps a reader frame's fields onto code results.
///
/// 刻意與驅動分開成純函式 / Deliberately a pure function, kept out of the driver:
/// 欄位對應是整條實機路徑上最不確定的一環 —— 它由裝置端的設定決定,只能靠實機電文確認,
/// 因此也是最需要反覆修改的部分。留在驅動裡的話,每測一種欄位排列都要先站起一個 TCP 連線;
/// 拆出來就能用一堆字串窮舉,不必接上任何裝置。
/// The field mapping is the least certain link in the whole real-hardware path — decided on the
/// device, confirmable only from a real frame — and therefore the part most often edited. Left
/// inside the driver, testing one field arrangement would mean standing up a TCP link; pulled
/// out, the arrangements can be exhausted with plain strings and no device at all.
/// </summary>
public static class CodeFrameReader
{
    /// <summary>
    /// 讀出這次觸發的每一筆條碼 / Read every code this trigger produced.
    /// </summary>
    public static IReadOnlyList<CodeResult> Read(string[] fields, SrX300Options options)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(options);

        var codes = new List<CodeResult>(options.CodeDataFields.Count);

        for (var slot = 0; slot < options.CodeDataFields.Count; slot++)
        {
            var data = NonProtocolFrame.FieldOrNull(fields, options.CodeDataFields[slot], options);

            // 解不出來的條碼根本不進結果清單,而不是回一筆空的 —— 與讀碼器本身的語意一致,
            // 也讓配方的「筆數不足」檢查成為漏貼標籤唯一會被抓到的地方。
            // A code that did not decode never enters the list rather than appearing as an empty
            // entry. That matches the reader's own semantics and keeps the recipe's count check as
            // the one place a missing label gets caught.
            if (data is null)
            {
                continue;
            }

            // Index 用結果清單目前的長度,而非設定裡的欄位序 —— 追溯紀錄要的是
            // 「這次讀到的第幾筆」,不是「它躺在電文的第幾格」。
            // Index is the position in the result list, not the configured slot: a traceability
            // record wants "the nth code read", not "the nth field in the frame".
            codes.Add(new CodeResult(
                Index: codes.Count,
                Data: data,
                Grade: ReadGrade(fields, options, slot),
                Judge: Verdict.Pass));
        }

        return codes;
    }

    private static int? ReadGrade(string[] fields, SrX300Options options, int slot)
    {
        if (slot >= options.CodeGradeFields.Count)
        {
            return null;
        }

        var text = NonProtocolFrame.FieldOrNull(fields, options.CodeGradeFields[slot], options);
        if (text is null)
        {
            return null;
        }

        // 解不出數字時回 null 而不是 0：0 在任何刻度上都是「最差」,會讓等級檢查
        // 把一張其實沒有等級資料的標籤判退。
        // An unparsable grade becomes null, not 0. Zero is the worst value on every scale and would
        // reject a label that simply carried no grade data.
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var grade)
            ? grade
            : null;
    }
}
