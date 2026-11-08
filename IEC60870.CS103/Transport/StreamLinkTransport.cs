//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS103;

/// <summary>
/// 基于任意 <see cref="Stream"/> 的 103 链路传输。
/// </summary>
/// <remarks>
/// <para>
/// 这是串口支持的<b>通用接缝</b>：<see cref="SerialPort.BaseStream"/>、
/// 无线数传、协议网关、内存管道（测试用）都只是一个 <see cref="Stream"/>，
/// 因此本实现同时覆盖「串口」「虚拟串口」与「自定义传输」三种场景。
/// </para>
/// <para>
/// <b>超时语义</b>：不依赖 <c>Stream.ReadTimeout</c>。某些流（网络流）没有该属性，
/// 而 103 原实现靠它抛 <c>TimeoutException</c> 轮询取消，导致断线后半帧永久卡死。
/// 此处所有等待由 <see cref="CancellationToken"/> 驱动，帧内字符超时交给
/// <see cref="Ft12FrameParser.TryResetStale"/>。
/// </para>
/// <para>
/// <b>所有权</b>：构造时传入的 <see cref="Stream"/> 由调用方拥有——
/// <see cref="DisposeAsync"/> 只标记弃用，<b>不</b>调用 <c>Dispose</c>，
/// 以便调用方复用（例如关闭后重新打开串口）。
/// </para>
/// </remarks>
internal sealed class StreamLinkTransport : IIec103LinkTransport
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    // 0 = 正常；1 = 已失败（已广播过 TransportClosed）。
    // 失败必须与 DisposeAsync 一样「粘住」：原实现读/写失败只 RaiseClosed 就返回，
    // IsOpen 仍为 true、后续 ReadAsync 仍会阻塞在同一坏流上，
    // 链路循环只能等取消令牌才能退出（TcpLinkTransport 靠 _queue.Complete() 立即返回 0）。
    private int _failed;

    /// <summary>用已有字节流构造传输。</summary>
    /// <param name="stream">双向字节流（读 = 接收，写 = 发送）。</param>
    /// <param name="endPoint">对端描述（日志/诊断用），默认取流的类型名。</param>
    public StreamLinkTransport(Stream stream, string? endPoint = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        EndPoint = endPoint ?? stream.GetType().Name;
    }

    /// <inheritdoc />
    public bool IsOpen => !_disposed && Volatile.Read(ref _failed) == 0 && _stream.CanRead;

    /// <inheritdoc />
    public string EndPoint { get; }

    /// <inheritdoc />
    public event EventHandler<Iec103TransportClosedEventArgs>? TransportClosed;

    /// <inheritdoc />
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        // 已释放或已失败：立即返回 0（对端关闭语义），不再碰底层的坏流。
        // 幂等：失败后无论调用多少次都应立刻返回，而不是再次阻塞/抛异常。
        if (_disposed || Volatile.Read(ref _failed) != 0)
        {
            return 0;
        }

        try
        {
            var read = await _stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0)
            {
                // 流结束同样要置失败标志：否则 IsOpen 仍为 true，上层无法区分
                // 「对端关闭」与「暂时无数据」
                MarkFailed("流已结束");
            }

            return read;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ObjectDisposedException)
        {
            MarkFailed("流已释放");
            return 0;
        }
        catch (Exception ex)
        {
            MarkFailed($"读取失败: {ex.Message}", ex);
            return 0;
        }
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        // 失败后与已释放同语义：立即抛，不再尝试写坏流（幂等）
        if (_disposed || Volatile.Read(ref _failed) != 0)
        {
            throw new ObjectDisposedException(nameof(StreamLinkTransport));
        }

        // 串口与多数 Stream 不支持并发写，这里串行化保证帧不交错
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(data, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            MarkFailed($"写入失败: {ex.Message}", ex);
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// 标记传输已失败并广播一次 <see cref="TransportClosed"/>（幂等）。
    /// </summary>
    /// <remarks>
    /// 置位与广播绑定在一起，避免「事件发了两遍」或「标志置了但订阅方不知道」；
    /// 只有首个调用者（CAS 成功）才广播。
    /// </remarks>
    private void MarkFailed(string reason, Exception? exception = null)
    {
        if (Interlocked.Exchange(ref _failed, 1) != 0)
        {
            return;
        }

        RaiseClosed(reason, exception);
    }

    private void RaiseClosed(string reason, Exception? exception = null)
        => TransportClosed?.Invoke(this, new Iec103TransportClosedEventArgs(EndPoint, reason, exception));

    /// <inheritdoc />
    /// <remarks>不释放底层 <see cref="Stream"/>（所有权归调用方）。</remarks>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return default;
        }

        _disposed = true;
        _writeLock.Dispose();
        return default;
    }
}

/// <summary>
/// 基于 <see cref="SerialPort"/> 的 103 链路传输（串口端点）。
/// </summary>
/// <remarks>
/// <para>
/// 内部使用 <see cref="SerialPort.BaseStream"/>，因此与 <see cref="StreamLinkTransport"/>
/// 共享同一套读写语义；本类额外负责串口的打开/关闭与访问被拒（<c>PortDenied</c>）的识别。
/// </para>
/// <para>
/// <b>所有权</b>：<see cref="SerialPort"/> 由调用方拥有。
/// <see cref="DisposeAsync"/> 只 <c>Close()</c> 不 <c>Dispose()</c>，
/// 关闭后可重新 <c>Open()</c> 复用同一实例。
/// </para>
/// </remarks>
internal sealed class SerialLinkTransport : IIec103LinkTransport
{
    private readonly SerialPort _port;
    private StreamLinkTransport? _inner;
    private bool _ownsPort;
    private bool _disposed;

    /// <summary>构造传输。串口可以在未打开状态下传入，由 <see cref="Open"/> 负责打开。</summary>
    /// <param name="port">串口实例；未打开时由 <see cref="Open"/> 负责打开。</param>
    /// <remarks>
    /// <b>不能在这里读 <see cref="SerialPort.BaseStream"/>：</b>串口未打开时该属性抛
    /// <see cref="InvalidOperationException"/>（"The BaseStream is only available when
    /// the port is open"）。而本类的契约正是「调用方传未打开的串口，库在 Open() 里打开」，
    /// 构造期提前取流会让整条串口路径在启动前就崩掉（与 CS101 的
    /// SerialTransceiverFT12 是同型缺陷，那边已改为延迟解析）。
    /// 内层 StreamLinkTransport 因此延迟到 <see cref="Open"/> 成功后才创建。
    /// </remarks>
    public SerialLinkTransport(SerialPort port)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
    }

    private static string Describe(SerialPort port)
        => $"{port.PortName}@{port.BaudRate},{port.Parity},{port.DataBits},{port.StopBits}";

    /// <inheritdoc />
    /// <remarks>
    /// 同时反映内层流传输的失败状态：内层读写失败后 <see cref="SerialPort.IsOpen"/>
    /// 仍为 true（串口对象没关），只看端口状态会让调用方误以为链路可用。
    /// </remarks>
    public bool IsOpen => !_disposed && _port.IsOpen && (_inner?.IsOpen ?? true);

    /// <inheritdoc />
    public string EndPoint => Describe(_port);

    /// <inheritdoc />
    public event EventHandler<Iec103TransportClosedEventArgs>? TransportClosed;

    /// <summary>
    /// 打开串口。
    /// </summary>
    /// <exception cref="IOException">串口被占用或不存在。</exception>
    public void Open()
    {
        if (_port.IsOpen)
        {
            EnsureInner();
            return;
        }

        try
        {
            _port.Open();
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException($"串口 {_port.PortName} 被占用或无权限访问", ex);
        }

        _ownsPort = true;

        // 丢弃打开前残留在驱动缓冲里的噪声字节，避免分帧器把旧数据当新帧
        try
        {
            _port.DiscardInBuffer();
            _port.DiscardOutBuffer();
        }
        catch (IOException)
        {
            // 某些虚拟串口不支持 Discard，忽略
        }

        EnsureInner();
    }

    /// <summary>串口打开后解析 <see cref="SerialPort.BaseStream"/> 并创建内层流传输（幂等）。</summary>
    private void EnsureInner()
    {
        if (_inner != null)
        {
            return;
        }

        _inner = new StreamLinkTransport(_port.BaseStream, Describe(_port));
        _inner.TransportClosed += (_, e) => TransportClosed?.Invoke(this, e);
    }

    /// <inheritdoc />
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        EnsureInner();   // 允许调用方自行 Open 后再读（不经过本类 Open）
        return _inner.ReadAsync(buffer, ct);
    }

    /// <inheritdoc />
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        EnsureInner();
        return _inner.WriteAsync(data, ct);
    }

    /// <summary>当前串口实例（诊断/测试用）。</summary>
    public SerialPort Port => _port;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_inner != null)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }

        // 只关闭不释放：串口对象所有权归调用方，Close 后可重新 Open 复用
        if (_ownsPort)
        {
            try
            {
                if (_port.IsOpen)
                {
                    _port.Close();
                }
            }
            catch (IOException)
            {
                // 端口已消失（USB 拔出等），忽略
            }
        }
    }
}
