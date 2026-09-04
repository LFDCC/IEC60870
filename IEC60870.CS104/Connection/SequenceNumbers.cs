//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS104;

/// <summary>
/// IEC 60870-5-104 序列号算术（模 32768 = 2^15）。
/// </summary>
/// <remarks>
/// APCI 序列号 N(S)/N(R) 为 15 位循环序号：本类集中封装模运算与圆上距离，
/// 消除散落的 <c>% 32768</c> 与手写回绕分支。圆上距离是 O(1) 确认校验的基础：
/// <c>Distance(a,b) = (b−a) mod 32768</c> 表示从 a 顺时针前进到 b 的步数。
/// </remarks>
internal static class SequenceNumbers
{
    private const int Modulus = 32768;

    /// <summary>
    /// <paramref name="value"/> 在序列号圆上顺时针前进到 <paramref name="target"/> 的步数（模 32768）。
    /// 相同值为 0；"target 在 value 后一拍" 为 1；"target 在 value 前一拍" 为 32767。
    /// </summary>
    public static int Distance(int value, int target)
        => (target - value + Modulus) % Modulus;

    /// <summary><paramref name="value"/> 递增 1（模 32768）。</summary>
    public static int Increment(int value)
        => (value + 1) % Modulus;
}
