//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 短浮点测量值信息对象（M_ME_NC_1 / _TC_1 / _TF_1）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;
using System.Buffers.Binary;

namespace IEC60870.Core;

/// <summary>
/// 短浮点测量值（M_ME_NC_1）：IEEE 754 单精度小端 4 字节 + 1 字节质量描述符。
/// </summary>
public class MeasuredValueShort : InformationObject
{
    private float _value;
    private QualityDescriptor _quality;

    /// <inheritdoc/>
    public override TypeID Type => TypeID.M_ME_NC_1;

    /// <inheritdoc/>
    public override bool SupportsSequence => true;

    /// <summary>浮点测量值。</summary>
    public float Value
    {
        get => _value;
        set => _value = value;
    }

    /// <summary>质量描述符。</summary>
    public QualityDescriptor Quality => _quality;

    public MeasuredValueShort(int objectAddress, float value, QualityDescriptor quality)
        : base(objectAddress)
    {
        _value = value;
        _quality = quality;
    }

    /// <summary>复制构造。</summary>
    public MeasuredValueShort(MeasuredValueShort original)
        : base(original.ObjectAddress)
    {
        _value = original._value;
        _quality = new QualityDescriptor(original.Quality);
    }

    internal MeasuredValueShort(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
        : base(parameters, msg, startIndex, isSequence)
    {
        if (!isSequence)
        {
            startIndex += parameters.SizeOfIOA; // 跳过 IOA
        }

        EnsureBodyAvailable(msg, startIndex);

        // IEEE 754 单精度，线上格式固定小端（BinaryPrimitives 处理平台字节序）。
        _value = BinaryPrimitives.ReadSingleLittleEndian(msg.Slice(startIndex, 4));
        startIndex += 4;

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

        // IEEE 754 单精度按小端 4 字节写入（线上格式固定小端）。
        w.WriteIntLittleEndian(BitConverter.SingleToInt32Bits(_value), 4);

        w.WriteByte(_quality.EncodedValue);
    }
}

/// <summary>
/// 带 CP24Time2a 时标的短浮点测量值（M_ME_TC_1）。
/// </summary>
public class MeasuredValueShortWithCP24Time2a : MeasuredValueShort
{
    private CP24Time2a _timestamp;

    /// <inheritdoc/>
    public override TypeID Type => TypeID.M_ME_TC_1;

    /// <inheritdoc/>
    public override bool SupportsSequence => false;

    /// <summary>事件时标（CP24Time2a）。</summary>
    public CP24Time2a Timestamp => _timestamp;

    public MeasuredValueShortWithCP24Time2a(int objectAddress, float value, QualityDescriptor quality, CP24Time2a timestamp)
        : base(objectAddress, value, quality)
    {
        _timestamp = timestamp;
    }

    /// <summary>复制构造。</summary>
    public MeasuredValueShortWithCP24Time2a(MeasuredValueShortWithCP24Time2a original)
        : base(original)
    {
        _timestamp = new CP24Time2a(original._timestamp);
    }

    internal MeasuredValueShortWithCP24Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
        : base(parameters, msg, startIndex, isSequence)
    {
        if (!isSequence)
        {
            startIndex += parameters.SizeOfIOA; // 跳过 IOA
        }

        EnsureBodyAvailable(msg, startIndex);

        startIndex += 5; // 浮点值 + QDS

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
/// 带 CP56Time2a 时标的短浮点测量值（M_ME_TF_1）。
/// </summary>
public class MeasuredValueShortWithCP56Time2a : MeasuredValueShort
{
    private CP56Time2a _timestamp;

    /// <inheritdoc/>
    public override TypeID Type => TypeID.M_ME_TF_1;

    /// <inheritdoc/>
    public override bool SupportsSequence => false;

    /// <summary>事件时标（CP56Time2a）。</summary>
    public CP56Time2a Timestamp => _timestamp;

    public MeasuredValueShortWithCP56Time2a(int objectAddress, float value, QualityDescriptor quality, CP56Time2a timestamp)
        : base(objectAddress, value, quality)
    {
        _timestamp = timestamp;
    }

    /// <summary>复制构造。</summary>
    public MeasuredValueShortWithCP56Time2a(MeasuredValueShortWithCP56Time2a original)
        : base(original)
    {
        _timestamp = new CP56Time2a(original._timestamp);
    }

    internal MeasuredValueShortWithCP56Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
        : base(parameters, msg, startIndex, isSequence)
    {
        if (!isSequence)
        {
            startIndex += parameters.SizeOfIOA; // 跳过 IOA
        }

        EnsureBodyAvailable(msg, startIndex);

        startIndex += 5; // 浮点值 + QDS

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
