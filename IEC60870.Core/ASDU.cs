//------------------------------------------------------------------------------
//  IEC60870.Core.NET — ASDU 应用层消息模型（类型标识/可变结构限定词/传送原因/公共地址与信息体载荷）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

namespace IEC60870.Core;

/// <summary>
/// 应用层消息（ASDU）。承载通用报文头信息（TI/VSQ/COT/Oa/CA）与一组同类型信息对象，
/// 既用于组包发送，也用于解析接收报文。
/// </summary>
public class ASDU
{
    // COT 字节内的控制位。
    private const byte MaskTest = 0x80;
    private const byte MaskNegative = 0x40;
    private const byte MaskCotValue = 0x3F;
    // VSQ 字节内的序列位。
    private const byte MaskSequence = 0x80;
    private const byte MaskElementCount = 0x7F;

    /* ---- 以下内部字段由同程序集的 AsduEncoder / AsduDecoder 直接读写 ---- */
    internal ApplicationLayerParameters _parameters;

    internal TypeID _typeId;
    internal bool _hasTypeId;

    // 可变结构限定词（VSQ）：最高位为序列标志，低 7 位为信息对象个数
    internal byte _vsq;

    internal CauseOfTransmission _cot;

    // 源发站地址（Oa）
    internal byte _oa;

    // 本报文是否为试验报文
    internal bool _isTest;

    // 本报文是否为否定确认
    internal bool _isNegative;

    // ASDU 公共地址（CA）
    internal int _ca;

    internal int _spaceLeft = 0;

    internal byte[] _payload = null;
    internal List<InformationObject> _informationObjects = null;

    internal PrivateInformationObjectTypes _privateObjectTypes = null;

    /// <summary>
    /// 解码该 ASDU 元素时使用的类型处理器注册表。
    /// null(默认)时使用 <see cref="AsduTypeHandlerRegistry.Default"/>;
    /// 私有类型场景可注入含自定义 <see cref="IAsduTypeHandler"/> 的独立注册表。
    /// </summary>
    public AsduTypeHandlerRegistry TypeHandlers { get; set; }

    /// <summary>类型标识（TI）。</summary>
    public TypeID TypeId => _typeId;

    /// <summary>传送原因（COT），可读写。</summary>
    public CauseOfTransmission Cot
    {
        get => _cot;
        set => _cot = value;
    }

    /// <summary>源发站地址（Oa）。</summary>
    public byte Oa => _oa;

    /// <summary>报文是否为试验报文。</summary>
    public bool IsTest => _isTest;

    /// <summary>报文是否为否定确认，可读写。</summary>
    public bool IsNegative
    {
        get => _isNegative;
        set => _isNegative = value;
    }

    /// <summary>ASDU 公共地址（CA）。</summary>
    public int Ca => _ca;

    /// <summary>
    /// 信息体是否按序列组织：序列内各信息对象共用首地址、地址依次递增。
    /// </summary>
    public bool IsSequence => (_vsq & MaskSequence) != 0;

    /// <summary>VSQ 低 7 位声明的信息对象（元素）个数。</summary>
    public int NumberOfElements => _vsq & MaskElementCount;

    /// <summary>
    /// 以默认类型 M_SP_NA_1 之外的任意类型创建待发送 ASDU（类型在首个信息对象加入时确定）。
    /// </summary>
    /// <param name="parameters">编解码使用的应用层参数</param>
    /// <param name="cot">传送原因（COT）</param>
    /// <param name="isTest">是否为试验报文</param>
    /// <param name="isNegative">是否为否定确认</param>
    /// <param name="oa">源发站地址（Oa）</param>
    /// <param name="ca">ASDU 公共地址（CA）</param>
    /// <param name="isSequence">信息体是否按序列组织</param>
    public ASDU(ApplicationLayerParameters parameters, CauseOfTransmission cot, bool isTest, bool isNegative, byte oa, int ca, bool isSequence)
        : this(parameters, TypeID.M_SP_NA_1, cot, isTest, isNegative, oa, ca, isSequence)
    {
        _hasTypeId = false;
    }

    internal ASDU(ApplicationLayerParameters parameters, TypeID typeId, CauseOfTransmission cot, bool isTest, bool isNegative, byte oa, int ca, bool isSequence)
    {
        _parameters = parameters;
        _typeId = typeId;
        _cot = cot;
        _isTest = isTest;
        _isNegative = isNegative;
        _oa = oa;
        _ca = ca;
        _spaceLeft = parameters.MaxAsduLength -
            parameters.SizeOfTypeId - parameters.SizeOfVSQ - parameters.SizeOfCA - parameters.SizeOfCOT;

        _vsq = isSequence ? MaskSequence : (byte)0;
        _hasTypeId = true;
    }

    /// <summary>
    /// 从接收字节缓冲的指定位置解析出 ASDU 头，并把剩余信息体区域原样保存为载荷。
    /// </summary>
    public ASDU(ApplicationLayerParameters parameters, byte[] msg, int bufPos, int msgLength)
    {
        _parameters = parameters;

        // 头部固定为 TI + VSQ，外加 COT 与 CA 的可变长度
        var asduHeaderSize = 2 + parameters.SizeOfCOT + parameters.SizeOfCA;

        if ((msgLength - bufPos) < asduHeaderSize)
        {
            throw new ASDUParsingException("Message header too small");
        }

        _typeId = (TypeID)msg[bufPos++];
        _vsq = msg[bufPos++];
        _hasTypeId = true;

        var cotByte = msg[bufPos++];
        _isTest = (cotByte & MaskTest) != 0;
        _isNegative = (cotByte & MaskNegative) != 0;
        _cot = (CauseOfTransmission)(cotByte & MaskCotValue);

        if (parameters.SizeOfCOT == 2)
        {
            _oa = msg[bufPos++];
        }

        // CA 按小端逐字节拼装
        _ca = msg[bufPos++];
        if (parameters.SizeOfCA > 1)
        {
            _ca += (msg[bufPos++] * 0x100);
        }

        var payloadSize = msgLength - bufPos;

        // 校验 payload 长度是否足以容纳 VSQ 声明的信息对象数（代码评审 #15 / 原 TODO）。
        // 短 payload + 过大 VSQ 会让 GetElement(index) 算出越界偏移，导致部分类型 IndexOutOfRange。
        var expected = AsduDecoder.ComputeExpectedPayloadSize(this);
        if (expected >= 0 && payloadSize < expected)
        {
            throw new ASDUParsingException("Payload too small for declared VSQ/TypeID (need " + expected + ", got " + payloadSize + ")");
        }

        _payload = new byte[payloadSize];
        Buffer.BlockCopy(msg, bufPos, _payload, 0, payloadSize);
    }

    /// <summary>
    /// 由零拷贝视图 <see cref="AsduView"/> 物化为 ASDU 对象：头字段逐项直接赋值
    /// （不经字节缓冲版构造的二次解析），信息体区域仅做一次拷贝后脱离接收缓冲区长期持有。
    /// </summary>
    /// <param name="view">叠加在接收缓冲区上的 ASDU 只读视图</param>
    /// <exception cref="IEC60870.Core.ASDUParsingException">视图未覆盖完整 ASDU 头部，
    /// 或信息体长度不足以容纳 VSQ 声明的信息对象数时抛出（与字节缓冲版构造的校验一致）</exception>
    public ASDU(AsduView view)
    {
        if (!view.IsValid)
        {
            throw new ASDUParsingException("Message header too small");
        }

        _parameters = view.Parameters;
        _typeId = view.TypeId;
        _hasTypeId = true;
        _vsq = view.Vsq;
        _cot = view.Cot;
        _isTest = view.IsTest;
        _isNegative = view.IsNegative;
        _oa = (byte)view.Oa;
        _ca = view.Ca;

        // 校验 payload 长度是否足以容纳 VSQ 声明的信息对象数（与字节缓冲版构造一致），
        // 短 payload + 过大 VSQ 会让 GetElement(index) 算出越界偏移。
        _payload = view.InformationObjects.ToArray();
        var expected = AsduDecoder.ComputeExpectedPayloadSize(this);
        if (expected >= 0 && _payload.Length < expected)
        {
            throw new ASDUParsingException("Payload too small for declared VSQ/TypeID (need " + expected + ", got " + _payload.Length + ")");
        }
    }

    /// <summary>
    /// 向 ASDU 追加一个信息对象（要求与已有信息对象同型）。
    /// 空间不足或序列地址不连续时返回 <c>false</c>。
    /// </summary>
    /// <param name="io">待追加的信息对象</param>
    public bool AddInformationObject(InformationObject io)
    {
        return AsduEncoder.AddInformationObject(io, this);
    }

    public void Encode(Frame frame, ApplicationLayerParameters parameters)
    {
        AsduEncoder.Encode(frame, parameters, this);
    }

    /// <summary>
    /// 将 ASDU 直写进 <see cref="AsduWriter"/>（连续缓冲、零中间分配、无逐字节虚调用）。
    /// CS104 发送热路径的编码出口；调用方须保证 writer 底层缓冲足够容纳整帧
    /// （<see cref="ApplicationLayerParameters.MaxAsduLength"/> 字节）。
    /// </summary>
    public void Encode(ref AsduWriter writer, ApplicationLayerParameters parameters)
    {
        AsduEncoder.EncodeAsdu(ref writer, parameters, this);
    }

    /// <summary>
    /// 将 ASDU 编码为字节数组。
    /// </summary>
    /// <returns>
    /// 编码后的字节；若实际编码长度与预期缓冲尺寸不符（理论上仅在 <see cref="AddInformationObject"/>
    /// 与 <see cref="Encode"/> 之间参数被改动时才可能发生），返回 <c>null</c>。
    /// 调用方必须判空（代码评审 #19）。如希望永不返回 null，可改用 <see cref="Encode"/> 配合可调长缓冲。
    /// </returns>
    public byte[] AsByteArray()
    {
        return AsduEncoder.AsByteArray(this);
    }

    /// <summary>按索引取信息对象，支持私有类型注册表。</summary>
    public InformationObject GetElement(int index, PrivateInformationObjectTypes privateObjectTypes)
    {
        return AsduDecoder.GetElement(index, privateObjectTypes, this);
    }

    /// <summary>按索引取信息对象，支持用户自定义 IO 工厂。</summary>
    public InformationObject GetElement(int index, IPrivateIOFactory ioFactory)
    {
        return AsduDecoder.GetElement(index, ioFactory, this);
    }

    /// <summary>
    /// 类型安全版 <see cref="GetElement(int)"/>：按报文 typeId 解析后，断言实际类型为 <typeparamref name="T"/>。
    /// </summary>
    /// <typeparam name="T">期望的信息对象具体类型，须为 <see cref="InformationObject"/> 的子类</typeparam>
    /// <param name="index">元素索引（从 0 开始）</param>
    /// <returns>类型为 <typeparamref name="T"/> 的信息对象</returns>
    /// <exception cref="IEC60870.Core.ASDUParsingException">
    /// 当 index 越界、解析结果为 <c>null</c>，或解析出的实际类型不是 <typeparamref name="T"/> 时抛出
    /// </exception>
    public T GetElement<T>(int index) where T : InformationObject
    {
        return AsduDecoder.GetElement<T>(index, this);
    }

    /// <summary>
    /// 按索引解码信息对象（索引从 0 开始）。
    /// </summary>
    /// <exception cref="IEC60870.Core.ASDUParsingException">报文解析失败时抛出</exception>
    public InformationObject GetElement(int index)
    {
        return AsduDecoder.GetElement(index, this);
    }

    public override string ToString()
    {
        var builder = new StringBuilder()
            .Append("TypeID: ").Append(_typeId)
            .Append(" COT: ").Append(_cot);

        if (_parameters.SizeOfCOT == 2)
        {
            builder.Append(" Oa: ").Append(_oa);
        }

        if (_isTest)
        {
            builder.Append(" [TEST]");
        }

        if (_isNegative)
        {
            builder.Append(" [NEG]");
        }

        if (IsSequence)
        {
            builder.Append(" [SEQ]");
        }

        builder.Append(" elements: ").Append(NumberOfElements);
        builder.Append(" CA: ").Append(_ca);

        return builder.ToString();
    }
}
