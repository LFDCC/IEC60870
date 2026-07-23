/*
 *  ISerialLinkTransport.cs
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
 *  This program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with IEC60870.Core.NET.  If not, see <http://www.gnu.org/licenses/>.
 *
 *  See COPYING file for the complete license text.
 */

using System;
using System.Threading;
using System.Threading.Tasks;


namespace IEC60870.CS101.LinkLayer
{
    /// <summary>
    /// 异步串行链路传输抽象。替代原同步 <see cref="SerialTransceiverFT12"/> 与基于原始 Socket 的
    /// TCP 虚拟串口。所有 I/O 均为 async/await，无工作线程阻塞。
    /// </summary>
    internal interface ISerialLinkTransport : IDisposable
    {
        /// <summary>
        /// 读取一个完整的 FT1.2 帧到 <paramref name="buffer"/>。
        /// </summary>
        /// <returns>帧字节数（&gt;0）；超时或连接关闭时返回 0。</returns>
        ValueTask<int> ReadFrameAsync(Memory<byte> buffer, CancellationToken ct);

        /// <summary>
        /// 发送原始字节（一个完整 FT1.2 帧）。
        /// </summary>
        ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

        /// <summary>
        /// 端口被拒绝（如串口访问被占用）时触发。
        /// </summary>
        event EventHandler PortDenied;

        /// <summary>
        /// 设置接收超时（毫秒）：等待帧起始字符 / 帧内后续字符。
        /// </summary>
        void SetTimeouts(int messageTimeout, int characterTimeout);
    }
}
