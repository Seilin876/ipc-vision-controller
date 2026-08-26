using IpcVisionController.Core.Machine;
using Xunit;

namespace IpcVisionController.Core.Tests;

/// <summary>
/// 站距換算成格數 / Turning the station distance into a pitch count.
///
/// 為什麼這段換算值得獨立測 / Why the conversion is worth testing on its own:
/// 它取代的是「請現場自己算幾格」那一步。人工換算算錯不會報錯,只會讓追溯紀錄把不同標籤的
/// 兩半湊在一起 —— 而那是這支程式最嚴重的錯誤。既然把那一步收進程式裡,它就得被窮舉。
/// It replaces the step where the line worked the pitch count out by hand. A manual mistake there never
/// announces itself and merely pairs halves of different labels in the traceability record, the worst error
/// this program can make. Having taken the step into the program, it has to be exhausted.
/// </summary>
public sealed class StationOffsetTests
{
    private static SequencerOptions WithDistance(int pulses) => new()
    {
        InspectionStationDistancePulses = pulses,
    };

    [Theory]
    [InlineData(0, 10_000, 0)]          // 兩台瞄同一位置 / both sensors on one position
    [InlineData(40_000, 10_000, 4)]     // 現場機構 / the line's mechanism
    [InlineData(40_000, 20_000, 2)]     // 標籤變長,同一距離變成較少格 / longer labels, fewer pitches
    [InlineData(40_000, 5_000, 8)]      // 標籤變短,同一距離變成較多格 / shorter labels, more pitches
    public void OffsetPitchesFor_DividesTheDistanceByOnePitch(int distance, int pitch, int expected)
    {
        // 同一個物理距離,格數隨標籤長度改變 —— 這正是「記距離、不記格數」的理由:
        // 換機種只改一格的脈波數,偏移自動跟著對。
        // One physical distance, a pitch count that changes with label length — precisely why the distance is
        // stored and the count is not: a changeover edits the pulses per feed and the offset follows.
        Assert.Equal(expected, WithDistance(distance).OffsetPitchesFor(pitch));
    }

    [Theory]
    [InlineData(45_000, 10_000, 5)]     // .5 往遠離零的方向 / away from zero
    [InlineData(44_000, 10_000, 4)]
    [InlineData(46_000, 10_000, 5)]
    public void OffsetPitchesFor_RoundsToTheNearestWholePitch(int distance, int pitch, int expected)
    {
        // 除不盡代表感測器沒落在標籤邊界上,那幾乎一定是有個數字填錯了。
        // 仍取最接近的一格讓機台跑得起來,再由初始化訊息把不整齊說出來 ——
        // 直接拒絕啟動會讓一台只是量得不夠精確的機器停擺。
        // A remainder means the sensor does not sit on a label boundary, almost certainly a mistyped number.
        // The nearest whole pitch is still used so the machine runs, with the initialise log saying it did not
        // divide evenly; refusing outright would stop a machine that was merely measured imprecisely.
        Assert.Equal(expected, WithDistance(distance).OffsetPitchesFor(pitch));
    }

    [Fact]
    public void OffsetPitchesFor_WithANonPositivePitch_IsRejected()
    {
        // 除以 0 沒有意義,而配方驗證本來就擋住了 0 —— 這裡守的是「萬一繞過了」。
        // Dividing by zero is meaningless and the recipe already refuses it; this guards the case where it
        // somehow got through anyway.
        Assert.Throws<ArgumentOutOfRangeException>(() => WithDistance(40_000).OffsetPitchesFor(0));
    }

    [Theory]
    [InlineData(40_000, 10_000, true)]
    [InlineData(45_000, 10_000, false)]
    [InlineData(0, 10_000, true)]
    public void DistanceDividesEvenlyBy_SaysWhetherTheSensorSitsOnABoundary(
        int distance, int pitch, bool expected)
    {
        // 除不盡時感測器停在兩張標籤之間,那個位置每一格都不一樣、不可重現,
        // 判定會時好時壞而現場只看到「偶發判退」。所以這件事必須能被說出來。
        // A remainder leaves the sensor between two labels, a position that differs every pitch and does not
        // repeat, so verdicts come and go and the line sees only intermittent rejects. It has to be sayable.
        Assert.Equal(expected, WithDistance(distance).DistanceDividesEvenlyBy(pitch));
    }

    [Fact]
    public void Validate_WithANegativeDistance_IsRejected()
    {
        var options = new SequencerOptions { InspectionStationDistancePulses = -1 };

        Assert.Throws<ArgumentException>(options.Validate);
    }
}
