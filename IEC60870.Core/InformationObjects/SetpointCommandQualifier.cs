//------------------------------------------------------------------------------
//  IEC60870.Core.NET — QL 设定值命令限定词
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

namespace IEC60870.Core;

/// <summary>
/// 设定值命令限定词（QOL）：bit0-6 = QL（设定值限定词），bit7 = S/E 选择标志。
/// </summary>
public class SetpointCommandQualifier
{
    private const byte MaskQl = 0x7F;      // QL 域（bit0-6）
    private const byte MaskSelect = 0x80;  // S/E 选择标志

    private readonly byte _encodedValue;

    /// <summary>以原始编码字节构造。</summary>
    public SetpointCommandQualifier(byte encodedValue) => _encodedValue = encodedValue;

    /// <summary>以选择标志与 QL 构造。</summary>
    /// <param name="select">是否选择（预置）命令。</param>
    /// <param name="ql">设定值限定词（0…127）。</param>
    public SetpointCommandQualifier(bool select, int ql)
    {
        var value = (byte)(ql & MaskQl);

        if (select)
        {
            value |= MaskSelect;
        }

        _encodedValue = value;
    }

    /// <summary>设定值限定词 QL（0…127）。</summary>
    public int QL => _encodedValue & MaskQl;

    /// <summary>是否选择（预置）命令。</summary>
    public bool Select => (_encodedValue & MaskSelect) != 0;

    /// <summary>原始编码字节。</summary>
    public byte GetEncodedValue() => _encodedValue;
}
