//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 文件传输就绪类消息 F_FR_NA_1(120) / F_SR_NA_1(121)
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;
using System.Buffers.Binary;

namespace IEC60870.Core;

/// <summary>
/// 文件类消息（F_*）编解码共用工具。
/// </summary>
internal static class FileMessageCodec
{
    /// <summary>
    /// 解码前置处理：非序列模式下跳过 IOA 前缀，并核对信息体剩余长度，
    /// 返回信息体起始下标。
    /// </summary>
    internal static int BodyStart(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg,
        int startIndex, bool isSequence, int bodySize)
    {
        var body = isSequence ? startIndex : startIndex + parameters.SizeOfIOA;

        if (msg.Length - body < bodySize)
        {
            throw new ASDUParsingException("报文长度不足以解析信息对象");
        }

        return body;
    }

    /// <summary>读取 3 字节小端无符号整数（IEC 60870 文件/段长度字段）。</summary>
    internal static int ReadUInt24LittleEndian(ReadOnlySpan<byte> msg, int position)
        => msg[position] | (msg[position + 1] << 8) | (msg[position + 2] << 16);
}

/// <summary>
/// 文件就绪（F_FR_NA_1，类型号 120）：由文件服务器发出，告知指定文件已准备好传输，
/// 并携带文件名（NOF）、文件长度与就绪限定词 FRQ。
/// </summary>
public class FileReady : InformationObject
{
    // FRQ 中的否定位置位：置 1 表示文件未就绪
    private const byte FrqNegativeBit = 0x80;

    private NameOfFile _nof;
    private int _lengthOfFile;
    private byte _frq;

    /// <inheritdoc/>
    public override TypeID Type => TypeID.F_FR_NA_1;

    /// <inheritdoc/>
    public override bool SupportsSequence => false;

    /// <summary>文件名（NOF），即文件类别。</summary>
    public NameOfFile NOF
    {
        get => _nof;
        set => _nof = value;
    }

    /// <summary>文件总长度（字节）。</summary>
    public int LengthOfFile
    {
        get => _lengthOfFile;
        set => _lengthOfFile = value;
    }

    /// <summary>文件就绪限定词 FRQ 原始字节。</summary>
    public byte FRQ
    {
        get => _frq;
        set => _frq = value;
    }

    /// <summary>FRQ 的否定位为 0，即文件确实就绪。</summary>
    public bool Positive => (_frq & FrqNegativeBit) == 0;

    /// <summary>
    /// 组包构造：按就绪与否设置 FRQ 否定位。
    /// </summary>
    /// <param name="objectAddress">信息对象地址（IOA）</param>
    /// <param name="nof">文件名（文件类别）</param>
    /// <param name="lengthOfFile">文件长度</param>
    /// <param name="positive">true 为肯定（文件就绪），false 为否定</param>
    public FileReady(int objectAddress, NameOfFile nof, int lengthOfFile, bool positive)
        : base(objectAddress)
    {
        _nof = nof;
        _lengthOfFile = lengthOfFile;
        _frq = positive ? (byte)0x00 : FrqNegativeBit;
    }

    internal FileReady(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
        : base(parameters, msg, startIndex, isSequence)
    {
        var position = FileMessageCodec.BodyStart(parameters, msg, startIndex, isSequence, GetEncodedSize());

        // NOF：2 字节小端
        _nof = (NameOfFile)BinaryPrimitives.ReadUInt16LittleEndian(msg.Slice(position, 2));
        position += 2;

        // 文件长度：3 字节小端
        _lengthOfFile = FileMessageCodec.ReadUInt24LittleEndian(msg, position);
        position += 3;

        _frq = msg[position];
    }

    internal override bool HasAsduWriterBody => true;

    /// <inheritdoc/>
    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteIntLittleEndian((int)_nof, 2);
        w.WriteIntLittleEndian(_lengthOfFile, 3);
        w.WriteByte(_frq);
    }
}

/// <summary>
/// 段就绪（F_SR_NA_1，类型号 121）：告知指定文件段的长度与就绪状态，携带 SRQ 限定词。
/// </summary>
public class SectionReady : InformationObject
{
    // SRQ 中的未就绪位
    private const byte SrqNotReadyBit = 0x80;

    private NameOfFile _nof;
    private byte _nameOfSection;
    private int _lengthOfSection;
    private byte _srq;

    /// <inheritdoc/>
    public override TypeID Type => TypeID.F_SR_NA_1;

    /// <inheritdoc/>
    public override bool SupportsSequence => false;

    /// <summary>所属文件的文件名（NOF）。</summary>
    public NameOfFile NOF
    {
        get => _nof;
        set => _nof = value;
    }

    /// <summary>段号。</summary>
    public byte NameOfSection
    {
        get => _nameOfSection;
        set => _nameOfSection = value;
    }

    /// <summary>段长度（字节）。</summary>
    public int LengthOfSection
    {
        get => _lengthOfSection;
        set => _lengthOfSection = value;
    }

    /// <summary>段就绪限定词 SRQ 原始字节。</summary>
    public byte SRQ
    {
        get => _srq;
        set => _srq = value;
    }

    /// <summary>SRQ 未就绪位置起，表示段暂不可传。</summary>
    public bool NotReady => (_srq & SrqNotReadyBit) != 0;

    public SectionReady(int objectAddress, NameOfFile nof, byte nameOfSection, int lengthOfSection, bool notReady)
        : base(objectAddress)
    {
        _nof = nof;
        _nameOfSection = nameOfSection;
        _lengthOfSection = lengthOfSection;
        _srq = notReady ? SrqNotReadyBit : (byte)0x00;
    }

    internal SectionReady(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
        : base(parameters, msg, startIndex, isSequence)
    {
        var position = FileMessageCodec.BodyStart(parameters, msg, startIndex, isSequence, GetEncodedSize());

        _nof = (NameOfFile)BinaryPrimitives.ReadUInt16LittleEndian(msg.Slice(position, 2));
        position += 2;

        _nameOfSection = msg[position++];

        _lengthOfSection = FileMessageCodec.ReadUInt24LittleEndian(msg, position);
        position += 3;

        _srq = msg[position];
    }

    internal override bool HasAsduWriterBody => true;

    /// <inheritdoc/>
    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteIntLittleEndian((int)_nof, 2);
        w.WriteByte(_nameOfSection);
        w.WriteIntLittleEndian(_lengthOfSection, 3);
        w.WriteByte(_srq);
    }
}
