using IpcVisionController.Core.Data;
using IpcVisionController.Core.Hal;
using IpcVisionController.Core.Machine;
using IpcVisionController.Core.Models;
using IpcVisionController.Core.Recipes;

namespace IpcVisionController.Core.Tests;

/// <summary>
/// 檢測週期協調器的行為規格 / Behavioural spec for the inspection sequencer.
///
/// 這裡守的是三條安全需求 / Three safety requirements are guarded here:
/// 1. 工件不良 ≠ 設備故障。NOREAD 判退後繼續生產,設備沒回應則停線。
///    A bad part is not a broken machine: reject and carry on for the former, stop for the latter.
/// 2. 操作員停機與設備逾時都是 OperationCanceledException,絕不可混為一談,
///    否則設備故障會被當成正常停機靜默吞掉。
///    An operator stop and a device timeout are both an OperationCanceledException; conflating
///    them silently swallows equipment faults as normal stops.
/// 3. 寫不進追溯資料庫就等於失去追溯性,必須停線。
///    Losing the traceability write means losing traceability, which must stop the line.
///
/// 時序 / Timing: 測試刻意壓縮行程與延遲（見 <see cref="FastOptions"/>）,
/// 讓整份測試在數秒內跑完,同時仍然走真正的非同步路徑。
/// Strokes and latencies are deliberately compressed so the whole file runs in seconds
/// while still exercising the real asynchronous code path.
/// </summary>
public sealed class InspectionSequencerTests : IDisposable
{
    /// <summary>可忽略的模擬延遲 / A simulated latency small enough to ignore.</summary>
    private static readonly TimeSpan Instant = TimeSpan.FromMilliseconds(1);

    /// <summary>「久到一定會被上層逾時攔下」的延遲 / Long enough that the caller's timeout must trip first.</summary>
    private static readonly TimeSpan Forever = TimeSpan.FromSeconds(30);

    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    // ── 狀態機 / State machine ───────────────────────────────────────────────

    [Fact]
    public async Task NewSequencer_IsOfflineWithNoFault()
    {
        await using var rig = NewRig();

        Assert.Equal(MachineState.Offline, rig.Sequencer.State);
        Assert.Null(rig.Sequencer.FaultReason);
        Assert.Equal(0, rig.Sequencer.CycleCount);
    }

    [Fact]
    public async Task InitializeAsync_WalksOfflineThroughInitializingToIdle()
    {
        await using var rig = NewRig();

        await rig.Sequencer.InitializeAsync();

        Assert.Equal(MachineState.Idle, rig.Sequencer.State);
        Assert.Equal(
            new[] { MachineState.Initializing, MachineState.Idle },
            rig.States());
    }

    [Fact]
    public async Task InitializeAsync_PreparesTheStoreAndTheRecipe()
    {
        await using var rig = NewRig();

        await rig.Sequencer.InitializeAsync();

        // 沒有建表就開始生產,第一個工件的紀錄就會掉
        // Cycling before the table exists loses the very first part's record.
        Assert.Equal(1, rig.Store.InitializeCount);
        Assert.True(File.Exists(rig.Recipes.FilePath));
        Assert.Contains(rig.Logs(), line => line.Contains("Recipe loaded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InitializeAsync_LeavesTheAxisHomedAndTheServoEnabled()
    {
        await using var rig = NewRig();

        await rig.Sequencer.InitializeAsync();

        Assert.True(rig.Motor.IsEnabled);
        Assert.Equal(0, rig.Motor.CurrentPosition);
        Assert.True(rig.Scanner.IsConnected);
        Assert.True(rig.Vision.IsConnected);
    }

    [Fact]
    public async Task InitializeAsync_WhenAlreadyIdle_IsRejected()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();

        // 重複初始化會在生產中重新復歸,軸會意外跑掉
        // Re-initialising mid-shift would re-home the axis and move it unexpectedly.
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Sequencer.InitializeAsync());
    }

    [Fact]
    public async Task Start_BeforeInitialize_IsRejected()
    {
        await using var rig = NewRig();

        // 未復歸就下命令,絕對位置沒有意義 / Absolute moves are meaningless before homing.
        Assert.Throws<InvalidOperationException>(rig.Sequencer.Start);
    }

    [Fact]
    public async Task Reset_WhenNotFaulted_IsRejected()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();

        Assert.Throws<InvalidOperationException>(rig.Sequencer.Reset);
    }

    // ── 設備故障 / Equipment faults ──────────────────────────────────────────

    [Fact]
    public async Task InitializeAsync_WhenAConnectExceedsItsTimeout_FaultsAndRethrows()
    {
        await using var rig = NewRig(
            FastOptions(connectTimeout: TimeSpan.FromMilliseconds(80)),
            motorConnectLatency: Forever);

        await Assert.ThrowsAsync<DeviceTimeoutException>(() => rig.Sequencer.InitializeAsync());

        Assert.Equal(MachineState.Faulted, rig.Sequencer.State);
        Assert.NotNull(rig.Sequencer.FaultReason);
        Assert.Contains("初始化失敗", rig.Sequencer.FaultReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_WhenHomingNeverCompletes_FaultsOnTheMoveTimeout()
    {
        await using var rig = NewRig(FastOptions(moveTimeout: TimeSpan.FromMilliseconds(120)));

        await FaultViaStalledHomingAsync(rig);

        Assert.Equal(MachineState.Faulted, rig.Sequencer.State);
        Assert.NotNull(rig.Sequencer.FaultReason);
        Assert.Contains("初始化失敗", rig.Sequencer.FaultReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reset_AfterAFault_ReturnsToOfflineAndClearsTheReason()
    {
        await using var rig = NewRig(FastOptions(moveTimeout: TimeSpan.FromMilliseconds(120)));
        await FaultViaStalledHomingAsync(rig);

        rig.Sequencer.Reset();

        // 回 Offline 而非 Idle：故障後軸位置不可信,必須重新復歸
        // Offline, not Idle: after a fault the axis position cannot be trusted.
        Assert.Equal(MachineState.Offline, rig.Sequencer.State);
        Assert.Null(rig.Sequencer.FaultReason);
    }

    [Fact]
    public async Task Reset_AfterAFault_AllowsAFullReinitialise()
    {
        await using var rig = NewRig(FastOptions(moveTimeout: TimeSpan.FromSeconds(5)));
        await FaultViaStalledHomingAsync(rig, homeTimeout: TimeSpan.FromMilliseconds(120));
        rig.Sequencer.Reset();

        // 現場排除卡料後,軸恢復動作 / The jam is cleared on the line and the axis moves again.
        rig.Motor.SimulateStall = false;

        await rig.Sequencer.InitializeAsync();

        Assert.Equal(MachineState.Idle, rig.Sequencer.State);
        Assert.Equal(0, rig.Motor.CurrentPosition);
    }

    // ── 單一週期的判定 / Single-cycle verdicts ───────────────────────────────

    [Fact]
    public async Task RunCycleAsync_WithAGoodPart_RecordsPass()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Scanner.ForcedCode = "ABC123456789";
        rig.Vision.ForcedResult = VisionResult.Ok;

        var record = await rig.Sequencer.RunCycleAsync(CancellationToken.None);

        Assert.Equal(Verdict.Pass, record.FinalJudge);
        Assert.Equal("ABC123456789", record.BarcodeData);
        Assert.Equal(VisionResult.Ok, record.Iv4Result);
        Assert.Equal("DEFAULT", record.ModelName);
        Assert.Equal(DateTimeKind.Utc, record.Timestamp.Kind);
        Assert.Single(rig.Store.Snapshot());
    }

    [Fact]
    public async Task RunCycleAsync_WithAnNgVerdict_RecordsFailButKeepsTheBarcode()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Scanner.ForcedCode = "ABC123456789";
        rig.Vision.ForcedResult = VisionResult.Ng;

        var record = await rig.Sequencer.RunCycleAsync(CancellationToken.None);

        // NG 品也要留下條碼,否則不良品無法追溯 / A reject still needs its barcode, or it cannot be traced.
        Assert.Equal(Verdict.Fail, record.FinalJudge);
        Assert.Equal("ABC123456789", record.BarcodeData);
        Assert.Equal(VisionResult.Ng, record.Iv4Result);
    }

    [Fact]
    public async Task RunCycleAsync_WithANoRead_RejectsThePartAndSkipsTheCapture()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Scanner.ForceNoRead = true;

        var record = await rig.Sequencer.RunCycleAsync(CancellationToken.None);

        Assert.Equal(Verdict.Fail, record.FinalJudge);
        Assert.Null(record.BarcodeData);
        // 沒拍照 —— 讀不到碼的工件無法追溯,省下拍照時間
        // No capture: an untraceable part is rejected outright, saving the cycle time.
        Assert.Null(record.Iv4Result);
        Assert.Equal(rig.Options.ScanPositionPulse, rig.Motor.CurrentPosition);
    }

    [Fact]
    public async Task RunCycleAsync_WithANoRead_DoesNotFaultTheMachine()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Scanner.ForceNoRead = true;

        await rig.Sequencer.RunCycleAsync(CancellationToken.None);

        // 這是整份測試的核心：工件不良不等於設備故障
        // The heart of this file: a bad part is not a broken machine.
        Assert.Equal(MachineState.Idle, rig.Sequencer.State);
        Assert.Null(rig.Sequencer.FaultReason);
    }

    [Fact]
    public async Task RunCycleAsync_WithAWrongLengthBarcode_RejectsAndSkipsTheCapture()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Scanner.ForcedCode = "SHORT";  // 配方期望 12 碼 / the recipe expects 12

        var record = await rig.Sequencer.RunCycleAsync(CancellationToken.None);

        Assert.Equal(Verdict.Fail, record.FinalJudge);
        Assert.Equal("SHORT", record.BarcodeData);
        Assert.Null(record.Iv4Result);
        Assert.Contains(
            rig.Logs(),
            line => line.Contains("Barcode length mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunCycleAsync_CountsTheCycleAndAnnouncesIt()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();

        await rig.Sequencer.RunCycleAsync(CancellationToken.None);
        await rig.Sequencer.RunCycleAsync(CancellationToken.None);

        Assert.Equal(2, rig.Sequencer.CycleCount);
        Assert.Equal(2, rig.Completed().Count);
        Assert.Equal(rig.Store.Snapshot(), rig.Completed());
    }

    [Fact]
    public async Task RunCycleAsync_PicksUpAChangeoverOnTheVeryNextPart()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();

        // 換線：8 碼機種 / Changeover to an 8-character model.
        await rig.Recipes.SaveAsync(new RecipeModel { ModelName = "MODEL-B", BarcodeLength = 8 });
        rig.Scanner.ForcedCode = "SHORTY12";
        rig.Vision.ForcedResult = VisionResult.Ok;

        var record = await rig.Sequencer.RunCycleAsync(CancellationToken.None);

        Assert.Equal("MODEL-B", record.ModelName);
        Assert.Equal(Verdict.Pass, record.FinalJudge);
    }

    // ── 逾時與停機的分辨 / Telling a timeout apart from a stop ────────────────

    [Fact]
    public async Task RunCycleAsync_WhenTheVisionSensorStalls_RaisesADeviceTimeout()
    {
        await using var rig = NewRig(FastOptions(visionTimeout: TimeSpan.FromMilliseconds(120)));
        await rig.Sequencer.InitializeAsync();
        rig.Scanner.ForcedCode = "ABC123456789";
        rig.Vision.ForcedLatency = Forever;

        // 必須是 DeviceTimeoutException 而非裸的 OperationCanceledException：
        // 後者會在上層被誤判為「操作員停機」而靜默吞掉。
        // It must be a DeviceTimeoutException, not a bare OperationCanceledException —
        // the latter reads as "operator stop" upstream and gets swallowed.
        var ex = await Assert.ThrowsAsync<DeviceTimeoutException>(
            () => rig.Sequencer.RunCycleAsync(CancellationToken.None));

        Assert.Contains("拍照判別", ex.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Store.Snapshot());
    }

    [Fact]
    public async Task RunCycleAsync_WhenTheScannerStalls_RaisesADeviceTimeoutNotANoRead()
    {
        await using var rig = NewRig(FastOptions(barcodeTimeout: TimeSpan.FromMilliseconds(120)));
        await rig.Sequencer.InitializeAsync();
        rig.Scanner.ForcedLatency = Forever;

        // 沒回應的讀碼器是設備故障,不是 NOREAD —— 判成 NOREAD 會讓整批好品被誤退
        // An unresponsive scanner is an equipment fault, not a NOREAD; treating it as one
        // would silently reject a whole batch of good parts.
        await Assert.ThrowsAsync<DeviceTimeoutException>(
            () => rig.Sequencer.RunCycleAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunCycleAsync_WhenTheOperatorCancels_SurfacesACancellationNotATimeout()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Scanner.ForcedLatency = Forever;

        using var cts = new CancellationTokenSource();
        var cycle = rig.Sequencer.RunCycleAsync(cts.Token);
        await WaitUntilAsync(
            () => rig.Motor.CurrentPosition == rig.Options.ScanPositionPulse,
            "軸抵達讀碼位 / axis to reach the scan position");
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cycle);
        Assert.IsNotType<DeviceTimeoutException>(await Record.ExceptionAsync(() => cycle));
    }

    // ── 連續生產迴圈 / The production loop ───────────────────────────────────

    [Fact]
    public async Task Start_CyclesContinuously()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();

        rig.Sequencer.Start();
        Assert.Equal(MachineState.Running, rig.Sequencer.State);
        await WaitUntilAsync(() => rig.Sequencer.CycleCount >= 3, "完成三個週期 / three cycles to complete");
        await rig.Sequencer.StopAsync();

        Assert.Equal(MachineState.Idle, rig.Sequencer.State);
        Assert.True(rig.Store.Snapshot().Count >= 3);
    }

    [Fact]
    public async Task RunLoop_WhenADeviceStalls_StopsTheLineInsteadOfCarryingOn()
    {
        await using var rig = NewRig(FastOptions(moveTimeout: TimeSpan.FromMilliseconds(150)));
        await rig.Sequencer.InitializeAsync();
        rig.Motor.SimulateStall = true;

        rig.Sequencer.Start();
        await WaitUntilAsync(
            () => rig.Sequencer.State == MachineState.Faulted,
            "設備逾時導致停線 / the device timeout to stop the line");

        Assert.NotNull(rig.Sequencer.FaultReason);
        Assert.Contains(nameof(DeviceTimeoutException), rig.Sequencer.FaultReason!, StringComparison.Ordinal);
        Assert.Empty(rig.Store.Snapshot());
    }

    [Fact]
    public async Task RunLoop_WhenTheTraceabilityWriteFails_StopsTheLine()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Store.FailInserts = true;

        rig.Sequencer.Start();
        await WaitUntilAsync(
            () => rig.Sequencer.State == MachineState.Faulted,
            "寫入失敗導致停線 / the failed write to stop the line");

        // 寫不進去就沒有追溯性,繼續生產等於生產無法追溯的貨
        // Without the write there is no traceability, and carrying on ships untraceable parts.
        Assert.NotNull(rig.Sequencer.FaultReason);
        Assert.Contains(nameof(IOException), rig.Sequencer.FaultReason!, StringComparison.Ordinal);
        Assert.Equal(0, rig.Sequencer.CycleCount);
    }

    [Fact]
    public async Task RunLoop_WhenAPartIsRejected_KeepsRunning()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Scanner.ForceNoRead = true;

        rig.Sequencer.Start();
        await WaitUntilAsync(() => rig.Sequencer.CycleCount >= 3, "連續判退後仍持續生產 / to keep cycling through rejects");
        await rig.Sequencer.StopAsync();

        Assert.All(rig.Store.Snapshot(), record => Assert.Equal(Verdict.Fail, record.FinalJudge));
        Assert.Equal(MachineState.Idle, rig.Sequencer.State);
    }

    [Fact]
    public async Task RunLoop_SurvivesAThrowingSubscriber()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();

        // UI 端的例外不該讓機台停下 / A UI bug must not take the machine down.
        rig.Sequencer.CycleCompleted += (_, _) => throw new InvalidOperationException("UI 壞了 / broken UI handler");

        rig.Sequencer.Start();
        await WaitUntilAsync(() => rig.Sequencer.CycleCount >= 3, "訂閱者拋例外後仍持續生產 / to keep cycling despite the handler");
        await rig.Sequencer.StopAsync();

        Assert.Equal(MachineState.Idle, rig.Sequencer.State);
        Assert.Null(rig.Sequencer.FaultReason);
    }

    // ── 停機 / Stopping ──────────────────────────────────────────────────────

    [Fact]
    public async Task StopAsync_MidCycle_ReturnsToIdleWithoutFaulting()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Scanner.ForcedLatency = Forever;

        rig.Sequencer.Start();
        await WaitUntilAsync(
            () => rig.Motor.CurrentPosition == rig.Options.ScanPositionPulse,
            "週期進行到讀碼動作 / the cycle to reach the barcode read");
        await rig.Sequencer.StopAsync();

        // 停機途中的取消不是故障 / A cancellation caused by the stop is not a fault.
        Assert.Equal(MachineState.Idle, rig.Sequencer.State);
        Assert.Null(rig.Sequencer.FaultReason);
    }

    [Fact]
    public async Task StopAsync_MidCycle_LeavesNoRecordForTheUnjudgedPart()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Scanner.ForcedLatency = Forever;

        rig.Sequencer.Start();
        await WaitUntilAsync(
            () => rig.Motor.CurrentPosition == rig.Options.ScanPositionPulse,
            "週期進行到讀碼動作 / the cycle to reach the barcode read");
        await rig.Sequencer.StopAsync();

        // 未完成判定的工件不該留下追溯資料 / An unjudged part must leave no traceability data.
        Assert.Empty(rig.Store.Snapshot());
        Assert.Equal(0, rig.Sequencer.CycleCount);
    }

    [Fact]
    public async Task StopAsync_WhenNotRunning_IsANoOp()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();

        await rig.Sequencer.StopAsync();

        Assert.Equal(MachineState.Idle, rig.Sequencer.State);
    }

    [Fact]
    public async Task Start_AfterAStop_RunsAgain()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();

        rig.Sequencer.Start();
        await WaitUntilAsync(() => rig.Sequencer.CycleCount >= 1, "第一輪完成一個週期 / one cycle in the first run");
        await rig.Sequencer.StopAsync();
        var afterFirstRun = rig.Sequencer.CycleCount;

        rig.Sequencer.Start();
        await WaitUntilAsync(
            () => rig.Sequencer.CycleCount > afterFirstRun,
            "第二輪繼續生產 / the second run to produce");
        await rig.Sequencer.StopAsync();

        Assert.Equal(MachineState.Idle, rig.Sequencer.State);
    }

    // ── 處置 / Disposal ──────────────────────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_WhileRunning_StopsTheLoop()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();

        rig.Sequencer.Start();
        await WaitUntilAsync(() => rig.Sequencer.CycleCount >= 1, "完成一個週期 / one cycle to complete");
        await rig.Sequencer.DisposeAsync();

        var atDisposal = rig.Store.Snapshot().Count;
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        Assert.Equal(atDisposal, rig.Store.Snapshot().Count);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Sequencer.Start();
        await WaitUntilAsync(() => rig.Sequencer.CycleCount >= 1, "完成一個週期 / one cycle to complete");

        await rig.Sequencer.DisposeAsync();

        // 第二次處置不可對已釋放的權杖來源下取消 / The second pass must not cancel a disposed CTS.
        await rig.Sequencer.DisposeAsync();
    }

    // ── 測試腳手架 / Test scaffolding ────────────────────────────────────────

    /// <summary>
    /// 壓縮過的站別設定 / Compressed station options.
    /// 行程短、速度高,讓一個週期約 20 ms;逾時預設寬鬆,由個別測試自行收緊。
    /// Short strokes at high velocity put a cycle at roughly 20 ms. Timeouts default
    /// generous; the tests that care tighten the one they are exercising.
    /// </summary>
    private static SequencerOptions FastOptions(
        TimeSpan? moveTimeout = null,
        TimeSpan? barcodeTimeout = null,
        TimeSpan? visionTimeout = null,
        TimeSpan? connectTimeout = null) => new()
        {
            ScanPositionPulse = 100,
            InspectPositionPulse = 200,
            MoveSpeedPulsePerSecond = 1_000_000,
            MoveTimeout = moveTimeout ?? TimeSpan.FromSeconds(5),
            BarcodeTimeout = barcodeTimeout ?? TimeSpan.FromSeconds(5),
            VisionTimeout = visionTimeout ?? TimeSpan.FromSeconds(5),
            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5),
            CycleInterval = TimeSpan.Zero,
        };

    private Rig NewRig(SequencerOptions? options = null, TimeSpan? motorConnectLatency = null)
    {
        var effective = options ?? FastOptions();

        var motor = new MockMotorController { ConnectLatency = motorConnectLatency ?? Instant };
        var scanner = new MockBarcodeScanner(seed: 20260731) { MinLatency = Instant, MaxLatency = Instant };
        var vision = new MockVisionSensor(seed: 20260731)
        {
            MinLatency = Instant,
            MaxLatency = Instant,
            // 預設一律 OK,讓判定只由測試明確控制 / Default to OK so verdicts are test-driven, never random.
            ForcedResult = VisionResult.Ok,
        };
        var store = new FakeStore();
        var recipes = new RecipeManager(_workspace.PathTo("recipe.json"));
        var sequencer = new InspectionSequencer(motor, scanner, vision, store, recipes, effective);

        return new Rig(motor, scanner, vision, store, recipes, sequencer, effective);
    }

    /// <summary>
    /// 輪詢等待條件成立 / Poll until a condition holds.
    /// 產線迴圈跑在背景執行緒上,測試只能觀察它的可見效果。
    /// The line loop runs on a background thread, so tests can only observe its effects.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        const int PollCount = 300;
        var interval = TimeSpan.FromMilliseconds(20);  // 300 × 20 ms = 6 s

        for (var i = 0; i < PollCount; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(interval);
        }

        Assert.Fail($"等待逾時 / Timed out waiting for: {description}");
    }

    /// <summary>受測組合 / The assembled system under test, plus the handles a test needs to poke it.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly List<MachineState> _states = [];
        private readonly List<string> _logs = [];
        private readonly List<InspectionRecord> _completed = [];

        public Rig(
            MockMotorController motor,
            MockBarcodeScanner scanner,
            MockVisionSensor vision,
            FakeStore store,
            RecipeManager recipes,
            InspectionSequencer sequencer,
            SequencerOptions options)
        {
            Motor = motor;
            Scanner = scanner;
            Vision = vision;
            Store = store;
            Recipes = recipes;
            Sequencer = sequencer;
            Options = options;

            // 事件在背景執行緒上引發,收集時必須自行上鎖
            // Events are raised on background threads, so collection has to take a lock.
            Sequencer.StateChanged += (_, e) =>
            {
                lock (_states)
                {
                    _states.Add(e.Current);
                }
            };

            Sequencer.LogEmitted += (_, e) =>
            {
                lock (_logs)
                {
                    _logs.Add(e.Message);
                }
            };

            Sequencer.CycleCompleted += (_, e) =>
            {
                lock (_completed)
                {
                    _completed.Add(e.Record);
                }
            };
        }

        public MockMotorController Motor { get; }

        public MockBarcodeScanner Scanner { get; }

        public MockVisionSensor Vision { get; }

        public FakeStore Store { get; }

        public RecipeManager Recipes { get; }

        public InspectionSequencer Sequencer { get; }

        public SequencerOptions Options { get; }

        /// <summary>觀察到的狀態轉移 / Observed state transitions.</summary>
        public MachineState[] States()
        {
            lock (_states)
            {
                return [.. _states];
            }
        }

        /// <summary>觀察到的訊息 / Observed log lines.</summary>
        public IReadOnlyList<string> Logs()
        {
            lock (_logs)
            {
                return [.. _logs];
            }
        }

        /// <summary>觀察到的完成週期 / Observed completed cycles.</summary>
        public IReadOnlyList<InspectionRecord> Completed()
        {
            lock (_completed)
            {
                return [.. _completed];
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Sequencer.DisposeAsync();
            await Motor.DisposeAsync();
            await Scanner.DisposeAsync();
            await Vision.DisposeAsync();
        }
    }

    /// <summary>
    /// 可控失敗的追溯儲存 / A traceability store that can be made to fail on demand.
    /// <see cref="IInspectionStore"/> 存在的理由就是這個：真的 SQLite 不會照指令壞掉。
    /// This is exactly why <see cref="IInspectionStore"/> exists — real SQLite will not
    /// break on request.
    /// </summary>
    private sealed class FakeStore : IInspectionStore
    {
        private readonly List<InspectionRecord> _records = [];
        private long _nextId;

        /// <summary>模擬寫入失敗 / Simulate a failing write.</summary>
        public bool FailInserts { get; set; }

        public int InitializeCount { get; private set; }

        public IReadOnlyList<InspectionRecord> Snapshot()
        {
            lock (_records)
            {
                return [.. _records];
            }
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            InitializeCount++;
            return Task.CompletedTask;
        }

        public Task<long> InsertAsync(InspectionRecord record, CancellationToken cancellationToken = default)
        {
            if (FailInserts)
            {
                throw new IOException("模擬追溯寫入失敗 / simulated traceability write failure.");
            }

            lock (_records)
            {
                _records.Add(record);
            }

            return Task.FromResult(Interlocked.Increment(ref _nextId));
        }
    }
}
