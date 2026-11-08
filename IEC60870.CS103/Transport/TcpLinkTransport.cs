//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS103;

/// <summary>
/// 基于 TouchSocket <see cref="TcpClient"/> 的 103 主站传输（继承式接入）。
/// </summary>
/// <remarks>
/// <para>
/// TCP 只作为「透明串口线」：FT1.2 帧字节原样承载，<b>无 APCI / 无长度前缀 /
/// 无 STARTDT 握手 / 无序列号</b>。
/// </para>
/// <para>
/// 接收侧覆写 <see cref="OnTcpReceived"/> 把字节推入队列，
/// 由链路层通过 <see cref="ReadAsync"/> 拉取——这样帧定界与串口路径共享同一份
/// <see cref="Ft12FrameParser"/>。
/// </para>
/// </remarks>
internal sealed class TcpClientLinkTransport : TcpClient, IIec103LinkTransport
{
    private readonly ByteQueue _queue = new();
    private readonly string _host;
    private readonly int _port;
    private int _disposed;

    public TcpClientLinkTransport(string host, int port, ILog? logger = null)
    {
        _host = host;
        _port = port;
        EndPoint = $"{host}:{port}";
        Logger = logger;
    }

    /// <inheritdoc />
    public bool IsOpen => Online;

    /// <inheritdoc />
    public string EndPoint { get; }

    /// <inheritdoc />
    public event EventHandler<Iec103TransportClosedEventArgs>? TransportClosed;

    /// <summary>建立连接（含建连超时）。</summary>
    public async Task ConnectAsync(int timeoutMs, CancellationToken ct, Action<TouchSocketConfig>? configureConfig = null)
    {
        var config = new TouchSocketConfig();
        config.SetRemoteIPHost(new IPHost(EndPoint));

        // 用户自定义配置在库默认项之后应用
        configureConfig?.Invoke(config);

        await SetupAsync(config).ConfigureAwait(false);

        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectTimeout.CancelAfter(timeoutMs > 0 ? timeoutMs : 5000);
        await base.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => _queue.ReadAsync(buffer, ct);

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (!Online)
        {
            throw new InvalidOperationException("TCP 连接未建立");
        }

        return new ValueTask(SendAsync(data, ct));
    }

    /// <inheritdoc />
    protected override async Task OnTcpReceived(ReceivedDataEventArgs e)
    {
        if (!e.Memory.IsEmpty)
        {
            _queue.Write(e.Memory.Span);
        }

        await base.OnTcpReceived(e).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task OnTcpClosed(ClosedEventArgs e)
    {
        _queue.Complete();
        TransportClosed?.Invoke(this, new Iec103TransportClosedEventArgs(EndPoint, "TCP 连接已关闭", e.Exception));
        await base.OnTcpClosed(e).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        // 幂等：重连/被动断线与显式断开可能先后调用同一个传输。
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return default;
        }

        _queue.Complete();
        Dispose();
        return default;
    }
}

/// <summary>
/// 103 从站会话的传输实现（每个主站连接一个实例）。
/// </summary>
/// <remarks>
/// 103 现场常有多个主站（监控后台、保护信息子站、便携式试验仪）同时连同一装置，
/// 故每个连接的传输实例必须<b>各自独立</b>。
/// </remarks>
internal sealed class TcpSessionLinkTransport : IIec103LinkTransport
{
    private readonly TcpSessionClient _session;
    private readonly ByteQueue _queue = new();
    private bool _disposed;

    public TcpSessionLinkTransport(TcpSessionClient session)
    {
        _session = session;
        EndPoint = session.RemoteEndPoint?.ToString() ?? "unknown";
    }

    /// <inheritdoc />
    public bool IsOpen => !_disposed && _session.Online;

    /// <inheritdoc />
    public string EndPoint { get; }

    /// <inheritdoc />
    public event EventHandler<Iec103TransportClosedEventArgs>? TransportClosed;

    /// <inheritdoc />
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => _queue.ReadAsync(buffer, ct);

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("会话已关闭");
        }

        return new ValueTask(_session.SendAsync(data, ct));
    }

    /// <summary>宿主会话收到数据时调用。</summary>
    public void PushData(ReadOnlySpan<byte> data) => _queue.Write(data);

    /// <summary>宿主会话关闭时调用。</summary>
    public void NotifyClosed(string? reason = null, Exception? exception = null)
    {
        _queue.Complete();
        TransportClosed?.Invoke(this, new Iec103TransportClosedEventArgs(EndPoint, reason, exception));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return default;
        }

        _disposed = true;
        _queue.Complete();
        return default;
    }
}

/// <summary>
/// 异步字节队列：单生产者（TouchSocket 回调）/ 单消费者（链路循环）。
/// </summary>
/// <remarks>
/// 定长环形缓冲 + <see cref="SemaphoreSlim"/> 信号。写侧满时丢弃最旧数据并计数，
/// 避免慢消费导致内存无界增长（103 现场断线时常见）。
/// <para>
/// 拉取式设计，额外保证
/// <see cref="Complete"/> 后 <see cref="ReadAsync"/> 立即返回 0，
/// 使链路循环能干净退出而不依赖取消令牌。
/// </para>
/// </remarks>
internal sealed class ByteQueue
{
    private const int DefaultCapacity = 16 * 1024;

    private readonly byte[] _buffer;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _signal = new(0);
    private int _head;
    private int _count;
    private bool _completed;
    private long _droppedBytes;

    public ByteQueue(int capacity = DefaultCapacity)
    {
        _buffer = new byte[capacity > 0 ? capacity : DefaultCapacity];
    }

    /// <summary>因缓冲满而丢弃的字节数（诊断）。</summary>
    public long DroppedBytes => Interlocked.Read(ref _droppedBytes);

    /// <summary>当前待读字节数。</summary>
    public int BufferedBytes
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>写入数据（内部串行化）。</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            if (data.Length > _buffer.Length)
            {
                // 单块数据大于整个环形缓冲：只保留最后 _buffer.Length 字节并重置头/尾。
                // 原实现 drop = data.Length - space 会让 _count 变为负数，
                // 随后 AppendUnsafe 覆盖已写内容、打乱字节顺序（再大一倍直接抛异常）。
                var overflow = data.Length - _buffer.Length;
                Interlocked.Add(ref _droppedBytes, overflow + _count);
                data = data[^_buffer.Length..];
                _head = 0;
                _count = 0;
                data.CopyTo(_buffer);
                _count = _buffer.Length;
            }
            else
            {
                var space = _buffer.Length - _count;
                if (data.Length > space)
                {
                    // 缓冲满：真正丢弃「最旧」的字节（前移 _head），保留新数据。
                    // 原实现是 data = data[drop..]，即砍掉新 TCP 块的头部——
                    // 对 FT1.2 这会砍掉 68 L L 68 / 10 / E5 起始字符，
                    // 让分帧器从半截帧体开始解析，整帧丢失。
                    // drop 以 _count 兜底，保证 _count 不会被减成负数。
                    var drop = Math.Min(data.Length - space, _count);
                    Interlocked.Add(ref _droppedBytes, drop);
                    _head = (_head + drop) % _buffer.Length;
                    _count -= drop;
                }

                AppendUnsafe(data);
            }
        }

        // 信号量按「写入批次」计数，不能按字节数释放：
        // 读取方一次 ReadAsync 会把队列现有字节全部取走却只消耗 1 个信号，
        // 若按字节释放会残留多余计数，导致下次 WaitAsync 立即返回却读到 0 字节，
        // 被误判为「连接关闭」而让读循环提前退出。
        _signal.Release(1);
    }

    private void AppendUnsafe(ReadOnlySpan<byte> data)
    {
        var tail = (_head + _count) % _buffer.Length;
        var firstPart = Math.Min(data.Length, _buffer.Length - tail);
        data[..firstPart].CopyTo(_buffer.AsSpan(tail));
        if (firstPart < data.Length)
        {
            data[firstPart..].CopyTo(_buffer);
        }

        _count += data.Length;
    }

    /// <summary>
    /// 读取至多 <paramref name="buffer"/>.Length 字节；返回 0 表示队列已完成且无剩余。
    /// </summary>
    /// <remarks>
    /// 返回 0 仅当 <see cref="Complete"/> 已调用。队列暂时为空时<b>阻塞等待</b>，
    /// 不能返回 0——否则调用方会把它当成「连接关闭」而提前退出读循环。
    /// </remarks>
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (true)
        {
            lock (_gate)
            {
                if (_count > 0)
                {
                    var take = Math.Min(buffer.Length, _count);
                    var firstPart = Math.Min(take, _buffer.Length - _head);
                    _buffer.AsSpan(_head, firstPart).CopyTo(buffer.Span);
                    if (firstPart < take)
                    {
                        _buffer.AsSpan(0, take - firstPart).CopyTo(buffer.Span[firstPart..]);
                    }

                    _head = (_head + take) % _buffer.Length;
                    _count -= take;
                    return take;
                }

                if (_completed)
                {
                    return 0;
                }
            }

            // 队列空且未完成：等待下一次写入或完成信号
            await _signal.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>标记完成：唤醒等待者，后续读取返回 0。</summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
        }

        _signal.Release();
    }
}
