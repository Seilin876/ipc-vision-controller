using IpcVisionController.Core.Models;

namespace IpcVisionController.Core.Hal;

/// <summary>
/// 尚未導入字符檢測器 / No character verifier installed yet.
///
/// 為什麼需要一個「不存在」的裝置 / Why an "absent" device is needed:
/// <see cref="Machine.InspectionSequencer"/> 一定要有一個 <see cref="ICharacterVerifier"/>。
/// 讓它可以是 null,等於在檢測週期裡撒一堆 null 檢查,而漏掉任何一個的後果是
/// 「字符檢測被跳過但紀錄看起來完整」。用一個明確表示「沒有這台裝置」的實作,
/// 型別上就不存在忘記檢查的可能。
/// The sequencer must have an <see cref="ICharacterVerifier"/>. Allowing null would scatter null
/// checks through the inspection cycle, and missing any one of them means "character verification
/// was skipped but the record looks complete". An implementation that explicitly means "there is
/// no such device" removes the possibility of forgetting.
///
/// 為什麼回空清單而不是一律合格 / Why an empty list rather than an automatic pass:
/// 空清單會讓配方的區域數檢查決定後果:配方要求 0 個區域就合格（本機種本來就不做字符檢測）,
/// 要求 1 個以上就以「區域數不足」判退（你要求了一項機器上沒有的檢查）。
/// 兩種都是對的答案,而且都是由配方說出來的 —— 這台假裝置自己不做任何判斷。
/// An empty list lets the recipe's region count decide: asking for zero regions passes, because
/// this product does not check characters anyway, and asking for one or more rejects on the count,
/// because a check was demanded that the machine does not have. Both answers are right and both
/// come from the recipe; this stand-in judges nothing itself.
/// </summary>
public sealed class AbsentCharacterVerifier : ICharacterVerifier
{
    /// <summary>
    /// 名稱要讓人一看就知道沒有這台裝置 / The name has to make the absence obvious.
    /// 它會出現在初始化訊息裡,而「連線中… 未導入字符檢測」正是現場需要讀到的一行。
    /// It appears in the initialise log, and "connecting… character verification not installed" is
    /// exactly the line the line needs to read.
    /// </summary>
    public string Name => "未導入字符檢測 / character verification not installed";

    /// <summary>
    /// 一律回報已連線 / Always reports connected.
    /// 沒有連線可言,而回報未連線會讓協調器把「這台機器沒有這項檢查」當成故障。
    /// There is no link to report on, and reporting false would make the sequencer treat "this
    /// machine has no such check" as a fault.
    /// </summary>
    public bool IsConnected => true;

    public Task ConnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>空清單：沒有任何區域回報 / An empty list: no region reported.</summary>
    public Task<IReadOnlyList<CharacterResult>> TriggerAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<CharacterResult>>([]);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
