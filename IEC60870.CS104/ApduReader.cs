/*
 *  ApduReader.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 */

using System;


namespace IEC60870.CS104
{
    /// <summary>
    /// 零拷贝 APDU 遍历器（<c>ref struct</c>）。
    /// 在一段接收缓冲区上顺序切出所有完整的 APDU，全程不分配堆内存。
    /// </summary>
    /// <remarks>
    /// 典型用法（TouchSocket 接收回调里，buffer 为 ByteBlock 的 Span）：
    /// <code>
    /// var reader = new ApduReader(buffer);
    /// while (reader.TryReadNext())
    /// {
    ///     switch (reader.Kind) { ... }   // 处理 reader.Payload / SendSeq / RecvSeq / UFunction
    /// }
    /// if (reader.HasError) { /* 关闭连接 */ }
    /// int consumed = reader.Consumed;    // 剩余未处理: buffer.Slice(consumed) 需保留待下次拼接
    /// </code>
    /// </remarks>
    public ref struct ApduReader
    {
        private readonly ReadOnlySpan<byte> _buffer;
        private int _consumed;

        /// <summary>当前 APDU 的帧类型。</summary>
        public ApduKind Kind;

        /// <summary>当前 I 帧的发送序列号 N(S)；非 I 帧为 -1。</summary>
        public int SendSeq;

        /// <summary>当前 I/S 帧的接收序列号 N(R)；U 帧为 -1。</summary>
        public int RecvSeq;

        /// <summary>当前 U 帧的功能码（控制域1）；非 U 帧为 0。</summary>
        public byte UFunction;

        /// <summary>当前 APDU 的 ASDU 负载（零拷贝切片）；U/S 帧为空。</summary>
        public ReadOnlySpan<byte> Payload;

        /// <summary>解析中是否遇到格式错误（非法起始字节/长度）。</summary>
        public bool HasError { get; private set; }

        public ApduReader(ReadOnlySpan<byte> buffer)
        {
            _buffer = buffer;
            _consumed = 0;
            Kind = ApduKind.Information;
            SendSeq = -1;
            RecvSeq = -1;
            UFunction = 0;
            Payload = default;
            HasError = false;
        }

        /// <summary>已成功消费的字节数（可安全丢弃的前缀长度）。</summary>
        public readonly int Consumed => _consumed;

        /// <summary>
        /// 尝试解析缓冲区中的下一个完整 APDU。
        /// </summary>
        /// <returns>
        /// <c>true</c>：成功解析出一个 APDU，字段已就绪；
        /// <c>false</c>：数据不足或遇到错误（错误时 <see cref="HasError"/> 为 true）。
        /// </returns>
        public bool TryReadNext()
        {
            if (HasError)
                return false;

            ReadOnlySpan<byte> remaining = _buffer.Slice(_consumed);

            int total = ApduCodec.TryParseApdu(remaining,
                out Kind, out SendSeq, out RecvSeq,
                out int payloadOffset, out int payloadLength, out UFunction);

            if (total <= 0)
            {
                if (total < 0)
                    HasError = true;   // 格式错误
                return false;          // total == 0: 数据不足
            }

            Payload = payloadLength > 0
                ? remaining.Slice(payloadOffset, payloadLength)
                : default;

            _consumed += total;
            return true;
        }
    }
}
