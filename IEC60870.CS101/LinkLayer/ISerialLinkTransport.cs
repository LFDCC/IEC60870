/*
 *  ISerialLinkTransport.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
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
