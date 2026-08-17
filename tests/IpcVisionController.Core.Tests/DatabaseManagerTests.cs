using IpcVisionController.Core.Data;
using IpcVisionController.Core.Models;
using Microsoft.Data.Sqlite;
using Xunit;

namespace IpcVisionController.Core.Tests;

/// <summary>
/// 追溯資料庫的行為規格 / Behavioural spec for the traceability database.
/// 重點在四件事：ID 不外洩、感測器內容是不可信輸入、一次觸發的多筆結果要完整存回、
/// 以及資料對不上時要明確失敗而非默默給空值。
/// Four things matter: the ID never leaks, sensor payloads are untrusted input, every
/// result from one trigger survives the round trip, and mismatched data fails loudly
/// instead of quietly returning nothing.
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

    private static CodeResult Code(
        int index = 0,
        string? data = "ABC123456789",
        int? grade = null,
        string judge = Verdict.Pass)
        => new(index, data, grade, judge);

    private static CharacterResult Character(
        int index = 0,
        string? text = "LOT-2026",
        string judge = Verdict.Pass)
        => new(index, text, judge);

    private static InspectionRecord Record(
        string judge,
        IReadOnlyList<CodeResult>? codes = null,
        IReadOnlyList<CharacterResult>? characters = null,
        string? rejectReason = null,
        int minuteOffset = 0)
        => new(
            Timestamp: BaseTime.AddMinutes(minuteOffset),
            ModelName: "MODEL-A",
            FinalJudge: judge,
            CodeResults: codes ?? [Code()],
            CharacterResults: characters ?? [Character()],
            RejectReason: rejectReason);

    /// <summary>
    /// 以本測試自己的連線直接下 SQL / Run raw SQL over a connection of the test's own.
    /// 用於偽造舊版檔案與損毀欄位 —— 這兩種狀況無法從正常 API 造出來。
    /// Used to forge an older file and a corrupt column; neither state is reachable
    /// through the normal API.
    /// 不使用連線池,測試結束才能確實釋放檔案句柄。
    /// Pooling is off so the file handle is certainly released by the end of the test.
    /// </summary>
    private static async Task ExecuteRawAsync(string databasePath, params string[] statements)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        foreach (var sql in statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }

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
        await database.InsertAsync(Record(Verdict.Pass, codes: [Code(data: "OLDEST00001A")], minuteOffset: 0));
        await database.InsertAsync(Record(Verdict.Pass, codes: [Code(data: "MIDDLE00002B")], minuteOffset: 1));
        await database.InsertAsync(Record(Verdict.Pass, codes: [Code(data: "NEWEST00003C")], minuteOffset: 2));

        var recent = await database.GetRecentAsync();

        Assert.Equal(
            new[] { "NEWEST00003C", "MIDDLE00002B", "OLDEST00001A" },
            recent.Select(r => r.CodeResults[0].Data).ToArray());
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
    public async Task InsertAsync_RoundTripsEveryResultFromOneTrigger()
    {
        // 一次觸發多筆是本層的核心能力。只留最後一筆等於默默丟掉不良品的證據,
        // 因此這裡逐筆比對全部欄位,而不只確認筆數。
        // Many results per trigger is this layer's core capability. Keeping only the last
        // one would silently discard the evidence for a reject, so this compares every
        // field of every result rather than just the counts.
        await using var database = await NewDatabaseAsync();
        var record = Record(
            Verdict.Fail,
            codes:
            [
                Code(index: 0, data: "AAA111222333", grade: 88),
                Code(index: 1, data: null, grade: null, judge: Verdict.Fail),
                Code(index: 2, data: "CCC333444555", grade: 4),
            ],
            characters:
            [
                Character(index: 0, text: "LOT-2026"),
                Character(index: 1, text: null, judge: Verdict.Fail),
            ],
            rejectReason: "條碼 #1 未解出內容 / code #1 decoded to nothing");

        await database.InsertAsync(record);
        var stored = Assert.Single(await database.GetRecentAsync());

        // record 是 record type,序列比對即為逐欄位比對
        // These are record types, so comparing the sequences compares every field.
        Assert.Equal(record.CodeResults, stored.CodeResults);
        Assert.Equal(record.CharacterResults, stored.CharacterResults);
        Assert.Equal(record.RejectReason, stored.RejectReason);
    }

    [Fact]
    public async Task InsertAsync_OnPass_LeavesTheRejectReasonNull()
    {
        // 合格品帶著判退原因會讓追溯報表自相矛盾
        // A reason attached to a good part makes the traceability report contradict itself.
        await using var database = await NewDatabaseAsync();
        await database.InsertAsync(Record(Verdict.Pass));

        var stored = Assert.Single(await database.GetRecentAsync());

        Assert.Null(stored.RejectReason);
    }

    [Fact]
    public async Task InsertAsync_WithNoReadTrigger_RoundTripsNulls()
    {
        // NOREAD 是最常見的不良情境,null 必須原樣回來而不是變成空字串
        // A NOREAD is the commonest defect; nulls must return as nulls, not empty strings.
        await using var database = await NewDatabaseAsync();
        await database.InsertAsync(Record(
            Verdict.Fail,
            codes: [Code(data: null, grade: null, judge: Verdict.Fail)],
            characters: [Character(text: null, judge: Verdict.Fail)],
            rejectReason: "NOREAD"));

        var stored = Assert.Single(await database.GetRecentAsync());

        Assert.Null(Assert.Single(stored.CodeResults).Data);
        Assert.Null(Assert.Single(stored.CodeResults).Grade);
        Assert.Null(Assert.Single(stored.CharacterResults).Text);
        Assert.Equal(Verdict.Fail, stored.FinalJudge);
    }

    [Theory]
    [InlineData("'); DROP TABLE InspectionLogs; --")]
    [InlineData("""{"Index":0,"Data":"spoofed","Grade":100,"Judge":"PASS"}""")]
    [InlineData("quote\" backslash\\ newline\n tab\t")]
    [InlineData("批號-中文-ＡＢＣ")]
    public async Task InsertAsync_StoresUntrustedCodePayloadsVerbatim(string payload)
    {
        // 條碼內容是外部輸入,而且現在還會經過 JSON 序列化,所以有兩層要顧：
        // SQL 不能被拼接執行,JSON 不能被內容本身破壞結構或偽造出一筆假結果。
        // A code payload is external input and now also passes through JSON, so there are
        // two layers to hold: SQL must not be concatenated and executed, and the payload
        // must not break the JSON structure or forge an extra result.
        await using var database = await NewDatabaseAsync();
        await database.InsertAsync(Record(Verdict.Fail, codes: [Code(data: payload)], rejectReason: "test"));

        var stored = Assert.Single(await database.GetRecentAsync());
        Assert.Equal(payload, Assert.Single(stored.CodeResults).Data);

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
        await database.InsertAsync(Record(Verdict.Fail, rejectReason: "test"));

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
    public async Task InitializeAsync_OnAnOlderSchemaFile_Throws()
    {
        // 資料表名稱沒變但欄位換了,CREATE TABLE IF NOT EXISTS 會「成功」卻什麼也沒建。
        // 若不在開檔時擋下,之後每一筆 INSERT 都會炸在缺欄位上。
        // The table name did not change but its columns did, so CREATE TABLE IF NOT EXISTS
        // succeeds without creating anything. Unless the file is refused at open time,
        // every later INSERT fails on a missing column.
        var path = _workspace.PathTo("legacy.db");
        await ExecuteRawAsync(
            path,
            """
            CREATE TABLE InspectionLogs (
                ID          INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp   DATETIME NOT NULL,
                ModelName   TEXT     NOT NULL,
                BarcodeData TEXT     NULL,
                Iv4Result   TEXT     NULL,
                FinalJudge  TEXT     NOT NULL
            );
            """,
            "PRAGMA user_version = 1;");

        await using var database = new DatabaseManager(path);

        var error = await Assert.ThrowsAsync<IncompatibleSchemaException>(() => database.InitializeAsync());
        // 訊息要能讓現場知道該怎麼辦,而不只是「失敗」
        // The message must tell the line what to do, not merely that something failed.
        Assert.Contains("1", error.Message, StringComparison.Ordinal);
        Assert.Contains("2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_OnAnUnrelatedSqliteFile_Throws()
    {
        // 指到別的 .db（例如打錯路徑）不該被當成新檔而長出一張表
        // Pointing at somebody else's .db — a mistyped path — must not grow a table there.
        var path = _workspace.PathTo("someone-elses.db");
        await ExecuteRawAsync(path, "CREATE TABLE InspectionLogs (Whatever TEXT);");

        await using var database = new DatabaseManager(path);

        await Assert.ThrowsAsync<IncompatibleSchemaException>(() => database.InitializeAsync());
    }

    [Fact]
    public async Task GetRecentAsync_WithACorruptResultColumn_ThrowsInsteadOfReturningNothing()
    {
        // 空清單看起來就像「這次什麼都沒讀到」,會把資料損毀偽裝成一筆正常的不良紀錄。
        // An empty list is indistinguishable from "this trigger read nothing", which would
        // disguise data corruption as an ordinary reject.
        var path = _workspace.PathTo("corrupt.db");
        await using var database = new DatabaseManager(path);
        await database.InitializeAsync();
        await database.InsertAsync(Record(Verdict.Pass));

        await ExecuteRawAsync(path, "UPDATE InspectionLogs SET CodeResults = 'not json';");

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => database.GetRecentAsync());
        // 訊息要指出是哪一欄壞了,否則現場無從判斷損毀範圍
        // The message must name the broken column, or the line cannot scope the damage.
        Assert.Contains(nameof(InspectionRecord.CodeResults), error.Message, StringComparison.Ordinal);
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
