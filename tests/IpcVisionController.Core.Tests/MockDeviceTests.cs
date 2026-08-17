using IpcVisionController.Core.Hal;
using IpcVisionController.Core.Models;
using Xunit;

namespace IpcVisionController.Core.Tests;

/// <summary>
/// 模擬裝置的行為規格 / Behavioural spec for the mock devices.
///
/// 模擬件也要測：上層的逾時與故障處理全都靠這些測試鉤子驅動,
/// 鉤子本身壞了,上層的測試就會給出假的綠燈。
/// The mocks need tests of their own: every timeout and fault test upstream is driven
/// by these hooks, so a broken hook produces a false green light higher up.
/// </summary>
public sealed class MockDeviceTests
{
    private static readonly TimeSpan Instant = TimeSpan.FromMilliseconds(1);

    // ── 進給軸 / Feed axis ───────────────────────────────────────────────────

    /// <summary>對標行程壓到一個伺服週期,讓測試不必等真的捲料 / One servo cycle's worth of align, so tests do not wait on a real wind.</summary>
    private static MockMotorController NewMotor() => new()
    {
        ConnectLatency = Instant,
        PulsesToNextMark = 500,
        AlignSpeedPulsePerSecond = 100_000,
    };

    private static async Task<MockMotorController> NewEnabledMotorAsync()
    {
        var motor = NewMotor();
        await motor.ConnectAsync(CancellationToken.None);
        await motor.EnableAsync(CancellationToken.None);
        return motor;
    }

    [Fact]
    public async Task Motor_FeedAsync_ReturnsOnlyOnceInPosition()
    {
        await using var motor = await NewEnabledMotorAsync();

        await motor.FeedAsync(500, speedPulsePerSecond: 100_000, CancellationToken.None);

        Assert.Equal(500, motor.CurrentPosition);
        Assert.True(motor.IsInPosition);
    }

    [Fact]
    public async Task Motor_FeedAsync_AccumulatesAcrossCalls()
    {
        await using var motor = await NewEnabledMotorAsync();

        await motor.FeedAsync(500, 100_000, CancellationToken.None);
        await motor.FeedAsync(300, 100_000, CancellationToken.None);

        // 相對進給的重點：位置是累加的,不是每次重設
        // The point of a relative feed: the count accumulates rather than resetting.
        Assert.Equal(800, motor.CurrentPosition);
    }

    [Fact]
    public async Task Motor_FeedAsync_LandsExactlyOnTheRequestedDistance()
    {
        await using var motor = await NewEnabledMotorAsync();

        // 進給量不是每週期步距的整數倍時,不可超衝 —— 超衝在料帶上就是切歪
        // When the distance is not a whole number of per-cycle steps it must not
        // overshoot; on a web an overshoot is a misplaced cut.
        await motor.FeedAsync(1_250, speedPulsePerSecond: 100_000, CancellationToken.None);

        Assert.Equal(1_250, motor.CurrentPosition);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public async Task Motor_FeedAsync_WithNonPositiveDistance_Throws(int stepPulses)
    {
        await using var motor = await NewEnabledMotorAsync();

        // 倒轉會把已檢測過的標籤送回鏡頭前,重複計入追溯紀錄
        // Reversing pushes already-inspected labels back under the sensors and double-counts them.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => motor.FeedAsync(stepPulses, 1_000, CancellationToken.None));
    }

    [Fact]
    public async Task Motor_FeedAsync_WithNonPositiveSpeed_Throws()
    {
        await using var motor = await NewEnabledMotorAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => motor.FeedAsync(100, speedPulsePerSecond: 0, CancellationToken.None));
    }

    [Fact]
    public async Task Motor_FeedAsync_BeforeEnable_Throws()
    {
        await using var motor = NewMotor();
        await motor.ConnectAsync(CancellationToken.None);

        // 未致能就下進給命令,實機會直接不動且不報錯 —— 模擬件必須報錯
        // On real hardware an un-enabled feed silently does nothing; the mock must complain.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => motor.FeedAsync(100, 1_000, CancellationToken.None));
    }

    [Fact]
    public async Task Motor_EnableAsync_BeforeConnect_Throws()
    {
        await using var motor = NewMotor();

        await Assert.ThrowsAsync<InvalidOperationException>(() => motor.EnableAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Motor_FeedAsync_HonoursCancellation()
    {
        await using var motor = await NewEnabledMotorAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // 速度極慢,取消一定發生在到位之前 / So slow that cancellation must land before arrival.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => motor.FeedAsync(1_000_000, speedPulsePerSecond: 1, cts.Token));
    }

    [Fact]
    public async Task Motor_WhenStalled_NeverReachesPosition()
    {
        await using var motor = await NewEnabledMotorAsync();
        motor.SimulateStall = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => motor.FeedAsync(500, 100_000, cts.Token));

        Assert.Equal(0, motor.CurrentPosition);
        Assert.False(motor.IsInPosition);
    }

    [Fact]
    public async Task Motor_HomeAsync_WindsToTheMarkThenZeroesTheCount()
    {
        await using var motor = await NewEnabledMotorAsync();
        await motor.FeedAsync(300, 100_000, CancellationToken.None);

        await motor.HomeAsync(CancellationToken.None);

        // 對標不是回到某個座標,而是把累計量重新起算
        // An align does not return to a coordinate; it restarts the accumulated count.
        Assert.Equal(0, motor.CurrentPosition);
    }

    [Fact]
    public async Task Motor_HomeAsync_WhenCancelledMidWind_LeavesTheCountAlone()
    {
        await using var motor = await NewEnabledMotorAsync();
        await motor.FeedAsync(300, 100_000, CancellationToken.None);
        motor.SimulateStall = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => motor.HomeAsync(cts.Token));

        // 對標到一半被中斷卻歸零,帳面會說「剛對過標」而料帶其實停在半路
        // Zeroing after an interrupted align would claim the web is freshly registered
        // when it is in fact stopped halfway.
        Assert.Equal(300, motor.CurrentPosition);
    }

    [Fact]
    public async Task Motor_DisconnectAsync_DropsEnableToo()
    {
        await using var motor = await NewEnabledMotorAsync();

        await motor.DisconnectAsync(CancellationToken.None);

        Assert.False(motor.IsConnected);
        Assert.False(motor.IsEnabled);
    }

    // ── 條碼讀取器 / Code reader ─────────────────────────────────────────────

    private static async Task<MockCodeReader> NewConnectedReaderAsync(double noReadRate = 0.0)
    {
        var reader = new MockCodeReader(seed: 42)
        {
            MinLatency = Instant,
            MaxLatency = Instant,
            NoReadRate = noReadRate,
        };
        await reader.ConnectAsync(CancellationToken.None);
        return reader;
    }

    [Fact]
    public async Task Reader_TriggerAsync_BeforeConnect_Throws()
    {
        await using var reader = new MockCodeReader();

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.TriggerAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reader_TriggerAsync_ReturnsOneResultPerCodeInView()
    {
        await using var reader = await NewConnectedReaderAsync();
        reader.CodesPerTrigger = 3;

        var results = await reader.TriggerAsync(CancellationToken.None);

        // 一次觸發多筆是整個重構的理由：只留最後一筆等於丟掉不良品的證據
        // Many results per trigger is the whole reason for this refactor; keeping only the
        // last one discards the evidence for a reject.
        Assert.Equal(3, results.Count);
        Assert.Equal([0, 1, 2], results.Select(r => r.Index));
    }

    [Fact]
    public async Task Reader_TriggerAsync_GeneratesCodesOfTheConfiguredLength()
    {
        await using var reader = await NewConnectedReaderAsync();
        reader.CodeLength = 17;

        var results = await reader.TriggerAsync(CancellationToken.None);

        Assert.All(results, r => Assert.Equal(17, r.Data!.Length));
    }

    [Fact]
    public async Task Reader_TriggerAsync_ReportsTheConfiguredGrade()
    {
        await using var reader = await NewConnectedReaderAsync();
        reader.Grade = 55;

        var result = Assert.Single(await reader.TriggerAsync(CancellationToken.None));

        Assert.Equal(55, result.Grade);
    }

    [Fact]
    public async Task Reader_ForcedCodes_WinOverGeneration()
    {
        await using var reader = await NewConnectedReaderAsync();
        reader.ForcedCodes = ["FIRST-CODE-1", "SECOND-CODE"];

        var results = await reader.TriggerAsync(CancellationToken.None);

        Assert.Equal(["FIRST-CODE-1", "SECOND-CODE"], results.Select(r => r.Data));
        Assert.All(results, r => Assert.Equal(Verdict.Pass, r.Judge));
    }

    [Fact]
    public async Task Reader_ForcedCodes_WithANullEntry_YieldsAnUndecodedSlot()
    {
        await using var reader = await NewConnectedReaderAsync();
        reader.ForcedCodes = ["GOOD-CODE-01", null];

        var results = await reader.TriggerAsync(CancellationToken.None);

        // 「有格位但解不出來」與「整批沒讀到」是兩種不良,必須分得開
        // "A slot was there but would not decode" and "nothing was read at all" are two
        // different defects and must stay distinguishable.
        Assert.Equal(2, results.Count);
        Assert.Null(results[1].Data);
        Assert.Null(results[1].Grade);
        Assert.Equal(Verdict.Fail, results[1].Judge);
    }

    [Fact]
    public async Task Reader_ForceNoRead_YieldsAnEmptyListNotAnException()
    {
        await using var reader = await NewConnectedReaderAsync();
        reader.ForceNoRead = true;

        // 完全沒讀到是工件問題,不是設備故障 —— 拋例外會讓整條線停下來
        // Reading nothing is a part problem, not an equipment fault; throwing would stop the line.
        Assert.Empty(await reader.TriggerAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reader_WithFullNoReadRate_ReturnsNothing()
    {
        await using var reader = await NewConnectedReaderAsync(noReadRate: 1.0);
        reader.CodesPerTrigger = 3;

        Assert.Empty(await reader.TriggerAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reader_SimulateFault_RaisesDeviceFault()
    {
        await using var reader = await NewConnectedReaderAsync();
        reader.SimulateFault = true;

        // 感測器自身異常必須是例外,上層才會停線而不是默默判退整批
        // A sensor-level fault must be an exception, so the caller stops the line instead
        // of quietly rejecting a whole batch.
        await Assert.ThrowsAsync<DeviceFaultException>(() => reader.TriggerAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Reader_ForcedLatency_HonoursCancellation()
    {
        await using var reader = await NewConnectedReaderAsync();
        reader.ForcedLatency = TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.TriggerAsync(cts.Token));
    }

    [Fact]
    public async Task Reader_SameSeed_ProducesSameCodes()
    {
        // 可重現性是試機報告能被複查的前提 / Reproducibility is what makes a dry-run report reviewable.
        await using var first = await NewConnectedReaderAsync();
        await using var second = await NewConnectedReaderAsync();

        Assert.Equal(
            (await first.TriggerAsync(CancellationToken.None)).Select(r => r.Data),
            (await second.TriggerAsync(CancellationToken.None)).Select(r => r.Data));
    }

    // ── 字符檢測器 / Character verifier ──────────────────────────────────────

    private static async Task<MockCharacterVerifier> NewConnectedVerifierAsync(double failRate = 0.0)
    {
        var verifier = new MockCharacterVerifier(seed: 42)
        {
            MinLatency = Instant,
            MaxLatency = Instant,
            FailRate = failRate,
        };
        await verifier.ConnectAsync(CancellationToken.None);
        return verifier;
    }

    [Fact]
    public async Task Verifier_TriggerAsync_BeforeConnect_Throws()
    {
        await using var verifier = new MockCharacterVerifier();

        await Assert.ThrowsAsync<InvalidOperationException>(() => verifier.TriggerAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Verifier_TriggerAsync_ReturnsOneResultPerEnabledRegion()
    {
        await using var verifier = await NewConnectedVerifierAsync();
        verifier.RegionsPerTrigger = 4;

        var results = await verifier.TriggerAsync(CancellationToken.None);

        Assert.Equal(4, results.Count);
        Assert.Equal([0, 1, 2, 3], results.Select(r => r.Index));
    }

    [Fact]
    public async Task Verifier_WithZeroFailRate_PassesEveryRegion()
    {
        await using var verifier = await NewConnectedVerifierAsync(failRate: 0.0);
        verifier.RegionsPerTrigger = 5;

        var results = await verifier.TriggerAsync(CancellationToken.None);

        Assert.All(results, r => Assert.Equal(Verdict.Pass, r.Judge));
    }

    [Fact]
    public async Task Verifier_WithFullFailRate_StillReportsTheRecognisedText()
    {
        await using var verifier = await NewConnectedVerifierAsync(failRate: 1.0);
        verifier.RecognisedText = "LOT99Z";

        var result = Assert.Single(await verifier.TriggerAsync(CancellationToken.None));

        // 判退的區域仍要回報讀到什麼,否則現場無從得知印錯成什麼
        // A failing region must still report what it read, or the line cannot tell what
        // was misprinted.
        Assert.Equal(Verdict.Fail, result.Judge);
        Assert.Equal("LOT99Z", result.Text);
    }

    [Fact]
    public async Task Verifier_ForcedTexts_WithANullEntry_MarksThatRegionUnrecognised()
    {
        await using var verifier = await NewConnectedVerifierAsync();
        verifier.ForcedTexts = ["LOT26A", null];

        var results = await verifier.TriggerAsync(CancellationToken.None);

        Assert.Equal("LOT26A", results[0].Text);
        Assert.Null(results[1].Text);
        Assert.Equal(Verdict.Fail, results[1].Judge);
    }

    [Fact]
    public async Task Verifier_ForceNoResult_YieldsAnEmptyList()
    {
        await using var verifier = await NewConnectedVerifierAsync();
        verifier.ForceNoResult = true;

        Assert.Empty(await verifier.TriggerAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Verifier_SimulateFault_RaisesDeviceFault()
    {
        await using var verifier = await NewConnectedVerifierAsync();
        verifier.SimulateFault = true;

        await Assert.ThrowsAsync<DeviceFaultException>(() => verifier.TriggerAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Verifier_ForcedLatency_HonoursCancellation()
    {
        await using var verifier = await NewConnectedVerifierAsync();
        verifier.ForcedLatency = TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verifier.TriggerAsync(cts.Token));
    }
}
