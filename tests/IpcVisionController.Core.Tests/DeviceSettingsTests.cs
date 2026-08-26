using IpcVisionController.Core.Hal;
using Xunit;

namespace IpcVisionController.Core.Tests;

/// <summary>
/// 實機設定的驗證與載入 / Validation and loading of the real-device settings.
///
/// 這些檢查存在的理由 / Why these checks exist:
/// 設定錯誤在現場的表徵是「連不上」或「每張標籤都判退」,兩者都會被當成硬體問題,
/// 然後有人去拆機台、換線材、重調焦距 —— 而問題其實在一個檔案裡。
/// 因此凡是能在啟動時看出來的錯誤,就必須在啟動時擋下並說清楚是哪個欄位、哪一台裝置。
/// A misconfiguration presents on the line as "cannot connect" or "every label rejects", both of
/// which read as a hardware problem — and then someone strips the machine, swaps cables and
/// re-focuses the sensor, while the fault sits in a file. So anything knowable at startup is
/// refused at startup, naming both the field and the device.
/// </summary>
public sealed class DeviceSettingsTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static SrX300Options ValidReader() => new()
    {
        Host = "192.168.1.10",
        Port = 9004,
        TriggerCommand = "LON",
        CodeField = 0,
        GradeField = 1,
    };

    private static Iv4Options ValidVerifier() => new()
    {
        Host = "192.168.1.11",
        Port = 8500,
        TriggerCommand = "T1",
        CharacterTextFields = [0, 1],
    };

    // ── 連線層的共用檢查 / Checks shared by both devices ─────────────────────

    [Fact]
    public void Validate_WithAUsableReader_Passes() => ValidReader().Validate();

    [Fact]
    public void Validate_WithAUsableVerifier_Passes() => ValidVerifier().Validate();

    [Fact]
    public void Validate_WithNoHost_IsRejected()
    {
        var reader = ValidReader();
        reader.Host = "   ";

        Assert.Throws<ArgumentException>(reader.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70_000)]
    public void Validate_WithAPortOutOfRange_IsRejected(int port)
    {
        var reader = ValidReader();
        reader.Port = port;

        Assert.Throws<ArgumentException>(reader.Validate);
    }

    [Fact]
    public void Validate_WithNoTriggerCommand_IsRejected()
    {
        // 命令刻意沒有預設值:給一個看似合理的猜測,只會讓錯的命令悄悄沿用,
        // 而命令錯誤的表徵是「裝置完全不回應」—— 與網路不通分不出來。
        // The command deliberately has no default: a plausible guess would let a wrong one persist
        // quietly, and a wrong command presents as "the device never answers", which is
        // indistinguishable from a dead link.
        var reader = ValidReader();
        reader.TriggerCommand = string.Empty;

        Assert.Throws<ArgumentException>(reader.Validate);
    }

    [Fact]
    public void Validate_WithANonPositiveTimeout_IsRejected()
    {
        var reader = ValidReader();
        reader.ResponseTimeoutMs = 0;

        Assert.Throws<ArgumentException>(reader.Validate);
    }

    [Theory]
    [InlineData(FrameTerminator.Cr, "\r")]
    [InlineData(FrameTerminator.Lf, "\n")]
    [InlineData(FrameTerminator.CrLf, "\r\n")]
    public void TerminatorText_MatchesTheChosenTerminator(FrameTerminator terminator, string expected)
    {
        var reader = ValidReader();
        reader.Terminator = terminator;

        Assert.Equal(expected, reader.TerminatorText);
    }

    [Fact]
    public void Describe_NamesTheDeviceAndEveryPositionInForce()
    {
        // 這行字是操作員唯一能拿來與原始電文對照的東西,而兩台裝置的電文長得很像 ——
        // 少了型號或位址就無法歸屬,少印一個位置則那個位置錯掉就查不出來。
        // This line is the only thing an operator can hold against a raw frame, and the two devices'
        // frames look alike: without the model and address it cannot be attributed, and an omitted
        // position makes a wrong one undiagnosable.
        var description = ValidReader().Describe();

        Assert.Contains("SR-X300", description, StringComparison.Ordinal);
        Assert.Contains("192.168.1.10:9004", description, StringComparison.Ordinal);
        Assert.Contains("LON", description, StringComparison.Ordinal);
        Assert.Contains("code at 0", description, StringComparison.Ordinal);
        Assert.Contains("grade at 1", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_SaysWhenNoGradeIsRead()
    {
        // 「不讀等級」與「等級位置填錯」在判定上都會讓等級檢查失效,但只有前者是意圖。
        // 摘要必須說得出是哪一種,否則現場會以為等級有在檢查。
        // Not reading a grade and pointing at the wrong position both disable the grade check, but only
        // the first is intended. The summary has to say which, or the line believes grades are checked.
        var reader = ValidReader();
        reader.GradeField = null;

        Assert.Contains("grade at off", reader.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceName_DistinguishesTheTwoModels()
    {
        // 兩台裝置的訊息會交錯出現在同一個操作訊息區,型號是唯一的區分方式
        // The two devices' messages interleave in one log pane, and the model is the only thing
        // that tells them apart.
        Assert.Contains("SR-X300", ValidReader().DeviceName, StringComparison.Ordinal);
        Assert.Contains("IV4", ValidVerifier().DeviceName, StringComparison.Ordinal);
    }

    // ── 讀碼器專屬 / Reader-specific ─────────────────────────────────────────

    [Fact]
    public void Validate_WithNoRecordDelimiter_IsRejected()
    {
        // 少了記錄分隔符,一次讀到六筆條碼會被當成一整串無法解讀的內容
        // Without it, six codes in one read arrive as a single unintelligible string.
        var reader = ValidReader();
        reader.RecordDelimiter = string.Empty;

        Assert.Throws<ArgumentException>(reader.Validate);
    }

    [Fact]
    public void Validate_WhenBothDelimitersAreTheSame_IsRejected()
    {
        // 兩層會塌成一層:碼與等級被當成兩筆條碼,筆數變成兩倍,而等級整批消失。
        // 這是實機格式最容易填錯的一項,因為兩個分隔符在設定軟體裡是分開的兩欄。
        // The two levels collapse: a code and its grade become two codes, the count doubles and every
        // grade disappears. It is the easiest thing to get wrong, because the reader's setup software
        // holds the two delimiters in two separate fields.
        var reader = ValidReader();
        reader.RecordDelimiter = ",";
        reader.FieldDelimiter = ",";

        Assert.Throws<ArgumentException>(reader.Validate);
    }

    [Fact]
    public void Validate_WhenTheCodeAndGradeSharePosition_IsRejected()
    {
        // 其中一個必然填錯了。真接上去的話,等級會從條碼文字解析,而長度檢查讀的是同一格
        // —— 兩個檢查一起壞掉,卻不會有任何錯誤訊息。
        // One of them is certainly wrong. Left in place, the grade parses from the code text while the
        // length check reads the same field: two checks broken at once, with no error anywhere.
        var reader = ValidReader();
        reader.CodeField = 0;
        reader.GradeField = 0;

        Assert.Throws<ArgumentException>(reader.Validate);
    }

    [Fact]
    public void Validate_WithANegativePosition_IsRejected()
    {
        var reader = ValidReader();
        reader.CodeField = -1;

        Assert.Throws<ArgumentException>(reader.Validate);
    }

    [Fact]
    public void Validate_WithNoGradeField_Passes()
    {
        // 不讀等級是合法的組態 —— 讀碼器可以完全關掉附加數據
        // Reading no grade is a legal configuration: appended data can be switched off entirely.
        var reader = ValidReader();
        reader.GradeField = null;

        reader.Validate();
    }

    // ── 字符檢測器專屬 / Verifier-specific ───────────────────────────────────

    [Fact]
    public void Validate_WithNoCharacterFields_IsRejected()
    {
        // 接上了卻沒有指定區域欄位:配方要求區域數就每張判退,不要求就等於整台白接。
        // 真的還沒有 IV4,做法是整段移除 CharacterVerifier,而不是留一段空的。
        // Attached with no region mapped: every label rejects if the recipe asks for regions, and
        // the device is wired up for nothing if it does not. The way to say "no IV4 yet" is to omit
        // the section, not to leave an empty one.
        var verifier = ValidVerifier();
        verifier.CharacterTextFields = [];

        Assert.Throws<ArgumentException>(verifier.Validate);
    }

    // ── 整份設定 / The file as a whole ───────────────────────────────────────

    [Fact]
    public void Validate_WithNoCodeReaderSection_IsRejected()
    {
        // 配方的條碼筆數下限為 1,沒有讀碼器就沒有一種可執行的組態
        // The recipe's code count has a floor of one, so a run without a reader is not a runnable
        // configuration at all.
        var settings = new DeviceSettings { CharacterVerifier = ValidVerifier() };

        Assert.Throws<ArgumentException>(settings.Validate);
    }

    [Fact]
    public void Validate_WithNoVerifierSection_Passes()
    {
        // 這正是目前現場的狀態：只有 SR-X300,IV4 之後才上
        // This is exactly the line's present state: an SR-X300 only, with the IV4 still to come.
        var settings = new DeviceSettings { CodeReader = ValidReader() };

        settings.Validate();
    }

    [Fact]
    public void Validate_WhenBothDevicesShareOneAddressAndPort_IsRejected()
    {
        // 幾乎一定是複製設定時忘了改。真接上去的話兩條連線會搶同一台裝置,
        // 而多數工業裝置只接受一條 —— 於是其中一台在初始化時被拒絕,
        // 錯誤訊息會指向輸掉競爭的那一台,而不是設定錯誤本身。
        // Almost always an unedited copy. Attached for real, two links contend for one device and
        // most industrial devices accept only one, so one is refused at initialise and the error
        // names whichever lost the race rather than the misconfiguration.
        var verifier = ValidVerifier();
        verifier.Host = "192.168.1.10";
        verifier.Port = 9004;

        var settings = new DeviceSettings { CodeReader = ValidReader(), CharacterVerifier = verifier };

        Assert.Throws<ArgumentException>(settings.Validate);
    }

    [Fact]
    public async Task LoadAsync_WhenTheFileIsAbsent_ReturnsNull()
    {
        var missing = Path.Combine(_workspace.Root, "device.json");

        Assert.Null(await DeviceSettings.LoadAsync(missing));
    }

    [Fact]
    public async Task LoadAsync_ReadsCommentsAndEnumNames()
    {
        // 現場會手改這個檔,註解與列舉名稱是它唯一的自我說明
        // The line hand-edits this file, and comments plus enum names are its only documentation.
        var path = Path.Combine(_workspace.Root, "device.json");
        await File.WriteAllTextAsync(path, """
            {
              // 讀碼器 / the reader
              "CodeReader": {
                "Host": "192.168.1.10",
                "Port": 9004,
                "TriggerCommand": "LON",
                "Terminator": "CrLf",
                "CodeField": 0,
              },
            }
            """);

        var settings = await DeviceSettings.LoadAsync(path);

        Assert.NotNull(settings);
        Assert.NotNull(settings.CodeReader);
        Assert.Equal(FrameTerminator.CrLf, settings.CodeReader.Terminator);
        Assert.Null(settings.CharacterVerifier);
    }

    [Fact]
    public async Task LoadAsync_CarriesTheStationOffset()
    {
        // 這個值決定追溯紀錄把哪一張的條碼配哪一張的字符。它必須來自設定檔 ——
        // 留成預設值 0 的話,相隔數格的機構會安靜地產出「兩半不屬於同一張標籤」的紀錄,
        // 而那不會報錯、良率也正常。
        // This value decides which label's code is paired with which label's characters. It has to come
        // from the file: left at the default of zero, a mechanism with stations several pitches apart
        // quietly produces records whose two halves belong to different labels, without erroring and
        // with a normal-looking yield.
        var path = Path.Combine(_workspace.Root, "device.json");
        await File.WriteAllTextAsync(path, """
            {
              "InspectionOffsetPitches": 4,
              "CodeReader": {
                "Host": "192.168.1.10",
                "Port": 9004,
                "TriggerCommand": "LON",
                "CodeField": 0
              }
            }
            """);

        var settings = await DeviceSettings.LoadAsync(path);

        Assert.NotNull(settings);
        Assert.Equal(4, settings.InspectionOffsetPitches);
    }

    [Fact]
    public async Task LoadAsync_WithNoStationOffset_DefaultsToBothSensorsOnOneLabel()
    {
        // 省略時視為 0。這是唯一安全的預設:0 的行為與「兩台瞄同一位置」完全一致,
        // 而猜一個正數會讓沒有相隔的機構前幾張標籤憑空消失。
        // Omitted means zero, the only safe default: it behaves exactly as both sensors sharing one
        // position, whereas guessing a positive value would make the first few labels of a machine with
        // no offset vanish for no reason.
        var path = Path.Combine(_workspace.Root, "device.json");
        await File.WriteAllTextAsync(path, """
            {
              "CodeReader": {
                "Host": "192.168.1.10",
                "Port": 9004,
                "TriggerCommand": "LON",
                "CodeField": 0
              }
            }
            """);

        var settings = await DeviceSettings.LoadAsync(path);

        Assert.NotNull(settings);
        Assert.Equal(0, settings.InspectionOffsetPitches);
    }

    [Fact]
    public async Task LoadAsync_WithAnUnusableFile_ThrowsRatherThanRunningOnDefaults()
    {
        var path = Path.Combine(_workspace.Root, "device.json");
        await File.WriteAllTextAsync(path, "{ not json");

        await Assert.ThrowsAnyAsync<Exception>(() => DeviceSettings.LoadAsync(path));
    }

    [Fact]
    public async Task LoadAsync_WithTheOldFlatShape_SaysTheFormatChanged()
    {
        // 舊版把 Host/Port 直接放在最外層。反序列化不會失敗,只會得到兩段都是 null,
        // 而預設訊息會是「必須包含 CodeReader」—— 現場手上明明有一個看起來填好的檔案,
        // 排查方向會完全錯掉。必須明說格式變了。
        // The old file put Host and Port at the top level. Deserialising it does not fail; it yields
        // both sections null and a "must contain a CodeReader" message, while the line holds a file
        // that looks filled in — sending the diagnosis the wrong way. Name the change.
        var path = Path.Combine(_workspace.Root, "device.json");
        await File.WriteAllTextAsync(path, """
            { "Host": "192.168.1.10", "Port": 8500, "CodeField": 0 }
            """);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => DeviceSettings.LoadAsync(path));

        Assert.Contains("device.sample.json", error.Message, StringComparison.Ordinal);
    }
}
