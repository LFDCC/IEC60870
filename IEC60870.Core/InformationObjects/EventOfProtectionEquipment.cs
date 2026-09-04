//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 保护设备事件（M_EP_TA_1 / M_EP_TD_1）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;

namespace IEC60870.Core;

/// <summary>
/// 保护设备事件信息体（M_EP_TA_1）。编码：SEP(1) + CP16Time2a(2) + CP24Time2a(3)。
/// </summary>
public class EventOfProtectionEquipment : InformationObject
{
    private SingleEvent _singleEvent;
    private CP16Time2a _elapsedTime;
    private CP24Time2a _timestamp;

    /// <summary>单事件。</summary>
    public SingleEvent Event => _singleEvent;

    /// <summary>相对动作时间（相对时标）。</summary>
    public CP16Time2a ElapsedTime => _elapsedTime;

    /// <summary>CP24Time2a 时标。</summary>
    public CP24Time2a Timestamp => _timestamp;

    public override TypeID Type => TypeID.M_EP_TA_1;

    public override bool SupportsSequence => false;

    public EventOfProtectionEquipment(int ioa, SingleEvent singleEvent, CP16Time2a elapsedTime, CP24Time2a timestamp)
        : base(ioa)
    {
        _singleEvent = singleEvent;
        _elapsedTime = elapsedTime;
        _timestamp = timestamp;
    }

    public EventOfProtectionEquipment(EventOfProtectionEquipment original)
        : base(original.ObjectAddress)
    {
        _singleEvent = new SingleEvent(original._singleEvent);
        _elapsedTime = new CP16Time2a(original._elapsedTime);
        _timestamp = new CP24Time2a(original._timestamp);
    }

    internal EventOfProtectionEquipment(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
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

        _singleEvent = new SingleEvent(msg[startIndex++]);
        _elapsedTime = new CP16Time2a(msg, startIndex);
        startIndex += 2;

        _timestamp = new CP24Time2a(msg, startIndex);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteByte(_singleEvent.EncodedValue);
        w.WriteBytes(_elapsedTime.AsSpan());
        w.WriteBytes(_timestamp.AsSpan());
    }
}

/// <summary>
/// 带 CP56Time2a 时标的保护设备事件信息体（M_EP_TD_1）。
/// 编码：SEP(1) + CP16Time2a(2) + CP56Time2a(7)。
/// </summary>
public class EventOfProtectionEquipmentWithCP56Time2a : InformationObject
{
    private SingleEvent _singleEvent;
    private CP16Time2a _elapsedTime;
    private CP56Time2a _timestamp;

    /// <summary>单事件。</summary>
    public SingleEvent Event => _singleEvent;

    /// <summary>相对动作时间（相对时标）。</summary>
    public CP16Time2a ElapsedTime => _elapsedTime;

    /// <summary>CP56Time2a 时标。</summary>
    public CP56Time2a Timestamp => _timestamp;

    public override TypeID Type => TypeID.M_EP_TD_1;

    public override bool SupportsSequence => false;

    public EventOfProtectionEquipmentWithCP56Time2a(int ioa, SingleEvent singleEvent, CP16Time2a elapsedTime, CP56Time2a timestamp)
        : base(ioa)
    {
        _singleEvent = singleEvent;
        _elapsedTime = elapsedTime;
        _timestamp = timestamp;
    }

    public EventOfProtectionEquipmentWithCP56Time2a(EventOfProtectionEquipmentWithCP56Time2a original)
        : base(original.ObjectAddress)
    {
        _singleEvent = new SingleEvent(original._singleEvent);
        _elapsedTime = new CP16Time2a(original._elapsedTime);
        _timestamp = new CP56Time2a(original._timestamp);
    }

    internal EventOfProtectionEquipmentWithCP56Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
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

        _singleEvent = new SingleEvent(msg[startIndex++]);
        _elapsedTime = new CP16Time2a(msg, startIndex);
        startIndex += 2;

        _timestamp = new CP56Time2a(msg, startIndex);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteByte(_singleEvent.EncodedValue);
        w.WriteBytes(_elapsedTime.AsSpan());
        w.WriteBytes(_timestamp.AsSpan());
    }
}