//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS104;

/// <summary>
/// IEC 60870-5-104 服务端每连接会话。由 <see cref="Iec104Server"/> 自动创建，
/// 协议状态机与定时循环聚合在共享的 <see cref="ApduEndpoint"/>（服务端角色）。
/// </summary>
public sealed class Iec104Session : TcpSessionClient, IIec104Session, IApduSink
{
    private ApduEndpoint _endpoint;
    private Iec104Server _server;

    #region 属性

    /// <summary>该会话对应的连接层状态机。</summary>
    public ApduConnection Connection => _endpoint?.Connection;

    /// <summary>数据传输是否已激活（收到 STARTDT_ACT 后）。</summary>
    public bool IsActivated => _endpoint?.Connection?.IsActive ?? false;

    /// <summary>
    /// 最近一次底层连接断开的原因。仅当 <see cref="Iec104Server.ConnectionEvent"/> 收到
    /// <see cref="ApduConnectionEvent.ConnectionClosed"/> 后有意义；未连接时为 <see cref="ConnectionCloseReason.Unknown"/>。
    /// </summary>
    public ConnectionCloseReason LastCloseReason => _endpoint?.CloseReason ?? ConnectionCloseReason.Unknown;

    #endregion 属性

    #region 事件

    /// <summary>
    /// 上行原始报文（客户端→本会话）。参数为完整单个 APDU（含 0x68 APCI 头），
    /// 生命周期安全（已拷贝为 byte[]），无订阅者零开销。适用于报文调试/抓包。
    /// </summary>
    public event Action<byte[]> RawFrameReceived
    {
        add
        {
            if (_endpoint != null)
            {
                _endpoint.RawFrameReceived += value;
            }
        }
        remove
        {
            if (_endpoint != null)
            {
                _endpoint.RawFrameReceived -= value;
            }
        }
    }

    /// <summary>
    /// 下行原始报文（本会话→客户端）。参数为即将发送的完整单个 APDU（含 0x68 APCI 头），
    /// 覆盖 I/S/U 三类帧。生命周期安全（已拷贝为 byte[]），无订阅者零开销。
    /// </summary>
    public event Action<byte[]> RawFrameSent;

    #endregion 事件

    #region 构造

    /// <summary>由 <see cref="Iec104Server"/> 自动创建，协议标识固定为 <see cref="Iec60870Utility.Iec104"/>（"iec104"）。</summary>
    public Iec104Session()
    {
        Protocol = new Protocol(Iec60870Utility.Iec104);
    }

    #endregion 构造

    #region IApduSink

    ValueTask IApduSink.SendAsync(ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken)
    {
        // 下行原始报文（有订阅者才分配拷贝，无订阅者零开销）
        if (RawFrameSent != null)
        {
            RawFrameSent(apdu.ToArray());
        }

        return new ValueTask(base.SendAsync(apdu, cancellationToken));
    }

    bool IApduSink.IsConnected => Online;

    #endregion IApduSink

    /// <summary>向该会话对端发送一个 ASDU（I 帧）。</summary>
    public async Task SendAsync(ASDU asdu, CancellationToken cancellationToken = default)
    {
        using var link = _endpoint.LinkScoped(cancellationToken);
        await _endpoint.Connection.SendAsduAsync(asdu, link.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// 发送一个已编码的 ASDU 体（不含 APCI 头）。供服务端冗余组排空循环投递队列快照，
    /// 与 <see cref="SendAsync"/> 共享 k 窗口背压与序列号语义。
    /// </summary>
    internal async ValueTask SendEncodedAsync(ReadOnlyMemory<byte> asduBody, CancellationToken cancellationToken)
    {
        using var link = _endpoint.LinkScoped(cancellationToken);
        await _endpoint.Connection.SendEncodedAsduAsync(asduBody, link.Token).ConfigureAwait(false);
    }

    /// <summary>对端（主站）IP 地址，用于冗余组按 IP 归属；无法解析时为 null。</summary>
    internal System.Net.IPAddress ClientIpAddress
    {
        get
        {
            try
            {
                return (RemoteEndPoint as System.Net.IPEndPoint)?.Address;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>标记本次关闭由服务端主动 StopAsync 发起，避免重复派发 ConnectionClosed。</summary>
    internal void MarkIntentionalClose() => _endpoint?.MarkIntentionalClose();

    #region 生命周期

    /// <summary>
    /// 连接建立时装载 IEC104 数据处理适配器（定长头三态解析 + 滑动重同步，与客户端共用 <see cref="Iec104Adapter"/>）。
    /// 端点在 OnTcpConnected 才创建，故捕获开关用 lambda 延迟解析。
    /// </summary>
    protected override Task OnTcpConnecting(ConnectingEventArgs e)
    {
        SetAdapter(new Iec104Adapter(() => _endpoint is { } ep && ep.HasRawFrameSubscribers));
        return base.OnTcpConnecting(e);
    }

    protected override Task OnTcpConnected(ConnectedEventArgs e)
    {
        _server = (Iec104Server)Service;
        _endpoint = new ApduEndpoint(_server.ApciParameters, _server.Parameters, this);
        _endpoint.Logger = Logger;
        _endpoint.Rebuild(default, conn =>
        {
            conn.AsduReceived += _server.RaiseAsduReceived(this);
            conn.EventHandler += ev => _server.RaiseConnectionEvent(this, ev);
        }, () => CloseAsync("timeout"));

        _server.RegisterSession(this);
        return base.OnTcpConnected(e);
    }

    /// <inheritdoc/>
    protected override async Task OnTcpReceived(ReceivedDataEventArgs e)
    {
        if (e.RequestInfo is IecApdu apdu && _endpoint != null && _endpoint.Connection != null)
        {
            if (!await _endpoint.HandleReceivedAsync(apdu).ConfigureAwait(false))
            {
                await CloseAsync("protocol error").ConfigureAwait(false);
                return;
            }
        }

        await base.OnTcpReceived(e).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    protected override async Task OnTcpClosed(ClosedEventArgs e)
    {
        _endpoint?.CancelLife();
        _server?.UnregisterSession(this);

        // 仅在非主动关闭（客户端断开 / 超时 / 协议错误）时派发 ConnectionClosed，
        // 让服务端 ConnectionEvent 订阅者能感知“哪个会话”断开了。服务端主动 StopAsync
        // 已通过 MarkIntentionalClose 标记，主动停止不会刷屏。
        _endpoint?.NotifyClosedIfUnexpected();
        _endpoint?.FinalizeSession();
        await base.OnTcpClosed(e).ConfigureAwait(false);
    }

    /// <summary>
    /// 服务端 Dispose 时的同步兜底清理。正常路径下 OnTcpClosed 已完成清理（幂等）；
    /// 此处覆盖“服务端直接 Dispose、异步断开回调未来得及执行”的竞态，确保会话的
    /// <see cref="ApduConnection"/> 与 CTS 不泄漏。
    /// </summary>
    internal void ForceCleanup()
    {
        MarkIntentionalClose();
        _endpoint?.FinalizeSession();
    }

    #endregion 生命周期
}
