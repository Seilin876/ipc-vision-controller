using System.Net.Sockets;
using System.Text;
using IpcVisionController.Core.Models;

namespace IpcVisionController.Core.Hal;

/// <summary>
/// 實機 Keyence IV4 驅動 / Real Keyence IV4 driver, over the non-protocol TCP channel.
///
/// 為什麼一個類別同時實作兩個介面 /
/// Why one class implements both interfaces:
/// 現場只有一顆感測器,它同時輸出條碼與字符結果。若拆成兩個類別,兩者會各開一條連線、
/// 各下一次觸發 —— 一個週期拍兩張影像,而追溯紀錄裡的條碼與字符會來自不同張。
/// 光學條件不完全相同時，這會讓「哪一半是對的」變成無法回答的問題。
/// 一顆感測器就用一個物件、一條連線、一次觸發。
/// There is one sensor on the line and it emits both the codes and the characters. Split
/// across two classes, each would open its own link and issue its own trigger — two captures
/// per cycle, with the codes and the characters in one traceability record coming from
/// different frames. When the optics differ even slightly, "which half is right" becomes
/// unanswerable. One sensor gets one object, one link, one trigger.
///
/// C# 不允許只靠回傳型別區分同名方法,所以兩個 TriggerAsync 必須用顯式介面實作。
/// C# cannot overload on return type alone, so both TriggerAsync methods are explicit
/// interface implementations.
///
/// 一次拍攝供兩次呼叫 / One capture serves both calls:
/// <see cref="Machine.InspectionSequencer"/> 每個週期先要條碼、再要字符。讀碼那次做真正的
/// 觸發並把兩半都存起來,字符那次直接取用同一次拍攝的另一半。緩衝是單次有效的,
/// 而且每次讀碼都會整份覆寫,所以中途停機留下的殘值不可能被下一個週期取用。
/// The sequencer asks for codes first, then characters, every cycle. The code call performs
/// the real trigger and keeps both halves; the character call takes the other half of that
/// same capture. The buffer is single-use and every code call overwrites it wholesale, so a
/// leftover from an interrupted cycle can never be served to the next one.
/// </summary>
public sealed class Iv4VisionSensor : ICodeReader, ICharacterVerifier
{
    private readonly Iv4Options _options;
    private readonly SemaphoreSlim _wireGate = new(1, 1);

    private TcpClient? _client;
    private NetworkStream? _stream;
    private Iv4Capture? _bufferedCapture;
    private bool _disposed;

    public Iv4VisionSensor(Iv4Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    public string Name => $"Keyence IV4 @ {_options.Host}:{_options.Port}";

    public bool IsConnected => _client?.Connected == true;

    /// <summary>
    /// 目前生效的欄位配置摘要 / A summary of the field layout now in force.
    /// 供畫面在開機時顯示。索引錯誤的表徵是「每張標籤都判退」,把實際生效的索引印出來,
    /// 才能與原始電文對照 —— 否則只能憑判定結果猜設定,而那猜不出來。
    /// Shown by the UI at startup. A wrong index presents as "every label rejects", so the
    /// indexes actually in force have to be printed to be checked against the raw frame;
    /// otherwise the only evidence is the verdicts, which cannot distinguish the cause.
    /// </summary>
    public string Configuration => _options.Describe();

    /// <summary>
    /// 每收到一筆原始電文就引發 / Raised for every raw frame received.
    ///
    /// 這是實機導入時最有用的一件事:電文格式由感測器端的設定決定,只有看到真正的電文
    /// 才能確認 <see cref="Iv4Options"/> 的欄位索引對不對。沒有這個事件,設定錯誤的表徵
    /// 會是「每張標籤都判退」,而那看起來跟印刷不良、跟感測器沒對焦一模一樣。
    /// The single most useful thing when commissioning: the layout is decided on the sensor,
    /// and only a real frame confirms whether the configured field indexes are right. Without
    /// this event, a misconfiguration presents as "every label rejects", which looks exactly
    /// like bad print and exactly like a sensor out of focus.
    /// </summary>
    public event EventHandler<Iv4RawFrameEventArgs>? RawFrameReceived;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _wireGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 冪等:同一個物件被當成兩個介面使用,協調器的初始化會連線兩次。
            // 第二次重新建連線會白丟掉第一次的連線,並在現場留下一條半開的 socket。
            // Idempotent: one object serving two interfaces gets connected twice by the
            // sequencer's initialise. Reconnecting on the second call would discard the first
            // link and leave a half-open socket behind on the line.
            if (IsConnected)
            {
                return;
            }

            CloseWire();

            var client = new TcpClient();
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linked.CancelAfter(TimeSpan.FromMilliseconds(_options.ConnectTimeoutMs));

                await client.ConnectAsync(_options.Host, _options.Port, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                client.Dispose();
                throw new DeviceFaultException(
                    $"{Name} 連線逾時 {_options.ConnectTimeoutMs} ms / connect timed out.");
            }
            catch (SocketException ex)
            {
                client.Dispose();
                throw new DeviceFaultException($"{Name} 連線失敗 / connect failed: {ex.Message}");
            }
            catch
            {
                client.Dispose();
                throw;
            }

            _client = client;
            _stream = client.GetStream();

            // 換了連線就沒有有效的拍攝了 / A new link invalidates any buffered capture.
            _bufferedCapture = null;
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
    /// 讀碼 —— 執行真正的觸發 / The code read, which performs the real trigger.
    /// </summary>
    async Task<IReadOnlyList<CodeResult>> ICodeReader.TriggerAsync(CancellationToken cancellationToken)
    {
        var capture = await TriggerAsync(cancellationToken).ConfigureAwait(false);
        _bufferedCapture = capture;
        return capture.Codes;
    }

    /// <summary>
    /// 字符檢測 —— 優先取用讀碼那次的同一張拍攝 /
    /// The character verification, preferring the same capture the code read took.
    /// </summary>
    async Task<IReadOnlyList<CharacterResult>> ICharacterVerifier.TriggerAsync(CancellationToken cancellationToken)
    {
        var buffered = Interlocked.Exchange(ref _bufferedCapture, null);
        if (buffered is not null)
        {
            return buffered.Characters;
        }

        // 沒有緩衝就自己觸發一次。只有在本物件被單獨當成字符檢測器使用時才會走到這裡;
        // 多拍一張比回傳上一張的殘值安全得多 —— 後者會把前一張標籤的結果記到這一張上。
        // With nothing buffered, trigger for ourselves. This path is only reached when the
        // object is used as a character verifier alone. An extra capture is far safer than
        // serving a stale one, which would log the previous label's result against this one.
        var capture = await TriggerAsync(cancellationToken).ConfigureAwait(false);
        return capture.Characters;
    }

    /// <summary>
    /// 觸發一次拍攝並取回兩半結果 / Trigger one capture and return both halves.
    /// </summary>
    public async Task<Iv4Capture> TriggerAsync(CancellationToken cancellationToken)
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
            linked.CancelAfter(TimeSpan.FromMilliseconds(_options.ResponseTimeoutMs));

            try
            {
                var command = Encoding.ASCII.GetBytes(_options.TriggerCommand + _options.TerminatorText);
                await stream.WriteAsync(command, linked.Token).ConfigureAwait(false);
                await stream.FlushAsync(linked.Token).ConfigureAwait(false);

                var frame = await ReadFrameAsync(stream, linked.Token).ConfigureAwait(false);

                // 先記錄再解析：解析失敗時,現場最需要看到的正是那筆解不開的電文。
                // Record before parsing: when parsing fails, the frame that broke it is
                // precisely what the line needs to see.
                RawFrameReceived?.Invoke(this, new Iv4RawFrameEventArgs(frame));

                return Iv4ResponseParser.Parse(frame, _options);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 感測器沒有在逾時內回應是設備問題,不是沒讀到 / A silent sensor is an
                // equipment problem, not a no-read.
                throw new DeviceFaultException(
                    $"{Name} 在 {_options.ResponseTimeoutMs} ms 內未回應 / did not answer in time.");
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
    /// Framing has to be done here rather than treating one read as one message: TCP is a
    /// byte stream, so a frame may arrive split across packets and two frames may share one.
    /// Assuming a read equals a frame produces occasional truncated messages that only appear
    /// when the network is busy — the hardest kind to reproduce.
    /// </summary>
    private async Task<string> ReadFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var terminator = _options.TerminatorText;
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

            // 沒有結束字元的無上限累積會在感測器設定錯誤時吃光記憶體
            // Unbounded growth without a terminator would exhaust memory if the sensor is
            // configured to emit none.
            if (builder.Length > MaxFrameLength)
            {
                throw new DeviceFaultException(
                    $"{Name} 回應超過 {MaxFrameLength} 位元組仍無結束字元 / no terminator within {MaxFrameLength} bytes.");
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
        _bufferedCapture = null;
    }

    public ValueTask DisposeAsync()
    {
        // 冪等:同一個物件以兩個介面身分交給組裝根,關機時會被處置兩次。
        // Idempotent: handed to the composition root under two interfaces, this object is
        // disposed twice on shutdown.
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

/// <summary>原始電文事件 / Raw frame notification.</summary>
public sealed class Iv4RawFrameEventArgs(string frame) : EventArgs
{
    /// <summary>已去除結束字元的電文 / The frame with its terminator stripped.</summary>
    public string Frame { get; } = frame;
}
