//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS101;

/// <summary>
/// IEC 60870-5-101 主站（客户端）契约。镜像 <see cref="Iec101Client"/> 的公共 API，
/// 提供可测试性与未来多态能力（串口 / TouchSocket TCP 隧道 / 内存回环均可实现本接口）。
/// </summary>
/// <remarks>
/// <para>
/// 设计取舍：CS101 的全部 <c>Send*</c> 均维持非阻塞入队语义（与原版一致），正常工作时同步返回
/// （<see langword="void"/>），因此<b>不</b>像 CS104 那样把命令动词抽成扩展方法，而是直接作为接口成员，
/// 调用方可以面向接口在单元测试中用假实现替换整套发送逻辑。
/// </para>
/// <para>
/// 同时保留旧式回调（<see cref="SetASDUReceivedHandler"/> 等返回值驱动消费语义）与多播事件
/// <see cref="AsduReceived"/> / <see cref="RawFrameReceived"/> / <see cref="RawFrameSent"/>，二者并存。
/// </para>
/// </remarks>
public interface IIec101Master : IDisposable
{
    /// <summary>日志记录器（TouchSocket <see cref="ILog"/>）。默认 null（静默）；链路层与文件服务的调试日志经由此输出。</summary>
    ILog Logger { get; set; }

    /// <summary>ASDU 类型处理器注册表（与 IEC60870.CS104 的 Iec104Client.TypeHandlers 对齐）。默认共享全局 <see cref="AsduTypeHandlerRegistry.Default"/>。</summary>
    AsduTypeHandlerRegistry TypeHandlers { get; set; }

    /// <summary>协议标识：串口构造为 <see cref="Iec60870Utility.Iec101"/>，TCP 隧道构造为 <see cref="Iec60870Utility.Iec101OverTcp"/>。</summary>
    Protocol Protocol { get; }

    /// <summary>本主站（主站侧）链路层地址。仅在平衡模式下参与帧编址。</summary>
    int OwnAddress { get; set; }

    /// <summary>当前选中的从站地址（仅平衡模式真正写入帧地址；非平衡模式作内部记录）。</summary>
    int SlaveAddress { get; set; }

    /// <summary>链路层 DIR 位（平衡模式 = 1）。</summary>
    bool DIR { get; set; }

    /// <summary>收到 ASDU 的多播事件（与 <see cref="SetASDUReceivedHandler"/> 并存；消费语义仍由回调返回值驱动）。</summary>
    event ASDUReceivedHandler AsduReceived;

    /// <summary>上行原始报文（从站→本主站），参数为完整 FT1.2 帧（含起始符/控制域/校验），生命周期已拷贝安全。</summary>
    event Action<byte[]> RawFrameReceived;

    /// <summary>下行原始报文（本主站→从站），覆盖固定/变长/单字符 ACK，生命周期已拷贝安全。</summary>
    event Action<byte[]> RawFrameSent;

    /// <summary>启动后台异步收发循环（同步取消，不阻塞）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <param name="configureConfig">TCP 传输时可选的 TouchSocket 配置回调，在库默认配置（远程地址）之后执行，可追加插件、日志等。串口传输时忽略。</param>
    Task StartAsync(CancellationToken ct = default, Action<TouchSocketConfig> configureConfig = null);

    /// <summary>停止后台异步收发循环（仅请求取消，不阻塞）。</summary>
    void Stop();

    /// <summary>运行一次协议状态机（用于不使用后台循环的场景）。</summary>
    /// <param name="ct">取消令牌。</param>
    Task RunAsync(CancellationToken ct = default);

    /// <summary>设置串口超时时间（毫秒）：等待帧起始字符 / 帧内后续字符。</summary>
    void SetTimeouts(int messageTimeout, int characterTimeout);

    /// <summary>设置 ASDU 接收回调（返回值 true 表示该 ASDU 已被完全消费）。</summary>
    void SetASDUReceivedHandler(ASDUReceivedHandler handler, object parameter);

    /// <summary>设置上行原始报文回调。返回 false 可阻止该帧继续进入协议栈解析。</summary>
    void SetReceivedRawMessageHandler(RawMessageHandler handler, object parameter);

    /// <summary>设置下行原始报文回调。返回 false 可阻止该帧实际发送。</summary>
    void SetSentRawMessageHandler(RawMessageHandler handler, object parameter);

    /// <summary>非平衡模式：登记一个从站地址，使其进入轮询表。</summary>
    void AddSlave(int slaveAddress);

    /// <summary>获取指定从站的链路层状态（非平衡）或整体链路层状态（平衡）。</summary>
    LinkLayerState GetLinkLayerState(int slaveAddress);

    /// <summary>获取整体链路层状态（平衡模式）。</summary>
    LinkLayerState GetLinkLayerState();

    /// <summary>设置链路层状态变化回调。</summary>
    void SetLinkLayerStateChangedHandler(LinkLayerStateChanged handler, object parameter);

    /// <summary>显式切换到指定从站地址（仅记录 / 平衡模式写入帧编址）。</summary>
    void UseSlaveAddress(int slaveAddress);

    /// <summary>非平衡模式：轮询单个从站的 Class 2（自发）数据。</summary>
    void PollSingleSlave(int address);

    /// <summary>非平衡模式：请求单个从站的 Class 1（事件/突发）数据。</summary>
    void RequestClass1Data(int address);

    /// <summary>发送链路层测试函数（固定帧 TESTFR）。</summary>
    void SendLinkLayerTestFunction();

    /// <summary>总召唤命令（C_IC_NA_1）。</summary>
    void SendInterrogationCommand(CauseOfTransmission cot, int ca, byte qoi);

    /// <summary>计数量总召唤命令（C_CI_NA_1）。</summary>
    void SendCounterInterrogationCommand(CauseOfTransmission cot, int ca, byte qcc);

    /// <summary>读命令（C_RD_NA_1）。</summary>
    void SendReadCommand(int ca, int ioa);

    /// <summary>时钟同步命令（C_CS_NA_1）。</summary>
    void SendClockSyncCommand(int ca, CP56Time2a time);

    /// <summary>测试命令（C_TS_NA_1）。</summary>
    void SendTestCommand(int ca);

    /// <summary>带时标的测试命令（C_TS_TA_1，使用 CP56Time2a）。</summary>
    void SendTestCommandWithCP56Time2a(int ca, ushort tsc, CP56Time2a time);

    /// <summary>复位进程命令（C_RP_NA_1）。</summary>
    void SendResetProcessCommand(CauseOfTransmission cot, int ca, byte qrp);

    /// <summary>延时获得命令（C_CD_NA_1）。</summary>
    void SendDelayAcquisitionCommand(CauseOfTransmission cot, int ca, CP16Time2a delay);

    /// <summary>通用控制命令发送（由 <see cref="CommandBuilder.Control"/> 构造）。</summary>
    void SendControlCommand(CauseOfTransmission cot, int ca, InformationObject sc);

    /// <summary>直接发送一个已构造的 ASDU（非阻塞入队）。</summary>
    void SendASDU(ASDU asdu);

    /// <summary>获取应用层参数（COT/CA/IOA 宽度等）。</summary>
    ApplicationLayerParameters GetApplicationLayerParameters();

    /// <summary>向从站请求下载一个文件（监控方向读取）。</summary>
    void GetFile(int ca, int ioa, NameOfFile nof, IFileReceiver receiver);

    /// <summary>向从站上传一个文件（控制方向发送）。</summary>
    void SendFile(int ca, int ioa, NameOfFile nof, IFileProvider fileProvider);
}
