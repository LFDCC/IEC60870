//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 文件传输控制类消息 F_SC_NA_1(122) / F_LS_NA_1(123) / F_AF_NA_1(124)
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;
using System.Buffers.Binary;

namespace IEC60870.Core;

/// <summary>
/// 目录/文件/段的选择与召唤（F_SC_NA_1，类型号 122）：主站用 SCQ 指明要执行的
/// 选择、请求、取消选择或删除动作。
/// </summary>
public class FileCallOrSelect : InformationObject
{
    private NameOfFile _nof;
    private byte _nameOfSection;
    private SelectAndCallQualifier _scq;

    /// <inheritdoc/>
    public override TypeID Type => TypeID.F_SC_NA_1;

    /// <inheritdoc/>
    public override bool SupportsSequence => false;

    /// <summary>目标文件名（NOF）。</summary>
    public NameOfFile NOF
    {
        get => _nof;
        set => _nof = value;
    }

    /// <summary>目标段号，未涉及段时为 0。</summary>
    public byte NameOfSection
    {
        get => _nameOfSection;
        set => _nameOfSection = value;
    }

    /// <summary>选择与召唤限定词 SCQ。</summary>
    public SelectAndCallQualifier SCQ
    {
        get => _scq;
        set => _scq = value;
    }

    public FileCallOrSelect(int objectAddress, NameOfFile nof, byte nameOfSection, SelectAndCallQualifier scq)
        : base(objectAddress)
    {
        _nof = nof;
        _nameOfSection = nameOfSection;
        _scq = scq;
    }

    internal FileCallOrSelect(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
        : base(parameters, msg, startIndex, isSequence)
    {
        var position = FileMessageCodec.BodyStart(parameters, msg, startIndex, isSequence, GetEncodedSize());

        _nof = (NameOfFile)BinaryPrimitives.ReadUInt16LittleEndian(msg.Slice(position, 2));
        position += 2;

        _nameOfSection = msg[position++];
        _scq = (SelectAndCallQualifier)msg[position];
    }

    internal override bool HasAsduWriterBody => true;

    /// <inheritdoc/>
    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteIntLittleEndian((int)_nof, 2);
        w.WriteByte(_nameOfSection);
        w.WriteByte((byte)_scq);
    }
}

/// <summary>
/// 末段/末节（F_LS_NA_1，类型号 123）：标志文件或段的结束，并附带整段校验和 CHS。
/// </summary>
public class FileLastSegmentOrSection : InformationObject
{
    private NameOfFile _nof;
    private byte _nameOfSection;
    private LastSectionOrSegmentQualifier _lsq;
    private byte _chs;

    /// <inheritdoc/>
    public override TypeID Type => TypeID.F_LS_NA_1;

    /// <inheritdoc/>
    public override bool SupportsSequence => false;

    /// <summary>所属文件名（NOF）。</summary>
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

    /// <summary>末段/末节限定词 LSQ。</summary>
    public LastSectionOrSegmentQualifier LSQ
    {
        get => _lsq;
        set => _lsq = value;
    }

    /// <summary>校验和 CHS。</summary>
    public byte CHS
    {
        get => _chs;
        set => _chs = value;
    }

    public FileLastSegmentOrSection(int objectAddress, NameOfFile nof, byte nameOfSection,
        LastSectionOrSegmentQualifier lsq, byte checksum)
        : base(objectAddress)
    {
        _nof = nof;
        _nameOfSection = nameOfSection;
        _lsq = lsq;
        _chs = checksum;
    }

    internal FileLastSegmentOrSection(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
        : base(parameters, msg, startIndex, isSequence)
    {
        var position = FileMessageCodec.BodyStart(parameters, msg, startIndex, isSequence, GetEncodedSize());

        _nof = (NameOfFile)BinaryPrimitives.ReadUInt16LittleEndian(msg.Slice(position, 2));
        position += 2;

        _nameOfSection = msg[position++];
        _lsq = (LastSectionOrSegmentQualifier)msg[position++];
        _chs = msg[position];
    }

    internal override bool HasAsduWriterBody => true;

    /// <inheritdoc/>
    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteIntLittleEndian((int)_nof, 2);
        w.WriteByte(_nameOfSection);
        w.WriteByte((byte)_lsq);
        w.WriteByte(_chs);
    }
}

/// <summary>
/// 文件/段应答（F_AF_NA_1，类型号 124）：AFQ 字节低 4 位为确认限定词、高 4 位为差错原因。
/// </summary>
public class FileACK : InformationObject
{
    // AFQ 内部分段：低 4 位 = 确认限定词，高 4 位 = 差错码
    private const byte QualifierMask = 0x0F;
    private const byte ErrorCodeShift = 4;

    private NameOfFile _nof;
    private byte _nameOfSection;
    private byte _afq;

    /// <inheritdoc/>
    public override TypeID Type => TypeID.F_AF_NA_1;

    /// <inheritdoc/>
    public override bool SupportsSequence => false;

    /// <summary>应答针对的文件名（NOF）。</summary>
    public NameOfFile NOF
    {
        get => _nof;
        set => _nof = value;
    }

    /// <summary>应答针对的段号。</summary>
    public byte NameOfSection
    {
        get => _nameOfSection;
        set => _nameOfSection = value;
    }

    /// <summary>AFQ 低 4 位：肯定/否定确认对象，写入时保留高 4 位差错码。</summary>
    public AcknowledgeQualifier AckQualifier
    {
        get => (AcknowledgeQualifier)(_afq & QualifierMask);
        set => _afq = (byte)((_afq & ~QualifierMask) | (byte)value);
    }

    /// <summary>AFQ 高 4 位：差错原因，写入时保留低 4 位确认限定词。</summary>
    public FileError ErrorCode
    {
        get => (FileError)(_afq >> ErrorCodeShift);
        set => _afq = (byte)((_afq & QualifierMask) | ((byte)value << ErrorCodeShift));
    }

    /// <summary>AFQ 原始字节。</summary>
    public byte AFQ
    {
        get => _afq;
        set => _afq = value;
    }

    public FileACK(int objectAddress, NameOfFile nof, byte nameOfSection,
        AcknowledgeQualifier qualifier, FileError errorCode)
        : base(objectAddress)
    {
        _nof = nof;
        _nameOfSection = nameOfSection;
        AckQualifier = qualifier;
        ErrorCode = errorCode;
    }

    internal FileACK(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
        : base(parameters, msg, startIndex, isSequence)
    {
        var position = FileMessageCodec.BodyStart(parameters, msg, startIndex, isSequence, GetEncodedSize());

        _nof = (NameOfFile)BinaryPrimitives.ReadUInt16LittleEndian(msg.Slice(position, 2));
        position += 2;

        _nameOfSection = msg[position++];
        _afq = msg[position];
    }

    internal override bool HasAsduWriterBody => true;

    /// <inheritdoc/>
    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteIntLittleEndian((int)_nof, 2);
        w.WriteByte(_nameOfSection);
        w.WriteByte(_afq);
    }
}
