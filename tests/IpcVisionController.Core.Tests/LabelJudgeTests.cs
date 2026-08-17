using IpcVisionController.Core.Machine;
using IpcVisionController.Core.Models;
using Xunit;

namespace IpcVisionController.Core.Tests;

/// <summary>
/// 判定規則的行為規格 / Behavioural spec for the judging rules.
///
/// 這是整台機器唯一「答錯就出貨不良品」的地方,所以規則逐條測,
/// 而且刻意不透過狀態機與模擬裝置 —— 一條規則錯了,失敗訊息就該直接指到那條規則。
/// This is the one place in the machine where being wrong ships a bad part, so every
/// rule is tested on its own and deliberately not through the state machine and mock
/// devices: when a rule breaks, the failure should name that rule.
///
/// 反向的規格同樣重要 / The negative spec matters just as much:
/// 「不檢查」的設定必須真的不檢查,否則現場會為了讓機器閉嘴而把整個檢查關掉。
/// A check that is switched off must really be off, or the line will switch the whole
/// inspection off just to stop the machine complaining.
/// </summary>
public sealed class LabelJudgeTests
{
    /// <summary>最寬鬆的配方：只要求一筆條碼與一個區域,不檢查長度與等級 / The loosest recipe.</summary>
    private static RecipeModel Recipe(
        int codes = 1,
        int length = RecipeModel.NoCheck,
        int? grade = null,
        int regions = 1) => new()
        {
            ModelName = "MODEL-A",
            ExpectedCodeCount = codes,
            BarcodeLength = length,
            MinimumCodeGrade = grade,
            ExpectedCharacterRegionCount = regions,
        };

    private static CodeResult Code(int index = 0, string? data = "ABC123456789", int? grade = 90, string? judge = null)
        => new(index, data, grade, judge ?? Verdict.Pass);

    private static CharacterResult Region(int index = 0, string? text = "LOT26A", string? judge = null)
        => new(index, text, judge ?? Verdict.Pass);

    // ── 合格 / The passing case ──────────────────────────────────────────────

    [Fact]
    public void Evaluate_WithEverythingInSpec_Passes()
    {
        var reason = LabelJudge.Evaluate(Recipe(), [Code()], [Region()]);

        // null 代表合格。回傳空字串會讓呼叫端的 null 判斷靜默失效。
        // Null means pass; returning an empty string would silently defeat the caller's null check.
        Assert.Null(reason);
    }

    [Fact]
    public void Evaluate_WithMoreResultsThanRequired_Passes()
    {
        // 目前規則是「少於即判退」,多讀到不判退。
        // 若日後認定多讀到代表視野看到隔壁標籤,這條測試就是要改的地方。
        // The rule today is "fewer is a reject"; extra results pass. If extras are later
        // deemed to mean the field of view is catching the neighbouring label, this test
        // is the one to change.
        var reason = LabelJudge.Evaluate(Recipe(codes: 1, regions: 1), [Code(0), Code(1)], [Region(0), Region(1)]);

        Assert.Null(reason);
    }

    // ── 條碼筆數 / Code count ────────────────────────────────────────────────

    [Fact]
    public void Evaluate_WithTooFewCodes_Rejects()
    {
        // 漏貼一張標籤的唯一防線 / The only line of defence against a missing label.
        var reason = LabelJudge.Evaluate(Recipe(codes: 2), [Code()], [Region()]);

        Assert.NotNull(reason);
        Assert.Contains("code count 1 < expected 2", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_WithNoCodesAtAll_Rejects()
    {
        var reason = LabelJudge.Evaluate(Recipe(), [], [Region()]);

        Assert.NotNull(reason);
        Assert.Contains("code count 0 < expected 1", reason, StringComparison.Ordinal);
    }

    // ── 條碼內容與長度 / Code payload and length ─────────────────────────────

    [Fact]
    public void Evaluate_WithAnUndecodedCode_Rejects()
    {
        var reason = LabelJudge.Evaluate(Recipe(), [Code(data: null, grade: null, judge: Verdict.Fail)], [Region()]);

        Assert.NotNull(reason);
        Assert.Contains("decoded to nothing", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_WithTheWrongCodeLength_Rejects()
    {
        // 長度不符通常代表機種掛錯 / A length mismatch usually means the wrong model is loaded.
        var reason = LabelJudge.Evaluate(Recipe(length: 12), [Code(data: "SHORT")], [Region()]);

        Assert.NotNull(reason);
        Assert.Contains("length 5 != expected 12", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_WithLengthCheckDisabled_IgnoresTheLength()
    {
        var reason = LabelJudge.Evaluate(Recipe(length: RecipeModel.NoCheck), [Code(data: "ANY-LENGTH-AT-ALL")], [Region()]);

        Assert.Null(reason);
    }

    [Fact]
    public void Evaluate_WithAnUndecodedCode_DoesNotAlsoComplainAboutItsLength()
    {
        var reason = LabelJudge.Evaluate(
            Recipe(length: 12, grade: 70),
            [Code(data: null, grade: null, judge: Verdict.Fail)],
            [Region()]);

        // 沒有內容就沒有長度可談。多報一條「長度 0 ≠ 12」只會讓現場找錯方向。
        // With no payload there is no length to discuss; an extra "length 0 != 12" would
        // send the operator looking in the wrong place.
        Assert.NotNull(reason);
        Assert.DoesNotContain("length", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("grade", reason, StringComparison.Ordinal);
    }

    // ── 感測器自身判定 / The sensor's own verdict ────────────────────────────

    [Fact]
    public void Evaluate_WhenTheReaderItselfRejects_Rejects()
    {
        var reason = LabelJudge.Evaluate(Recipe(), [Code(judge: Verdict.Fail)], [Region()]);

        Assert.NotNull(reason);
        Assert.Contains("rejected by the sensor", reason, StringComparison.Ordinal);
    }

    // ── 品質等級 / Code grade ────────────────────────────────────────────────

    [Fact]
    public void Evaluate_WithAGradeBelowTheMinimum_Rejects()
    {
        var reason = LabelJudge.Evaluate(Recipe(grade: 70), [Code(grade: 55)], [Region()]);

        Assert.NotNull(reason);
        Assert.Contains("grade 55 < minimum 70", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_WithAGradeExactlyAtTheMinimum_Passes()
    {
        // 下限是「含」,不是「大於」—— 邊界寫錯會讓剛好合格的批號整批誤退
        // The minimum is inclusive; getting the boundary wrong rejects an entire
        // just-acceptable batch.
        Assert.Null(LabelJudge.Evaluate(Recipe(grade: 70), [Code(grade: 70)], [Region()]));
    }

    [Fact]
    public void Evaluate_WithGradeCheckDisabled_IgnoresALowGrade()
    {
        Assert.Null(LabelJudge.Evaluate(Recipe(grade: null), [Code(grade: 1)], [Region()]));
    }

    [Fact]
    public void Evaluate_WhenAGradeIsRequiredButNotReported_Rejects()
    {
        var reason = LabelJudge.Evaluate(Recipe(grade: 70), [Code(grade: null)], [Region()]);

        // 感測器沒輸出等級卻當成合格,等於「已啟用品質管制」是一句空話
        // Passing a label whose grade the sensor never output makes "grade checking is
        // enabled" a claim with nothing behind it.
        Assert.NotNull(reason);
        Assert.Contains("reported no grade", reason, StringComparison.Ordinal);
    }

    // ── 字符區域 / Character regions ─────────────────────────────────────────

    [Fact]
    public void Evaluate_WithTooFewCharacterRegions_Rejects()
    {
        var reason = LabelJudge.Evaluate(Recipe(regions: 3), [Code()], [Region(0), Region(1)]);

        Assert.NotNull(reason);
        Assert.Contains("character regions 2 < expected 3", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_WithNoRegionsReported_Rejects()
    {
        // 「沒回報」不等於「合格」 / Nothing reported is not the same as passed.
        var reason = LabelJudge.Evaluate(Recipe(), [Code()], []);

        Assert.NotNull(reason);
        Assert.Contains("character regions 0 < expected 1", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_WithAnUnrecognisedRegion_Rejects()
    {
        var reason = LabelJudge.Evaluate(Recipe(), [Code()], [Region(text: null, judge: Verdict.Fail)]);

        Assert.NotNull(reason);
        Assert.Contains("recognised nothing", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_WhenARegionMismatchesTheMasterText_Rejects()
    {
        var reason = LabelJudge.Evaluate(Recipe(), [Code()], [Region(text: "L0T26A", judge: Verdict.Fail)]);

        Assert.NotNull(reason);
        Assert.Contains("did not match the master text", reason, StringComparison.Ordinal);
    }

    // ── 多重原因 / Multiple reasons ──────────────────────────────────────────

    [Fact]
    public void Evaluate_WithSeveralProblems_ReportsThemAll()
    {
        var reason = LabelJudge.Evaluate(
            Recipe(codes: 2, length: 12, grade: 70, regions: 2),
            [Code(data: "SHORT", grade: 40)],
            [Region(judge: Verdict.Fail)]);

        // 印刷調機時要一次看到所有不符項,逐條試錯會拖長停機時間
        // Dialling in a print job needs every mismatch at once; discovering them one
        // reject at a time lengthens the stoppage.
        Assert.NotNull(reason);
        Assert.Contains("code count 1 < expected 2", reason, StringComparison.Ordinal);
        Assert.Contains("length 5 != expected 12", reason, StringComparison.Ordinal);
        Assert.Contains("grade 40 < minimum 70", reason, StringComparison.Ordinal);
        Assert.Contains("character regions 1 < expected 2", reason, StringComparison.Ordinal);
        Assert.Contains("did not match the master text", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_NamesTheOffendingIndex()
    {
        var reason = LabelJudge.Evaluate(
            Recipe(codes: 3, length: 12),
            [Code(0), Code(1, data: "SHORT"), Code(2)],
            [Region()]);

        // 三筆條碼裡是哪一筆出問題,現場需要直接知道
        // With three codes in view, the line needs to be told which one is at fault.
        Assert.NotNull(reason);
        Assert.Contains("code #1 length", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("code #0 length", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("code #2 length", reason, StringComparison.Ordinal);
    }
}
