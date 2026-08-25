using IpcVisionController.Core.Models;

namespace IpcVisionController.Core.Hal;

/// <summary>
/// 把字符檢測電文的欄位對應成區域結果 / Maps a verifier frame's fields onto region results.
/// 與 <see cref="CodeFrameReader"/> 同樣是純函式,理由相同：欄位排列要能不接裝置窮舉測試。
/// A pure function for the same reason as <see cref="CodeFrameReader"/>: the field arrangements
/// have to be exhaustible without a device attached.
/// </summary>
public static class CharacterFrameReader
{
    /// <summary>
    /// 讀出每個區域的結果 / Read every region's result.
    /// </summary>
    public static IReadOnlyList<CharacterResult> Read(string[] fields, Iv4Options options)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(options);

        var characters = new List<CharacterResult>(options.CharacterTextFields.Count);

        for (var region = 0; region < options.CharacterTextFields.Count; region++)
        {
            var text = NonProtocolFrame.FieldOrNull(fields, options.CharacterTextFields[region], options);

            // 字符區域與條碼相反:辨識失敗仍要留一筆 null。區域是設定好的固定數量,
            // 少一筆就不是「這張標籤少一個字串」而是「感測器少回一個區域」,
            // 那兩件事的追溯意義完全不同。
            // The opposite of codes: a failed region still leaves a null entry. Regions are a
            // fixed, configured count, so a missing entry would mean "the sensor omitted a region"
            // rather than "this label lacked a string" — very different in a trace.
            characters.Add(new CharacterResult(
                Index: region,
                Text: text,
                Judge: text is null ? Verdict.Fail : Verdict.Pass));
        }

        return characters;
    }
}
