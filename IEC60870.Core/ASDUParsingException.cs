//------------------------------------------------------------------------------
//  IEC60870.Core.NET — ASDU 解析异常
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;

namespace IEC60870.Core;

/// <summary>
/// ASDU / 信息对象解码失败时抛出的异常（报文截断、字段越界、非法结构等）。
/// </summary>
[Serializable]
public class ASDUParsingException : Exception
{
    /// <summary>以描述信息构造。</summary>
    public ASDUParsingException(string message)
        : base(message)
    {
    }
}
