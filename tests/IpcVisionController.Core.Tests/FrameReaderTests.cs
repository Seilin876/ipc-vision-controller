using IpcVisionController.Core.Hal;
using IpcVisionController.Core.Models;
using Xunit;

namespace IpcVisionController.Core.Tests;

/// <summary>
/// 電文解讀 / Interpreting a frame.
///
/// 為什麼這一組測試不接任何裝置 / Why none of these attach a device:
/// 電文格式是整條實機路徑上最不確定的一環 —— 由裝置端設定決定,只能靠實機電文確認,
/// 因此也是最常被修改的部分。解讀是純函式,所以每一種格式都能用一串字串驗完。
/// The frame layout is the least certain link in the whole path: decided on the device, confirmable only
/// from a real frame, and therefore the most frequently edited. Interpreting is a pure function, so every
/// shape can be settled with one string.
///
/// 測資取自實機 / The fixtures come from the hardware:
/// 下面的電文是 AutoID Terminal 從 SR-X300 實際擷取的,包含它的兩層結構、小數等級輸出,
/// 以及「未評估」用的 -0.0 哨兵。用實機字串當測資,是這一層唯一能避免「測到自己的假設」的辦法
/// —— 先前那份測試用的是我想像的格式,而它讓一個真實的解析錯誤完全看不出來。
/// The frames below were captured from the SR-X300 through AutoID Terminal, complete with its two levels,
/// its decimal grade output and the -0.0 sentinel for not-evaluated. Using real strings is the only way
/// this layer avoids testing my own assumptions: the previous suite used a format I had imagined, and it
/// left a genuine parsing error completely invisible.
/// </summary>
public sealed class FrameReaderTests
{
    private const string Device = "測試裝置 / test device";

    private static SrX300Options Reader(int? gradeField = 1) => new()
    {
        Host = "192.168.1.10",
        Port = 9004,
        TriggerCommand = "LON",
        RecordDelimiter = ",",
        FieldDelimiter = ":",
        CodeField = 0,
        GradeField = gradeField,
    };

    private static Iv4Options Verifier(IReadOnlyList<int>? regions = null) => new()
    {
        Host = "192.168.1.11",
        Port = 8500,
        TriggerCommand = "T1",
        FieldDelimiter = ",",
        CharacterTextFields = regions ?? [0, 1],
    };

    private static string Clean(string frame, NonProtocolLinkOptions options)
        => NonProtocolFrame.Clean(frame, options, Device);

    // ── 實機電文 / Real captured frames ─────────────────────────────────────

    /// <summary>單筆條碼,等級 A 級 / One code graded at the top of the scale.</summary>
    private const string OneCode = "2132030090A0S6X10703:4.0";

    /// <summary>六筆條碼,其中一筆未評估 / Six codes, one of them not evaluated.</summary>
    private const string SixCodes =
        "2132030090A0S6X10683:-0.0,2132030090A0S6X10685:3.0,2132030090A0S6X10684:3.0,"
        + "2132030090A0S6X10679:4.0,2132030090A0S6X10688:2.0,2132030090A0S6X10687:3.0";

    [Fact]
    public void Codes_WithOneRecord_ReadsTheCodeAndItsGrade()
    {
        var options = Reader();
        var codes = CodeFrameReader.Read(Clean(OneCode + "\r", options), options);

        var only = Assert.Single(codes);
        Assert.Equal("2132030090A0S6X10703", only.Data);
        Assert.Equal(20, only.Data!.Length);
        Assert.Equal(4, only.Grade);
    }

    [Fact]
    public void Codes_WithManyRecords_ReadsEveryOneInOrder()
    {
        // 筆數由電文決定,不由設定決定。先前的單層索引模型會讓第一筆之後的結果整批消失
        // 而不報錯 —— 那正是這一項存在的理由。
        // The count comes from the frame, not from configuration. The previous flat-index model dropped
        // everything past the first result without an error, which is exactly why this case exists.
        var options = Reader();
        var codes = CodeFrameReader.Read(Clean(SixCodes, options), options);

        Assert.Equal(6, codes.Count);
        Assert.Equal(
            [
                "2132030090A0S6X10683", "2132030090A0S6X10685", "2132030090A0S6X10684",
                "2132030090A0S6X10679", "2132030090A0S6X10688", "2132030090A0S6X10687",
            ],
            codes.Select(c => c.Data));

        // Index 是「這次讀到的第幾筆」,而非它躺在電文的第幾段
        // Index is the nth code read, not the nth segment of the message.
        Assert.Equal([0, 1, 2, 3, 4, 5], codes.Select(c => c.Index));
    }

    [Fact]
    public void Codes_ReadTheGradePairedWithItsOwnCode()
    {
        // 兩層結構讓等級與條碼天生綁在同一筆記錄裡,不需要位置對應 ——
        // 也就不可能發生「等級掛到別筆條碼上」的錯位。
        // The two levels bind a grade to its own code within one record, with no positional pairing to keep
        // in step, so a grade can never end up attached to the wrong code.
        var options = Reader();
        var codes = CodeFrameReader.Read(Clean(SixCodes, options), options);

        Assert.Equal([null, 3, 3, 4, 2, 3], codes.Select(c => c.Grade));
    }

    [Fact]
    public void Codes_WithADecimalGrade_ParseItRatherThanDroppingIt()
    {
        // 等級實機輸出帶小數位（"3.0"),只試整數的話 int.TryParse 會失敗,
        // 每一筆等級都靜默變成 null —— 讀碼器最主要的用途整個失效卻不報錯。
        // The hardware emits a decimal place, so trying an integer alone fails: every grade silently
        // becomes null and the reader's main purpose stops working without any error.
        var options = Reader();
        var codes = CodeFrameReader.Read(Clean("2132030090A0S6X10685:3.0", options), options);

        Assert.Equal(3, Assert.Single(codes).Grade);
    }

    [Fact]
    public void Codes_WithANegativeGrade_TreatItAsNotEvaluatedRatherThanZero()
    {
        // -0.0 是實機用來表示「未評估」的哨兵。0 在 ISO 刻度上是最差的合格等級,
        // 把未評估記成 0 等於把「沒量到」寫成「量到最差」——
        // 兩者都會判退,但判退原因不同,而現場依原因行動。
        // -0.0 is the hardware's not-evaluated sentinel. Zero is the worst *valid* grade on the ISO scale,
        // so recording the sentinel as zero writes "not measured" as "measured at the worst". Both reject,
        // for different reasons, and the line acts on the reason.
        var options = Reader();
        var codes = CodeFrameReader.Read(Clean("2132030090A0S6X10683:-0.0", options), options);

        Assert.Null(Assert.Single(codes).Grade);
    }

    [Fact]
    public void Codes_WithAFractionalGrade_FloorItSoTheThresholdStaysStrict()
    {
        // 門檻的語意是「至少」,所以 3.7 必須算成 3。往上取整會讓門檻比設定的寬鬆,
        // 而那是往出貨不良的方向錯。
        // The threshold means "at least", so 3.7 counts as 3. Rounding up makes it looser than configured,
        // and that errs towards shipping a defect.
        var options = Reader();
        var codes = CodeFrameReader.Read(Clean("2132030090A0S6X10685:3.7", options), options);

        Assert.Equal(3, Assert.Single(codes).Grade);
    }

    [Fact]
    public void Codes_WithNoGradeFieldConfigured_LeaveGradeNull()
    {
        var options = Reader(gradeField: null);
        var codes = CodeFrameReader.Read(Clean(OneCode, options), options);

        Assert.Null(Assert.Single(codes).Grade);
    }

    [Fact]
    public void Codes_WithNoGradeAppended_LeaveGradeNullRatherThanFailing()
    {
        // 讀碼器可以完全不輸出附加數據。此時記錄裡只有條碼一格,而等級位置不存在 ——
        // 那是設定選擇,不是錯誤,不該讓一張標籤停線。
        // Appended data can be switched off entirely, leaving one field per record and no grade position.
        // That is a configuration choice rather than an error, and must not stop the line over one label.
        var options = Reader();
        var codes = CodeFrameReader.Read(Clean("2132030090A0S6X10703", options), options);

        var only = Assert.Single(codes);
        Assert.Equal("2132030090A0S6X10703", only.Data);
        Assert.Null(only.Grade);
    }

    [Fact]
    public void Codes_WithAMissingRead_DropThatRecordSoTheCountCheckCatchesIt()
    {
        // 少一筆就是配方筆數檢查唯一會抓到漏貼標籤的地方
        // A dropped record is what makes the recipe's count check the one place a missing label is caught.
        var options = Reader();
        var codes = CodeFrameReader.Read(
            Clean("2132030090A0S6X10683:3.0,NOREAD:-0.0,2132030090A0S6X10685:4.0", options), options);

        Assert.Equal(2, codes.Count);
        Assert.Equal([0, 1], codes.Select(c => c.Index));
    }

    [Fact]
    public void Codes_WithNothingRead_ReturnAnEmptyListNotAFault()
    {
        // 完全沒讀到是工件問題,判退後繼續生產 —— 不是停線的理由
        // Reading nothing is a part problem: reject and keep running, not a reason to stop the line.
        var options = Reader();
        var codes = CodeFrameReader.Read(Clean("NOREAD", options), options);

        Assert.Empty(codes);
    }

    // ── 共通處理 / Handling common to every device ──────────────────────────

    [Fact]
    public void Clean_StripsTheTerminatorOnly()
    {
        // 分隔字元是 tab 時,一般的 Trim 會吃掉開頭的空欄位,讓後面每個位置位移一格 ——
        // 不會報錯,只會讓判定看起來像印刷不良。
        // With a tab delimiter a general Trim would eat the leading empty field and shift every position by
        // one: nothing errors, and the verdicts merely look like bad print.
        var options = Reader();

        Assert.Equal("\tABC", Clean("\tABC\r\n", options));
    }

    [Fact]
    public void Clean_WithAnErrorFrame_RaisesADeviceFaultNamingTheDevice()
    {
        // 異常電文的結構與正常電文不同,硬解會變成「什麼都沒讀到」,
        // 也就是把設備故障誤報成一張不良標籤。訊息要指名裝置 ——
        // 兩台裝置的訊息交錯出現,不標明來源就無法指出該去看哪一台。
        // An error frame is shaped differently and forcing it through yields "nothing read", an equipment
        // fault misreported as one bad label. The message names the device: the two devices' messages
        // interleave, and an unattributed fault cannot say which one to go and look at.
        var error = Assert.Throws<DeviceFaultException>(() => Clean("ER,03", Reader()));

        Assert.Contains("ER,03", error.Message, StringComparison.Ordinal);
        Assert.Contains(Device, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Clean_WithAnEmptyErrorPrefix_LetsTheFrameThrough()
    {
        // 讀碼器讀不到時輸出的字樣若剛好以 ErrorPrefix 開頭,「漏貼一張標籤」就會停掉整條線。
        // 停用本檢查、改把該字樣列入 EmptyTokens,才會讓它變回一次判退。
        // If what the reader emits on a no-read happens to start with ErrorPrefix, one missing label stops
        // the whole line. Disabling the check and listing that token in EmptyTokens turns it back into a
        // single reject.
        var options = Reader();
        options.ErrorPrefix = string.Empty;
        options.EmptyTokens = ["ERROR"];

        var codes = CodeFrameReader.Read(Clean("ERROR", options), options);

        Assert.Empty(codes);
    }

    [Fact]
    public void Clean_WithAnEmptyFrame_RaisesADeviceFaultNotAReject()
    {
        // 裝置有回應就至少會有內容。當成沒讀到會讓斷線被記成一批不良品。
        // A device that answers at all sends something; treating this as a no-read logs a dropped link as a
        // batch of bad parts.
        Assert.Throws<DeviceFaultException>(() => Clean("   ", Reader()));
    }

    // ── 字符區域 / Character regions ────────────────────────────────────────

    [Fact]
    public void Regions_ReadEachConfiguredPosition()
    {
        var options = Verifier(regions: [0, 1]);
        var regions = CharacterFrameReader.Read(Clean("LOT26A,2026-08-26", options), options);

        Assert.Equal(["LOT26A", "2026-08-26"], regions.Select(r => r.Text));
        Assert.All(regions, r => Assert.Equal(Verdict.Pass, r.Judge));
    }

    [Fact]
    public void Regions_WithAnUnrecognisedRegion_KeepANullEntryAndFailIt()
    {
        // 與條碼相反:區域數是設定好的固定值,少一筆代表「感測器少回一個區域」,
        // 而不是「這張標籤少一個字串」—— 兩者的追溯意義完全不同。
        // The opposite of codes: the region count is fixed by configuration, so a missing entry means the
        // sensor omitted a region rather than the label lacking a string, and the two mean quite different
        // things in a trace.
        var options = Verifier(regions: [0, 1]);
        var regions = CharacterFrameReader.Read(Clean("LOT26A,NG", options), options);

        Assert.Equal(2, regions.Count);
        Assert.Null(regions[1].Text);
        Assert.Equal(Verdict.Fail, regions[1].Judge);
    }

    [Fact]
    public void ReaderAndVerifier_InterpretTheirOwnFramesTheirOwnWay()
    {
        // 兩台裝置的電文結構不同:讀碼器兩層,字符檢測器單層。連線層若替兩台分格,
        // 就是挑一種結構強加給另一台 —— 那正是讀碼器第二筆之後整批消失的原因。
        // The two are shaped differently: the reader has two levels, the verifier one. Splitting in the link
        // layer would impose one shape on the other, which is what made everything past the reader's first
        // code disappear.
        var reader = Reader();
        var verifier = Verifier(regions: [0]);

        var codes = CodeFrameReader.Read(Clean(OneCode, reader), reader);
        var regions = CharacterFrameReader.Read(Clean("LOT26A", verifier), verifier);

        Assert.Equal("2132030090A0S6X10703", Assert.Single(codes).Data);
        Assert.Equal("LOT26A", Assert.Single(regions).Text);
    }
}
