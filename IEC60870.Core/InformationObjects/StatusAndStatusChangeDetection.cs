//------------------------------------------------------------------------------
//  IEC60870.Core.NET — SCD 状态与状态变位检测（4 字节双 16 位字）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;
using System.Text;

namespace IEC60870.Core;

/// <summary>
/// 状态与状态变位检测（SCD）：4 字节，前 2 字节为 ST（当前状态，16 位），
/// 后 2 字节为 CD（变位检测，16 位），均为小端。
/// </summary>
public class StatusAndStatusChangeDetection
{
    private readonly byte[] _encodedValue = new byte[4];

    /// <summary>状态字 ST（bit i = 通道 i 当前状态）。</summary>
    public UInt16 STn
    {
        get => (ushort)(_encodedValue[0] | (_encodedValue[1] << 8));
        set
        {
            _encodedValue[0] = (byte)value;
            _encodedValue[1] = (byte)(value >> 8);
        }
    }

    /// <summary>变位检测字 CD（bit i = 通道 i 是否发生过变位）。</summary>
    public UInt16 CDn
    {
        get => (ushort)(_encodedValue[2] | (_encodedValue[3] << 8));
        set
        {
            _encodedValue[2] = (byte)value;
            _encodedValue[3] = (byte)(value >> 8);
        }
    }

    /// <summary>查询通道 <paramref name="i"/>（0…15）的当前状态。</summary>
    public bool ST(int i) => InRange(i) && (STn & (1 << i)) != 0;

    /// <summary>设置通道 <paramref name="i"/>（0…15）的当前状态。</summary>
    public void ST(int i, bool value)
    {
        if (!InRange(i))
        {
            return;
        }

        if (value)
        {
            STn = (UInt16)(STn | (1 << i));
        }
        else
        {
            STn = (UInt16)(STn & ~(1 << i));
        }
    }

    /// <summary>查询通道 <paramref name="i"/>（0…15）是否发生变位。</summary>
    public bool CD(int i) => InRange(i) && (CDn & (1 << i)) != 0;

    /// <summary>设置通道 <paramref name="i"/>（0…15）的变位检测位。</summary>
    public void CD(int i, bool value)
    {
        if (!InRange(i))
        {
            return;
        }

        if (value)
        {
            CDn = (UInt16)(CDn | (1 << i));
        }
        else
        {
            CDn = (UInt16)(CDn & ~(1 << i));
        }
    }

    /// <summary>构造全零 SCD。</summary>
    public StatusAndStatusChangeDetection()
    {
    }

    /// <summary>复制构造。</summary>
    public StatusAndStatusChangeDetection(StatusAndStatusChangeDetection original)
    {
        STn = original.STn;
        CDn = original.CDn;
    }

    /// <summary>从报文切片解析 4 字节 SCD。</summary>
    /// <exception cref="ASDUParsingException">剩余长度不足 4 字节时抛出。</exception>
    public StatusAndStatusChangeDetection(ReadOnlySpan<byte> msg, int startIndex)
    {
        if (msg.Length < startIndex + 4)
        {
            throw new ASDUParsingException("报文长度不足以解析 SCD");
        }

        msg.Slice(startIndex, 4).CopyTo(_encodedValue);
    }

    /// <summary>内部编码数组引用（勿修改）。</summary>
    public byte[] GetEncodedValue() => _encodedValue;

    /// <summary>零分配编码切片。</summary>
    public ReadOnlySpan<byte> AsSpan() => _encodedValue.AsSpan();

    private static bool InRange(int i) => i >= 0 && i < 16;

    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new StringBuilder(50);

        sb.Append("ST:");
        for (var i = 0; i < 16; i++)
        {
            sb.Append(ST(i) ? "1" : "0");
        }

        sb.Append(" CD:");
        for (var i = 0; i < 16; i++)
        {
            sb.Append(CD(i) ? "1" : "0");
        }

        return sb.ToString();
    }
}
