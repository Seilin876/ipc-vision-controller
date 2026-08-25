using IpcVisionController.Core.Hal;
using Xunit;

namespace IpcVisionController.Core.Tests;

/// <summary>
/// 無協定 TCP 驅動的行為規格 / Behavioural spec for the non-protocol TCP drivers.
///
/// 為什麼用 loopback 上的假伺服器而不是模擬物件 /
/// Why a fake server on loopback rather than a mock object:
/// 這層真正的風險全在 socket 那一側 —— TCP 組框、逾時、斷線、同一台裝置被連線兩次。
/// 把 TcpClient 換成介面再模擬掉,測到的只是我自己寫的假設;走真正的 socket,
/// 「一筆電文分兩個封包抵達」這種只在網路忙碌時才出現的錯誤才驗得到。
/// Every real risk at this layer sits on the socket side: TCP framing, timeouts, dropped links, one
/// device connected twice. Swapping TcpClient for an interface and mocking it would only test my
/// own assumptions; a real socket is what lets "one frame arriving as two packets" — a fault that
/// appears only when the network is busy — be tested at all.
///
/// 為什麼兩台裝置都測 / Why both devices are exercised:
/// 連線層是共用的,所以組框只需要驗一次;但「兩台裝置各自一條連線、各自一次觸發」
/// 是這次架構改動的重點,那必須直接驗,不能靠共用實作推論。
/// The link layer is shared, so the framing needs proving once. But "two devices, one link and one
/// trigger each" is the point of this architecture, and that has to be demonstrated rather than
/// inferred from a shared implementation.
/// </summary>
public sealed class NonProtocolSensorTests
{
    private static SrX300Options ReaderOptions(int port) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        TriggerCommand = "LON",
        Terminator = FrameTerminator.Cr,
        ConnectTimeoutMs = 2_000,
        ResponseTimeoutMs = 500,
        CodeDataFields = [1, 3],
        CodeGradeFields = [2, 4],
    };

    private static Iv4Options VerifierOptions(int port) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        TriggerCommand = "T1",
        Terminator = FrameTerminator.Cr,
        ConnectTimeoutMs = 2_000,
        ResponseTimeoutMs = 500,
        CharacterTextFields = [1, 2],
    };

    // ── 讀碼器 / The reader ─────────────────────────────────────────────────

    [Fact]
    public async Task Reader_SendsTheConfiguredCommandAndMapsTheReply()
    {
        await using var server = new FakeNonProtocolServer(_ => ["OK,ABC123456789,88,DEF123456789,91"]);
        await using var reader = new SrX300CodeReader(ReaderOptions(server.Port));

        await reader.ConnectAsync(CancellationToken.None);
        var codes = await reader.TriggerAsync(CancellationToken.None);

        Assert.True(reader.IsConnected);
        Assert.Equal(["LON"], server.Commands());
        Assert.Equal(["ABC123456789", "DEF123456789"], codes.Select(c => c.Data));
        Assert.Equal([88, 91], codes.Select(c => c.Grade));
    }

    [Fact]
    public async Task Reader_WhenTheFrameArrivesInPieces_StillReadsItWhole()
    {
        // TCP 是位元流,一筆電文可能分成數個封包抵達。假設「讀一次就是一筆」的驅動
        // 會在這裡拿到截斷的電文,而那只在網路忙碌時發生,現場最難重現。
        // TCP is a byte stream and one frame may arrive across several packets. A driver that assumes
        // one read equals one frame gets a truncated message here — a failure that only shows up when
        // the network is busy and is the hardest to reproduce on the line.
        await using var server = new FakeNonProtocolServer(_ => ["OK,ABC1234", "56789,88,DEF123456789,91"]);
        await using var reader = new SrX300CodeReader(ReaderOptions(server.Port));

        await reader.ConnectAsync(CancellationToken.None);
        var codes = await reader.TriggerAsync(CancellationToken.None);

        Assert.Equal("ABC123456789", codes[0].Data);
    }

    [Fact]
    public async Task Reader_EachTriggerAsksTheDeviceAgain()
    {
        // 每個週期都必須重新觸發。回傳快取的上一筆結果,等於把前一張標籤的判定
        // 記到這一張上 —— 那是追溯資料裡最嚴重的一種錯。
        // Every cycle has to trigger afresh. Returning a cached previous result would log the last
        // label's verdict against this one, the worst kind of error a trace can hold.
        await using var server = new FakeNonProtocolServer(n => [$"OK,CODE{n},90,,"]);
        await using var reader = new SrX300CodeReader(ReaderOptions(server.Port));

        await reader.ConnectAsync(CancellationToken.None);
        var first = await reader.TriggerAsync(CancellationToken.None);
        var second = await reader.TriggerAsync(CancellationToken.None);

        Assert.Equal("CODE0", first[0].Data);
        Assert.Equal("CODE1", second[0].Data);
        Assert.Equal(["LON", "LON"], server.Commands());
    }

    // ── 字符檢測器 / The verifier ────────────────────────────────────────────

    [Fact]
    public async Task Verifier_SendsItsOwnCommandAndMapsItsOwnFields()
    {
        // 命令與欄位索引都與讀碼器不同,而且必須互不影響
        // Its command and its indexes both differ from the reader's, and neither may leak into the
        // other.
        await using var server = new FakeNonProtocolServer(_ => ["OK,LOT26A,2026-08-25"]);
        await using var verifier = new Iv4CharacterVerifier(VerifierOptions(server.Port));

        await verifier.ConnectAsync(CancellationToken.None);
        var regions = await verifier.TriggerAsync(CancellationToken.None);

        Assert.Equal(["T1"], server.Commands());
        Assert.Equal(["LOT26A", "2026-08-25"], regions.Select(r => r.Text));
    }

    // ── 兩台一起 / Both at once ──────────────────────────────────────────────

    [Fact]
    public async Task BothDevices_KeepSeparateLinksAndTriggerIndependently()
    {
        // 這是本次架構改動要達成的事:兩台裝置、兩個位址、兩條連線、兩次觸發。
        // 先前的驅動把兩個角色綁在同一個物件與同一條連線上,第二台裝置無處可接。
        // This is what the architecture change is for: two devices, two addresses, two links, two
        // triggers. The previous driver bound both roles to one object on one link, leaving the
        // second device nowhere to attach.
        await using var readerServer = new FakeNonProtocolServer(_ => ["OK,ABC123456789,88,,"]);
        await using var verifierServer = new FakeNonProtocolServer(_ => ["OK,LOT26A,2026-08-25"]);

        await using var reader = new SrX300CodeReader(ReaderOptions(readerServer.Port));
        await using var verifier = new Iv4CharacterVerifier(VerifierOptions(verifierServer.Port));

        await reader.ConnectAsync(CancellationToken.None);
        await verifier.ConnectAsync(CancellationToken.None);

        var codes = await reader.TriggerAsync(CancellationToken.None);
        var regions = await verifier.TriggerAsync(CancellationToken.None);

        Assert.NotEqual(readerServer.Port, verifierServer.Port);
        Assert.Equal(["LON"], readerServer.Commands());
        Assert.Equal(["T1"], verifierServer.Commands());
        Assert.Equal("ABC123456789", Assert.Single(codes).Data);
        Assert.Equal(["LOT26A", "2026-08-25"], regions.Select(r => r.Text));
    }

    // ── 連線與生命週期 / Link and lifetime ───────────────────────────────────

    [Fact]
    public async Task ConnectAsync_IsIdempotent()
    {
        // 重複連線會白丟掉現有連線,並在現場留下一條半開的 socket
        // Reconnecting would discard the live link and leave a half-open socket on the line.
        await using var server = new FakeNonProtocolServer(_ => ["OK,ABC,90,,"]);
        await using var reader = new SrX300CodeReader(ReaderOptions(server.Port));

        await reader.ConnectAsync(CancellationToken.None);
        await reader.ConnectAsync(CancellationToken.None);

        Assert.Equal(1, server.AcceptedConnections);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        // 組裝根在關機路徑上可能對同一個物件處置多次
        // The composition root may dispose the same object more than once on shutdown.
        await using var server = new FakeNonProtocolServer(_ => ["OK,ABC,90,,"]);
        var reader = new SrX300CodeReader(ReaderOptions(server.Port));

        await reader.ConnectAsync(CancellationToken.None);
        await reader.DisposeAsync();
        await reader.DisposeAsync();
    }

    [Fact]
    public async Task TriggerAsync_BeforeConnect_IsRejected()
    {
        await using var server = new FakeNonProtocolServer(_ => ["OK,ABC,90,,"]);
        await using var reader = new SrX300CodeReader(ReaderOptions(server.Port));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.TriggerAsync(CancellationToken.None));
    }

    // ── 設備異常 / Equipment faults ──────────────────────────────────────────

    [Fact]
    public async Task TriggerAsync_WhenTheDeviceStaysSilent_RaisesADeviceFaultNotANoRead()
    {
        // 裝置沒有在逾時內回應是設備問題,不是沒讀到。記成一批不良品等於把斷線出貨。
        // 訊息要指向命令與結束字元,因為埠既然連上了,那兩者才是剩下的嫌疑。
        // A silent device is an equipment problem, not a no-read; logging it as bad parts ships a
        // dropped link. The message points at the command and the terminator, because with the port
        // open those are what is left to suspect.
        await using var server = new FakeNonProtocolServer(_ => null);
        await using var reader = new SrX300CodeReader(ReaderOptions(server.Port));

        await reader.ConnectAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<DeviceFaultException>(
            () => reader.TriggerAsync(CancellationToken.None));

        Assert.Contains("未回應", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TriggerAsync_WhenTheLinkDropsMidResponse_RaisesADeviceFault()
    {
        await using var server = new FakeNonProtocolServer(_ => ["OK,ABC"], closeAfterResponse: true);
        await using var reader = new SrX300CodeReader(ReaderOptions(server.Port));

        await reader.ConnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<DeviceFaultException>(
            () => reader.TriggerAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConnectAsync_WhenNothingIsListening_RaisesADeviceFaultNamingTheDevice()
    {
        // 這裡刻意只斷言「拋出設備故障,而且訊息指名是哪一台」。
        // 作業系統對「連到已關閉的 loopback 埠」並不一致:macOS 立刻回 RST(拒絕連線),
        // Windows 對剛釋放的埠是丟棄 SYN,於是走到逾時。兩者都是正確的失敗,
        // 而把其中一種寫進斷言,測到的是平台行為而不是本程式的行為。
        // 錯誤碼到現場語彙的翻譯由 ConnectFailureHintTests 逐碼驗證,不必經過 socket。
        // This deliberately asserts only that a device fault is raised and that it names the device.
        // Operating systems disagree about connecting to a closed loopback port: macOS sends an
        // immediate RST (refused) while Windows drops the SYN on a just-released port and the attempt
        // times out. Both are correct failures, and pinning either one into an assertion tests the
        // platform rather than this program. The mapping from error code to the line's vocabulary is
        // verified code by code in ConnectFailureHintTests, without a socket.
        int closedPort;
        await using (var server = new FakeNonProtocolServer(_ => ["OK"]))
        {
            closedPort = server.Port;
        }

        // 連線逾時壓到 300 ms：在會走到逾時的平台上,預設的 2 秒只是讓整份測試變慢
        // The connect timeout is cut to 300 ms: on platforms that take the timeout path, the default
        // two seconds only makes the suite slower.
        var options = ReaderOptions(closedPort);
        options.ConnectTimeoutMs = 300;

        await using var reader = new SrX300CodeReader(options);

        var error = await Assert.ThrowsAsync<DeviceFaultException>(
            () => reader.ConnectAsync(CancellationToken.None));

        Assert.Contains("SR-X300", error.Message, StringComparison.Ordinal);
        Assert.Contains(closedPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            error.Message, StringComparison.Ordinal);
        Assert.False(reader.IsConnected);
    }

    // ── 導入用的原始電文 / Raw frames for commissioning ──────────────────────

    [Fact]
    public async Task RawFrameReceived_CarriesTheFrameAndTheDeviceThatSentIt()
    {
        // 兩台裝置同時導入時,不標明來源的電文記錄毫無用處 ——
        // 兩者的電文長得很像,而欄位索引是各自獨立設定的。
        // With two devices being commissioned at once an unattributed frame log is useless: the
        // frames look alike while their indexes are configured independently.
        await using var server = new FakeNonProtocolServer(_ => ["OK,ABC123456789,88,,"]);
        await using var reader = new SrX300CodeReader(ReaderOptions(server.Port));

        RawFrameEventArgs? seen = null;
        reader.RawFrameReceived += (_, e) => seen = e;

        await reader.ConnectAsync(CancellationToken.None);
        await reader.TriggerAsync(CancellationToken.None);

        Assert.NotNull(seen);
        Assert.Equal("OK,ABC123456789,88,,", seen.Frame);
        Assert.Contains("SR-X300", seen.DeviceName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawFrameReceived_FiresBeforeParsing()
    {
        // 解析失敗時,現場最需要看到的正是那筆解不開的電文
        // When parsing fails, the frame that broke it is precisely what the line needs to see.
        await using var server = new FakeNonProtocolServer(_ => ["ER,03"]);
        await using var reader = new SrX300CodeReader(ReaderOptions(server.Port));

        RawFrameEventArgs? seen = null;
        reader.RawFrameReceived += (_, e) => seen = e;

        await reader.ConnectAsync(CancellationToken.None);
        await Assert.ThrowsAsync<DeviceFaultException>(() => reader.TriggerAsync(CancellationToken.None));

        Assert.NotNull(seen);
        Assert.Equal("ER,03", seen.Frame);
    }

    [Fact]
    public void Configuration_ShowsTheIndexesActuallyInForce()
    {
        // 電文說裝置送了什麼,這一行說程式在讀哪幾格。少了任何一半都無法判斷索引對不對。
        // The frame says what the device sent; this line says which fields the program reads.
        // Neither half alone can tell you whether the indexes are right.
        var reader = new SrX300CodeReader(ReaderOptions(9004));

        Assert.Contains("code fields [1 3]", reader.Configuration, StringComparison.Ordinal);
        Assert.Contains("LON", reader.Configuration, StringComparison.Ordinal);
    }
}
