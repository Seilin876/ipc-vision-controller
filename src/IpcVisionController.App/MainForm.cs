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

    /// <summary>畫面保留的紀錄筆數 / Rows retained on screen.</summary>
    private const int MaxVisibleRows = 200;

    private readonly InspectionSequencer _sequencer;
    private readonly RecipeManager _recipes;
    private readonly IMotorController _motor;
    private readonly DatabaseManager _database;

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
        Font = new Font("Consolas", 11F),
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

    private readonly NumericUpDown _minimumGradeBox = new()
    {
        Minimum = 0, Maximum = 100, Value = 70, Width = 60, Enabled = false,
    };

    private readonly NumericUpDown _regionCountBox = new() { Minimum = 1, Maximum = 10, Value = 1, Width = 60 };
    private readonly Button _saveRecipeButton = new() { Text = "儲存配方 Save", Width = 130, Height = 30 };

    private readonly ListView _recordList = new()
    {
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9F),
    };

    private readonly TextBox _logBox = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9F),
    };

    private readonly System.Windows.Forms.Timer _refreshTimer = new();

    private int _passCount;
    private int _failCount;

    public MainForm(
        InspectionSequencer sequencer,
        RecipeManager recipes,
        IMotorController motor,
        DatabaseManager database)
    {
        _sequencer = sequencer ?? throw new ArgumentNullException(nameof(sequencer));
        _recipes = recipes ?? throw new ArgumentNullException(nameof(recipes));
        _motor = motor ?? throw new ArgumentNullException(nameof(motor));
        _database = database ?? throw new ArgumentNullException(nameof(database));

        Text = "IPC Vision Controller";
        Icon = LoadWindowIcon();
        MinimumSize = new Size(900, 640);
        StartPosition = FormStartPosition.CenterScreen;

        BuildLayout();
        WireEvents();
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
        _recordList.Columns.Add("條碼 Codes", 190);
        _recordList.Columns.Add("字符 Characters", 140);
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
            new Label { Text = "字符區域 Regions:", AutoSize = true, Padding = new Padding(12, 8, 0, 0) },
            _regionCountBox,
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
            };

            await _recipes.SaveAsync(recipe).ConfigureAwait(true);
            AppendLog(string.Create(CultureInfo.InvariantCulture,
                $"配方已儲存 / Recipe saved: {recipe.ModelName} ({recipe.ExpectedCodeCount} code(s), length {recipe.BarcodeLength}, grade ≥ {recipe.MinimumCodeGrade?.ToString(CultureInfo.InvariantCulture) ?? "off"}, {recipe.ExpectedCharacterRegionCount} region(s))"));
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

        var recent = await _database.GetRecentAsync(MaxVisibleRows).ConfigureAwait(true);

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

    private void InsertRecordRow(InspectionRecord record, bool atTop)
    {
        var item = new ListViewItem(
        [
            // UTC 存檔、當地時間顯示：現場只看得懂當地時間
            // Stored in UTC, shown in local time — the line only reads local time.
            record.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            record.ModelName,
            Describe(record.CodeResults, r => r.Data, "(NOREAD)"),
            Describe(record.CharacterResults, r => r.Text, "(未辨識 no read)"),
            record.FinalJudge,
            record.RejectReason ?? string.Empty,
        ]);

        item.BackColor = record.FinalJudge == Verdict.Pass ? Color.Honeydew : Color.MistyRose;

        if (atTop)
        {
            _recordList.Items.Insert(0, item);
        }
        else
        {
            _recordList.Items.Add(item);
        }
    }

    /// <summary>
    /// 把一次觸發的多筆結果併成一格文字 / Fold one trigger's many results into a single cell.
    /// 空清單與「有結果但內容是空的」必須看得出差別 —— 前者是感測器什麼都沒回,
    /// 後者是回了卻解不出來,現場的排查方向完全不同。
    /// An empty list and "a result arrived but carried nothing" must look different: the
    /// first means the sensor reported nothing at all, the second that it reported and
    /// could not decode. They send the operator looking in different places.
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

        // 載入現有配方讓畫面與磁碟一致 / Load the on-disk recipe so the screen matches it.
        var current = _recipes.Current;
        _modelNameBox.Text = current.ModelName;
        _codeCountBox.Value = Clamp(current.ExpectedCodeCount, _codeCountBox);
        _barcodeLengthBox.Value = Clamp(current.BarcodeLength, _barcodeLengthBox);
        _regionCountBox.Value = Clamp(current.ExpectedCharacterRegionCount, _regionCountBox);

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
