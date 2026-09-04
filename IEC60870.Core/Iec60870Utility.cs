//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

namespace IEC60870.Core;

/// <summary>
/// IEC 60870 组件库公共常量。
/// </summary>
/// <remarks>
/// 协议名常量配合 TouchSocket <c>Protocol</c> 类型使用（与 TouchSocketModbusUtility 同款约定）：
/// <code>
/// var protocol = new TouchSocket.Sockets.Protocol(Iec60870Utility.Iec104);   // "iec104"
/// bool is104 = client.Protocol == new Protocol(Iec60870Utility.Iec104);
/// </code>
/// 各站点组件（<c>Iec104Client</c>/<c>Iec104Server</c>/<c>Iec101Client</c>/<c>Iec101Server</c>）
/// 构造时已自动设置对应的 Protocol 标识，业务侧通常只需读取比对。
/// </remarks>
public static class Iec60870Utility
{
    /// <summary>IEC 60870-5-101 串口（FT1.2）协议标识。</summary>
    public const string Iec101 = nameof(Iec101);

    /// <summary>IEC 60870-5-101 over TCP 隧道协议标识。</summary>
    public const string Iec101OverTcp = nameof(Iec101OverTcp);

    /// <summary>IEC 60870-5-104 协议标识。</summary>
    public const string Iec104 = nameof(Iec104);
}
