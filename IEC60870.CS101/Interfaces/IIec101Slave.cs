//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS101;

/// <summary>
/// IEC 60870-5-101 从站（服务端）契约。镜像 <see cref="Iec101Server"/> 的公共 API，
/// 提供可测试性与未来多态能力。
/// </summary>
/// <remarks>
/// <para>
/// 发送侧（<see cref="EnqueueUserDataClass1"/> / <see cref="EnqueueUserDataClass2"/>）与
/// 回调注册（<c>Set*Handler</c>）均为同步接口成员，不入队语义保持与实现类一致。
/// </para>
/// <para>
/// 同时保留旧式回调（<c>Set*Handler</c>，返回值驱动消费语义）与多播事件
/// <see cref="AsduReceived"/> / <see cref="RawFrameReceived"/> / <see cref="RawFrameSent"/>，二者并存。
/// </para>
/// </remarks>
public interface IIec101Slave : IDisposable
{
    /// <summary>日志记录器（TouchSocket <see cref="ILog"/>）。默认 null（静默）；链路层与文件服务的调试日志经由此输出。</summary>
    ILog Logger { get; set; }

    /// <summary>应用层参数（COT/CA/IOA 宽度等）。</summary>
    ApplicationLayerParameters Parameters { get; set; }

    /// <summary>ASDU 类型处理器注册表（与 IEC60870.CS104 对齐）。默认共享全局 <see cref="AsduTypeHandlerRegistry.Default"/>。</summary>
    AsduTypeHandlerRegistry TypeHandlers { get; set; }

    /// <summary>协议标识：串口构造为 <see cref="Iec60870Utility.Iec101"/>，TCP 隧道构造为 <see cref="Iec60870Utility.Iec101OverTcp"/>。</summary>
    Protocol Protocol { get; }

    /// <summary>链路层 DIR 位（平衡模式）。</summary>
    bool DIR { get; set; }

    /// <summary>链路层模式（非平衡/平衡）。仅在未初始化（首次 <see cref="RunAsync"/> 前）时可修改。</summary>
    LinkLayerMode LinkLayerMode { get; set; }

    /// <summary>本从站链路层地址（非平衡模式接收过滤 / 平衡模式响应帧地址）。</summary>
    int LinkLayerAddress { get; set; }

    /// <summary>对端（主站）链路层地址（平衡模式）。</summary>
    int LinkLayerAddressOtherStation { get; set; }

    /// <summary>文件传输超时（毫秒）。</summary>
    int FileTimeout { get; set; }

    /// <summary>收到 ASDU 的多播事件（与 <see cref="SetASDUHandler"/> 并存；消费语义仍由回调返回值驱动）。</summary>
    event ASDUHandler AsduReceived;

    /// <summary>上行原始报文（主站→本从站），参数为完整 FT1.2 帧，生命周期已拷贝安全。</summary>
    event Action<byte[]> RawFrameReceived;

    /// <summary>下行原始报文（本从站→主站），覆盖固定/变长/单字符 ACK，生命周期已拷贝安全。</summary>
    event Action<byte[]> RawFrameSent;

    /// <summary>启动后台异步收发循环（TCP 隧道构造时同步等待监听就绪）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <param name="configureConfig">TCP 传输时可选的 TouchSocket 配置回调，在库默认配置（监听地址）之后执行，可追加插件、日志等。串口传输时忽略。</param>
    Task StartAsync(CancellationToken ct = default, Action<TouchSocketConfig> configureConfig = null);

    /// <summary>停止后台异步收发循环（仅请求取消，不阻塞）。</summary>
    void Stop();

    /// <summary>运行一次消息接收与状态机（不使用后台循环而手动驱动）。</summary>
    /// <param name="ct">取消令牌。</param>
    Task RunAsync(CancellationToken ct = default);

    /// <summary>设置 Class 1（事件/突发）与 Class 2（周期/自发）用户数据队列容量。</summary>
    void SetUserDataQueueSizes(int class1QueueSize, int class2QueueSize);

    /// <summary>Class 1（高优先级）用户数据队列是否已满。</summary>
    bool IsUserDataClass1QueueFull();

    /// <summary>Class 2（低优先级）用户数据队列是否已满。</summary>
    bool IsUserDataClass2QueueFull();

    /// <summary>向 Class 1（高优先级）队列入队一个 ASDU，等待主站轮询/召唤时发送。</summary>
    void EnqueueUserDataClass1(ASDU asdu);

    /// <summary>向 Class 2（低优先级）队列入队一个 ASDU，等待主站轮询/召唤时发送。</summary>
    void EnqueueUserDataClass2(ASDU asdu);

    /// <summary>发送链路层测试函数（固定帧 TESTFR）。</summary>
    void SendLinkLayerTestFunction();

    /// <summary>设置总召唤（C_IC_NA_1）回调。返回 true 表示已处理。</summary>
    void SetInterrogationHandler(InterrogationHandler handler, object parameter);

    /// <summary>设置计数量总召唤（C_CI_NA_1）回调。返回 true 表示已处理。</summary>
    void SetCounterInterrogationHandler(CounterInterrogationHandler handler, object parameter);

    /// <summary>设置读命令（C_RD_NA_1）回调。返回 true 表示已处理。</summary>
    void SetReadHandler(ReadHandler handler, object parameter);

    /// <summary>设置时钟同步命令（C_CS_NA_1）回调。返回 true 表示已处理。</summary>
    void SetClockSynchronizationHandler(ClockSynchronizationHandler handler, object parameter);

    /// <summary>设置复位进程命令（C_RP_NA_1）回调。返回 true 表示已处理。</summary>
    void SetResetProcessHandler(ResetProcessHandler handler, object parameter);

    /// <summary>设置延时获得命令（C_CD_NA_1）回调。返回 true 表示已处理。</summary>
    void SetDelayAcquisitionHandler(DelayAcquisitionHandler handler, object parameter);

    /// <summary>设置通用 ASDU 回调（未被其它专用回调处理的 ASDU 落入此处）。返回 true 表示已处理。</summary>
    void SetASDUHandler(ASDUHandler handler, object parameter);

    /// <summary>设置文件就绪（F_FR_NA_1，主站发起文件下载）回调。</summary>
    void SetFileReadyHandler(FileReadyHandler handler, object parameter);

    /// <summary>设置上行原始报文回调。返回 false 可阻止该帧继续进入协议栈解析。</summary>
    void SetReceivedRawMessageHandler(RawMessageHandler handler, object parameter);

    /// <summary>设置下行原始报文回调。返回 false 可阻止该帧实际发送。</summary>
    void SetSentRawMessageHandler(RawMessageHandler handler, object parameter);

    /// <summary>获取已注册的可供下载文件列表。</summary>
    FilesAvailable GetAvailableFiles();
}
