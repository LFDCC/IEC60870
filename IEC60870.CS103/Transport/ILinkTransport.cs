//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS103;

/// <summary>
/// 103 链路传输抽象（FT1.2 字节流）。
/// </summary>
/// <remarks>
/// <para>
/// 设计要点：<b>传输层只负责搬运字节流，不感知 FT1.2 帧边界</b>。
/// 帧定界由 <see cref="Ft12FrameParser"/> 在<b>接收侧</b>完成，
/// 因此串口、TCP 客户端、TCP 服务端会话三种实现共用同一套分帧逻辑。
/// </para>
/// <para>
/// <b>为什么传输层不做帧定界</b>：
/// FT1.2 的帧头长度不固定（<c>0xE5</c> 单字符 / <c>0x10</c> 固定 5 字节 /
/// <c>0x68 L L 68</c> 可变），且 103 现场线路存在噪声字节，需要在字节流层面
/// 逐字节重同步。把定界放在传输层之上，可让三种载体共享同一份经过测试的状态机。
/// </para>
/// <para>
/// <b>超时契约</b>：流实现不依赖 <c>Stream.ReadTimeout</c>（TCP 字节流无此语义，
/// 103 原实现据此轮询取消导致断线后半帧卡死）。所有等待都必须由
/// <see cref="CancellationToken"/> 驱动；帧内字符超时由调用方用
/// <see cref="Ft12FrameParser.TryResetStale"/> 判定。
/// </para>
/// </remarks>
public interface IIec103LinkTransport : IAsyncDisposable
{
    /// <summary>该传输是否已连接/打开。</summary>
    bool IsOpen { get; }

    /// <summary>对端描述（<c>IP:Port</c> 或串口名），用于日志与诊断。</summary>
    string EndPoint { get; }

    /// <summary>
    /// 读取一段字节（不保证帧边界）。返回 0 表示对端已关闭。
    /// </summary>
    /// <param name="buffer">目标缓冲。</param>
    /// <param name="ct">取消令牌；取消应尽快返回，不阻塞调用方。</param>
    /// <returns>实际读取字节数；0 表示连接关闭。</returns>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct);

    /// <summary>写入一段字节（一个或多个完整 FT1.2 帧）。</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

    /// <summary>传输关闭事件（对端断开、IO 错误、本地关闭）。</summary>
    /// <remarks>
    /// 命名为 <c>TransportClosed</c> 而非 <c>Closed</c>：实现类继承 TouchSocket 的
    /// <c>TcpClient</c>，后者已有 <c>Closed</c> 事件（<see cref="ClosedEventArgs"/> 签名），
    /// 同名会遮蔽基类成员，导致订阅方拿到错误的事件源。
    /// </remarks>
    event EventHandler<Iec103TransportClosedEventArgs>? TransportClosed;
}

/// <summary>传输关闭事件参数。</summary>
public sealed class Iec103TransportClosedEventArgs : EventArgs
{
    /// <summary>构造关闭事件参数。</summary>
    /// <param name="endPoint">对端描述。</param>
    /// <param name="reason">关闭原因；正常本地关闭为 null。</param>
    /// <param name="exception">导致关闭的异常（若有）。</param>
    public Iec103TransportClosedEventArgs(string endPoint, string? reason = null, Exception? exception = null)
    {
        EndPoint = endPoint;
        Reason = reason;
        Exception = exception;
    }

    /// <summary>对端描述。</summary>
    public string EndPoint { get; }

    /// <summary>关闭原因。</summary>
    public string? Reason { get; }

    /// <summary>导致关闭的异常。</summary>
    public Exception? Exception { get; }
}
