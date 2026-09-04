//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 文件段数据消息 F_SG_NA_1(125)（变长段负载）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;
using System.Buffers.Binary;

namespace IEC60870.Core;

/// <summary>
/// 文件段（F_SG_NA_1，类型号 125）：将文件按段传输，每段携带 LOS 声明的数据长度。
/// 信息体为变长，故 <see cref="GetEncodedSize"/> 只计入固定前缀（NOF/NS/LOS 共 4 字节），
/// 段数据长度由 LOS 单独表达。
/// </summary>
public class FileSegment : InformationObject
{
    // 固定前缀字节数：NOF(2) + 段号(1) + LOS(1)
    private const int FixedPrefixSize = 4;

    private NameOfFile _nof;
    private byte _nameOfSection;
    private byte _los;
    private byte[] _data = null;

    /// <inheritdoc/>
    public override int GetEncodedSize() => FixedPrefixSize;

    /// <inheritdoc/>
    public override TypeID Type => TypeID.F_SG_NA_1;

    /// <inheritdoc/>
    public override bool SupportsSequence => false;

    /// <summary>本段所属文件的文件名（NOF）。</summary>
    public NameOfFile NOF
    {
        get => _nof;
        set => _nof = value;
    }

    /// <summary>本段所属的段号。</summary>
    public byte NameOfSection
    {
        get => _nameOfSection;
        set => _nameOfSection = value;
    }

    /// <summary>段数据长度 LOS。</summary>
    public byte LengthOfSegment
    {
        get => _los;
        set => _los = value;
    }

    /// <summary>段数据本体（长度等于 <see cref="LengthOfSegment"/>）。</summary>
    public byte[] SegmentData => _data;

    /// <summary>
    /// 组包构造。数据数组按引用持有，不再复制。
    /// </summary>
    public FileSegment(int objectAddress, NameOfFile nof, byte nameOfSection, byte[] data)
        : base(objectAddress)
    {
        _nof = nof;
        _nameOfSection = nameOfSection;
        _data = data;
        _los = (byte)data.Length;
    }

    internal FileSegment(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
        : base(parameters, msg, startIndex, isSequence)
    {
        var position = FileMessageCodec.BodyStart(parameters, msg, startIndex, isSequence, FixedPrefixSize);

        _nof = (NameOfFile)BinaryPrimitives.ReadUInt16LittleEndian(msg.Slice(position, 2));
        position += 2;

        _nameOfSection = msg[position++];
        _los = msg[position++];

        if (_los > GetMaxDataSize(parameters))
        {
            throw new ASDUParsingException("段数据长度超出单 ASDU 上限");
        }

        if (msg.Length - position < _los)
        {
            throw new ASDUParsingException("报文长度不足以容纳声明的段数据");
        }

        _data = msg.Slice(position, _los).ToArray();
    }

    /// <summary>
    /// 单条 ASDU 能容纳的最大段数据字节数：从 ASDU 上限中扣除头部、IOA 与固定前缀。
    /// </summary>
    public static int GetMaxDataSize(ApplicationLayerParameters parameters)
        => parameters.MaxAsduLength
            - parameters.SizeOfTypeId
            - parameters.SizeOfVSQ
            - parameters.SizeOfCA
            - parameters.SizeOfCOT
            - parameters.SizeOfIOA
            - FixedPrefixSize;

    internal override bool HasAsduWriterBody => true;

    /// <inheritdoc/>
    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        if (_data.Length > GetMaxDataSize(parameters))
        {
            throw new ASDUParsingException("段数据长度超出单 ASDU 上限");
        }

        w.WriteIntLittleEndian((int)_nof, 2);
        w.WriteByte(_nameOfSection);
        w.WriteByte(_los);
        w.WriteBytes(_data);
    }
}
