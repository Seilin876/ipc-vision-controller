using IpcVisionController.Core.Models;

namespace IpcVisionController.Core.Hal;

/// <summary>
/// 把 IV4 的電文解成區域結果 / Turns an IV4 frame into region results.
///
/// 格式尚未經實機驗證 / The format is not yet verified against hardware:
/// IV4 還沒導入,所以這裡沿用「單層、以分隔字元分格、位置固定」的假設 ——
/// 那是設定軟體最常見的輸出形式,但它就只是個假設。
/// 讀碼器身上已經證明過這種假設會怎麼錯:SR-X300 實際是兩層結構,單層模型讓第一筆之後的
/// 結果整批消失而不報錯。IV4 到場時要做的第一件事,是用實機電文覆核這個假設,而不是相信它。
/// The IV4 is not installed, so this keeps the assumption of a flat frame split by one delimiter at fixed
/// positions — the commonest shape such setup software emits, and no more than an assumption. The reader
/// already demonstrated how that assumption fails: the SR-X300 is actually two levels, and a flat model
/// dropped everything past the first result without an error. The first thing to do when the IV4 arrives
/// is to check this against a real frame rather than trust it.
/// </summary>
public static class CharacterFrameReader
{
    /// <summary>
    /// 讀出每個區域的結果 / Read every region's result.
    /// </summary>
    /// <param name="frame">已去除結束字元的電文本文 / The frame body, terminator already stripped.</param>
    /// <param name="options">字符檢測器設定 / The verifier's settings.</param>
    public static IReadOnlyList<CharacterResult> Read(string frame, Iv4Options options)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);

        var fields = frame.Split(options.FieldDelimiter);
        var characters = new List<CharacterResult>(options.CharacterTextFields.Count);

        for (var region = 0; region < options.CharacterTextFields.Count; region++)
        {
            var text = NonProtocolFrame.FieldOrNull(fields, options.CharacterTextFields[region], options);

            // 字符區域與條碼相反:辨識失敗仍要留一筆 null。區域是設定好的固定數量,
            // 少一筆就不是「這張標籤少一個字串」而是「感測器少回一個區域」,
            // 那兩件事的追溯意義完全不同。
            // The opposite of codes: a failed region still leaves a null entry. Regions are a fixed,
            // configured count, so a missing entry would mean "the sensor omitted a region" rather than
            // "this label lacked a string" — very different in a trace.
            characters.Add(new CharacterResult(
                Index: region,
                Text: text,
                Judge: text is null ? Verdict.Fail : Verdict.Pass));
        }

        return characters;
    }
}
