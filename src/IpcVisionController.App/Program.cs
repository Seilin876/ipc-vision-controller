using IpcVisionController.Core.Data;
using IpcVisionController.Core.Hal;
using IpcVisionController.Core.Machine;
using IpcVisionController.Core.Recipes;

namespace IpcVisionController.App;

/// <summary>
/// 應用程式入口與組裝根 / Entry point and composition root.
///
/// 這裡是全程式唯一決定接哪些裝置的地方,規則只有兩條:
/// device.json 存在就依其內容接實機；不存在就「拒絕啟動」,而不是退回模擬。
/// This is the only place that decides which devices are attached, and there are just two
/// rules: device.json present means the real devices it names; absent means the program refuses
/// to start rather than falling back to mocks.
///
/// 兩台裝置,各自獨立 / Two devices, independently attached:
/// CodeReader（SR-X300）是必要的 —— 配方的條碼筆數下限為 1,沒有讀碼器就每張標籤都判退。
/// CharacterVerifier（IV4）可以缺,那代表這台機器還沒有字符檢測,此時配方的字符區域數要設 0。
/// 兩台各有 IP、埠、觸發命令與欄位配置,因為它們是不同型號、不同設定軟體、不同時間導入的。
/// The CodeReader (SR-X300) is required: the recipe's code count has a floor of one, so without a
/// reader every label rejects. The CharacterVerifier (IV4) may be absent, meaning this machine has
/// no character verification yet and the recipe's region count must be zero. Each device carries
/// its own address, port, command and field layout, because they are different models with
/// different setup software, commissioned at different times.
///
/// 為什麼「找不到設定檔」不再自動跑模擬 /
/// Why a missing settings file no longer silently mocks:
/// 模擬裝置產生的判定看起來與實機結果完全一樣 —— 有條碼、有良率、有判退原因,還會寫進
/// 追溯資料庫 —— 而觸發條件只是「一個檔案不在」。把設定漏放這種再普通不過的失誤,
/// 變成一批可簽核卻毫無意義的生產紀錄,代價與機率都不成比例。
/// 現在要跑模擬必須在啟動時明示 --mock,而那是不會手滑做出來的動作。
/// Mock verdicts are indistinguishable from real ones — codes, a yield, reject reasons, all
/// written to the traceability database — and the only trigger was a file being absent. That
/// turned the most ordinary mistake there is, forgetting to place a settings file, into a
/// batch of signable but meaningless production records. Mocks now require --mock on the
/// command line, which is not something anyone does by accident.
///
/// 為什麼不是編譯期開關 / Why not a compile-time switch:
/// 現場沒有 SDK,也不該為了切換裝置而重新發佈。
/// The line has no SDK and must not need a redeploy to swap devices.
///
/// 進給軸不在這條規則內 —— 它沒有實機驅動可選,一律 MockMotorController。
/// 那不是「模擬與實機二選一」,是「硬體還不存在」的佔位,兩件事不該共用同一個判斷。
/// The feed axis is outside this rule: there is no real driver to choose, so it is always
/// MockMotorController. That is not a mock-or-real choice but a placeholder for hardware that
/// does not exist yet, and the two must not share one decision.
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

    /// <summary>
    /// 明示要求模擬裝置的啟動引數 / The argument that explicitly asks for mock devices.
    /// 只給「沒有感測器但要看畫面」用途：改版面、給人看操作流程。
    /// 從檔案總管雙擊永遠帶不到這個引數,所以產線的正常啟動路徑不可能誤入模擬。
    /// For the one legitimate case: no sensor attached but the UI is needed — a layout change,
    /// or walking someone through the operating sequence. Double-clicking from Explorer can
    /// never supply it, so the line's normal launch path cannot land in mocks by accident.
    /// </summary>
    private const string MockArgument = "--mock";

    [STAThread]
    private static void Main(string[] args)
    {
        // 由 csproj 的 Application* 屬性產生,設定 DPI 與視覺樣式
        // Generated from the csproj Application* properties; sets DPI mode and visual styles.
        ApplicationConfiguration.Initialize();

        // 組裝與執行必須分開包住 / Composing and running have to be wrapped separately.
        //
        // 本專案是 WinExe —— 沒有主控台。Application.Run 之前拋出的任何例外都會讓行程
        // 直接結束,而畫面上什麼都不會出現:雙擊 exe,毫無反應。
        // 組裝階段偏偏是最會拋例外的地方（設定檔格式、欄位驗證、資料庫路徑）,
        // 於是「大聲拒絕啟動」的設計會退化成「安靜死掉」—— 那比默默跑模擬更難查,
        // 因為連一句訊息都沒有。
        // This is a WinExe and has no console. Any exception thrown before Application.Run ends the
        // process with nothing on screen: double-click the exe, nothing happens. The composition
        // phase is precisely where exceptions come from — settings format, field validation, database
        // paths — so a design meant to refuse loudly degrades into dying silently, which is harder to
        // diagnose than quietly running on mocks, because there is not even a message.
        Composition composed;
        try
        {
            var composition = Compose(args);
            if (composition is null)
            {
                // 已經用對話框說明過原因 / The reason was already shown in a dialog.
                return;
            }

            composed = composition;
        }
        catch (Exception ex)
        {
            ReportStartupFailure(ex);
            return;
        }

        // 裝置與資料庫由此處擁有,故在此處處置；Sequencer 只處置自己的權杖來源。
        // The devices and database are owned here, so they are disposed here; the sequencer
        // disposes only its own token source.
        try
        {
            using var form = new MainForm(
                composed.Sequencer, composed.Recipes, composed.Database, composed.Devices);
            Application.Run(form);
        }
        finally
        {
            DisposeAllAsync(composed.Sequencer, [.. composed.Devices.All, composed.Database])
                .GetAwaiter().GetResult();
        }
    }

    /// <summary>組裝結果 / What composition produced.</summary>
    private sealed record Composition(
        InspectionSequencer Sequencer,
        RecipeManager Recipes,
        DatabaseManager Database,
        DeviceSet Devices);

    /// <summary>
    /// 依設定組裝整個系統 / Compose the whole system from the settings.
    /// </summary>
    /// <returns>
    /// 組裝結果；已向使用者說明並決定不啟動時為 null /
    /// The composition, or null when the user has been told why the program will not start.
    /// </returns>
    private static Composition? Compose(string[] args)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var dataDirectory = Path.Combine(baseDirectory, "data");

        // 進給軸尚無實機驅動,一律模擬 / No real drive yet for the feed axis: always mocked.
        var motor = new MockMotorController();

        // 設定檔讀不通就直接讓程式開不起來 —— 內容錯誤與檔案不存在是兩件事,
        // 前者一定是打錯了,不該被當成「這台機器要跑模擬」。
        // A broken settings file stops the program outright: bad contents and no file at all are
        // different things, and the former is always a typo rather than a decision to mock.
        var settings = DeviceSettings.LoadAsync(DevicePath).GetAwaiter().GetResult();
        var mockRequested = args.Contains(MockArgument, StringComparer.OrdinalIgnoreCase);

        // 沒有設定檔又沒有明示模擬 —— 拒絕啟動。
        // 這裡刻意不「開起來但停在離線狀態」:畫面開著就會有人按下去,而按下去就會產生紀錄。
        // No settings file and no explicit request: refuse. Deliberately not "open but sit
        // offline" — an open window gets pressed, and pressing it produces records.
        if (settings is null && !mockRequested)
        {
            RefuseToStart();
            return null;
        }

        // 兩者同時存在時實機優先,但必須說出來。
        // 實機優先是為了讓「產線桌面上留著一個帶 --mock 的捷徑」這種事無害;
        // 而靜默忽略一個明示引數,正是本次要消滅的那類行為 —— 所以擋一個對話框。
        // Real hardware wins when both are present, but not silently. Real-wins keeps a
        // leftover --mock shortcut on the line's desktop harmless; announcing it avoids
        // silently ignoring an explicit argument, which is the very behaviour being removed.
        if (settings is not null && mockRequested)
        {
            MessageBox.Show(
                $"{MockArgument} 已忽略：{DevicePath} 存在,將以實機裝置啟動。{Environment.NewLine}"
                + $"要跑模擬請先移走該檔案。{Environment.NewLine}{Environment.NewLine}"
                + $"{MockArgument} ignored: {DevicePath} exists, so the real devices are used. "
                + "Move that file aside to run on mocks.",
                "IPC Vision Controller",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        ICodeReader codeReader;
        ICharacterVerifier verifier;

        if (settings is null)
        {
            // 明示要求的模擬裝置：固定種子讓畫面上的良率序列可重現
            // Mocks, explicitly asked for: fixed seeds make the on-screen sequence reproducible.
            codeReader = new MockCodeReader(seed: 20260730) { NoReadRate = 0.03 };
            verifier = new MockCharacterVerifier(seed: 20260730) { FailRate = 0.08 };
        }
        else
        {
            // 兩台獨立裝置,各自一條連線 / Two independent devices, one link each.
            codeReader = new SrX300CodeReader(settings.CodeReader!);

            // 沒有 CharacterVerifier 這一段就是「還沒接 IV4」。用一個明確表示不存在的實作,
            // 而不是讓 verifier 可以是 null —— 後者會在檢測週期裡撒一堆 null 檢查,
            // 漏掉任何一個的後果是「字符檢測被跳過但紀錄看起來完整」。
            // No CharacterVerifier section means the IV4 is not attached yet. An implementation
            // that explicitly means "absent" beats letting verifier be null, which would scatter
            // null checks through the cycle where missing one yields "verification skipped but the
            // record looks complete".
            verifier = settings.CharacterVerifier is null
                ? new AbsentCharacterVerifier()
                : new Iv4CharacterVerifier(settings.CharacterVerifier);
        }

        var devices = new DeviceSet(motor, codeReader, verifier, AreMocks: settings is null);

        var database = new DatabaseManager(Path.Combine(dataDirectory, DatabaseFileName));
        var recipes = new RecipeManager(Path.Combine(baseDirectory, RecipeFileName));

        // 兩站相隔格數來自 device.json —— 它描述的是這兩台裝置裝得多遠。
        // 模擬模式沒有裝置也就沒有站別,一律 0（兩台看同一張)。
        // 這個值錯掉的後果不是「跑不動」,而是追溯紀錄把不同標籤的兩半湊在一起 ——
        // 所以它必須來自設定檔而不是預設值,現場才有機會填對。
        // The station offset comes from device.json, which is what describes how far apart these two
        // devices are mounted. On mocks there are no stations, so it is zero — both sensors on one label.
        // A wrong value here does not stop the machine; it pairs halves of different labels in the
        // traceability record, which is why it has to come from the settings file rather than a default
        // the line never sees.
        var sequencerOptions = new SequencerOptions
        {
            InspectionOffsetPitches = settings?.InspectionOffsetPitches ?? 0,
        };

        var sequencer = new InspectionSequencer(
            devices.Motor, devices.CodeReader, devices.Verifier, database, recipes, sequencerOptions);

        return new Composition(sequencer, recipes, database, devices);
    }

    /// <summary>
    /// 啟動失敗時把原因說出來 / Say why startup failed.
    ///
    /// 沒有主控台可以印,所以只剩對話框。訊息必須包含設定檔的完整路徑與例外本文 ——
    /// 現場拿不到堆疊追蹤,而例外本文正是驗證邏輯特意寫給人看的那句話。
    /// There is no console to print to, so a dialog is all that is left. The message has to carry the
    /// settings file's full path and the exception's own text: the line cannot read a stack trace, and
    /// that text is the sentence the validation deliberately wrote for a person.
    /// </summary>
    private static void ReportStartupFailure(Exception ex)
    {
        var message = string.Join(Environment.NewLine, [
            "程式無法啟動 / The program could not start.",
            string.Empty,
            ex.Message,
            string.Empty,
            $"裝置設定檔 / device settings file:{Environment.NewLine}{DevicePath}",
            string.Empty,
            "多數情況是這個檔案的內容不合。改好後重新啟動；",
            $"若要在沒有裝置的情況下檢視畫面,請以 {MockArgument} 啟動。",
            "Most often the contents of that file are not usable. Fix it and restart, or start with "
                + $"{MockArgument} to view the UI with no devices attached.",
        ]);

        MessageBox.Show(
            message,
            "IPC Vision Controller",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    /// <summary>
    /// 拒絕啟動並說明原因 / Refuse to start, and say why.
    ///
    /// 訊息必須包含程式實際找過的完整路徑。最常見的兩種失誤 —— device.json 放在原始碼
    /// 資料夾（csproj 只複製 device.sample.json,不會被帶到輸出目錄）、以及 Windows 隱藏
    /// 副檔名時存成 device.json.txt —— 都只有把路徑印出來才會當場現形。
    /// The message has to carry the full path actually searched. The two usual mistakes — a
    /// device.json left in the source folder, which the csproj never copies, and a
    /// device.json.txt saved with Windows hiding extensions — only become visible when the
    /// path is spelled out.
    /// </summary>
    private static void RefuseToStart()
    {
        var message = string.Join(Environment.NewLine, [
            "找不到裝置設定檔,程式不會以模擬資料啟動。",
            "Device settings file not found. The program will not start on mock data.",
            string.Empty,
            DevicePath,
            string.Empty,
            "請把 device.sample.json 複製成上述路徑的 device.json（與執行檔同一資料夾,",
            "不是原始碼資料夾）,填入現場數值後重新啟動。",
            "Copy device.sample.json to that exact path as device.json — next to the executable,",
            "not in the source folder — fill in the line's values, and restart.",
            string.Empty,
            $"若只是要檢視操作畫面而不接感測器,請以 {MockArgument} 啟動。",
            "該模式的判定由產生器產出,與任何標籤無關,不可作為出貨依據。",
            $"To inspect the UI without a sensor, start with {MockArgument}. Its verdicts come from",
            "a generator, relate to no real label, and are not a shipping record.",
        ]);

        MessageBox.Show(
            message,
            "IPC Vision Controller",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
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
