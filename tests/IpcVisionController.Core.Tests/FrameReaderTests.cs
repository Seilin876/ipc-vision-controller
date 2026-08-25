using IpcVisionController.Core.Hal;
using IpcVisionController.Core.Models;
using Xunit;

namespace IpcVisionController.Core.Tests;

/// <summary>
/// 電文分格與欄位對應 / Frame splitting and field mapping.
///
/// 為什麼這一組測試不接任何裝置 / Why none of these attach a device:
/// 電文格式是整條實機路徑上最不確定的一環 —— 由裝置端設定決定,只能靠實機電文確認,
/// 因此也是最常被修改的部分。分格與對應是純函式,所以每一種欄位排列都能用一串字串驗完。
/// The frame layout is the least certain link in the whole path: decided on the device, confirmable
/// only from a real frame, and therefore the most frequently edited. Splitting and mapping are pure
/// functions, so every arrangement can be settled with one string.
/// </summary>
public sealed class FrameReaderTests
{
    private const string Device = "測試裝置 / test device";

    private static SrX300Options Reader(
        IReadOnlyList<int>? data = null,
        IReadOnlyList<int>? grades = null,
        string delimiter = ",") => new()
        {
            Host = "192.168.1.10",
            Port = 9004,
            TriggerCommand = "LON",
            FieldDelimiter = delimiter,
            CodeDataFields = data ?? [1, 3],
            CodeGradeFields = grades ?? [2, 4],
        };

    private static Iv4Options Verifier(IReadOnlyList<int>? regions = null) => new()
    {
        Host = "192.168.1.11",
        Port = 8500,
        TriggerCommand = "T1",
        CharacterTextFields = regions ?? [5, 6],
    };

    private static string[] Split(string frame, NonProtocolLinkOptions options)
        => NonProtocolFrame.SplitFields(frame, options, Device);

    // ── 分格 / Splitting ────────────────────────────────────────────────────

    [Fact]
    public void SplitFields_WithAnErrorFrame_RaisesADeviceFaultNotAReject()
    {
        // 異常電文的欄位數與正常電文不同,硬解出來會變成「每個欄位都沒讀到」,
        // 也就是把設備故障誤報成一張不良標籤。
        // An error frame carries a different field count; forcing it through yields "nothing read
        // anywhere", an equipment fault misreported as one bad label.
        var error = Assert.Throws<DeviceFaultException>(() => Split("ER,03", Reader()));

        Assert.Contains("ER,03", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitFields_WithAnEmptyFrame_RaisesADeviceFaultNotAReject()
    {
        // 裝置有回應就至少會有判定欄位。當成沒讀到會讓斷線被記成一批不良品。
        // A device that answers at all emits at least a verdict field; treating this as a no-read
        // logs a dropped link as a batch of bad parts.
        Assert.Throws<DeviceFaultException>(() => Split("   ", Reader()));
    }

    [Fact]
    public void SplitFields_NamesTheDeviceInTheFault()
    {
        // 兩台裝置的訊息交錯出現,不標明來源的故障訊息無法指出該去看哪一台
        // The two devices' messages interleave, and an unattributed fault cannot say which device to
        // go and look at.
        var error = Assert.Throws<DeviceFaultException>(() => Split("ER", Reader()));

        Assert.Contains(Device, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitFields_StripsOnlyTheTerminatorNeverTrimsGenerally()
    {
        // 分隔字元是 tab 時,一般的 Trim 會吃掉開頭的空欄位,讓後面每個索引位移一格 ——
        // 不會報錯,只會讓判定看起來像印刷不良。
        // With a tab delimiter a general Trim would eat the leading empty field and shift every
        // index by one: nothing errors, and the verdicts merely look like bad print.
        var fields = Split("\tABC\t90\r\n", Reader(delimiter: "\t"));

        Assert.Equal(3, fields.Length);
        Assert.Equal(string.Empty, fields[0]);
    }

    [Fact]
    public void SplitFields_WithACustomDelimiter_SplitsOnIt()
    {
        var fields = Split("a|b|c", Reader(delimiter: "|"));

        Assert.Equal(["a", "b", "c"], fields);
    }

    // ── 條碼對應 / Code mapping ─────────────────────────────────────────────

    [Fact]
    public void Codes_WithAFullFrame_ReadsEveryCodeAndGrade()
    {
        var options = Reader();
        var codes = CodeFrameReader.Read(Split("OK,ABC123456789,92,DEF987654321,88", options), options);

        Assert.Equal(2, codes.Count);
        Assert.Equal("ABC123456789", codes[0].Data);
        Assert.Equal(92, codes[0].Grade);
        Assert.Equal("DEF987654321", codes[1].Data);
        Assert.Equal(88, codes[1].Grade);
    }

    [Fact]
    public void Codes_AreIndexedByReadOrderNotByFieldPosition()
    {
        // 追溯紀錄要的是「這次讀到的第幾筆」,不是「它躺在電文的第幾格」
        // A traceability record wants "the nth code read", not "the nth field in the frame".
        var options = Reader();
        var codes = CodeFrameReader.Read(Split("OK,NOREAD,,DEF987654321,88", options), options);

        var only = Assert.Single(codes);
        Assert.Equal(0, only.Index);
        Assert.Equal("DEF987654321", only.Data);
    }

    [Fact]
    public void Codes_WithAMissingLabel_DropsItSoTheCountCheckCatchesIt()
    {
        // 少一筆就是配方筆數檢查唯一會抓到漏貼標籤的地方
        // A dropped entry is what makes the recipe's count check the one place a missing label is
        // caught.
        var options = Reader();
        var codes = CodeFrameReader.Read(Split("OK,ABC123456789,92,NOREAD,----", options), options);

        Assert.Single(codes);
    }

    [Fact]
    public void Codes_WithEveryCodeMissing_ReturnsAnEmptyListNotAFault()
    {
        // 完全沒讀到是工件問題,判退後繼續生產 —— 不是停線的理由
        // Reading nothing is a part problem: reject and keep running, not a reason to stop the line.
        var options = Reader();
        var codes = CodeFrameReader.Read(Split("OK,NOREAD,,NOREAD,", options), options);

        Assert.Empty(codes);
    }

    [Fact]
    public void Codes_TreatEmptyTokensCaseInsensitively()
    {
        var options = Reader(data: [1], grades: []);
        var codes = CodeFrameReader.Read(Split("OK,noread", options), options);

        Assert.Empty(codes);
    }

    [Fact]
    public void Codes_WhenTheFrameStopsShort_TreatTheAbsentFieldsAsNoResult()
    {
        // 沒讀到時裝置可能就是少輸出後面幾格,那是工件問題,不該為此停掉整條線
        // On a no-read the device may simply emit fewer fields: a part problem, and not a reason to
        // stop the whole line.
        var options = Reader();
        var codes = CodeFrameReader.Read(Split("OK,ABC123456789,92", options), options);

        Assert.Single(codes);
    }

    [Fact]
    public void Codes_WithNoGradeFieldsConfigured_LeaveGradeNull()
    {
        // 讀碼器沒輸出等級時,配方的等級檢查就沒有東西可比 —— 那要由 LabelJudge 判退,
        // 不是在這裡編一個數字出來。
        // With no grade emitted the recipe's grade check has nothing to compare, which is for
        // LabelJudge to reject rather than for this layer to invent a number.
        var options = Reader(data: [1], grades: []);
        var codes = CodeFrameReader.Read(Split("OK,ABC123456789", options), options);

        Assert.Null(Assert.Single(codes).Grade);
    }

    [Fact]
    public void Codes_WithAnUnparsableGrade_LeaveGradeNullRatherThanZero()
    {
        // 0 在任何刻度上都是最差,會讓等級檢查判退一張其實沒有等級資料的標籤
        // Zero is the worst value on every scale and would reject a label that simply carried no
        // grade data.
        var options = Reader(data: [1], grades: [2]);
        var codes = CodeFrameReader.Read(Split("OK,ABC123456789,--", options), options);

        Assert.Null(Assert.Single(codes).Grade);
    }

    // ── 字符區域對應 / Character-region mapping ──────────────────────────────

    [Fact]
    public void Regions_WithAFullFrame_ReadsEveryRegion()
    {
        var options = Verifier();
        var regions = CharacterFrameReader.Read(Split("OK,x,x,x,x,LOT26A,2026-08-25", options), options);

        Assert.Equal(2, regions.Count);
        Assert.Equal("LOT26A", regions[0].Text);
        Assert.Equal("2026-08-25", regions[1].Text);
        Assert.All(regions, r => Assert.Equal(Verdict.Pass, r.Judge));
    }

    [Fact]
    public void Regions_WithAnUnrecognisedRegion_KeepANullEntryAndFailIt()
    {
        // 與條碼相反:區域數是設定好的固定值,少一筆代表「感測器少回一個區域」,
        // 而不是「這張標籤少一個字串」—— 兩者的追溯意義完全不同。
        // The opposite of codes: the region count is fixed by configuration, so a missing entry
        // would mean the sensor omitted a region rather than the label lacking a string, and the two
        // mean quite different things in a trace.
        var options = Verifier(regions: [5, 6]);
        var regions = CharacterFrameReader.Read(Split("OK,x,x,x,x,LOT26A,NG", options), options);

        Assert.Equal(2, regions.Count);
        Assert.Null(regions[1].Text);
        Assert.Equal(Verdict.Fail, regions[1].Judge);
    }

    [Fact]
    public void Regions_WhenTheFrameStopsShort_StillReportOneEntryPerConfiguredRegion()
    {
        var options = Verifier(regions: [5, 6]);
        var regions = CharacterFrameReader.Read(Split("OK,x,x,x,x,LOT26A", options), options);

        Assert.Equal(2, regions.Count);
        Assert.Equal(Verdict.Fail, regions[1].Judge);
    }

    [Fact]
    public void ReaderAndVerifier_ReadTheirOwnFieldsFromTheirOwnFrames()
    {
        // 兩台裝置各自一次觸發、各自一筆電文、各自一組索引。
        // 這一項存在的理由是先前的架構把兩者塞進同一筆電文 ——
        // 那假設了現場只有一顆感測器,而現場其實是兩台。
        // Each device has its own trigger, its own frame and its own indexes. This case exists
        // because an earlier architecture packed both into one frame, assuming a single sensor where
        // the line actually has two.
        var reader = Reader(data: [1], grades: [2]);
        var verifier = Verifier(regions: [1]);

        var codes = CodeFrameReader.Read(Split("OK,ABC123456789,92", reader), reader);
        var regions = CharacterFrameReader.Read(Split("OK,LOT26A", verifier), verifier);

        Assert.Equal("ABC123456789", Assert.Single(codes).Data);
        Assert.Equal("LOT26A", Assert.Single(regions).Text);
    }
}
