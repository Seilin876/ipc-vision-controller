using IpcVisionController.Core.Hal;
using Xunit;

namespace IpcVisionController.Core.Tests;

/// <summary>
/// 實機設定的驗證與載入 / Validation and loading of the real-device settings.
///
/// 這些檢查存在的理由 / Why these checks exist:
/// 設定錯誤在現場的表徵是「連不上」或「每張標籤都判退」,兩者都會被當成硬體問題,
/// 然後有人去拆機台、換線材、重調焦距 —— 而問題其實在一個檔案裡。
/// 因此凡是能在啟動時看出來的錯誤,就必須在啟動時擋下並說清楚是哪個欄位。
/// A misconfiguration presents on the line as "cannot connect" or "every label rejects", both
/// of which read as a hardware problem — and then someone strips the machine, swaps cables and
/// re-focuses the sensor, while the fault sits in a file. So anything knowable at startup is
/// refused at startup, naming the field.
/// </summary>
public sealed class Iv4OptionsTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private static Iv4Options Valid() => new()
    {
        Host = "192.168.0.10",
        CodeDataFields = [1],
        CodeGradeFields = [2],
        CharacterTextFields = [3],
    };

    [Fact]
    public void Validate_WithAUsableConfiguration_Passes() => Valid().Validate();

    [Fact]
    public void Validate_WithNoHost_IsRejected()
    {
        var options = Valid();
        options.Host = "   ";

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70_000)]
    public void Validate_WithAPortOutOfRange_IsRejected(int port)
    {
        var options = Valid();
        options.Port = port;

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Validate_WithNoFieldsAtAll_IsRejected()
    {
        // 兩份索引都空的話每次觸發都回空結果,配方的筆數檢查會讓每一張標籤都判退 ——
        // 看起來像整批不良,實際上是設定漏填。
        // With both lists empty every trigger returns nothing and the recipe's count check
        // rejects every label: it looks like a bad batch and is an unfilled setting.
        var options = Valid();
        options.CodeDataFields = [];
        options.CharacterTextFields = [];

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Validate_WithMoreGradeFieldsThanCodeFields_IsRejected()
    {
        // 對應關係錯位,等級會掛到別筆條碼上 —— 判退原因會指向錯的那一筆
        // The pairing is out of step and a grade lands on the wrong code, so the reject reason
        // points at the wrong one.
        var options = Valid();
        options.CodeGradeFields = [2, 4];

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public void Validate_WithANegativeFieldIndex_IsRejected()
    {
        var options = Valid();
        options.CodeDataFields = [-1];

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithANonPositiveTimeout_IsRejected(int timeout)
    {
        // 逾時為 0 或負數會讓每次觸發立刻「逾時」,現場看到的是感測器好像壞了
        // A zero or negative timeout makes every trigger fail instantly, which reads on the
        // line as a broken sensor.
        var options = Valid();
        options.ResponseTimeoutMs = timeout;

        Assert.Throws<ArgumentException>(() => options.Validate());
    }

    [Fact]
    public async Task LoadAsync_WhenTheFileIsAbsent_ReturnsNullSoTheAppFallsBackToMocks()
    {
        // 檔案不存在是有意義的狀態而非錯誤:那代表這台機器要跑模擬裝置
        // A missing file is a meaningful state, not an error: this machine runs on mocks.
        Assert.Null(await Iv4Options.LoadAsync(_workspace.PathTo("device.json")));
    }

    [Fact]
    public async Task LoadAsync_ReadsCommentsAndEnumNames()
    {
        var path = _workspace.PathTo("device.json");
        await File.WriteAllTextAsync(path, """
            {
              // 現場手寫的註解必須讀得過 / Hand-written comments must survive.
              "Host": "192.168.0.20",
              "Port": 8500,
              "Terminator": "CrLf",
              "CodeDataFields": [1],
              "CodeGradeFields": [2],
              "CharacterTextFields": [3],
            }
            """);

        var options = await Iv4Options.LoadAsync(path);

        Assert.NotNull(options);
        Assert.Equal("192.168.0.20", options!.Host);
        Assert.Equal(Iv4Terminator.CrLf, options.Terminator);
        Assert.Equal("\r\n", options.TerminatorText);
    }

    [Fact]
    public async Task LoadAsync_WithAnUnusableFile_ThrowsRatherThanFallingBackToMocks()
    {
        // 「以為接了實機、其實跑模擬」比「開不起來」危險得多:模擬資料會被當成實機結果簽核
        // Believing the real sensor is attached while running on mocks is far worse than not
        // starting: mock data would be signed off as a real result.
        var path = _workspace.PathTo("device.json");
        await File.WriteAllTextAsync(path, """{ "Host": "", "CodeDataFields": [1] }""");

        await Assert.ThrowsAsync<ArgumentException>(() => Iv4Options.LoadAsync(path));
    }

    [Theory]
    [InlineData(Iv4Terminator.Cr, "\r")]
    [InlineData(Iv4Terminator.Lf, "\n")]
    [InlineData(Iv4Terminator.CrLf, "\r\n")]
    public void TerminatorText_MatchesTheChosenTerminator(Iv4Terminator terminator, string expected)
    {
        var options = Valid();
        options.Terminator = terminator;

        Assert.Equal(expected, options.TerminatorText);
    }

    /// <summary>
    /// 摘要必須帶著三組索引 / The summary has to carry all three index lists.
    /// 這行字是操作員唯一能拿來與原始電文對照的東西。少印一組索引,那一組錯掉就查不出來 ——
    /// 表徵仍是「每張標籤都判退」,而現場會以為設定已經核對過了。
    /// This line is the only thing an operator can hold against a raw frame. Omit one list and a
    /// wrong index in it is undiagnosable: it still presents as "every label rejects", except now
    /// the line believes the configuration has already been checked.
    /// </summary>
    [Fact]
    public void Describe_CarriesTheAddressAndEveryFieldList()
    {
        var options = Valid();
        options.Port = 8501;
        options.CodeDataFields = [1, 3];
        options.CodeGradeFields = [2, 4];
        options.CharacterTextFields = [5, 6];

        var description = options.Describe();

        Assert.Contains("192.168.0.10:8501", description, StringComparison.Ordinal);
        Assert.Contains("1 3", description, StringComparison.Ordinal);
        Assert.Contains("2 4", description, StringComparison.Ordinal);
        Assert.Contains("5 6", description, StringComparison.Ordinal);
    }
}
