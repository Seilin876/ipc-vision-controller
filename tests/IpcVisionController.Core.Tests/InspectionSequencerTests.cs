using IpcVisionController.Core.Data;
using IpcVisionController.Core.Hal;
using IpcVisionController.Core.Machine;
using IpcVisionController.Core.Models;
using IpcVisionController.Core.Recipes;
using Xunit;

namespace IpcVisionController.Core.Tests;

/// <summary>
/// 檢測週期協調器的行為規格 / Behavioural spec for the inspection sequencer.
///
/// 這裡守的是三條安全需求 / Three safety requirements are guarded here:
/// 1. 工件不良 ≠ 設備故障。讀不到碼判退後繼續生產,設備回報異常或沒回應則停線。
///    A bad part is not a broken machine: reject and carry on for the former, stop the
///    line when a device reports a fault or stops answering.
/// 2. 操作員停機與設備逾時都是 OperationCanceledException,絕不可混為一談,
///    否則設備故障會被當成正常停機靜默吞掉。
///    An operator stop and a device timeout are both an OperationCanceledException; conflating
///    them silently swallows equipment faults as normal stops.
/// 3. 寫不進追溯資料庫就等於失去追溯性,必須停線。
///    Losing the traceability write means losing traceability, which must stop the line.
///
/// 判定規則本身不在這裡測,那是 <see cref="LabelJudgeTests"/> 的工作;
/// 本檔只驗證協調器有把結果原封不動交給規則,並照規則的結論行動。
/// The judging rules themselves are not tested here — that is <see cref="LabelJudgeTests"/>.
/// This file only checks that the sequencer hands the results to the rules untouched and
/// then acts on the answer.
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

        // 沒有建表就開始生產,第一張標籤的紀錄就會掉
        // Cycling before the table exists loses the very first label's record.
        Assert.Equal(1, rig.Store.InitializeCount);
        Assert.True(File.Exists(rig.Recipes.FilePath));
        Assert.Contains(rig.Logs(), line => line.Contains("Recipe loaded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InitializeAsync_LeavesTheWebAlignedAndTheServoEnabled()
    {
        await using var rig = NewRig();

        await rig.Sequencer.InitializeAsync();

        Assert.True(rig.Motor.IsEnabled);
        // 對標後累計進給量重新起算 / The feed count restarts after a registration align.
        Assert.Equal(0, rig.Motor.CurrentPosition);
        Assert.True(rig.Reader.IsConnected);
        Assert.True(rig.Verifier.IsConnected);
    }

    [Fact]
    public async Task InitializeAsync_WhenAlreadyIdle_IsRejected()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();

        // 重複初始化會在生產中重新對標,料帶會意外多走一段
        // Re-initialising mid-shift would re-align the web and advance it unexpectedly.
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Sequencer.InitializeAsync());
    }

    [Fact]
    public async Task Start_BeforeInitialize_IsRejected()
    {
        await using var rig = NewRig();

        // 未對標就進給,標籤與感測器的相位沒有意義
        // Feeding before the registration align leaves the labels out of phase with the sensors.
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
    public async Task InitializeAsync_WhenTheAlignNeverCompletes_FaultsOnTheFeedTimeout()
    {
        await using var rig = NewRig(FastOptions(feedTimeout: TimeSpan.FromMilliseconds(120)));

        await FaultViaStalledAlignAsync(rig);

        Assert.Equal(MachineState.Faulted, rig.Sequencer.State);
        Assert.NotNull(rig.Sequencer.FaultReason);
        Assert.Contains("初始化失敗", rig.Sequencer.FaultReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reset_AfterAFault_ReturnsToOfflineAndClearsTheReason()
    {
        await using var rig = NewRig(FastOptions(feedTimeout: TimeSpan.FromMilliseconds(120)));
        await FaultViaStalledAlignAsync(rig);

        rig.Sequencer.Reset();

        // 回 Offline 而非 Idle：故障後料帶對位不可信,必須重新對標
        // Offline, not Idle: after a fault the web's registration cannot be trusted.
        Assert.Equal(MachineState.Offline, rig.Sequencer.State);
        Assert.Null(rig.Sequencer.FaultReason);
    }

    [Fact]
    public async Task Reset_AfterAFault_AllowsAFullReinitialise()
    {
        await using var rig = NewRig(FastOptions(feedTimeout: TimeSpan.FromSeconds(5)));
        await FaultViaStalledAlignAsync(rig, alignTimeout: TimeSpan.FromMilliseconds(120));
        rig.Sequencer.Reset();

        // 現場排除卡料後,料帶恢復進給 / The jam is cleared on the line and the web feeds again.
        rig.Motor.SimulateStall = false;

        await rig.Sequencer.InitializeAsync();

        Assert.Equal(MachineState.Idle, rig.Sequencer.State);
        Assert.Equal(0, rig.Motor.CurrentPosition);
    }

    [Fact]
    public async Task RunCycleAsync_WhenTheReaderReportsItsOwnFault_StopsTheLine()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Reader.SimulateFault = true;

        // 感測器自身異常是設備問題,必須上拋停線,不能當成一張不良標籤
        // A sensor-level fault is an equipment problem: it must propagate and stop the
        // line, not be recorded as one bad label.
        await Assert.ThrowsAsync<DeviceFaultException>(
            () => rig.Sequencer.RunCycleAsync(CancellationToken.None));

        Assert.Empty(rig.Store.Snapshot());
    }

    [Fact]
    public async Task RunLoop_WhenAVerifierFaults_StopsTheLine()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Verifier.SimulateFault = true;

        rig.Sequencer.Start();
        await WaitUntilAsync(
            () => rig.Sequencer.State == MachineState.Faulted,
            "感測器異常導致停線 / the sensor fault to stop the line");

        Assert.NotNull(rig.Sequencer.FaultReason);
        Assert.Contains(nameof(DeviceFaultException), rig.Sequencer.FaultReason!, StringComparison.Ordinal);
    }

    // ── 單一週期的判定 / Single-cycle verdicts ───────────────────────────────

    [Fact]
    public async Task RunCycleAsync_WithAGoodLabel_RecordsPass()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Reader.ForcedCodes = ["ABC123456789"];
        rig.Verifier.ForcedTexts = ["LOT26A"];

        var record = await rig.Sequencer.RunCycleAsync(CancellationToken.None);

        // 相隔格數預設為 0，所以第一個週期就該完成判定；回 null 代表暫存邏輯把
        // 「兩台看同一張」的情況也當成了要等 / With the default offset of zero the very first
        // cycle must produce a verdict; a null here would mean the shift register also makes the
        // both-sensors-one-label case wait.
        Assert.NotNull(record);

        Assert.Equal(Verdict.Pass, record.FinalJudge);
        Assert.Null(record.RejectReason);
        Assert.Equal(["ABC123456789"], record.CodeResults.Select(r => r.Data));
        Assert.Equal(["LOT26A"], record.CharacterResults.Select(r => r.Text));
        Assert.Equal("DEFAULT", record.ModelName);
        Assert.Equal(DateTimeKind.Utc, record.Timestamp.Kind);
        Assert.Single(rig.Store.Snapshot());
    }

    [Fact]
    public async Task RunCycleAsync_AdvancesTheWebByExactlyOnePitch()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();

        await rig.Sequencer.RunCycleAsync(CancellationToken.None);
        await rig.Sequencer.RunCycleAsync(CancellationToken.None);

        // 每個週期剛好一格。多走一格會整批跳過標籤,少走一格會重複檢測同一張。
        // Exactly one pitch per cycle: feeding more skips labels wholesale, feeding less
        // inspects the same label twice.
        Assert.Equal(rig.Options.FeedPitchPulses * 2, rig.Motor.CurrentPosition);
    }

    [Fact]
    public async Task RunCycleAsync_WithAFailingLabel_KeepsTheEvidenceAndTheReason()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Reader.ForcedCodes = ["ABC123456789"];
        rig.Verifier.ForcedTexts = [null];

        var record = await rig.Sequencer.RunCycleAsync(CancellationToken.None);

        // 相隔格數預設為 0，所以第一個週期就該完成判定；回 null 代表暫存邏輯把
        // 「兩台看同一張」的情況也當成了要等 / With the default offset of zero the very first
        // cycle must produce a verdict; a null here would mean the shift register also makes the
        // both-sensors-one-label case wait.
        Assert.NotNull(record);

        // 不良品也要留下讀到的內容,否則無法追溯 / A reject still needs its payload, or it cannot be traced.
        Assert.Equal(Verdict.Fail, record.FinalJudge);
        Assert.Equal(["ABC123456789"], record.CodeResults.Select(r => r.Data));
        Assert.NotNull(record.RejectReason);
        Assert.Contains("recognised nothing", record.RejectReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunCycleAsync_WithANoRead_RejectsWithoutFaultingTheMachine()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Reader.ForceNoRead = true;

        var record = await rig.Sequencer.RunCycleAsync(CancellationToken.None);

        // 相隔格數預設為 0，所以第一個週期就該完成判定；回 null 代表暫存邏輯把
        // 「兩台看同一張」的情況也當成了要等 / With the default offset of zero the very first
        // cycle must produce a verdict; a null here would mean the shift register also makes the
        // both-sensors-one-label case wait.
        Assert.NotNull(record);

        // 這是整份測試的核心：工件不良不等於設備故障
        // The heart of this file: a bad part is not a broken machine.
        Assert.Equal(Verdict.Fail, record.FinalJudge);
        Assert.Empty(record.CodeResults);
        Assert.Equal(MachineState.Idle, rig.Sequencer.State);
        Assert.Null(rig.Sequencer.FaultReason);
    }

    [Fact]
    public async Task RunCycleAsync_AfterANoRead_StillTriggersTheVerifier()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Reader.ForceNoRead = true;

        var record = await rig.Sequencer.RunCycleAsync(CancellationToken.None);

        // 相隔格數預設為 0，所以第一個週期就該完成判定；回 null 代表暫存邏輯把
        // 「兩台看同一張」的情況也當成了要等 / With the default offset of zero the very first
        // cycle must produce a verdict; a null here would mean the shift register also makes the
        // both-sensors-one-label case wait.
        Assert.NotNull(record);

        // 舊架構在讀碼失敗時略過拍照以省下一趟行程;現在兩者共用同一次進給,
        // 沒有行程可省,而少一組結果就少一半的判退依據。
        // The old design skipped the capture after a failed read to save a move. With one
        // feed serving both sensors there is no travel to save, and dropping a result set
        // halves the evidence behind the reject.
        Assert.NotEmpty(record.CharacterResults);
    }

    [Fact]
    public async Task RunCycleAsync_AnnouncesWhyItRejected()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Reader.ForcedCodes = ["SHORT"];

        await rig.Sequencer.RunCycleAsync(CancellationToken.None);

        Assert.Contains(
            rig.Logs(),
            line => line.Contains("length 5 != expected 12", StringComparison.Ordinal));
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
    public async Task RunCycleAsync_PicksUpAChangeoverOnTheVeryNextLabel()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();

        // 換線：8 碼、兩張標籤的機種 / Changeover to an 8-character, two-label model.
        await rig.Recipes.SaveAsync(new RecipeModel
        {
            ModelName = "MODEL-B",
            ExpectedCodeCount = 2,
            BarcodeLength = 8,
        });
        rig.Reader.ForcedCodes = ["SHORTY12", "SHORTY34"];

        var record = await rig.Sequencer.RunCycleAsync(CancellationToken.None);

        // 相隔格數預設為 0，所以第一個週期就該完成判定；回 null 代表暫存邏輯把
        // 「兩台看同一張」的情況也當成了要等 / With the default offset of zero the very first
        // cycle must produce a verdict; a null here would mean the shift register also makes the
        // both-sensors-one-label case wait.
        Assert.NotNull(record);

        Assert.Equal("MODEL-B", record.ModelName);
        Assert.Equal(Verdict.Pass, record.FinalJudge);
    }

    // ── 逾時與停機的分辨 / Telling a timeout apart from a stop ────────────────

    [Fact]
    public async Task RunCycleAsync_WhenTheVerifierStalls_RaisesADeviceTimeout()
    {
        await using var rig = NewRig(FastOptions(characterVerifyTimeout: TimeSpan.FromMilliseconds(120)));
        await rig.Sequencer.InitializeAsync();
        rig.Verifier.ForcedLatency = Forever;

        // 必須是 DeviceTimeoutException 而非裸的 OperationCanceledException：
        // 後者會在上層被誤判為「操作員停機」而靜默吞掉。
        // It must be a DeviceTimeoutException, not a bare OperationCanceledException —
        // the latter reads as "operator stop" upstream and gets swallowed.
        var ex = await Assert.ThrowsAsync<DeviceTimeoutException>(
            () => rig.Sequencer.RunCycleAsync(CancellationToken.None));

        Assert.Contains("字符檢測", ex.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Store.Snapshot());
    }

    [Fact]
    public async Task RunCycleAsync_WhenTheReaderStalls_RaisesADeviceTimeoutNotANoRead()
    {
        await using var rig = NewRig(FastOptions(codeReadTimeout: TimeSpan.FromMilliseconds(120)));
        await rig.Sequencer.InitializeAsync();
        rig.Reader.ForcedLatency = Forever;

        // 沒回應的讀碼器是設備故障,不是 NOREAD —— 判成 NOREAD 會讓整批好品被誤退
        // An unresponsive reader is an equipment fault, not a NOREAD; treating it as one
        // would silently reject a whole batch of good parts.
        await Assert.ThrowsAsync<DeviceTimeoutException>(
            () => rig.Sequencer.RunCycleAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RunCycleAsync_WhenTheOperatorCancels_SurfacesACancellationNotATimeout()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Reader.ForcedLatency = Forever;

        using var cts = new CancellationTokenSource();
        var cycle = rig.Sequencer.RunCycleAsync(cts.Token);
        await WaitUntilAsync(
            () => rig.Motor.CurrentPosition >= rig.Options.FeedPitchPulses,
            "料帶進給完成 / the web to finish its pitch");
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
    public async Task RunLoop_WhenTheWebJams_StopsTheLineInsteadOfCarryingOn()
    {
        await using var rig = NewRig(FastOptions(feedTimeout: TimeSpan.FromMilliseconds(150)));
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
    public async Task RunLoop_WhenALabelIsRejected_KeepsRunning()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Reader.ForceNoRead = true;

        rig.Sequencer.Start();
        await WaitUntilAsync(() => rig.Sequencer.CycleCount >= 3, "連續判退後仍持續生產 / to keep cycling through rejects");
        await rig.Sequencer.StopAsync();

        Assert.All(rig.Store.Snapshot(), record => Assert.Equal(Verdict.Fail, record.FinalJudge));
        Assert.All(rig.Store.Snapshot(), record => Assert.NotNull(record.RejectReason));
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
        rig.Reader.ForcedLatency = Forever;

        rig.Sequencer.Start();
        await WaitUntilAsync(
            () => rig.Motor.CurrentPosition >= rig.Options.FeedPitchPulses,
            "週期進行到讀碼動作 / the cycle to reach the code read");
        await rig.Sequencer.StopAsync();

        // 停機途中的取消不是故障 / A cancellation caused by the stop is not a fault.
        Assert.Equal(MachineState.Idle, rig.Sequencer.State);
        Assert.Null(rig.Sequencer.FaultReason);
    }

    [Fact]
    public async Task StopAsync_MidCycle_LeavesNoRecordForTheUnjudgedLabel()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Reader.ForcedLatency = Forever;

        rig.Sequencer.Start();
        await WaitUntilAsync(
            () => rig.Motor.CurrentPosition >= rig.Options.FeedPitchPulses,
            "週期進行到讀碼動作 / the cycle to reach the code read");
        await rig.Sequencer.StopAsync();

        // 未完成判定的標籤不該留下追溯資料 / An unjudged label must leave no traceability data.
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

    // ── 單次觸發 / Single-shot trigger ───────────────────────────────────────

    [Fact]
    public async Task TriggerOnceAsync_RunsOneCycleAndReturnsToIdle()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Reader.ForcedCodes = ["ABC123456789"];
        rig.Verifier.ForcedTexts = ["LOT26A"];

        var record = await rig.Sequencer.TriggerOnceAsync();

        Assert.NotNull(record);
        Assert.Equal(Verdict.Pass, record!.FinalJudge);
        Assert.Equal(1, rig.Sequencer.CycleCount);
        Assert.Single(rig.Store.Snapshot());

        // 回到 Idle 才能再按下一次,停在 Running 會讓試機只能觸發一格就卡死
        // Returning to Idle is what allows a second press; stuck in Running, a dry run
        // gets exactly one label and then nothing.
        Assert.Equal(MachineState.Idle, rig.Sequencer.State);
        Assert.Equal(
            new[] { MachineState.Running, MachineState.Idle },
            rig.States()[^2..]);
    }

    [Fact]
    public async Task TriggerOnceAsync_FeedsOnePitchAndThenLeavesTheWebAlone()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();

        await rig.Sequencer.TriggerOnceAsync();

        // 這是單次觸發與「開始」的唯一分野。一個週期約 20 ms,所以若它其實還在跑,
        // 這段等待足夠讓料帶多走好幾格。變成連續生產就無法逐格核對判定 ——
        // 而逐格核對正是試機與印刷調機唯一有用的模式。
        // The one distinction from Start. A cycle takes roughly 20 ms, so if the loop were
        // still running this wait is long enough for several more pitches. Decaying into
        // cycling removes the only mode that is useful for a dry run or a print setup:
        // one verdict checked at a time.
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        Assert.Equal(rig.Options.FeedPitchPulses, rig.Motor.CurrentPosition);
        Assert.Equal(1, rig.Sequencer.CycleCount);
        Assert.Equal(MachineState.Idle, rig.Sequencer.State);
    }

    [Fact]
    public async Task TriggerOnceAsync_BeforeInitialize_IsRejected()
    {
        await using var rig = NewRig();

        // 尚未連線與對標就進給,料帶位置無從得知
        // Feeding before the link is up and the web is aligned moves stock to an unknown place.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Sequencer.TriggerOnceAsync());

        Assert.Equal(MachineState.Offline, rig.Sequencer.State);
        Assert.Equal(0, rig.Motor.CurrentPosition);
    }

    [Fact]
    public async Task TriggerOnceAsync_WhenOneIsAlreadyInFlight_IsRejected()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Reader.ForcedLatency = TimeSpan.FromMilliseconds(250);

        var inFlight = rig.Sequencer.TriggerOnceAsync();

        // 連按第二下必須被擋下：兩個週期重疊會對同一顆馬達連下兩次進給,
        // 料帶多送一格,而那一格從未被檢測就流到下游。
        // The second press must be refused: two overlapping cycles issue two feeds to one
        // motor, so the web advances a pitch that is never inspected and still reaches
        // the next station.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Sequencer.TriggerOnceAsync());

        Assert.NotNull(await inFlight);
        Assert.Equal(rig.Options.FeedPitchPulses, rig.Motor.CurrentPosition);
        Assert.Equal(1, rig.Sequencer.CycleCount);
    }

    [Fact]
    public async Task TriggerOnceAsync_WhenADeviceReportsItsOwnFault_StopsTheLine()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Reader.SimulateFault = true;

        // 單次觸發吸收例外並轉為故障狀態,而非上拋：按鈕的呼叫端是 UI,
        // 讓設備異常變成一個對話框而機台仍顯示待命,是最糟的組合。
        // The single shot absorbs the exception into the fault state rather than rethrowing:
        // its caller is a UI, and turning an equipment fault into a dialog box while the
        // machine still reads Ready is the worst of both.
        var record = await rig.Sequencer.TriggerOnceAsync();

        Assert.Null(record);
        Assert.Equal(MachineState.Faulted, rig.Sequencer.State);
        Assert.Contains(nameof(DeviceFaultException), rig.Sequencer.FaultReason!, StringComparison.Ordinal);
        Assert.Empty(rig.Store.Snapshot());
    }

    [Fact]
    public async Task TriggerOnceAsync_WhenTheOperatorStopsMidCycle_LeavesNoRecord()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();
        rig.Reader.ForcedLatency = TimeSpan.FromMilliseconds(400);

        var inFlight = rig.Sequencer.TriggerOnceAsync();
        await rig.Sequencer.StopAsync();

        // 未完成判定的工件不該留下追溯資料,單次觸發與連續生產在這點上必須一致
        // An unjudged part must not leave traceability data behind, and a single shot has
        // to agree with cycling on that.
        Assert.Null(await inFlight);
        Assert.Empty(rig.Store.Snapshot());
        Assert.Equal(MachineState.Idle, rig.Sequencer.State);
        Assert.Equal(0, rig.Sequencer.CycleCount);
    }

    [Fact]
    public async Task TriggerOnceAsync_ThenStart_StillCycles()
    {
        await using var rig = NewRig();
        await rig.Sequencer.InitializeAsync();

        await rig.Sequencer.TriggerOnceAsync();
        rig.Sequencer.Start();

        // 試機用單次觸發驗完就直接轉連續生產,不該逼操作員重新初始化
        // A dry run ends by handing straight over to production; the operator should not
        // have to re-initialise in between.
        await WaitUntilAsync(
            () => rig.Sequencer.CycleCount >= 3,
            "單次觸發後仍能連續生產 / cycling to continue after a single shot");

        await rig.Sequencer.StopAsync();
        Assert.Equal(MachineState.Idle, rig.Sequencer.State);
    }

    // ── 測試腳手架 / Test scaffolding ────────────────────────────────────────

    /// <summary>
    /// 壓縮過的站別設定 / Compressed station options.
    /// 進給量短、速度高,讓一個週期約 20 ms;逾時預設寬鬆,由個別測試自行收緊。
    /// A short pitch at high velocity puts a cycle at roughly 20 ms. Timeouts default
    /// generous; the tests that care tighten the one they are exercising.
    /// </summary>
    // ── 兩站相隔的暫存 / The two-station shift register ──────────────────────

    /// <summary>
    /// 相隔 k 格的站別設定 / Options for stations k pitches apart.
    /// </summary>
    private static SequencerOptions OffsetOptions(int pitches)
    {
        var fast = FastOptions();
        return new SequencerOptions
        {
            FeedPitchPulses = fast.FeedPitchPulses,
            FeedSpeedPulsePerSecond = fast.FeedSpeedPulsePerSecond,
            FeedTimeout = fast.FeedTimeout,
            CodeReadTimeout = fast.CodeReadTimeout,
            CharacterVerifyTimeout = fast.CharacterVerifyTimeout,
            ConnectTimeout = fast.ConnectTimeout,
            CycleInterval = TimeSpan.Zero,
            InspectionOffsetPitches = pitches,
        };
    }

    [Fact]
    public async Task WithAnOffset_TheFirstLabelsAreScannedButLeaveNoRecord()
    {
        // 標籤要走 4 格才抵達檢測站,所以前 4 個週期沒有任何一張可以判定。
        // 這是現場一定會看到、卻最容易被當成程式壞掉的行為。
        // A label needs four pitches to reach the verification station, so none can be judged in the
        // first four cycles. This is the behaviour the line is certain to notice and most likely to
        // read as a broken program.
        await using var rig = NewRig(OffsetOptions(4));
        await rig.Sequencer.InitializeAsync();

        for (var i = 0; i < 4; i++)
        {
            Assert.Null(await rig.Sequencer.RunCycleAsync(CancellationToken.None));
        }

        Assert.Empty(rig.Store.Snapshot());
        Assert.Equal(0, rig.Sequencer.CycleCount);
    }

    [Fact]
    public async Task WithAnOffset_TheVerifierIsNotTriggeredWhileTheQueueFills
        ()
    {
        // 檢測站底下躺著的是開機前就越過讀碼站的標籤。拍它會得到一個無法歸屬的結果,
        // 而把無法歸屬的結果寫進追溯資料,比完全沒有紀錄糟得多。
        // What lies under the verifier passed the reading station before this run began. Capturing it
        // yields a result belonging to no known label, and an unattributable row in the traceability
        // data is far worse than no row at all.
        await using var rig = NewRig(OffsetOptions(2));
        await rig.Sequencer.InitializeAsync();

        var triggersBefore = rig.Verifier.TriggerCount;
        await rig.Sequencer.RunCycleAsync(CancellationToken.None);
        await rig.Sequencer.RunCycleAsync(CancellationToken.None);

        Assert.Equal(triggersBefore, rig.Verifier.TriggerCount);
    }

    [Fact]
    public async Task WithAnOffset_TheRecordPairsTheCodeScannedThatManyCyclesEarlier()
    {
        // 這一項是整個暫存存在的理由。相隔 2 格時,第 3 個週期的紀錄必須是
        // 第 1 個週期讀到的條碼,配上第 3 個週期字符檢測回報的結果。
        // 若程式當成相隔 0 格,紀錄會把第 3 張的條碼與第 1 張的字符湊在一起 ——
        // 不會報錯、良率正常,但兩半不屬於同一張標籤,而且每一筆看起來都完整。
        // This is why the shift register exists. With an offset of two, the record from the third cycle
        // has to carry the code read in the first, paired with what the verifier reported in the third.
        // Assuming an offset of zero would pair the third label's code with the first label's
        // characters: nothing errors, the yield looks normal, the two halves belong to different labels,
        // and every row looks complete.
        await using var rig = NewRig(OffsetOptions(2));
        await rig.Sequencer.InitializeAsync();

        rig.Reader.ForcedCodes = ["FIRST-LABEL0"];
        Assert.Null(await rig.Sequencer.RunCycleAsync(CancellationToken.None));

        rig.Reader.ForcedCodes = ["SECONDLABEL"];
        Assert.Null(await rig.Sequencer.RunCycleAsync(CancellationToken.None));

        rig.Reader.ForcedCodes = ["THIRD-LABEL"];
        rig.Verifier.ForcedTexts = ["REGION-3RD"];
        var record = await rig.Sequencer.RunCycleAsync(CancellationToken.None);

        Assert.NotNull(record);
        Assert.Equal(["FIRST-LABEL0"], record.CodeResults.Select(r => r.Data));
        Assert.Equal(["REGION-3RD"], record.CharacterResults.Select(r => r.Text));
    }

    [Fact]
    public async Task WithAnOffset_LabelsAreJudgedByTheRecipeInForceWhenTheyWereScanned()
    {
        // 換線那一刻,兩站之間的標籤屬於舊機種 —— 它們是在舊配方下被掃的,就該用舊配方判定。
        // 用當下的新配方判,等於把在製品用下一個機種的規格判退。
        // At a changeover the labels between the stations belong to the outgoing product: they were
        // scanned under the old recipe and must be judged by it. Judging them against the incoming one
        // rejects work in progress by the next product's specification.
        await using var rig = NewRig(OffsetOptions(1));
        await rig.Sequencer.InitializeAsync();

        rig.Reader.ForcedCodes = ["OLDMODEL0001"];
        Assert.Null(await rig.Sequencer.RunCycleAsync(CancellationToken.None));

        // 換線：新機種要求兩筆條碼,而在製品那一張只有一筆
        // Changeover: the incoming product wants two codes, and the label in flight has one.
        await rig.Recipes.SaveAsync(new RecipeModel
        {
            ModelName = "MODEL-NEW",
            ExpectedCodeCount = 2,
            BarcodeLength = RecipeModel.NoCheck,
            ExpectedCharacterRegionCount = 1,
        });

        var record = await rig.Sequencer.RunCycleAsync(CancellationToken.None);

        Assert.NotNull(record);
        Assert.Equal("DEFAULT", record.ModelName);
        Assert.Equal(Verdict.Pass, record.FinalJudge);
    }

    [Fact]
    public async Task WithAnOffset_StoppingSaysHowManyLabelsWereLeftUnverified()
    {
        // 那幾張標籤已經越過讀碼站,現在停在兩站之間,既沒有被判定,也不會有任何紀錄提到
        // 它們存在過。現場必須知道有幾張,才有機會挑出來重驗 —— 少了這一句,它們會安靜地
        // 流到下游。
        // Those labels passed the reading station and now sit between the two, unjudged, with nothing
        // anywhere recording that they existed. The line has to be told how many to have any chance of
        // pulling them; without that message they travel downstream in silence.
        await using var rig = NewRig(OffsetOptions(3));
        await rig.Sequencer.InitializeAsync();

        await rig.Sequencer.RunCycleAsync(CancellationToken.None);
        await rig.Sequencer.RunCycleAsync(CancellationToken.None);

        rig.Sequencer.Start();
        await rig.Sequencer.StopAsync();

        Assert.Contains(
            rig.Logs(),
            line => line.Contains("已掃碼但未完成檢測", StringComparison.Ordinal));
        Assert.Empty(rig.Store.Snapshot());
    }

    [Fact]
    public async Task WithAnOffset_AfterAStopTheQueueRefillsSoTheFirstRecordCannotMispair()
    {
        // 停機作廢了兩站之間的在製品,所以下一輪必須重新填滿。
        // 若佇列跨過停機留了下來,復產後的第一筆紀錄會拿停機前掃到的條碼,配上復產後拍到的字符
        // —— 而停機期間操作員多半已經把那幾張標籤抽掉、或把料帶拉動過,那兩半根本不屬於同一張。
        // A stop voids the work in progress between the stations, so the next run has to fill again. Were
        // the queue to survive the stop, the first record after resuming would pair a code scanned before
        // it with characters captured after — and by then the operator has most likely pulled those
        // labels out or moved the web, so the two halves belong to nothing in common.
        await using var rig = NewRig(OffsetOptions(1));
        await rig.Sequencer.InitializeAsync();

        rig.Reader.ForcedCodes = ["BEFORESTOP01"];
        Assert.Null(await rig.Sequencer.RunCycleAsync(CancellationToken.None));

        rig.Sequencer.Start();
        await rig.Sequencer.StopAsync();

        // 復產：第一個週期仍然沒有紀錄,因為佇列是空的
        // Resuming: the first cycle still yields nothing, because the queue is empty.
        rig.Reader.ForcedCodes = ["AFTERSTOP001"];
        Assert.Null(await rig.Sequencer.RunCycleAsync(CancellationToken.None));

        rig.Verifier.ForcedTexts = ["REGION-NEW"];
        var record = await rig.Sequencer.RunCycleAsync(CancellationToken.None);

        // 停機前掃到的那一張不曾、也不該出現在任何紀錄裡
        // The label scanned before the stop never appears in any record, and must not.
        Assert.NotNull(record);
        Assert.Equal(["AFTERSTOP001"], record.CodeResults.Select(r => r.Data));
        Assert.DoesNotContain(
            rig.Store.Snapshot(),
            r => r.CodeResults.Any(c => c.Data == "BEFORESTOP01"));
    }

    [Fact]
    public async Task WithAnOffset_CyclingStillProducesARecordPerLabelOnceTheQueueIsFull()
    {
        // 暫存只延後第一筆,不減少總量。填滿之後每一格進給都該產生一筆紀錄,
        // 否則產能就被這個機制吃掉了。
        // The register only delays the first record; it does not reduce the total. Once full, every pitch
        // must still yield one record, or the mechanism has eaten throughput.
        await using var rig = NewRig(OffsetOptions(2));
        await rig.Sequencer.InitializeAsync();

        rig.Sequencer.Start();

        await WaitUntilAsync(
            () => rig.Sequencer.CycleCount >= 5,
            "填滿後持續產生紀錄 / records to keep coming once the queue is full");

        await rig.Sequencer.StopAsync();
        Assert.True(rig.Store.Snapshot().Count >= 5);
    }

    private static SequencerOptions FastOptions(
        TimeSpan? feedTimeout = null,
        TimeSpan? codeReadTimeout = null,
        TimeSpan? characterVerifyTimeout = null,
        TimeSpan? connectTimeout = null) => new()
        {
            FeedPitchPulses = 100,
            FeedSpeedPulsePerSecond = 1_000_000,
            FeedTimeout = feedTimeout ?? TimeSpan.FromSeconds(5),
            CodeReadTimeout = codeReadTimeout ?? TimeSpan.FromSeconds(5),
            CharacterVerifyTimeout = characterVerifyTimeout ?? TimeSpan.FromSeconds(5),
            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5),
            CycleInterval = TimeSpan.Zero,
        };

    private Rig NewRig(SequencerOptions? options = null, TimeSpan? motorConnectLatency = null)
    {
        var effective = options ?? FastOptions();

        var motor = new MockMotorController
        {
            ConnectLatency = motorConnectLatency ?? Instant,
            // 對標行程壓到一個伺服週期 / One servo cycle's worth of align.
            PulsesToNextMark = 100,
            AlignSpeedPulsePerSecond = 1_000_000,
        };

        // 兩個感測器都預設「一切正常」,讓判定只由測試明確控制,不受亂數影響
        // Both sensors default to healthy so verdicts are test-driven, never random.
        var reader = new MockCodeReader(seed: 20260731) { MinLatency = Instant, MaxLatency = Instant };
        var verifier = new MockCharacterVerifier(seed: 20260731) { MinLatency = Instant, MaxLatency = Instant };

        var store = new FakeStore();
        var recipes = new RecipeManager(_workspace.PathTo("recipe.json"));
        var sequencer = new InspectionSequencer(motor, reader, verifier, store, recipes, effective);

        return new Rig(motor, reader, verifier, store, recipes, sequencer, effective);
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

    /// <summary>
    /// 以「對標卡住」把機台推入 Faulted / Drive the rig into Faulted through a stalled align.
    ///
    /// 相對進給讓這件事變簡單了：對標一定要走 PulsesToNextMark 這段距離,
    /// 所以 SimulateStall 一設就必然卡住。
    /// 舊的絕對定位版本必須先把軸推離原點,否則 HomeAsync 的目標 0 已經到位,
    /// 等待迴圈一次都不會執行,SimulateStall 就卡不住任何東西。
    /// The relative feed simplifies this: an align always has PulsesToNextMark of travel
    /// to cover, so setting SimulateStall necessarily stalls it. The old absolute-position
    /// version had to park the axis off zero first, or HomeAsync's target of 0 was already
    /// satisfied, the wait loop never ran, and SimulateStall stalled nothing.
    ///
    /// alignTimeout 為 null 時由 <see cref="SequencerOptions.FeedTimeout"/> 把關,
    /// 走的是設備逾時路徑(DeviceTimeoutException);
    /// 給值時改由呼叫端的權杖取消,讓站別設定得以留寬而故障仍然來得快。
    /// 兩者的差別正是 WithTimeoutAsync 用外層權杖狀態所區分的「設備逾時」與「操作員停機」。
    /// With alignTimeout null the sequencer's own FeedTimeout is the guard, taking the
    /// device-timeout path (DeviceTimeoutException). When supplied, the caller's token
    /// cancels instead, so the options can stay generous while the fault still arrives
    /// promptly. The two are precisely the device-timeout versus operator-stop cases that
    /// WithTimeoutAsync separates by inspecting the outer token.
    /// </summary>
    private static async Task FaultViaStalledAlignAsync(Rig rig, TimeSpan? alignTimeout = null)
    {
        rig.Motor.SimulateStall = true;

        if (alignTimeout is null)
        {
            await Assert.ThrowsAsync<DeviceTimeoutException>(() => rig.Sequencer.InitializeAsync());
        }
        else
        {
            using var cts = new CancellationTokenSource(alignTimeout.Value);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => rig.Sequencer.InitializeAsync(cts.Token));
        }

        Assert.Equal(MachineState.Faulted, rig.Sequencer.State);
    }

    /// <summary>受測組合 / The assembled system under test, plus the handles a test needs to poke it.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly List<MachineState> _states = [];
        private readonly List<string> _logs = [];
        private readonly List<InspectionRecord> _completed = [];

        public Rig(
            MockMotorController motor,
            MockCodeReader reader,
            MockCharacterVerifier verifier,
            FakeStore store,
            RecipeManager recipes,
            InspectionSequencer sequencer,
            SequencerOptions options)
        {
            Motor = motor;
            Reader = reader;
            Verifier = verifier;
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

        public MockCodeReader Reader { get; }

        public MockCharacterVerifier Verifier { get; }

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
            await Reader.DisposeAsync();
            await Verifier.DisposeAsync();
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
