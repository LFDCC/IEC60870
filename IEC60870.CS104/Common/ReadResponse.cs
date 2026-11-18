//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS104;

/// <summary>
/// 一次读命令（C_RD_NA_1）应答的快照（不可变）。
/// </summary>
/// <remarks>
/// 读命令的应答与命令确认不同：<b>类型标识由点本身决定</b>（M_SP_NA_1 / M_ME_NC_1 …），
/// 请求方事先不知道，因此 <see cref="TypeId"/> 是「实际回来的数据类型」而非 102。
/// 应答正常时为被读点的值（<see cref="Value"/>），装置拒绝读时为 C_RD_NA_1 的否定应答
/// （<see cref="IsNegative"/> 为 true、<see cref="Value"/> 为 null）。
/// </remarks>
public readonly struct ReadResponse
{
    /// <summary>构造读命令应答快照。</summary>
    public ReadResponse(TypeID typeId, int commonAddress, int ioa,
        bool isNegative, InformationObject value, bool isTest)
    {
        TypeId = typeId;
        CommonAddress = commonAddress;
        Ioa = ioa;
        IsNegative = isNegative;
        Value = value;
        IsTest = isTest;
    }

    /// <summary>实际应答的 ASDU 类型标识（被读点的数据类型，不是 C_RD_NA_1）。</summary>
    public TypeID TypeId { get; }

    /// <summary>公共地址（CA）。</summary>
    public int CommonAddress { get; }

    /// <summary>信息对象地址（IOA）。</summary>
    public int Ioa { get; }

    /// <summary>true = 装置否定应答（不支持读 / 无此点 / 未知地址）。</summary>
    public bool IsNegative { get; }

    /// <summary>
    /// 解码后的点值（<see cref="MeasuredValueShort"/>、<see cref="SinglePointInformation"/> …）。
    /// 否定应答时为 null；应答到达但本端未注册该类型处理器时也为 null（此时
    /// <see cref="TypeId"/> 仍然有效，可用于自证收到了什么）。
    /// </summary>
    public InformationObject Value { get; }

    /// <summary>true = 测试帧应答（COT bit7）。</summary>
    public bool IsTest { get; }

    /// <inheritdoc/>
    public override string ToString() =>
        IsNegative
            ? $"[读拒绝] IOA={Ioa} CA={CommonAddress}{(IsTest ? " [TEST]" : "")}"
            : $"[读应答] Type={TypeId} IOA={Ioa} CA={CommonAddress} " +
              $"{(Value == null ? "(未解码)" : Value.GetType().Name)}{(IsTest ? " [TEST]" : "")}";
}
