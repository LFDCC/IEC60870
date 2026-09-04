//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 32 位位串（M_BO_NA_1 / M_BO_TA_1 / M_BO_TB_1）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;

namespace IEC60870.Core;

/// <summary>
/// 32 位位串信息体（M_BO_NA_1）。编码：4 字节小端 UInt32 + 1 字节 QDS。
/// </summary>
public class Bitstring32 : InformationObject
{
    private UInt32 _value;
    private QualityDescriptor _quality;

    /// <summary>32 位位串值。</summary>
    public UInt32 Value => _value;

    /// <summary>质量描述符。</summary>
    public QualityDescriptor Quality => _quality;

    public override TypeID Type => TypeID.M_BO_NA_1;

    public override bool SupportsSequence => true;

    public Bitstring32(int ioa, UInt32 value, QualityDescriptor quality)
        : base(ioa)
    {
        _value = value;
        _quality = quality;
    }

    public Bitstring32(Bitstring32 original)
        : base(original.ObjectAddress)
    {
        _value = original._value;
        _quality = new QualityDescriptor(original._quality);
    }

    internal Bitstring32(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
        : base(parameters, msg, startIndex, isSequence)
    {
        if (!isSequence)
        {
            startIndex += parameters.SizeOfIOA; /* 跳过信息体地址 */
        }

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("Message too small");
        }

        _value = msg[startIndex]
                 | ((uint)msg[startIndex + 1] << 8)
                 | ((uint)msg[startIndex + 2] << 16)
                 | ((uint)msg[startIndex + 3] << 24);

        _quality = new QualityDescriptor(msg[startIndex + 4]);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteByte((byte)_value);
        w.WriteByte((byte)(_value >> 8));
        w.WriteByte((byte)(_value >> 16));
        w.WriteByte((byte)(_value >> 24));

        w.WriteByte(_quality.EncodedValue);
    }
}

/// <summary>
/// 带 CP24Time2a 时标的 32 位位串信息体（M_BO_TA_1）。
/// </summary>
public class Bitstring32WithCP24Time2a : Bitstring32
{
    private CP24Time2a _timestamp;

    /// <summary>CP24Time2a 时标。</summary>
    public CP24Time2a Timestamp => _timestamp;

    public override TypeID Type => TypeID.M_BO_TA_1;

    public override bool SupportsSequence => false;

    public Bitstring32WithCP24Time2a(int ioa, UInt32 value, QualityDescriptor quality, CP24Time2a timestamp)
        : base(ioa, value, quality)
    {
        _timestamp = timestamp;
    }

    public Bitstring32WithCP24Time2a(Bitstring32WithCP24Time2a original)
        : base(original)
    {
        _timestamp = new CP24Time2a(original._timestamp);
    }

    internal Bitstring32WithCP24Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
        : base(parameters, msg, startIndex, isSequence)
    {
        if (!isSequence)
        {
            startIndex += parameters.SizeOfIOA; /* 跳过信息体地址 */
        }

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("Message too small");
        }

        startIndex += 5; /* 跳过 value(4) + QDS(1) */

        _timestamp = new CP24Time2a(msg, startIndex);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteBytes(_timestamp.AsSpan());
    }
}

/// <summary>
/// 带 CP56Time2a 时标的 32 位位串信息体（M_BO_TB_1）。
/// </summary>
public class Bitstring32WithCP56Time2a : Bitstring32
{
    private CP56Time2a _timestamp;

    /// <summary>CP56Time2a 时标。</summary>
    public CP56Time2a Timestamp => _timestamp;

    public override TypeID Type => TypeID.M_BO_TB_1;

    public override bool SupportsSequence => false;

    public Bitstring32WithCP56Time2a(int ioa, UInt32 value, QualityDescriptor quality, CP56Time2a timestamp)
        : base(ioa, value, quality)
    {
        _timestamp = timestamp;
    }

    public Bitstring32WithCP56Time2a(Bitstring32WithCP56Time2a original)
        : base(original)
    {
        _timestamp = new CP56Time2a(original._timestamp);
    }

    internal Bitstring32WithCP56Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
        : base(parameters, msg, startIndex, isSequence)
    {
        if (!isSequence)
        {
            startIndex += parameters.SizeOfIOA; /* 跳过信息体地址 */
        }

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("Message too small");
        }

        startIndex += 5; /* 跳过 value(4) + QDS(1) */

        _timestamp = new CP56Time2a(msg, startIndex);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteBytes(_timestamp.AsSpan());
    }
}