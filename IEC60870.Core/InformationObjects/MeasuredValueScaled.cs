//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 标度化测量值信息对象（M_ME_NB_1 / _TB_1 / _TE_1）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;

namespace IEC60870.Core;

/// <summary>
/// 标度化测量值（M_ME_NB_1）：2 字节标度值（<see cref="ScaledValue"/>）+ 1 字节质量描述符。
/// </summary>
public class MeasuredValueScaled : InformationObject
{
    private ScaledValue _scaledValue;
    private QualityDescriptor _quality;

    /// <inheritdoc/>
    public override TypeID Type => TypeID.M_ME_NB_1;

    /// <inheritdoc/>
    public override bool SupportsSequence => true;

    /// <summary>标度化值（有符号 16 位）。</summary>
    public ScaledValue ScaledValue => _scaledValue;

    /// <summary>质量描述符。</summary>
    public QualityDescriptor Quality => _quality;

    /// <summary>构造：地址 + 标度值 + 质量。</summary>
    /// <param name="objectAddress">信息对象地址。</param>
    /// <param name="value">标度值（-32768…32767）。</param>
    /// <param name="quality">质量描述符（IEC 60870-5-101 §7.2.6.3）。</param>
    public MeasuredValueScaled(int objectAddress, int value, QualityDescriptor quality)
        : base(objectAddress)
    {
        _scaledValue = new ScaledValue(value);
        _quality = quality;
    }

    /// <summary>复制构造。</summary>
    public MeasuredValueScaled(MeasuredValueScaled original)
        : base(original.ObjectAddress)
    {
        _scaledValue = new ScaledValue(original.ScaledValue);
        _quality = new QualityDescriptor(original._quality);
    }

    internal MeasuredValueScaled(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
        : base(parameters, msg, startIndex, isSequence)
    {
        if (!isSequence)
        {
            startIndex += parameters.SizeOfIOA; // 跳过 IOA
        }

        EnsureBodyAvailable(msg, startIndex);

        _scaledValue = new ScaledValue(msg, startIndex);
        startIndex += 2;

        _quality = new QualityDescriptor(msg[startIndex++]);
    }

    internal void EnsureBodyAvailable(ReadOnlySpan<byte> msg, int startIndex)
    {
        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("报文长度不足以解析信息对象");
        }
    }

    internal override bool HasAsduWriterBody => true;

    /// <inheritdoc/>
    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteBytes(_scaledValue.AsSpan());
        w.WriteByte(_quality.EncodedValue);
    }
}

/// <summary>
/// 带 CP24Time2a 时标的标度化测量值（M_ME_TB_1）。
/// </summary>
public class MeasuredValueScaledWithCP24Time2a : MeasuredValueScaled
{
    private CP24Time2a _timestamp;

    /// <inheritdoc/>
    public override TypeID Type => TypeID.M_ME_TB_1;

    /// <inheritdoc/>
    public override bool SupportsSequence => false;

    /// <summary>事件时标（CP24Time2a）。</summary>
    public CP24Time2a Timestamp => _timestamp;

    public MeasuredValueScaledWithCP24Time2a(int objectAddress, int value, QualityDescriptor quality, CP24Time2a timestamp)
        : base(objectAddress, value, quality)
    {
        _timestamp = timestamp;
    }

    /// <summary>复制构造。</summary>
    public MeasuredValueScaledWithCP24Time2a(MeasuredValueScaledWithCP24Time2a original)
        : base(original)
    {
        _timestamp = new CP24Time2a(original._timestamp);
    }

    internal MeasuredValueScaledWithCP24Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
        : base(parameters, msg, startIndex, isSequence)
    {
        if (!isSequence)
        {
            startIndex += parameters.SizeOfIOA; // 跳过 IOA
        }

        EnsureBodyAvailable(msg, startIndex);

        startIndex += 3; // 标度值 + QDS

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
/// 带 CP56Time2a 时标的标度化测量值（M_ME_TE_1）。
/// </summary>
public class MeasuredValueScaledWithCP56Time2a : MeasuredValueScaled
{
    private CP56Time2a _timestamp;

    /// <inheritdoc/>
    public override TypeID Type => TypeID.M_ME_TE_1;

    /// <inheritdoc/>
    public override bool SupportsSequence => false;

    /// <summary>事件时标（CP56Time2a）。</summary>
    public CP56Time2a Timestamp => _timestamp;

    public MeasuredValueScaledWithCP56Time2a(int objectAddress, int value, QualityDescriptor quality, CP56Time2a timestamp)
        : base(objectAddress, value, quality)
    {
        _timestamp = timestamp;
    }

    /// <summary>复制构造。</summary>
    public MeasuredValueScaledWithCP56Time2a(MeasuredValueScaledWithCP56Time2a original)
        : base(original)
    {
        _timestamp = new CP56Time2a(original._timestamp);
    }

    internal MeasuredValueScaledWithCP56Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
        : base(parameters, msg, startIndex, isSequence)
    {
        if (!isSequence)
        {
            startIndex += parameters.SizeOfIOA; // 跳过 IOA
        }

        EnsureBodyAvailable(msg, startIndex);

        startIndex += 3; // 标度值 + QDS

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
