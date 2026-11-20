//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS103;

/// <summary>
/// 从字节流切出的一个帧：原始线路字节 + 其中的 ASDU。
/// </summary>
/// <remarks>
/// <see cref="Raw"/> 用于 <c>RawFrameReceived</c> 等诊断事件；
/// <see cref="Asdu"/> 为帧内承载的应用数据（不含任何帧头）。
/// 不承载 ASDU 的帧（如链路确认）由编解码器内部消化，不会出现在返回值里。
/// </remarks>
/// <param name="Raw">完整帧的原始线路字节。</param>
/// <param name="Asdu">帧内的 ASDU 字节。</param>
public readonly record struct Iec103IncomingFrame(byte[] Raw, byte[] Asdu);

/// <summary>
/// 帧层编解码器：字节流 ↔ ASDU。厂商私有承载的扩展点。
/// </summary>
/// <remarks>
/// <para>
/// 标准 103（DL/T 667-1999）只定义 FT1.2 帧，库内置实现已覆盖，<b>不需要</b>实现本接口。
/// 它用于厂商在网口上自定义的承载（例如把 103 的 ASDU 装进 104 的 APCI），
/// 这类承载不属于任何标准，实现放在使用方（参考 <c>examples/103/cs103-private-ioa/Xuji</c>）。
/// </para>
/// <para>
/// 本接口只覆盖「承载即收发 ASDU」的形态。FT1.2 的链路服务（FCB 翻转、1/2 级队列、
/// ACD/DFC、固定帧应答）不在其内；接了自定义编解码器后，<c>ResetLinkAsync</c> 等
/// 服务会抛 <see cref="NotSupportedException"/>。
/// </para>
/// </remarks>
public interface IIec103FramingCodec
{
    /// <summary>承载名称（诊断用，如 "VendorApdu"）。</summary>
    string Name { get; }

    /// <summary>该承载的 ASDU 头部布局。</summary>
    Iec103AsduLayout Layout { get; }

    /// <summary>
    /// 从字节流切帧，返回本批解出的帧（可能 0 到多个）。
    /// </summary>
    /// <param name="bytes">本次从传输层读到的字节（不保证帧边界）。</param>
    /// <returns>切出的帧；不含 ASDU 的帧不应返回。</returns>
    IReadOnlyList<Iec103IncomingFrame> Feed(ReadOnlySpan<byte> bytes);

    /// <summary>把一个 ASDU 编码成待发送的完整帧字节。</summary>
    /// <param name="asdu">完整 ASDU 字节（头部按 <see cref="Layout"/> 布局）。</param>
    byte[] Encode(ReadOnlySpan<byte> asdu);

    /// <summary>
    /// 编解码器在切帧过程中产生、需立刻回送的原始帧（如对 STARTDT_ACT 回 STARTDT_CON）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 部分承载在报文之外还有链路控制交互。主机侧把它接到传输层写回即可：
    /// </para>
    /// <code>
    /// codec.ControlFrameResponse += raw => transport.WriteAsync(raw, ct);
    /// </code>
    /// <para>
    /// 标准 FT1.2 由库内置实现，不使用本事件。
    /// </para>
    /// </remarks>
    event Action<byte[]>? ControlFrameResponse;

    /// <summary>复位分帧状态机（重连时调用，不产生坏帧事件）。</summary>
    void Reset();

    /// <summary>半帧超时复位；确实复位了返回 true。</summary>
    /// <param name="timeoutMilliseconds">帧内字符间隔超时。</param>
    bool TryResetStale(int timeoutMilliseconds);

    /// <summary>坏帧被舍弃时触发：(原始十六进制, 原因)。</summary>
    event Action<string, string>? FrameRejected;
}