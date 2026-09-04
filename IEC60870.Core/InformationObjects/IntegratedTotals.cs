//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 累计量（M_IT_NA_1 / M_IT_TA_1 / M_IT_TB_1）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;

namespace IEC60870.Core;

/// <summary>
/// 累计量信息体（M_IT_NA_1）。编码为 BinaryCounterReading（BCR，5 字节）。
/// </summary>
public class IntegratedTotals : InformationObject
{
    private BinaryCounterReading _bcr;

    /// <summary>二进制计数器读数。</summary>
    public BinaryCounterReading BCR => _bcr;

    public override TypeID Type => TypeID.M_IT_NA_1;

    public override bool SupportsSequence => true;

    public IntegratedTotals(int ioa, BinaryCounterReading bcr)
        : base(ioa)
    {
        _bcr = bcr;
    }

    public IntegratedTotals(IntegratedTotals original)
        : base(original.ObjectAddress)
    {
        _bcr = new BinaryCounterReading(original._bcr);
    }

    internal IntegratedTotals(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
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

        _bcr = new BinaryCounterReading(msg, startIndex);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteBytes(_bcr.AsSpan());
    }
}

/// <summary>
/// 带 CP24Time2a 时标的累计量信息体（M_IT_TA_1）。
/// </summary>
public class IntegratedTotalsWithCP24Time2a : IntegratedTotals
{
    private CP24Time2a _timestamp;

    /// <summary>CP24Time2a 时标。</summary>
    public CP24Time2a Timestamp => _timestamp;

    public override TypeID Type => TypeID.M_IT_TA_1;

    public override bool SupportsSequence => true;

    public IntegratedTotalsWithCP24Time2a(int ioa, BinaryCounterReading bcr, CP24Time2a timestamp)
        : base(ioa, bcr)
    {
        _timestamp = timestamp;
    }

    public IntegratedTotalsWithCP24Time2a(IntegratedTotalsWithCP24Time2a original)
        : base(original)
    {
        _timestamp = new CP24Time2a(original._timestamp);
    }

    public IntegratedTotalsWithCP24Time2a(IntegratedTotals original)
        : base(original)
    {
        _timestamp = new CP24Time2a();
    }

    internal IntegratedTotalsWithCP24Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
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

        startIndex += 5; /* 跳过 BCR */

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
/// 带 CP56Time2a 时标的累计量信息体（M_IT_TB_1）。
/// </summary>
public class IntegratedTotalsWithCP56Time2a : IntegratedTotals
{
    private CP56Time2a _timestamp;

    /// <summary>CP56Time2a 时标。</summary>
    public CP56Time2a Timestamp => _timestamp;

    public override TypeID Type => TypeID.M_IT_TB_1;

    public override bool SupportsSequence => true;

    public IntegratedTotalsWithCP56Time2a(int ioa, BinaryCounterReading bcr, CP56Time2a timestamp)
        : base(ioa, bcr)
    {
        _timestamp = timestamp;
    }

    public IntegratedTotalsWithCP56Time2a(IntegratedTotalsWithCP56Time2a original)
        : base(original)
    {
        _timestamp = new CP56Time2a(original._timestamp);
    }

    public IntegratedTotalsWithCP56Time2a(IntegratedTotals original)
        : base(original)
    {
        _timestamp = new CP56Time2a(DateTime.Now);
    }

    internal IntegratedTotalsWithCP56Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
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

        startIndex += 5; /* 跳过 BCR */

        _timestamp = new CP56Time2a(msg, startIndex);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteBytes(_timestamp.AsSpan());
    }
}