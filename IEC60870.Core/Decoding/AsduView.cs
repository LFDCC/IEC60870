//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;


namespace IEC60870.Core;

    /// <summary>
    /// 零拷贝 ASDU 只读视图（<c>ref struct</c>）。
    /// 直接叠加在接收缓冲区的 ASDU 字节切片上按需解析头字段，不分配堆内存、不复制数据。
    /// </summary>
    /// <remarks>
    /// ASDU 布局（字段宽度由 <see cref="ApplicationLayerParameters"/> 决定，均为小端）：
    /// <code>
    /// TypeID(1) | VSQ(1) | COT(SizeOfCOT: 1 或 2, 含 OA) | CA(SizeOfCA) | 信息对象...
    /// COT 字节: bit7=test, bit6=negative, bits0-5=cause
    /// VSQ 字节: bit7=sequence, bits0-6=元素个数
    /// </code>
    /// 需要遍历各信息对象的具体数据时，用 <see cref="InformationObjects"/> 取得起始切片，
    /// 再交由类型化解码器（<c>ASDUDecoder</c>）处理。
    /// </remarks>
    public readonly ref struct AsduView
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly ApplicationLayerParameters _p;

        /// <summary>
        /// 在一段 ASDU 字节切片上创建视图。
        /// </summary>
        /// <param name="asdu">完整 ASDU 字节（从 TypeID 开始，不含 APCI）。</param>
        /// <param name="parameters">应用层参数（决定 COT/CA/IOA 宽度）。</param>
        public AsduView(ReadOnlySpan<byte> asdu, ApplicationLayerParameters parameters)
        {
            _data = asdu;
            _p = parameters;
        }

        /// <summary>视图是否覆盖了至少完整的 ASDU 头部。</summary>
        public bool IsValid => _data.Length >= HeaderLength;

        /// <summary>ASDU 头部长度（TypeID + VSQ + COT + CA）。</summary>
        public int HeaderLength => 2 + _p.SizeOfCOT + _p.SizeOfCA;

        /// <summary>类型标识。</summary>
        public TypeID TypeId => (TypeID)_data[0];

        /// <summary>可变结构限定词原始字节。</summary>
        public byte Vsq => _data[1];

        /// <summary>信息元素个数（VSQ 低 7 位）。</summary>
        public int NumberOfElements => _data[1] & 0x7f;

        /// <summary>是否为连续（sequence）寻址（VSQ bit7）。</summary>
        public bool IsSequence => (_data[1] & 0x80) == 0x80;

        /// <summary>传输原因（COT 字节低 6 位）。</summary>
        public CauseOfTransmission Cot => (CauseOfTransmission)(_data[2] & 0x3f);

        /// <summary>测试位（COT 字节 bit7）。</summary>
        public bool IsTest => (_data[2] & 0x80) == 0x80;

        /// <summary>否定确认位（COT 字节 bit6）。</summary>
        public bool IsNegative => (_data[2] & 0x40) == 0x40;

        /// <summary>源发地址 OA（仅当 SizeOfCOT == 2 时有效，否则为 0）。</summary>
        public int OriginatorAddress => _p.SizeOfCOT == 2 ? _data[3] : 0;

        /// <summary>公共地址 CA（小端，宽度由 SizeOfCA 决定）。</summary>
        public int CommonAddress
        {
            get
            {
                var off = 2 + _p.SizeOfCOT;
                int ca = _data[off];
                if (_p.SizeOfCA > 1)
            {
                ca += _data[off + 1] * 0x100;
            }

            return ca;
            }
        }

        /// <summary>信息对象区域（零拷贝切片，从第一个信息对象起始处到结尾）。</summary>
        public ReadOnlySpan<byte> InformationObjects
        {
            get
            {
                var off = HeaderLength;
                return off <= _data.Length ? _data.Slice(off) : default;
            }
        }

        /// <summary>整个 ASDU 的原始字节。</summary>
        public ReadOnlySpan<byte> Raw => _data;

        /// <summary>
        /// 应用层参数(决定 IOA/COT/CA 宽度),供直解方法使用。
        /// </summary>
        public ApplicationLayerParameters Parameters => _p;

        /// <summary>
        /// 零拷贝按索引解码信息对象:直接在接收缓冲切片上寻址并委托
        /// <see cref="AsduTypeHandlerRegistry"/> 中的类型处理器,无需先拷贝为 ASDU 对象。
        /// </summary>
        /// <param name="index">元素索引(从 0 开始)。</param>
        /// <param name="registry">类型处理器注册表;null 使用默认注册表。</param>
        /// <returns>解码出的信息对象。</returns>
        /// <exception cref="ASDUParsingException">类型未注册/越界/长度不足时抛出。</exception>
        public InformationObject GetElement(int index, AsduTypeHandlerRegistry registry = null)
        {
            var io = AsduDecoder.DecodeElement(
                InformationObjects, TypeId, IsSequence, NumberOfElements,
                index, _p, registry);

            if (io == null)
        {
            throw new ASDUParsingException("Unknown ASDU type id:" + TypeId);
        }

        return io;
        }

        /// <summary>
        /// 类型安全版 <see cref="GetElement(int, AsduTypeHandlerRegistry)"/>:
        /// 解码后断言实际类型为 <typeparamref name="T"/>。
        /// </summary>
        public T GetElement<T>(int index, AsduTypeHandlerRegistry registry = null) where T : InformationObject
            => AsduDecoder.DecodeElement<T>(
                InformationObjects, TypeId, IsSequence, NumberOfElements,
                index, _p, registry);
    }
