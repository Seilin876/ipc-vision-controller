namespace IpcVisionController.Core.Models;

/// <summary>
/// 機種配方 / Product-model recipe.
/// 判定規則的唯一依據 —— 換線時只需切換此配方。
/// The single source of truth for the judging rules; changing over the line means
/// swapping this recipe, nothing else.
///
/// 與 <see cref="Machine.SequencerOptions"/> 的分工 / Split of responsibility:
/// 本檔是「這個機種怎麼判」（換線會變）,SequencerOptions 是「這台機構長怎樣」（幾乎不變）。
/// This file is how *this product* is judged (changes at every changeover);
/// SequencerOptions describes *this machine's mechanics* (essentially fixed).
/// </summary>
public sealed class RecipeModel
{
    /// <summary>不檢查該項目的哨兵值 / Sentinel meaning "do not check this item".</summary>
    public const int NoCheck = 0;

    /// <summary>機種名稱 / Product model name.</summary>
    public string ModelName { get; set; } = "DEFAULT";

    /// <summary>
    /// 一次觸發應讀到的條碼筆數 / Codes expected from one trigger.
    /// 少於此數即判退：讀碼器只回一筆而配方要兩筆,代表有一張標籤漏印或漏貼,
    /// 這正是「只看有沒有讀到」會漏掉的不良。
    /// Fewer than this is a reject: one code back when the recipe wants two means a
    /// label is missing or unprinted — exactly the defect that a bare "did we read
    /// anything?" check lets through.
    /// </summary>
    public int ExpectedCodeCount { get; set; } = 1;

    /// <summary>
    /// 期望的條碼字元長度；<see cref="NoCheck"/> 表示不檢查 /
    /// Expected barcode character length; <see cref="NoCheck"/> disables the check.
    /// 長度不符通常代表機種掛錯 / A mismatch usually means the wrong model is loaded.
    /// </summary>
    public int BarcodeLength { get; set; } = 12;

    /// <summary>
    /// 條碼品質等級下限；null 表示不檢查 / Lower bound on code quality; null disables the check.
    /// 刻度由感測器決定（見 <see cref="CodeResult.Grade"/>）,本欄只是門檻。
    /// The scale is the sensor's (see <see cref="CodeResult.Grade"/>); this is only the threshold.
    /// </summary>
    public int? MinimumCodeGrade { get; set; }

    /// <summary>
    /// 一次觸發應回報的字符檢測區域數 / Character regions expected from one trigger.
    /// 同樣要檢查數量：區域沒被觸發到就不會回報,而「沒回報」不等於「合格」。
    /// The count matters here too: an untriggered region simply reports nothing, and
    /// "nothing reported" is not the same as "passed".
    /// </summary>
    public int ExpectedCharacterRegionCount { get; set; } = 1;

    /// <summary>
    /// 驗證配方合法性 / Validate the recipe.
    /// 寧可在載入時就失敗,也不要讓不合法的配方污染整批判定結果。
    /// Fail at load time rather than let an invalid recipe corrupt a whole batch of verdicts.
    /// </summary>
    /// <exception cref="InvalidRecipeException">配方欄位不合法 / A field is invalid.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ModelName))
        {
            throw new InvalidRecipeException("ModelName 不可為空白 / ModelName must not be blank.");
        }

        // 期望 0 筆等於整台機器不檢查任何東西,一律 PASS —— 幾乎確定是打錯,不是意圖
        // Expecting zero results makes the whole station pass everything; that is almost
        // certainly a typo rather than an intent.
        if (ExpectedCodeCount <= 0)
        {
            throw new InvalidRecipeException(
                $"ExpectedCodeCount 必須為正整數,目前為 {ExpectedCodeCount} / must be positive, got {ExpectedCodeCount}.");
        }

        if (ExpectedCharacterRegionCount <= 0)
        {
            throw new InvalidRecipeException(
                $"ExpectedCharacterRegionCount 必須為正整數,目前為 {ExpectedCharacterRegionCount} / must be positive, got {ExpectedCharacterRegionCount}.");
        }

        if (BarcodeLength < 0)
        {
            throw new InvalidRecipeException(
                $"BarcodeLength 不可為負,目前為 {BarcodeLength}（0 表示不檢查）/ must not be negative, got {BarcodeLength} (0 disables the check).");
        }

        if (MinimumCodeGrade is < 0)
        {
            throw new InvalidRecipeException(
                $"MinimumCodeGrade 不可為負,目前為 {MinimumCodeGrade} / must not be negative, got {MinimumCodeGrade}.");
        }
    }

    /// <summary>建立複本,避免 UI 直接改動使用中的配方 / A copy, so the UI cannot mutate the live recipe.</summary>
    public RecipeModel Clone() => new()
    {
        ModelName = ModelName,
        ExpectedCodeCount = ExpectedCodeCount,
        BarcodeLength = BarcodeLength,
        MinimumCodeGrade = MinimumCodeGrade,
        ExpectedCharacterRegionCount = ExpectedCharacterRegionCount,
    };
}

/// <summary>配方不合法 / The recipe is not usable.</summary>
public sealed class InvalidRecipeException(string message) : Exception(message);
