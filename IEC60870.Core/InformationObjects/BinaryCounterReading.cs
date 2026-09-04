//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 二进制计数器读数（BCR，5 字节编码）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;

namespace IEC60870.Core;

/// <summary>
/// 二进制计数器读数（Binary Counter Reading），用于累计量传输。
/// 编码：4 字节小端有符号整数 + 1 字节标志（bit0-4=序列号，bit5=进位，bit6=已调整，bit7=无效）。
/// </summary>
public class BinaryCounterReading
{
    private const int EncodedSize = 5;

    // 标志字节位域。
    private const byte MaskSequenceNumber = 0x1f;
    private const byte MaskCarry = 0x20;
    private const byte MaskAdjusted = 0x40;
    private const byte MaskInvalid = 0x80;

    private readonly byte[] _encodedValue = new byte[EncodedSize];

    /// <summary>返回编码字节的副本。</summary>
    public byte[] GetEncodedValue() => _encodedValue;

    /// <summary>零分配方式返回编码字节切片。</summary>
    public ReadOnlySpan<byte> AsSpan() => _encodedValue.AsSpan();

    /// <summary>计数器值（有符号 32 位整数，小端编码）。</summary>
    public Int32 Value
    {
        get
        {
            var raw = _encodedValue[0]
                       | (_encodedValue[1] << 8)
                       | (_encodedValue[2] << 16)
                       | (_encodedValue[3] << 24);
            return raw;
        }
        set
        {
            _encodedValue[0] = (byte)value;
            _encodedValue[1] = (byte)(value >> 8);
            _encodedValue[2] = (byte)(value >> 16);
            _encodedValue[3] = (byte)(value >> 24);
        }
    }

    /// <summary>序列号（0 … 31）。</summary>
    public int SequenceNumber
    {
        get => _encodedValue[4] & MaskSequenceNumber;
        set
        {
            _encodedValue[4] = (byte)((_encodedValue[4] & ~MaskSequenceNumber) | (value & MaskSequenceNumber));
        }
    }

    /// <summary>进位标志（计数器溢出）。</summary>
    public bool Carry
    {
        get => (_encodedValue[4] & MaskCarry) != 0;
        set
        {
            if (value)
            {
                _encodedValue[4] |= MaskCarry;
            }
            else
            {
                _encodedValue[4] &= 0xdf;
            }
        }
    }

    /// <summary>已调整标志（读数经人工或自动调整）。</summary>
    public bool Adjusted
    {
        get => (_encodedValue[4] & MaskAdjusted) != 0;
        set
        {
            if (value)
            {
                _encodedValue[4] |= MaskAdjusted;
            }
            else
            {
                _encodedValue[4] &= 0xbf;
            }
        }
    }

    /// <summary>无效标志（读数不可用）。</summary>
    public bool Invalid
    {
        get => (_encodedValue[4] & MaskInvalid) != 0;
        set
        {
            if (value)
            {
                _encodedValue[4] |= MaskInvalid;
            }
            else
            {
                _encodedValue[4] &= 0x7f;
            }
        }
    }

    /// <summary>从报文切片解析 5 字节 BCR。</summary>
    /// <exception cref="ASDUParsingException">报文剩余长度不足时抛出。</exception>
    public BinaryCounterReading(ReadOnlySpan<byte> msg, int startIndex)
    {
        if (msg.Length < startIndex + EncodedSize)
        {
            throw new ASDUParsingException("Message too small for parsing BinaryCounterReading");
        }

        for (var i = 0; i < EncodedSize; i++)
        {
            _encodedValue[i] = msg[startIndex + i];
        }
    }

    /// <summary>构造零值 BCR。</summary>
    public BinaryCounterReading()
    {
    }

    /// <summary>以另一实例复制构造。</summary>
    public BinaryCounterReading(BinaryCounterReading original)
    {
        for (var i = 0; i < EncodedSize; i++)
        {
            _encodedValue[i] = original._encodedValue[i];
        }
    }
}