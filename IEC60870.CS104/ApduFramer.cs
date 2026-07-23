/*
 *  ApduFramer.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  IEC60870.Core.NET is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  IEC60870.Core.NET is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with IEC60870.Core.NET.  If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using SysPool = System.Buffers.ArrayPool<byte>;


namespace IEC60870.CS104
{
    /// <summary>
    /// APDU 粘包重组器。累积 TCP 分片、按 IEC104 帧切分完整 APDU 并驱动 <see cref="ApduConnection"/>。
    /// 缓冲区从 <see cref="System.Buffers.ArrayPool{T}"/> 租借；单线程访问（TouchSocket 每连接接收回调串行）。
    /// </summary>
    internal sealed class ApduFramer : IDisposable
    {
        private byte[] _buffer;
        private int _length;

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
