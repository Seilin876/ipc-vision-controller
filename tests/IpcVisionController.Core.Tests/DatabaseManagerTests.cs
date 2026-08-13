using IpcVisionController.Core.Data;
using IpcVisionController.Core.Models;
using Xunit;

namespace IpcVisionController.Core.Tests;

/// <summary>
/// 追溯資料庫的行為規格 / Behavioural spec for the traceability database.
/// 重點在三件事：ID 不外洩、條碼是不可信輸入、查詢順序要能追溯。
/// Three things matter: the ID never leaks, the barcode is untrusted input, and the
/// query order is usable for traceability.
/// </summary>
public sealed class DatabaseManagerTests : IDisposable
{
    /// <summary>固定時間戳,避免測試依賴當下時鐘 / A fixed stamp keeps tests off the wall clock.</summary>
    private static readonly DateTime BaseTime = new(2026, 7, 30, 6, 0, 0, DateTimeKind.Utc);

    private readonly TempWorkspace _workspace = new();

    private async Task<DatabaseManager> NewDatabaseAsync(string fileName = "inspection.db")
    {
        var database = new DatabaseManager(_workspace.PathTo(fileName));
        await database.InitializeAsync();
        return database;
    }

    private static InspectionRecord Record(
        string judge,
        string? barcode = "ABC123456789",
        string? vision = VisionResult.Ok,
        int minuteOffset = 0)
        => new(
            Timestamp: BaseTime.AddMinutes(minuteOffset),
            ModelName: "MODEL-A",
            BarcodeData: barcode,
            Iv4Result: vision,
            FinalJudge: judge);

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        await using var database = await NewDatabaseAsync();

        // 每次啟動都會呼叫一次,重複執行不能爆
        // Called on every start-up, so repeated calls must be harmless.
        await database.InitializeAsync();
        await database.InitializeAsync();

        Assert.Equal((0, 0), await database.GetTallyAsync());
    }

    [Fact]
    public async Task InsertAsync_AssignsStrictlyIncreasingIds()
    {
        await using var database = await NewDatabaseAsync();

        var first = await database.InsertAsync(Record(Verdict.Pass));
        var second = await database.InsertAsync(Record(Verdict.Pass));

        Assert.True(second > first, $"expected {second} > {first}");
    }

    [Fact]
    public async Task GetRecentAsync_ReturnsNewestFirst()
    {
        await using var database = await NewDatabaseAsync();
        await database.InsertAsync(Record(Verdict.Pass, barcode: "OLDEST00001A", minuteOffset: 0));
        await database.InsertAsync(Record(Verdict.Pass, barcode: "MIDDLE00002B", minuteOffset: 1));
        await database.InsertAsync(Record(Verdict.Pass, barcode: "NEWEST00003C", minuteOffset: 2));

        var recent = await database.GetRecentAsync();

        Assert.Equal(
            new[] { "NEWEST00003C", "MIDDLE00002B", "OLDEST00001A" },
            recent.Select(r => r.BarcodeData).ToArray());
    }

    [Fact]
    public async Task GetRecentAsync_RespectsLimit()
    {
        await using var database = await NewDatabaseAsync();
        for (var i = 0; i < 5; i++)
        {
            await database.InsertAsync(Record(Verdict.Pass, minuteOffset: i));
        }

        var recent = await database.GetRecentAsync(limit: 2);

        Assert.Equal(2, recent.Count);
    }

    [Fact]
    public async Task GetRecentAsync_WithNonPositiveLimit_Throws()
    {
        await using var database = await NewDatabaseAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => database.GetRecentAsync(limit: 0));
    }

    [Fact]
    public async Task InsertAsync_WithNoReadPart_RoundTripsNulls()
    {
        await using var database = await NewDatabaseAsync();
        await database.InsertAsync(Record(Verdict.Fail, barcode: null, vision: null));

        var stored = Assert.Single(await database.GetRecentAsync());

        Assert.Null(stored.BarcodeData);
        Assert.Null(stored.Iv4Result);
        Assert.Equal(Verdict.Fail, stored.FinalJudge);
    }

    [Fact]
    public async Task InsertAsync_WithSqlPayloadInBarcode_StoresItLiterally()
    {
        // 條碼是外部輸入。掃到這種字串時必須原樣存下,而不是執行它。
        // The barcode is external input. Such a string must be stored verbatim, not executed.
        const string Payload = "'); DROP TABLE InspectionLogs; --";

        await using var database = await NewDatabaseAsync();
        await database.InsertAsync(Record(Verdict.Fail, barcode: Payload));

        var stored = Assert.Single(await database.GetRecentAsync());
        Assert.Equal(Payload, stored.BarcodeData);

        // 資料表仍在,才證明沒有被注入 / The table still answering proves nothing was injected.
        Assert.Equal((0, 1), await database.GetTallyAsync());
    }

    [Fact]
    public async Task InsertAsync_PreservesTheUtcTimestamp()
    {
        await using var database = await NewDatabaseAsync();
        var expected = BaseTime.AddMilliseconds(123);
        await database.InsertAsync(Record(Verdict.Pass) with { Timestamp = expected });

        var stored = Assert.Single(await database.GetRecentAsync());

        Assert.Equal(expected, stored.Timestamp);
        // 時區資訊必須明確,否則畫面上的當地時間會偏移
        // The kind must be explicit, or the local time shown on screen drifts.
        Assert.Equal(DateTimeKind.Utc, stored.Timestamp.Kind);
    }

    [Fact]
    public async Task GetTallyAsync_CountsPassSeparatelyFromEverythingElse()
    {
        await using var database = await NewDatabaseAsync();
        await database.InsertAsync(Record(Verdict.Pass));
        await database.InsertAsync(Record(Verdict.Pass));
        await database.InsertAsync(Record(Verdict.Fail));

        Assert.Equal((2, 1), await database.GetTallyAsync());
    }

    [Fact]
    public async Task GetTallyAsync_OnEmptyTable_ReturnsZeros()
    {
        await using var database = await NewDatabaseAsync();

        // SUM 在空表回傳 NULL,未處理就會炸在良率看板上
        // SUM returns NULL over an empty table; unhandled, that blows up the yield readout.
        Assert.Equal((0, 0), await database.GetTallyAsync());
    }

    [Fact]
    public async Task Records_SurviveReopeningTheFile()
    {
        var path = _workspace.PathTo("persisted.db");

        await using (var first = new DatabaseManager(path))
        {
            await first.InitializeAsync();
            await first.InsertAsync(Record(Verdict.Pass));
        }

        await using var second = new DatabaseManager(path);
        await second.InitializeAsync();

        Assert.Equal((1, 0), await second.GetTallyAsync());
    }

    [Fact]
    public async Task InsertAsync_ToleratesConcurrentCallers()
    {
        // SQLite 只允許單一寫入者。UI 與產線迴圈同時寫時不得出現 "database is locked"。
        // SQLite allows a single writer; the UI and the line loop writing at once must
        // not produce "database is locked".
        await using var database = await NewDatabaseAsync();

        var writes = Enumerable.Range(0, 25)
            .Select(i => database.InsertAsync(Record(Verdict.Pass, minuteOffset: i)));
        await Task.WhenAll(writes);

        Assert.Equal((25, 0), await database.GetTallyAsync());
    }

    [Fact]
    public void Constructor_WithBlankPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => new DatabaseManager("  "));
    }

    public void Dispose() => _workspace.Dispose();
}
