using IpcVisionController.Core.Models;
using IpcVisionController.Core.Recipes;
using Xunit;

namespace IpcVisionController.Core.Tests;

/// <summary>
/// 配方讀寫的行為規格 / Behavioural spec for recipe persistence.
/// 配方錯了會讓一整批工件誤判,所以「不合法就失敗」比「盡量吃下去」重要。
/// A wrong recipe mis-judges an entire batch, so failing loudly beats being lenient.
/// </summary>
public sealed class RecipeManagerTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    private RecipeManager NewManager(string fileName = "recipe.json")
        => new(_workspace.PathTo(fileName));

    [Fact]
    public async Task LoadAsync_WhenFileMissing_WritesDefaultSoTheLineHasSomethingToEdit()
    {
        var manager = NewManager();

        var recipe = await manager.LoadAsync();

        Assert.True(File.Exists(manager.FilePath));
        Assert.Equal("DEFAULT", recipe.ModelName);
        Assert.Equal(12, recipe.BarcodeLength);
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsEveryField()
    {
        var manager = NewManager();
        await manager.SaveAsync(new RecipeModel
        {
            ModelName = "MODEL-B",
            ExpectedCodeCount = 3,
            BarcodeLength = 20,
            MinimumCodeGrade = 65,
            ExpectedCharacterRegionCount = 4,
            FeedPitchPulses = 24_000,
        });

        // 另開一個實例,確保讀的是磁碟而不是記憶體快取
        // A second instance proves the value came off disk, not from an in-memory cache.
        var reloaded = await NewManager().LoadAsync();

        // 逐欄檢查而非只看兩欄:漏掉的欄位會靜靜地退回預設值,
        // 換線時看起來配方已載入,判定用的卻是上一個機種的門檻。
        // Every field, not just two: a field missed by the serialiser or by Clone() silently
        // falls back to its default, so after a changeover the recipe looks loaded while the
        // judging still uses the previous model's thresholds.
        Assert.Equal("MODEL-B", reloaded.ModelName);
        Assert.Equal(3, reloaded.ExpectedCodeCount);
        Assert.Equal(20, reloaded.BarcodeLength);
        Assert.Equal(65, reloaded.MinimumCodeGrade);
        Assert.Equal(4, reloaded.ExpectedCharacterRegionCount);
        Assert.Equal(24_000, reloaded.FeedPitchPulses);
    }

    [Fact]
    public async Task LoadAsync_IsCaseInsensitive_BecauseEngineersHandEditTheFile()
    {
        var manager = NewManager();
        await File.WriteAllTextAsync(manager.FilePath, """
            { "modelname": "MODEL-C", "barcodelength": 8 }
            """);

        var recipe = await manager.LoadAsync();

        Assert.Equal("MODEL-C", recipe.ModelName);
        Assert.Equal(8, recipe.BarcodeLength);
    }

    [Fact]
    public async Task LoadAsync_WithMalformedJson_ThrowsInvalidRecipe()
    {
        var manager = NewManager();
        await File.WriteAllTextAsync(manager.FilePath, "{ this is not json");

        await Assert.ThrowsAsync<InvalidRecipeException>(() => manager.LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_WithZeroBarcodeLength_DisablesTheLengthCheck()
    {
        var manager = NewManager();
        await File.WriteAllTextAsync(manager.FilePath, """
            { "ModelName": "MODEL-D", "BarcodeLength": 0 }
            """);

        var recipe = await manager.LoadAsync();

        // 0 是 NoCheck 哨兵,不是不合法值：有些機種的條碼長度本來就不固定,
        // 對這種機種而言「不檢查長度」是正確設定,載入時不該失敗。
        // Zero is the NoCheck sentinel, not an invalid value: some models genuinely carry
        // variable-length codes, and for those "do not check the length" is the correct
        // setting rather than a load-time failure.
        Assert.Equal(RecipeModel.NoCheck, recipe.BarcodeLength);
    }

    [Fact]
    public async Task LoadAsync_WithNegativeBarcodeLength_ThrowsInvalidRecipe()
    {
        var manager = NewManager();
        await File.WriteAllTextAsync(manager.FilePath, """
            { "ModelName": "MODEL-D", "BarcodeLength": -1 }
            """);

        // 負長度無論如何都比不中,會讓整批工件判退,寧可在載入時就爆
        // A negative length can never match, so it would reject every part; fail at load
        // time instead.
        await Assert.ThrowsAsync<InvalidRecipeException>(() => manager.LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_WithZeroExpectedCodeCount_ThrowsInvalidRecipe()
    {
        var manager = NewManager();
        await File.WriteAllTextAsync(manager.FilePath, """
            { "ModelName": "MODEL-H", "ExpectedCodeCount": 0 }
            """);

        // 期望 0 筆條碼等於讀碼站什麼都不檢查,一律 PASS —— 是打錯,不是意圖
        // Expecting zero codes makes the reading station check nothing and pass everything;
        // that is a typo, not an intent.
        await Assert.ThrowsAsync<InvalidRecipeException>(() => manager.LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_WithZeroExpectedCharacterRegionCount_MeansNoCharacterCheck()
    {
        var manager = NewManager();
        await File.WriteAllTextAsync(manager.FilePath, """
            { "ModelName": "MODEL-I", "ExpectedCharacterRegionCount": 0 }
            """);

        // 與條碼筆數不同,0 個區域是合法設定:感測器程式裡沒有 OCR 區域的機種確實存在,
        // 拒絕載入的話那些機種每張標籤都會以「區域數不足」判退。
        // 讀碼筆數仍要求為正（見上一個測試）,所以本站不會變成什麼都不檢查。
        // Unlike the code count, zero regions is a legal configuration: products whose sensor
        // program has no OCR region do exist, and refusing the recipe rejects every one of their
        // labels on the region count. The code count is still required to be positive (see the
        // test above), so the station never ends up checking nothing.
        var recipe = await manager.LoadAsync();

        Assert.Equal(RecipeModel.NoCheck, recipe.ExpectedCharacterRegionCount);
    }

    [Fact]
    public async Task LoadAsync_WithNegativeExpectedCharacterRegionCount_ThrowsInvalidRecipe()
    {
        var manager = NewManager();
        await File.WriteAllTextAsync(manager.FilePath, """
            { "ModelName": "MODEL-I2", "ExpectedCharacterRegionCount": -1 }
            """);

        // 0 有意義,負數沒有 —— 那只會是手改配方時打錯
        // Zero means something; a negative number cannot, and only arises from a typo in a
        // hand-edited recipe.
        await Assert.ThrowsAsync<InvalidRecipeException>(() => manager.LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_WithANonPositiveFeedPitch_ThrowsInvalidRecipe()
    {
        var manager = NewManager();
        await File.WriteAllTextAsync(manager.FilePath, """
            { "ModelName": "MODEL-J", "FeedPitchPulses": 0 }
            """);

        // 進給量為 0 代表料帶不動,同一張標籤會被反覆檢測並反覆寫入追溯紀錄 ——
        // 良率看起來正常,而資料庫裡是同一張標籤的幾百筆紀錄。
        // A zero pitch leaves the web still, so one label is inspected and logged over and over: the yield
        // looks normal while the database fills with hundreds of rows for a single label.
        await Assert.ThrowsAsync<InvalidRecipeException>(() => manager.LoadAsync());
    }

    [Fact]
    public async Task LoadAsync_CarriesTheFeedPitch()
    {
        var manager = NewManager();
        await File.WriteAllTextAsync(manager.FilePath, """
            { "ModelName": "MODEL-K", "FeedPitchPulses": 24000 }
            """);

        // 這個值同時決定料帶走多遠、以及檢測站在下游幾格,所以它必須真的從檔案帶出來
        // —— 靜默用回預設值的後果是兩件事一起錯。
        // This value decides both how far the web moves and how many pitches downstream the verifier sits, so
        // it has to genuinely come from the file: silently falling back to a default gets both wrong at once.
        var recipe = await manager.LoadAsync();

        Assert.Equal(24_000, recipe.FeedPitchPulses);
    }

    [Fact]
    public async Task LoadAsync_WithNegativeMinimumCodeGrade_ThrowsInvalidRecipe()
    {
        var manager = NewManager();
        await File.WriteAllTextAsync(manager.FilePath, """
            { "ModelName": "MODEL-J", "MinimumCodeGrade": -1 }
            """);

        // 等級下限為負代表任何等級都過關,卻讓人誤以為品質管制已啟用 ——
        // 不檢查要寫 null,不是負數。
        // A negative minimum passes every grade while reading as though grade checking were
        // enabled. Disabling the check is spelled null, not a negative number.
        await Assert.ThrowsAsync<InvalidRecipeException>(() => manager.LoadAsync());
    }

    [Fact]
    public async Task SaveAsync_WithBlankModelName_ThrowsAndLeavesDiskUntouched()
    {
        var manager = NewManager();
        await manager.SaveAsync(new RecipeModel { ModelName = "GOOD", BarcodeLength = 10 });

        await Assert.ThrowsAsync<InvalidRecipeException>(
            () => manager.SaveAsync(new RecipeModel { ModelName = "   ", BarcodeLength = 10 }));

        // 驗證失敗不得寫壞已在線上使用的配方
        // A rejected save must not corrupt the recipe already in production.
        var onDisk = await NewManager().LoadAsync();
        Assert.Equal("GOOD", onDisk.ModelName);
    }

    [Fact]
    public async Task SaveAsync_LeavesNoTempFileBehind()
    {
        var manager = NewManager();
        await manager.SaveAsync(new RecipeModel { ModelName = "MODEL-E", BarcodeLength = 16 });

        // 暫存檔殘留代表原子換名沒有完成 / A leftover temp file means the atomic rename did not complete.
        Assert.False(File.Exists(manager.FilePath + ".tmp"));
        Assert.Single(Directory.GetFiles(_workspace.Root));
    }

    [Fact]
    public async Task Current_ReturnsCopy_SoCallersCannotMutateTheLiveRecipe()
    {
        var manager = NewManager();
        await manager.SaveAsync(new RecipeModel { ModelName = "MODEL-F", BarcodeLength = 14 });

        var borrowed = manager.Current;
        borrowed.BarcodeLength = 999;

        Assert.Equal(14, manager.Current.BarcodeLength);
    }

    [Fact]
    public async Task SaveAsync_CreatesMissingDirectories()
    {
        var manager = new RecipeManager(Path.Combine(_workspace.Root, "nested", "deeper", "recipe.json"));

        await manager.SaveAsync(new RecipeModel { ModelName = "MODEL-G", BarcodeLength = 11 });

        Assert.True(File.Exists(manager.FilePath));
    }

    [Fact]
    public void Constructor_WithBlankPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => new RecipeManager("   "));
    }

    public void Dispose() => _workspace.Dispose();
}
