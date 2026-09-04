//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS104;

/// <summary>
/// IEC 60870-5-104 APDU 解析产物（<see cref="Iec104Adapter"/> 的 TRequest，
/// 实现 <see cref="IFixedHeaderRequestInfo"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 由 <see cref="CustomFixedHeaderDataHandlingAdapter{T}"/> 复用实例驱动：
/// <c>OnParsingHeader</c> 收到 2 字节定长头（0x68 + 长度）即判定帧头合法性并给出
/// BodyLength（= 帧总长 − 2，即 4 字节 APCI + ASDU）；<c>OnParsingBody</c> 收到
/// 负载后解析 APCI 控制域并按需保留数据。
/// </para>
/// <para>
/// 免拷贝策略（消除旧版每帧 <c>ToArray</c> 整帧拷贝）：
/// <list type="bullet">
/// <item><b>S/U 控制帧</b>（6 字节、无 ASDU）：零拷贝零分配——控制字段已在
/// <c>OnParsingBody</c> 解析，负载只读不存；</item>
/// <item><b>I 帧</b>：拷贝 ASDU 负载切片（2..len−1），<see cref="AsduSpan"/> 叠加在
/// 该自有数组上，供接收回调（同步消费）期间零拷贝视图使用；</item>
/// <item><b>完整帧</b>（供 <see cref="RawFrameReceived"/> 订阅者长期持有）：仅当
/// 订阅者存在（<see cref="CaptureRawFrames"/>）时才整帧拷贝。</item>
/// </list>
/// </para>
/// </remarks>
internal sealed class IecApdu : IFixedHeaderRequestInfo
{
    /// <summary>是否捕获完整帧（宿主在存在原始帧订阅者时置位；置位后每帧拷整帧）。</summary>
    internal bool CaptureRawFrames;

    /// <summary>完整帧字节（0x68 + APCI + ASDU）；仅需时按帧拷贝，否则为 null。</summary>
    internal byte[] RawFrame;

    /// <summary>I 帧的 ASDU 负载（拷贝自接收缓冲，自有数组）；S/U 帧为 null。</summary>
    private byte[] _asdu;

    /// <summary>帧总字节数（含起始字节与 APCI，即 0x68 头之后长度 + 2）。</summary>
    private int _length;

    /// <summary>是否已解析 APCI 控制域。</summary>
    private bool _controlParsed;

    /// <summary>帧类型（I/S/U）。</summary>
    internal ApduKind Kind;

    /// <summary>APCI 控制域固定长度（4 字节：不含起始字节 0x68 和长度字节）。</summary>
    private const int ControlFieldLength = 4;

    /// <summary>发送序列号 N(S)（仅 I 帧，其余为 -1）。</summary>
    internal int SendSeq;

    /// <summary>接收序列号 N(R)（I/S 帧，其余为 -1）。</summary>
    internal int RecvSeq;

    /// <summary>U 帧功能码（仅 U 帧）。</summary>
    internal byte UFunction;

    /// <inheritdoc/>
    public int BodyLength { get; private set; }

    /// <summary>
    /// 解析 2 字节定长头（0x68 + 长度）。返回 false 表示非合法帧头，
    /// 框架将 Advance(1) 滑动重同步（GoOn）。
    /// </summary>
    public bool OnParsingHeader(ReadOnlySpan<byte> header)
    {
        // 帧头契约：0x68 + 长度（长度 = 总帧长 − 2，含 APCI 与 ASDU，不含起始字节）
        if (header[0] != ApduCodec.StartByte)
        {
            return false;
        }

        int len = header[1];
        var total = len + 2;

        // 非法长度（短于 APCI / 超过 APDU 上限）：滑动重同步
        if (total < ApduCodec.ApciLength || total > ApduCodec.MaxApduLength)
        {
            return false;
        }

        _length = total;
        BodyLength = total - 2; // 负载 = 4 字节 APCI + ASDU

        _controlParsed = false;
        return true;
    }

    /// <summary>
    /// 解析负载（4 字节 APCI + ASDU）。先解析控制域落定 N(S)/N(R)/U 功能码，
    /// 再按免拷贝策略决定是否保留数据。返回 true 表示本帧有效。
    /// </summary>
    public bool OnParsingBody(ReadOnlySpan<byte> body)
    {
        // 解析 APCI 控制域（负载前 4 字节；body 仅含 4 字节控制域 + ASDU，
        // 起始字节 0x68 和长度字节已被适配器头解析消耗）
        if (body.Length >= ControlFieldLength)
        {
            ParseControl(body.Slice(0, ControlFieldLength));
        }

        _controlParsed = true;

        // I 帧：拷贝 ASDU 负载（供 AsduView 零拷贝视图；AsduView 需在回调期间存活）
        // body 布局：4 字节控制域 + ASDU 数据
        if (Kind == ApduKind.Information && body.Length > ControlFieldLength)
        {
            _asdu = new byte[body.Length - ControlFieldLength];
            body.Slice(ControlFieldLength).CopyTo(_asdu);
        }

        // 原始帧订阅者：整帧拷贝（供长期持有/入队/落盘）
        if (CaptureRawFrames)
        {
            var raw = new byte[_length];
            raw[0] = ApduCodec.StartByte;
            raw[1] = (byte)(_length - 2);
            body.CopyTo(raw.AsSpan(2));
            RawFrame = raw;
        }

        return true;
    }

    /// <summary>解析 APCI 控制域到 <see cref="Kind"/>/<see cref="SendSeq"/>/<see cref="RecvSeq"/>/<see cref="UFunction"/>。</summary>
    private void ParseControl(ReadOnlySpan<byte> control)
    {
        var ctrl1 = control[0];

        // 帧型判定（IEC 60870-5-104 §5.1，与 ApduCodec.TryParseApdu 同规则）：
        //   I 帧：bit0 == 0；S 帧：低2位 == 01；U 帧：低2位 == 11
        if ((ctrl1 & 0x01) == 0)
        {
            Kind = ApduKind.Information;
            SendSeq = (ctrl1 >> 1) | (control[1] << 7);
            RecvSeq = (control[2] >> 1) | (control[3] << 7);
        }
        else if ((ctrl1 & 0x03) == 0x01)
        {
            Kind = ApduKind.Supervisory;
            SendSeq = -1;
            RecvSeq = (control[2] >> 1) | (control[3] << 7);
        }
        else
        {
            Kind = ApduKind.Unnumbered;
            SendSeq = -1;
            RecvSeq = -1;
            UFunction = ctrl1;
        }
    }

    /// <summary>ASDU 负载切片（仅 I 帧有效；叠加在自有数组上，接收回调期间有效）。</summary>
    internal ReadOnlySpan<byte> AsduSpan => _asdu;

    /// <summary>原始帧字节（无订阅者时为 null）。</summary>
    internal byte[] RawFrameBytes => RawFrame;
}
