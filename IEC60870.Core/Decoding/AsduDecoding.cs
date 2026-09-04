//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

namespace IEC60870.Core;

/// <summary>
/// ASDU 元素解码分派器。寻址策略(单一事实源)与 <see cref="IAsduTypeHandler"/> 策略注册表协同:
/// 本类只负责「按 Kind/序列模式计算元素偏移」,具体类型的解码委托给注册表中的处理器。
/// </summary>
/// <remarks>
/// 三种寻址策略:
/// <list type="bullet">
/// <item><b>Monitor + 序列</b>:首元素带 IOA 前缀,后续按 PayloadSize 步进,IOA = 首 IOA + index;</item>
/// <item><b>Monitor 非序列 / Command</b>:固定步进 SizeOfIOA + PayloadSize;</item>
/// <item><b>File</b>:恒定偏移 0,单元素。</item>
/// </list>
/// </remarks>
internal static class AsduDecoder
{
    /// <summary>解析 ASDU 元素时使用的注册表(ASDU 级注入优先,否则默认注册表)。</summary>
    private static AsduTypeHandlerRegistry ResolveRegistry(AsduTypeHandlerRegistry registry)
        => registry ?? AsduTypeHandlerRegistry.Default;

    /// <summary>
    /// 计算声明元素数所需的 payload 字节数(用于构造期长度校验,防止 GetElement 越界)。
    /// 返回 -1 表示跳过校验(未注册类型/文件类型/变长类型由各自解码器自行处理);
    /// 否则为期望的最小 payload 长度。尺寸/方向全部查注册表,消除重复尺寸表。
    /// </summary>
    internal static int ComputeExpectedPayloadSize(ASDU asdu)
    {
        var n = asdu.NumberOfElements;
        if (n == 0)
        {
            return 0;
        }

        if (!ResolveRegistry(asdu.TypeHandlers).TryGetSlot(asdu._typeId, out var slot))
        {
            return -1; // 未知/私有类型:跳过,交给各自解码器
        }

        // 文件类型(单元素、偏移 0)与变长类型:跳过构造期尺寸校验
        if (slot.Kind == AsduTypeKind.File || slot.PayloadSize < 0)
        {
            return -1;
        }

        // Monitor 序列:首元素带 IOA 前缀 + n*payload;非序列 / Command:n*(SizeOfIOA+payload)
        if (slot.Kind == AsduTypeKind.Monitor && asdu.IsSequence)
        {
            return asdu._parameters.SizeOfIOA + n * slot.PayloadSize;
        }

        return n * (asdu._parameters.SizeOfIOA + slot.PayloadSize);
    }

    /// <summary>
    /// 从 ASDU(已拷贝的 payload)按索引解码信息对象。
    /// </summary>
    internal static InformationObject GetElement(int index, ASDU asdu)
    {
        if (index < 0 || index >= asdu.NumberOfElements)
        {
            throw new ASDUParsingException("Index out of range");
        }

        var retVal = DecodeElement(
            asdu._payload, asdu._typeId, asdu.IsSequence, asdu.NumberOfElements,
            index, asdu._parameters, ResolveRegistry(asdu.TypeHandlers));

        // 未知/私有类型:委托 IPrivateIOFactory 旁路(兼容既有私有类型扩展)
        if (retVal == null && asdu._privateObjectTypes != null)
        {
            var ioFactory = asdu._privateObjectTypes.GetFactory(asdu._typeId);

            if (ioFactory != null)
            {
                var elementSize = asdu._parameters.SizeOfIOA + ioFactory.GetEncodedSize();

                if (asdu.IsSequence)
                {
                    var ioa = InformationObject.ParseInformationObjectAddress(asdu._parameters, asdu._payload, 0);

                    retVal = ioFactory.Decode(asdu._parameters, asdu._payload, index * elementSize, true);

                    retVal.ObjectAddress = ioa + index;
                }
                else
                {
                    retVal = ioFactory.Decode(asdu._parameters, asdu._payload, index * elementSize, false);
                }
            }
        }

        if (retVal == null)
        {
            throw new ASDUParsingException("Unknown ASDU type id:" + asdu._typeId);
        }

        return retVal;
    }

    /// <summary>
    /// 从 ASDU 按索引解码信息对象,使用私有类型注册表。
    /// </summary>
    internal static InformationObject GetElement(int index, PrivateInformationObjectTypes privateObjectTypes, ASDU asdu)
    {
        asdu._privateObjectTypes = privateObjectTypes;

        return GetElement(index, asdu);
    }

    /// <summary>
    /// 从 ASDU 按索引解码信息对象,使用用户指定的私有 IO 工厂。
    /// </summary>
    internal static InformationObject GetElement(int index, IPrivateIOFactory ioFactory, ASDU asdu)
    {
        if (ioFactory == null)
        {
            return null;
        }

        if (index < 0 || index >= asdu.NumberOfElements)
        {
            throw new ASDUParsingException("Index out of range");
        }

        var elementSize = ioFactory.GetEncodedSize();
        int offset;
        int needed;

        if (asdu.IsSequence)
        {
            if (asdu._payload.Length < asdu._parameters.SizeOfIOA)
            {
                throw new ASDUParsingException("Payload too small for sequence IOA prefix");
            }

            offset = asdu._parameters.SizeOfIOA + (index * elementSize);
            needed = elementSize;
        }
        else
        {
            offset = index * (asdu._parameters.SizeOfIOA + elementSize);
            needed = asdu._parameters.SizeOfIOA + elementSize;
        }

        if (offset < 0 || offset + needed > asdu._payload.Length)
        {
            throw new ASDUParsingException("Payload too small for declared element size/VSQ (offset=" + offset + ", needed=" + needed + ", payload=" + asdu._payload.Length + ")");
        }

        InformationObject retVal;

        if (asdu.IsSequence)
        {
            var ioa = InformationObject.ParseInformationObjectAddress(asdu._parameters, asdu._payload, 0);

            retVal = ioFactory.Decode(asdu._parameters, asdu._payload, offset, true);

            retVal.ObjectAddress = ioa + index;
        }
        else
        {
            retVal = ioFactory.Decode(asdu._parameters, asdu._payload, offset, false);
        }

        return retVal;
    }

    /// <summary>
    /// 类型安全版 <see cref="GetElement(int, ASDU)"/>。
    /// </summary>
    internal static T GetElement<T>(int index, ASDU asdu) where T : InformationObject
    {
        var io = GetElement(index, asdu);

        if (io == null)
        {
            throw new ASDUParsingException("Element " + index + " is null");
        }

        if (io is not T typed)
        {
            throw new ASDUParsingException(
                "Element " + index + " decoded as " + io.GetType().Name + ", not " + typeof(T).Name);
        }

        return typed;
    }

    // ──────────────────────────────────────────────────────────────
    //  零拷贝直解:直接从接收缓冲的 ASDU 切片解码(供 AsduView 使用,
    //  无需先拷贝为 ASDU 对象 —— 接收热路径无额外分配)。
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 从 ASDU 信息对象区域切片(零拷贝)按索引解码信息对象。
    /// </summary>
    /// <param name="payload">信息对象区域切片(AsduView.InformationObjects)。</param>
    /// <param name="typeId">ASDU 类型标识。</param>
    /// <param name="isSequence">序列寻址模式。</param>
    /// <param name="numberOfElements">元素总数(VSQ 低 7 位)。</param>
    /// <param name="index">元素索引(从 0 开始)。</param>
    /// <param name="parameters">应用层参数。</param>
    /// <param name="registry">类型处理器注册表;null 使用默认注册表。</param>
    /// <returns>解码出的信息对象;类型未注册返回 null(调用方决定回退路径)。</returns>
    internal static InformationObject DecodeElement(ReadOnlySpan<byte> payload, TypeID typeId,
        bool isSequence, int numberOfElements, int index,
        ApplicationLayerParameters parameters, AsduTypeHandlerRegistry registry)
    {
        if (index < 0 || index >= numberOfElements)
        {
            throw new ASDUParsingException("Index out of range");
        }

        if (!ResolveRegistry(registry).TryGetSlot(typeId, out var slot))
        {
            return null;
        }

        var handler = slot.Handler;

        if (slot.Kind == AsduTypeKind.File)
        {
            // 文件类型:恒定偏移 0,单元素 ASDU
            return handler.Decode(parameters, payload, 0, false);
        }

        if (slot.Kind == AsduTypeKind.Monitor && isSequence)
        {
            // 监视序列:首元素带 IOA 前缀,后续按 PayloadSize 步进
            if (payload.Length < parameters.SizeOfIOA)
            {
                throw new ASDUParsingException("Payload too small for sequence IOA prefix");
            }

            var ioa = InformationObject.ParseInformationObjectAddress(parameters, payload, 0);
            var offset = parameters.SizeOfIOA + (index * slot.PayloadSize);

            var retVal = handler.Decode(parameters, payload, offset, true);
            retVal.ObjectAddress = ioa + index;
            return retVal;
        }

        // 监视非序列 / 命令类型:固定步进 SizeOfIOA + PayloadSize
        {
            var stride = parameters.SizeOfIOA + slot.PayloadSize;
            var offset = index * stride;
            if (offset + stride > payload.Length)
            {
                throw new ASDUParsingException(
                    "Payload too small for element (offset=" + offset + ", needed=" + stride + ", payload=" + payload.Length + ")");
            }

            return handler.Decode(parameters, payload, offset, false);
        }
    }

    /// <summary>
    /// 类型安全版 <see cref="DecodeElement"/>。
    /// </summary>
    internal static T DecodeElement<T>(ReadOnlySpan<byte> payload, TypeID typeId,
        bool isSequence, int numberOfElements, int index,
        ApplicationLayerParameters parameters, AsduTypeHandlerRegistry registry) where T : InformationObject
    {
        var io = DecodeElement(payload, typeId, isSequence, numberOfElements, index, parameters, registry);

        if (io == null)
        {
            throw new ASDUParsingException("Unknown ASDU type id:" + typeId);
        }

        if (io is not T typed)
        {
            throw new ASDUParsingException(
                "Element " + index + " decoded as " + io.GetType().Name + ", not " + typeof(T).Name);
        }

        return typed;
    }
}
