using System.Text.Json;
using System.Text.Json.Serialization;
using IpcVisionController.Core.Models;

namespace IpcVisionController.Core.Recipes;

/// <summary>
/// 配方的 JSON 讀寫管理 / JSON persistence for <see cref="RecipeModel"/>.
/// 寫入採「暫存檔 + 原子換名」，避免 IPC 突然斷電時留下半截檔案。
/// Saves via temp-file + atomic rename, so a sudden IPC power loss cannot
/// leave a half-written recipe on disk.
/// </summary>
public sealed class RecipeManager
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // 現場工程師會手改這個檔，欄位大小寫要寬容
        // Field engineers hand-edit this file, so be lenient about casing.
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private RecipeModel _current = new();

    public RecipeManager(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("配方路徑不可為空 / Recipe path must not be empty.", nameof(filePath));
        }

        FilePath = Path.GetFullPath(filePath);
    }

    /// <summary>配方檔完整路徑 / Absolute path of the recipe file.</summary>
    public string FilePath { get; }

    /// <summary>
    /// 目前使用中的配方（複本）/ The live recipe, as a copy.
    /// 回傳複本讓呼叫端無法在未經 <see cref="SaveAsync"/> 的情況下改動狀態。
    /// Returning a copy stops callers mutating state without going through <see cref="SaveAsync"/>.
    /// </summary>
    public RecipeModel Current => _current.Clone();

    /// <summary>
    /// 載入配方；檔案不存在時建立預設檔 / Load the recipe, writing a default file when none exists.
    /// </summary>
    public async Task<RecipeModel> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(FilePath))
            {
                // 首次啟動：落地一份預設配方，讓現場有東西可改
                // First start: materialise a default so the line has something to edit.
                var seeded = new RecipeModel();
                await WriteUnsafeAsync(seeded, cancellationToken).ConfigureAwait(false);
                _current = seeded;
                return _current.Clone();
            }

            await using var stream = new FileStream(
                FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: true);

            RecipeModel? loaded;
            try
            {
                loaded = await JsonSerializer
                    .DeserializeAsync<RecipeModel>(stream, SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new InvalidRecipeException($"配方 JSON 格式錯誤 / Malformed recipe JSON: {ex.Message}");
            }

            if (loaded is null)
            {
                throw new InvalidRecipeException("配方檔內容為 null / Recipe file deserialised to null.");
            }

            loaded.Validate();
            _current = loaded;
            return _current.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>驗證並儲存配方 / Validate then persist the recipe.</summary>
    public async Task SaveAsync(RecipeModel recipe, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        recipe.Validate();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteUnsafeAsync(recipe, cancellationToken).ConfigureAwait(false);
            _current = recipe.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>呼叫端必須已持有 <see cref="_gate"/> / Caller must already hold <see cref="_gate"/>.</summary>
    private async Task WriteUnsafeAsync(RecipeModel recipe, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = FilePath + ".tmp";

        await using (var stream = new FileStream(
            tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 4096, useAsync: true))
        {
            await JsonSerializer
                .SerializeAsync(stream, recipe, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);
            // 換名前先刷到磁碟，否則原子換名保護不到未落盤的資料
            // Flush to disk before renaming; otherwise the atomic rename protects nothing.
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, FilePath, overwrite: true);
    }
}
