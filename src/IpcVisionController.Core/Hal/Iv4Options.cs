using System.Text;

namespace IpcVisionController.Core.Hal;

/// <summary>
/// IV4 字符檢測器的連線與電文設定 / Connection and frame settings for the IV4 verifier.
///
/// 與讀碼器完全獨立 / Entirely independent of the reader:
/// 各自的 IP、埠、觸發命令、結束字元、欄位索引。兩台是不同型號、不同設定軟體、
/// 不同時間導入的裝置,共用任何一項設定都只會在其中一台改設定時把另一台弄壞。
/// Its own address, port, command, terminator and indexes. The two are different models with
/// different setup software, commissioned at different times; sharing any single setting would
/// only break one device when the other is re-configured.
/// </summary>
public sealed class Iv4Options : NonProtocolLinkOptions
{
    /// <inheritdoc />
    protected override string Model => "Keyence IV4";

    /// <summary>字符檢測各區域文字所在的欄位索引 / Zero-based field indexes holding each OCR region's text.</summary>
    public IReadOnlyList<int> CharacterTextFields { get; set; } = [];

    /// <inheritdoc />
    protected override void ValidateFields()
    {
        EnsureNonNegative(CharacterTextFields, nameof(CharacterTextFields));

        // 字符檢測器接上了卻沒有指定任何區域欄位,每次觸發都回空結果 ——
        // 配方若要求區域數就每張判退,若不要求就等於這台裝置整台白接。
        // 兩種結果都不是意圖,所以在啟動時擋下。真的還沒有 IV4,做法是不要在 device.json
        // 裡放 CharacterVerifier 這一段,而不是放一段空的。
        // A verifier that is attached but has no region field mapped returns nothing on every
        // trigger: every label rejects if the recipe asks for regions, and the device is wired up
        // for no purpose if it does not. Neither is an intent, so it is refused at startup. The
        // way to say "no IV4 yet" is to leave the CharacterVerifier section out of device.json
        // altogether, not to include an empty one.
        if (CharacterTextFields.Count == 0)
        {
            throw new ArgumentException(
                "CharacterTextFields 不可為空。若尚未接 IV4,請整段移除 CharacterVerifier / "
                + "must not be empty; omit the whole CharacterVerifier section when there is no IV4 yet.",
                nameof(CharacterTextFields));
        }
    }

    /// <inheritdoc />
    protected override void DescribeFields(StringBuilder builder)
        => builder.Append(", 字符欄位 / character fields [").AppendJoin(' ', CharacterTextFields).Append(']');
}
