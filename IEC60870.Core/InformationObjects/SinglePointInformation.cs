//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 单点信息信息对象（M_SP_NA_1 / _TA_1 / _TB_1 / M_PS_NA_1）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;

namespace IEC60870.Core;

/// <summary>
/// 单点信息（M_SP_NA_1）：SIQ 字节 bit0 = 状态，bit4-7 = 质量位。
/// </summary>
public class SinglePointInformation : InformationObject
{
    private bool _value;
    private QualityDescriptor _quality;

    /// <inheritdoc/>
    public override TypeID Type => TypeID.M_SP_NA_1;

    /// <inheritdoc/>
    public override bool SupportsSequence => true;

    /// <summary>开关状态（false=分，true=合）。</summary>
    public bool Value
    {
        get => _value;
        set => _value = value;
    }

    /// <summary>质量描述符。</summary>
    public QualityDescriptor Quality => _quality;

    /// <summary>以地址、状态与质量构造。</summary>
    public SinglePointInformation(int objectAddress, bool value, QualityDescriptor quality)
        : base(objectAddress)
    {
        _value = value;
        _quality = quality;
    }

    /// <summary>复制构造。</summary>
    public SinglePointInformation(SinglePointInformation original)
        : base(original.ObjectAddress)
    {
        _value = original.Value;
        _quality = original.Quality;
    }

    internal SinglePointInformation(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
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

        // SIQ：bit0 状态位，bit4-7 质量位
        var siq = msg[startIndex++];

        _value = (siq & 0x01) == 0x01;
        _quality = new QualityDescriptor((byte)(siq & 0xf0));
    }

    internal override bool HasAsduWriterBody => true;

    /// <inheritdoc/>
    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        var siq = _quality.EncodedValue;
        if (_value)
        {
            siq |= 0x01;
        }

        w.WriteByte(siq);
    }
}

/// <summary>
/// 带 CP24Time2a 时标的单点信息（M_SP_TA_1）。
/// </summary>
public class SinglePointWithCP24Time2a : SinglePointInformation
{
    private CP24Time2a _timestamp;

    /// <inheritdoc/>
    public override TypeID Type => TypeID.M_SP_TA_1;

    /// <inheritdoc/>
    public override bool SupportsSequence => false;

    /// <summary>事件时标（CP24Time2a）。</summary>
    public CP24Time2a Timestamp => _timestamp;

    public SinglePointWithCP24Time2a(int objectAddress, bool value, QualityDescriptor quality, CP24Time2a timestamp)
        : base(objectAddress, value, quality)
    {
        _timestamp = timestamp;
    }

    /// <summary>复制构造。</summary>
    public SinglePointWithCP24Time2a(SinglePointWithCP24Time2a original)
        : base(original)
    {
        _timestamp = new CP24Time2a(original.Timestamp);
    }

    internal SinglePointWithCP24Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
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

        startIndex += 1; // SIQ

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
/// 带 CP56Time2a 时标的单点信息（M_SP_TB_1）。
/// </summary>
public class SinglePointWithCP56Time2a : SinglePointInformation
{
    private CP56Time2a _timestamp;

    /// <inheritdoc/>
    public override TypeID Type => TypeID.M_SP_TB_1;

    /// <inheritdoc/>
    public override bool SupportsSequence => false;

    /// <summary>事件时标（CP56Time2a）。</summary>
    public CP56Time2a Timestamp => _timestamp;

    public SinglePointWithCP56Time2a(int objectAddress, bool value, QualityDescriptor quality, CP56Time2a timestamp)
        : base(objectAddress, value, quality)
    {
        _timestamp = timestamp;
    }

    /// <summary>复制构造。</summary>
    public SinglePointWithCP56Time2a(SinglePointWithCP56Time2a original)
        : base(original)
    {
        _timestamp = new CP56Time2a(original._timestamp);
    }

    internal SinglePointWithCP56Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
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

        startIndex += 1; // SIQ

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

/// <summary>
/// 带状态变位检测的成组单点信息（M_PS_NA_1）：
/// 4 字节状态/变位检测 + 1 字节质量。
/// </summary>
public class PackedSinglePointWithSCD : InformationObject
{
    private StatusAndStatusChangeDetection _scd;
    private QualityDescriptor _qds;

    /// <inheritdoc/>
    public override TypeID Type => TypeID.M_PS_NA_1;

    /// <inheritdoc/>
    public override bool SupportsSequence => true;

    /// <summary>状态与变位检测（SCD）。</summary>
    public StatusAndStatusChangeDetection SCD
    {
        get => _scd;
        set => _scd = value;
    }

    /// <summary>质量描述符。</summary>
    public QualityDescriptor QDS
    {
        get => _qds;
        set => _qds = value;
    }

    public PackedSinglePointWithSCD(int objectAddress, StatusAndStatusChangeDetection scd, QualityDescriptor quality)
        : base(objectAddress)
    {
        _scd = scd;
        _qds = quality;
    }

    /// <summary>复制构造。</summary>
    public PackedSinglePointWithSCD(PackedSinglePointWithSCD original)
        : base(original.ObjectAddress)
    {
        _scd = new StatusAndStatusChangeDetection(original.SCD);
        _qds = new QualityDescriptor(original.QDS);
    }

    internal PackedSinglePointWithSCD(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
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

        _scd = new StatusAndStatusChangeDetection(msg, startIndex);
        startIndex += 4;

        _qds = new QualityDescriptor(msg[startIndex++]);
    }

    internal override bool HasAsduWriterBody => true;

    /// <inheritdoc/>
    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteBytes(_scd.AsSpan());
        w.WriteByte(_qds.EncodedValue);
    }
}
