using System.Net.Sockets;
using IpcVisionController.Core.Hal;
using Xunit;

namespace IpcVisionController.Core.Tests;

/// <summary>
/// 連線失敗的錯誤碼翻譯 / Translating a failed connect into the line's next move.
///
/// 為什麼這一組測試不開任何 socket / Why none of these open a socket:
/// 要讓作業系統「真的」回報某個錯誤碼並不可靠 —— 連到已關閉的 loopback 埠,macOS 立刻回 RST,
/// Windows 對剛釋放的埠是丟棄 SYN 而走到逾時。靠 socket 驗這段翻譯,測到的是平台行為;
/// 直接餵錯誤碼,測到的才是本程式的判斷,而且每一碼都驗得到。
/// Getting an operating system to genuinely report a chosen error code is not reliable: connecting to
/// a closed loopback port draws an immediate RST on macOS while Windows drops the SYN on a
/// just-released port and times out. Verifying this translation through a socket would test the
/// platform; feeding the codes directly tests this program's own decision, and reaches every code.
///
/// 為什麼這段翻譯值得測 / Why the translation is worth testing:
/// 「連不上」在現場最常見的處理是拆機台、換線材、重調焦距,而這裡每一種錯誤碼都不是那些原因。
/// 這幾行字是唯一會把現場導向正確方向的東西。
/// The line's usual response to "cannot connect" is to strip the machine, swap the cable and re-focus
/// the sensor, and none of these codes has any of those as its cause. These few sentences are the only
/// thing pointing the diagnosis the right way.
/// </summary>
public sealed class ConnectFailureHintTests
{
    [Fact]
    public void ConnectionRefused_PointsAtThePortNotTheHardware()
    {
        // 拒絕代表位址是通的、對方主動回了 RST —— 該埠沒有人在聽。
        // 這正是實機 SR-X300 在無協定通訊未啟用時給出的錯誤。
        // Refused means the address answered with a RST: nothing is listening on that port. This is
        // exactly what the real SR-X300 returns while non-protocol communication is still disabled.
        var hint = ConnectFailureHint.For(SocketError.ConnectionRefused);

        Assert.Contains("沒有人在聽", hint, StringComparison.Ordinal);
        Assert.Contains("埠號", hint, StringComparison.Ordinal);
        Assert.Contains("TCP 伺服器", hint, StringComparison.Ordinal);
        Assert.Contains("設定軟體", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void TimedOut_PointsAtTheAddressAndPowerNotThePort()
    {
        // 逾時代表 SYN 完全沒有回應,與「拒絕」是相反的線索:
        // 拒絕時埠號是嫌疑,逾時時位址與供電才是。混為一談會讓現場從錯的一端開始查。
        // A timeout means the SYN drew no response at all, which is the opposite clue to a refusal:
        // refused puts the port under suspicion, timed out puts the address and the power supply
        // there. Conflating them starts the diagnosis at the wrong end.
        var hint = ConnectFailureHint.For(SocketError.TimedOut);

        Assert.Contains("沒有任何回應", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("沒有人在聽", hint, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SocketError.HostUnreachable)]
    [InlineData(SocketError.NetworkUnreachable)]
    public void Unreachable_PointsAtTheSubnet(SocketError error)
    {
        // 不可達是路由問題,不是裝置問題 —— 該看的是子網路遮罩,不是裝置設定
        // Unreachable is a routing problem rather than a device problem: the thing to look at is the
        // subnet mask, not the device's settings.
        var hint = ConnectFailureHint.For(error);

        Assert.Contains("網段", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnrecognisedCode_AddsNothing()
    {
        // 沒有把握的錯誤碼就不要編一句建議。錯的指引比沒有指引更貴 ——
        // 它會讓現場去查一個本來就沒問題的地方,而原始的 socket 訊息仍然在故障訊息裡。
        // A code we have nothing certain to say about gets no invented advice. Wrong guidance costs
        // more than none: it sends the line to inspect something that was never wrong, and the raw
        // socket message is still there in the fault text.
        Assert.Empty(ConnectFailureHint.For(SocketError.AddressFamilyNotSupported));
    }
}
