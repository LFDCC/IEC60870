//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS104;

/// <summary>
/// IEC 60870-5-104 主站（客户端）契约。仅保留最小核心成员，
/// 标准命令动词（总召唤、读命令、时钟同步……）以 <see cref="Iec104MasterExtension"/> 扩展方法提供。
/// </summary>
public interface IIec104Master : IAsyncDisposable
{
    /// <summary>应用层参数（COT/CA/IOA 宽度等）。</summary>
    ApplicationLayerParameters Parameters { get; }

    /// <summary>ASDU 类型处理器注册表（实例级可替换，私有类型场景注入自定义处理器）。</summary>
    AsduTypeHandlerRegistry TypeHandlers { get; set; }

    /// <summary>数据传输是否已激活（收到 STARTDT_CON 后）。</summary>
    bool IsActivated { get; }

    /// <summary>最近一次底层连接断开的原因；未连接或对象已释放时为 null。</summary>
    ConnectionCloseReason? LastCloseReason { get; }

    /// <summary><see cref="Iec104MasterExtension.SendControlCommandAndWaitAsync"/> 等待 ACT-CON 的超时时长。</summary>
    TimeSpan CommandConfirmationTimeout { get; set; }

    /// <summary>收到 ASDU 的同步零拷贝事件（支持多订阅者）。</summary>
    event AsduViewHandler AsduReceived;

    /// <summary>上行原始报文（对端→本端），参数为完整单个 APDU（含 0x68 APCI 头）。</summary>
    event Action<byte[]> RawFrameReceived;

    /// <summary>下行原始报文（本端→对端），覆盖 I/S/U 三类帧。</summary>
    event Action<byte[]> RawFrameSent;

    /// <summary>连接层事件（STARTDT_CON 等，支持多订阅者）。</summary>
    event Action<ApduConnectionEvent> ConnectionEvent;

    /// <summary>建立 TCP 连接。</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default, Action<TouchSocketConfig> configureConfig = null);

    /// <summary>发送 STARTDT_ACT 并等待 STARTDT_CON，激活数据传输。</summary>
    Task StartDataTransferAsync(CancellationToken cancellationToken = default);

    /// <summary>发送 STOPDT_ACT 并等待 STOPDT_CON。</summary>
    Task StopDataTransferAsync(CancellationToken cancellationToken = default);

    /// <summary>主动断开连接。</summary>
    Task DisconnectAsync();

    /// <summary>发送一个 ASDU（I 帧）。k 窗口满时异步背压等待，不阻塞线程。</summary>
    Task SendAsync(ASDU asdu, CancellationToken cancellationToken = default);

    /// <summary>发送一条控制命令 ASDU，并异步等待其 ACTIVATION_CON（或否定确认）。</summary>
    Task<ControlConfirmation> SendControlCommandAndWaitAsync(
        CauseOfTransmission cot, int ca, InformationObject io,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// IEC 60870-5-104 从站（服务端）契约。
/// </summary>
public interface IIec104Slave
{
    /// <summary>APCI 参数（k/w/T1/T2/T3）。</summary>
    APCIParameters ApciParameters { get; }

    /// <summary>应用层参数。</summary>
    ApplicationLayerParameters Parameters { get; }

    /// <summary>ASDU 类型处理器注册表（会话接收时戳记）。</summary>
    AsduTypeHandlerRegistry TypeHandlers { get; set; }

    /// <summary>当前活动会话数。</summary>
    int SessionCount { get; }

    /// <summary>服务端事件分发模式（冗余组支持）。</summary>
    ServerMode Mode { get; }

    /// <summary>收到 ASDU 的零拷贝事件（带来源会话，支持多订阅者）。</summary>
    event ServerAsduHandler AsduReceived;

    /// <summary>连接层事件（带来源会话，支持多订阅者）。</summary>
    event Action<Iec104Session, ApduConnectionEvent> ConnectionEvent;

    /// <summary>在指定端口启动监听。</summary>
    Task StartAsync(int port, Action<TouchSocketConfig> configureConfig = null);

    /// <summary>向所有已激活会话广播一个 ASDU（直接发送，不缓冲）。</summary>
    Task BroadcastAsync(ASDU asdu, CancellationToken cancellationToken = default);

    /// <summary>把一个自发 ASDU 编码入队，由冗余组排空循环按序发送（缓冲、不阻塞、切换不丢）。</summary>
    Task EnqueueAsync(ASDU asdu, CancellationToken cancellationToken = default);

    /// <summary>返回事件队列中的待发送 ASDU 数。</summary>
    int GetNumberOfQueueEntries(RedundancyGroup redundancyGroup = null);

    /// <summary>停止监听并关闭所有会话。</summary>
    Task StopAsync();
}

/// <summary>
/// IEC 60870-5-104 服务端每连接会话契约。
/// </summary>
public interface IIec104Session
{
    /// <summary>该会话对应的连接层状态机。</summary>
    ApduConnection Connection { get; }

    /// <summary>数据传输是否已激活（收到 STARTDT_ACT 后）。</summary>
    bool IsActivated { get; }

    /// <summary>最近一次底层连接断开的原因。</summary>
    ConnectionCloseReason LastCloseReason { get; }

    /// <summary>上行原始报文（客户端→本会话）。</summary>
    event Action<byte[]> RawFrameReceived;

    /// <summary>下行原始报文（本会话→客户端）。</summary>
    event Action<byte[]> RawFrameSent;

    /// <summary>向该会话对端发送一个 ASDU（I 帧）。</summary>
    Task SendAsync(ASDU asdu, CancellationToken cancellationToken = default);
}
