using System.Drawing.Text;

namespace IpcVisionController.App;

/// <summary>
/// 畫面字型 / The fonts the screen uses.
///
/// 為什麼需要這個型別 / Why this exists:
/// 先前訊息區、檢測紀錄與進給標籤都指定 Consolas —— 選它是為了條碼對齊,卻沒考慮那些控制項
/// 同時要顯示中文。Consolas 沒有中日韓字符,於是現場看到的是一格一格的豆腐框:
/// 「已進給 Fed: 10,000」變成「▯▯▯ Fed: 10,000」,判退原因整句都是方框。
/// 字串本身沒有問題（複製出來仍是正常的中文),純粹是字型缺字。
/// The log pane, the record list and the fed-count label all specified Consolas, chosen for lining codes
/// up without regard for the Chinese those same controls have to show. Consolas carries no CJK glyphs, so
/// the line saw rows of tofu boxes: the fed label rendered as boxes and a reject reason was boxes end to
/// end. The strings were never wrong — copying them out yields correct text — the font simply lacks the
/// characters.
///
/// 為什麼不直接寫死一個字型名稱 / Why not just name one font:
/// 指定不存在的字型時 WinForms 會靜默退回預設字型,而預設字型（Microsoft Sans Serif)同樣缺字 ——
/// 那會把「缺字」換成另一種「缺字」,而且看不出是退回造成的。
/// 因此逐一檢查系統實際安裝了哪一個,全部沒有才交給系統預設。
/// WinForms silently falls back to a default when a named font is absent, and that default — Microsoft
/// Sans Serif — lacks the same characters, trading one case of tofu for another with no sign that a
/// fallback happened. So the installed families are checked in order, and only if none is present does the
/// system default get used.
///
/// 為什麼放棄等寬 / Why monospacing is given up:
/// Windows 上有繁體字符又等寬的字型只有細明體系列,而它在小字級是點陣字,清晰度反而更差。
/// 條碼在檢測紀錄裡是分欄顯示的,對齊由 ListView 的欄位負責,不靠字型 ——
/// 所以等寬在這裡是可以放棄的,缺字不行。
/// The only monospaced families on Windows that also carry Traditional Chinese are the MingLiU series,
/// which render as bitmaps at small sizes and read worse. Codes appear in ListView columns where alignment
/// comes from the columns rather than the font, so monospacing is the affordable loss here and missing
/// glyphs is not.
/// </summary>
internal static class UiFont
{
    /// <summary>
    /// 偏好順序 / Preference order, best first.
    /// 微軟正黑體 UI 自 Windows 8 起內建,微軟正黑體自 Windows 7,新細明體更早 ——
    /// 三者都含繁體字符。排序是「字面清晰度優先」,而不是「覆蓋率優先」,
    /// 因為三者的覆蓋率對本程式的用字都足夠。
    /// Microsoft JhengHei UI ships from Windows 8, Microsoft JhengHei from Windows 7 and PMingLiU earlier
    /// still; all three carry Traditional Chinese. The order favours legibility rather than coverage,
    /// because all three cover everything this program prints.
    /// </summary>
    private static readonly string[] Preferred =
    [
        "Microsoft JhengHei UI",
        "Microsoft JhengHei",
        "PMingLiU",
        "MingLiU",
    ];

    private static readonly string Family = Resolve();

    /// <summary>指定字級的字型 / A font at the given size.</summary>
    public static Font Of(float size, FontStyle style = FontStyle.Regular) => new(Family, size, style);

    /// <summary>實際選用的字型名稱,供開機訊息記錄 / The family actually chosen, for the startup log.</summary>
    public static string FamilyName => Family;

    private static string Resolve()
    {
        try
        {
            using var installed = new InstalledFontCollection();
            var names = installed.Families
                .Select(family => family.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in Preferred)
            {
                if (names.Contains(candidate))
                {
                    return candidate;
                }
            }
        }
        catch (Exception)
        {
            // 查詢字型清單失敗不該讓程式開不起來 —— 字型是外觀,產線工具不該為了外觀停擺。
            // 退回系統預設後最壞的情況是回到缺字,那與修正前一樣,不會更糟。
            // A failure to enumerate fonts must not stop the program: fonts are cosmetic and a line tool must
            // not refuse to open over cosmetics. Falling back to the system default risks tofu again, which
            // is no worse than before this fix.
        }

        return SystemFonts.DefaultFont.FontFamily.Name;
    }
}
