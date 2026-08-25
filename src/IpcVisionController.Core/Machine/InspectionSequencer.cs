using System.Globalization;
using IpcVisionController.Core.Data;
using IpcVisionController.Core.Hal;
using IpcVisionController.Core.Models;
using IpcVisionController.Core.Recipes;

namespace IpcVisionController.Core.Machine;

/// <summary>
/// 機台狀態 / Machine state.
/// 轉移規則刻意收斂：故障後必須經過 <see cref="InspectionSequencer.Reset"/> 回到
/// <see cref="Offline"/> 並重新初始化,不允許「按一下就繼續跑」。
/// Transitions are deliberately strict: after a fault the machine must go through
/// <see cref="InspectionSequencer.Reset"/> back to <see cref="Offline"/> and be
/// re-initialised. There is no one-click "carry on regardless".
/// </summary>
public enum MachineState
{
    /// <summary>未連線 / Devices not connected.</summary>
    Offline,

    /// <summary>正在連線與復歸 / Connecting and homing.</summary>
    Initializing,

    /// <summary>待命,可開始生產 / Ready to run.</summary>
    Idle,

    /// <summary>連續生產中 / Cycling.</summary>
    Running,

    /// <summary>停機中,等待當前動作收尾 / Stopping; the in-flight step is unwinding.</summary>
    Stopping,

    /// <summary>設備故障,需人員處理 / Equipment fault; requires operator attention.</summary>
    Faulted,
}

/// <summary>設備在逾時內沒有回應 / A device did not respond within its timeout.</summary>
public sealed class DeviceTimeoutException(string message) : Exception(message);

/// <summary>狀態變更事件 / State transition notification.</summary>
public sealed class StateChangedEventArgs(MachineState previous, MachineState current, string? reason) : EventArgs
{
    public MachineState Previous { get; } = previous;

    public MachineState Current { get; } = current;

    /// <summary>故障原因；非故障轉移為 null / Fault reason; null for non-fault transitions.</summary>
    public string? Reason { get; } = reason;
}

/// <summary>單一週期完成事件 / One cycle finished.</summary>
public sealed class CycleCompletedEventArgs(InspectionRecord record) : EventArgs
{
    public InspectionRecord Record { get; } = record;
}

/// <summary>操作訊息事件 / Human-readable progress message.</summary>
public sealed class SequencerLogEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

/// <summary>
/// 檢測週期協調器 / Inspection cycle sequencer.
///
/// 職責 / Responsibilities:
/// - 以狀態機管制「什麼時候可以下什麼命令」。
///   Gate which commands are legal in which state.
/// - 每個裝置動作都套用個別逾時,現場設備沒回應時不會無限等待。
///   Apply a per-step timeout to every device call, so an unresponsive device never hangs the line.
/// - 區分「工件不良」與「設備故障」：前者判 FAIL 後繼續生產,後者停線。
///   Separate a bad part from a broken machine: reject and continue for the former, stop the line for the latter.
///
/// 執行緒 / Threading:
/// 事件在背景執行緒上引發。UI 端必須自行封送（WinForms：Control.BeginInvoke）。
/// Events are raised on background threads; the UI must marshal them itself
/// (WinForms: Control.BeginInvoke).
///
/// 所有權 / Ownership:
/// 本類別不擁有裝置與資料庫,不會處置它們;僅處置自己的取消權杖來源。
/// This class does not own the devices or the database and will not dispose them;
/// it disposes only its own cancellation token source.
/// </summary>
public sealed class InspectionSequencer : IAsyncDisposable
{
    /// <summary>故障後的安全停止動作逾時 / Timeout for the safety stop issued after a fault.</summary>
    private static readonly TimeSpan SafetyStopTimeout = TimeSpan.FromSeconds(3);

    private readonly IMotorController _motor;
    private readonly ICodeReader _codeReader;
    private readonly ICharacterVerifier _verifier;
    private readonly IInspectionStore _database;
    private readonly RecipeManager _recipes;
    private readonly SequencerOptions _options;

    private readonly object _stateGate = new();
    private MachineState _state = MachineState.Offline;

    /// <summary>
    /// 已掃碼、還沒走到字符檢測站的標籤 / Labels scanned but not yet at the verification station.
    ///
    /// 兩站相隔 k 格,所以讀碼站看到的第 N 張要等 k 次進給才抵達檢測站。這個佇列就是那段距離
    /// 在程式裡的樣子:進給後把本次的讀碼結果推入,再從另一端取出 k 次進給前的那一張,
    /// 與檢測站此刻回報的結果配成一筆紀錄。
    /// The two stations are k pitches apart, so the label the reader sees as N needs k feeds to reach
    /// the verifier. This queue is what that distance looks like in code: after each feed the fresh
    /// code results go in one end, and the label from k feeds ago comes out the other to be paired
    /// with what the verifier reports now.
    ///
    /// 為什麼連配方一起存 / Why the recipe is stored alongside:
    /// 配方是每張標籤重讀一次的,好讓換線後的下一張立即生效。但換線那一刻,佇列裡的標籤屬於
    /// 舊機種 —— 它們是在舊配方下被掃的,就該用舊配方判定。存下當時的配方,換線才不會把
    /// 在製品用新規格判退。
    /// The recipe is re-read per label so a changeover takes effect on the very next one. At the moment
    /// of a changeover, though, the labels in this queue belong to the outgoing product: they were
    /// scanned under the old recipe and must be judged by it. Capturing the recipe with the label is
    /// what stops a changeover from rejecting work in progress against the incoming specification.
    ///
    /// 執行緒 / Threading:
    /// 只由產線工作（RunLoopAsync／RunSingleCycleAsync）觸碰,或在該工作已結束後由 StopAsync
    /// 觸碰。狀態機保證不會有兩個週期同時進行,因此不需要額外上鎖。
    /// Touched only by the line task, or by StopAsync after that task has completed. The state machine
    /// guarantees no two cycles overlap, so no further locking is needed.
    /// </summary>
    private readonly Queue<PendingLabel> _inFlight = new();

    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private bool _disposed;

    /// <summary>
    /// 在製品:已掃碼、等著抵達字符檢測站的一張標籤 /
    /// Work in progress: one label scanned and awaiting the verification station.
    /// </summary>
    private sealed record PendingLabel(RecipeModel Recipe, IReadOnlyList<CodeResult> Codes);

    public InspectionSequencer(
        IMotorController motor,
        ICodeReader codeReader,
        ICharacterVerifier verifier,
        IInspectionStore database,
        RecipeManager recipes,
        SequencerOptions? options = null)
    {
        _motor = motor ?? throw new ArgumentNullException(nameof(motor));
        _codeReader = codeReader ?? throw new ArgumentNullException(nameof(codeReader));
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _recipes = recipes ?? throw new ArgumentNullException(nameof(recipes));

        _options = options ?? new SequencerOptions();
        _options.Validate();
    }

    /// <summary>目前狀態 / Current state.</summary>
    public MachineState State
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    /// <summary>最近一次故障原因；無故障為 null / Last fault reason, or null.</summary>
    public string? FaultReason { get; private set; }

    /// <summary>本次啟動後完成的週期數 / Cycles completed since construction.</summary>
    public int CycleCount { get; private set; }

    public event EventHandler<StateChangedEventArgs>? StateChanged;

    public event EventHandler<CycleCompletedEventArgs>? CycleCompleted;

    public event EventHandler<SequencerLogEventArgs>? LogEmitted;

    /// <summary>
    /// 連線、致能、復歸 / Connect, enable, home.
    /// 任一步失敗即進入 <see cref="MachineState.Faulted"/>,並向外拋出原始例外。
    /// Any failure lands in <see cref="MachineState.Faulted"/> and rethrows the original exception.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        TransitionTo(MachineState.Initializing, from: MachineState.Offline);

        try
        {
            // 上一輪留下的在製品一律作廢:初始化包含重新對標,而料帶在停機期間多半被人手拉動過,
            // 佇列裡那些標籤現在究竟停在哪一格已經不可信。
            // Any work in progress from the previous run is void: initialising includes re-aligning, the
            // web has almost certainly been pulled by hand while stopped, and where those queued labels
            // now sit can no longer be trusted.
            _inFlight.Clear();

            Log("連線中 / Connecting devices…");
            await WithTimeoutAsync(
                $"{_motor.Name} 連線 / connect", _options.ConnectTimeout,
                _motor.ConnectAsync, cancellationToken).ConfigureAwait(false);
            await WithTimeoutAsync(
                $"{_codeReader.Name} 連線 / connect", _options.ConnectTimeout,
                _codeReader.ConnectAsync, cancellationToken).ConfigureAwait(false);
            await WithTimeoutAsync(
                $"{_verifier.Name} 連線 / connect", _options.ConnectTimeout,
                _verifier.ConnectAsync, cancellationToken).ConfigureAwait(false);

            Log("伺服致能 / Enabling servo…");
            await WithTimeoutAsync(
                "伺服致能 / servo enable", _options.ConnectTimeout,
                _motor.EnableAsync, cancellationToken).ConfigureAwait(false);

            Log("對齊定位標記 / Aligning to the registration mark…");
            await WithTimeoutAsync(
                "對標 / registration align", _options.FeedTimeout,
                _motor.HomeAsync, cancellationToken).ConfigureAwait(false);

            await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var recipe = await _recipes.LoadAsync(cancellationToken).ConfigureAwait(false);
            Log(string.Create(CultureInfo.InvariantCulture,
                $"配方載入 / Recipe loaded: {recipe.ModelName} ({recipe.ExpectedCodeCount} code(s), {recipe.ExpectedCharacterRegionCount} region(s))"));

            // 兩站相隔格數要說出來。它決定「前幾張標籤只掃碼、不留紀錄」這個現場一定會看到、
            // 卻最容易被當成程式壞掉的行為。
            // The station offset is stated out loud. It governs the behaviour the line is certain to
            // notice and most likely to read as a broken program: the first few labels are scanned and
            // leave no record.
            var offset = _options.InspectionOffsetPitches.ToString(CultureInfo.InvariantCulture);
            Log(_options.InspectionOffsetPitches == 0
                ? "讀碼與字符檢測同一位置,每張標籤當次判定 / Reading and verification share one position; "
                    + "each label is judged in its own cycle."
                : $"檢測站在讀碼站下游 {offset} 格,因此前 {offset} 張標籤只掃碼、尚不會產生紀錄"
                    + $" / the verification station is {offset} pitches downstream, so the first "
                    + $"{offset} labels are scanned and produce no record yet.");

            TransitionTo(MachineState.Idle, from: MachineState.Initializing);
            Log("待命 / Ready.");
        }
        catch (Exception ex)
        {
            await FaultAsync($"初始化失敗 / Initialisation failed: {ex.Message}").ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 開始連續生產 / Begin cycling.
    /// 不等待任何動作,僅啟動背景迴圈,因此不是 async。
    /// Starts the background loop and returns; it awaits nothing, hence not async.
    /// </summary>
    public void Start()
    {
        TransitionTo(MachineState.Running, from: MachineState.Idle);

        // 前一輪的權杖來源必須釋放,否則每次啟動都漏一個計時器
        // Release the previous run's token source, or every start leaks a timer.
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        _runTask = RunLoopAsync(_runCts.Token);
    }

    /// <summary>
    /// 停止生產 / Stop cycling.
    /// 取消進行中的動作並下停止命令；進行中的工件不會產生紀錄
    /// （未完成判定的工件不應留下追溯資料）。
    /// Cancels the in-flight step and commands a stop. The part in progress produces
    /// no record — an unjudged part must not leave traceability data behind.
    /// </summary>
    public async Task StopAsync()
    {
        lock (_stateGate)
        {
            if (_state != MachineState.Running)
            {
                return;
            }
        }

        TransitionTo(MachineState.Stopping, from: MachineState.Running);
        Log("停機中 / Stopping…");

        if (_runCts is not null)
        {
            await _runCts.CancelAsync().ConfigureAwait(false);
        }

        if (_runTask is not null)
        {
            // RunLoopAsync 自行吸收取消例外,此處不需再包 try
            // RunLoopAsync swallows its own cancellation, so no try is needed here.
            await _runTask.ConfigureAwait(false);
        }

        await SafeStopMotorAsync().ConfigureAwait(false);

        // 產線工作已經結束,此時觸碰佇列是安全的
        // The line task has finished, so touching the queue here is safe.
        DiscardInFlight();

        // 故障期間收到停機命令時,故障狀態優先保留
        // A fault raised during the stop wins: do not paper over it with Idle.
        lock (_stateGate)
        {
            if (_state == MachineState.Faulted)
            {
                return;
            }
        }

        TransitionTo(MachineState.Idle, from: MachineState.Stopping);
        Log("已停機 / Stopped.");
    }

    /// <summary>
    /// 解除故障 / Clear a fault.
    /// 回到 <see cref="MachineState.Offline"/> 而非 Idle：故障後裝置狀態與料帶對位都不可信
    /// —— 卡料排除時料帶多半已被人手拉動,必須重新初始化並重新對標。
    /// Returns to <see cref="MachineState.Offline"/> rather than Idle: after a fault
    /// neither the device links nor the web's registration can be trusted — clearing a
    /// jam almost always means the web was pulled by hand — so a full re-initialise and
    /// re-align is mandatory.
    /// </summary>
    public void Reset()
    {
        TransitionTo(MachineState.Offline, from: MachineState.Faulted);
        FaultReason = null;
        Log("故障已解除,請重新初始化 / Fault cleared; re-initialise before running.");
    }

    /// <summary>
    /// 執行單一週期後回到待命 / Run exactly one cycle, then return to Idle.
    ///
    /// 試機與印刷調機用：跑一格就停,操作員能逐格核對判定與判退原因。
    /// For dry runs and dialling in a print job: one pitch per press, so each verdict and
    /// reject reason can be checked before the next label moves.
    ///
    /// 為什麼不讓呼叫端直接用 <see cref="RunCycleAsync"/> /
    /// Why callers must not simply call <see cref="RunCycleAsync"/>:
    /// 後者不看狀態也不互斥,連按兩下就會有兩個週期重疊,對同一顆馬達連下兩次進給命令
    /// —— 料帶多送一格,而那一格從未被檢測就流到下游。此處先佔住 Running,第二次呼叫
    /// 因狀態不符而被擋下,互斥由狀態機本身提供,不另設旗標。
    /// RunCycleAsync neither checks the state nor excludes concurrent callers, so a
    /// double-click overlaps two cycles and issues two feeds to one motor: the web
    /// advances a pitch that is never inspected and still reaches the next station.
    /// Claiming Running first makes the second call fail the state check, so mutual
    /// exclusion comes from the state machine itself rather than a separate flag.
    /// </summary>
    /// <returns>
    /// 本次的檢測紀錄；期間被停機或故障中斷則為 null /
    /// This cycle's record, or null when a stop or a fault intervened.
    /// </returns>
    public async Task<InspectionRecord?> TriggerOnceAsync(CancellationToken cancellationToken = default)
    {
        TransitionTo(MachineState.Running, from: MachineState.Idle);

        // 與連續生產共用 _runCts/_runTask,讓 StopAsync 能一視同仁地中止單次觸發
        // Share _runCts/_runTask with cycling so StopAsync cancels a single shot too.
        _runCts?.Dispose();
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var task = RunSingleCycleAsync(_runCts.Token);
        _runTask = task;
        return await task.ConfigureAwait(false);
    }

    private async Task<InspectionRecord?> RunSingleCycleAsync(CancellationToken cancellationToken)
    {
        try
        {
            var record = await RunCycleAsync(cancellationToken).ConfigureAwait(false);

            // 用 Try 而非直接轉移：期間若已被 StopAsync 轉為 Stopping、或故障轉為 Faulted,
            // 這裡不得把狀態搶回 Idle 蓋掉它們 —— 那會讓故障機台看起來可以繼續生產。
            // Try rather than a plain transition: if StopAsync moved us to Stopping, or a
            // fault to Faulted, this must not snatch the state back to Idle over them —
            // that would make a faulted machine look ready to run.
            TryTransitionTo(MachineState.Idle, from: MachineState.Running);
            return record;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 操作員停機,狀態由 StopAsync 收尾 / Operator stop; StopAsync settles the state.
            return null;
        }
        catch (Exception ex)
        {
            await FaultAsync($"{ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// 執行單一檢測週期 / Run exactly one inspection cycle.
    /// 供背景迴圈與單元測試共用 —— 測試打的是產線真正跑的那段程式碼。
    /// Shared by the background loop and the unit tests, so tests exercise the same
    /// code path the line runs.
    /// </summary>
    /// <returns>
    /// 本週期完成判定的那一張標籤的紀錄；標籤還在兩站之間前進、尚無任何一張可判定時為 null /
    /// The record for the label judged this cycle, or null while labels are still advancing between
    /// the two stations and none can be judged yet.
    /// </returns>
    public async Task<InspectionRecord?> RunCycleAsync(CancellationToken cancellationToken)
    {
        // 每張標籤都重讀配方：換線後下一張立即生效
        // Re-read the recipe per label, so a changeover takes effect on the very next one.
        var recipe = _recipes.Current;

        await WithTimeoutAsync(
            "進給一格 / feed one pitch", _options.FeedTimeout,
            token => _motor.FeedAsync(_options.FeedPitchPulses, _options.FeedSpeedPulsePerSecond, token),
            cancellationToken).ConfigureAwait(false);

        // 讀碼站看的是剛進站的這一張 / The reading station sees the label that just arrived.
        //
        // 兩個感測器都要觸發,不因其中一個先判退就略過另一個:少一組結果就少一半的判退依據,
        // 而印刷調機時那正是操作員最需要看到的東西。
        // Both sensors fire; neither is skipped because the other already found a reason to reject.
        // Dropping one result set halves the evidence behind the reject, which is exactly what an
        // operator dialling in a print job needs.
        var codeResults = await WithTimeoutAsync(
            "讀碼 / code read", _options.CodeReadTimeout,
            _codeReader.TriggerAsync, cancellationToken).ConfigureAwait(false);

        // 連同當時的配方一起推入在製品佇列 / Queue it with the recipe in force at scan time.
        _inFlight.Enqueue(new PendingLabel(recipe, codeResults));

        // 佇列還沒填滿相隔的格數,代表檢測站底下還沒有任何「已掃碼」的標籤。
        // 此時刻意不觸發字符檢測:那裡躺著的是開機前就已經越過讀碼站的標籤,
        // 拍它只會拍到一個無法歸屬的結果,而把無法歸屬的結果寫進追溯資料,
        // 比完全沒有紀錄糟得多。
        // The queue has not yet spanned the offset, so no scanned label has reached the verification
        // station. The verifier is deliberately not triggered: what lies under it passed the reading
        // station before this run began, and capturing it would produce a result belonging to no
        // known label — and an unattributable row in the traceability data is far worse than no row.
        if (_inFlight.Count <= _options.InspectionOffsetPitches)
        {
            var filled = _inFlight.Count.ToString(CultureInfo.InvariantCulture);
            var span = _options.InspectionOffsetPitches.ToString(CultureInfo.InvariantCulture);
            Log($"標籤前進中,尚未抵達檢測站（{filled}/{span} 格）"
                + $" / label advancing, not yet at the verification station ({filled}/{span} pitches).");
            return null;
        }

        // 檢測站底下的那一張,是相隔格數次進給之前掃到的
        // The label under the verification station is the one scanned that many feeds ago.
        var pending = _inFlight.Dequeue();

        var characterResults = await WithTimeoutAsync(
            "字符檢測 / character verify", _options.CharacterVerifyTimeout,
            _verifier.TriggerAsync, cancellationToken).ConfigureAwait(false);

        // DeviceFaultException 刻意不在此攔截：那是設備層級異常,必須一路上拋到
        // RunLoopAsync 讓機台停線。工件不良則完全由 LabelJudge 以結果內容表達。
        // DeviceFaultException is deliberately not caught here: it is an equipment-level
        // fault and must propagate to RunLoopAsync and stop the line. A bad label is
        // expressed purely through the result contents, which LabelJudge reads.
        //
        // 判定用的是 pending 裡的配方與讀碼結果,不是本迴圈開頭那個 recipe ——
        // 兩者在相隔格數大於 0 時屬於不同的標籤。
        // The verdict uses the recipe and codes held in pending, not the recipe read at the top of this
        // cycle: with a non-zero offset those belong to two different labels.
        var rejectReason = LabelJudge.Evaluate(pending.Recipe, pending.Codes, characterResults);
        var judge = rejectReason is null ? Verdict.Pass : Verdict.Fail;

        if (rejectReason is not null)
        {
            Log($"判退 / Rejected: {rejectReason}");
        }

        var record = new InspectionRecord(
            Timestamp: DateTime.UtcNow,
            ModelName: pending.Recipe.ModelName,
            FinalJudge: judge,
            CodeResults: pending.Codes,
            CharacterResults: characterResults,
            RejectReason: rejectReason);

        // 寫入失敗即失去追溯性,必須停線 —— 由呼叫端的故障處理接手
        // A failed insert means traceability is lost, which must stop the line; the
        // caller's fault handling takes it from here.
        await _database.InsertAsync(record, cancellationToken).ConfigureAwait(false);

        CycleCount++;
        Raise(CycleCompleted, new CycleCompletedEventArgs(record));
        return record;
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await RunCycleAsync(cancellationToken).ConfigureAwait(false);

                if (_options.CycleInterval > TimeSpan.Zero)
                {
                    // 實機此處應改為等待上游「工件到位」訊號
                    // On real hardware, wait on the upstream part-present signal instead.
                    await Task.Delay(_options.CycleInterval, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 操作員停機,正常結束 / Operator stop: a normal exit.
        }
        catch (Exception ex)
        {
            await FaultAsync($"{ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 套用單步逾時 / Apply a per-step timeout.
    ///
    /// 關鍵：連結權杖被取消時,必須分辨「操作員停機」與「設備逾時」。
    /// 兩者都是 OperationCanceledException,只能靠外層權杖的狀態判斷。
    /// 混為一談的後果是設備故障被當成正常停機而靜默吞掉。
    /// The subtle part: when the linked token trips, an operator stop and a device
    /// timeout are both an OperationCanceledException. Only the outer token's state
    /// tells them apart — conflating them silently swallows equipment faults as
    /// normal stops.
    /// </summary>
    private static async Task<T> WithTimeoutAsync<T>(
        string step,
        TimeSpan timeout,
        Func<CancellationToken, Task<T>> action,
        CancellationToken outerToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        linked.CancelAfter(timeout);

        try
        {
            return await action(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!outerToken.IsCancellationRequested)
        {
            throw new DeviceTimeoutException(string.Create(CultureInfo.InvariantCulture,
                $"{step} 逾時 {timeout.TotalSeconds:0.##} 秒 / timed out after {timeout.TotalSeconds:0.##}s."));
        }
    }

    private static async Task WithTimeoutAsync(
        string step,
        TimeSpan timeout,
        Func<CancellationToken, Task> action,
        CancellationToken outerToken)
    {
        await WithTimeoutAsync<bool>(
            step, timeout,
            async token =>
            {
                await action(token).ConfigureAwait(false);
                return true;
            },
            outerToken).ConfigureAwait(false);
    }

    private async Task FaultAsync(string reason)
    {
        FaultReason = reason;

        MachineState previous;
        lock (_stateGate)
        {
            previous = _state;
            _state = MachineState.Faulted;
        }

        // 在鎖外引發事件：訂閱者可能回查 State 或跨執行緒封送,持鎖通知是死鎖來源
        // Raise outside the lock: a subscriber may read State back or marshal across
        // threads, and notifying while holding the lock is a deadlock waiting to happen.
        Raise(StateChanged, new StateChangedEventArgs(previous, MachineState.Faulted, reason));
        Log($"故障 / FAULT: {reason}");
        DiscardInFlight();
        await SafeStopMotorAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 作廢兩站之間的在製品,並說出有幾張 / Void the work in progress between the stations, and say how many.
    ///
    /// 不留紀錄的理由與既有規則一致:未完成判定的工件不該留下追溯資料。
    /// 但「不留紀錄」不等於「不用說」—— 那幾張標籤已經實際越過讀碼站,現在停在兩站之間,
    /// 它們既沒有被判定,也不會有任何紀錄提到它們存在過。現場必須知道有幾張,才有機會把它們
    /// 挑出來重驗。少了這一句,它們會安靜地流到下游。
    /// The no-record rule matches the existing one: a part whose verdict never completed leaves no
    /// traceability data. But leaving no record is not the same as saying nothing. Those labels did pass
    /// the reading station and are now sitting between the two, unjudged, with nothing anywhere
    /// recording that they existed. The line has to be told how many there are to have any chance of
    /// pulling them for re-inspection; without this line they travel downstream in silence.
    /// </summary>
    private void DiscardInFlight()
    {
        if (_inFlight.Count == 0)
        {
            return;
        }

        var abandoned = _inFlight.Count.ToString(CultureInfo.InvariantCulture);
        _inFlight.Clear();

        Log($"兩站之間有 {abandoned} 張標籤已掃碼但未完成檢測,不留紀錄 —— 請自兩站之間取出重驗"
            + $" / {abandoned} label(s) between the stations were scanned but never verified and leave "
            + "no record; pull them from between the stations for re-inspection.");
    }

    /// <summary>
    /// 盡力停止軸動作 / Best-effort axis stop.
    /// 用全新的權杖：導致故障的權杖可能已被取消,拿它下停止命令等於沒下。
    /// Uses a fresh token: the token that led here may already be cancelled, and a
    /// stop command on a cancelled token is no stop at all.
    /// </summary>
    private async Task SafeStopMotorAsync()
    {
        using var cts = new CancellationTokenSource(SafetyStopTimeout);
        try
        {
            await _motor.StopAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 已在故障處理路徑上,再拋例外只會遮蓋原始原因
            // Already on the fault path; rethrowing here would mask the original cause.
            Log($"停止命令失敗 / Stop command failed: {ex.Message}");
        }
    }

    private void TransitionTo(MachineState target, MachineState from)
    {
        if (!TryTransitionTo(target, from))
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"狀態不允許此操作：目前 {State},需為 {from} / Illegal transition: state is {State}, requires {from}."));
        }
    }

    /// <summary>
    /// 嘗試轉移狀態,不合法時回報失敗而非拋例外 /
    /// Attempt a transition, reporting failure instead of throwing.
    ///
    /// 檢查與寫入必須在同一個鎖內：分成兩段的話兩個呼叫端會同時通過檢查,
    /// 互斥就失效了 —— 這正是單次觸發用來擋連按的機制。
    /// The check and the write must share one lock. Split across two, two callers both
    /// pass the check and the exclusion is gone — and that exclusion is exactly what
    /// stops a double-press from running two cycles at once.
    /// </summary>
    private bool TryTransitionTo(MachineState target, MachineState from)
    {
        MachineState previous;
        lock (_stateGate)
        {
            if (_state != from)
            {
                return false;
            }

            previous = _state;
            _state = target;
        }

        Raise(StateChanged, new StateChangedEventArgs(previous, target, reason: null));
        return true;
    }

    private void Log(string message) => Raise(LogEmitted, new SequencerLogEventArgs(message));

    /// <summary>
    /// 引發事件 / Raise an event.
    /// 訂閱者（UI）拋出的例外絕不能停掉產線迴圈。
    /// An exception thrown by a subscriber (the UI) must never take the line loop down.
    /// </summary>
    private void Raise<TArgs>(EventHandler<TArgs>? handler, TArgs args)
        where TArgs : EventArgs
    {
        try
        {
            handler?.Invoke(this, args);
        }
        catch (Exception)
        {
            // 刻意吞掉：UI 的錯不該讓機台停下 / Deliberately swallowed: a UI bug must not stop the machine.
        }
    }

    /// <summary>
    /// 處置 / Dispose.
    /// 必須可重複呼叫：`await using` 與明確呼叫併存時會處置兩次,
    /// 而對已處置的 CancellationTokenSource 下取消會拋 ObjectDisposedException。
    /// Must be idempotent: an explicit call alongside `await using` disposes twice, and
    /// cancelling an already-disposed CancellationTokenSource throws ObjectDisposedException.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_runCts is not null)
        {
            await _runCts.CancelAsync().ConfigureAwait(false);
        }

        if (_runTask is not null)
        {
            try
            {
                await _runTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 處置期間的例外無處可報 / Nothing useful to do with an exception during disposal.
            }
        }

        _runCts?.Dispose();
    }
}
