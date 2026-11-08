//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS103;

/// <summary>
/// 收到未识别的 ASDU 且 <see cref="Iec103UnknownTypePolicy"/> 为
/// <see cref="Iec103UnknownTypePolicy.Throw"/> 时抛出。
/// </summary>
/// <remarks>
/// 仅用于开发期严格校验（例如联调新厂商私有规约时确认覆盖完整），
/// 生产环境请用 <see cref="Iec103UnknownTypePolicy.Log"/> 或
/// <see cref="Iec103UnknownTypePolicy.Ignore"/>，避免因个别未知帧中断整条链路。
/// </remarks>
public sealed class Iec103UnknownAsduException : Exception
{
    /// <summary>用未识别 ASDU 载体构造异常。</summary>
    /// <param name="asdu">未识别的 ASDU 信息。</param>
    public Iec103UnknownAsduException(Iec103UnknownAsdu asdu)
        : base(asdu?.ToString() ?? "未识别的 ASDU")
    {
        Asdu = asdu;
    }

    /// <summary>未识别的 ASDU 信息。</summary>
    public Iec103UnknownAsdu? Asdu { get; }
}

/// <summary>
/// 链路事务已被结束（<see cref="LinkTransaction103.Complete"/>）而等待方仍在读取。
/// </summary>
/// <remarks>
/// 内部使用。与"帧超时"必须区分：超时要求上层重发，事务结束则要求上层停止。
/// 原先两者都用 <see cref="OperationCanceledException"/> 表达，导致事务结束被误判为
/// 超时并多驱动一次重发。
/// </remarks>
internal sealed class Iec103TransactionAbortedException : Exception
{
    public Iec103TransactionAbortedException()
        : base("链路事务已结束，不再等待应答")
    {
    }
}

/// <summary>
/// FT1.2 帧被舍弃（校验和错、长度不一致、半帧超时等）时的事件载荷。
/// </summary>
public sealed class Iec103FrameRejectedEventArgs
{
    /// <summary>构造坏帧事件载荷。</summary>
    /// <param name="rawHex">原始字节十六进制。</param>
    /// <param name="reason">舍弃原因。</param>
    /// <param name="session">来源会话（主站侧为 null）。</param>
    public Iec103FrameRejectedEventArgs(string rawHex, string reason, IIec103Session? session = null)
    {
        RawHex = rawHex ?? string.Empty;
        Reason = reason ?? string.Empty;
        Session = session;
        TimestampUtc = DateTime.UtcNow;
    }

    /// <summary>原始字节十六进制。</summary>
    public string RawHex { get; }

    /// <summary>舍弃原因。</summary>
    public string Reason { get; }

    /// <summary>来源会话。</summary>
    public IIec103Session? Session { get; }

    /// <summary>发生时刻（UTC）。</summary>
    public DateTime TimestampUtc { get; }

    /// <inheritdoc />
    public override string ToString() => $"{TimestampUtc:HH:mm:ss.fff} 帧舍弃: {Reason} RAW={RawHex}";
}
