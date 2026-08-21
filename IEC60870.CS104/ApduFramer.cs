/*
 *  ApduFramer.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 */

using System;
using SysPool = System.Buffers.ArrayPool<byte>;


namespace IEC60870.CS104
{
    /// <summary>切出完整 APDU 时的回调（含 APCI 头，ref struct 参数需同步消费）。</summary>
    internal delegate void FrameParsedHandler(ReadOnlySpan<byte> frame);

    /// <summary>
    /// APDU 粘包重组器。累积 TCP 分片、按 IEC104 帧切分完整 APDU 并驱动 <see cref="ApduConnection"/>。
    /// 缓冲区从 <see cref="System.Buffers.ArrayPool{T}"/> 租借；单线程访问（TouchSocket 每连接接收回调串行）。
    /// </summary>
    internal sealed class ApduFramer : IDisposable
    {
        private byte[] _buffer;
        private int _length;

        /// <summary>
        /// 每当从缓冲切出一个完整 APDU 时回调（含 APCI 头，仅回调执行期间有效）。
        /// 用于向外部暴露原始报文；帧数据位于 framer 内部缓冲，回调返回后即可能被复用，须在回调内同步拷贝。
        /// </summary>
        public FrameParsedHandler OnFrameParsed;

        public ApduFramer(int initialCapacity = 1024)
        {
            _buffer = SysPool.Shared.Rent(initialCapacity < 260 ? 260 : initialCapacity);
            _length = 0;
        }

        /// <summary>追加新收到的字节。</summary>
        public void Append(ReadOnlySpan<byte> data)
        {
            EnsureCapacity(_length + data.Length);
            data.CopyTo(_buffer.AsSpan(_length));
            _length += data.Length;
        }

        /// <summary>
        /// 解析缓冲区中所有完整 APDU 并驱动状态机。
        /// </summary>
        /// <returns>false 表示协议/序列号错误，调用方应关闭连接。</returns>
        public bool Process(ApduConnection connection)
        {
            var reader = new ApduReader(_buffer.AsSpan(0, _length));

            while (reader.TryReadNext())
            {
                OnFrameParsed?.Invoke(reader.Frame); // 完整 APDU（含 APCI），同步回调须立即拷贝

                switch (reader.Kind)
                {
                    case ApduKind.Information:
                        if (!connection.OnIFrame(reader.SendSeq, reader.RecvSeq, reader.Payload))
                            return false;
                        break;
                    case ApduKind.Supervisory:
                        if (!connection.OnSFrame(reader.RecvSeq))
                            return false;
                        break;
                    case ApduKind.Unnumbered:
                        connection.OnUFrame(reader.UFunction);
                        break;
                }
            }

            if (reader.HasError)
                return false;

            // 压缩剩余未处理字节到头部
            int consumed = reader.Consumed;
            int remaining = _length - consumed;
            if (consumed > 0 && remaining > 0)
                _buffer.AsSpan(consumed, remaining).CopyTo(_buffer);
            _length = remaining;
            return true;
        }

        private void EnsureCapacity(int needed)
        {
            if (_buffer.Length >= needed)
                return;

            int newSize = _buffer.Length * 2;
            while (newSize < needed)
                newSize *= 2;

            byte[] bigger = SysPool.Shared.Rent(newSize);
            _buffer.AsSpan(0, _length).CopyTo(bigger);
            SysPool.Shared.Return(_buffer);
            _buffer = bigger;
        }

        public void Dispose()
        {
            byte[] buf = _buffer;
            _buffer = null;
            if (buf != null)
                SysPool.Shared.Return(buf);
        }
    }
}
