using System.Globalization;
using IpcVisionController.Core.Models;

namespace IpcVisionController.Core.Hal;

/// <summary>
/// 一次 IV4 觸發的完整結果 / Everything one IV4 trigger produced.
/// 兩半來自同一次拍攝,所以追溯紀錄裡的條碼與字符必然描述同一張標籤 ——
/// 分兩次觸發就無法保證這件事。
/// Both halves come from one capture, so the codes and the characters in a traceability
/// record necessarily describe the same label. Two separate triggers cannot promise that.
/// </summary>
public sealed record Iv4Capture(
    IReadOnlyList<CodeResult> Codes,
    IReadOnlyList<CharacterResult> Characters);

/// <summary>
/// 把 IV4 的無協定電文解成結果物件 / Turns an IV4 non-protocol frame into result objects.
///
/// 刻意做成純函式與獨立類別 / Deliberately a pure function in its own class:
/// 電文格式是這整條實機路徑上最不確定的一環 —— 它由感測器端的設定決定,只能靠實機看到的
/// 原始電文來確認。把它與 TCP 連線分開,格式就能用一堆字串窮舉測試,不必接上感測器,
/// 也不必為了改一個欄位索引去碰通訊程式碼。
/// The frame layout is the least certain link in the whole real-hardware path: it is decided
/// on the sensor and can only be confirmed from a frame captured off the real device. Kept
/// apart from the socket, the layout can be exhausted with plain strings — no sensor
/// attached — and a field index can change without touching networking code.
/// </summary>
public static class Iv4ResponseParser
{
    /// <summary>
    /// 解析一筆電文 / Parse one frame.
    /// </summary>
    /// <param name="frame">已去除結束字元的電文 / The frame with its terminator already stripped.</param>
    /// <param name="options">欄位配置 / The field layout.</param>
    /// <exception cref="DeviceFaultException">
    /// 電文為感測器自身異常 / The frame reports a sensor-level fault.
    /// </exception>
    public static Iv4Capture Parse(string frame, Iv4Options options)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);

        // 只去掉結束字元,不做一般的 Trim。分隔字元是 tab 或空白時,一般的 Trim 會吃掉
        // 開頭的空欄位,讓後面每一個欄位索引默默位移一格 —— 那是最難察覺的誤解方式,
        // 因為它不會報錯,只會讓判定結果看起來像印刷不良。
        // Strip the terminator characters only, never a general Trim. With a tab or space
        // delimiter a general Trim eats a leading empty field and silently shifts every index
        // by one — the hardest kind of misparse to notice, because nothing errors and the
        // verdicts merely look like bad print.
        var framed = frame.Trim('\r', '\n');

        // 感測器自身異常必須在解析之前攔下：異常電文的欄位數與正常電文不同,
        // 硬解出來會變成「每個欄位都沒讀到」,也就是把設備故障誤報成一張不良標籤。
        // A sensor-level fault must be caught before parsing: an error frame carries a
        // different field count, and forcing it through yields "nothing read in any field" —
        // an equipment fault misreported as one bad label.
        if (!string.IsNullOrEmpty(options.ErrorPrefix)
            && framed.TrimStart().StartsWith(options.ErrorPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeviceFaultException(
                $"IV4 回報異常電文 / reported an error frame: {framed.Trim()}");
        }

        // 空電文不是「沒讀到」而是通訊異常：感測器有回應就至少會有判定欄位。
        // 當成沒讀到會讓斷線被記成一批不良品。
        // An empty frame is a comms failure, not a no-read: a sensor that answers at all
        // emits at least a verdict field. Treating it as a no-read logs a dropped link as a
        // batch of bad parts.
        if (string.IsNullOrWhiteSpace(framed))
        {
            throw new DeviceFaultException("IV4 回應為空電文 / returned an empty frame.");
        }

        var fields = framed.Split(options.FieldDelimiter);

        return new Iv4Capture(
            Codes: ReadCodes(fields, options),
            Characters: ReadCharacters(fields, options));
    }

    private static List<CodeResult> ReadCodes(string[] fields, Iv4Options options)
    {
        var codes = new List<CodeResult>(options.CodeDataFields.Count);

        for (var slot = 0; slot < options.CodeDataFields.Count; slot++)
        {
            var data = FieldOrNull(fields, options.CodeDataFields[slot], options);

            // 解不出來的條碼根本不進結果清單,而不是回一筆空的 —— 與 SR 系列的語意一致,
            // 也讓配方的「筆數不足」檢查成為漏貼標籤唯一會被抓到的地方。
            // A code that did not decode never enters the list rather than appearing as an
            // empty entry. That matches the SR-series semantics and keeps the recipe's count
            // check as the one place a missing label gets caught.
            if (data is null)
            {
                continue;
            }

            // Index 用結果清單目前的長度,而非設定裡的欄位序 —— 追溯紀錄要的是
            // 「這次讀到的第幾筆」,不是「它躺在電文的第幾格」。
            // Index is the position in the result list, not the configured slot: a
            // traceability record wants "the nth code read", not "the nth field in the frame".
            codes.Add(new CodeResult(
                Index: codes.Count,
                Data: data,
                Grade: ReadGrade(fields, options, slot),
                Judge: Verdict.Pass));
        }

        return codes;
    }

    private static int? ReadGrade(string[] fields, Iv4Options options, int slot)
    {
        if (slot >= options.CodeGradeFields.Count)
        {
            return null;
        }

        var text = FieldOrNull(fields, options.CodeGradeFields[slot], options);
        if (text is null)
        {
            return null;
        }

        // 解不出數字時回 null 而不是 0：0 在任何刻度上都是「最差」,會讓等級檢查
        // 把一張其實沒有等級資料的標籤判退。
        // An unparsable grade becomes null, not 0. Zero is the worst value on every scale and
        // would reject a label that simply carried no grade data.
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var grade)
            ? grade
            : null;
    }

    private static List<CharacterResult> ReadCharacters(string[] fields, Iv4Options options)
    {
        var characters = new List<CharacterResult>(options.CharacterTextFields.Count);

        for (var region = 0; region < options.CharacterTextFields.Count; region++)
        {
            var text = FieldOrNull(fields, options.CharacterTextFields[region], options);

            // 字符區域與條碼相反:辨識失敗仍要留一筆 null。區域是設定好的固定數量,
            // 少一筆就不是「這張標籤少一個字串」而是「感測器少回一個區域」,
            // 那兩件事的追溯意義完全不同。
            // The opposite of codes: a failed region still leaves a null entry. Regions are a
            // fixed, configured count, so a missing entry would mean "the sensor omitted a
            // region" rather than "this label lacked a string" — very different in a trace.
            characters.Add(new CharacterResult(
                Index: region,
                Text: text,
                Judge: text is null ? Verdict.Fail : Verdict.Pass));
        }

        return characters;
    }

    /// <summary>
    /// 取欄位內容；不存在、空白或屬於「無結果」字樣時回 null /
    /// The field's text, or null when it is absent, blank, or an empty-token.
    ///
    /// 索引超出電文範圍刻意不拋例外：IV4 在沒讀到時可能就是少輸出後面幾格,
    /// 那是工件問題而非設定錯誤,拋例外會讓一張漏貼的標籤停掉整條線。
    /// An out-of-range index deliberately does not throw. On a no-read the IV4 may simply
    /// emit fewer fields; that is a part problem, not a misconfiguration, and throwing would
    /// stop the whole line for one missing label.
    /// </summary>
    private static string? FieldOrNull(string[] fields, int index, Iv4Options options)
    {
        if (index >= fields.Length)
        {
            return null;
        }

        var text = fields[index].Trim();
        if (text.Length == 0)
        {
            return null;
        }

        foreach (var token in options.EmptyTokens)
        {
            if (string.Equals(text, token, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return text;
    }
}
