//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS104;

/// <summary>一次控制命令确认的快照（不可变）。</summary>
public readonly struct ControlConfirmation
{
    /// <summary>构造确认快照。</summary>
    public ControlConfirmation(TypeID typeId, int commonAddress, int ioa,
        bool isSelect, bool isNegative, bool isTest)
    {
        TypeId = typeId;
        CommonAddress = commonAddress;
        Ioa = ioa;
        IsSelect = isSelect;     // true = 预发确认；false = 执行确认
        IsNegative = isNegative; // true = 否定确认（被拒）
        IsTest = isTest;
    }

    /// <summary>确认对应的 ASDU 类型标识。</summary>
    public TypeID TypeId { get; }

    /// <summary>公共地址（CA）。</summary>
    public int CommonAddress { get; }

    /// <summary>信息对象地址（IOA）。</summary>
    public int Ioa { get; }

    /// <summary>true = 预发（select）确认；false = 执行确认。</summary>
    public bool IsSelect { get; }

    /// <summary>true = 否定确认（命令被拒绝）。</summary>
    public bool IsNegative { get; }

    /// <summary>true = 测试帧确认。</summary>
    public bool IsTest { get; }

    /// <summary>阶段中文名：预发结束 / 执行完成。</summary>
    public string Phase => IsSelect ? "预发结束" : "执行完成";

    /// <inheritdoc/>
    public override string ToString() =>
        $"[{Phase}] Type={TypeId} IOA={Ioa} CA={CommonAddress} " +
        $"{(IsNegative ? "NEGATIVE(拒绝)" : "OK")}{(IsTest ? " [TEST]" : "")}";
}

/// <summary>
/// 控制命令确认的关联键与字段解析工具（零分配，直接读 <see cref="AsduView"/> 原始字节）。
/// 关联键 = (TypeID, 公共地址 CA, 信息对象地址 IOA, Select 位)。预发与执行共用同一 IOA,
/// 靠 Select 位区分，二者确认互不串台。
/// </summary>
internal static class ControlConfirmMatcher
{
    /// <summary>构造 (TypeID, CA, IOA, Select) 四元组关联键。</summary>
    internal static long MakeKey(TypeID type, int ca, int ioa, bool select) =>
        ((long)type << 48) | ((long)(ca & 0xffff) << 32) | ((long)(ioa & 0xffffff) << 8) | (select ? 1L : 0L);

    /// <summary>从收到的 ACT-CON 原始字节读 IOA（小端，宽度由应用层参数决定）。</summary>
    internal static int ReadIoa(in AsduView view, ApplicationLayerParameters al)
    {
        var raw = view.Raw;
        var off = view.HeaderLength;
        var ioa = 0;
        for (var i = 0; i < al.SizeOfIOA && off + i < raw.Length; i++)
        {
            ioa |= raw[off + i] << (8 * i);
        }

        return ioa;
    }

    /// <summary>
    /// 控制命令「限定词字节」（含 Select 位）相对信息元素起点的偏移（不含 IOA）。
    /// 返回 -1 表示该类型无 Select 位（如 C_BO_NA_1 / C_BO_TA_1）。
    /// </summary>
    /// <remarks>
    /// 各命令类型的信息元素布局不同：
    /// <list type="bullet">
    /// <item>C_SC/DC/RC（SCO/DCQ/RCO 即限定词本身）：偏移 0；</item>
    /// <item>C_SE_NA_1/NB_1（2 字节归一化/标度化值 + QOS）：偏移 2；</item>
    /// <item>C_SE_NC_1（4 字节短浮点值 + QOS）：偏移 4；</item>
    /// <item>带 CP56Time2a 的变体：时标追加在限定词之后，<b>不改变限定词偏移</b>；</item>
    /// <item>C_BO_NA_1 / C_BO_TA_1：仅 32 位串，无 Select 位。</item>
    /// </list>
    /// 这是接收侧读取 Select 位的唯一事实源，必须与发送侧
    /// <see cref="GetSelectBit(InformationObject)"/>（类型化读取）保持一致。
    /// </remarks>
    private static int SelectQualifierOffset(TypeID type) => type switch
    {
        TypeID.C_SC_NA_1 or TypeID.C_DC_NA_1 or TypeID.C_RC_NA_1 => 0,
        TypeID.C_SC_TA_1 or TypeID.C_DC_TA_1 or TypeID.C_RC_TA_1 => 0,
        TypeID.C_SE_NA_1 or TypeID.C_SE_NB_1 or TypeID.C_SE_TA_1 or TypeID.C_SE_TB_1 => 2,
        TypeID.C_SE_NC_1 or TypeID.C_SE_TC_1 => 4,
        TypeID.C_BO_NA_1 or TypeID.C_BO_TA_1 => -1,
        _ => -1 // 非控制命令无 Select 位
    };

    /// <summary>从收到的 ACT-CON 读 Select 位（限定词字节 bit7），零分配。</summary>
    /// <remarks>
    /// 限定词在信息元素内的偏移随类型而异（见 <see cref="SelectQualifierOffset"/>）——
    /// 早期实现固定用 <c>HeaderLength + SizeOfIOA</c>，对 C_SE_* 会误读值的低字节 bit7，
    /// 导致设定值低位恰好置位时确认匹配失败而超时（已修复）。
    /// </remarks>
    internal static bool ReadSelectBit(in AsduView view, ApplicationLayerParameters al)
    {
        var rel = SelectQualifierOffset(view.TypeId);
        if (rel < 0)
        {
            return false; // 无 Select 位
        }

        var raw = view.Raw;
        var off = view.HeaderLength + al.SizeOfIOA + rel;
        if (off < raw.Length)
        {
            return (raw[off] & 0x80) != 0;
        }

        return false;
    }

    /// <summary>
    /// 读取控制命令的 Select 位（预发=true / 执行=false）。各命令类型 Select 位位置不同：
    /// C_SC/DC/RC 在 IOA 后第 1 字节（SCO/DCQ/RCO）；C_SE_NA/NB 在 2 字节值之后的 QOS、
    /// C_SE_NC 在 4 字节值之后的 QOS。类型化读取各自属性最稳妥，避免字节偏移假设。
    /// 无 Select 位的命令（如 C_BO_NA_1 / C_BO_TA_1）按执行（false）处理。
    /// </summary>
    /// <remarks>
    /// <see cref="StepCommand"/>（C_RC_NA_1 / C_RC_TA_1）虽继承自 <see cref="DoubleCommand"/>、
    /// 类型模式已能命中，此处仍显式列出：避免将来 <see cref="StepCommand"/> 不再继承
    /// <see cref="DoubleCommand"/> 时静默落入 <c>_ => false</c>，导致预发（select）确认的
    /// 关联键永远等于执行（false），预发确认匹配不上而超时。
    /// </remarks>
    internal static bool GetSelectBit(InformationObject io) => io switch
    {
        SingleCommand sc => sc.Select,
        StepCommand rc => rc.Select,
        DoubleCommand dc => dc.Select,
        SetpointCommandNormalized s => s.QOS.Select,
        SetpointCommandScaled s => s.QOS.Select,
        SetpointCommandShort s => s.QOS.Select,
        _ => false
    };
}
