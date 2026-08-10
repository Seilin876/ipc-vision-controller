using IpcVisionController.Core.Data;
using IpcVisionController.Core.Hal;
using IpcVisionController.Core.Machine;
using IpcVisionController.Core.Recipes;

namespace IpcVisionController.App;

/// <summary>
/// 應用程式入口與組裝根 / Entry point and composition root.
///
/// 這裡是全程式唯一決定「用模擬還是實機」的地方。
/// 換成實機時只改本檔的三個 new,其餘程式碼不動 —— 這正是 HAL 介面存在的理由。
/// This is the only place in the program that decides mock versus real hardware.
/// Swapping to real devices means changing the three `new` expressions below and
/// nothing else, which is the whole point of the HAL interfaces.
/// </summary>
internal static class Program
{
    /// <summary>資料庫檔名 / Database file name.</summary>
    private const string DatabaseFileName = "inspection.db";

    /// <summary>現場配方檔名（非版控）/ Live recipe file name (not versioned).</summary>
    private const string RecipeFileName = "recipe.json";

    [STAThread]
    private static void Main()
    {
        // 由 csproj 的 Application* 屬性產生,設定 DPI 與視覺樣式
        // Generated from the csproj Application* properties; sets DPI mode and visual styles.
        ApplicationConfiguration.Initialize();

        var baseDirectory = AppContext.BaseDirectory;
        var dataDirectory = Path.Combine(baseDirectory, "data");

        // 模擬裝置：固定種子讓試機時的良率序列可重現
        // Mock devices: fixed seeds make the dry-run verdict sequence reproducible.
        var motor = new MockMotorController();
        var scanner = new MockBarcodeScanner(seed: 20260730) { NoReadRate = 0.03 };
        var vision = new MockVisionSensor(seed: 20260730) { NgRate = 0.08 };

        var database = new DatabaseManager(Path.Combine(dataDirectory, DatabaseFileName));
        var recipes = new RecipeManager(Path.Combine(baseDirectory, RecipeFileName));

        var sequencer = new InspectionSequencer(motor, scanner, vision, database, recipes);

        // 裝置與資料庫由此處擁有,故在此處處置；Sequencer 只處置自己的權杖來源。
        // The devices and database are owned here, so they are disposed here; the
        // sequencer disposes only its own token source.
        try
        {
            using var form = new MainForm(sequencer, recipes, motor, database);
            Application.Run(form);
        }
        finally
        {
            DisposeAllAsync(sequencer, motor, scanner, vision, database).GetAwaiter().GetResult();
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
