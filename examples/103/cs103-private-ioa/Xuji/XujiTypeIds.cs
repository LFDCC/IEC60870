//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace cs103_private_ioa.Xuji;

/// <summary>
/// 许继（Xuji）103 规约扩展的 TypeID 与 COT 常量。
/// </summary>
/// <remarks>
/// <para>
/// <b>与 104 私有类型的区别</b>：104 的私有类型集中在 TypeID 128-170 的「制造厂私有区」，
/// 许继只用了 166 / 168 两个。103 标准（DL/T 667-1999）的 TypeID 只定义到 31，
/// 许继把 36~94 作为<b>扩展类型</b>使用——形式上与 104 的私有 TypeID 完全同构，
/// 因此同样由 <c>Iec103PrivateTypeRegistry</c> 的 TypeID 轴承载。
/// </para>
/// <para>
/// <b>规约依据</b>：《许继103通信规约》（2005-12-11，CBZ8000 变电站自动化系统），
/// 各类型对应的表格号见每个常量的注释。规约写明「INF 见装置的相应信息表」——
/// 具体 FUN/INF 取值由装置信息表定义，不在规约内，故此处只定义 TypeID 与数据布局。
/// </para>
/// <para>
/// <b>承载方式</b>：规约正文写的是 RS-232/RS-485（9600, 8E1, 主从式），
/// 但 FT1.2 帧格式与承载介质无关——同一套报文可直接跑在 TCP 上（网口 103）。
/// 本示例只依赖 <c>Iec103Client</c> / <c>Iec103Server</c>，串口与网口均可。
/// </para>
/// </remarks>
public static class XujiTypeIds
{
    // ══════════════════════════════════════════════════════════
    //  标准 103 范围（1..31）—— 许继沿用，不在此重复定义
    //  1=带时标监视信息 2=带相对时标监视信息 5=标识 6=时间同步
    //  7=总召唤 8=总召唤结束 9=测量值 10=通用分类数据 20=一般命令 23=扰动表
    // ══════════════════════════════════════════════════════════

    /// <summary>电度量数据（表20）。信息体 = N × [4 字节读数 LE + 1 字节 IV/CA/CY/顺序号]。</summary>
    public const byte EnergyCounter = 36;

    /// <summary>调压开关升降状态（3.3.12 正文）。信息体同 TI=1 布局（DPI + CP56 时标）。</summary>
    public const byte TapChangerStatus = 39;

    /// <summary>
    /// 遥信量状态（表10）—— <b>无时标版本</b>。
    /// 信息体 = N × 1 字节 [IV|NT|BL|SB|0|0|0|SPI]。
    /// </summary>
    public const byte SinglePointStatus = 40;

    /// <summary>
    /// 告警信号（表22）。信息体 = N × [DPI(1) + 毫秒(2 LE) + IV/分(1) + SU/时(1)]，共 5 字节。
    /// </summary>
    public const byte AlarmSignal = 41;

    /// <summary>
    /// 断路器状态（表10）—— <b>带 CP56 时标版本</b>。
    /// 信息体 = DPI(1) + CP56Time2a(7)，共 8 字节。
    /// </summary>
    public const byte BreakerStatus = 42;

    /// <summary>
    /// 断路器状态变位（表11）。信息体 = N × [DPI(1) + CP24Time2a(4)]，共 5 字节/元素。
    /// </summary>
    public const byte BreakerStatusChange = 43;

    /// <summary>
    /// 遥测值（表12）—— <b>非序列寻址</b>：SQ=0 时每个元素前带 INF(1)。
    /// 信息体 = N × [INF(1) + 被测值 int16 LE(2)]。
    /// </summary>
    /// <remarks>
    /// 与标准 TI=9 的区别：TI=9 是序列寻址（首元素带 INF，后续按步进），
    /// ASDU_50 在 SQ=0 时逐元素带 INF，用于「越限主动上送」场景（COT=1）。
    /// </remarks>
    public const byte MeasuredValueWithInf = 50;

    /// <summary>
    /// 调定值 / 定值修改执行 / 撤销（表26~28）。
    /// 信息体 = CPU板地址(1) + 定值区号(1) + 定值序号(1) [+ 定值 N×2B]。
    /// 具体子命令由 INF 区分：100=调定值 101=预发 102=返校 103=执行 104=执行确认 105=撤销 106=撤销确认。
    /// </summary>
    public const byte SettingCommand = 61;

    /// <summary>故障报告确认 / 故障报告结束（表33）。信息体 = 故障报告序号(1)。</summary>
    public const byte FaultReportAck = 62;

    /// <summary>断路器控制（表15）。信息体 = DCO(1) + RII(1)。</summary>
    public const byte BreakerControl = 64;

    /// <summary>调压开关升降（表15）。信息体 = DCO(1) + RII(1)。</summary>
    public const byte TapChangerControl = 65;

    /// <summary>同期方式选择（表15）。信息体 = DCO(1) + RII(1)。</summary>
    public const byte SynchroCheckControl = 67;

    /// <summary>故障报告（表34）。信息体 = DPI(1) + 毫秒(2 LE) + IV/分(1) + SU/时(1) [+ 测距结果]。</summary>
    public const byte FaultReport = 70;

    /// <summary>自检与运行事件记录（保护装置，表37）。信息体 = DPI(1) + CP56(7) [+ 附带结果]。</summary>
    public const byte SelfCheckRecordProtection = 73;

    /// <summary>自检与运行事件记录（测控装置，表38）。信息体 = SPI(1) + CP56(7) [+ 附带结果]。</summary>
    public const byte SelfCheckRecordControl = 74;

    /// <summary>电度量冻结 / 冻结确认 / 电度量数据结束（表19）。信息体 = FRZ(1) + RII(1)。</summary>
    /// <remarks>
    /// 同一个 TI 用 COT 区分三种语义：2 = 冻结命令或肯定确认，66 = 否定确认，10 = 电度量数据结束。
    /// </remarks>
    public const byte EnergyFreeze = 88;

    /// <summary>调遥测值的否定确认 / 遥测值结束（表14）。无信息体，用 COT 区分：21 = 无遥测值，10 = 结束。</summary>
    public const byte MeasuredValueAck = 93;

    /// <summary>调自检与运行事件信息记录命令（表35）。信息体 = 信息记录段号(1)。</summary>
    public const byte SelfCheckCommand = 94;

    /// <summary>该 TypeID 的规约语义描述。</summary>
    public static string Describe(byte typeId) => typeId switch
    {
        EnergyCounter => "电度量数据",
        TapChangerStatus => "调压开关升降状态",
        SinglePointStatus => "遥信量状态（无时标）",
        AlarmSignal => "告警信号",
        BreakerStatus => "断路器状态",
        BreakerStatusChange => "断路器状态变位",
        MeasuredValueWithInf => "遥测值（逐条带INF）",
        SettingCommand => "定值命令",
        FaultReportAck => "故障报告确认/结束",
        BreakerControl => "断路器控制",
        TapChangerControl => "调压开关升降控制",
        SynchroCheckControl => "同期方式选择",
        FaultReport => "故障报告",
        SelfCheckRecordProtection => "自检/事件记录（保护）",
        SelfCheckRecordControl => "自检/事件记录（测控）",
        EnergyFreeze => "电度量冻结/结束",
        MeasuredValueAck => "调遥测否定/结束",
        SelfCheckCommand => "调自检记录命令",
        _ => $"许继扩展 TI={typeId}",
    };

    /// <summary>该 TypeID 是否为许继扩展类型（标准 103 只到 31）。</summary>
    public static bool IsXujiExtension(byte typeId) => typeId switch
    {
        EnergyCounter or TapChangerStatus or SinglePointStatus or AlarmSignal
            or BreakerStatus or BreakerStatusChange or MeasuredValueWithInf
            or SettingCommand or FaultReportAck or BreakerControl or TapChangerControl
            or SynchroCheckControl or FaultReport or SelfCheckRecordProtection
            or SelfCheckRecordControl or EnergyFreeze or MeasuredValueAck
            or SelfCheckCommand => true,
        _ => false,
    };
}

/// <summary>
/// 许继 103 规约使用的附加传送原因（COT）。
/// </summary>
/// <remarks>
/// <para>
/// 标准 <c>CotCodes</c> 只定义到 44；许继在控制/报告/电度量场景用了额外取值。
/// 其中 <b>13 / 64 / 66</b> 落在标准范围之外，通过 <c>Iec103PrivateTypeRegistry.RegisterCot</c>
/// 登记后仅影响日志/诊断描述，不改变分派逻辑。
/// </para>
/// <para>
/// <b>2 / 10 / 20 / 21 与标准同值</b>（2 周期、10 总召唤终止、20 肯定确认、21 否定确认），
/// 许继只是把它们复用到电度量/结束帧/命令确认场景，因此<b>不</b>覆盖登记——
/// 否则会把标准 TI=9 的「周期」也改名成「电度量数据」。
/// </para>
/// </remarks>
public static class XujiCot
{
    /// <summary>控制命令（表15：断路器控制/调压开关/同期方式）。</summary>
    public const byte ControlCommand = 13;

    /// <summary>电度量数据（表20：定时或召唤上送）。</summary>
    public const byte EnergyData = 2;

    /// <summary>报告召唤（表34/37/38：调故障报告、调自检记录）。</summary>
    public const byte ReportCall = 64;

    /// <summary>电度量冻结否定确认（表19）。</summary>
    public const byte EnergyFreezeNegative = 66;

    /// <summary>肯定确认（表15/25：控制命令与一般命令的肯定返回）。</summary>
    public const byte Positive = 20;

    /// <summary>否定确认（表15/25）。</summary>
    public const byte Negative = 21;

    /// <summary>数据结束帧（表14/19/33：遥测/电度量/故障报告结束）。</summary>
    public const byte EndOfData = 10;

    /// <summary>许继附加 COT 的语义描述。</summary>
    public static string? Describe(byte cot) => cot switch
    {
        ControlCommand => "控制命令",
        EnergyData => "电度量数据",
        ReportCall => "报告召唤",
        EnergyFreezeNegative => "电度量冻结否定确认",
        _ => null,
    };
}
