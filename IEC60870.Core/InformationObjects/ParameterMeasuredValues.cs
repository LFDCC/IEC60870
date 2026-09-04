//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 参数测量值（P_ME_NA_1 / NB_1 / NC_1 / P_AC_NA_1）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;
using System.Buffers.Binary;

namespace IEC60870.Core;

/// <summary>
/// 归一化参数测量值（P_ME_NA_1）。编码：2 字节缩放值 + 1 字节 QPM。
/// </summary>
public class ParameterNormalizedValue : InformationObject
{
    private ScaledValue _scaledValue;
    private byte _qpm;

    /// <summary>原始短整型值。</summary>
    public short RawValue
    {
        get => _scaledValue.ShortValue;
        set => _scaledValue.ShortValue = value;
    }

    /// <summary>归一化浮点值。</summary>
    public float NormalizedValue
    {
        get => _scaledValue.GetNormalizedValue();
        set => _scaledValue.SetScaledFromNormalizedValue(value);
    }

    /// <summary>参数限定符（QPM）。</summary>
    public byte QPM => _qpm;

    public override TypeID Type => TypeID.P_ME_NA_1;

    public override bool SupportsSequence => false;

    public ParameterNormalizedValue(int objectAddress, float normalizedValue, byte qpm)
        : base(objectAddress)
    {
        _scaledValue = new ScaledValue();
        NormalizedValue = normalizedValue;
        _qpm = qpm;
    }

    public ParameterNormalizedValue(int objectAddress, short rawValue, byte qpm)
        : base(objectAddress)
    {
        _scaledValue = new ScaledValue(rawValue);
        _qpm = qpm;
    }

    internal ParameterNormalizedValue(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex, false)
    {
        startIndex += parameters.SizeOfIOA; /* 跳过信息体地址 */

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("Message too small");
        }

        _scaledValue = new ScaledValue(msg, startIndex);
        _qpm = msg[startIndex + 2];
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteBytes(_scaledValue.AsSpan());
        w.WriteByte(_qpm);
    }
}

/// <summary>
/// 缩放值参数测量值（P_ME_NB_1）。编码：2 字节缩放值 + 1 字节 QPM。
/// </summary>
public class ParameterScaledValue : InformationObject
{
    private ScaledValue _scaledValue;
    private byte _qpm;

    /// <summary>缩放值。</summary>
    public ScaledValue ScaledValue
    {
        get => _scaledValue;
        set => _scaledValue = value;
    }

    /// <summary>参数限定符（QPM）。</summary>
    public byte QPM => _qpm;

    public override TypeID Type => TypeID.P_ME_NB_1;

    public override bool SupportsSequence => false;

    public ParameterScaledValue(int objectAddress, ScaledValue value, byte qpm)
        : base(objectAddress)
    {
        _scaledValue = value;
        _qpm = qpm;
    }

    internal ParameterScaledValue(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex, false)
    {
        startIndex += parameters.SizeOfIOA; /* 跳过信息体地址 */

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("Message too small");
        }

        _scaledValue = new ScaledValue(msg, startIndex);
        _qpm = msg[startIndex + 2];
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteBytes(_scaledValue.AsSpan());
        w.WriteByte(_qpm);
    }
}

/// <summary>
/// 浮点参数测量值（P_ME_NC_1）。编码：4 字节 IEEE 754 单精度 + 1 字节 QPM。
/// </summary>
public class ParameterFloatValue : InformationObject
{
    private float _value;
    private byte _qpm;

    /// <summary>浮点值。</summary>
    public float Value => _value;

    /// <summary>参数限定符（QPM）。</summary>
    public byte QPM => _qpm;

    public override TypeID Type => TypeID.P_ME_NC_1;

    public override bool SupportsSequence => false;

    public ParameterFloatValue(int objectAddress, float value, byte qpm)
        : base(objectAddress)
    {
        _value = value;
        _qpm = qpm;
    }

    internal ParameterFloatValue(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex, false)
    {
        startIndex += parameters.SizeOfIOA; /* 跳过信息体地址 */

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("Message too small");
        }

        _value = BinaryPrimitives.ReadSingleLittleEndian(msg.Slice(startIndex, 4));
        _qpm = msg[startIndex + 4];
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        // IEEE 754 单精度按小端 4 字节写入。
        w.WriteIntLittleEndian(BitConverter.SingleToInt32Bits(_value), 4);

        w.WriteByte(_qpm);
    }
}

/// <summary>
/// 参数激活命令（P_AC_NA_1）。编码：1 字节 QPA。
/// </summary>
public class ParameterActivation : InformationObject
{
    private byte _qpa;

    /// <summary>QPA = 0：未使用。</summary>
    public static byte NOT_USED = 0;

    /// <summary>QPA = 1：去激活之前装载的参数。</summary>
    public static byte DE_ACT_PREV_LOADED_PARAMETER = 1;

    /// <summary>QPA = 2：去激活目标参数。</summary>
    public static byte DE_ACT_OBJECT_PARAMETER = 2;

    /// <summary>QPA = 3：去激活目标传输。</summary>
    public static byte DE_ACT_OBJECT_TRANSMISSION = 3;

    /// <summary>参数激活限定符（QPA）。</summary>
    public byte QPA => _qpa;

    public override TypeID Type => TypeID.P_AC_NA_1;

    public override bool SupportsSequence => false;

    public ParameterActivation(int objectAddress, byte qpa)
        : base(objectAddress)
    {
        _qpa = qpa;
    }

    internal ParameterActivation(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex, false)
    {
        startIndex += parameters.SizeOfIOA; /* 跳过信息体地址 */

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("Message too small");
        }

        _qpa = msg[startIndex];
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteByte(_qpa);
    }
}