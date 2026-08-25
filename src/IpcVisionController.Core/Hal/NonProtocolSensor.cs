using System.Net.Sockets;
using System.Text;

namespace IpcVisionController.Core.Hal;

/// <summary>
/// 無協定 TCP 裝置的共用連線層 / Shared link layer for a non-protocol TCP device.
///
/// 負責的事 / What this owns:
/// 連線、組框、逾時、把通訊層的失敗一律轉成 <see cref="DeviceFaultException"/>,以及把原始電文
/// 交給畫面。子類別只負責「這一格是什麼意思」。
/// The link, the framing, the timeouts, turning every transport failure into a
/// <see cref="DeviceFaultException"/>, and handing the raw frame to the UI. Subclasses own only
/// the meaning of each field.
///
/// 為什麼不做成兩個各自獨立的驅動 / Why not two independent drivers:
/// 組框與逾時是這條路徑上最容易寫錯、而且錯了最難重現的部分。寫兩份就是兩份錯的機會,
/// 而且其中一份的錯只會在另一台裝置身上看不到 —— 排查時最先被懷疑的永遠是硬體。
/// Framing and timeouts are the easiest thing on this path to get wrong and the hardest to
/// reproduce when wrong. Two copies is two chances at it, and a bug in one copy is invisible on
/// the other device — while the first thing suspected is always the hardware.
/// </summary>
public abstract class NonProtocolSensor : IDevice, IRawFrameSource
{
    private readonly NonProtocolLinkOptions _link;
    private readonly SemaphoreSlim _wireGate = new(1, 1);

    private TcpClient? _client;
    private NetworkStream? _stream;
    private bool _disposed;

    protected NonProtocolSensor(NonProtocolLinkOptions link)
    {
        ArgumentNullException.ThrowIfNull(link);
        link.Validate();
        _link = link;
    }

    public string Name => _link.DeviceName;

    public bool IsConnected => _client?.Connected == true;

    /// <summary>
    /// 目前生效的設定摘要 / A summary of the settings now in force.
    /// 供畫面在開機時顯示 —— 索引錯誤的表徵是「每張標籤都判退」,把實際生效的索引印出來
    /// 才能與原始電文對照。
    /// Shown by the UI at startup: a wrong index presents as "every label rejects", so the
    /// indexes actually in force have to be printed to be checked against the raw frame.
    /// </summary>
    public string Configuration => _link.Describe();

    /// <inheritdoc />
    public event EventHandler<RawFrameEventArgs>? RawFrameReceived;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _wireGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 冪等:重複連線會白丟掉現有連線,並在現場留下一條半開的 socket
            // Idempotent: reconnecting would discard the live link and leave a half-open socket
            // behind on the line.
            if (IsConnected)
            {
                return;
            }

            CloseWire();

            var client = new TcpClient();
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linked.CancelAfter(TimeSpan.FromMilliseconds(_link.ConnectTimeoutMs));

                await client.ConnectAsync(_link.Host, _link.Port, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                client.Dispose();
                throw new DeviceFaultException(
                    $"{Name} 連線逾時 {_link.ConnectTimeoutMs} ms / connect timed out. "
                    + "位址或網段不對,或裝置沒有回應 / wrong address or subnet, or the device is not answering.");
            }
            catch (SocketException ex)
            {
                client.Dispose();

                // 「拒絕連線」與「逾時」是兩件完全不同的事,值得分開講:
                // 拒絕代表位址是通的而該埠沒有人在聽 —— 通常是埠號錯、無協定通訊沒啟用,
                // 或裝置只接受一條連線而設定軟體正佔著。
                // Refused and timed out are quite different and worth saying separately: refused
                // means the address answered and nothing is listening on that port — usually the
                // wrong port, non-protocol communication left disabled, or a single-connection
                // device with its setup software still attached.
                var hint = ex.SocketErrorCode == SocketError.ConnectionRefused
                    ? " 位址可達但該埠沒有人在聽：確認埠號、確認裝置端已啟用無協定通訊、"
                        + "並關閉正佔著連線的設定軟體 / the address answered but nothing is listening on "
                        + "that port: check the port, check that non-protocol communication is enabled, "
                        + "and close any setup software holding the link."
                    : string.Empty;

                throw new DeviceFaultException($"{Name} 連線失敗 / connect failed: {ex.Message}{hint}");
            }
            catch
            {
                client.Dispose();
                throw;
            }

            _client = client;
            _stream = client.GetStream();
        }
        finally
        {
            _wireGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await _wireGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CloseWire();
        }
        finally
        {
            _wireGate.Release();
        }
    }

    /// <summary>
    /// 觸發一次並取回已分格的電文 / Trigger once and return the frame already split into fields.
    /// </summary>
    /// <exception cref="DeviceFaultException">
    /// 通訊失敗,或裝置回報自身異常 / The link failed, or the device reported its own fault.
    /// </exception>
    protected async Task<string[]> TriggerAndSplitAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _wireGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = _stream;
            if (stream is null || !IsConnected)
            {
                throw new InvalidOperationException($"{Name} 尚未連線 / is not connected.");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(TimeSpan.FromMilliseconds(_link.ResponseTimeoutMs));

            try
            {
                var command = Encoding.ASCII.GetBytes(_link.TriggerCommand + _link.TerminatorText);
                await stream.WriteAsync(command, linked.Token).ConfigureAwait(false);
                await stream.FlushAsync(linked.Token).ConfigureAwait(false);

                var frame = await ReadFrameAsync(stream, linked.Token).ConfigureAwait(false);

                // 先記錄再解析：解析失敗時,現場最需要看到的正是那筆解不開的電文
                // Record before parsing: when parsing fails, the frame that broke it is precisely
                // what the line needs to see.
                RawFrameReceived?.Invoke(this, new RawFrameEventArgs(Name, frame));

                return NonProtocolFrame.SplitFields(frame, _link, Name);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 裝置沒有在逾時內回應是設備問題,不是沒讀到 / A silent device is an equipment
                // problem, not a no-read.
                throw new DeviceFaultException(
                    $"{Name} 在 {_link.ResponseTimeoutMs} ms 內未回應 / did not answer in time. "
                    + "埠通了但命令可能不對：確認觸發命令與結束字元 / the port opened, so check the "
                    + "trigger command and the terminator.");
            }
            catch (IOException ex)
            {
                throw new DeviceFaultException($"{Name} 通訊中斷 / link failed: {ex.Message}");
            }
            catch (SocketException ex)
            {
                throw new DeviceFaultException($"{Name} 通訊中斷 / link failed: {ex.Message}");
            }
        }
        finally
        {
            _wireGate.Release();
        }
    }

    /// <summary>
    /// 讀到結束字元為止 / Read until the terminator arrives.
    ///
    /// 必須自己組框,不能「讀一次就當成一筆」:TCP 是位元流,一筆電文可能分成數個封包抵達,
    /// 兩筆也可能黏在同一個封包裡。假設一次讀取等於一筆電文,現場會看到偶發的截斷電文,
    /// 而那種錯誤只在網路忙碌時出現,最難重現。
    /// Framing has to be done here rather than treating one read as one message: TCP is a byte
    /// stream, so a frame may arrive split across packets and two frames may share one. Assuming a
    /// read equals a frame produces occasional truncated messages that only appear when the
    /// network is busy — the hardest kind to reproduce.
    /// </summary>
    private async Task<string> ReadFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var terminator = _link.TerminatorText;
        var buffer = new byte[256];
        var builder = new StringBuilder();

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new DeviceFaultException($"{Name} 在回應完成前關閉連線 / closed the link mid-response.");
            }

            builder.Append(Encoding.ASCII.GetString(buffer, 0, read));

            var text = builder.ToString();
            var end = text.IndexOf(terminator, StringComparison.Ordinal);
            if (end >= 0)
            {
                return text[..end];
            }

            // 沒有結束字元的無上限累積會在裝置設定錯誤時吃光記憶體
            // Unbounded growth without a terminator would exhaust memory if the device is
            // configured to emit none.
            if (builder.Length > MaxFrameLength)
            {
                throw new DeviceFaultException(
                    $"{Name} 回應超過 {MaxFrameLength} 位元組仍無結束字元 / no terminator within "
                    + $"{MaxFrameLength} bytes. 結束字元設定可能不符 / the terminator setting may not match.");
            }
        }
    }

    /// <summary>單筆電文的上限 / Cap on one frame's length.</summary>
    private const int MaxFrameLength = 8 * 1024;

    private void CloseWire()
    {
        _stream?.Dispose();
        _stream = null;
        _client?.Dispose();
        _client = null;
    }

    public ValueTask DisposeAsync()
    {
        // 冪等:組裝根在關機路徑上可能對同一個物件處置多次
        // Idempotent: the composition root may dispose the same object more than once on the
        // shutdown path.
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        CloseWire();
        _wireGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
