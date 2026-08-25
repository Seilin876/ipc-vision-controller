using System.Net.Sockets;

namespace IpcVisionController.Core.Hal;

/// <summary>
/// 把 socket 錯誤碼翻成現場能行動的下一步 / Turns a socket error code into the line's next move.
///
/// 為什麼值得獨立成一個型別 / Why this is worth its own type:
/// 「連不上」在現場最常見的處理是拆機台、換線材、重調焦距 —— 而這三件事對本檔涵蓋的每一種
/// 錯誤碼都沒有幫助。錯誤碼其實已經指出了原因,只是它用的是 socket 的語彙而不是產線的語彙。
/// 翻譯放在這裡而不是塞在 catch 區塊裡,是因為它是可以獨立窮舉測試的判斷 ——
/// 而擺在 catch 裡就只能靠「讓作業系統真的拒絕一次連線」來驗,那件事各平台行為並不一致。
/// The line's usual response to "cannot connect" is to strip the machine, swap the cable and
/// re-focus the sensor — none of which helps with any error code covered here. The code already
/// names the cause; it just says it in the vocabulary of sockets rather than of a production line.
/// The translation lives here rather than inside a catch block because it is a decision that can be
/// exhausted in tests on its own, whereas inside a catch it could only be exercised by getting the
/// operating system to genuinely refuse a connection — and platforms do not agree on that.
/// </summary>
public static class ConnectFailureHint
{
    /// <summary>
    /// 這個錯誤碼在現場代表什麼 / What this error code means on the line.
    /// </summary>
    /// <returns>可附加在故障訊息後的說明；沒有特別可說的就回空字串 /
    /// Text to append to the fault message, or an empty string when there is nothing specific to add.</returns>
    public static string For(SocketError error) => error switch
    {
        // 位址是通的,對方主動回了 RST —— 該埠沒有人在聽。
        // 三個最常見的原因都不在本程式裡:埠號填錯、裝置端的無協定通訊沒啟用、
        // 或裝置只接受一條連線而設定軟體正佔著。
        // The address answered and actively sent a RST: nothing is listening on that port. The three
        // usual causes are all outside this program — the wrong port, non-protocol communication left
        // disabled on the device, or a single-connection device with its setup software attached.
        SocketError.ConnectionRefused =>
            " 位址可達但該埠沒有人在聽：確認埠號、確認裝置端已啟用無協定通訊並設為 TCP 伺服器、"
            + "並關閉正佔著連線的設定軟體 / the address answered but nothing is listening on that port: "
            + "check the port, check that non-protocol communication is enabled and the device is a TCP "
            + "server, and close any setup software holding the link.",

        // 主機不可達／網路不可達：這是路由或網段的問題,不是裝置的問題。
        // 現場最該先做的是確認子網路遮罩,而不是動裝置設定。
        // Host or network unreachable: a routing or subnet problem rather than a device problem. The
        // first thing to check is the subnet mask, not the device's settings.
        SocketError.HostUnreachable or SocketError.NetworkUnreachable =>
            " 路由不可達：確認 IPC 與裝置在同一網段、子網路遮罩正確 / unreachable: check that the IPC "
            + "and the device share a subnet and that the mask is right.",

        // 逾時代表 SYN 送出去沒有任何回應 —— 位址錯、裝置沒開機,或中間有東西默默丟包。
        // 與「拒絕」是完全不同的線索,不可混為一談。
        // A timeout means the SYN drew no response at all: wrong address, unpowered device, or
        // something dropping packets in between. A quite different clue from a refusal.
        SocketError.TimedOut =>
            " 沒有任何回應：確認位址、確認裝置已開機、確認防火牆沒有丟包 / no response at all: check the "
            + "address, that the device is powered, and that a firewall is not dropping packets.",

        _ => string.Empty,
    };
}
