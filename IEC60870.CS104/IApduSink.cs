/*
 *  IApduSink.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
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
        /// <summary>收到 STARTDT_CON（客户端已收到激活确认）。</summary>
        StartDtConReceived,
        /// <summary>收到 STOPDT_CON（客户端已收到停止确认）。</summary>
        StopDtConReceived,
        /// <summary>收到 STARTDT_ACT（服务端侧：对端请求激活）。</summary>
        StartDtActReceived,
        /// <summary>收到 STOPDT_ACT（服务端侧：对端请求停止）。</summary>
        StopDtActReceived,
        /// <summary>数据传输已激活。</summary>
        Activated,
        /// <summary>数据传输已停止。</summary>
        Deactivated,
        /// <summary>协议/IO 层错误导致的连接异常。</summary>
        ConnectionError,
        /// <summary>
        /// 连接已断开（非主动）。涵盖：远端关闭（FIN/RST）、链路超时（T1/T3）、协议帧错误等。
        /// 主动 <see cref="Iec104Client.DisconnectAsync"/> 不会触发本事件。
        /// </summary>
        ConnectionClosed
    }
}
