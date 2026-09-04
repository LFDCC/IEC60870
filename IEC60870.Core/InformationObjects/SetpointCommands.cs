//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 设定值命令（C_SE_* 与 C_BO_*）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;
using System.Buffers.Binary;

namespace IEC60870.Core;

/// <summary>
/// 归一化设定值命令（C_SE_NA_1）。编码：2 字节归一化值 + 1 字节 QOS。
/// </summary>
public class SetpointCommandNormalized : InformationObject
{
    private ScaledValue _scaledValue;
    private SetpointCommandQualifier _qos;

    /// <summary>原始短整型设定值。</summary>
    public short RawValue
    {
        get => _scaledValue.ShortValue;
        set => _scaledValue.ShortValue = value;
    }

    /// <summary>归一化浮点设定值（-1.0 … +1.0 附近）。</summary>
    public float NormalizedValue
    {
        get => _scaledValue.GetNormalizedValue();
        set => _scaledValue.SetScaledFromNormalizedValue(value);
    }

    /// <summary>设定值限定符。</summary>
    public SetpointCommandQualifier QOS => _qos;

    public override TypeID Type => TypeID.C_SE_NA_1;

    public override bool SupportsSequence => false;

    public SetpointCommandNormalized(int objectAddress, float value, SetpointCommandQualifier qos)
        : base(objectAddress)
    {
        _scaledValue = new ScaledValue((int)((value * 32767.5) - 0.5));
        _qos = qos;
    }

    public SetpointCommandNormalized(int objectAddress, short value, SetpointCommandQualifier qos)
        : base(objectAddress)
    {
        _scaledValue = new ScaledValue(value);
        _qos = qos;
    }

    internal SetpointCommandNormalized(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex, false)
    {
        startIndex += parameters.SizeOfIOA; /* 跳过信息体地址 */

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("Message too small");
        }

        _scaledValue = new ScaledValue(msg, startIndex);
        _qos = new SetpointCommandQualifier(msg[startIndex + 2]);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteBytes(_scaledValue.AsSpan());
        w.WriteByte(_qos.GetEncodedValue());
    }
}

/// <summary>
/// 带 CP56Time2a 时标的归一化设定值命令（C_SE_TA_1）。
/// </summary>
public class SetpointCommandNormalizedWithCP56Time2a : SetpointCommandNormalized
{
    private CP56Time2a _timestamp;

    /// <summary>CP56Time2a 时标。</summary>
    public CP56Time2a Timestamp => _timestamp;

    public override TypeID Type => TypeID.C_SE_TA_1;

    public override bool SupportsSequence => false;

    public SetpointCommandNormalizedWithCP56Time2a(int objectAddress, float value, SetpointCommandQualifier qos, CP56Time2a timestamp)
        : base(objectAddress, value, qos)
    {
        _timestamp = timestamp;
    }

    public SetpointCommandNormalizedWithCP56Time2a(int objectAddress, short value, SetpointCommandQualifier qos, CP56Time2a timestamp)
        : base(objectAddress, value, qos)
    {
        _timestamp = timestamp;
    }

    internal SetpointCommandNormalizedWithCP56Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex)
    {
        startIndex += parameters.SizeOfIOA + 3; /* 跳过信息体地址 + 归一化值(2) + QOS(1) */

        if ((msg.Length - startIndex) < GetEncodedSize() - 3)
        {
            throw new ASDUParsingException("Message too small");
        }

        _timestamp = new CP56Time2a(msg, startIndex);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteBytes(_timestamp.AsSpan());
    }
}

/// <summary>
/// 缩放值设定值命令（C_SE_NB_1）。编码：2 字节缩放值 + 1 字节 QOS。
/// </summary>
public class SetpointCommandScaled : InformationObject
{
    private ScaledValue _scaledValue;
    private SetpointCommandQualifier _qos;

    /// <summary>缩放值。</summary>
    public ScaledValue ScaledValue => _scaledValue;

    /// <summary>设定值限定符。</summary>
    public SetpointCommandQualifier QOS => _qos;

    public override TypeID Type => TypeID.C_SE_NB_1;

    public override bool SupportsSequence => false;

    public SetpointCommandScaled(int objectAddress, ScaledValue value, SetpointCommandQualifier qos)
        : base(objectAddress)
    {
        _scaledValue = value;
        _qos = qos;
    }

    internal SetpointCommandScaled(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex, false)
    {
        startIndex += parameters.SizeOfIOA; /* 跳过信息体地址 */

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("Message too small");
        }

        _scaledValue = new ScaledValue(msg, startIndex);
        _qos = new SetpointCommandQualifier(msg[startIndex + 2]);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteBytes(_scaledValue.AsSpan());
        w.WriteByte(_qos.GetEncodedValue());
    }
}

/// <summary>
/// 带 CP56Time2a 时标的缩放值设定值命令（C_SE_TB_1）。
/// </summary>
public class SetpointCommandScaledWithCP56Time2a : SetpointCommandScaled
{
    private CP56Time2a _timestamp;

    /// <summary>CP56Time2a 时标。</summary>
    public CP56Time2a Timestamp => _timestamp;

    public override TypeID Type => TypeID.C_SE_TB_1;

    public override bool SupportsSequence => false;

    public SetpointCommandScaledWithCP56Time2a(int objectAddress, ScaledValue value, SetpointCommandQualifier qos, CP56Time2a timestamp)
        : base(objectAddress, value, qos)
    {
        _timestamp = timestamp;
    }

    internal SetpointCommandScaledWithCP56Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex)
    {
        startIndex += parameters.SizeOfIOA + 3; /* 跳过信息体地址 + 缩放值(2) + QOS(1) */

        if ((msg.Length - startIndex) < GetEncodedSize() - 3)
        {
            throw new ASDUParsingException("Message too small");
        }

        _timestamp = new CP56Time2a(msg, startIndex);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteBytes(_timestamp.AsSpan());
    }
}

/// <summary>
/// 短浮点设定值命令（C_SE_NC_1）。编码：4 字节 IEEE 754 单精度 + 1 字节 QOS。
/// </summary>
public class SetpointCommandShort : InformationObject
{
    private float _value;
    private SetpointCommandQualifier _qos;

    /// <summary>浮点设定值。</summary>
    public float Value => _value;

    /// <summary>设定值限定符。</summary>
    public SetpointCommandQualifier QOS => _qos;

    public override TypeID Type => TypeID.C_SE_NC_1;

    public override bool SupportsSequence => false;

    public SetpointCommandShort(int objectAddress, float value, SetpointCommandQualifier qos)
        : base(objectAddress)
    {
        _value = value;
        _qos = qos;
    }

    internal SetpointCommandShort(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex, false)
    {
        startIndex += parameters.SizeOfIOA; /* 跳过信息体地址 */

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("Message too small");
        }

        _value = BinaryPrimitives.ReadSingleLittleEndian(msg.Slice(startIndex, 4));

        _qos = new SetpointCommandQualifier(msg[startIndex + 4]);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        // IEEE 754 单精度按小端 4 字节写入。
        w.WriteIntLittleEndian(BitConverter.SingleToInt32Bits(_value), 4);

        w.WriteByte(_qos.GetEncodedValue());
    }
}

/// <summary>
/// 带 CP56Time2a 时标的短浮点设定值命令（C_SE_TC_1）。
/// </summary>
public class SetpointCommandShortWithCP56Time2a : SetpointCommandShort
{
    private CP56Time2a _timestamp;

    /// <summary>CP56Time2a 时标。</summary>
    public CP56Time2a Timestamp => _timestamp;

    public override TypeID Type => TypeID.C_SE_TC_1;

    public override bool SupportsSequence => false;

    public SetpointCommandShortWithCP56Time2a(int objectAddress, float value, SetpointCommandQualifier qos, CP56Time2a timestamp)
        : base(objectAddress, value, qos)
    {
        _timestamp = timestamp;
    }

    internal SetpointCommandShortWithCP56Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex)
    {
        startIndex += parameters.SizeOfIOA + 5; /* 跳过信息体地址 + 浮点值(4) + QOS(1) */

        if ((msg.Length - startIndex) < GetEncodedSize() - 5)
        {
            throw new ASDUParsingException("Message too small");
        }

        _timestamp = new CP56Time2a(msg, startIndex);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteBytes(_timestamp.AsSpan());
    }
}

/// <summary>
/// 位串命令（C_BO_NA_1）。编码：4 字节小端 UInt32。
/// </summary>
public class Bitstring32Command : InformationObject
{
    private UInt32 _value;

    /// <summary>32 位位串值。</summary>
    public UInt32 Value => _value;

    public override TypeID Type => TypeID.C_BO_NA_1;

    public override bool SupportsSequence => false;

    public Bitstring32Command(int objectAddress, UInt32 bitstring)
        : base(objectAddress)
    {
        _value = bitstring;
    }

    internal Bitstring32Command(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex, false)
    {
        startIndex += parameters.SizeOfIOA; /* 跳过信息体地址 */

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("Message too small");
        }

        _value = msg[startIndex]
                 | ((uint)msg[startIndex + 1] << 8)
                 | ((uint)msg[startIndex + 2] << 16)
                 | ((uint)msg[startIndex + 3] << 24);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteByte((byte)_value);
        w.WriteByte((byte)(_value >> 8));
        w.WriteByte((byte)(_value >> 16));
        w.WriteByte((byte)(_value >> 24));
    }
}

/// <summary>
/// 带 CP56Time2a 时标的位串命令（C_BO_TA_1）。
/// </summary>
public class Bitstring32CommandWithCP56Time2a : Bitstring32Command
{
    private CP56Time2a _timestamp;

    /// <summary>CP56Time2a 时标。</summary>
    public CP56Time2a Timestamp => _timestamp;

    public override TypeID Type => TypeID.C_BO_TA_1;

    public override bool SupportsSequence => false;

    public Bitstring32CommandWithCP56Time2a(int objectAddress, UInt32 bitstring, CP56Time2a timestamp)
        : base(objectAddress, bitstring)
    {
        _timestamp = timestamp;
    }

    internal Bitstring32CommandWithCP56Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex)
    {
        startIndex += parameters.SizeOfIOA + 4; /* 跳过信息体地址 + 位串(4) */

        if ((msg.Length - startIndex) < GetEncodedSize() - 4)
        {
            throw new ASDUParsingException("Message too small");
        }

        _timestamp = new CP56Time2a(msg, startIndex);
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteBytes(_timestamp.AsSpan());
    }
}