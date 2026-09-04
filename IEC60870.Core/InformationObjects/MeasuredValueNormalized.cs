//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 归一化测量值信息对象（M_ME_ND_1 / _NA_1 / _TA_1 / _TD_1）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;

namespace IEC60870.Core;

/// <summary>
/// 归一化测量值，无质量描述（M_ME_ND_1）：仅 2 字节归一化值。
/// </summary>
public class MeasuredValueNormalizedWithoutQuality : InformationObject
{
    private ScaledValue _scaledValue;

    /// <inheritdoc/>
    public override TypeID Type => TypeID.M_ME_ND_1;

    /// <inheritdoc/>
    public override bool SupportsSequence => false;

    /// <summary>原始 16 位编码值。</summary>
    public short RawValue
    {
        get => _scaledValue.ShortValue;
        set => _scaledValue.ShortValue = value;
    }

    /// <summary>归一化浮点值（约 -1.0 … 32767/32768）。</summary>
    public float NormalizedValue
    {
        get => _scaledValue.GetNormalizedValue();
        set => _scaledValue.SetScaledFromNormalizedValue(value);
    }

    /// <summary>以归一化浮点值构造。</summary>
    public MeasuredValueNormalizedWithoutQuality(int objectAddress, float normalizedValue)
        : base(objectAddress)
    {
        _scaledValue = new ScaledValue();
        NormalizedValue = normalizedValue;
    }

    /// <summary>以原始 16 位编码值构造。</summary>
    public MeasuredValueNormalizedWithoutQuality(int objectAddress, short rawValue)
        : base(objectAddress)
    {
        _scaledValue = new ScaledValue(rawValue);
    }

    /// <summary>复制构造。</summary>
    public MeasuredValueNormalizedWithoutQuality(MeasuredValueNormalizedWithoutQuality original)
        : base(original.ObjectAddress)
    {
        _scaledValue = new ScaledValue(original._scaledValue);
    }

    internal MeasuredValueNormalizedWithoutQuality(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
        : base(parameters, msg, startIndex, isSequence)
    {
        if (!isSequence)
        {
            startIndex += parameters.SizeOfIOA; // 跳过 IOA
        }

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("报文长度不足以解析信息对象");
        }

        _scaledValue = new ScaledValue(msg, startIndex);
    }

    internal override bool HasAsduWriterBody => true;

    /// <inheritdoc/>
    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteBytes(_scaledValue.AsSpan());
    }
}

/// <summary>
/// 归一化测量值（M_ME_NA_1）：2 字节归一化值 + 1 字节质量描述符。
/// </summary>
public class MeasuredValueNormalized : MeasuredValueNormalizedWithoutQuality
{
    private QualityDescriptor _quality;

    /// <inheritdoc/>
    public override TypeID Type => TypeID.M_ME_NA_1;

    /// <inheritdoc/>
    public override bool SupportsSequence => true;

    /// <summary>质量描述符。</summary>
    public QualityDescriptor Quality => _quality;

    public MeasuredValueNormalized(int objectAddress, float value, QualityDescriptor quality)
        : base(objectAddress, value)
    {
        _quality = quality;
    }

    public MeasuredValueNormalized(int objectAddress, short value, QualityDescriptor quality)
        : base(objectAddress, value)
    {
        _quality = quality;
    }

    /// <summary>复制构造。</summary>
    public MeasuredValueNormalized(MeasuredValueNormalized original)
        : base(original)
    {
        _quality = new QualityDescriptor(original._quality);
    }

    internal MeasuredValueNormalized(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
        : base(parameters, msg, startIndex, isSequence)
    {
        if (!isSequence)
        {
            startIndex += parameters.SizeOfIOA; // 跳过 IOA
        }

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("报文长度不足以解析信息对象");
        }

        startIndex += 2; // 归一化值

        _quality = new QualityDescriptor(msg[startIndex++]);
    }

    internal override bool HasAsduWriterBody => true;

    /// <inheritdoc/>
    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteByte(_quality.EncodedValue);
    }
}

/// <summary>
/// 带 CP24Time2a 时标的归一化测量值（M_ME_TA_1）。
/// </summary>
public class MeasuredValueNormalizedWithCP24Time2a : MeasuredValueNormalized
{
    private CP24Time2a _timestamp;

    /// <inheritdoc/>
    public override TypeID Type => TypeID.M_ME_TA_1;

    /// <inheritdoc/>
    public override bool SupportsSequence => false;

    /// <summary>事件时标（CP24Time2a）。</summary>
    public CP24Time2a Timestamp => _timestamp;

    public MeasuredValueNormalizedWithCP24Time2a(int objectAddress, float value, QualityDescriptor quality, CP24Time2a timestamp)
        : base(objectAddress, value, quality)
    {
        _timestamp = timestamp;
    }

    public MeasuredValueNormalizedWithCP24Time2a(int objectAddress, short value, QualityDescriptor quality, CP24Time2a timestamp)
        : base(objectAddress, value, quality)
    {
        _timestamp = timestamp;
    }

    /// <summary>复制构造。</summary>
    public MeasuredValueNormalizedWithCP24Time2a(MeasuredValueNormalizedWithCP24Time2a original)
        : base(original)
    {
        _timestamp = new CP24Time2a(original._timestamp);
    }

    internal MeasuredValueNormalizedWithCP24Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
        : base(parameters, msg, startIndex, isSequence)
    {
        if (!isSequence)
        {
            startIndex += parameters.SizeOfIOA; // 跳过 IOA
        }

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("报文长度不足以解析信息对象");
        }

        startIndex += 3; // 归一化值 + QDS

        _timestamp = new CP24Time2a(msg, startIndex);
    }

    internal override bool HasAsduWriterBody => true;

    /// <inheritdoc/>
    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteBytes(_timestamp.AsSpan());
    }
}

/// <summary>
/// 带 CP56Time2a 时标的归一化测量值（M_ME_TD_1）。
/// </summary>
public class MeasuredValueNormalizedWithCP56Time2a : MeasuredValueNormalized
{
    private CP56Time2a _timestamp;

    /// <inheritdoc/>
    public override TypeID Type => TypeID.M_ME_TD_1;

    /// <inheritdoc/>
    public override bool SupportsSequence => false;

    /// <summary>事件时标（CP56Time2a）。</summary>
    public CP56Time2a Timestamp => _timestamp;

    public MeasuredValueNormalizedWithCP56Time2a(int objectAddress, float value, QualityDescriptor quality, CP56Time2a timestamp)
        : base(objectAddress, value, quality)
    {
        _timestamp = timestamp;
    }

    public MeasuredValueNormalizedWithCP56Time2a(int objectAddress, short value, QualityDescriptor quality, CP56Time2a timestamp)
        : base(objectAddress, value, quality)
    {
        _timestamp = timestamp;
    }

    /// <summary>复制构造。</summary>
    public MeasuredValueNormalizedWithCP56Time2a(MeasuredValueNormalizedWithCP56Time2a original)
        : base(original)
    {
        _timestamp = new CP56Time2a(original._timestamp);
    }

    internal MeasuredValueNormalizedWithCP56Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
        : base(parameters, msg, startIndex, isSequence)
    {
        if (!isSequence)
        {
            startIndex += parameters.SizeOfIOA; // 跳过 IOA
        }

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("报文长度不足以解析信息对象");
        }

        startIndex += 3; // 归一化值 + QDS

        _timestamp = new CP56Time2a(msg, startIndex);
    }

    internal override bool HasAsduWriterBody => true;

    /// <inheritdoc/>
    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteBytes(_timestamp.AsSpan());
    }
}
