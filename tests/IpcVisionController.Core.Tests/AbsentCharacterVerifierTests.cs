using IpcVisionController.Core.Hal;
using IpcVisionController.Core.Machine;
using IpcVisionController.Core.Models;
using Xunit;

namespace IpcVisionController.Core.Tests;

/// <summary>
/// 「尚未導入字符檢測」的行為 / How an uninstalled character verifier behaves.
///
/// 這是目前現場實際會走的路徑：只有 SR-X300,IV4 之後才上。
/// 因此這裡驗的不只是一個空清單,而是「沒有這台裝置」與配方之間的合約 ——
/// 那個合約錯了,後果是整條線在錯誤的一端排查：明明是設定不相符,看起來卻像每張標籤都不良。
/// This is the path the line actually takes today: an SR-X300 only, with the IV4 still to come. So
/// what is under test is not merely an empty list but the contract between "no such device" and the
/// recipe. Get that contract wrong and the line diagnoses from the wrong end: a configuration
/// mismatch that looks like every label being defective.
/// </summary>
public sealed class AbsentCharacterVerifierTests
{
    private static RecipeModel Recipe(int regions) => new()
    {
        ModelName = "DRYRUN",
        ExpectedCodeCount = 1,
        BarcodeLength = RecipeModel.NoCheck,
        MinimumCodeGrade = null,
        ExpectedCharacterRegionCount = regions,
    };

    private static CodeResult Code() => new(0, "ABC123456789", 90, Verdict.Pass);

    [Fact]
    public async Task TriggerAsync_ReportsNoRegions()
    {
        // 回空清單而不是「一律合格」:後果要由配方決定,這台假裝置自己不做任何判斷。
        // An empty list rather than an automatic pass: the consequence is the recipe's to decide, and
        // this stand-in judges nothing itself.
        await using var verifier = new AbsentCharacterVerifier();

        Assert.Empty(await verifier.TriggerAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Connect_Succeeds_SoAMissingDeviceIsNotAFault()
    {
        // 沒有連線可言。回報未連線會讓協調器把「這台機器沒有這項檢查」當成初始化失敗,
        // 而那會讓一台組態正確的機器開不了工。
        // There is no link. Reporting otherwise would make the sequencer treat "this machine has no
        // such check" as an initialise failure, stopping a correctly configured machine from running.
        await using var verifier = new AbsentCharacterVerifier();

        await verifier.ConnectAsync(CancellationToken.None);

        Assert.True(verifier.IsConnected);
    }

    [Fact]
    public void Name_SaysTheDeviceIsNotInstalled()
    {
        // 這個名稱會出現在初始化訊息裡,是現場唯一會讀到「這台機器沒有字符檢測」的地方
        // The name appears in the initialise log, the one place the line reads that this machine has
        // no character verification.
        Assert.Contains("未導入", new AbsentCharacterVerifier().Name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithARecipeAskingForNoRegions_TheLabelPasses()
    {
        // 目前現場要的組合：沒有 IV4、配方字符區域數 0 → 判定只看條碼。
        // The combination the line needs today: no IV4 and a region count of zero, so the verdict
        // rests on the code alone.
        await using var verifier = new AbsentCharacterVerifier();
        var regions = await verifier.TriggerAsync(CancellationToken.None);

        var reason = LabelJudge.Evaluate(Recipe(RecipeModel.NoCheck), [Code()], regions);

        Assert.Null(reason);
    }

    [Fact]
    public async Task WithARecipeAskingForRegions_TheLabelRejectsOnTheCount()
    {
        // 配方要求了一項機器上沒有的檢查 —— 判退是對的答案,而且原因必須說出是「區域數不足」,
        // 否則現場會以為標籤有問題,去調印刷、調焦距,而真正該改的是配方或 device.json。
        // The recipe demanded a check the machine does not have: rejecting is the right answer, and
        // the reason has to name the region count. Otherwise the line blames the label and starts
        // adjusting print and focus, when what needs changing is the recipe or device.json.
        await using var verifier = new AbsentCharacterVerifier();
        var regions = await verifier.TriggerAsync(CancellationToken.None);

        var reason = LabelJudge.Evaluate(Recipe(2), [Code()], regions);

        Assert.NotNull(reason);
        Assert.Contains("character regions 0 < expected 2", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceSet_ExcludesItFromTheFrameSources()
    {
        // 沒有裝置就沒有電文。把它列入電文來源,畫面會為一台不存在的裝置開一個永遠空白的
        // 電文區,而導入時那會被讀成「IV4 接了但沒回應」。
        // No device means no frames. Listing it would give the UI a permanently empty frame log for a
        // device that does not exist, which during commissioning reads as "the IV4 is attached but
        // silent".
        var devices = new DeviceSet(
            new MockMotorController(),
            new MockCodeReader(seed: 1),
            new AbsentCharacterVerifier(),
            AreMocks: true);

        Assert.Empty(devices.FrameSources);
        Assert.Equal(3, devices.All.Count);
    }
}
