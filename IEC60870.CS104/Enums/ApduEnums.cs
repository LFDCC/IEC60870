//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS104;

/// <summary>
/// IEC 60870-5-104 APDU 帧类型（由控制域1的低2位决定）。
/// </summary>
public enum ApduKind : byte
{
    /// <summary>I 格式——信息传输帧，bit0 = 0。</summary>
    Information = 0,

    /// <summary>S 格式——监视帧，低2位 = 01。</summary>
    Supervisory = 1,

    /// <summary>U 格式——未编号控制帧，低2位 = 11。</summary>
    Unnumbered = 3
}

/// <summary>连接层状态/事件。</summary>
public enum ApduConnectionEvent
{
    /// <summary>收到 STARTDT_CON（客户端已收到激活确认）。</summary>
    StartDtConReceived,
    /// <summary>收到 STOPDT_CON（客户端已收到停止确认）。</summary>
    StopDtConReceived,
    /// <summary>收到 STARTDT_ACT（服务端侧：对端请求激活）。</summary>
    StartDtActReceived,
    /// <summary>收到 STOPDT_ACT（服务端侧：对端请求停止）。</summary>
    StopDtActReceived,
    /// <summary>数据传输已激活。</summary>
    Activated,
    /// <summary>数据传输已停止。</summary>
    Deactivated,
    /// <summary>
    /// 连接已断开（非主动）。涵盖：远端关闭（FIN/RST）、链路超时（T1/T3）、协议帧错误等。
    /// 主动 <see cref="Iec104Client.DisconnectAsync"/> / <see cref="Iec104Server.StopAsync"/> 不会触发本事件；
    /// 订阅方应将其视为“链路丢失，客户端需要重连”的信号。具体断开原因可通过
    /// <see cref="ApduConnection.CloseReason"/> 查询。
    /// </summary>
    ConnectionClosed
}

/// <summary>
/// 底层连接断开的原因，配合 <see cref="ApduConnectionEvent.ConnectionClosed"/> 使用。
/// 用于在重连策略中区分“远端优雅关闭 / 链路超时 / 协议错误”，便于日志与差异化处理。
/// </summary>
public enum ConnectionCloseReason
{
    /// <summary>未知或默认（例如底层 socket 关闭回调未携带更具体原因）。</summary>
    Unknown,
    /// <summary>远端关闭连接（FIN/RST）或 EOF。</summary>
    RemoteClosed,
    /// <summary>链路层超时（T1/T3）被判定为断开。</summary>
    Timeout,
    /// <summary>收到无法解析的协议帧（长度/序列号等严重错误）。</summary>
    ProtocolError,
    /// <summary>底层 IO 异常（如写失败）。</summary>
    IoError
}
