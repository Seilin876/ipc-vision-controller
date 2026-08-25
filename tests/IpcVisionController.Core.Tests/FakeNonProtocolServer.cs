using System.Net;
using System.Net.Sockets;
using System.Text;

namespace IpcVisionController.Core.Tests;

/// <summary>
/// loopback 上的假裝置 / A fake non-protocol device on loopback.
///
/// 回應以「字串陣列」給出,每個元素寫成一個封包並在之間插入延遲 ——
/// 這是唯一能造出「一筆電文分兩個封包抵達」的方法。
/// 回傳 null 表示收到命令但不回應,用來驗證逾時。
/// The reply is given as an array of strings, each written as its own packet with a delay
/// between them — the only way to produce "one frame arriving as two packets". Returning
/// null means the command is received and deliberately unanswered, which exercises the
/// timeout.
/// </summary>
internal sealed class FakeNonProtocolServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly Func<int, string[]?> _respond;
    private readonly bool _closeAfterResponse;
    private readonly List<string> _commands = [];

    private int _triggers;

    public FakeNonProtocolServer(Func<int, string[]?> respond, bool closeAfterResponse = false)
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
