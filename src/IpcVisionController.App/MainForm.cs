using System.Globalization;
using IpcVisionController.Core.Data;
using IpcVisionController.Core.Hal;
using IpcVisionController.Core.Machine;
using IpcVisionController.Core.Models;
using IpcVisionController.Core.Recipes;

namespace IpcVisionController.App;

/// <summary>
/// 操作主畫面 / Operator main screen.
///
/// 本類別刻意「不含」任何判定或設備邏輯 —— 它只做三件事：
/// 1) 把按鈕轉成 Sequencer 命令,2) 把 Sequencer 事件封送到 UI 執行緒,3) 顯示。
/// This class deliberately contains no judgement or device logic. It does three
/// things: turn buttons into sequencer commands, marshal sequencer events onto the
/// UI thread, and render.
///
/// 版面以程式碼手寫而非設計工具產生：產線程式需要可 code review 的版面差異。
/// The layout is hand-written rather than designer-generated, because line software
/// needs layout changes that show up in a code review.
/// </summary>
internal sealed class MainForm : Form
{
    /// <summary>畫面更新週期；快到看得出動作,慢到不吃滿 CPU / Refresh period: fast enough to look live, slow enough to stay cheap.</summary>
    private static readonly TimeSpan RefreshPeriod = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// 畫面保留的列數 / Rows retained on screen.
    /// 一次觸發現在會產生「讀到幾筆條碼」那麼多列,所以列數不再等於觸發次數 ——
    /// 六道並排的機構下,600 列大約是一百次觸發。
    /// One trigger now produces as many rows as codes read, so rows no longer equal triggers: with six lanes,
    /// six hundred rows is roughly a hundred triggers.
    /// </summary>
    private const int MaxVisibleRows = 600;

    /// <summary>
    /// 開機時載入幾次觸發的歷史 / How many past triggers to load at startup.
    /// 與列數分開計:用列數去查資料庫會讀回六倍的紀錄,渲染完再修剪掉五分之四,白做工。
    /// Counted separately from rows: querying the database by row count would fetch six times as many records
    /// and throw four fifths of the rendering away.
    /// </summary>
    private const int HistoryTriggers = 100;

    private readonly InspectionSequencer _sequencer;
    private readonly RecipeManager _recipes;
    private readonly IMotorController _motor;
    private readonly DatabaseManager _database;
    private readonly DeviceSet _devices;

    private readonly Button _initializeButton = new() { Text = "初始化 Initialize", Width = 150, Height = 40 };
    private readonly Button _startButton = new() { Text = "開始 Start", Width = 120, Height = 40, Enabled = false };
    // 試機用：跑一格就停,逐格核對判定 / Dry runs: one pitch per press, verdict checked each time
    private readonly Button _triggerButton = new() { Text = "單次觸發 Trigger once", Width = 170, Height = 40, Enabled = false };
    private readonly Button _stopButton = new() { Text = "停止 Stop", Width = 120, Height = 40, Enabled = false };
    private readonly Button _resetButton = new() { Text = "解除故障 Reset", Width = 150, Height = 40, Enabled = false };

    private readonly Label _stateLabel = new()
    {
        Text = "OFFLINE",
        Font = new Font("Segoe UI", 20F, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleCenter,
        Dock = DockStyle.Fill,
        BackColor = Color.Gainsboro,
    };

    private readonly Label _tallyLabel = new()
    {
        Text = "PASS 0 / FAIL 0",
        Font = new Font("Segoe UI", 12F),
        TextAlign = ContentAlignment.MiddleCenter,
        Dock = DockStyle.Fill,
    };

    private readonly Label _positionLabel = new()
    {
        Text = "已進給 Fed: 0",
        Font = UiFont.Of(11F),
        TextAlign = ContentAlignment.MiddleCenter,
        Dock = DockStyle.Fill,
    };

    private readonly TextBox _modelNameBox = new() { Width = 140 };
    private readonly NumericUpDown _codeCountBox = new() { Minimum = 1, Maximum = 10, Value = 1, Width = 60 };

    /// <summary>下限 0 代表不檢查長度 / A minimum of 0 means the length is not checked.</summary>
    private readonly NumericUpDown _barcodeLengthBox = new() { Minimum = 0, Maximum = 128, Value = 12, Width = 70 };

    /// <summary>
    /// 等級下限是可選的,因此用核取方塊而非哨兵值 / The grade minimum is optional, so a
    /// checkbox carries the "unset" case rather than another sentinel number.
    /// 長度已經用 0 當哨兵了,再疊一個哨兵刻度只會讓現場記不住哪個數字代表關閉。
    /// The length field already spends 0 as a sentinel; a second sentinel scale would
    /// leave the line guessing which number switches which check off.
    /// </summary>
    private readonly CheckBox _checkGradeBox = new()
    {
        Text = "檢查等級 Check grade",
        AutoSize = true,
        Padding = new Padding(12, 6, 0, 0),
    };

    /// <summary>
    /// 等級下限 / The grade minimum.
    ///
    /// 預設 2 對應 ISO/IEC 15415 / 15416 的 0–4 刻度（4 最好,對應字母 A)——
    /// 那是 SR-X300 目前輸出的刻度。上限留在 100,讓改成 0–100 的讀取餘裕度時不必動程式。
    /// 先前預設 70,那是餘裕度刻度的數字;在 0–4 的刻度上,勾選「檢查等級」的那一刻
    /// 就會讓每一張標籤判退,而看起來像整批印刷不良。
    /// A default of 2 suits the 0–4 ISO/IEC 15415 and 15416 scale, where 4 is best, which is what the
    /// SR-X300 emits today. The maximum stays at 100 so a switch to a 0–100 read margin needs no code
    /// change. The old default of 70 belonged to that margin scale: on a 0–4 scale, ticking the grade
    /// check would reject every single label and look like a batch of bad print.
    /// </summary>
    private readonly NumericUpDown _minimumGradeBox = new()
    {
        Minimum = 0, Maximum = 100, Value = 2, Width = 60, Enabled = false,
    };

    /// <summary>
    /// 字符區域數；下限 0 表示不檢查 / Character regions; a minimum of 0 disables the check.
    /// 感測器程式裡沒有 OCR 區域時必須能填 0,否則每張標籤都會以「區域數不足」判退。
    /// Zero has to be reachable when the sensor program has no OCR region, or every label
    /// rejects on the region count.
    /// </summary>
    private readonly NumericUpDown _regionCountBox = new() { Minimum = 0, Maximum = 10, Value = 1, Width = 60 };

    /// <summary>
    /// 一次進給的脈波數 / Pulses commanded for one feed.
    ///
    /// 為什麼放在配方面板而不是設定檔 / Why this sits in the recipe panel rather than a file:
    /// 一次進給要走「視野裡那幾張標籤」的距離,而標籤長度隨機種變 —— 所以它是換線要調的值,
    /// 而換線是在這個畫面上做的。寫在設定檔裡就得為了換一個機種去改檔案、重開程式。
    /// One feed covers the labels sitting in the field of view, and label length changes with the product, so
    /// this is a changeover value — and changeovers happen on this screen. In a file it would mean editing
    /// the file and restarting the program to run a different product.
    ///
    /// 它同時決定「檢測站在下游幾格」:程式用 device.json 裡的固定站距除以本值,
    /// 所以換機種只改這一個數字,偏移就自動跟著對,不必人工換算。
    /// It also decides how many pitches downstream the verifier sits: the program divides the fixed station
    /// distance from device.json by this value, so a changeover edits one number and the offset follows with
    /// no manual conversion.
    ///
    /// 上限一百萬:實機傳動比未知,留寬讓現場填得下真實值;下限 1,因為 0 代表料帶不動,
    /// 同一張標籤會被反覆檢測並反覆寫入追溯紀錄。
    /// The maximum is a million because the real drive ratio is not yet known and the line must be able to
    /// enter the true value; the minimum is one, because zero leaves the web still and one label would be
    /// inspected and logged over and over.
    /// </summary>
    private readonly NumericUpDown _feedPitchBox = new()
    {
        Minimum = 1, Maximum = 1_000_000, Value = 10_000, Increment = 100, Width = 90,
        ThousandsSeparator = true,
    };

    private readonly Button _saveRecipeButton = new() { Text = "儲存配方 Save", Width = 130, Height = 30 };

    private readonly ListView _recordList = new()
    {
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        Dock = DockStyle.Fill,
        Font = UiFont.Of(9F),
    };

    private readonly TextBox _logBox = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        Font = UiFont.Of(9F),
    };

    private readonly System.Windows.Forms.Timer _refreshTimer = new();

    private int _passCount;
    private int _failCount;

    /// <param name="devices">
    /// 這次執行接上的裝置 / The devices this run attached.
    /// 畫面需要它來做兩件事：宣告模擬或實機,以及逐台顯示原始電文與生效的欄位配置。
    /// 電文一定要標明來源 —— 讀碼器與字符檢測器的電文長得很像,索引卻是各自獨立設定的。
    /// The UI needs it for two things: declaring mock versus live, and showing each device's raw
    /// frames alongside the field layout in force. Frames always name their device, because the
    /// reader's and the verifier's look alike while their indexes are configured independently.
    /// </param>
    public MainForm(
        InspectionSequencer sequencer,
        RecipeManager recipes,
        DatabaseManager database,
        DeviceSet devices)
    {
        _sequencer = sequencer ?? throw new ArgumentNullException(nameof(sequencer));
        _recipes = recipes ?? throw new ArgumentNullException(nameof(recipes));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _motor = devices.Motor;

        // 標題在此設定而非 OnShown：標題列是模擬/實機唯一永遠可見的標示,
        // 不該有任何一瞬間顯示未標示的標題。Form.Text 在句柄建立前指派是安全的。
        // The title is set here rather than in OnShown: the title bar is the only always-visible
        // mark of mock versus live and must never appear unmarked, not even for one frame.
        // Assigning Form.Text before the handle exists is safe.
        Text = DeviceModeTitle();
        Icon = LoadWindowIcon();
        MinimumSize = new Size(900, 640);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        WireEvents();
    }

    /// <summary>視窗標題的固定部分 / The fixed part of the window title.</summary>
    private const string BaseTitle = "IPC Vision Controller";

    /// <summary>
    /// 帶模擬/實機標示的視窗標題 / The window title, carrying the mock-versus-live mark.
    ///
    /// 模擬判定來自固定種子的產生器,與任何一張標籤都無關,但在畫面上與實機結果一模一樣 ——
    /// 有條碼、有良率、有判退原因。不標示等於默許把模擬資料簽核成出貨紀錄。
    /// 標示放在標題列,是因為那是唯一永遠在畫面上、連截圖都帶得走的位置。
    /// Mock verdicts come from a seeded generator and have nothing to do with any label, yet on
    /// screen they are indistinguishable from real ones — codes, a yield, reject reasons. Not
    /// marking the mode permits mock data to be signed off as a shipping record. The mark lives
    /// in the title bar as the one place always on screen that also survives a screenshot.
    /// </summary>
    private string DeviceModeTitle() => _devices.AreMocks
        ? $"{BaseTitle} — 【模擬資料 MOCK DATA】非出貨依據 / not a shipping record"
        : $"{BaseTitle} — 實機 / LIVE {_devices.CodeReader.Name}";

    /// <summary>
    /// 把模擬/實機的來由寫進操作訊息區 / Record why this is mock or live in the log pane.
    ///
    /// 光標示模式不夠:現場還需要知道「為什麼是模擬」與「要改哪個檔」,否則只知道不對,
    /// 不知道下一步。實機時則印出實際生效的欄位索引,那是唯一能與原始電文對照的東西。
    /// Marking the mode is not enough: the line also needs why it is mocked and which file
    /// changes it, or it knows only that something is wrong and not what to do next. On live
    /// hardware it prints the indexes actually in force, the only thing a raw frame can be
    /// checked against.
    /// </summary>
    private void AppendDeviceModeLog()
    {
        // 選用的字型要說出來。缺字的表徵是中文變成一格一格的方框,而那看起來像「程式壞了」
        // 或「資料壞了」—— 兩者都不是。印出字型名稱,現場一眼就能分辨是字型問題,
        // 而不是去懷疑判定結果。
        // The chosen font is stated. Missing glyphs present as rows of boxes where Chinese should be, which
        // reads as a broken program or corrupt data when it is neither. Naming the font lets the line tell a
        // font problem apart at a glance instead of doubting the verdicts.
        AppendLog($"畫面字型 / screen font: {UiFont.FamilyName}");

        if (_devices.AreMocks)
        {
            AppendLog("模擬模式 / MOCK MODE（以 --mock 啟動 / started with --mock）");
            AppendLog("判定來自固定種子的產生器,與任何實際標籤無關,不可作為出貨依據,");
            AppendLog("紀錄列表與追溯資料庫裡的內容同樣不是量測結果。");
            AppendLog("Verdicts come from a seeded generator and relate to no real label. Neither the "
                + "record list nor the traceability database holds measurements here.");
            AppendLog($"接實機：關閉本程式,確認 {Program.DevicePath} 存在且內容正確,不帶引數重新啟動。");
            AppendLog($"To go live: close this, ensure {Program.DevicePath} exists and is correct, and "
                + "restart with no arguments.");
            return;
        }

        AppendLog("實機模式 / LIVE MODE");
        AppendLog($"讀碼 / code read: {_devices.CodeReader.Name}");
        AppendLog($"字符檢測 / character verify: {_devices.Verifier.Name}");

        // 每台裝置各印一行生效的欄位配置。兩台是各自獨立設定的,
        // 只印其中一台會讓另一台的索引錯誤完全無跡可循。
        // One line of live field layout per device. The two are configured independently, and
        // printing only one leaves a wrong index on the other with no trace at all.
        foreach (var source in _devices.FrameSources)
        {
            AppendLog($"欄位配置 / field layout: {source.Configuration}");
        }

        // 字符檢測未導入時要明說,並指出配方該怎麼配合 ——
        // 否則配方仍要求區域數,而現場會看到每張標籤都以「區域數不足」判退。
        // Say so explicitly when verification is not installed, and say what the recipe must do to
        // match: otherwise the recipe still demands regions and every label rejects on the count.
        if (_devices.Verifier is AbsentCharacterVerifier)
        {
            AppendLog("尚未導入字符檢測,配方的字符區域數請設為 0,否則每張標籤都會以「區域數不足」判退。");
            AppendLog("No character verification installed: set the recipe's region count to 0, or "
                + "every label rejects on the region count.");
        }

        AppendLog("欄位索引未經實機電文核對前,判定結果不足以採信 —— 請先按單次觸發,"
            + "對照隨後出現的「電文」行逐格數過去。");
        AppendLog("Until the field indexes are checked against a real frame the verdicts cannot be "
            + "trusted: press Trigger once and count the fields in the frame lines that follow.");
    }

    /// <summary>
    /// 載入視窗圖示 / Load the window icon.
    ///
    /// 從內嵌資源而非檔案讀取：圖示檔若沒被複製到輸出目錄就會變成執行期才發現的缺檔,
    /// 內嵌資源則跟著組件走,發佈方式（framework-dependent、single-file）再怎麼換都在。
    /// Read from an embedded resource rather than a file: a loose icon that misses the
    /// output directory becomes a run-time surprise, whereas an embedded one travels
    /// with the assembly whatever the publish shape.
    ///
    /// 讀不到就回 null（沿用系統預設圖示）而不拋例外 ——
    /// 圖示是外觀,產線工具不該為了外觀開不起來。
    /// A failure yields null (keeping the stock icon) rather than an exception: the icon
    /// is cosmetic, and a line tool must not refuse to open over cosmetics.
    /// </summary>
    private static Icon? LoadWindowIcon()
    {
        // 名稱對應 csproj 的 LogicalName / Matches the LogicalName in the csproj.
        using var stream = typeof(MainForm).Assembly
            .GetManifestResourceStream("IpcVisionController.App.delta.ico");

        // 傳入多尺寸 .ico,由 WinForms 自行挑標題列與 Alt-Tab 各自需要的尺寸
        // Handing over the multi-size .ico lets WinForms pick the right one for the
        // title bar and for Alt-Tab independently.
        return stream is null ? null : new Icon(stream);
    }

    private void BuildLayout()
    {
        _recordList.Columns.Add("時間 Time", 140);
        _recordList.Columns.Add("機種 Model", 90);
        // 位置就是實體位置:讀碼器按標籤的物理順序輸出,所以這個編號指得出是哪一道。
        // 讀不到的那一道會缺號,而缺號本身就是「哪一張沒讀到」的答案。
        // The position is the physical one: the reader emits codes in the labels' order, so this number
        // identifies which lane. An unread lane leaves a gap, and the gap is the answer to which label failed.
        _recordList.Columns.Add("位置 #", 50);
        _recordList.Columns.Add("條碼 Code", 210);
        _recordList.Columns.Add("等級 Grade", 60);
        _recordList.Columns.Add("字符 Characters", 130);
        _recordList.Columns.Add("判定 Judge", 70);
        // 判退原因擺最後且給最寬：不良品的追溯價值有一半在「為什麼退」
        // Widest and last: half the traceability value of a reject is *why*.
        _recordList.Columns.Add("判退原因 Reject reason", 420);

        var commandPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            WrapContents = false,
        };
        commandPanel.Controls.AddRange([_initializeButton, _triggerButton, _startButton, _stopButton, _resetButton]);

        var statusPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
        };
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));
        statusPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30F));
        statusPanel.Controls.Add(_stateLabel, 0, 0);
        statusPanel.Controls.Add(_tallyLabel, 1, 0);
        statusPanel.Controls.Add(_positionLabel, 2, 0);

        // 欄位變多後單行擺不下,允許換行而非橫向捲動 —— 現場用觸控操作,捲動比換行難按
        // Too many fields for one row now; wrap rather than scroll sideways, because the
        // line drives this by touch and a scrollbar is the harder target.
        var recipePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 4, 8, 4),
            WrapContents = true,
        };
        recipePanel.Controls.AddRange(
        [
            new Label { Text = "機種 Model:", AutoSize = true, Padding = new Padding(0, 8, 0, 0) },
            _modelNameBox,
            new Label { Text = "條碼筆數 Codes:", AutoSize = true, Padding = new Padding(12, 8, 0, 0) },
            _codeCountBox,
            new Label { Text = "長度 Length (0=不檢查 off):", AutoSize = true, Padding = new Padding(12, 8, 0, 0) },
            _barcodeLengthBox,
            _checkGradeBox,
            _minimumGradeBox,
            new Label { Text = "字符區域 Regions (0=不檢查 off):", AutoSize = true, Padding = new Padding(12, 8, 0, 0) },
            _regionCountBox,
            new Label { Text = "一格脈波 Pulses/feed:", AutoSize = true, Padding = new Padding(12, 8, 0, 0) },
            _feedPitchBox,
            _saveRecipeButton,
        ]);

        var recipeGroup = new GroupBox { Text = "配方 Recipe", Dock = DockStyle.Fill };
        recipeGroup.Controls.Add(recipePanel);

        var recordGroup = new GroupBox { Text = "檢測紀錄 Inspection log", Dock = DockStyle.Fill };
        recordGroup.Controls.Add(_recordList);

        var logGroup = new GroupBox { Text = "訊息 Messages", Dock = DockStyle.Fill };
        logGroup.Controls.Add(_logBox);

        var contentSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterDistance = 300,
        };
        contentSplit.Panel1.Controls.Add(recordGroup);
        contentSplit.Panel2.Controls.Add(logGroup);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 70F));
        // 配方欄位換行後需要兩行的高度 / Two rows' worth of height now that the fields wrap.
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.Controls.Add(commandPanel, 0, 0);
        root.Controls.Add(statusPanel, 0, 1);
        root.Controls.Add(recipeGroup, 0, 2);
        root.Controls.Add(contentSplit, 0, 3);

        Controls.Add(root);
    }

    private void WireEvents()
    {
        _initializeButton.Click += OnInitializeClicked;
        _triggerButton.Click += OnTriggerOnceClicked;
        _startButton.Click += OnStartClicked;
        _stopButton.Click += OnStopClicked;
        _resetButton.Click += OnResetClicked;
        _saveRecipeButton.Click += OnSaveRecipeClicked;
        _checkGradeBox.CheckedChanged += (_, _) => _minimumGradeBox.Enabled = _checkGradeBox.Checked;

        _sequencer.StateChanged += OnStateChanged;
        _sequencer.CycleCompleted += OnCycleCompleted;
        _sequencer.LogEmitted += OnLogEmitted;

        foreach (var source in _devices.FrameSources)
        {
            source.RawFrameReceived += OnRawFrameReceived;
        }

        _refreshTimer.Interval = (int)RefreshPeriod.TotalMilliseconds;
        _refreshTimer.Tick += OnRefreshTick;
        _refreshTimer.Start();
    }

    // ── 命令 / Commands ──────────────────────────────────────────────────────

    private async void OnInitializeClicked(object? sender, EventArgs e)
    {
        SetCommandsEnabled(false);
        try
        {
            await _sequencer.InitializeAsync().ConfigureAwait(true);
            await LoadHistoryAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowError("初始化失敗 / Initialisation failed", ex);
        }
        finally
        {
            ApplyStateToCommands(_sequencer.State);
        }
    }

    /// <summary>
    /// 單次觸發 / Trigger exactly one cycle.
    /// 期間全數停用命令鈕：連按會讓料帶多送一格而該格從未被檢測,
    /// 狀態機那一層也擋,但擋在 UI 才不會讓操作員收到看不懂的狀態例外。
    /// Every command button is disabled while it runs: a double-press advances the web by
    /// an uninspected pitch. The state machine refuses it too, but refusing it here spares
    /// the operator an illegal-transition exception they cannot act on.
    /// </summary>
    private async void OnTriggerOnceClicked(object? sender, EventArgs e)
    {
        SetCommandsEnabled(false);
        try
        {
            await _sequencer.TriggerOnceAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowError("單次觸發失敗 / Trigger failed", ex);
        }
        finally
        {
            ApplyStateToCommands(_sequencer.State);
        }
    }

    private void OnStartClicked(object? sender, EventArgs e)
    {
        try
        {
            _sequencer.Start();
        }
        catch (Exception ex)
        {
            ShowError("無法開始 / Cannot start", ex);
        }
        finally
        {
            ApplyStateToCommands(_sequencer.State);
        }
    }

    private async void OnStopClicked(object? sender, EventArgs e)
    {
        SetCommandsEnabled(false);
        try
        {
            await _sequencer.StopAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ShowError("停機異常 / Stop failed", ex);
        }
        finally
        {
            ApplyStateToCommands(_sequencer.State);
        }
    }

    private void OnResetClicked(object? sender, EventArgs e)
    {
        try
        {
            _sequencer.Reset();
        }
        catch (Exception ex)
        {
            ShowError("無法解除故障 / Cannot reset", ex);
        }
        finally
        {
            ApplyStateToCommands(_sequencer.State);
        }
    }

    private async void OnSaveRecipeClicked(object? sender, EventArgs e)
    {
        _saveRecipeButton.Enabled = false;
        try
        {
            var recipe = new RecipeModel
            {
                ModelName = _modelNameBox.Text.Trim(),
                ExpectedCodeCount = (int)_codeCountBox.Value,
                BarcodeLength = (int)_barcodeLengthBox.Value,
                MinimumCodeGrade = _checkGradeBox.Checked ? (int)_minimumGradeBox.Value : null,
                ExpectedCharacterRegionCount = (int)_regionCountBox.Value,
                FeedPitchPulses = (int)_feedPitchBox.Value,
            };

            await _recipes.SaveAsync(recipe).ConfigureAwait(true);

            // 0 印成 off 而不是 0：操作員讀到「0 region(s)」會以為是設定漏填,
            // 而它其實是「本機種不檢查字符」的意思。
            // Zero prints as off: an operator reading "0 region(s)" takes it for an unfilled
            // field, when it actually says this product does not check characters.
            var regions = recipe.ExpectedCharacterRegionCount == RecipeModel.NoCheck
                ? "off"
                : recipe.ExpectedCharacterRegionCount.ToString(CultureInfo.InvariantCulture);

            AppendLog(string.Create(CultureInfo.InvariantCulture,
                $"配方已儲存 / Recipe saved: {recipe.ModelName} ({recipe.ExpectedCodeCount} code(s), length {recipe.BarcodeLength}, grade ≥ {recipe.MinimumCodeGrade?.ToString(CultureInfo.InvariantCulture) ?? "off"}, regions {regions}, {recipe.FeedPitchPulses} pulses/feed)"));

            // 一格脈波數改變會同時改變料帶走的距離與「檢測站在下游幾格」,
            // 所以要提醒重新初始化 —— 那是重算偏移並清空在製品佇列的地方。
            // Changing the pulses per feed changes both how far the web moves and how many pitches downstream
            // the verifier sits, so it prompts a re-initialise: that is where the offset is recomputed and the
            // in-flight queue cleared.
            AppendLog("一格脈波數已變更時請重新初始化,讓相隔格數重新換算 / re-initialise after changing the "
                + "pulses per feed so the station offset is recomputed.");
        }
        catch (InvalidRecipeException ex)
        {
            // 配方不合法是操作錯誤,不是程式錯誤 —— 用一般提示,不要當成當機
            // An invalid recipe is operator error, not a program fault: inform, don't alarm.
            MessageBox.Show(this, ex.Message, "配方不合法 / Invalid recipe",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            ShowError("配方儲存失敗 / Could not save recipe", ex);
        }
        finally
        {
            _saveRecipeButton.Enabled = true;
        }
    }

    // ── Sequencer 事件（背景執行緒）/ Sequencer events (background threads) ──

    private void OnStateChanged(object? sender, StateChangedEventArgs e) => RunOnUi(() =>
    {
        ApplyStateToCommands(e.Current);
        if (e.Reason is not null)
        {
            AppendLog($"狀態 / State → {e.Current}: {e.Reason}");
        }
    });

    private void OnCycleCompleted(object? sender, CycleCompletedEventArgs e) => RunOnUi(() =>
    {
        if (e.Record.FinalJudge == Verdict.Pass)
        {
            _passCount++;
        }
        else
        {
            _failCount++;
        }

        InsertRecordRow(e.Record, atTop: true);
        TrimRecordRows();
    });

    private void OnLogEmitted(object? sender, SequencerLogEventArgs e) => RunOnUi(() => AppendLog(e.Message));

    /// <summary>
    /// 顯示裝置的原始電文 / Show a device's raw frame.
    /// 導入期間唯一能確認 device.json 欄位索引是否正確的依據。少了它,設定錯誤的表徵
    /// 是「每張標籤都判退」—— 與印刷不良、與感測器沒對焦完全分不出來。
    /// 一定要標明來源:兩台裝置的電文格式各自獨立設定,混在一起就無法對照。
    /// The only thing that confirms whether device.json's field indexes are right. Without it a
    /// misconfiguration presents as "every label rejects", indistinguishable from bad print and
    /// from a sensor out of focus. The device is always named: the two frame layouts are
    /// configured independently and cannot be checked against each other once mixed.
    /// </summary>
    private void OnRawFrameReceived(object? sender, RawFrameEventArgs e) =>
        RunOnUi(() => AppendLog($"電文 / frame [{e.DeviceName}]: {e.Frame}"));

    private void OnRefreshTick(object? sender, EventArgs e)
    {
        var state = _sequencer.State;
        _stateLabel.Text = StateCaption(state);
        _stateLabel.BackColor = StateColour(state);

        _tallyLabel.Text = string.Create(CultureInfo.InvariantCulture,
            $"PASS {_passCount} / FAIL {_failCount}");

        // 累計進給量而非絕對座標：料帶沒有絕對位置,只有「自上次對標以來走了多少」
        // Accumulated feed, not an absolute coordinate: a web has no absolute position,
        // only how far it has run since the last registration align.
        _positionLabel.Text = string.Create(CultureInfo.InvariantCulture,
            $"已進給 Fed: {_motor.CurrentPosition:N0}");
    }

    // ── 顯示輔助 / Rendering helpers ─────────────────────────────────────────

    private async Task LoadHistoryAsync()
    {
        var (pass, fail) = await _database.GetTallyAsync().ConfigureAwait(true);
        _passCount = pass;
        _failCount = fail;

        var recent = await _database.GetRecentAsync(HistoryTriggers).ConfigureAwait(true);

        _recordList.BeginUpdate();
        try
        {
            _recordList.Items.Clear();
            // GetRecentAsync 已由新到舊排序,依序附加即維持同一順序
            // GetRecentAsync already returns newest-first, so appending preserves that order.
            foreach (var record in recent)
            {
                InsertRecordRow(record, atTop: false);
            }
        }
        finally
        {
            _recordList.EndUpdate();
        }

        AppendLog(string.Create(CultureInfo.InvariantCulture,
            $"已載入歷史紀錄 / Loaded {recent.Count} historical record(s)."));
    }

    /// <summary>
    /// 把一次觸發展成「一筆條碼一列」/ Spread one trigger across one row per code.
    ///
    /// 為什麼不是一次觸發一列 / Why not one row per trigger:
    /// 六道並排的機構下,一次觸發讀到六筆條碼。併成一列的話「條碼」欄位是一串逗號分隔的
    /// 六個二十位數字,而等級根本擺不進去 —— 而等級正是這台讀碼器存在的理由。
    /// With six lanes, one trigger reads six codes. Folded into one row, the code cell is six twenty-digit
    /// strings joined by commas and there is nowhere to put the grades — and the grades are why this reader
    /// is here at all.
    ///
    /// 判定為什麼仍是「整次觸發」的 / Why the verdict is still the trigger's:
    /// 資料庫存的是一次觸發一筆紀錄,判定與判退原因屬於那一筆。要逐筆條碼判定,得重算每一筆
    /// 對配方的符合度,而歷史紀錄沒有存下「當時的配方」—— 重算不出來。
    /// 因此判定與判退原因只填在該次觸發的第一列,其餘列留空,並以整列同色表示它們屬於同一次觸發。
    /// 逐筆判定要做的話,是紀錄粒度的改動（一筆條碼一筆紀錄),不是顯示的改動。
    /// The database stores one record per trigger, and the verdict and reasons belong to it. A per-code
    /// verdict would mean re-evaluating each code against the recipe, and a stored record does not carry the
    /// recipe that was in force — so it cannot be recomputed. The verdict and reason therefore appear on the
    /// trigger's first row only, with the shared colour marking the rows as one trigger. Per-code verdicts
    /// would be a change of record granularity rather than of display.
    /// </summary>
    private void InsertRecordRow(InspectionRecord record, bool atTop)
    {
        // 完全沒讀到時仍要留一列 / A trigger that read nothing still leaves a row.
        //
        // 這是本機構最常見的判退:讀碼器設成「六個都讀到才輸出」,任一張不良就整批回 ERROR,
        // 於是條碼清單是空的。若「幾筆條碼幾列」照字面執行,這一次觸發會從紀錄裡整個消失 ——
        // 而那正是最需要留下痕跡的一次。
        // This is the line's commonest reject: the reader emits only when all six decode, so one bad label
        // returns ERROR for the batch and the code list is empty. Taking "one row per code" literally would
        // make that trigger vanish from the log — the very trigger that most needs a trace.
        var rows = record.CodeResults.Count == 0
            ? [BuildRow(record, position: null, code: null, grade: null, isFirst: true)]
            : record.CodeResults
                .Select((code, ordinal) => BuildRow(
                    record,
                    position: code.Index,
                    code: code.Data,
                    grade: code.Grade,
                    isFirst: ordinal == 0))
                .ToArray();

        // 由新到舊時要整組插在最前面,而組內順序不能倒過來 ——
        // 逐列 Insert(0) 會讓 #5 排在 #0 上面,而位置編號的用途就是對照實體順序。
        // Newest-first means inserting the group at the top without reversing it: inserting row by row at zero
        // would put #5 above #0, and matching the physical order is the whole point of the position column.
        for (var i = 0; i < rows.Length; i++)
        {
            if (atTop)
            {
                _recordList.Items.Insert(i, rows[i]);
            }
            else
            {
                _recordList.Items.Add(rows[i]);
            }
        }
    }

    private static ListViewItem BuildRow(
        InspectionRecord record,
        int? position,
        string? code,
        int? grade,
        bool isFirst)
    {
        var item = new ListViewItem(
        [
            // UTC 存檔、當地時間顯示：現場只看得懂當地時間
            // Stored in UTC, shown in local time — the line only reads local time.
            record.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            record.ModelName,
            position?.ToString(CultureInfo.InvariantCulture) ?? "—",
            code ?? "(無回報 none)",

            // 「沒有等級」與「等級 0」必須看得出差別:0 是 ISO 刻度上最差的合格等級,
            // 而空白表示讀碼器回報未評估。兩者判退原因不同,現場該做的事也不同。
            // "No grade" and "grade 0" have to look different: zero is the worst valid grade on the ISO scale
            // while blank means the reader reported not-evaluated. They reject for different reasons and send
            // the line to different places.
            grade?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,

            // 字符與判定屬於整次觸發,只填在第一列 / Characters and the verdict belong to the trigger.
            isFirst ? Describe(record.CharacterResults, r => r.Text, "(未辨識 no read)") : string.Empty,
            isFirst ? record.FinalJudge : string.Empty,
            isFirst ? record.RejectReason ?? string.Empty : string.Empty,
        ]);

        // 整組同色:一次觸發的判定是一個,顏色讓那幾列讀起來是一組而不是各自獨立的判定。
        // The whole group shares a colour: one trigger has one verdict, and the colour keeps its rows reading
        // as one group rather than as separate verdicts.
        item.BackColor = record.FinalJudge == Verdict.Pass ? Color.Honeydew : Color.MistyRose;
        return item;
    }

    /// <summary>
    /// 把一次觸發的多筆結果併成一格文字 / Fold one trigger's many results into a single cell.
    ///
    /// 只剩字符結果用它。條碼已經改成一筆一列,而字符仍屬於整次觸發 ——
    /// IV4 導入後若證實它也是「一次觸發多個區域」,那時要考慮的是區域也逐列展開。
    /// Only the character results still use this. Codes are one per row now, while characters still belong to
    /// the trigger; if the IV4 turns out to report many regions per trigger, spreading those across rows too
    /// becomes the next question.
    ///
    /// 空清單與「有結果但內容是空的」必須看得出差別 —— 前者是感測器什麼都沒回,
    /// 後者是回了卻解不出來,現場的排查方向完全不同。
    /// An empty list and "a result arrived but carried nothing" must look different: the first means the
    /// sensor reported nothing at all, the second that it reported and could not decode. They send the
    /// operator looking in different places.
    /// </summary>
    private static string Describe<T>(IReadOnlyList<T> results, Func<T, string?> select, string emptyValue)
        => results.Count == 0
            ? "(無回報 none)"
            : string.Join(", ", results.Select(r => select(r) ?? emptyValue));

    private void TrimRecordRows()
    {
        // 連續生產會無上限累積列,不修剪最終會吃光記憶體
        // Continuous production appends rows forever; without trimming it eventually eats all memory.
        while (_recordList.Items.Count > MaxVisibleRows)
        {
            _recordList.Items.RemoveAt(_recordList.Items.Count - 1);
        }
    }

    private void ApplyStateToCommands(MachineState state)
    {
        _initializeButton.Enabled = state == MachineState.Offline;
        _triggerButton.Enabled = state == MachineState.Idle;
        _startButton.Enabled = state == MachineState.Idle;
        _stopButton.Enabled = state == MachineState.Running;
        _resetButton.Enabled = state == MachineState.Faulted;
        // 生產中禁止改配方：換線必須先停機
        // No recipe edits while cycling: a changeover means stopping first.
        _saveRecipeButton.Enabled = state is MachineState.Offline or MachineState.Idle;
    }

    private void SetCommandsEnabled(bool enabled)
    {
        _initializeButton.Enabled = enabled;
        _triggerButton.Enabled = enabled;
        _startButton.Enabled = enabled;
        _stopButton.Enabled = enabled;
        _resetButton.Enabled = enabled;
    }

    private void AppendLog(string message)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        _logBox.AppendText(line);
    }

    private void ShowError(string caption, Exception ex)
    {
        AppendLog($"{caption}: {ex.Message}");
        MessageBox.Show(this, ex.Message, caption, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>
    /// 夾到控制項容許範圍 / Clamp into the control's own range.
    /// 手改過的配方檔可能帶著超出範圍的值,直接指派會拋 ArgumentOutOfRangeException
    /// 並讓主畫面開不起來 —— 顯示夾住的值再讓操作員自行更正比較好。
    /// A hand-edited recipe may carry an out-of-range value, and assigning it directly
    /// throws ArgumentOutOfRangeException and stops the main screen from opening.
    /// Showing the clamped value and letting the operator correct it is the kinder failure.
    /// </summary>
    private static decimal Clamp(int value, NumericUpDown box)
        => Math.Clamp(value, (int)box.Minimum, (int)box.Maximum);

    private static string StateCaption(MachineState state) => state switch
    {
        MachineState.Offline => "OFFLINE 未連線",
        MachineState.Initializing => "INIT 初始化中",
        MachineState.Idle => "IDLE 待命",
        MachineState.Running => "RUNNING 生產中",
        MachineState.Stopping => "STOPPING 停機中",
        MachineState.Faulted => "FAULT 故障",
        _ => state.ToString(),
    };

    private static Color StateColour(MachineState state) => state switch
    {
        MachineState.Running => Color.PaleGreen,
        MachineState.Faulted => Color.Salmon,
        MachineState.Initializing or MachineState.Stopping => Color.Khaki,
        MachineState.Idle => Color.LightSkyBlue,
        _ => Color.Gainsboro,
    };

    /// <summary>
    /// 把動作封送到 UI 執行緒 / Marshal an action onto the UI thread.
    /// 用 BeginInvoke（非同步）而非 Invoke：Invoke 會讓產線迴圈等 UI 畫完,
    /// UI 一卡就拖慢節拍。
    /// Uses BeginInvoke, not Invoke: Invoke makes the line loop wait for a repaint, so
    /// any UI hiccup drags out the cycle time.
    /// </summary>
    private void RunOnUi(Action action)
    {
        // 視窗尚未建立或正在關閉時,BeginInvoke 會拋例外 —— 直接丟棄該次更新
        // Before the handle exists or while closing, BeginInvoke throws; drop the update.
        if (!IsHandleCreated || IsDisposed)
        {
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (ObjectDisposedException)
        {
            // 關閉過程中的競態,可安全忽略 / A benign race during shutdown.
        }
        catch (InvalidOperationException)
        {
            // 視窗句柄已消失 / The window handle is already gone.
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // 模式擺在操作訊息區的第一行,任何判定之前 / The mode heads the log, ahead of any verdict.
        AppendDeviceModeLog();

        // 載入現有配方讓畫面與磁碟一致 / Load the on-disk recipe so the screen matches it.
        var current = _recipes.Current;
        _modelNameBox.Text = current.ModelName;
        _codeCountBox.Value = Clamp(current.ExpectedCodeCount, _codeCountBox);
        _barcodeLengthBox.Value = Clamp(current.BarcodeLength, _barcodeLengthBox);
        _regionCountBox.Value = Clamp(current.ExpectedCharacterRegionCount, _regionCountBox);
        _feedPitchBox.Value = Clamp(current.FeedPitchPulses, _feedPitchBox);

        _checkGradeBox.Checked = current.MinimumCodeGrade.HasValue;
        _minimumGradeBox.Enabled = _checkGradeBox.Checked;
        if (current.MinimumCodeGrade is int grade)
        {
            _minimumGradeBox.Value = Clamp(grade, _minimumGradeBox);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 生產中誤按關閉是產線最常見的意外停機 / Closing mid-run is the most common accidental line stop.
        if (_sequencer.State == MachineState.Running)
        {
            var answer = MessageBox.Show(
                this,
                "機台仍在生產中,確定要關閉嗎？/ The machine is still cycling. Close anyway?",
                "確認關閉 / Confirm close",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _refreshTimer.Stop();
        _sequencer.StateChanged -= OnStateChanged;
        _sequencer.CycleCompleted -= OnCycleCompleted;
        _sequencer.LogEmitted -= OnLogEmitted;

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
