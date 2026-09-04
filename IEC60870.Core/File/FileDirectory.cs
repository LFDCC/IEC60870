//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 文件目录条目消息 F_DR_TA_1(126)
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;
using System.Buffers.Binary;

namespace IEC60870.Core;

/// <summary>
/// 文件目录（F_DR_TA_1，类型号 126）：一条目录项描述一个可传输文件——类别、长度、
/// 状态（SOF）与创建时刻（CP56Time2a）。SOF 低 5 位为状态码，高位依次为 FA/FOR/LFD 标志。
/// </summary>
public class FileDirectory : InformationObject
{
    // SOF 位定义（IEC 60870-5-101 7.2.6.38）
    private const byte StatusMask = 0x1F;
    private const byte LfdBit = 0x20;
    private const byte ForBit = 0x40;
    private const byte FaBit = 0x80;
    private const int MaxStatusValue = 31;

    private NameOfFile _nof;
    private int _lengthOfFile;
    private byte _sof;
    private CP56Time2a _creationTime;

    /// <inheritdoc/>
    public override TypeID Type => TypeID.F_DR_TA_1;

    /// <inheritdoc/>
    public override bool SupportsSequence => true;

    /// <summary>文件名（NOF）。</summary>
    public NameOfFile NOF
    {
        get => _nof;
        set => _nof = value;
    }

    /// <summary>文件长度（字节）。</summary>
    public int LengthOfFile
    {
        get => _lengthOfFile;
        set => _lengthOfFile = value;
    }

    /// <summary>SOF 原始字节（文件状态）。</summary>
    public byte SOF
    {
        get => _sof;
        set => _sof = value;
    }

    /// <summary>SOF 低 5 位状态码。</summary>
    public int STATUS => _sof & StatusMask;

    /// <summary>LFD 标志：目录后文件将被删除。</summary>
    public bool LFD => (_sof & LfdBit) != 0;

    /// <summary>FOR 标志：文件为只读。</summary>
    public bool FOR => (_sof & ForBit) != 0;

    /// <summary>FA 标志：文件可用（先于传输被激活）。</summary>
    public bool FA => (_sof & FaBit) != 0;

    /// <summary>直接以 SOF 字节构造目录项。</summary>
    public FileDirectory(int objectAddress, NameOfFile nof, int lengthOfFile, byte sof, CP56Time2a creationTime)
        : base(objectAddress)
    {
        _nof = nof;
        _lengthOfFile = lengthOfFile;
        _sof = sof;
        _creationTime = creationTime;
    }

    /// <summary>
    /// 以分散的状态码与三个标志位构造 SOF；状态码会被夹取到 0..31。
    /// </summary>
    public FileDirectory(int objectAddress, NameOfFile nof, int lengthOfFile, int status, bool LFD, bool FOR, bool FA, CP56Time2a creationTime)
        : base(objectAddress)
    {
        _nof = nof;
        _lengthOfFile = lengthOfFile;

        var clamped = Math.Clamp(status, 0, MaxStatusValue);
        var sof = (byte)clamped;

        sof |= LFD ? LfdBit : (byte)0;
        sof |= FOR ? ForBit : (byte)0;
        sof |= FA ? FaBit : (byte)0;

        _sof = sof;
        _creationTime = creationTime;
    }

    internal FileDirectory(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
        : base(parameters, msg, startIndex, isSequence)
    {
        var position = FileMessageCodec.BodyStart(parameters, msg, startIndex, isSequence, GetEncodedSize());

        _nof = (NameOfFile)BinaryPrimitives.ReadUInt16LittleEndian(msg.Slice(position, 2));
        position += 2;

        _lengthOfFile = FileMessageCodec.ReadUInt24LittleEndian(msg, position);
        position += 3;

        _sof = msg[position++];

        // 剩余 7 字节为文件创建时刻
        _creationTime = new CP56Time2a(msg, position);
    }

    internal override bool HasAsduWriterBody => true;

    /// <inheritdoc/>
    protected internal override void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
        base.EncodeBody(ref w, parameters, isSequence);

        w.WriteIntLittleEndian((int)_nof, 2);
        w.WriteIntLittleEndian(_lengthOfFile, 3);
        w.WriteByte(_sof);
        w.WriteBytes(_creationTime.AsSpan());
    }
}
