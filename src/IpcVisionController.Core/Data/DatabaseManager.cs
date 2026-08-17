using System.Globalization;
using System.Text.Json;
using IpcVisionController.Core.Models;
using Microsoft.Data.Sqlite;

namespace IpcVisionController.Core.Data;

/// <summary>
/// SQLite 檢測紀錄儲存層 / SQLite persistence for inspection logs.
///
/// 設計要點 / Design notes:
/// - ID 由資料庫的 INTEGER PRIMARY KEY AUTOINCREMENT 嚴格維護（刪除列後不重用）,
///   但不透過任何讀取 API 外洩給 UI。
///   The ID is maintained strictly by SQLite's AUTOINCREMENT (never reused after a
///   delete) and is not exposed through any read API consumed by the UI.
/// - 一律使用參數化查詢,杜絕字串拼接的注入風險（條碼是外部輸入！）。
///   All statements are parameterised — the barcode is untrusted external input.
/// - SQLite 僅允許單一寫入者,因此以 SemaphoreSlim 序列化寫入。
///   SQLite allows a single writer, so writes are serialised with a SemaphoreSlim.
///
/// 一次檢測有多筆條碼與多個字符區域,存成 JSON 欄位而非子資料表 /
/// One inspection carries many codes and many character regions, stored as JSON columns
/// rather than child tables:
/// 一筆紀錄仍然是一列,寫入是單一 INSERT,不需要交易與外鍵串接,
/// 讀回時也不必為了拼回一筆紀錄而做第二次查詢。
/// A record stays one row, the write stays a single INSERT with no transaction or
/// foreign-key cascade to get right, and reading back needs no second query to
/// reassemble one record.
/// 代價是「哪些標籤帶有條碼 X」不再是單純的 WHERE —— 但 SQLite 內建 JSON1,
/// 這類追溯查詢仍可用 json_each 展開,例如：
/// The cost is that "which labels carried code X" is no longer a plain WHERE. SQLite
/// ships JSON1 though, so such a traceability query is still expressible with json_each:
///   SELECT L.Timestamp FROM InspectionLogs AS L, json_each(L.CodeResults) AS C
///   WHERE json_extract(C.value, '$.Data') = 'ABC123456789';
/// 若日後這類查詢變成日常操作而非偶爾追查,再改為子資料表並不遲。
/// If those queries ever become routine rather than occasional, moving to child tables
/// is still open.
/// </summary>
public sealed class DatabaseManager : IInspectionStore, IAsyncDisposable
{
    /// <summary>
    /// 結構版本 / Schema version, tracked in PRAGMA user_version.
    ///
    /// 為什麼需要 / Why this exists:
    /// 資料表名稱沒變但欄位換了,CREATE TABLE IF NOT EXISTS 遇到舊檔會「成功」卻什麼也沒建,
    /// 接著每一次 INSERT 都炸在缺欄位上 —— 或更糟,讀到對不上的資料。
    /// 寧可在開檔時就明確拒絕,也不要讓舊檔靜默地半動不動。
    /// The table name did not change but its columns did, so CREATE TABLE IF NOT EXISTS
    /// silently succeeds against an old file without creating anything — and then every
    /// INSERT fails on a missing column, or worse, a read returns data that does not line
    /// up. Refusing the file outright at open time beats limping along.
    /// </summary>
    private const long SchemaVersion = 2;

    /// <summary>
    /// JSON 欄位的序列化設定 / Serialisation settings for the JSON columns.
    /// 不縮排：這是機器讀的欄位,縮排只會讓每一列白白變大。
    /// Not indented: these columns are machine-read, and indentation only inflates rows.
    /// </summary>
    private static readonly JsonSerializerOptions ResultJsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public DatabaseManager(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("資料庫路徑不可為空 / Database path must not be empty.", nameof(databasePath));
        }

        DatabasePath = Path.GetFullPath(databasePath);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
    }

    /// <summary>資料庫檔完整路徑 / Absolute path of the database file.</summary>
    public string DatabasePath { get; }

    /// <summary>
    /// 建立資料表與索引（可重複呼叫）/ Create the table and index; safe to call repeatedly.
    /// </summary>
    /// <exception cref="IncompatibleSchemaException">
    /// 現有檔案是舊版結構 / The existing file carries an older schema.
    /// </exception>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // WAL：讀寫互不阻塞,產線連續打點時比較穩
            // WAL keeps readers and the writer from blocking each other during continuous logging.
            await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA synchronous = NORMAL;", cancellationToken).ConfigureAwait(false);

            await EnsureSchemaVersionAsync(connection, cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS InspectionLogs (
                    ID               INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp        DATETIME NOT NULL,
                    ModelName        TEXT     NOT NULL,
                    FinalJudge       TEXT     NOT NULL,
                    RejectReason     TEXT     NULL,
                    CodeResults      TEXT     NOT NULL,
                    CharacterResults TEXT     NOT NULL
                );
                """, cancellationToken).ConfigureAwait(false);

            // 依時間查詢是最常見的追溯需求 / Traceability queries are almost always by time.
            await ExecuteAsync(connection, """
                CREATE INDEX IF NOT EXISTS IX_InspectionLogs_Timestamp
                    ON InspectionLogs (Timestamp DESC);
                """, cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(
                connection,
                string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {SchemaVersion};"),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// 確認檔案的結構版本相容 / Confirm the file's schema version is one we can use.
    /// 空檔（user_version = 0 且尚無資料表）視為全新檔,由呼叫端接著建表。
    /// A blank file — user_version 0 with no table yet — counts as new, and the caller
    /// goes on to create the schema.
    /// </summary>
    private static async Task EnsureSchemaVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var version = Convert.ToInt64(
            await ScalarAsync(connection, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);

        if (version == SchemaVersion)
        {
            return;
        }

        var hasTable = await ScalarAsync(
            connection,
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'InspectionLogs';",
            cancellationToken).ConfigureAwait(false) is not null;

        if (version == 0 && !hasTable)
        {
            return;
        }

        throw new IncompatibleSchemaException(string.Create(CultureInfo.InvariantCulture,
            $"""
             追溯資料庫結構為第 {version} 版,本程式需要第 {SchemaVersion} 版。
             請將舊檔另存封存後移開,程式會建立新檔：
             The traceability database is at schema version {version}, but this build requires
             version {SchemaVersion}. Archive the old file and move it aside; a new one will be created.
             """));
    }

    /// <summary>
    /// 寫入一筆檢測紀錄 / Insert one inspection record.
    /// </summary>
    /// <returns>
    /// 資料庫產生的 ID,僅供追溯記錄,請勿顯示於 UI /
    /// The database-generated ID, for traceability logging only — do not display it in the UI.
    /// </returns>
    public async Task<long> InsertAsync(InspectionRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            // ID 不出現在欄位清單中：交由 AUTOINCREMENT 決定
            // ID is absent from the column list on purpose: AUTOINCREMENT owns it.
            command.CommandText = """
                INSERT INTO InspectionLogs
                    (Timestamp, ModelName, FinalJudge, RejectReason, CodeResults, CharacterResults)
                VALUES
                    ($timestamp, $modelName, $finalJudge, $rejectReason, $codeResults, $characterResults);
                SELECT last_insert_rowid();
                """;

            // 參數化：條碼可能含 ' 或 ; 等字元,絕不字串拼接。
            // JSON 欄位同樣走參數 —— 序列化過的條碼內容一樣是外部輸入。
            // Parameterised: a barcode may contain quotes or semicolons — never concatenate.
            // The JSON columns are parameters too; serialised code payloads are still
            // untrusted external input.
            command.Parameters.AddWithValue("$timestamp", record.Timestamp);
            command.Parameters.AddWithValue("$modelName", record.ModelName);
            command.Parameters.AddWithValue("$finalJudge", record.FinalJudge);
            command.Parameters.AddWithValue("$rejectReason", (object?)record.RejectReason ?? DBNull.Value);
            command.Parameters.AddWithValue("$codeResults", Serialise(record.CodeResults));
            command.Parameters.AddWithValue("$characterResults", Serialise(record.CharacterResults));

            var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// 取回最近的紀錄供 UI 顯示 / Fetch the most recent records for the UI.
    /// 刻意不 SELECT ID —— UI 永遠看不到主鍵。
    /// Deliberately does not SELECT ID; the UI never sees the primary key.
    /// </summary>
    public async Task<IReadOnlyList<InspectionRecord>> GetRecentAsync(
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit 必須為正整數 / limit must be positive.");
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Timestamp, ModelName, FinalJudge, RejectReason, CodeResults, CharacterResults
            FROM InspectionLogs
            ORDER BY ID DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<InspectionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // SQLite 不保存時區,讀回來預設是 Unspecified。明確標回 UTC,
            // 才不會讓下游的 ToLocalTime() 依賴「Unspecified 視為 UTC」這種隱性規則。
            // SQLite stores no time zone, so the value comes back as Unspecified.
            // Stamping it as UTC keeps downstream ToLocalTime() from relying on the
            // implicit "treat Unspecified as UTC" rule.
            var timestamp = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc);

            results.Add(new InspectionRecord(
                Timestamp: timestamp,
                ModelName: reader.GetString(1),
                FinalJudge: reader.GetString(2),
                CodeResults: Deserialise<CodeResult>(reader.GetString(4), timestamp, nameof(InspectionRecord.CodeResults)),
                CharacterResults: Deserialise<CharacterResult>(reader.GetString(5), timestamp, nameof(InspectionRecord.CharacterResults)),
                RejectReason: reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return results;
    }

    /// <summary>統計 PASS / FAIL 數量 / Count PASS vs FAIL, for the yield readout.</summary>
    public async Task<(int Pass, int Fail)> GetTallyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                SUM(CASE WHEN FinalJudge = $pass THEN 1 ELSE 0 END),
                SUM(CASE WHEN FinalJudge <> $pass THEN 1 ELSE 0 END)
            FROM InspectionLogs;
            """;
        command.Parameters.AddWithValue("$pass", Verdict.Pass);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return (0, 0);
        }

        var pass = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
        var fail = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        return (pass, fail);
    }

    private static string Serialise<T>(IReadOnlyList<T> results)
        => JsonSerializer.Serialize(results, ResultJsonOptions);

    /// <summary>
    /// 解回結果清單 / Read a result list back.
    ///
    /// 解析失敗時明確拋出,而非默默給一份空清單：
    /// 空清單看起來就像「這次什麼都沒讀到」,會把資料損毀偽裝成一筆正常的不良紀錄。
    /// A parse failure throws rather than quietly yielding an empty list: an empty list
    /// is indistinguishable from "this trigger read nothing", which would disguise data
    /// corruption as an ordinary reject.
    /// </summary>
    private static IReadOnlyList<T> Deserialise<T>(string json, DateTime timestamp, string column)
    {
        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, ResultJsonOptions)
                ?? throw new InvalidDataException("JSON 解析結果為 null / deserialised to null.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture,
                $"追溯紀錄 {timestamp:O} 的 {column} 欄位無法解析 / could not parse the {column} column of the record at {timestamp:O}: {ex.Message}"));
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        // 連線池由 Microsoft.Data.Sqlite 管理,此處僅清空以釋放檔案句柄
        // The pool is owned by Microsoft.Data.Sqlite; clear it to release file handles.
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// 現有資料庫檔的結構版本與本程式不符 / The existing database file's schema does not match this build.
/// </summary>
public sealed class IncompatibleSchemaException(string message) : Exception(message);
