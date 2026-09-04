//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 步位置信息（M_ST_NA_1 / M_ST_TA_1 / M_ST_TB_1）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;

namespace IEC60870.Core;

/// <summary>
/// 步位置信息（M_ST_NA_1）。VTI 字节编码：bit0-6=位置值（-64 … +63，补码风格），
/// bit7=瞬变标志位（T）。
/// </summary>
public class StepPositionInformation : InformationObject
{
    // VTI 位域。
    private const byte MaskTransient = 0x80;
    private const byte MaskValue = 0x7f;
    private const int ValueMax = 63;
    private const int ValueMin = -64;

    private int _value;
    private bool _isTransient;
    private QualityDescriptor _quality;

    public override TypeID Type => TypeID.M_ST_NA_1;

    public override bool SupportsSequence => true;

    /// <summary>步位置（-64 … +63）。超出范围将被钳位。</summary>
    public int Value
    {
        get => _value;
        set => _value = value > ValueMax ? ValueMax : (value < ValueMin ? ValueMin : value);
    }

    /// <summary>瞬变标志：步位置正在过渡中。</summary>
    public bool Transient
    {
        get => _isTransient;
        set => _isTransient = value;
    }

    /// <summary>质量描述符。</summary>
    public QualityDescriptor Quality => _quality;

    public StepPositionInformation(int ioa, int value, bool isTransient, QualityDescriptor quality)
        : base(ioa)
    {
        if (value < ValueMin || value > ValueMax)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "value has to be in range -64 .. 63");
        }

        _value = value;
        _isTransient = isTransient;
        _quality = quality;
    }

    public StepPositionInformation(StepPositionInformation original)
        : base(original.ObjectAddress)
    {
        _value = original._value;
        _isTransient = original._isTransient;
        _quality = new QualityDescriptor(original._quality);
    }

    internal StepPositionInformation(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
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

        var vti = msg[startIndex++];

        _isTransient = (vti & MaskTransient) != 0;

        var raw = vti & MaskValue;

        _value = raw > ValueMax ? raw - 128 : raw;

        _quality = new QualityDescriptor(msg[startIndex]);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        var vti = (byte)(_value < 0 ? _value + 128 : _value);

        if (_isTransient)
        {
            vti |= MaskTransient;
        }

        w.WriteByte(vti);
        w.WriteByte(_quality.EncodedValue);
    }
}

/// <summary>
/// 带 CP24Time2a 时标的步位置信息（M_ST_TA_1）。
/// </summary>
public class StepPositionWithCP24Time2a : StepPositionInformation
{
    private CP24Time2a _timestamp;

    /// <summary>CP24Time2a 时标。</summary>
    public CP24Time2a Timestamp
    {
        get => _timestamp;
        set => _timestamp = value;
    }

    public override TypeID Type => TypeID.M_ST_TA_1;

    public override bool SupportsSequence => false;

    public StepPositionWithCP24Time2a(int ioa, int value, bool isTransient, QualityDescriptor quality, CP24Time2a timestamp)
        : base(ioa, value, isTransient, quality)
    {
        _timestamp = timestamp;
    }

    public StepPositionWithCP24Time2a(StepPositionWithCP24Time2a original)
        : base(original)
    {
        _timestamp = new CP24Time2a(original._timestamp);
    }

    internal StepPositionWithCP24Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
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

        startIndex += 2; /* 跳过 VTI + QDS */

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
/// 带 CP56Time2a 时标的步位置信息（M_ST_TB_1）。
/// </summary>
public class StepPositionWithCP56Time2a : StepPositionInformation
{
    private CP56Time2a _timestamp;

    /// <summary>CP56Time2a 时标。</summary>
    public CP56Time2a Timestamp
    {
        get => _timestamp;
        set => _timestamp = value;
    }

    public override TypeID Type => TypeID.M_ST_TB_1;

    public override bool SupportsSequence => false;

    public StepPositionWithCP56Time2a(int ioa, int value, bool isTransient, QualityDescriptor quality, CP56Time2a timestamp)
        : base(ioa, value, isTransient, quality)
    {
        _timestamp = timestamp;
    }

    public StepPositionWithCP56Time2a(StepPositionWithCP56Time2a original)
        : base(original)
    {
        _timestamp = new CP56Time2a(original._timestamp);
    }

    internal StepPositionWithCP56Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
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

        startIndex += 2; /* 跳过 VTI + QDS */

        _timestamp = new CP56Time2a(msg, startIndex);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteBytes(_timestamp.AsSpan());
    }
}