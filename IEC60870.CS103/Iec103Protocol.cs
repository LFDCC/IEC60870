//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS103;

/// <summary>
/// IEC 60870-5-103 协议标识常量。
/// </summary>
/// <remarks>
/// <para>
/// 本包<b>不依赖任何其他协议包</b>：103 的 ASDU 采用 FUN/INF 两级寻址，COT/TypeID
/// 数值空间与应用层时标语义都是本协议私有的，没有可复用的应用层模型。
/// 只依赖 TouchSocket 与 BCL，避免把无关的协议类型带进每个编译单元，
/// 也避免只差大小写、语义却不同的近似名风险（写错一个字母仍能编译通过）。
/// </para>
/// </remarks>
public static class Iec103Protocol
{
    /// <summary>IEC 60870-5-103 协议标识（"iec103"）。</summary>
    public const string Iec103 = nameof(Iec103);

    /// <summary>IEC 60870-5-103 over TCP 隧道协议标识（"Iec103OverTcp"）。</summary>
    /// <remarks>
    /// 103 的 FT1.2 帧可直接由 TCP 承载（无 APCI），与串口语义完全一致；
    /// 该标识仅用于在诊断/日志中区分承载方式。
    /// </remarks>
    public const string Iec103OverTcp = nameof(Iec103OverTcp);
}
