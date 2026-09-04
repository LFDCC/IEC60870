//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 单点/双点/步位置调节命令（C_SC_* / C_DC_* / C_RC_*）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;

namespace IEC60870.Core;

/// <summary>
/// 步位置调节命令状态（RCS），见 IEC 60870-5-101:2003 7.2.6.17。
/// 仅 1（降）与 2（升）为有效值。
/// </summary>
public enum StepCommandValue
{
    INVALID_0 = 0,
    LOWER = 1,
    HIGHER = 2,
    INVALID_3 = 3
}

/// <summary>
/// 单点命令（C_SC_NA_1）。SCO 字节：bit0=命令状态，bit2-6=QU，bit7=选择/执行。
/// </summary>
public class SingleCommand : InformationObject
{
    // SCO 位域。
    private const byte MaskState = 0x01;
    private const byte MaskQu = 0x7c;
    private const byte MaskSelect = 0x80;
    private const int QuShift = 2;
    private const int QuMask = 0x1f;

    private byte _sco;

    public override TypeID Type => TypeID.C_SC_NA_1;

    public override bool SupportsSequence => false;

    public SingleCommand(int ioa, bool command, bool selectCommand, int qu)
        : base(ioa)
    {
        _sco = (byte)((qu & QuMask) << QuShift);

        if (command)
        {
            _sco |= MaskState;
        }

        if (selectCommand)
        {
            _sco |= MaskSelect;
        }
    }

    public SingleCommand(SingleCommand original)
        : base(original.ObjectAddress)
    {
        _sco = original._sco;
    }

    internal SingleCommand(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex, false)
    {
        startIndex += parameters.SizeOfIOA; /* 跳过信息体地址 */

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("Message too small");
        }

        _sco = msg[startIndex];
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteByte(_sco);
    }

    /// <summary>限定选择符 QU（0 … 31）。</summary>
    public int QU
    {
        get => (_sco & MaskQu) >> QuShift;
        set
        {
            _sco = (byte)(_sco & ~(byte)MaskQu);
            _sco |= (byte)((value & QuMask) << QuShift);
        }
    }

    /// <summary>命令状态：合（<c>true</c>）/ 分（<c>false</c>）。</summary>
    public bool State
    {
        get => (_sco & MaskState) != 0;
        set
        {
            if (value)
            {
                _sco |= MaskState;
            }
            else
            {
                _sco &= 0xfe;
            }
        }
    }

    /// <summary>是否为选择命令；<c>false</c> 表示执行命令。</summary>
    public bool Select
    {
        get => (_sco & MaskSelect) != 0;
        set
        {
            if (value)
            {
                _sco |= MaskSelect;
            }
            else
            {
                _sco &= 0x7f;
            }
        }
    }

    public override string ToString() => $"[SingleCommand: QU={QU}, State={State}, Select={Select}]";
}

/// <summary>
/// 带 CP56Time2a 时标的单点命令（C_SC_TA_1）。
/// </summary>
public class SingleCommandWithCP56Time2a : SingleCommand
{
    /// <summary>元素尺寸 = SCO(1) + CP56Time2a(7) = 8。基类仅返回 SCO 的 1，
    /// 此处必须重写：否则解码长度守卫形同虚设（短消息读时标越界），
    /// 且 AddInformationObject 的剩余空间计算与实际编码长度不一致。</summary>
    private const int ElementSize = 8;

    private CP56Time2a _timestamp;

    /// <summary>CP56Time2a 时标。</summary>
    public CP56Time2a Timestamp => _timestamp;

    public override TypeID Type => TypeID.C_SC_TA_1;

    public override bool SupportsSequence => false;

    public SingleCommandWithCP56Time2a(int ioa, bool command, bool selectCommand, int qu, CP56Time2a timestamp)
        : base(ioa, command, selectCommand, qu)
    {
        _timestamp = timestamp;
    }

    public SingleCommandWithCP56Time2a(SingleCommandWithCP56Time2a original)
        : base(original)
    {
        _timestamp = new CP56Time2a(original._timestamp);
    }

    internal SingleCommandWithCP56Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex)
    {
        startIndex += parameters.SizeOfIOA + 1; /* 跳过信息体地址与 SCO */

        if ((msg.Length - startIndex) < ElementSize - 1)
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
/// 双点命令（C_DC_NA_1）。DCQ 字节：bit0-1=命令值，bit2-6=QU，bit7=选择/执行。
/// </summary>
public class DoubleCommand : InformationObject
{
    private const byte MaskState = 0x03;
    private const byte MaskQu = 0x7c;
    private const byte MaskSelect = 0x80;
    private const int QuShift = 2;
    private const int QuMask = 0x1f;

    /// <summary>双点命令值：分。</summary>
    public static int OFF = 1;

    /// <summary>双点命令值：合。</summary>
    public static int ON = 2;

    private byte _dcq;

    public override TypeID Type => TypeID.C_DC_NA_1;

    public override bool SupportsSequence => false;

    public DoubleCommand(int ioa, int command, bool select, int quality)
        : base(ioa)
    {
        _dcq = (byte)(command & MaskState);
        _dcq |= (byte)((quality & QuMask) << QuShift);

        if (select)
        {
            _dcq |= MaskSelect;
        }
    }

    public DoubleCommand(DoubleCommand original)
        : base(original.ObjectAddress)
    {
        _dcq = original._dcq;
    }

    internal DoubleCommand(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex, false)
    {
        startIndex += parameters.SizeOfIOA; /* 跳过信息体地址 */

        if ((msg.Length - startIndex) < GetEncodedSize())
        {
            throw new ASDUParsingException("Message too small");
        }

        _dcq = msg[startIndex];
    }

    internal override bool HasAsduWriterBody => true;

    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteByte(_dcq);
    }

    /// <summary>限定选择符 QU（0 … 31）。</summary>
    public int QU => (_dcq & MaskQu) >> QuShift;

    /// <summary>命令值（0 … 3）。</summary>
    public int State => _dcq & MaskState;

    /// <summary>是否为选择命令；<c>false</c> 表示执行命令。</summary>
    public bool Select => (_dcq & MaskSelect) != 0;
}

/// <summary>
/// 带 CP56Time2a 时标的双点命令（C_DC_TA_1）。
/// </summary>
public class DoubleCommandWithCP56Time2a : DoubleCommand
{
    /// <summary>元素尺寸 = DCQ(1) + CP56Time2a(7) = 8，重写理由同
    /// <see cref="SingleCommandWithCP56Time2a"/>。</summary>
    private const int ElementSize = 8;

    private CP56Time2a _timestamp;

    /// <summary>CP56Time2a 时标。</summary>
    public CP56Time2a Timestamp => _timestamp;

    public override TypeID Type => TypeID.C_DC_TA_1;

    public override bool SupportsSequence => false;

    public DoubleCommandWithCP56Time2a(int ioa, int command, bool select, int quality, CP56Time2a timestamp)
        : base(ioa, command, select, quality)
    {
        _timestamp = timestamp;
    }

    public DoubleCommandWithCP56Time2a(DoubleCommandWithCP56Time2a original)
        : base(original)
    {
        _timestamp = new CP56Time2a(original._timestamp);
    }

    internal DoubleCommandWithCP56Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex)
    {
        startIndex += parameters.SizeOfIOA + 1; /* 跳过信息体地址与 DCQ */

        if ((msg.Length - startIndex) < ElementSize - 1)
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
/// 步位置调节命令（C_RC_NA_1）。编码结构与双点命令相同，仅语义与 TypeID 不同。
/// </summary>
public class StepCommand : DoubleCommand
{
    public override TypeID Type => TypeID.C_RC_NA_1;

    public override bool SupportsSequence => false;

    public StepCommand(int ioa, StepCommandValue command, bool select, int quality)
        : base(ioa, (int)command, select, quality)
    {
    }

    public StepCommand(StepCommand original)
        : base(original)
    {
    }

    internal StepCommand(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex)
    {
    }

    /// <summary>以步位置枚举视图返回命令值。</summary>
    public new StepCommandValue State => (StepCommandValue)base.State;
}

/// <summary>
/// 带 CP56Time2a 时标的步位置调节命令（C_RC_TA_1）。
/// </summary>
public class StepCommandWithCP56Time2a : StepCommand
{
    /// <summary>元素尺寸 = RCO(1) + CP56Time2a(7) = 8，重写理由同
    /// <see cref="SingleCommandWithCP56Time2a"/>。</summary>
    private const int ElementSize = 8;

    private CP56Time2a _timestamp;

    /// <summary>CP56Time2a 时标。</summary>
    public CP56Time2a Timestamp => _timestamp;

    public override TypeID Type => TypeID.C_RC_TA_1;

    public override bool SupportsSequence => false;

    public StepCommandWithCP56Time2a(int ioa, StepCommandValue command, bool select, int quality, CP56Time2a timestamp)
        : base(ioa, command, select, quality)
    {
        _timestamp = timestamp;
    }

    public StepCommandWithCP56Time2a(StepCommandWithCP56Time2a original)
        : base(original)
    {
        _timestamp = new CP56Time2a(original._timestamp);
    }

    internal StepCommandWithCP56Time2a(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
        : base(parameters, msg, startIndex)
    {
        startIndex += parameters.SizeOfIOA + 1; /* 跳过信息体地址与 RCO */

        if ((msg.Length - startIndex) < ElementSize - 1)
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
