namespace IpcVisionController.Core.Hal;

/// <summary>
/// 無協定電文的共通處理 / Handling common to every non-protocol frame.
///
/// 這裡只做「不分裝置都一樣」的三件事:去除結束字元、攔下裝置自身異常、擋掉空電文。
/// 電文的內部結構由各裝置自己解讀 —— 讀碼器是兩層（多筆記錄,每筆再分欄）,
/// 字符檢測器目前假設為單層,而那個假設在實機到場前無法驗證。把結構的解讀留在各自的裝置裡,
/// 才不會因為其中一台的格式而扭曲另一台。
/// Only the three things that are the same for every device happen here: strip the terminator, catch a
/// device-level fault, refuse an empty frame. The internal structure is each device's own business —
/// the reader's is two levels, records subdivided into fields, while the verifier's is assumed flat and
/// that assumption cannot be checked until the hardware arrives. Keeping structure with the device is
/// what stops one device's format from distorting the other's.
///
/// 刻意做成純函式 / Deliberately pure functions:
/// 電文格式是整條實機路徑上最不確定的一環 —— 它由裝置端的設定決定,只能靠實機看到的原始電文
/// 來確認,因此也是最常被修改的部分。與 TCP 連線分開,格式就能用一堆字串窮舉測試。
/// The frame layout is the least certain link in the whole real-hardware path: decided on the device,
/// confirmable only from a captured frame, and therefore the most frequently edited. Kept apart from the
/// socket, it can be exhausted with plain strings.
/// </summary>
public static class NonProtocolFrame
{
    /// <summary>
    /// 去除結束字元並把設備層級的異常攔下 / Strip the terminator and catch device-level trouble.
    /// </summary>
    /// <param name="frame">收到的電文 / The frame as received.</param>
    /// <param name="options">連線設定 / The link settings.</param>
    /// <param name="deviceName">裝置代號,用於錯誤訊息 / Device tag, for the error message.</param>
    /// <returns>可供解讀的電文本文 / The frame body, ready to be interpreted.</returns>
    /// <exception cref="DeviceFaultException">
    /// 電文為裝置自身異常,或為空 / The frame reports a device fault, or is empty.
    /// </exception>
    public static string Clean(string frame, NonProtocolLinkOptions options, string deviceName)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);

        // 只去掉結束字元,不做一般的 Trim。分隔字元是 tab 或空白時,一般的 Trim 會吃掉
        // 開頭的空欄位,讓後面每一個位置默默位移一格 —— 那是最難察覺的誤解方式,
        // 因為它不會報錯,只會讓判定結果看起來像印刷不良。
        // Strip the terminator characters only, never a general Trim. With a tab or space delimiter a
        // general Trim eats a leading empty field and silently shifts every position by one — the hardest
        // kind of misparse to notice, because nothing errors and the verdicts merely look like bad print.
        var framed = frame.Trim('\r', '\n');

        // 裝置自身異常必須在解讀之前攔下：異常電文的結構與正常電文不同,
        // 硬解出來會變成「什麼都沒讀到」,也就是把設備故障誤報成一張不良標籤。
        // A device-level fault must be caught before interpreting: an error frame is shaped differently,
        // and forcing it through yields "nothing read at all" — an equipment fault misreported as one bad
        // label.
        //
        // ErrorPrefix 留空即停用本檢查。讀碼器讀不到時若輸出的字樣剛好以 ErrorPrefix 開頭,
        // 「漏貼一張標籤」就會停掉整條線 —— 那時停用本檢查、改把該字樣列入 EmptyTokens
        // 才是對的做法。
        // An empty ErrorPrefix disables this check. If what the reader emits on a no-read happens to start
        // with ErrorPrefix, one missing label would stop the whole line; disabling this and listing that
        // token in EmptyTokens instead is then the right answer.
        if (!string.IsNullOrEmpty(options.ErrorPrefix)
            && framed.TrimStart().StartsWith(options.ErrorPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new DeviceFaultException($"{deviceName} 回報異常電文 / reported an error frame: {framed.Trim()}");
        }

        // 空電文不是「沒讀到」而是通訊異常：裝置有回應就至少會有內容。
        // 當成沒讀到會讓斷線被記成一批不良品。
        // An empty frame is a comms failure, not a no-read: a device that answers at all sends something.
        // Treating it as a no-read logs a dropped link as a batch of bad parts.
        if (string.IsNullOrWhiteSpace(framed))
        {
            throw new DeviceFaultException($"{deviceName} 回應為空電文 / returned an empty frame.");
        }

        return framed;
    }

    /// <summary>
    /// 取欄位內容；不存在、空白或屬於「無結果」字樣時回 null /
    /// The field's text, or null when it is absent, blank, or an empty-token.
    ///
    /// 位置超出範圍刻意不拋例外：裝置在沒讀到時可能就是少輸出後面幾格,
    /// 那是工件問題而非設定錯誤,拋例外會讓一張漏貼的標籤停掉整條線。
    /// An out-of-range position deliberately does not throw. On a no-read the device may simply emit
    /// fewer fields; that is a part problem, not a misconfiguration, and throwing would stop the whole
    /// line for one missing label.
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
