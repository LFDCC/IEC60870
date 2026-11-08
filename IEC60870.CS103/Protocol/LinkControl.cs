//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//  协议实现依据：DL/T 667-1999 (IEC 60870-5-103) 表 2.1 / 表 2.2
//------------------------------------------------------------------------------

namespace IEC60870.CS103;

/// <summary>主站（PRM=1）链路功能码。DL/T 667-1999 表 2.1。</summary>
public enum PrimaryFunction : byte
{
    /// <summary>复位通信单元（RESET CU）。FCV=0。</summary>
    ResetCu = 0,

    /// <summary>发送/确认 传送数据（SEND/CONFIRM 用户数据）。FCV=1。</summary>
    SendConfirmed = 3,

    /// <summary>发送/无回答 传送数据（广播）。FCV=0。</summary>
    SendNoReply = 4,

    /// <summary>复位帧计数位（RESET FCB）。FCV=0，用于链路重建。</summary>
    ResetFcb = 7,

    /// <summary>请求链路状态。FCV=0。</summary>
    RequestLinkStatus = 9,

    /// <summary>召唤 1 级用户数据（请求/响应）。FCV=1。</summary>
    RequestClass1 = 10,

    /// <summary>召唤 2 级用户数据（请求/响应）。FCV=1。</summary>
    RequestClass2 = 11,
}

/// <summary>从站（PRM=0）链路功能码。DL/T 667-1999 表 2.2。</summary>
public enum SecondaryFunction : byte
{
    /// <summary>确认帧（ACK）。</summary>
    Ack = 0,

    /// <summary>链路忙，未收到报文（NACK）。</summary>
    Nack = 1,

    /// <summary>以数据响应请求帧。</summary>
    RespondData = 8,

    /// <summary>无所召唤的数据。</summary>
    RespondNoData = 9,

    /// <summary>链路状态。</summary>
    LinkStatus = 11,
}

/// <summary>
/// 链路控制域。位布局（主从同构）：
/// <c>bit7=0</c>（103 无 DIR）、<c>bit6 PRM</c>（1=启动站/主站）、
/// <c>bit5 FCB|ACD</c>、<c>bit4 FCV|DFC</c>、<c>bit0-3</c> 功能码。
/// </summary>
/// <remarks>
/// 103 只用非平衡传输：主站（PRM=1）发起，从站（PRM=0）应答。
/// 主站对同一从站的新一轮 SEND/CONFIRM 或 REQUEST 服务需翻转 FCB。
/// <para>
/// 控制域 <c>bit7</c> 在 103 里<b>没有定义</b>：装置按标准不解析该位且恒发 0，
/// 故本类型不提供对应成员，也不应在编码时置位。
/// </para>
/// </remarks>
public readonly record struct LinkControl(bool IsPrimary, byte Function, bool FcbOrAcd, bool FcvOrDfc)
{
    /// <summary>PRM 位（bit6）。</summary>
    public const byte PrmMask = 0x40;

    /// <summary>FCB / ACD 位（bit5）。</summary>
    public const byte FcbAcdMask = 0x20;

    /// <summary>FCV / DFC 位（bit4）。</summary>
    public const byte FcvDfcMask = 0x10;

    /// <summary>功能码掩码（bit0-3）。</summary>
    public const byte FunctionMask = 0x0F;

    /// <summary>编码为控制域字节。</summary>
    public byte ToByte()
    {
        var value = (byte)(Function & FunctionMask);
        if (IsPrimary)
        {
            value |= PrmMask;
        }

        if (FcbOrAcd)
        {
            value |= FcbAcdMask;
        }

        if (FcvOrDfc)
        {
            value |= FcvDfcMask;
        }

        return value;
    }

    /// <summary>从控制域字节解码（bit7 忽略）。</summary>
    public static LinkControl FromByte(byte control) => new(
        IsPrimary: (control & PrmMask) != 0,
        Function: (byte)(control & FunctionMask),
        FcbOrAcd: (control & FcbAcdMask) != 0,
        FcvOrDfc: (control & FcvDfcMask) != 0);

    // —— 常用构造 ——

    /// <summary>构造主站控制域。</summary>
    public static LinkControl Primary(PrimaryFunction function, bool fcb, bool fcv)
        => new(true, (byte)function, fcb, fcv);

    /// <summary>复位通信单元（<c>0x40</c>）：FCB=0、FCV=0。</summary>
    public static LinkControl ResetCu() => Primary(PrimaryFunction.ResetCu, false, false);

    /// <summary>复位帧计数位（<c>0x47</c>）：FCB=0、FCV=0。</summary>
    public static LinkControl ResetFcb() => Primary(PrimaryFunction.ResetFcb, false, false);

    /// <summary>请求链路状态（<c>0x49</c>）：FCB=0、FCV=0。</summary>
    public static LinkControl RequestLinkStatus() => Primary(PrimaryFunction.RequestLinkStatus, false, false);

    /// <summary>发送/确认用户数据（<c>0x53</c>/<c>0x73</c> 随 FCB 翻转）。</summary>
    public static LinkControl SendConfirmed(bool fcb) => Primary(PrimaryFunction.SendConfirmed, fcb, true);

    /// <summary>发送/无回答用户数据（广播，<c>0x44</c>）：FCB=0、FCV=0。</summary>
    public static LinkControl SendNoReply() => Primary(PrimaryFunction.SendNoReply, false, false);

    /// <summary>召唤 1 级用户数据（<c>0x5A</c>/<c>0x7A</c>）。</summary>
    public static LinkControl RequestClass1(bool fcb) => Primary(PrimaryFunction.RequestClass1, fcb, true);

    /// <summary>召唤 2 级用户数据（<c>0x5B</c>/<c>0x7B</c>）。</summary>
    public static LinkControl RequestClass2(bool fcb) => Primary(PrimaryFunction.RequestClass2, fcb, true);

    /// <summary>从站响应：确认/数据/无数据；<paramref name="acd"/> = 1 表示有 1 级数据待取。</summary>
    public static LinkControl Secondary(SecondaryFunction function, bool acd = false, bool dfc = false)
        => new(false, (byte)function, acd, dfc);

    /// <summary>从站 ACK（<c>0x00</c>，ACD/DFC 可带）。</summary>
    public static LinkControl Ack(bool acd = false, bool dfc = false) => Secondary(SecondaryFunction.Ack, acd, dfc);

    /// <summary>从站「无所召唤数据」（<c>0x09</c>）。</summary>
    public static LinkControl NoData(bool acd = false, bool dfc = false) => Secondary(SecondaryFunction.RespondNoData, acd, dfc);

    /// <summary>功能码语义描述（诊断/日志用）。</summary>
    public string DescribeFunction() => IsPrimary
        ? ((PrimaryFunction)Function) switch
        {
            PrimaryFunction.ResetCu => "RESET CU",
            PrimaryFunction.SendConfirmed => "SEND/CONFIRM",
            PrimaryFunction.SendNoReply => "SEND/NO REPLY",
            PrimaryFunction.ResetFcb => "RESET FCB",
            PrimaryFunction.RequestLinkStatus => "REQ LINK STATUS",
            PrimaryFunction.RequestClass1 => "REQ CLASS 1",
            PrimaryFunction.RequestClass2 => "REQ CLASS 2",
            _ => $"FC={Function}",
        }
        : ((SecondaryFunction)Function) switch
        {
            SecondaryFunction.Ack => "ACK",
            SecondaryFunction.Nack => "NACK",
            SecondaryFunction.RespondData => "DATA",
            SecondaryFunction.RespondNoData => "NO DATA",
            SecondaryFunction.LinkStatus => "LINK STATUS",
            _ => $"FC={Function}",
        };

    /// <inheritdoc />
    public override string ToString()
    {
        var flags = IsPrimary
            ? $"FCB={(FcbOrAcd ? 1 : 0)} FCV={(FcvOrDfc ? 1 : 0)}"
            : $"ACD={(FcbOrAcd ? 1 : 0)} DFC={(FcvOrDfc ? 1 : 0)}";
        return $"PRM={(IsPrimary ? 1 : 0)} {flags} {DescribeFunction()}";
    }
}
