/*
 *  AsduView.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 */

using System;


namespace IEC60870.Core
{
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
                int off = 2 + _p.SizeOfCOT;
                int ca = _data[off];
                if (_p.SizeOfCA > 1)
                    ca += _data[off + 1] * 0x100;
                return ca;
            }
        }

        /// <summary>信息对象区域（零拷贝切片，从第一个信息对象起始处到结尾）。</summary>
        public ReadOnlySpan<byte> InformationObjects
        {
            get
            {
                int off = HeaderLength;
                return off <= _data.Length ? _data.Slice(off) : default;
            }
        }

        /// <summary>整个 ASDU 的原始字节。</summary>
        public ReadOnlySpan<byte> Raw => _data;
    }
}
