using IpcVisionController.Core.Hal;
using IpcVisionController.Core.Models;

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

    // ── 步進驅動 / Stepper drive ─────────────────────────────────────────────

    private static MockMotorController NewMotor() => new() { ConnectLatency = Instant };

    private static async Task<MockMotorController> NewEnabledMotorAsync()
    {
        var motor = NewMotor();
        await motor.ConnectAsync(CancellationToken.None);
        await motor.EnableAsync(CancellationToken.None);
        return motor;
    }

    [Fact]
    public async Task Motor_MoveToAsync_ReturnsOnlyOnceInPosition()
    {
        await using var motor = await NewEnabledMotorAsync();

        await motor.MoveToAsync(500, speedPulsePerSecond: 100_000, CancellationToken.None);

        Assert.Equal(500, motor.CurrentPosition);
        Assert.True(motor.IsInPosition);
    }

    [Fact]
    public async Task Motor_MoveToAsync_HandlesNegativeDirection()
    {
        await using var motor = await NewEnabledMotorAsync();
        await motor.MoveToAsync(500, 100_000, CancellationToken.None);

        await motor.MoveToAsync(-200, 100_000, CancellationToken.None);

        Assert.Equal(-200, motor.CurrentPosition);
    }

    [Fact]
    public async Task Motor_MoveToAsync_BeforeEnable_Throws()
    {
        await using var motor = NewMotor();
        await motor.ConnectAsync(CancellationToken.None);

        // 未致能就下定位命令,實機會直接不動且不報錯 —— 模擬件必須報錯
        // On real hardware an un-enabled move silently does nothing; the mock must complain.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => motor.MoveToAsync(100, 1_000, CancellationToken.None));
    }

    [Fact]
    public async Task Motor_EnableAsync_BeforeConnect_Throws()
    {
        await using var motor = NewMotor();

        await Assert.ThrowsAsync<InvalidOperationException>(() => motor.EnableAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Motor_MoveToAsync_WithNonPositiveSpeed_Throws()
    {
        await using var motor = await NewEnabledMotorAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => motor.MoveToAsync(100, speedPulsePerSecond: 0, CancellationToken.None));
    }

    [Fact]
    public async Task Motor_MoveToAsync_HonoursCancellation()
    {
        await using var motor = await NewEnabledMotorAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // 速度極慢,取消一定發生在到位之前 / So slow that cancellation must land before arrival.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => motor.MoveToAsync(1_000_000, speedPulsePerSecond: 1, cts.Token));
    }

    [Fact]
    public async Task Motor_WhenStalled_NeverReachesPosition()
    {
        await using var motor = await NewEnabledMotorAsync();
        motor.SimulateStall = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => motor.MoveToAsync(500, 100_000, cts.Token));

        Assert.Equal(0, motor.CurrentPosition);
        Assert.False(motor.IsInPosition);
    }

    [Fact]
    public async Task Motor_HomeAsync_ReturnsToZero()
    {
        await using var motor = await NewEnabledMotorAsync();
        await motor.MoveToAsync(300, 100_000, CancellationToken.None);

        await motor.HomeAsync(CancellationToken.None);

        Assert.Equal(0, motor.CurrentPosition);
    }

    [Fact]
    public async Task Motor_DisconnectAsync_DropsEnableToo()
    {
        await using var motor = await NewEnabledMotorAsync();

        await motor.DisconnectAsync(CancellationToken.None);

        Assert.False(motor.IsConnected);
        Assert.False(motor.IsEnabled);
    }

    // ── 條碼讀取器 / Barcode scanner ─────────────────────────────────────────

    private static async Task<MockBarcodeScanner> NewConnectedScannerAsync()
    {
        var scanner = new MockBarcodeScanner(seed: 42) { MinLatency = Instant, MaxLatency = Instant };
        await scanner.ConnectAsync(CancellationToken.None);
        return scanner;
    }

    [Fact]
    public async Task Scanner_ReadAsync_BeforeConnect_Throws()
    {
        await using var scanner = new MockBarcodeScanner();

        await Assert.ThrowsAsync<InvalidOperationException>(() => scanner.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Scanner_ReadAsync_GeneratesCodeOfTheConfiguredLength()
    {
        await using var scanner = await NewConnectedScannerAsync();
        scanner.CodeLength = 17;

        var code = await scanner.ReadAsync(CancellationToken.None);

        Assert.Equal(17, code.Length);
    }

    [Fact]
    public async Task Scanner_ForcedCode_WinsOverGeneration()
    {
        await using var scanner = await NewConnectedScannerAsync();
        scanner.ForcedCode = "FIXED-CODE-1";

        Assert.Equal("FIXED-CODE-1", await scanner.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Scanner_ForceNoRead_RaisesDeviceReadException()
    {
        await using var scanner = await NewConnectedScannerAsync();
        scanner.ForceNoRead = true;

        // NOREAD 必須是可辨識的例外型別,上層才能只退這一件而不停線
        // A NOREAD must be its own exception type so the caller can reject the part
        // without stopping the line.
        await Assert.ThrowsAsync<DeviceReadException>(() => scanner.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Scanner_WithFullNoReadRate_AlwaysNoReads()
    {
        var scanner = new MockBarcodeScanner(seed: 7)
        {
            MinLatency = Instant,
            MaxLatency = Instant,
            NoReadRate = 1.0,
        };
        await using var _ = scanner;
        await scanner.ConnectAsync(CancellationToken.None);

        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<DeviceReadException>(() => scanner.ReadAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task Scanner_SameSeed_ProducesSameCodes()
    {
        // 可重現性是試機報告能被複查的前提 / Reproducibility is what makes a dry-run report reviewable.
        await using var first = await NewConnectedScannerAsync();
        await using var second = await NewConnectedScannerAsync();

        Assert.Equal(
            await first.ReadAsync(CancellationToken.None),
            await second.ReadAsync(CancellationToken.None));
    }

    // ── 影像感測器 / Vision sensor ───────────────────────────────────────────

    private static async Task<MockVisionSensor> NewConnectedVisionAsync(double ngRate = 0.0)
    {
        var vision = new MockVisionSensor(seed: 42)
        {
            MinLatency = Instant,
            MaxLatency = Instant,
            NgRate = ngRate,
        };
        await vision.ConnectAsync(CancellationToken.None);
        return vision;
    }

    [Fact]
    public async Task Vision_TriggerAsync_BeforeConnect_Throws()
    {
        await using var vision = new MockVisionSensor();

        await Assert.ThrowsAsync<InvalidOperationException>(() => vision.TriggerAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Vision_WithZeroNgRate_AlwaysReturnsOk()
    {
        await using var vision = await NewConnectedVisionAsync(ngRate: 0.0);

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(VisionResult.Ok, await vision.TriggerAsync(CancellationToken.None));
        }
    }

    [Fact]
    public async Task Vision_WithFullNgRate_AlwaysReturnsNg()
    {
        await using var vision = await NewConnectedVisionAsync(ngRate: 1.0);

        Assert.Equal(VisionResult.Ng, await vision.TriggerAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Vision_ForcedResult_WinsOverTheRandomVerdict()
    {
        await using var vision = await NewConnectedVisionAsync(ngRate: 1.0);
        vision.ForcedResult = VisionResult.Ok;

        Assert.Equal(VisionResult.Ok, await vision.TriggerAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Vision_ForcedLatency_HonoursCancellation()
    {
        await using var vision = await NewConnectedVisionAsync();
        vision.ForcedLatency = TimeSpan.FromSeconds(30);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vision.TriggerAsync(cts.Token));
    }
}
