using System.Globalization;
using IpcVisionController.Core.Models;

namespace IpcVisionController.Core.Hal;

/// <summary>
/// 把 SR-X300 的電文解成條碼結果 / Turns an SR-X300 frame into code results.
///
/// 電文是兩層 / The frame has two levels:
///   2132030090A0S6X10683:-0.0,2132030090A0S6X10685:3.0
///   └───── 一筆記錄 ─────┘   └───── 一筆記錄 ─────┘
///        碼    :   等級      內部分隔符 / record delimiter
///
/// 筆數由電文決定,不由設定決定 / The count comes from the frame, not from configuration:
/// 一次觸發讀到幾筆取決於視野裡有幾張標籤。這也是配方 ExpectedCodeCount 唯一能真正發揮
/// 作用的前提 —— 若筆數是設定死的,「少讀到一筆」就永遠不會發生,漏貼標籤也就永遠抓不到。
/// How many codes come back depends on how many labels are in view. It is also the precondition for the
/// recipe's ExpectedCodeCount to mean anything: with the count fixed by configuration, "one fewer than
/// expected" could never happen and a missing label could never be caught.
///
/// 刻意與驅動分開成純函式 / Deliberately a pure function, kept out of the driver:
/// 欄位對應是最常被修改的部分,留在驅動裡的話每測一種排列都要先站起一個 TCP 連線。
/// The mapping is the most frequently edited part; inside the driver, testing one arrangement would mean
/// standing up a TCP link.
/// </summary>
public static class CodeFrameReader
{
    /// <summary>
    /// 讀出這次觸發的每一筆條碼 / Read every code this trigger produced.
    /// </summary>
    /// <param name="frame">已去除結束字元的電文本文 / The frame body, terminator already stripped.</param>
    /// <param name="options">讀碼器設定 / The reader's settings.</param>
    public static IReadOnlyList<CodeResult> Read(string frame, SrX300Options options)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);

        var records = frame.Split(options.RecordDelimiter);
        var codes = new List<CodeResult>(records.Length);

        foreach (var record in records)
        {
            var fields = record.Split(options.FieldDelimiter);
            var data = NonProtocolFrame.FieldOrNull(fields, options.CodeField, options);

            // 解不出來的條碼根本不進結果清單,而不是回一筆空的 —— 與讀碼器本身的語意一致,
            // 也讓配方的「筆數不足」檢查成為漏貼標籤唯一會被抓到的地方。
            // A code that did not decode never enters the list rather than appearing as an empty entry.
            // That matches the reader's own semantics and keeps the recipe's count check as the one place
            // a missing label gets caught.
            if (data is null)
            {
                continue;
            }

            // Index 用結果清單目前的長度,而非它在電文裡的第幾筆 —— 追溯紀錄要的是
            // 「這次讀到的第幾筆」,不是「它躺在電文的第幾段」。
            // Index is the position in the result list rather than in the frame: a traceability record
            // wants "the nth code read", not "the nth segment of the message".
            codes.Add(new CodeResult(
                Index: codes.Count,
                Data: data,
                Grade: ReadGrade(fields, options),
                Judge: Verdict.Pass));
        }

        return codes;
    }

    /// <summary>
    /// 讀出一筆記錄的品質等級 / Read one record's quality grade.
    ///
    /// 三種「沒有等級」都回 null,而不是回一個數字 /
    /// All three ways of having no grade yield null rather than a number:
    /// 沒設定要讀、欄位不存在或屬於無結果字樣、以及裝置回報「未評估」。
    /// Not configured to read one, the field absent or an empty-token, and the device reporting that it
    /// did not evaluate.
    /// </summary>
    private static int? ReadGrade(string[] fields, SrX300Options options)
    {
        if (options.GradeField is not int position)
        {
            return null;
        }

        var text = NonProtocolFrame.FieldOrNull(fields, position, options);
        if (text is null)
        {
            return null;
        }

        // 負號開頭是「未評估」的哨兵,不是等級。實測無法評分的那一筆輸出 -0.0,
        // 而 ISO/IEC 15415 與 15416 的等級不可能為負。
        //
        // 必須看文字而不是看解析後的數值:decimal 會把 -0.0 正規化,value < 0 對它是 false,
        // 於是「未評估」會被當成等級 0 —— 而 0 是 ISO 刻度上最差的合格等級,
        // 那等於把「沒量到」寫成「量到最差」。
        // A leading minus is the not-evaluated sentinel rather than a grade: the record that could not be
        // scored emits -0.0 in practice, and neither ISO/IEC 15415 nor 15416 can be negative. The check has
        // to read the text rather than the parsed value, because decimal normalises -0.0 and `value < 0` is
        // false for it — so the sentinel would land as grade 0, the worst *valid* grade on the ISO scale,
        // recording "not measured" as "measured at the worst".
        //
        // 這個分辨很重要:兩者都判退,但判退原因不同,而現場依原因行動 ——
        // 「等級 0」會去調印刷,「未輸出等級」才會去查讀碼器的符號設定。
        // The distinction matters because both reject but for different reasons, and the line acts on the
        // reason: "grade 0" sends someone to the printer, "no grade reported" to the reader's symbol setup.
        if (text.StartsWith('-'))
        {
            return null;
        }

        // 等級是小數輸出（實測為 "3.0"、"4.0"）,所以不能只試整數 ——
        // int.TryParse("3.0") 會失敗,而那會讓每一筆等級都靜默變成 null,
        // 也就是讀碼器最主要的用途整個失效卻不報錯。
        // The grade is emitted with a decimal place — "3.0", "4.0" in practice — so trying an integer
        // alone is not enough: int.TryParse("3.0") fails, every grade silently becomes null, and the
        // reader's main purpose stops working without any error.
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            // 解不出數字時回 null 而不是 0：理由同上,0 是有意義的最差等級,不是「沒有等級」。
            // An unparsable grade becomes null, not 0, for the same reason: zero is a meaningful worst
            // grade rather than the absence of one.
            return null;
        }

        // 向下取整。等級門檻是「至少」,所以 3.7 必須算成 3 而不是 4 ——
        // 取整方向錯的話,門檻會比設定的寬鬆,而那是往出貨不良的方向錯。
        // Floored. The threshold means "at least", so 3.7 has to count as 3 and not 4: rounding the other
        // way makes the threshold looser than configured, and that errs towards shipping a defect.
        return (int)Math.Floor(value);
    }
}
