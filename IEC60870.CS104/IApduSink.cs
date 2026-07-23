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
        /// <summary>
        /// 连接已断开（非主动）。涵盖：远端关闭（FIN/RST）、链路超时（T1/T3）、协议帧错误等。
        /// 主动 <see cref="Iec104Client.DisconnectAsync"/> / <see cref="Iec104Server.StopAsync"/> 不会触发本事件；
        /// 订阅方应将其视为“链路丢失，客户端需要重连”的信号。具体断开原因可通过
        /// <see cref="ApduConnection.CloseReason"/> 查询。
        /// </summary>
        ConnectionClosed
    }

    /// <summary>
    /// 底层连接断开的原因，配合 <see cref="ApduConnectionEvent.ConnectionClosed"/> 使用。
    /// 用于在重连策略中区分“远端优雅关闭 / 链路超时 / 协议错误”，便于日志与差异化处理。
    /// </summary>
    public enum ConnectionCloseReason
    {
        /// <summary>未知或默认（例如底层 socket 关闭回调未携带更具体原因）。</summary>
        Unknown,
        /// <summary>远端关闭连接（FIN/RST）或 EOF。</summary>
        RemoteClosed,
        /// <summary>链路层超时（T1/T3）被判定为断开。</summary>
        Timeout,
        /// <summary>收到无法解析的协议帧（长度/序列号等严重错误）。</summary>
        ProtocolError,
        /// <summary>底层 IO 异常（如写失败）。</summary>
        IoError
    }
}
