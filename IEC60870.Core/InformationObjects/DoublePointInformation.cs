//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 双点信息（M_DP_NA_1 / M_DP_TA_1 / M_DP_TB_1）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;

namespace IEC60870.Core;

/// <summary>
/// 双点状态值（DIQ 的低 2 位）：0=中间状态，1=分，2=合，3=不确定。
/// </summary>
public enum DoublePointValue
{
    INTERMEDIATE = 0,
    OFF = 1,
    ON = 2,
    INDETERMINATE = 3
}

/// <summary>
/// 双点信息（M_DP_NA_1）。DIQ 字节低 2 位承载状态值，高 4 位承载质量描述符。
/// </summary>
public class DoublePointInformation : InformationObject
{
    // DIQ 位域划分。
    private const byte MaskValue = 0x03;
    private const byte MaskQuality = 0xf0;

    private DoublePointValue _value;
    private QualityDescriptor _quality;

    /// <summary>双点状态值。</summary>
    public DoublePointValue Value => _value;

    /// <summary>质量描述符。</summary>
    public QualityDescriptor Quality => _quality;

    public override TypeID Type => TypeID.M_DP_NA_1;

    public override bool SupportsSequence => true;

    public DoublePointInformation(int ioa, DoublePointValue value, QualityDescriptor quality)
        : base(ioa)
    {
        _value = value;
        _quality = quality;
    }

    public DoublePointInformation(DoublePointInformation original)
        : base(original.ObjectAddress)
    {
        _value = original._value;
        _quality = new QualityDescriptor(original._quality);
    }

    internal DoublePointInformation(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
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

        var diq = msg[startIndex];

        _value = (DoublePointValue)(diq & MaskValue);
        _quality = new QualityDescriptor((byte)(diq & MaskQuality));
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteByte((byte)(_quality.EncodedValue | (byte)_value));
    }
}

/// <summary>
/// 带 CP24Time2a 时标的双点信息（M_DP_TA_1）。
/// </summary>
public class DoublePointWithCP24Time2a : DoublePointInformation
{
    private CP24Time2a _timestamp;

    /// <summary>CP24Time2a 时标。</summary>
    public CP24Time2a Timestamp => _timestamp;

    public override TypeID Type => TypeID.M_DP_TA_1;

    public override bool SupportsSequence => false;

    public DoublePointWithCP24Time2a(int ioa, DoublePointValue value, QualityDescriptor quality, CP24Time2a timestamp)
        : base(ioa, value, quality)
    {
        _timestamp = timestamp;
    }

    public DoublePointWithCP24Time2a(DoublePointWithCP24Time2a original)
        : base(original)
    {
        _timestamp = new CP24Time2a(original._timestamp);
    }

    internal DoublePointWithCP24Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
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

        startIndex += 1; /* 跳过 DIQ */

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
/// 带 CP56Time2a 时标的双点信息（M_DP_TB_1）。
/// </summary>
public class DoublePointWithCP56Time2a : DoublePointInformation
{
    private CP56Time2a _timestamp;

    /// <summary>CP56Time2a 时标。</summary>
    public CP56Time2a Timestamp => _timestamp;

    public override TypeID Type => TypeID.M_DP_TB_1;

    public override bool SupportsSequence => false;

    public DoublePointWithCP56Time2a(int ioa, DoublePointValue value, QualityDescriptor quality, CP56Time2a timestamp)
        : base(ioa, value, quality)
    {
        _timestamp = timestamp;
    }

    public DoublePointWithCP56Time2a(DoublePointWithCP56Time2a original)
        : base(original)
    {
        _timestamp = new CP56Time2a(original._timestamp);
    }

    internal DoublePointWithCP56Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
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

        startIndex += 1; /* 跳过 DIQ */

        _timestamp = new CP56Time2a(msg, startIndex);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteBytes(_timestamp.AsSpan());
    }
}
