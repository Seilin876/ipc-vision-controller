using IpcVisionController.Core.Models;
using IpcVisionController.Core.Recipes;

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
    public async Task SaveAsync_ThenLoadAsync_RoundTripsBothFields()
    {
        var manager = NewManager();
        await manager.SaveAsync(new RecipeModel { ModelName = "MODEL-B", BarcodeLength = 20 });

        // 另開一個實例,確保讀的是磁碟而不是記憶體快取
        // A second instance proves the value came off disk, not from an in-memory cache.
        var reloaded = await NewManager().LoadAsync();

        Assert.Equal("MODEL-B", reloaded.ModelName);
        Assert.Equal(20, reloaded.BarcodeLength);
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
    public async Task LoadAsync_WithNonPositiveBarcodeLength_ThrowsInvalidRecipe()
    {
        var manager = NewManager();
        await File.WriteAllTextAsync(manager.FilePath, """
            { "ModelName": "MODEL-D", "BarcodeLength": 0 }
            """);

        // 長度 0 會讓所有工件都判退,寧可在載入時就爆
        // A length of 0 would reject every part; fail at load time instead.
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
