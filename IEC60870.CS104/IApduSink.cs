/*
 *  IApduSink.cs
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
using System.Threading;
using System.Threading.Tasks;


namespace IEC60870.CS104
{
    /// <summary>
    /// APDU 字节发送汇（传输层抽象）。<see cref="ApduConnection"/> 通过它把编码好的
    /// APDU 字节推给底层（TouchSocket 的 TcpClient / TcpSessionClient 等），
    /// 与具体 socket 实现解耦。
    /// </summary>
    public interface IApduSink
    {
        /// <summary>
        /// 异步发送一个完整 APDU。<paramref name="apdu"/> 的内存在方法返回（await 完成）后可被复用/归还。
        /// 实现方应保证在返回前已将数据复制到自身缓冲，或已完成写出。
        /// </summary>
        ValueTask SendAsync(ReadOnlyMemory<byte> apdu, CancellationToken cancellationToken);

        /// <summary>底层连接当前是否可用。</summary>
        bool IsConnected { get; }
    }

    /// <summary>零拷贝 ASDU 视图回调（在接收缓冲区仍有效期间同步调用）。</summary>
    public delegate void AsduViewHandler(in IEC60870.Core.AsduView asdu);

    /// <summary>连接层状态/事件。</summary>
    public enum ApduConnectionEvent
    {
        StartDtConReceived,
        StopDtConReceived,
        StartDtActReceived,
        StopDtActReceived,
        Activated,
        Deactivated,
        ConnectionError
    }
}
