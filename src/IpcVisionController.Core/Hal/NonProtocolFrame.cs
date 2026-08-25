namespace IpcVisionController.Core.Hal;

/// <summary>
/// 無協定電文的分格與取值 / Splitting a non-protocol frame and reading fields out of it.
///
/// 刻意做成純函式 / Deliberately pure functions:
/// 電文格式是整條實機路徑上最不確定的一環 —— 它由裝置端的設定決定,只能靠實機看到的原始電文
/// 來確認。與 TCP 連線分開,格式就能用一堆字串窮舉測試,不必接上任何裝置,
/// 也不必為了改一個欄位索引去碰通訊程式碼。
/// The frame layout is the least certain link in the whole real-hardware path: it is decided on
/// the device and can only be confirmed from a frame captured off it. Kept apart from the socket,
/// the layout can be exhausted with plain strings — no device attached — and a field index can
/// change without touching networking code.
/// </summary>
public static class NonProtocolFrame
{
    /// <summary>
    /// 把電文切成欄位 / Split one frame into its fields.
    /// </summary>
    /// <param name="frame">已去除結束字元的電文 / The frame with its terminator already stripped.</param>
    /// <param name="options">連線設定 / The link settings.</param>
    /// <param name="deviceName">裝置代號,用於錯誤訊息 / Device tag, for the error message.</param>
    /// <exception cref="DeviceFaultException">
    /// 電文為裝置自身異常,或為空 / The frame reports a device fault, or is empty.
    /// </exception>
    public static string[] SplitFields(string frame, NonProtocolLinkOptions options, string deviceName)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);

        // 只去掉結束字元,不做一般的 Trim。分隔字元是 tab 或空白時,一般的 Trim 會吃掉
        // 開頭的空欄位,讓後面每一個欄位索引默默位移一格 —— 那是最難察覺的誤解方式,
        // 因為它不會報錯,只會讓判定結果看起來像印刷不良。
        // Strip the terminator characters only, never a general Trim. With a tab or space
        // delimiter a general Trim eats a leading empty field and silently shifts every index by
        // one — the hardest kind of misparse to notice, because nothing errors and the verdicts
        // merely look like bad print.
        var framed = frame.Trim('\r', '\n');

        // 裝置自身異常必須在解析之前攔下：異常電文的欄位數與正常電文不同,
        // 硬解出來會變成「每個欄位都沒讀到」,也就是把設備故障誤報成一張不良標籤。
        // A device-level fault must be caught before parsing: an error frame carries a different
        // field count, and forcing it through yields "nothing read in any field" — an equipment
        // fault misreported as one bad label.
        if (!string.IsNullOrEmpty(options.ErrorPrefix)
            && framed.TrimStart().StartsWith(options.ErrorPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeviceFaultException($"{deviceName} 回報異常電文 / reported an error frame: {framed.Trim()}");
        }

        // 空電文不是「沒讀到」而是通訊異常：裝置有回應就至少會有判定欄位。
        // 當成沒讀到會讓斷線被記成一批不良品。
        // An empty frame is a comms failure, not a no-read: a device that answers at all emits at
        // least a verdict field. Treating it as a no-read logs a dropped link as a batch of bad
        // parts.
        if (string.IsNullOrWhiteSpace(framed))
        {
            throw new DeviceFaultException($"{deviceName} 回應為空電文 / returned an empty frame.");
        }

        return framed.Split(options.FieldDelimiter);
    }

    /// <summary>
    /// 取欄位內容；不存在、空白或屬於「無結果」字樣時回 null /
    /// The field's text, or null when it is absent, blank, or an empty-token.
    ///
    /// 索引超出電文範圍刻意不拋例外：裝置在沒讀到時可能就是少輸出後面幾格,
    /// 那是工件問題而非設定錯誤,拋例外會讓一張漏貼的標籤停掉整條線。
    /// An out-of-range index deliberately does not throw. On a no-read the device may simply emit
    /// fewer fields; that is a part problem, not a misconfiguration, and throwing would stop the
    /// whole line for one missing label.
    /// </summary>
    public static string? FieldOrNull(string[] fields, int index, NonProtocolLinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(options);

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
