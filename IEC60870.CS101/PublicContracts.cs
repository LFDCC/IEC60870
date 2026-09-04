/*
 *  Copyright 2026 LFDCC
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 */

/*
 *  PublicContracts.cs
 *
 *  CS101 公共委托、接口与异常契约聚合（原 DebugLogger.cs / ClientBase.cs / ServerBase.cs 中的
 *  公共定义统一迁移至此）。
 */

using System;
using IEC60870.Core;

namespace IEC60870.CS101;

    /// <summary>
    /// 调试日志回调（仅消息文本）。链路层内部使用；站点侧请使用 <c>ILog</c>。
    /// </summary>
    public delegate void DebugLogger(string message);

    /// <summary>
    /// 原始报文回调。
    /// 可用来访问原始报文（FT1.2 帧，含起始符/控制域/校验）。
    /// 返回 true 表示该报文应由协议栈继续处理；返回 false 则忽略该报文。
    /// </summary>
    /// <param name="parameter">注册回调时传入的用户参数。</param>
    /// <param name="message">原始报文缓冲区。</param>
    /// <param name="messageSize">有效报文字节数。</param>
    /// <returns>true 继续解析；false 丢弃。</returns>
    public delegate bool RawMessageHandler(object parameter, byte[] message, int messageSize);

    /// <summary>收到 ASDU 的回调（返回 true 表示该 ASDU 已被完全消费，链路层无需再发送确认后续）。</summary>
    /// <param name="parameter">注册回调时传入的用户参数。</param>
    /// <param name="slaveAddress">发送该 ASDU 的从站地址。</param>
    /// <param name="asdu">解析得到的 ASDU。</param>
    /// <returns>true 表示已消费。</returns>
    public delegate bool ASDUReceivedHandler(object parameter, int slaveAddress, ASDU asdu);

    /// <summary>
    /// 提供给 ServerBase 回调使用的连接抽象：回调内可借其向主站回发数据。
    /// </summary>
    public interface IClientConnection
    {
        /// <summary>向主站发送一个 ASDU。</summary>
        void SendASDU(ASDU asdu);

        /// <summary>发送激活确认（ACT_CON，COT 置为 ACTIVATION_CON）。</summary>
        /// <param name="asdu">待确认的 ASDU（其 COT 会被改写）。</param>
        /// <param name="negative">是否否定确认（N 位置位）。</param>
        void SendACT_CON(ASDU asdu, bool negative);

        /// <summary>发送激活终止（ACT_TERM，COT 置为 ACTIVATION_TERMINATION）。</summary>
        void SendACT_TERM(ASDU asdu);

        /// <summary>获取本连接的应用层参数（COT/CA/IOA 宽度等）。</summary>
        ApplicationLayerParameters GetApplicationLayerParameters();
    }

    /// <summary>
    /// 总召唤（C_IC_NA_1 - 100）处理器。
    /// </summary>
    /// <param name="parameter">注册回调时传入的用户参数。</param>
    /// <param name="connection">来源连接，可回发数据。</param>
    /// <param name="asdu">收到的召唤 ASDU。</param>
    /// <param name="qoi">召唤限定词（QOI）。</param>
    /// <returns>true 表示已处理。</returns>
    public delegate bool InterrogationHandler(object parameter, IClientConnection connection, ASDU asdu, byte qoi);

    /// <summary>
    /// 计数量总召唤（C_CI_NA_1 - 101）处理器。
    /// </summary>
    /// <param name="parameter">注册回调时传入的用户参数。</param>
    /// <param name="connection">来源连接，可回发数据。</param>
    /// <param name="asdu">收到的召唤 ASDU。</param>
    /// <param name="qoi">计数召唤限定词（QCC）。</param>
    /// <returns>true 表示已处理。</returns>
    public delegate bool CounterInterrogationHandler(object parameter, IClientConnection connection, ASDU asdu, byte qoi);

    /// <summary>
    /// 读命令（C_RD_NA_1 - 102）处理器。
    /// </summary>
    /// <param name="parameter">注册回调时传入的用户参数。</param>
    /// <param name="connection">来源连接，可回发数据。</param>
    /// <param name="asdu">收到的读命令 ASDU。</param>
    /// <param name="ioa">被读取的信息对象地址。</param>
    /// <returns>true 表示已处理。</returns>
    public delegate bool ReadHandler(object parameter, IClientConnection connection, ASDU asdu, int ioa);

    /// <summary>
    /// 时钟同步命令（C_CS_NA_1 - 103）处理器。
    /// </summary>
    /// <param name="parameter">注册回调时传入的用户参数。</param>
    /// <param name="connection">来源连接，可回发数据。</param>
    /// <param name="asdu">收到的时钟同步 ASDU。</param>
    /// <param name="newTime">新的时间值（CP56Time2a）。</param>
    /// <returns>true 表示已处理。</returns>
    public delegate bool ClockSynchronizationHandler(object parameter, IClientConnection connection, ASDU asdu, CP56Time2a newTime);

    /// <summary>
    /// 复位进程命令（C_RP_NA_1 - 105）处理器。
    /// </summary>
    /// <param name="parameter">注册回调时传入的用户参数。</param>
    /// <param name="connection">来源连接，可回发数据。</param>
    /// <param name="asdu">收到的复位进程 ASDU。</param>
    /// <param name="qrp">复位进程限定词（QRP）。</param>
    /// <returns>true 表示已处理。</returns>
    public delegate bool ResetProcessHandler(object parameter, IClientConnection connection, ASDU asdu, byte qrp);

    /// <summary>
    /// 延时获得命令（C_CD_NA:1 - 106）处理器。
    /// </summary>
    /// <param name="parameter">注册回调时传入的用户参数。</param>
    /// <param name="connection">来源连接，可回发数据。</param>
    /// <param name="asdu">收到的延时获得 ASDU。</param>
    /// <param name="delayTime">延时时间（CP16Time2a）。</param>
    /// <returns>true 表示已处理。</returns>
    public delegate bool DelayAcquisitionHandler(object parameter, IClientConnection connection, ASDU asdu, CP16Time2a delayTime);

    /// <summary>
    /// 未被其它专用回调处理的 ASDU（默认处理器）。
    /// </summary>
    /// <param name="parameter">注册回调时传入的用户参数。</param>
    /// <param name="connection">来源连接，可回发数据。</param>
    /// <param name="asdu">收到的 ASDU。</param>
    /// <returns>true 表示已处理。</returns>
    public delegate bool ASDUHandler(object parameter, IClientConnection connection, ASDU asdu);

    [Serializable]
    public class ASDUQueueException : Exception
    {
        public ASDUQueueException()
        {
        }

        public ASDUQueueException(string message)
            : base(message)
        {
        }

        public ASDUQueueException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
