//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 系统信息命令（C_IC_NA_1 / C_CI_NA_1 / C_RD_NA_1 /
//  C_TS_NA_1 / C_TS_TA_1 / C_CS_NA_1 / C_RP_NA_1 / C_CD_NA_1）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;

namespace IEC60870.Core;

/// <summary>
/// 总召唤限定符（QOI）常量。
/// </summary>
public static class QualifierOfInterrogation
{
    public const byte STATION = 20;
    public const byte GROUP_1 = 21;
    public const byte GROUP_2 = 22;
    public const byte GROUP_3 = 23;
    public const byte GROUP_4 = 24;
    public const byte GROUP_5 = 25;
    public const byte GROUP_6 = 26;
    public const byte GROUP_7 = 27;
    public const byte GROUP_8 = 28;
    public const byte GROUP_9 = 29;
    public const byte GROUP_10 = 30;
    public const byte GROUP_11 = 31;
    public const byte GROUP_12 = 32;
    public const byte GROUP_13 = 33;
    public const byte GROUP_14 = 34;
    public const byte GROUP_15 = 35;
    public const byte GROUP_16 = 36;
}

/// <summary>
/// 总召唤命令（C_IC_NA_1）。编码：1 字节 QOI。
/// </summary>
public class InterrogationCommand : InformationObject
{
    private byte _qoi;

    /// <summary>总召唤限定符。</summary>
    public byte QOI
    {
        get => _qoi;
        set => _qoi = value;
    }

    public override TypeID Type => TypeID.C_IC_NA_1;

    public override bool SupportsSequence => false;

    public InterrogationCommand(int ioa, byte qoi)
        : base(ioa)
    {
        _qoi = qoi;
    }

    internal InterrogationCommand(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex, false)
    {
        startIndex += parameters.SizeOfIOA; /* 跳过信息体地址 */

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("Message too small");
        }

        _qoi = msg[startIndex];
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteByte(_qoi);
    }
}

/// <summary>
/// 计数量召唤命令（C_CI_NA_1）。编码：1 字节 QCC。
/// </summary>
public class CounterInterrogationCommand : InformationObject
{
    private byte _qcc;

    /// <summary>计数量召唤限定符（QCC）。</summary>
    public byte QCC
    {
        get => _qcc;
        set => _qcc = value;
    }

    public override TypeID Type => TypeID.C_CI_NA_1;

    public override bool SupportsSequence => false;

    public CounterInterrogationCommand(int ioa, byte qcc)
        : base(ioa)
    {
        _qcc = qcc;
    }

    internal CounterInterrogationCommand(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex, false)
    {
        startIndex += parameters.SizeOfIOA; /* 跳过信息体地址 */

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("Message too small");
        }

        _qcc = msg[startIndex];
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteByte(_qcc);
    }
}

/// <summary>
/// 读命令（C_RD_NA_1）。只有信息体地址，无数据体。
/// </summary>
public class ReadCommand : InformationObject
{
    public override TypeID Type => TypeID.C_RD_NA_1;

    public override bool SupportsSequence => false;

    public ReadCommand(int ioa)
        : base(ioa)
    {
    }

    internal ReadCommand(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex, false)
    {
    }
}

/// <summary>
/// 带 CP56Time2a 时标的测试命令（C_TS_TA_1）。编码：2 字节 TSC（小端）+ 7 字节时标。
/// </summary>
public class TestCommandWithCP56Time2a : InformationObject
{
    private CP56Time2a _time;
    private ushort _tsc;

    /// <summary>测试时间。</summary>
    public CP56Time2a Time
    {
        get => _time;
        set => _time = value;
    }

    /// <summary>测试序列号。</summary>
    public ushort TSC
    {
        get => _tsc;
        set => _tsc = value;
    }

    public override TypeID Type => TypeID.C_TS_TA_1;

    public override bool SupportsSequence => false;

    public TestCommandWithCP56Time2a()
        : base(0)
    {
        _time = new CP56Time2a();
    }

    public TestCommandWithCP56Time2a(ushort tsc, CP56Time2a time)
        : base(0)
    {
        _time = time;
        _tsc = tsc;
    }

    internal TestCommandWithCP56Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex, false)
    {
        startIndex += parameters.SizeOfIOA; /* 跳过信息体地址 */

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("Message too small");
        }

        _tsc = (ushort)(msg[startIndex] | (msg[startIndex + 1] << 8));

        _time = new CP56Time2a(msg, startIndex + 2);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteByte((byte)_tsc);
        w.WriteByte((byte)(_tsc >> 8));

        w.WriteBytes(_time.AsSpan());
    }
}

/// <summary>
/// 无时标测试命令（C_TS_NA_1）。编码：固定 0xAA 0x55。
/// </summary>
public class TestCommand : InformationObject
{
    private bool _valid = true;

    /// <summary>命令是否有效。</summary>
    public bool Valid => _valid;

    public override TypeID Type => TypeID.C_TS_NA_1;

    public override bool SupportsSequence => false;

    public TestCommand()
        : base(0)
    {
    }

    internal TestCommand(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex, false)
    {
        startIndex += parameters.SizeOfIOA; /* 跳过信息体地址 */

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("Message too small");
        }

        if (msg[startIndex] != 0xaa)
        {
            _valid = false;
        }

        if (msg[startIndex + 1] != 0x55)
        {
            _valid = false;
        }
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteByte(0xaa);
        w.WriteByte(0x55);
    }
}

/// <summary>
/// 时钟同步命令（C_CS_NA_1）。编码：CP56Time2a 时标（7 字节）。
/// </summary>
public class ClockSynchronizationCommand : InformationObject
{
    private CP56Time2a _newTime;

    /// <summary>待同步的新时间。</summary>
    public CP56Time2a NewTime
    {
        get => _newTime;
        set => _newTime = value;
    }

    public override TypeID Type => TypeID.C_CS_NA_1;

    public override bool SupportsSequence => false;

    public ClockSynchronizationCommand(int ioa, CP56Time2a newTime)
        : base(ioa)
    {
        _newTime = newTime;
    }

    internal ClockSynchronizationCommand(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex, false)
    {
        startIndex += parameters.SizeOfIOA; /* 跳过信息体地址 */

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("Message too small");
        }

        _newTime = new CP56Time2a(msg, startIndex);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteBytes(_newTime.AsSpan());
    }
}

/// <summary>
/// 复位进程命令（C_RP_NA_1）。编码：1 字节 QRP。
/// </summary>
public class ResetProcessCommand : InformationObject
{
    private byte _qrp;

    /// <summary>复位进程限定符（QRP）。</summary>
    public byte QRP
    {
        get => _qrp;
        set => _qrp = value;
    }

    public override TypeID Type => TypeID.C_RP_NA_1;

    public override bool SupportsSequence => false;

    public ResetProcessCommand(int ioa, byte qrp)
        : base(ioa)
    {
        _qrp = qrp;
    }

    internal ResetProcessCommand(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex, false)
    {
        startIndex += parameters.SizeOfIOA; /* 跳过信息体地址 */

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("Message too small");
        }

        _qrp = msg[startIndex];
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteByte(_qrp);
    }
}

/// <summary>
/// 延时采集命令（C_CD_NA_1）。编码：CP16Time2a 延时（2 字节）。
/// </summary>
public class DelayAcquisitionCommand : InformationObject
{
    private CP16Time2a _delay;

    /// <summary>延时值。</summary>
    public CP16Time2a Delay
    {
        get => _delay;
        set => _delay = value;
    }

    public override TypeID Type => TypeID.C_CD_NA_1;

    public override bool SupportsSequence => false;

    public DelayAcquisitionCommand(int ioa, CP16Time2a delay)
        : base(ioa)
    {
        _delay = delay;
    }

    internal DelayAcquisitionCommand(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex, false)
    {
        startIndex += parameters.SizeOfIOA; /* 跳过信息体地址 */

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("Message too small");
        }

        _delay = new CP16Time2a(msg, startIndex);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteBytes(_delay.AsSpan());
    }
}