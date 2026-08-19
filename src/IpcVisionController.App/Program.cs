using IpcVisionController.Core.Data;
using IpcVisionController.Core.Hal;
using IpcVisionController.Core.Machine;
using IpcVisionController.Core.Recipes;

namespace IpcVisionController.App;

/// <summary>
/// 應用程式入口與組裝根 / Entry point and composition root.
///
/// 這裡是全程式唯一決定「用模擬還是實機」的地方,而且改由設定檔決定而非改程式:
/// device.json 存在就接實機 IV4,不存在就跑模擬裝置。
/// 為什麼不是編譯期開關 —— 現場沒有 SDK,也不該為了切換而重新發佈;
/// 更重要的是「這台機器現在跑的是模擬還是實機」必須能當場看出來,
/// 把模擬資料誤當實機結果簽核,是這支程式最不能發生的事。
/// This is the only place that decides mock versus real hardware, and the decision now comes
/// from a file rather than from an edit: device.json present means the real IV4, absent means
/// mocks. Not a compile-time switch, because the line has no SDK and must not need a redeploy
/// to swap — and more importantly because which one is running has to be visible on the spot.
/// Signing off mock data as a real result is the worst thing this program could allow.
/// </summary>
internal static class Program
{
    /// <summary>資料庫檔名 / Database file name.</summary>
    private const string DatabaseFileName = "inspection.db";

    /// <summary>現場配方檔名（非版控）/ Live recipe file name (not versioned).</summary>
    private const string RecipeFileName = "recipe.json";

    /// <summary>實機裝置設定檔名（非版控）/ Real-device settings file name (not versioned).</summary>
    private const string DeviceFileName = "device.json";

    /// <summary>
    /// 裝置設定檔的完整路徑 / The device settings file's full path.
    ///
    /// 位置是「exe 旁邊」而非原始碼資料夾。csproj 只複製 device.sample.json,
    /// 所以放在原始碼資料夾的 device.json 永遠不會抵達輸出目錄,程式會安靜地跑模擬。
    /// 由此處單一提供給畫面顯示,現場才看得到程式究竟找了哪個路徑。
    /// The location is next to the exe, not the source folder: the csproj copies only
    /// device.sample.json, so a device.json left in the source folder never reaches the
    /// output directory and the program quietly runs on mocks. Exposed from here so the UI
    /// can show the line exactly which path was searched.
    /// </summary>
    internal static string DevicePath => Path.Combine(AppContext.BaseDirectory, DeviceFileName);

    [STAThread]
    private static void Main()
    {
        // 由 csproj 的 Application* 屬性產生,設定 DPI 與視覺樣式
        // Generated from the csproj Application* properties; sets DPI mode and visual styles.
        ApplicationConfiguration.Initialize();

        var baseDirectory = AppContext.BaseDirectory;
        var dataDirectory = Path.Combine(baseDirectory, "data");

        // 進給軸尚無實機驅動,一律模擬 / No real drive yet for the feed axis: always mocked.
        var motor = new MockMotorController();

        // 設定檔讀不通就直接讓程式開不起來,不要默默退回模擬 ——
        // 「以為接了實機、其實跑模擬」比「開不起來」危險得多。
        // A broken settings file stops the program rather than silently falling back to mocks:
        // believing the real sensor is attached while running on mocks is far more dangerous
        // than not starting at all.
        var deviceOptions = Iv4Options.LoadAsync(DevicePath).GetAwaiter().GetResult();

        Iv4VisionSensor? sensor = null;
        ICodeReader codeReader;
        ICharacterVerifier verifier;

        if (deviceOptions is null)
        {
            // 模擬裝置：固定種子讓試機時的良率序列可重現
            // Mock devices: fixed seeds make the dry-run verdict sequence reproducible.
            codeReader = new MockCodeReader(seed: 20260730) { NoReadRate = 0.03 };
            verifier = new MockCharacterVerifier(seed: 20260730) { FailRate = 0.08 };
        }
        else
        {
            // 同一顆感測器、同一條連線、一次觸發供兩個角色使用
            // One sensor, one link, one trigger serving both roles.
            sensor = new Iv4VisionSensor(deviceOptions);
            codeReader = sensor;
            verifier = sensor;
        }

        var database = new DatabaseManager(Path.Combine(dataDirectory, DatabaseFileName));
        var recipes = new RecipeManager(Path.Combine(baseDirectory, RecipeFileName));

        var sequencer = new InspectionSequencer(motor, codeReader, verifier, database, recipes);

        // 裝置與資料庫由此處擁有,故在此處處置；Sequencer 只處置自己的權杖來源。
        // 實機時 codeReader 與 verifier 是同一個物件,會被處置兩次 ——
        // Iv4VisionSensor.DisposeAsync 為此做成冪等。
        // The devices and database are owned here, so they are disposed here; the
        // sequencer disposes only its own token source. On real hardware codeReader and
        // verifier are the same object and get disposed twice, which is why
        // Iv4VisionSensor.DisposeAsync is idempotent.
        try
        {
            using var form = new MainForm(sequencer, recipes, motor, database, sensor);
            Application.Run(form);
        }
        finally
        {
            DisposeAllAsync(sequencer, motor, codeReader, verifier, database).GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// 依相依順序處置 / Dispose in dependency order.
    /// 先停迴圈,再關裝置：反過來會讓迴圈對已關閉的裝置下命令。
    /// Stop the loop first, then close the devices; the reverse order lets the loop
    /// issue commands to already-closed devices.
    /// </summary>
    private static async Task DisposeAllAsync(
        InspectionSequencer sequencer,
        params IAsyncDisposable[] resources)
    {
        await sequencer.DisposeAsync().ConfigureAwait(false);

        foreach (var resource in resources)
        {
            try
            {
                await resource.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 關機路徑：已無處回報,也不該因此擋住程式結束
                // Shutdown path: nowhere left to report, and it must not block exit.
            }
        }
    }
}
