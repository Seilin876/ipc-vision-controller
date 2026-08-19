using IpcVisionController.Core.Hal;
using IpcVisionController.Core.Models;
using Xunit;

namespace IpcVisionController.Core.Tests;

/// <summary>
/// IV4 電文解析的行為規格 / Behavioural spec for IV4 frame parsing.
///
/// 為什麼這份測試是實機導入的重點 / Why this file carries the commissioning risk:
/// 電文格式由感測器端的設定決定,是整條實機路徑上唯一無法從程式碼推導的東西。
/// 這裡把「哪一種電文該解成什麼」窮舉下來,現場只要比對原始電文與這些案例,
/// 就能判斷是設定錯了還是標籤真的不良 —— 兩者的表徵都是「判退」。
/// The layout is decided on the sensor and is the one thing in the real-hardware path that
/// cannot be derived from the code. Exhausting "which frame means what" here lets the line
/// compare a raw frame against these cases and tell a misconfiguration from a genuinely bad
/// label — both of which present as a reject.
///
/// 三條分野必須守住 / Three distinctions that must hold:
/// 1. 沒讀到（工件問題,判退後繼續生產）≠ 感測器異常（設備問題,停線）。
///    A no-read (reject and continue) is not a sensor fault (stop the line).
/// 2. 沒有等級資料 ≠ 等級為 0。後者在任何刻度上都是最差,會誤判退。
///    Absent grade data is not a grade of zero, which is the worst value on every scale.
/// 3. 條碼解不出就不進清單；字符區域解不出仍留一筆 null。
///    An undecoded code leaves the list; an unrecognised region still leaves a null entry.
/// </summary>
public sealed class Iv4ResponseParserTests
{
    /// <summary>
    /// 測試用的欄位配置 / The field layout under test.
    /// 電文形如 / Frames look like:
    ///   OK,CODE-A,88,CODE-B,91,TEXT-1,TEXT-2
    ///   0  1      2  3      4  5      6
    /// </summary>
    private static Iv4Options Layout() => new()
    {
        Host = "127.0.0.1",
        CodeDataFields = [1, 3],
        CodeGradeFields = [2, 4],
        CharacterTextFields = [5, 6],
    };

    [Fact]
    public void Parse_WithAFullFrame_ReadsBothHalvesOfTheOneCapture()
    {
        var capture = Iv4ResponseParser.Parse("OK,ABC123456789,88,DEF123456789,91,LOT26A,26/08/17", Layout());

        Assert.Equal(["ABC123456789", "DEF123456789"], capture.Codes.Select(c => c.Data));
        Assert.Equal([88, 91], capture.Codes.Select(c => c.Grade));
        Assert.Equal(["LOT26A", "26/08/17"], capture.Characters.Select(c => c.Text));
        Assert.All(capture.Codes, code => Assert.Equal(Verdict.Pass, code.Judge));
        Assert.All(capture.Characters, region => Assert.Equal(Verdict.Pass, region.Judge));
    }

    [Fact]
    public void Parse_IndexesCodesByReadOrderNotByFieldPosition()
    {
        // 第一格條碼沒讀到,第二格讀到 / The first code slot is a no-read, the second is not.
        var capture = Iv4ResponseParser.Parse("OK,NOREAD,,DEF123456789,91,LOT26A,26/08/17", Layout());

        // 追溯紀錄要的是「這次讀到的第幾筆」,不是「它躺在電文的第幾格」
        // A trace wants "the nth code read", not "the nth field in the frame".
        var code = Assert.Single(capture.Codes);
        Assert.Equal(0, code.Index);
        Assert.Equal("DEF123456789", code.Data);
        Assert.Equal(91, code.Grade);
    }

    [Fact]
    public void Parse_WithAMissingLabel_DropsItFromTheListSoTheCountCheckCatchesIt()
    {
        var capture = Iv4ResponseParser.Parse("OK,ABC123456789,88,,,LOT26A,26/08/17", Layout());

        // 漏貼標籤唯一會被抓到的地方是配方的 ExpectedCodeCount,
        // 而那需要「少一張就少一筆」而不是「少一張就多一筆空的」。
        // ExpectedCodeCount is the only check that catches a missing label, and it needs one
        // fewer entry rather than one more empty entry.
        Assert.Single(capture.Codes);
    }

    [Fact]
    public void Parse_WithEveryCodeMissing_ReturnsAnEmptyListNotAFault()
    {
        var capture = Iv4ResponseParser.Parse("OK,NOREAD,,NOREAD,,LOT26A,26/08/17", Layout());

        // 整批沒讀到仍是工件問題:判退後繼續生產,不可停線
        // A whole-trigger no-read is still a part problem: reject and keep running.
        Assert.Empty(capture.Codes);
        Assert.Equal(2, capture.Characters.Count);
    }

    [Theory]
    [InlineData("NG")]
    [InlineData("noread")]
    [InlineData("----")]
    [InlineData("  ")]
    public void Parse_TreatsEmptyTokensAsNoResultCaseInsensitively(string token)
    {
        var capture = Iv4ResponseParser.Parse($"OK,{token},88,DEF123456789,91,LOT26A,26/08/17", Layout());

        Assert.Single(capture.Codes);
        Assert.Equal("DEF123456789", capture.Codes[0].Data);
    }

    [Fact]
    public void Parse_WithAnUnrecognisedRegion_KeepsANullEntryAndFailsIt()
    {
        var capture = Iv4ResponseParser.Parse("OK,ABC123456789,88,DEF123456789,91,,26/08/17", Layout());

        // 區域數量是設定好的固定值。少一筆代表「感測器少回一個區域」,
        // 與「這張標籤少一個字串」的追溯意義完全不同,所以要留下 null 而非刪掉。
        // The region count is fixed by configuration. A missing entry would mean the sensor
        // omitted a region, which traces differently from a label lacking a string, so a null
        // entry is kept rather than dropped.
        Assert.Equal(2, capture.Characters.Count);
        Assert.Null(capture.Characters[0].Text);
        Assert.Equal(Verdict.Fail, capture.Characters[0].Judge);
        Assert.Equal("26/08/17", capture.Characters[1].Text);
        Assert.Equal(Verdict.Pass, capture.Characters[1].Judge);
    }

    [Fact]
    public void Parse_WhenTheFrameStopsShort_TreatsTheAbsentFieldsAsNoResult()
    {
        // IV4 在沒讀到時可能就是少輸出後面幾格。索引超出範圍是工件問題而非設定錯誤,
        // 拋例外會讓一張漏貼的標籤停掉整條線。
        // On a no-read the IV4 may simply emit fewer fields. An out-of-range index is a part
        // problem, not a misconfiguration, and throwing would stop the line for one label.
        var capture = Iv4ResponseParser.Parse("OK,ABC123456789,88", Layout());

        Assert.Single(capture.Codes);
        Assert.Equal(2, capture.Characters.Count);
        Assert.All(capture.Characters, region => Assert.Null(region.Text));
    }

    [Fact]
    public void Parse_WithNoGradeFieldsConfigured_LeavesGradeNull()
    {
        var options = Layout();
        options.CodeGradeFields = [];

        var capture = Iv4ResponseParser.Parse("OK,ABC123456789,88,DEF123456789,91,LOT26A,26/08/17", options);

        // 感測器沒輸出等級時 Grade 必須是 null,配方的等級檢查才會自然失效而非全部判退
        // With no grade emitted, Grade must be null so the recipe's grade check stands down
        // instead of rejecting everything.
        Assert.All(capture.Codes, code => Assert.Null(code.Grade));
    }

    [Fact]
    public void Parse_WithAnUnparsableGrade_LeavesGradeNullRatherThanZero()
    {
        var capture = Iv4ResponseParser.Parse("OK,ABC123456789,---,DEF123456789,91,LOT26A,26/08/17", Layout());

        // 0 在 SR 的 0–100 與 ISO 的 0–4 上都是「最差」。把解不開的等級當成 0,
        // 會讓一張其實沒有等級資料的標籤被等級檢查判退。
        // Zero is the worst value on both the SR 0–100 scale and the ISO 0–4 scale. Reading an
        // unparsable grade as zero would reject a label that merely carried no grade data.
        Assert.Null(capture.Codes[0].Grade);
        Assert.Equal("ABC123456789", capture.Codes[0].Data);
        Assert.Equal(91, capture.Codes[1].Grade);
    }

    [Fact]
    public void Parse_WithAnErrorFrame_RaisesADeviceFaultNotAReject()
    {
        // 感測器自身異常必須停線。當成「每個欄位都沒讀到」會把設備故障
        // 記成一整批不良品,而真正的原因不會留在任何地方。
        // A sensor-level fault must stop the line. Read as "nothing in any field" it would log
        // an equipment failure as a batch of bad parts, with the real cause recorded nowhere.
        var ex = Assert.Throws<DeviceFaultException>(
            () => Iv4ResponseParser.Parse("ER,04", Layout()));

        Assert.Contains("ER,04", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_WithAnEmptyFrame_RaisesADeviceFaultNotAReject()
    {
        // 有回應的感測器至少會送回判定欄位。空電文是通訊異常,
        // 當成沒讀到會把斷線記成一批不良品。
        // A sensor that answers at all emits a verdict field. An empty frame is a comms
        // failure; treated as a no-read it logs a dropped link as a batch of bad parts.
        Assert.Throws<DeviceFaultException>(() => Iv4ResponseParser.Parse("   ", Layout()));
    }

    [Fact]
    public void Parse_WithACustomDelimiter_SplitsOnIt()
    {
        var options = Layout();
        options.FieldDelimiter = "\t";

        var capture = Iv4ResponseParser.Parse(
            "OK\tABC123456789\t88\tDEF123456789\t91\tLOT26A\t26/08/17", options);

        Assert.Equal(2, capture.Codes.Count);
        Assert.Equal("LOT26A", capture.Characters[0].Text);
    }
}
