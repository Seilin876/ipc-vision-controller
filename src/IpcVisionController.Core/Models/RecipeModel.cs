namespace IpcVisionController.Core.Models;

/// <summary>
/// 機種配方 / Product-model recipe.
/// 判定條碼長度的唯一依據 —— 換線時只需切換此配方。
/// The single source of truth for the expected barcode length; changing over
/// the line means swapping this recipe, nothing else.
/// </summary>
public sealed class RecipeModel
{
    /// <summary>機種名稱 / Product model name.</summary>
    public string ModelName { get; set; } = "DEFAULT";

    /// <summary>期望的條碼字元長度 / Expected barcode character length.</summary>
    public int BarcodeLength { get; set; } = 12;

    /// <summary>
    /// 驗證配方合法性 / Validate the recipe.
    /// 寧可在載入時就失敗，也不要讓不合法的配方污染整批判定結果。
    /// Fail at load time rather than let an invalid recipe corrupt a whole batch of verdicts.
    /// </summary>
    /// <exception cref="InvalidRecipeException">配方欄位不合法 / A field is invalid.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ModelName))
        {
            throw new InvalidRecipeException("ModelName 不可為空白 / ModelName must not be blank.");
        }

        if (BarcodeLength <= 0)
        {
            throw new InvalidRecipeException(
                $"BarcodeLength 必須為正整數，目前為 {BarcodeLength} / BarcodeLength must be positive, got {BarcodeLength}.");
        }
    }

    /// <summary>建立淺層複本，避免 UI 直接改動使用中的配方 / Shallow copy, so the UI cannot mutate the live recipe.</summary>
    public RecipeModel Clone() => new() { ModelName = ModelName, BarcodeLength = BarcodeLength };
}

/// <summary>配方不合法 / The recipe is not usable.</summary>
public sealed class InvalidRecipeException(string message) : Exception(message);
