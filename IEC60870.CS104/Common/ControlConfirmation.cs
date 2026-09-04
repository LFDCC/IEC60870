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

    /// <summary>从收到的 ACT-CON 读 Select 位：IOA 之后第一个限定词字节的 bit7。</summary>
    /// <remarks>
    /// 直接读字节（零分配），与 <see cref="GetSelectBit(InformationObject)"/> 的位定义一致：
    /// C_SC/DC/RC 的 SCO/DCQ、C_SE 的 QOS 均为信息元素首字节 bit7 = Select；
    /// 无 Select 位的命令（如 C_BO_NA_1）按 false 处理。
    /// </remarks>
    internal static bool ReadSelectBit(in AsduView view, ApplicationLayerParameters al)
    {
        if (view.TypeId == TypeID.C_BO_NA_1)
        {
            return false; // 无 Select 位
        }

        var raw = view.Raw;
        var off = view.HeaderLength + al.SizeOfIOA; // 第一个信息元素的限定词字节
        if (off < raw.Length)
        {
            return (raw[off] & 0x80) != 0;
        }

        return false;
    }

    /// <summary>
    /// 读取控制命令的 Select 位（预发=true / 执行=false）。各命令类型 Select 位位置不同：
    /// C_SC/DC/RC 在 IOA 后第 1 字节（SCO/DCQ/RCO），C_SE 在 QOS 字节，直接读类型化属性最稳妥。
    /// 无 Select 位的命令（如 C_BO_NA_1）按执行（false）处理。
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
