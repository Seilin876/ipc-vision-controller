using IpcVisionController.Core.Models;
using Microsoft.Data.Sqlite;

namespace IpcVisionController.Core.Data;

/// <summary>
/// SQLite 檢測紀錄儲存層 / SQLite persistence for inspection logs.
///
/// 設計要點 / Design notes:
/// - ID 由資料庫的 INTEGER PRIMARY KEY AUTOINCREMENT 嚴格維護（刪除列後不重用），
///   但不透過任何讀取 API 外洩給 UI。
///   The ID is maintained strictly by SQLite's AUTOINCREMENT (never reused after a
///   delete) and is not exposed through any read API consumed by the UI.
/// - 一律使用參數化查詢，杜絕字串拼接的注入風險（條碼是外部輸入！）。
///   All statements are parameterised — the barcode is untrusted external input.
/// - SQLite 僅允許單一寫入者，因此以 SemaphoreSlim 序列化寫入。
///   SQLite allows a single writer, so writes are serialised with a SemaphoreSlim.
/// </summary>
public sealed class DatabaseManager : IInspectionStore, IAsyncDisposable
{
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

            // WAL：讀寫互不阻塞，產線連續打點時比較穩
            // WAL keeps readers and the writer from blocking each other during continuous logging.
            await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA synchronous = NORMAL;", cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS InspectionLogs (
                    ID          INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp   DATETIME NOT NULL,
                    ModelName   TEXT     NOT NULL,
                    BarcodeData TEXT     NULL,
                    IV4_Result  TEXT     NULL,
                    FinalJudge  TEXT     NOT NULL
                );
                """, cancellationToken).ConfigureAwait(false);

            // 依時間查詢是最常見的追溯需求 / Traceability queries are almost always by time.
            await ExecuteAsync(connection, """
                CREATE INDEX IF NOT EXISTS IX_InspectionLogs_Timestamp
                    ON InspectionLogs (Timestamp DESC);
                """, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// 寫入一筆檢測紀錄 / Insert one inspection record.
    /// </summary>
    /// <returns>
    /// 資料庫產生的 ID，僅供追溯記錄，請勿顯示於 UI /
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
                INSERT INTO InspectionLogs (Timestamp, ModelName, BarcodeData, IV4_Result, FinalJudge)
                VALUES ($timestamp, $modelName, $barcodeData, $iv4Result, $finalJudge);
                SELECT last_insert_rowid();
                """;

            // 參數化：條碼可能含 ' 或 ; 等字元，絕不字串拼接
            // Parameterised: a barcode may contain quotes or semicolons — never concatenate.
            command.Parameters.AddWithValue("$timestamp", record.Timestamp);
            command.Parameters.AddWithValue("$modelName", record.ModelName);
            command.Parameters.AddWithValue("$barcodeData", (object?)record.BarcodeData ?? DBNull.Value);
            command.Parameters.AddWithValue("$iv4Result", (object?)record.Iv4Result ?? DBNull.Value);
            command.Parameters.AddWithValue("$finalJudge", record.FinalJudge);

            var scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture);
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
            SELECT Timestamp, ModelName, BarcodeData, IV4_Result, FinalJudge
            FROM InspectionLogs
            ORDER BY ID DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<InspectionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new InspectionRecord(
                // SQLite 不保存時區,讀回來預設是 Unspecified。明確標回 UTC,
                // 才不會讓下游的 ToLocalTime() 依賴「Unspecified 視為 UTC」這種隱性規則。
                // SQLite stores no time zone, so the value comes back as Unspecified.
                // Stamping it as UTC keeps downstream ToLocalTime() from relying on the
                // implicit "treat Unspecified as UTC" rule.
                Timestamp: DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                ModelName: reader.GetString(1),
                BarcodeData: reader.IsDBNull(2) ? null : reader.GetString(2),
                Iv4Result: reader.IsDBNull(3) ? null : reader.GetString(3),
                FinalJudge: reader.GetString(4)));
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

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        // 連線池由 Microsoft.Data.Sqlite 管理，此處僅清空以釋放檔案句柄
        // The pool is owned by Microsoft.Data.Sqlite; clear it to release file handles.
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        return ValueTask.CompletedTask;
    }
}
