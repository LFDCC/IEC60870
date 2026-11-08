//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS103;

/// <summary>
/// 103 从站会话契约（一个主站连接对应一个会话）。
/// </summary>
/// <remarks>
/// 103 现场常见多个主站（后台、保护信息子站、便携式试验仪）同时连同一装置，
/// 因此从站必须支持<b>多会话并发</b>。
/// </remarks>
public interface IIec103Session
{
    /// <summary>会话唯一标识（连接建立时分配）。</summary>
    long SessionId { get; }

    /// <summary>对端地址（<c>IP:Port</c>）。</summary>
    string RemoteEndPoint { get; }

    /// <summary>链路地址（主站复位/召唤时携带，由首帧确定）。</summary>
    byte LinkAddress { get; }

    /// <summary>链路是否已建立（收到 RESET CU 或有效请求帧后为 true）。</summary>
    bool LinkEstablished { get; }

    /// <summary>最近一次链路活动时刻（UTC）。</summary>
    DateTime LastActivityUtc { get; }

    /// <summary>该会话的原始帧接收事件（诊断用，占位）。</summary>
    event Action<byte[]>? RawFrameReceived;

    /// <summary>主动向本会话上送 ASDU（1 级队列优先）。</summary>
    /// <param name="asdu">待上送的应用服务数据单元。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask SendAsduAsync(Asdu asdu, CancellationToken ct = default);

    /// <summary>主动向本会话上送原始 ASDU 字节（厂商私有类型直接构造字节时使用）。</summary>
    /// <param name="asduBytes">完整 ASDU 字节（不含 FT1.2 帧头）。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask SendRawAsduAsync(ReadOnlyMemory<byte> asduBytes, CancellationToken ct = default);
}

/// <summary>
/// 103 主站契约（Client）。
/// </summary>
/// <remarks>
/// 所有 I/O 均为 <c>XxxAsync(..., CancellationToken)</c>；严禁在实现内使用
/// <c>.Result</c>/<c>.Wait()</c>/<c>Thread.Sleep</c>（103 原实现的 sync-over-async 必须消除）。
/// </remarks>
public interface IIec103Master : IAsyncDisposable
{
    /// <summary>协议标识（<see cref="Iec103Protocol.Iec103"/>）。</summary>
    string ProtocolName { get; }

    /// <summary>私有类型注册表（实例级，不同装置可挂不同表）。</summary>
    Iec103PrivateTypeRegistry PrivateTypes { get; }

    /// <summary>连接是否已建立。</summary>
    bool IsConnected { get; }

    /// <summary>建立 TCP 连接（含 FT1.2 链路复位前状态）。</summary>
    Task ConnectAsync(CancellationToken ct = default);

    /// <summary>断开连接。</summary>
    Task DisconnectAsync(CancellationToken ct = default);

    /// <summary>复位通信单元（RESET CU，FC=0）。</summary>
    Task ResetLinkAsync(byte ca, CancellationToken ct = default);

    /// <summary>复位帧计数位（RESET FCB，FC=7）。</summary>
    Task ResetFcbAsync(byte ca, CancellationToken ct = default);

    /// <summary>请求链路状态（FC=9）。</summary>
    Task<Ft12Frame> RequestLinkStatusAsync(byte ca, CancellationToken ct = default);

    /// <summary>时间同步（TI=6，COT=8）。</summary>
    Task SendClockSyncAsync(byte ca, DateTime time, CancellationToken ct = default);

    /// <summary>总召唤（TI=7）并取回全部数据，直到 TI=8 终止帧。</summary>
    Task<IReadOnlyList<Asdu>> GeneralInterrogationAsync(byte ca, CancellationToken ct = default);

    /// <summary>召唤 1 级用户数据并按 <paramref name="stop"/> 条件排空。</summary>
    Task<IReadOnlyList<Asdu>> RequestClass1Async(byte ca, Func<Asdu, bool>? stop = null, CancellationToken ct = default);

    /// <summary>召唤 2 级用户数据（测量值）。</summary>
    Task<IReadOnlyList<Asdu>> RequestClass2Async(byte ca, CancellationToken ct = default);

    /// <summary>读取通用分类全目录。</summary>
    Task<IReadOnlyList<GenericDataSet>> ReadGenericProfileAsync(byte ca, CancellationToken ct = default);

    /// <summary>读取指定通用分类条目。</summary>
    Task<IReadOnlyList<GenericDataSet>> ReadGenericAsync(byte ca, params (Gin Gin, byte Kod)[] entries);

    /// <summary>写通用分类值（TI=10，COT=40）。</summary>
    Task<IReadOnlyList<GenericDataSet>> WriteGenericAsync(byte ca, IReadOnlyList<GenericDataSet> sets, CancellationToken ct = default);

    /// <summary>发送一般命令（TI=20）。</summary>
    Task SendCommandAsync(byte ca, byte fun, byte inf, int dco, byte rii = 0, CancellationToken ct = default);

    /// <summary>发送厂商私有 ASDU（走已注册的私有处理器编码）。</summary>
    Task SendPrivateAsync(byte ca, byte fun, byte inf, Iec103DecodedAsdu value, CancellationToken ct = default);

    /// <summary>
    /// 收到的 ASDU（零拷贝，回调期有效）。标准报文与厂商私有报文都走这一个入口：
    /// 标准类型用 <c>Iec103AsduDecoder.Decode(in asdu)</c> 取强类型，
    /// 私有类型由业务代码按 <c>asdu.TypeId</c> 自行解析。
    /// </summary>
    event Iec103AsduViewHandler? AsduReceived;

    /// <summary>未识别 ASDU 事件（兜底，绝不丢帧）。</summary>
    event Action<Iec103UnknownAsdu>? UnknownAsduReceived;

    /// <summary>链路事件（连接/断开/链路状态变化）。</summary>
    event Action<Iec103LinkEvent>? Iec103LinkEvent;

    /// <summary>原始帧接收（<see cref="byte"/> 为新拷贝，可长期持有）。</summary>
    event Action<byte[]>? RawFrameReceived;

    /// <summary>原始帧发送。</summary>
    event Action<byte[]>? RawFrameSent;
}

/// <summary>
/// 103 从站契约（Server）。
/// </summary>
public interface IIec103Slave : IAsyncDisposable
{
    /// <summary>协议标识（<see cref="Iec103Protocol.Iec103"/> 或 <see cref="Iec103Protocol.Iec103OverTcp"/>）。</summary>
    string ProtocolName { get; }

    /// <summary>私有类型注册表。</summary>
    Iec103PrivateTypeRegistry PrivateTypes { get; }

    /// <summary>监听端口。</summary>
    int Port { get; }

    /// <summary>当前在线会话快照。</summary>
    IReadOnlyList<IIec103Session> Sessions { get; }

    /// <summary>启动监听（多主站会话）。</summary>
    Task StartAsync(int port, CancellationToken ct = default);

    /// <summary>停止监听并断开全部会话。</summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>向所有已建链会话上送 ASDU。</summary>
    Task BroadcastAsync(Asdu asdu, CancellationToken ct = default);

    /// <summary>向指定会话上送 ASDU。</summary>
    Task SendToAsync(IIec103Session session, Asdu asdu, CancellationToken ct = default);

    /// <summary>收到主站 ASDU（零拷贝，回调期有效）。</summary>
    event Iec103ServerAsduHandler? AsduReceived;

    /// <summary>会话建立。</summary>
    event Action<IIec103Session>? SessionOpened;

    /// <summary>会话关闭。</summary>
    event Action<IIec103Session, string?>? SessionClosed;

    /// <summary>原始帧接收（诊断）。</summary>
    event Action<IIec103Session, byte[]>? RawFrameReceived;
}

/// <summary>
/// 未识别 ASDU 的兜底载体：保留完整原始字节与已解出的头部，绝不丢帧。
/// </summary>
/// <remarks>
/// 这是现场逆向厂商私有规约的主通道：即使注册表里一个私有类型都没登记，
/// 也能拿到 <see cref="Raw"/> 做离线分析。
/// </remarks>
public sealed class Iec103UnknownAsdu
{
    /// <summary>构造未识别 ASDU 载体。</summary>
    /// <param name="raw">完整 ASDU 字节（含 6 字节头）。</param>
    /// <param name="session">来源会话；主站侧为 null。</param>
    /// <param name="reason">未识别原因（诊断用）。</param>
    public Iec103UnknownAsdu(byte[] raw, IIec103Session? session = null, string? reason = null)
    {
        Raw = raw ?? throw new ArgumentNullException(nameof(raw));
        Session = session;
        Reason = reason;

        if (raw.Length >= Asdu.HeaderLength)
        {
            TypeId = raw[0];
            Sq = (raw[1] & 0x80) != 0;
            Count = raw[1] & 0x7F;
            CotByte = raw[2];
            Ca = raw[3];
            Fun = raw[4];
            Inf = raw[5];
            HasHeader = true;
        }
    }

    /// <summary>完整 ASDU 字节（含 6 字节头）。</summary>
    public byte[] Raw { get; }

    /// <summary>来源会话（主站侧为 null）。</summary>
    public IIec103Session? Session { get; }

    /// <summary>未识别原因。</summary>
    public string? Reason { get; }

    /// <summary>头部是否成功解析（长度 ≥ 6）。</summary>
    public bool HasHeader { get; }

    /// <summary>类型标识。</summary>
    public byte TypeId { get; }

    /// <summary>序列标志。</summary>
    public bool Sq { get; }

    /// <summary>信息对象数目。</summary>
    public int Count { get; }

    /// <summary>COT 原始字节（含 P/N）。</summary>
    public byte CotByte { get; }

    /// <summary>传送原因（去 P/N）。</summary>
    public byte Cot => (byte)(CotByte & CotCodes.ValueMask);

    /// <summary>公共地址。</summary>
    public byte Ca { get; }

    /// <summary>功能类型。</summary>
    public byte Fun { get; }

    /// <summary>信息序号。</summary>
    public byte Inf { get; }

    /// <summary>诊断描述（含十六进制原始报文，便于抓包比对）。</summary>
    public override string ToString()
    {
        var head = HasHeader
            ? $"TI={TypeId} COT={Cot}({CotCodes.Describe(CotByte)}) CA={Ca} FUN={Fun} INF={Inf}"
            : "头部不完整";
        var reason = string.IsNullOrEmpty(Reason) ? string.Empty : $" 原因={Reason}";
        return $"[未识别 ASDU] {head}{reason} RAW={Convert.ToHexString(Raw)}";
    }
}

/// <summary>链路事件类型。</summary>
public enum Iec103LinkEventKind
{
    /// <summary>TCP 已连接。</summary>
    Connected = 0,

    /// <summary>TCP 已断开。</summary>
    Disconnected = 1,

    /// <summary>链路已复位（RESET CU 完成）。</summary>
    LinkReset = 2,

    /// <summary>请求链路状态应答。</summary>
    LinkStatus = 3,

    /// <summary>帧被舍弃（校验错/半帧超时等）。</summary>
    FrameRejected = 6,
}

/// <summary>链路事件载荷。</summary>
public sealed class Iec103LinkEvent
{
    /// <summary>构造链路事件。</summary>
    /// <param name="kind">事件类型。</param>
    /// <param name="message">附加说明。</param>
    /// <param name="session">关联会话（主站侧为 null）。</param>
    public Iec103LinkEvent(Iec103LinkEventKind kind, string? message = null, IIec103Session? session = null)
    {
        Kind = kind;
        Message = message;
        Session = session;
        TimestampUtc = DateTime.UtcNow;
    }

    /// <summary>事件类型。</summary>
    public Iec103LinkEventKind Kind { get; }

    /// <summary>附加说明。</summary>
    public string? Message { get; }

    /// <summary>关联会话。</summary>
    public IIec103Session? Session { get; }

    /// <summary>发生时刻（UTC）。</summary>
    public DateTime TimestampUtc { get; }

    /// <inheritdoc />
    public override string ToString() => $"{TimestampUtc:HH:mm:ss.fff} {Kind}"
        + (string.IsNullOrEmpty(Message) ? string.Empty : $" {Message}");
}
