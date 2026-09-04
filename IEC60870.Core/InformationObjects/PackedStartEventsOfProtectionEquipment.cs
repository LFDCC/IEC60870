//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 打包启动事件信息（M_EP_TB_1 / M_EP_TE_1）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;

namespace IEC60870.Core;

/// <summary>
/// 打包保护设备启动事件信息体（M_EP_TB_1）。
/// 编码：SPE(1) + QDP(1) + CP16Time2a(2) + CP24Time2a(3)。
/// </summary>
public class PackedStartEventsOfProtectionEquipment : InformationObject
{
    private StartEvent _spe;
    private QualityDescriptorP _qdp;
    private CP16Time2a _elapsedTime;
    private CP24Time2a _timestamp;

    /// <summary>启动事件位串。</summary>
    public StartEvent SPE => _spe;

    /// <summary>保护事件质量描述符。</summary>
    public QualityDescriptorP QDP => _qdp;

    /// <summary>相对动作时间（相对时标）。</summary>
    public CP16Time2a ElapsedTime => _elapsedTime;

    /// <summary>CP24Time2a 时标。</summary>
    public CP24Time2a Timestamp => _timestamp;

    public override TypeID Type => TypeID.M_EP_TB_1;

    public override bool SupportsSequence => true;

    public PackedStartEventsOfProtectionEquipment(int objectAddress, StartEvent spe, QualityDescriptorP qdp, CP16Time2a elapsedTime, CP24Time2a timestamp)
        : base(objectAddress)
    {
        _spe = spe;
        _qdp = qdp;
        _elapsedTime = elapsedTime;
        _timestamp = timestamp;
    }

    public PackedStartEventsOfProtectionEquipment(PackedStartEventsOfProtectionEquipment original)
        : base(original.ObjectAddress)
    {
        _spe = new StartEvent(original._spe);
        _qdp = new QualityDescriptorP(original._qdp);
        _elapsedTime = new CP16Time2a(original._elapsedTime);
        _timestamp = new CP24Time2a(original._timestamp);
    }

    internal PackedStartEventsOfProtectionEquipment(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
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

        _spe = new StartEvent(msg[startIndex++]);
        _qdp = new QualityDescriptorP(msg[startIndex++]);
        _elapsedTime = new CP16Time2a(msg, startIndex);
        startIndex += 2;

        _timestamp = new CP24Time2a(msg, startIndex);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteByte(_spe.EncodedValue);
        w.WriteByte(_qdp.EncodedValue);
        w.WriteBytes(_elapsedTime.AsSpan());
        w.WriteBytes(_timestamp.AsSpan());
    }
}

/// <summary>
/// 带 CP56Time2a 时标的打包保护设备启动事件信息体（M_EP_TE_1）。
/// 编码：SPE(1) + QDP(1) + CP16Time2a(2) + CP56Time2a(7)。
/// </summary>
public class PackedStartEventsOfProtectionEquipmentWithCP56Time2a : InformationObject
{
    private StartEvent _spe;
    private QualityDescriptorP _qdp;
    private CP16Time2a _elapsedTime;
    private CP56Time2a _timestamp;

    /// <summary>启动事件位串。</summary>
    public StartEvent SPE => _spe;

    /// <summary>保护事件质量描述符。</summary>
    public QualityDescriptorP QDP => _qdp;

    /// <summary>相对动作时间（相对时标）。</summary>
    public CP16Time2a ElapsedTime => _elapsedTime;

    /// <summary>CP56Time2a 时标。</summary>
    public CP56Time2a Timestamp => _timestamp;

    public override TypeID Type => TypeID.M_EP_TE_1;

    public override bool SupportsSequence => true;

    public PackedStartEventsOfProtectionEquipmentWithCP56Time2a(int objectAddress, StartEvent spe, QualityDescriptorP qdp, CP16Time2a elapsedTime, CP56Time2a timestamp)
        : base(objectAddress)
    {
        _spe = spe;
        _qdp = qdp;
        _elapsedTime = elapsedTime;
        _timestamp = timestamp;
    }

    public PackedStartEventsOfProtectionEquipmentWithCP56Time2a(PackedStartEventsOfProtectionEquipmentWithCP56Time2a original)
        : base(original.ObjectAddress)
    {
        _spe = new StartEvent(original._spe);
        _qdp = new QualityDescriptorP(original._qdp);
        _elapsedTime = new CP16Time2a(original._elapsedTime);
        _timestamp = new CP56Time2a(original._timestamp);
    }

    internal PackedStartEventsOfProtectionEquipmentWithCP56Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
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

        _spe = new StartEvent(msg[startIndex++]);
        _qdp = new QualityDescriptorP(msg[startIndex++]);
        _elapsedTime = new CP16Time2a(msg, startIndex);
        startIndex += 2;

        _timestamp = new CP56Time2a(msg, startIndex);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteByte(_spe.EncodedValue);
        w.WriteByte(_qdp.EncodedValue);
        w.WriteBytes(_elapsedTime.AsSpan());
        w.WriteBytes(_timestamp.AsSpan());
    }
}