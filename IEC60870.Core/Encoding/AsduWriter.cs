//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;

namespace IEC60870.Core;

/// <summary>
/// 面向 <see cref="Span{T}"/> 的顺序字节写入器，是信息体编码热路径的目标类型
/// （Modbus 风格 <c>Build(ref TWriter)</c> 的本地化）。
/// </summary>
/// <remarks>
/// <para>
/// 编码数据直接写入调用方提供的连续缓冲（ASDU 上限 253 字节，单缓冲足矣），
/// 无中间数组分配、无逐字节虚调用：写入方法全部内联到调用方。</para>
/// <para>
/// 越界（超出缓冲）抛 <see cref="ArgumentOutOfRangeException"/>——编码长度由
/// <see cref="InformationObject.GetEncodedSize"/> 预先核算，正常路径不会触发，
/// 触发即表示信息体实现与尺寸核算不一致（bug）。
/// </para>
/// </remarks>
public ref struct AsduWriter
{
    private readonly Span<byte> _span;
    private int _pos;

    /// <summary>在 <paramref name="span"/> 上创建写入器，起始位置 0。</summary>
    public AsduWriter(Span<byte> span)
    {
        _span = span;
        _pos = 0;
    }

    /// <summary>当前写入位置（已写字节数）。</summary>
    public readonly int Position => _pos;

    /// <summary>已写出的字节视图。</summary>
    public readonly ReadOnlySpan<byte> Written => _span.Slice(0, _pos);

    /// <summary>写入单个字节并推进。</summary>
    public void WriteByte(byte value)
    {
        _span[_pos++] = value;
    }

    /// <summary>写入低 <paramref name="byteCount"/> 字节（小端序，IEC 60870 基元整数序）并推进。</summary>
    public void WriteIntLittleEndian(int value, int byteCount)
    {
        for (var i = 0; i < byteCount; i++)
        {
            _span[_pos++] = (byte)((value >> (i * 8)) & 0xff);
        }
    }

    /// <summary>批量写入字节并推进。</summary>
    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(_span.Slice(_pos));
        _pos += bytes.Length;
    }
}