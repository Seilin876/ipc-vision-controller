using IpcVisionController.Core.Models;

namespace IpcVisionController.Core.Hal;

/// <summary>
/// 實機 Keyence IV4 字符檢測器 / Real Keyence IV4 character verifier, over non-protocol TCP.
///
/// 與讀碼器各自一條連線、各自一次觸發 / Its own link and its own trigger:
/// 兩台裝置在物理上是分開的,所以電氣上也分開。共用連線在這裡不是最佳化,而是錯的 ——
/// 兩台裝置有各自的 IP。
/// The two devices are physically separate, so they are electrically separate too. Sharing a link
/// here would not be an optimisation but an error: each device has its own address.
/// </summary>
public sealed class Iv4CharacterVerifier(Iv4Options options) : NonProtocolSensor(options), ICharacterVerifier
{
    private readonly Iv4Options _options = options;

    /// <summary>
    /// 觸發一次字符檢測 / Trigger one character inspection.
    /// </summary>
    public async Task<IReadOnlyList<CharacterResult>> TriggerAsync(CancellationToken cancellationToken)
    {
        var fields = await TriggerAndSplitAsync(cancellationToken).ConfigureAwait(false);
        return CharacterFrameReader.Read(fields, _options);
    }
}
