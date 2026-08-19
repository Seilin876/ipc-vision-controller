using System.Net;
using System.Net.Sockets;
using System.Text;
using IpcVisionController.Core.Hal;
using Xunit;

namespace IpcVisionController.Core.Tests;

/// <summary>
/// 實機 IV4 驅動的行為規格 / Behavioural spec for the real IV4 driver.
///
/// 為什麼用 loopback 上的假伺服器而不是模擬物件 /
/// Why a fake server on loopback rather than a mock object:
/// 這支驅動真正的風險全在 socket 那一層 —— TCP 組框、逾時、斷線、同一顆感測器被連線兩次。
/// 把 TcpClient 換成介面再模擬掉,測到的只是我自己寫的假設;走真正的 socket,
/// 「一筆電文分兩個封包抵達」這種只在網路忙碌時才出現的錯誤才驗得到。
/// 代價是每個測試多幾毫秒,換來的是這條路徑在接上真感測器之前就已經被走過。
/// Every real risk in this driver sits at the socket layer: TCP framing, timeouts, dropped
/// links, one sensor connected twice. Swapping TcpClient for an interface and mocking it would
/// only test my own assumptions; a real socket is what lets "one frame arriving as two packets"
/// — a fault that appears only when the network is busy — be tested at all. The cost is a few
/// milliseconds per test; the return is that this path has been walked before a sensor is
/// ever attached.
/// </summary>
public sealed class Iv4VisionSensorTests
{
    private static Iv4Options OptionsFor(int port) => new()
    {
        Host = "127.0.0.1",
        Port = port,
        TriggerCommand = "T1",
        Terminator = Iv4Terminator.Cr,
        ConnectTimeoutMs = 2_000,
        ResponseTimeoutMs = 500,
        CodeDataFields = [1, 3],
        CodeGradeFields = [2, 4],
        CharacterTextFields = [5, 6],
    };

    [Fact]
    public async Task TriggerAsync_SendsTheConfiguredCommandAndParsesTheReply()
    {
        await using var server = new FakeIv4Server(_ => ["OK,ABC123456789,88,DEF123456789,91,LOT26A,26/08/17"]);
        await using var sensor = new Iv4VisionSensor(OptionsFor(server.Port));

        await sensor.ConnectAsync(CancellationToken.None);
        var capture = await sensor.TriggerAsync(CancellationToken.None);

        Assert.True(sensor.IsConnected);
        Assert.Equal(["T1"], server.Commands());
        Assert.Equal(["ABC123456789", "DEF123456789"], capture.Codes.Select(c => c.Data));
        Assert.Equal([88, 91], capture.Codes.Select(c => c.Grade));
        Assert.Equal(["LOT26A", "26/08/17"], capture.Characters.Select(c => c.Text));
    }

    [Fact]
    public async Task TriggerAsync_WhenTheFrameArrivesInPieces_StillReadsItWhole()
    {
        // TCP 是位元流,一筆電文可能分成數個封包抵達。假設「讀一次就是一筆」的驅動
        // 會在這裡拿到截斷的電文,而那只在網路忙碌時發生,現場最難重現。
        // TCP is a byte stream and one frame may arrive across several packets. A driver that
        // assumes one read equals one frame gets a truncated message here — a failure that only
        // shows up when the network is busy and is the hardest to reproduce on the line.
        await using var server = new FakeIv4Server(_ => ["OK,ABC1234", "56789,88,DEF123456789,91,LOT26A,26/08/17"]);
        await using var sensor = new Iv4VisionSensor(OptionsFor(server.Port));

        await sensor.ConnectAsync(CancellationToken.None);
        var capture = await sensor.TriggerAsync(CancellationToken.None);

        Assert.Equal("ABC123456789", capture.Codes[0].Data);
        Assert.Equal("26/08/17", capture.Characters[1].Text);
    }

    [Fact]
    public async Task OneTriggerServesBothRolesFromTheSameCapture()
    {
        // 一個週期只拍一張。條碼與字符來自同一張影像,追溯紀錄的兩半才必然描述同一張標籤。
        // One capture per cycle. Codes and characters come from one image, so both halves of a
        // traceability record necessarily describe the same label.
        await using var server = new FakeIv4Server(ordinal =>
            [$"OK,CODE{ordinal},88,CODE{ordinal}B,91,TEXT{ordinal},26/08/17"]);
        await using var sensor = new Iv4VisionSensor(OptionsFor(server.Port));

        await sensor.ConnectAsync(CancellationToken.None);

        var codes = await ((ICodeReader)sensor).TriggerAsync(CancellationToken.None);
        var characters = await ((ICharacterVerifier)sensor).TriggerAsync(CancellationToken.None);

        Assert.Single(server.Commands());
        Assert.Equal("CODE0", codes[0].Data);
        Assert.Equal("TEXT0", characters[0].Text);
    }

    [Fact]
    public async Task EachCycleTriggersAgainSoAStaleCaptureCannotBeServed()
    {
        await using var server = new FakeIv4Server(ordinal =>
            [$"OK,CODE{ordinal},88,CODE{ordinal}B,91,TEXT{ordinal},26/08/17"]);
        await using var sensor = new Iv4VisionSensor(OptionsFor(server.Port));

        await sensor.ConnectAsync(CancellationToken.None);

        var first = await ((ICodeReader)sensor).TriggerAsync(CancellationToken.None);
        _ = await ((ICharacterVerifier)sensor).TriggerAsync(CancellationToken.None);
        var second = await ((ICodeReader)sensor).TriggerAsync(CancellationToken.None);
        var secondCharacters = await ((ICharacterVerifier)sensor).TriggerAsync(CancellationToken.None);

        // 每個週期都必須是新的一張。取到上一張的殘值,就是把前一張標籤的結果
        // 記到這一張的追溯紀錄上。
        // Every cycle must get a fresh capture. Serving a leftover would log the previous
        // label's result against this one.
        Assert.Equal("CODE0", first[0].Data);
        Assert.Equal("CODE1", second[0].Data);
        Assert.Equal("TEXT1", secondCharacters[0].Text);
        Assert.Equal(2, server.Commands().Count);
    }

    [Fact]
    public async Task CharacterVerifyAlone_TriggersForItselfRatherThanReturningNothing()
    {
        await using var server = new FakeIv4Server(_ => ["OK,ABC123456789,88,DEF123456789,91,LOT26A,26/08/17"]);
        await using var sensor = new Iv4VisionSensor(OptionsFor(server.Port));

        await sensor.ConnectAsync(CancellationToken.None);
        var characters = await ((ICharacterVerifier)sensor).TriggerAsync(CancellationToken.None);

        Assert.Equal("LOT26A", characters[0].Text);
        Assert.Single(server.Commands());
    }

    [Fact]
    public async Task ConnectAsync_IsIdempotentBecauseOneObjectFillsTwoRoles()
    {
        // 協調器的初始化會對兩個介面各連線一次,而實機時那是同一個物件。
        // 第二次重建連線會丟掉第一條,並在現場留下一條半開的 socket。
        // The sequencer's initialise connects once per interface, and on real hardware that is
        // one object. Reconnecting on the second call would discard the first link and leave a
        // half-open socket behind.
        await using var server = new FakeIv4Server(_ => ["OK,ABC123456789,88,DEF123456789,91,LOT26A,26/08/17"]);
        await using var sensor = new Iv4VisionSensor(OptionsFor(server.Port));

        await sensor.ConnectAsync(CancellationToken.None);
        await sensor.ConnectAsync(CancellationToken.None);

        Assert.True(sensor.IsConnected);
        Assert.Equal(1, server.AcceptedConnections);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotentBecauseOneObjectIsDisposedTwice()
    {
        await using var server = new FakeIv4Server(_ => ["OK,ABC123456789,88,DEF123456789,91,LOT26A,26/08/17"]);
        var sensor = new Iv4VisionSensor(OptionsFor(server.Port));

        await sensor.ConnectAsync(CancellationToken.None);

        // 組裝根把同一個物件當成兩個介面交出去,關機時會處置兩次
        // The composition root hands the object out under two interfaces, so shutdown disposes
        // it twice.
        await sensor.DisposeAsync();
        await sensor.DisposeAsync();

        Assert.False(sensor.IsConnected);
    }

    [Fact]
    public async Task TriggerAsync_WhenTheSensorStaysSilent_RaisesADeviceFaultNotANoRead()
    {
        // 沒回應是設備問題,不是沒讀到。當成沒讀到會讓斷線的感測器
        // 靜靜地把整批貨記成不良品。
        // Silence is an equipment problem, not a no-read. Read as a no-read, a disconnected
        // sensor would quietly log a whole batch as bad parts.
        await using var server = new FakeIv4Server(_ => null);
        await using var sensor = new Iv4VisionSensor(OptionsFor(server.Port));

        await sensor.ConnectAsync(CancellationToken.None);

        var ex = await Assert.ThrowsAsync<DeviceFaultException>(
            () => sensor.TriggerAsync(CancellationToken.None));

        Assert.Contains("未回應", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TriggerAsync_WhenTheLinkDropsMidResponse_RaisesADeviceFault()
    {
        await using var server = new FakeIv4Server(_ => ["OK,ABC1234"], closeAfterResponse: true);
        await using var sensor = new Iv4VisionSensor(OptionsFor(server.Port));

        await sensor.ConnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<DeviceFaultException>(
            () => sensor.TriggerAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TriggerAsync_RecordsTheRawFrameBeforeParsingIt()
    {
        // 導入期間唯一能確認欄位索引的依據。解析失敗時,現場最需要看到的正是
        // 那筆解不開的電文,所以記錄必須發生在解析之前。
        // The only thing that confirms the field indexes during commissioning. When parsing
        // fails, the frame that broke it is exactly what the line needs, so the record has to
        // happen before the parse.
        await using var server = new FakeIv4Server(_ => ["ER,04"]);
        await using var sensor = new Iv4VisionSensor(OptionsFor(server.Port));

        var frames = new List<string>();
        sensor.RawFrameReceived += (_, e) => frames.Add(e.Frame);

        await sensor.ConnectAsync(CancellationToken.None);
        await Assert.ThrowsAsync<DeviceFaultException>(() => sensor.TriggerAsync(CancellationToken.None));

        Assert.Equal(["ER,04"], frames);
    }

    [Fact]
    public async Task TriggerAsync_BeforeConnect_IsRejected()
    {
        await using var server = new FakeIv4Server(_ => ["OK,ABC123456789,88,DEF123456789,91,LOT26A,26/08/17"]);
        await using var sensor = new Iv4VisionSensor(OptionsFor(server.Port));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sensor.TriggerAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConnectAsync_WhenNothingIsListening_RaisesADeviceFault()
    {
        // 埠號取一個確定沒人在聽的：先開再關,系統不會立刻重用同一個埠。
        // Take a port nobody is listening on by opening and closing one; the system will not
        // immediately reuse it.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        await using var sensor = new Iv4VisionSensor(OptionsFor(deadPort));

        await Assert.ThrowsAsync<DeviceFaultException>(
            () => sensor.ConnectAsync(CancellationToken.None));
    }

    // ── 測試腳手架 / Test scaffolding ────────────────────────────────────────

    /// <summary>
    /// loopback 上的假 IV4 / A fake IV4 on loopback.
    ///
    /// 回應以「字串陣列」給出,每個元素寫成一個封包並在之間插入延遲 ——
    /// 這是唯一能造出「一筆電文分兩個封包抵達」的方法。
    /// 回傳 null 表示收到命令但不回應,用來驗證逾時。
    /// The reply is given as an array of strings, each written as its own packet with a delay
    /// between them — the only way to produce "one frame arriving as two packets". Returning
    /// null means the command is received and deliberately unanswered, which exercises the
    /// timeout.
    /// </summary>
    private sealed class FakeIv4Server : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly Func<int, string[]?> _respond;
        private readonly bool _closeAfterResponse;
        private readonly List<string> _commands = [];

        private int _triggers;

        public FakeIv4Server(Func<int, string[]?> respond, bool closeAfterResponse = false)
        {
            _respond = respond;
            _closeAfterResponse = closeAfterResponse;

            // 埠號 0 讓系統挑一個空閒埠,測試才能併行執行而不互搶
            // Port 0 lets the system pick a free port so the tests can run in parallel.
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _loop = ServeAsync(_cts.Token);
        }

        public int Port { get; }

        public int AcceptedConnections { get; private set; }

        public IReadOnlyList<string> Commands()
        {
            lock (_commands)
            {
                return [.. _commands];
            }
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                    AcceptedConnections++;

                    await using var stream = client.GetStream();
                    await HandleAsync(stream, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // 測試結束 / The test is over.
            }
            catch (Exception)
            {
                // 假伺服器的錯誤不該蓋掉受測失敗 / A fake's error must not mask the real assertion.
            }
        }

        private async Task HandleAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[256];
            var pending = new StringBuilder();

            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                pending.Append(Encoding.ASCII.GetString(buffer, 0, read));

                while (true)
                {
                    var text = pending.ToString();
                    var end = text.IndexOf('\r', StringComparison.Ordinal);
                    if (end < 0)
                    {
                        break;
                    }

                    var command = text[..end];
                    pending.Clear();
                    pending.Append(text[(end + 1)..]);

                    lock (_commands)
                    {
                        _commands.Add(command);
                    }

                    var chunks = _respond(_triggers++);
                    if (chunks is null)
                    {
                        continue;
                    }

                    for (var i = 0; i < chunks.Length; i++)
                    {
                        // 最後一段才附上結束字元,前面的刻意不完整 —— 這才逼得受測端組框
                        // Only the last chunk carries the terminator; the earlier ones are
                        // deliberately incomplete, which is what forces the driver to frame.
                        var payload = i == chunks.Length - 1 && !_closeAfterResponse
                            ? chunks[i] + "\r"
                            : chunks[i];

                        await stream.WriteAsync(Encoding.ASCII.GetBytes(payload), cancellationToken)
                            .ConfigureAwait(false);
                        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

                        if (i < chunks.Length - 1)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(30), cancellationToken).ConfigureAwait(false);
                        }
                    }

                    if (_closeAfterResponse)
                    {
                        return;
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();

            try
            {
                await _loop;
            }
            catch (Exception)
            {
                // 收尾例外無處可報 / Nothing useful to do with a shutdown exception.
            }

            _cts.Dispose();
        }
    }
}
