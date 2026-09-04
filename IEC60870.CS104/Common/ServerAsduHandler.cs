//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS104;

/// <summary>服务端收到 ASDU 的零拷贝回调（带来源会话）。</summary>
public delegate void ServerAsduHandler(Iec104Session session, in IEC60870.Core.AsduView asdu);
