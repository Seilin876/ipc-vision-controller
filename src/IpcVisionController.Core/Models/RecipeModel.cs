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
    /// 一次觸發應回報的字符檢測區域數；<see cref="NoCheck"/> 表示本機種不做字符檢測 /
    /// Character regions expected from one trigger; <see cref="NoCheck"/> means this product
    /// has no character verification.
    ///
    /// 有設數量時要檢查數量：區域沒被觸發到就不會回報,而「沒回報」不等於「合格」。
    /// 設為 <see cref="NoCheck"/> 的唯一正當理由是感測器程式裡根本沒有 OCR 區域 ——
    /// 此時原本會以「區域數不足」判退每一張標籤,而那是設定表達不出需求,不是標籤有問題。
    /// When a count is set it is enforced: an untriggered region simply reports nothing, and
    /// "nothing reported" is not "passed". The one legitimate reason to disable it is a sensor
    /// program with no OCR region at all, where the count check otherwise rejects every single
    /// label — a configuration unable to express the requirement, not a defect on the label.
    ///
    /// 只關掉「數量」這一項,不關掉已回報區域的判定（見 <see cref="Machine.LabelJudge"/>）。
    /// 感測器既然回報了結果,失敗就是失敗;要真正停用字符檢測,device.json 的
    /// CharacterTextFields 也必須清空 —— 那兩個檔描述的是不同的事:
    /// 一個是「感測器輸出什麼」,一個是「這個機種要求什麼」。
    /// Only the count is disabled, not the judging of regions that did report (see
    /// <see cref="Machine.LabelJudge"/>): if the sensor returned a result, a failure is still a
    /// failure. Genuinely retiring character verification also means emptying
    /// CharacterTextFields in device.json — the two files describe different things, namely what
    /// the sensor emits versus what this product requires.
    ///
    /// <see cref="ExpectedCodeCount"/> 仍必須為正,所以本站永遠至少檢查一項,
    /// 不存在「全部關掉、一律 PASS」的配方。
    /// <see cref="ExpectedCodeCount"/> must still be positive, so the station always checks at
    /// least one thing; no recipe can switch everything off and pass every part.
    /// </summary>
    public int ExpectedCharacterRegionCount { get; set; } = 1;

    /// <summary>
    /// 一次進給的脈波數 / Pulses commanded for one feed.
    ///
    /// 為什麼在配方而不在機構設定 / Why this is per-product rather than mechanical:
    /// 一次進給要走「視野裡那幾張標籤」的距離,而標籤長度與一次進給涵蓋幾張,都隨機種變。
    /// 放在機構設定裡的話,換線時它不會跟著配方切換,料帶就會走錯距離。
    /// One feed has to cover the labels that sit in the field of view, and both the label length and how
    /// many fit in one feed change with the product. Kept among the mechanical settings it would not switch
    /// at a changeover, and the web would advance the wrong distance.
    ///
    /// 它同時決定「檢測站在下游幾格」/ It also decides how many pitches downstream the verifier sits:
    /// 字符檢測器鎖在固定位置,所以讀碼站到檢測站的物理距離不變;但那個距離換算成幾「格」,
    /// 取決於一格有多長。因此程式不要求現場自己算格數,而是用
    /// <see cref="Machine.SequencerOptions.InspectionStationDistancePulses"/> 除以本值 ——
    /// 換機種只改這一個數字,偏移就自動跟著對。人工換算的那一步從流程裡消失,
    /// 而那一步算錯是不會報錯的。
    /// The verifier is bolted in one place, so the physical distance from the reading station is fixed;
    /// how many pitches that distance amounts to depends on how long a pitch is. The program therefore does
    /// not ask anyone to work the pitch count out: it divides the fixed station distance by this value. A
    /// changeover edits one number and the offset follows, removing a manual conversion whose errors would
    /// never announce themselves.
    /// </summary>
    public int FeedPitchPulses { get; set; } = 10_000;

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

        // 0 是合法的（不檢查字符區域數,見屬性說明）,負數不是。
        // 條碼筆數仍要求為正,因此本站不可能變成「什麼都不檢查」。
        // Zero is legal — it disables the region count check, see the property — and a negative
        // number is not. The code count is still required to be positive, so the station cannot
        // be configured to check nothing at all.
        if (ExpectedCharacterRegionCount < 0)
        {
            throw new InvalidRecipeException(
                $"ExpectedCharacterRegionCount 不可為負,目前為 {ExpectedCharacterRegionCount}（0 表示不檢查）/ must not be negative, got {ExpectedCharacterRegionCount} (0 disables the check).");
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

        // 進給量為 0 代表料帶不動,同一張標籤會被反覆檢測並反覆寫入追溯紀錄;
        // 為負則是反向捲動,那不是這台機構做得到的事。
        // A zero pitch leaves the web still, so one label is inspected and logged over and over, while a
        // negative one would wind backwards, which this mechanism cannot do.
        if (FeedPitchPulses <= 0)
        {
            throw new InvalidRecipeException(
                $"FeedPitchPulses 必須為正整數,目前為 {FeedPitchPulses} / must be positive, got {FeedPitchPulses}.");
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
        FeedPitchPulses = FeedPitchPulses,
    };
}

/// <summary>配方不合法 / The recipe is not usable.</summary>
public sealed class InvalidRecipeException(string message) : Exception(message);
