//------------------------------------------------------------------------------
//  IEC60870.Core.NET — SCO 单点命令限定词
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

namespace IEC60870.Core;

/// <summary>
/// 单点命令限定词（SCO）：bit0 = 命令状态（0 分 / 1合），
/// bit1-6 = QU（限定词，×4 编码），bit7 = S/E 选择标志。
/// </summary>
public class SingleCommandQualifier
{
    private const byte MaskState = 0x01;   // 命令状态
    private const byte MaskSelect = 0x80;  // S/E 选择标志
    private const byte MaskQu = 0x7C;      // QU 域（bit2-6）

    private readonly byte _encodedValue;

    /// <summary>以原始编码字节构造。</summary>
    public SingleCommandQualifier(byte encodedValue) => _encodedValue = encodedValue;

    /// <summary>以状态、选择标志与限定词构造。</summary>
    /// <param name="state">命令状态（false=分 / true=合）。</param>
    /// <param name="selectCommand">是否选择（预置）命令。</param>
    /// <param name="qu">限定词（0…31）。</param>
    public SingleCommandQualifier(bool state, bool selectCommand, int qu)
    {
        var value = (byte)((qu & 0x1f) * 4);

        if (state)
        {
            value |= MaskState;
        }

        if (selectCommand)
        {
            value |= MaskSelect;
        }

        _encodedValue = value;
    }

    /// <summary>限定词 QU（0…31）。</summary>
    public int QU => (_encodedValue & MaskQu) / 4;

    /// <summary>命令状态（false=分，true=合）。</summary>
    public bool State => (_encodedValue & MaskState) != 0;

    /// <summary>是否选择（预置）命令。</summary>
    public bool Select => (_encodedValue & MaskSelect) != 0;

    /// <summary>原始编码字节。</summary>
    public byte GetEncodedValue() => _encodedValue;
}
