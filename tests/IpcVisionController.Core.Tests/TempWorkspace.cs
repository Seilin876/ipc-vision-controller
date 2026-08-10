namespace IpcVisionController.Core.Tests;

/// <summary>
/// 每個測試獨立的暫存目錄 / A throwaway directory scoped to one test.
///
/// 配方與資料庫都是真的檔案操作,用假的檔案系統反而測不到「原子換名」與
/// 「SQLite WAL」這些真正會出事的地方。因此這裡用真目錄,測完刪掉。
/// Recipes and the database are genuinely file-backed; a fake file system would skip
/// exactly the parts that break in the field (atomic rename, SQLite WAL). So these
/// tests use a real directory and delete it afterwards.
/// </summary>
internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "ipcvc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>暫存目錄完整路徑 / Absolute path of the directory.</summary>
    public string Root { get; }

    /// <summary>取得目錄下的檔案路徑 / Path of a file inside the directory.</summary>
    public string PathTo(string fileName) => Path.Combine(Root, fileName);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // SQLite 的檔案句柄偶爾晚一步釋放；殘留暫存檔不該讓測試變紅。
            // SQLite occasionally releases its handle a beat late; a leftover temp
            // directory must not turn a passing test red.
        }
    }
}
