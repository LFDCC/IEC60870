//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//  协议实现依据：DL/T 667-1999 (IEC 60870-5-103) 第 7 章
//------------------------------------------------------------------------------

namespace IEC60870.CS103;

/// <summary>
/// IEC 60870-5-103 ASDU 类型标识（TI）。DL/T 667-1999 第 7 章。
/// </summary>
/// <remarks>
/// <b>TI 的数值是本协议私有的</b>：103 的 TI=1 是「带时标 CP56Time2a 的监视方向信息」，
/// 不能按其他规约的同号类型理解。
/// <para>
/// 厂商私有 TI 不在此枚举内，通过 <see cref="Iec103PrivateTypeRegistry.RegisterType(byte, IIec103PrivateTypeHandler)"/>
/// 运行时登记；未登记的 TI 由 <see cref="Iec103UnknownAsdu"/> 兜底，不丢帧。
/// </para>
/// </remarks>
public static class TypeIds
{
    /// <summary>带时标 CP56Time2a 的监视方向信息（1 级数据）。</summary>
    public const byte TimeTaggedMessage = 1;

    /// <summary>带相对时标 CP32Time2a 和故障序号的监视方向信息（专用范围）。</summary>
    public const byte TimeTaggedMessagePrivate = 2;

    /// <summary>标识报文（复位/总召唤时应答）。</summary>
    public const byte Identification = 5;

    /// <summary>时间同步。</summary>
    public const byte ClockSync = 6;

    /// <summary>总召唤命令。</summary>
    public const byte GeneralInterrogation = 7;

    /// <summary>总召唤终止。</summary>
    public const byte GeneralInterrogationEnd = 8;

    /// <summary>测量值（循环/2 级数据）。</summary>
    public const byte Measurand = 9;

    /// <summary>通用分类数据（读响应/写命令/写确认，COT 区分语义）。</summary>
    public const byte GenericData = 10;

    /// <summary>通用分类标识（响应读标识）。</summary>
    public const byte GenericIdentification = 11;

    /// <summary>一般命令。</summary>
    public const byte GeneralCommand = 20;

    /// <summary>通用分类读命令（RII+NOG+GIN/KOD 列表）。</summary>
    public const byte GenericCommand = 21;

    /// <summary>被记录的扰动表（FAN+SOF+CP56 × 记录数）。</summary>
    public const byte DisturbanceList = 23;

    /// <summary>扰动数据传输的命令（TOO/TOV/FAN/ACC）。</summary>
    public const byte DisturbanceCommand = 24;

    /// <summary>扰动数据传输的认可。</summary>
    public const byte DisturbanceAck = 25;

    /// <summary>扰动数据传输准备就绪（FAN/NOC/NOE/INT/CP32）。</summary>
    public const byte DisturbanceReady = 26;

    /// <summary>通道一次值（RPV/RSV/RFA）。</summary>
    public const byte ChannelRating = 27;

    /// <summary>带标签的通道扰动数据（标签传输）。</summary>
    public const byte DisturbanceTags = 29;

    /// <summary>带标签的通道扰动数据段（NDV/NFE/SDV）。</summary>
    public const byte ChannelData = 30;

    /// <summary>通道扰动数据传输终止。</summary>
    public const byte ChannelEnd = 31;

    /// <summary>标准 TI 的语义描述；非标准值返回 <c>"ASDU {n}"</c>。</summary>
    /// <remarks>私有 TI 的友好名称由 <see cref="Iec103PrivateTypeRegistry.DescribeType"/> 提供。</remarks>
    public static string Describe(byte typeId) => typeId switch
    {
        TimeTaggedMessage => "带时标 CP56Time2a 的监视方向信息",
        TimeTaggedMessagePrivate => "带相对时标的监视方向信息（专用范围）",
        Identification => "标识报文",
        ClockSync => "时间同步",
        GeneralInterrogation => "总召唤",
        GeneralInterrogationEnd => "总召唤终止",
        Measurand => "测量值",
        GenericData => "通用分类数据",
        GenericIdentification => "通用分类标识",
        GeneralCommand => "一般命令",
        GenericCommand => "通用分类读命令",
        DisturbanceCommand => "扰动数据传输的命令",
        DisturbanceAck => "扰动数据传输的认可",
        DisturbanceTags => "带标签的通道扰动数据",
        DisturbanceList => "被记录的扰动表",
        DisturbanceReady => "扰动数据传输准备就绪",
        ChannelRating => "通道一次值",
        ChannelData => "通道扰动数据段",
        ChannelEnd => "通道扰动数据传输终止",
        _ => $"ASDU {typeId}",
    };

    /// <summary>IEC 60870-5-103 标准 TypeID 范围上界（1-31）；超出即厂商扩展。</summary>
    public const byte StandardTypeIdMax = 31;

    /// <summary>
    /// 该 TI 是否落在标准范围（1-31）。
    /// </summary>
    /// <remarks>
    /// 103 标准只定义到 31，32 以上是厂商扩展（如许继 36~94、其他厂商各自的专用区）。
    /// 原先这里是一份 18 个常量的白名单，与上面的常量表重复维护、加一个新标准类型要改两处；
    /// 标准 TI 本就在 1-31 连续区间内，用范围判断即可，保留位也一并归为标准侧，不误报为私有。
    /// </remarks>
    public static bool IsStandard(byte typeId) => typeId is >= 1 and <= StandardTypeIdMax;
}

/// <summary>
/// IEC 60870-5-103 传输原因（COT，1 字节，bit7 为 P/N 否定位）。
/// </summary>
/// <remarks>
/// <b>1/2 的语义容易记反</b>：103 里 1=突发（1 级数据）、2=周期循环（2 级数据）；
/// 3/4 是链路复位（复位 FCB / 复位 CU），不是数据cause。
/// 总召唤数据点回送默认 COT=11（工程通行；部分装置用 5/9）。
/// <para>
/// 注意 40/42 是<b>请求与响应共用</b>的编号（写命令与写确认都是 40，
/// 读命令与读响应都是 42），语义必须结合方向与 TI 判定。
/// 此处刻意不提供「同值异名」的重复常量，避免 switch 分支误配。
/// </para>
/// </remarks>
public static class CotCodes
{
    /// <summary>突发（1 级数据上送）。</summary>
    public const byte Spontaneous = 1;

    /// <summary>周期、循环（2 级数据）。</summary>
    public const byte Cyclic = 2;

    /// <summary>复位帧计数位（FCB）。</summary>
    public const byte ResetFcb = 3;

    /// <summary>复位通信单元（CU）。</summary>
    public const byte ResetCu = 4;

    /// <summary>启动/重启。</summary>
    public const byte Restart = 5;

    /// <summary>上电。</summary>
    public const byte PowerOn = 6;

    /// <summary>测试模式。</summary>
    public const byte TestMode = 7;

    /// <summary>时间同步。</summary>
    public const byte ClockSync = 8;

    /// <summary>总召唤启动。</summary>
    public const byte GiStart = 9;

    /// <summary>总召唤终止。</summary>
    public const byte GiStop = 10;

    /// <summary>远方操作/返送（也用于总召唤数据点回送）。</summary>
    public const byte RemoteOperation = 11;

    /// <summary>当地操作/返送。</summary>
    public const byte LocalOperation = 12;

    /// <summary>命令肯定确认。</summary>
    public const byte CommandAck = 20;

    /// <summary>命令否定确认。</summary>
    public const byte CommandNack = 21;

    /// <summary>扰动数据传输。</summary>
    public const byte DisturbanceTransfer = 31;

    /// <summary>通用分类写命令 / 写命令肯定确认（请求与响应共用编号，按方向区分）。</summary>
    public const byte GenericWrite = 40;

    /// <summary>通用分类写命令否定确认。</summary>
    public const byte GenericWriteNegative = 41;

    /// <summary>通用分类读命令 / 读命令有效数据响应（请求与响应共用编号，按方向区分）。</summary>
    public const byte GenericRead = 42;

    /// <summary>通用分类读命令无效数据响应。</summary>
    public const byte GenericReadInvalid = 43;

    /// <summary>通用分类写确认（后续帧）。</summary>
    public const byte GenericWriteConfirm = 44;

    /// <summary>P/N 否定位掩码（bit7）。</summary>
    public const byte NegativeMask = 0x80;

    /// <summary>COT 数值掩码（bit0-6）。</summary>
    public const byte ValueMask = 0x7F;

    /// <summary>该 COT 是否为否定确认（bit7）。</summary>
    public static bool IsNegative(byte cot) => (cot & NegativeMask) != 0;

    /// <summary>去掉 P/N 位后的 COT 值。</summary>
    public static byte Value(byte cot) => (byte)(cot & ValueMask);

    /// <summary>标准 COT 的语义描述（不含 P/N 标注）；非标准值返回 <c>"COT {n}"</c>。</summary>
    public static string Describe(byte cot)
    {
        var name = Value(cot) switch
        {
            Spontaneous => "突发",
            Cyclic => "周期、循环",
            ResetFcb => "复位帧计数位（FCB）",
            ResetCu => "复位通信单元（CU）",
            Restart => "启动/重启",
            PowerOn => "上电",
            TestMode => "测试模式",
            ClockSync => "时间同步",
            GiStart => "总召唤启动",
            GiStop => "总召唤终止",
            RemoteOperation => "远方操作/返送（远方命令）",
            LocalOperation => "当地操作/返送（当地命令）",
            CommandAck => "命令肯定确认",
            CommandNack => "命令否定确认",
            DisturbanceTransfer => "扰动数据传输",
            GenericWrite => "通用分类写命令/写确认",
            GenericWriteNegative => "通用分类写命令否定确认",
            GenericRead => "通用分类读命令/读响应（有效）",
            GenericReadInvalid => "通用分类读命令无效数据响应",
            GenericWriteConfirm => "通用分类写确认",
            _ => $"COT {Value(cot)}",
        };
        return IsNegative(cot) ? $"{name}（否定 N）" : name;
    }
}
