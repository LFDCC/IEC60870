//------------------------------------------------------------------------------
//  IEC60870.Core.NET — ASDU 编码器：信息对象追加、空间核算与报文头/信息体序列化
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

namespace IEC60870.Core;

/// <summary>
/// ASDU 的内部编码实现。原先内联在 <see cref="ASDU"/> 中的组包与容量核算逻辑集中于此，
/// 全部为 internal 静态方法，以参数形式接收目标 <see cref="ASDU"/> 实例。
/// </summary>
internal static class AsduEncoder
{
    // VSQ 中的序列标志位；低 7 位为信息对象计数。
    private const byte SequenceFlag = 0x80;
    private const byte CountMask = 0x7F;
    // COT 字节中的试验位与否定确认位。
    private const byte TestFlag = 0x80;
    private const byte NegativeFlag = 0x40;
    // 单条 ASDU 可容纳的信息对象上限（受 VSQ 计数位宽限制）。
    private const int MaxObjects = 0x7F;

    /// <summary>
    /// 把信息对象并入 ASDU：校验类型一致、序列地址连续、剩余空间足够，
    /// 全部满足才更新容量与 VSQ 计数。
    /// </summary>
    /// <returns>成功并入返回 <c>true</c>；空间不足或序列地址不连续返回 <c>false</c>。</returns>
    internal static bool AddInformationObject(InformationObject io, ASDU asdu)
    {
        asdu._informationObjects ??= new List<InformationObject>();

        if (!asdu._hasTypeId)
        {
            asdu._typeId = io.Type;
            asdu._hasTypeId = true;
        }
        else if (io.Type != asdu._typeId)
        {
            throw new ArgumentException("Invalid information object type: expected "
                + asdu._typeId + " was " + io.Type);
        }

        var objects = asdu._informationObjects;
        if (objects.Count >= MaxObjects)
        {
            return false;
        }

        // 计算本对象实际占用的字节数：非序列（或序列首对象）需另计 IOA 前缀
        var ioaSize = asdu._parameters.SizeOfIOA;
        var objectSize = io.GetEncodedSize();

        if (asdu.IsSequence)
        {
            if (objects.Count == 0)
            {
                objectSize += ioaSize;
            }
            else if (io.ObjectAddress != objects[0].ObjectAddress + objects.Count)
            {
                // 序列要求地址严格递增且连续
                return false;
            }
        }
        else
        {
            objectSize += ioaSize;
        }

        if (objectSize > asdu._spaceLeft)
        {
            return false;
        }

        asdu._spaceLeft -= objectSize;
        objects.Add(io);
        asdu._vsq = (byte)((asdu._vsq & SequenceFlag) | objects.Count);

        return true;
    }

    /// <summary>把 TI/VSQ/COT/OA/CA 六个头部字段写入帧。</summary>
    private static void WriteHeader(Frame frame, ASDU asdu, ApplicationLayerParameters parameters)
    {
        frame.SetNextByte((byte)asdu._typeId);
        frame.SetNextByte(asdu._vsq);
        frame.SetNextByte(FlagsOf(asdu));

        if (parameters.SizeOfCOT == 2)
        {
            frame.SetNextByte(asdu._oa);
        }

        frame.SetNextByte((byte)(asdu._ca & 0xFF));
        if (parameters.SizeOfCA > 1)
        {
            frame.SetNextByte((byte)(asdu._ca >> 8));
        }
    }

    /// <summary>把 COT 枚举与试验/否定确认标志合并成线上 COT 字节。</summary>
    private static byte FlagsOf(ASDU asdu)
    {
        var cotByte = (byte)asdu._cot;

        if (asdu._isTest)
        {
            cotByte |= TestFlag;
        }

        if (asdu._isNegative)
        {
            cotByte |= NegativeFlag;
        }

        return cotByte;
    }

    /// <summary>
    /// 将 ASDU 序列化到 <see cref="Frame"/>。若来自接收路径（已有原始载荷）则整段搬运，
    /// 否则逐个编码内存中的信息对象。
    /// </summary>
    internal static void Encode(Frame frame, ApplicationLayerParameters parameters, ASDU asdu)
    {
        WriteHeader(frame, asdu, parameters);

        if (asdu._payload != null)
        {
            frame.AppendBytes(asdu._payload);
            return;
        }

        // 首个对象一律自带 IOA；其后对象仅在序列模式下省略 IOA
        var isFirstObject = true;
        foreach (var io in asdu._informationObjects)
        {
            io.Encode(frame, parameters, asdu.IsSequence && !isFirstObject);
            isFirstObject = false;
        }
    }

    /// <summary>
    /// Encodes the ASDU directly into an <see cref="AsduWriter"/>（连续缓冲直写，
    /// 无中间 Frame 分配、无逐字节虚调用）。为 CS104 发送热路径的编码出口。
    /// </summary>
    /// <remarks>
    /// 单帧 APDU 预留整段后：头部（TypeId/VSQ/COT/OA/CA）直写，随后逐信息体调用
    /// <see cref="InformationObject.EncodeBody(ref AsduWriter,ApplicationLayerParameters,bool)"/>
    /// （每 IO 一次虚调用，原为每字节一次）。对于仅 override
    /// <c>Encode(Frame,...)</c> 的用户自定义 IO（HasAsduWriterBody=false），
    /// 回退到 <see cref="InformationObject.Encode(Frame,ApplicationLayerParameters,bool)"/>，
    /// 以 <see cref="AsduWriter.Remaining"/> 为底层包一个 Frame 兼容层。
    /// </remarks>
    internal static void EncodeAsdu(ref AsduWriter w, ApplicationLayerParameters parameters, ASDU asdu)
    {
        w.WriteByte((byte)asdu._typeId);
        w.WriteByte(asdu._vsq);
        w.WriteByte(FlagsOf(asdu));

        if (parameters.SizeOfCOT == 2)
        {
            w.WriteByte(asdu._oa);
        }

        w.WriteByte((byte)(asdu._ca & 0xFF));
        if (parameters.SizeOfCA > 1)
        {
            w.WriteByte((byte)(asdu._ca >> 8));
        }

        if (asdu._payload != null)
        {
            w.WriteBytes(asdu._payload);
            return;
        }

        var isFirstObject = true;
        foreach (var io in asdu._informationObjects)
        {
            // 序列模式下除首对象外均省略 IOA
            var omitIoa = asdu.IsSequence && !isFirstObject;

            if (io.HasAsduWriterBody)
            {
                // 内置信息对象：非序列时先写 IOA，再直写信息体
                if (!omitIoa)
                {
                    w.WriteIntLittleEndian(io.ObjectAddress, parameters.SizeOfIOA);
                }

                io.EncodeBody(ref w, parameters, omitIoa);
            }
            else
            {
                // 用户自定义 IO（仅 override Encode(Frame,...)）：编码到临时缓冲后并入写入器。
                // 自定义 IO 属罕见路径，一次小分配可接受（原 Frame 路径本就逐帧分配）。
                // io.Encode 内部 base.Encode 同样会经 GetRemainingSpan 适配 EncodeBody。
                var scratch = new byte[parameters.MaxAsduLength];
                var shim = new BufferFrame(scratch, 0);
                io.Encode(shim, parameters, omitIoa);
                w.WriteBytes(scratch.AsSpan(0, shim.GetMsgSize()));
            }

            isFirstObject = false;
        }
    }

    /// <summary>
    /// 将 ASDU 编码为独立字节数组。
    /// </summary>
    /// <returns>
    /// 编码结果；若实际写出的长度与按容量核算得到的期望长度不一致则返回 <c>null</c>。
    /// </returns>
    internal static byte[] AsByteArray(ASDU asdu)
    {
        var expectedSize = asdu._parameters.MaxAsduLength - asdu._spaceLeft;
        var frame = new BufferFrame(new byte[expectedSize], 0);

        Encode(frame, asdu._parameters, asdu);

        return frame.GetMsgSize() == expectedSize ? frame.GetBuffer() : null;
    }
}
