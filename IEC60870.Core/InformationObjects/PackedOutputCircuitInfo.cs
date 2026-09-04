//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 打包输出回路信息（M_EP_TC_1 / M_EP_TF_1）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;

namespace IEC60870.Core;

/// <summary>
/// 打包输出回路信息（M_EP_TC_1）。编码：OCI(1) + QDP(1) + CP16Time2a(2) + CP24Time2a(3)。
/// </summary>
public class PackedOutputCircuitInfo : InformationObject
{
    private OutputCircuitInfo _oci;
    private QualityDescriptorP _qdp;
    private CP16Time2a _operatingTime;
    private CP24Time2a _timestamp;

    /// <summary>输出回路信息。</summary>
    public OutputCircuitInfo OCI => _oci;

    /// <summary>保护事件质量描述符。</summary>
    public QualityDescriptorP QDP => _qdp;

    /// <summary>动作时间（相对时标）。</summary>
    public CP16Time2a OperatingTime => _operatingTime;

    /// <summary>CP24Time2a 时标。</summary>
    public CP24Time2a Timestamp => _timestamp;

    public override TypeID Type => TypeID.M_EP_TC_1;

    public override bool SupportsSequence => true;

    public PackedOutputCircuitInfo(int objectAddress, OutputCircuitInfo oci, QualityDescriptorP qdp, CP16Time2a operatingTime, CP24Time2a timestamp)
        : base(objectAddress)
    {
        _oci = oci;
        _qdp = qdp;
        _operatingTime = operatingTime;
        _timestamp = timestamp;
    }

    public PackedOutputCircuitInfo(PackedOutputCircuitInfo original)
        : base(original.ObjectAddress)
    {
        _oci = new OutputCircuitInfo(original._oci);
        _qdp = new QualityDescriptorP(original._qdp);
        _operatingTime = new CP16Time2a(original._operatingTime);
        _timestamp = new CP24Time2a(original._timestamp);
    }

    internal PackedOutputCircuitInfo(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
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

        _oci = new OutputCircuitInfo(msg[startIndex++]);
        _qdp = new QualityDescriptorP(msg[startIndex++]);
        _operatingTime = new CP16Time2a(msg, startIndex);
        startIndex += 2;

        _timestamp = new CP24Time2a(msg, startIndex);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteByte(_oci.EncodedValue);
        w.WriteByte(_qdp.EncodedValue);
        w.WriteBytes(_operatingTime.AsSpan());
        w.WriteBytes(_timestamp.AsSpan());
    }
}

/// <summary>
/// 带 CP56Time2a 时标的打包输出回路信息（M_EP_TF_1）。
/// 编码：OCI(1) + QDP(1) + CP16Time2a(2) + CP56Time2a(7)。
/// </summary>
public class PackedOutputCircuitInfoWithCP56Time2a : InformationObject
{
    private OutputCircuitInfo _oci;
    private QualityDescriptorP _qdp;
    private CP16Time2a _operatingTime;
    private CP56Time2a _timestamp;

    /// <summary>输出回路信息。</summary>
    public OutputCircuitInfo OCI => _oci;

    /// <summary>保护事件质量描述符。</summary>
    public QualityDescriptorP QDP => _qdp;

    /// <summary>动作时间（相对时标）。</summary>
    public CP16Time2a OperatingTime => _operatingTime;

    /// <summary>CP56Time2a 时标。</summary>
    public CP56Time2a Timestamp => _timestamp;

    public override TypeID Type => TypeID.M_EP_TF_1;

    public override bool SupportsSequence => true;

    public PackedOutputCircuitInfoWithCP56Time2a(int objectAddress, OutputCircuitInfo oci, QualityDescriptorP qdp, CP16Time2a operatingTime, CP56Time2a timestamp)
        : base(objectAddress)
    {
        _oci = oci;
        _qdp = qdp;
        _operatingTime = operatingTime;
        _timestamp = timestamp;
    }

    public PackedOutputCircuitInfoWithCP56Time2a(PackedOutputCircuitInfoWithCP56Time2a original)
        : base(original.ObjectAddress)
    {
        _oci = new OutputCircuitInfo(original._oci);
        _qdp = new QualityDescriptorP(original._qdp);
        _operatingTime = new CP16Time2a(original._operatingTime);
        _timestamp = new CP56Time2a(original._timestamp);
    }

    internal PackedOutputCircuitInfoWithCP56Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
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

        _oci = new OutputCircuitInfo(msg[startIndex++]);
        _qdp = new QualityDescriptorP(msg[startIndex++]);
        _operatingTime = new CP16Time2a(msg, startIndex);
        startIndex += 2;

        _timestamp = new CP56Time2a(msg, startIndex);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteByte(_oci.EncodedValue);
        w.WriteByte(_qdp.EncodedValue);
        w.WriteBytes(_operatingTime.AsSpan());
        w.WriteBytes(_timestamp.AsSpan());
    }
}