//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 初始化结束信息对象（M_EI_NA_1）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;

namespace IEC60870.Core;

/// <summary>
/// 初始化结束信息对象（M_EI_NA_1）：单字节 COI（初始化原因）。
/// </summary>
public class EndOfInitialization : InformationObject
{
    private byte _coi;

    /// <summary>初始化原因（COI，bit0-6 本地/远方，bit7 兼容级）。</summary>
    public byte COI
    {
        get => _coi;
        set => _coi = value;
    }

    /// <inheritdoc/>
    public override TypeID Type => TypeID.M_EI_NA_1;

    /// <inheritdoc/>
    public override bool SupportsSequence => false;

    /// <summary>以初始化原因构造。</summary>
    public EndOfInitialization(byte coi)
        : base(0)
    {
        _coi = coi;
    }

    /// <summary>复制构造。</summary>
    public EndOfInitialization(EndOfInitialization original)
        : base(original.ObjectAddress)
    {
        _coi = original._coi;
    }

    internal EndOfInitialization(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex, false)
    {
        startIndex += parameters.SizeOfIOA; // 跳过 IOA

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("报文长度不足以解析信息对象");
        }

        _coi = msg[startIndex];
    }

    internal override bool HasAsduWriterBody => true;

    /// <inheritdoc/>
    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteByte(_coi);
    }
}
