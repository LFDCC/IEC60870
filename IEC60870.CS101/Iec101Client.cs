//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using TouchSocket.Core;
using TouchSocket.Sockets;
using IEC60870.Core;



namespace IEC60870.CS101;

    /// <summary>
    /// IEC 60870-5-101 异步主站。全异步：无工作线程阻塞，收发循环由
    /// <see cref="Task"/> 驱动，底层可接串口（<see cref="SerialPort"/>）或 TouchSocket TCP 隧道。
    /// 应用层 <c>Send*</c> 维持非阻塞入队语义（与原版一致），由链路层状态机负责确认与重发。
    /// 公共契约见 <see cref="IIec101Master"/>。
    /// </summary>
    /// <remarks>日志：默认静默，设置 <see cref="Logger"/> 可启用（<see cref="LoggerGroup"/> + <see cref="LoggerContainerExtension.AddConsoleLogger(LoggerGroup, LogLevel)"/>）。</remarks>
    public class Iec101Client : IIec101Master, IClientLinkLayerCallbacks, IDisposable
    {
        private CancellationTokenSource _cts = null;

        internal LinkLayerEngine _linkLayer = null;

        internal FileClient _fileClient = null;

        private SerialPort _port = null;
        private ISerialLinkTransport _transport;
        private bool _fatalError = false;

        /// <summary>
        /// 日志记录器（TouchSocket <see cref="ILog"/>）。默认 null（静默）；
        /// 链路层与文件服务的调试日志经由此输出。
        /// </summary>
        /// <example>
        /// <code>
        /// var logger = new LoggerGroup();
        /// logger.AddConsoleLogger(LogLevel.Debug);
        /// client.Logger = logger;
        /// </code>
        /// </example>
        public ILog Logger { get; set; } = null;

        private void DebugLog(string msg) => Logger?.Debug("CS101 MASTER: " + msg);

        /// <summary>
        /// ASDU 类型处理器注册表(与 IEC60870.CS104 的 Iec104Client.TypeHandlers 对齐)。
        /// 默认共享全局 <see cref="AsduTypeHandlerRegistry.Default"/>;
        /// 私有类型场景可整体替换或直接在其上 Register 自定义 <see cref="IAsduTypeHandler"/>,
        /// 接收路径解析出的 ASDU 会自动戳记本注册表。
        /// </summary>
        public AsduTypeHandlerRegistry TypeHandlers { get; set; } = AsduTypeHandlerRegistry.Default;

        public bool DIR
        {
            get { return _linkLayer.DIR; }
            set { _linkLayer.DIR = value; }
        }

        /// <summary>
        /// 运行一次协议状态机（用于不使用后台循环的场景）。
        /// </summary>
        public async Task RunAsync(CancellationToken ct = default)
        {
            if (_fatalError == false)
            {
                await _linkLayer.RunAsync(ct).ConfigureAwait(false);

                if (_fileClient != null)
            {
                _fileClient.HandleFileService();
            }
        }
        }

        private void FatalErrorHandler(object sender, EventArgs eventArgs)
        {
            _fatalError = true;
        }

        public void AddPortDeniedHandler(EventHandler eventHandler)
        {
            _linkLayer.AddPortDeniedHandler(eventHandler);
        }

        /// <summary>
        /// 启动后台异步收发循环。
        /// </summary>
        /// <param name="ct">取消令牌。</param>
        /// <param name="configureConfig">TCP 传输时可选的 TouchSocket 配置回调，在库默认配置（远程地址）之后执行，
        /// 可追加插件、日志等配置。串口传输时忽略。</param>
        public Task StartAsync(CancellationToken ct = default, Action<TouchSocketConfig> configureConfig = null)
        {
            // 重复调用时先释放上一次创建的 CTS，避免泄漏（代码评审 #16）。无可取消外部 token 时不分配链接源。
            _cts?.Dispose();
            _cts = ct.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : new CancellationTokenSource();

            if (_port != null)
            {
                if (_port.IsOpen == false)
            {
                _port.Open();
            }

            _port.DiscardInBuffer();
            }

            _linkLayer.AddPortDeniedHandler(FatalErrorHandler);

            if (_transport is TcpClientLinkTransport tcp)
        {
            return Task.Run(async () =>
                {
                    await tcp.ConnectAsync(_cts.Token, configureConfig).ConfigureAwait(false);
                    await RunLoopAsync(_cts.Token).ConfigureAwait(false);
                }, _cts.Token);
        }

        return RunLoopAsync(_cts.Token);
        }

        /// <summary>
        /// 停止后台异步收发循环（同步取消，不阻塞）。
        /// </summary>
        public void Stop()
        {
            _cts?.Cancel();
        }

        /// <summary>
        /// 释放全部资源：取消并释放运行循环 CTS、关闭链路传输（TCP 连接/字节队列），
        /// 并 Close 构造时传入的串口（仅 Close 不 Dispose，串口对象所有权仍归调用方，
        /// Close 后可重新 Open 复用）。
        /// </summary>
        public void Dispose()
        {
            Stop();
            _cts.SafeDispose();
            _cts = null;
            _transport.SafeDispose();
            _transport = null;
            try
        {
            if (_port is { IsOpen: true })
            {
                _port.Close(); }
        }
        catch { /* port already gone */ }
    }

    private async Task RunLoopAsync(CancellationToken ct)
        {
            try
            {
                while (ct.IsCancellationRequested == false)
                {
                    await _linkLayer.RunAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _fatalError = true;
                DebugLog("Loop error: " + ex.Message);
            }
        }

        public int OwnAddress
        {
            get { return _linkLayer.OwnAddress; }
            set { _linkLayer.OwnAddress = value; }
        }

        public LinkLayerState GetLinkLayerState()
        {
            if (_linkLayer.LinkLayerMode == LinkLayerMode.BALANCED)
        {
            return _primaryLinkLayer.GetLinkLayerState();
        }
        else
        {
            return _linkLayerUnbalanced.GetStateOfSlave(_slaveAddress);
        }
    }

        public void SetReceivedRawMessageHandler(RawMessageHandler handler, object parameter)
        {
            _linkLayer.SetReceivedRawMessageHandler(handler, parameter);
        }

        public void SetSentRawMessageHandler(RawMessageHandler handler, object parameter)
        {
            _linkLayer.SetSentRawMessageHandler(handler, parameter);
        }

        /// <summary>
        /// 上行原始报文（从站→本主站）。参数为完整 FT1.2 帧（含起始符/控制域/校验），
        /// 生命周期安全（已拷贝为 byte[]），无订阅者零开销。兼容"先订阅后启动"。
        /// </summary>
        public event Action<byte[]> RawFrameReceived;

        /// <summary>
        /// 下行原始报文（本主站→从站）。参数为完整 FT1.2 帧，覆盖固定/变长/单字符 ACK。
        /// 生命周期安全（已拷贝为 byte[]），无订阅者零开销。
        /// </summary>
        public event Action<byte[]> RawFrameSent;

        private PrimaryLinkLayerUnbalanced _linkLayerUnbalanced = null;
        private PrimaryLinkLayerBalanced _primaryLinkLayer = null;

        private SecondaryLinkLayer _secondaryLinkLayer = null;

        private int _slaveAddress = 0;

        private byte[] _buffer = new byte[300];

        private LinkLayerParameters _linkLayerParameters;
        private ApplicationLayerParameters _appLayerParameters;

        private ASDUReceivedHandler _asduReceivedHandler = null;
        private object _asduReceivedHandlerParameter = null;

        /// <summary>收到 ASDU 的事件（多播，与 <see cref="SetASDUReceivedHandler"/> 并存；消费语义仍由 SetASDUReceivedHandler 的返回值驱动）。</summary>
        public event ASDUReceivedHandler AsduReceived;

        /// <summary>
        /// 协议标识（约定同 TouchSocket 组件体系）：串口构造为 <see cref="Iec60870Utility.Iec101"/>，
        /// TCP 隧道构造为 <see cref="Iec60870Utility.Iec101OverTcp"/>。
        /// </summary>
        public Protocol Protocol { get; protected set; }

        private Queue<BufferFrame> _userDataQueue;

        public Iec101Client(SerialPort port, LinkLayerMode mode, LinkLayerParameters llParams = null, ApplicationLayerParameters alParams = null)
        {
            Protocol = new Protocol(Iec60870Utility.Iec101);

            if (llParams == null)
        {
            _linkLayerParameters = new LinkLayerParameters();
        }
        else
        {
            _linkLayerParameters = llParams;
        }

        if (alParams == null)
        {
            _appLayerParameters = new ApplicationLayerParameters();
        }
        else
        {
            _appLayerParameters = alParams;
        }

        _transport = new SerialTransceiverFT12(port, _linkLayerParameters, DebugLog);

            InitializeLinkLayer(mode);

            _port = port;
            _fileClient = null;
        }

        public Iec101Client(Stream serialStream, LinkLayerMode mode, LinkLayerParameters llParams = null, ApplicationLayerParameters alParams = null)
        {
            Protocol = new Protocol(Iec60870Utility.Iec101);

            if (llParams == null)
        {
            _linkLayerParameters = new LinkLayerParameters();
        }
        else
        {
            _linkLayerParameters = llParams;
        }

        if (alParams == null)
        {
            _appLayerParameters = new ApplicationLayerParameters();
        }
        else
        {
            _appLayerParameters = alParams;
        }

        _transport = new SerialTransceiverFT12(serialStream, _linkLayerParameters, DebugLog);

            InitializeLinkLayer(mode);

            _fileClient = null;
        }

        /// <summary>
        /// 通过 TouchSocket TCP 隧道构造主站。
        /// </summary>
        public Iec101Client(string hostname, int tcpPort, LinkLayerMode mode, LinkLayerParameters llParams = null, ApplicationLayerParameters alParams = null)
        {
            Protocol = new Protocol(Iec60870Utility.Iec101OverTcp);

            if (llParams == null)
        {
            _linkLayerParameters = new LinkLayerParameters();
        }
        else
        {
            _linkLayerParameters = llParams;
        }

        if (alParams == null)
        {
            _appLayerParameters = new ApplicationLayerParameters();
        }
        else
        {
            _appLayerParameters = alParams;
        }

        _transport = new TcpClientLinkTransport(hostname, tcpPort, _linkLayerParameters, DebugLog);

            InitializeLinkLayer(mode);

            _fileClient = null;
        }

        private void InitializeLinkLayer(LinkLayerMode mode)
        {
            _linkLayer = new LinkLayerEngine(_buffer, _linkLayerParameters, _transport, DebugLog);
            _linkLayer.LinkLayerMode = mode;

            // 桥接原始报文事件：linkLayer 在构造函数即创建，此后任意时刻订阅均能收到（lambda 动态读取订阅者）
            _linkLayer.RawFrameReceived += f => RawFrameReceived?.Invoke(f);
            _linkLayer.RawFrameSent += f => RawFrameSent?.Invoke(f);

            if (mode == LinkLayerMode.BALANCED)
            {
                _linkLayer.DIR = true;

                _primaryLinkLayer = new PrimaryLinkLayerBalanced(_linkLayer, GetUserData, DebugLog);

                _linkLayer.SetPrimaryLinkLayer(_primaryLinkLayer);
                _secondaryLinkLayer = new SecondaryLinkLayerBalanced(_linkLayer, 0, HandleApplicationLayer, DebugLog);
                _linkLayer.SetSecondaryLinkLayer(_secondaryLinkLayer);

                _userDataQueue = new Queue<BufferFrame>();
            }
            else
            {
                _linkLayerUnbalanced = new PrimaryLinkLayerUnbalanced(_linkLayer, this, DebugLog);
                _linkLayer.SetPrimaryLinkLayer(_linkLayerUnbalanced);
            }
        }

        public void SetTimeouts(int messageTimeout, int characterTimeout)
        {
            _transport.SetTimeouts(messageTimeout, characterTimeout);
        }

        public void SetASDUReceivedHandler(ASDUReceivedHandler handler, object parameter)
        {
            _asduReceivedHandler = handler;
            _asduReceivedHandlerParameter = parameter;
        }

        public void AddSlave(int slaveAddress)
        {
            if (_linkLayerUnbalanced != null)
        {
            _linkLayerUnbalanced.AddSlaveConnection(slaveAddress);
        }
    }

        public LinkLayerState GetLinkLayerState(int slaveAddress)
        {
            if (_linkLayerUnbalanced != null)
        {
            return _linkLayerUnbalanced.GetStateOfSlave(slaveAddress);
        }
        else
        {
            return _primaryLinkLayer.GetLinkLayerState();
        }
    }

        public void SetLinkLayerStateChangedHandler(LinkLayerStateChanged handler, object parameter)
        {
            if (_linkLayerUnbalanced != null)
        {
            _linkLayerUnbalanced.SetLinkLayerStateChanged(handler, parameter);
        }
        else
        {
            _primaryLinkLayer.SetLinkLayerStateChanged(handler, parameter);
        }
    }

        public int SlaveAddress
        {
            set
            {
                UseSlaveAddress(value);

                if (_secondaryLinkLayer != null)
            {
                _secondaryLinkLayer.Address = _slaveAddress;
            }
        }

            get
            {
                if (_primaryLinkLayer == null)
            {
                return _slaveAddress;
            }
            else
            {
                return _primaryLinkLayer.LinkLayerAddressOtherStation;
            }
        }
        }

        public void UseSlaveAddress(int slaveAddress)
        {
            if (_primaryLinkLayer != null)
        {
            _primaryLinkLayer.LinkLayerAddressOtherStation = slaveAddress;
        }

        _slaveAddress = slaveAddress;
        }

        void IClientLinkLayerCallbacks.AccessDemand(int slaveAddress)
        {
            DebugLog("Access demand slave " + slaveAddress);
            _linkLayerUnbalanced.RequestClass1Data(slaveAddress);
        }

        void IClientLinkLayerCallbacks.UserData(int slaveAddress, byte[] message, int start, int length)
        {
            DebugLog("User data slave " + slaveAddress);

            ASDU asdu;

            try
            {
                asdu = new ASDU(_appLayerParameters, message, start, start + length);
                asdu.TypeHandlers = TypeHandlers;
            }
            catch (ASDUParsingException e)
            {
                DebugLog("ASDU parsing failed: " + e.Message);
                return;
            }

            var messageHandled = false;

            if (_fileClient != null)
        {
            messageHandled = _fileClient.HandleFileAsdu(asdu);
        }

        if (messageHandled == false)
            {
                if (_asduReceivedHandler != null)
            {
                _asduReceivedHandler(_asduReceivedHandlerParameter, slaveAddress, asdu);
            }

            AsduReceived?.Invoke(_asduReceivedHandlerParameter, slaveAddress, asdu);
            }
        }

        void IClientLinkLayerCallbacks.Timeout(int slaveAddress)
        {
            DebugLog("Timeout accessing slave " + slaveAddress);
        }

        public void PollSingleSlave(int address)
        {
            try
            {
                if (_linkLayerUnbalanced != null)
            {
                _linkLayerUnbalanced.RequestClass2Data(address);
            }
        }
            catch (LinkLayerBusyException)
            {
                DebugLog("Link layer busy");
            }
        }

        public void RequestClass1Data(int address)
        {
            try
            {
                if (_linkLayerUnbalanced != null)
            {
                _linkLayerUnbalanced.RequestClass1Data(address);
            }
        }
            catch (LinkLayerBusyException)
            {
                DebugLog("Link layer busy");
            }
        }

        private void EnqueueUserData(ASDU asdu)
        {
            if (_linkLayerUnbalanced != null)
            {
                /* 用户线程编码，使用独立缓冲区，避免与后台接收循环共享 buffer 产生数据竞争
                   （平衡分支本就使用 new byte[256]）。SendConfirmed 仅保存帧引用，稍后由
                   链路层线程发送，故此处缓冲区不会被并发访问。 */
                BufferFrame frame = new BufferFrame(new byte[256], 0);

                asdu.Encode(frame, _appLayerParameters);

                _linkLayerUnbalanced.SendConfirmed(_slaveAddress, frame);
            }
            else
            {
                lock (_userDataQueue)
                {
                    BufferFrame frame = new BufferFrame(new byte[256], 0);

                    asdu.Encode(frame, _appLayerParameters);

                    _userDataQueue.Enqueue(frame);
                }
            }
        }

        private BufferFrame DequeueUserData()
        {
            lock (_userDataQueue)
            {
                if (_userDataQueue.Count > 0)
            {
                return _userDataQueue.Dequeue();
            }
            else
            {
                return null;
            }
        }
        }

        private bool IsUserDataAvailable()
        {
            lock (_userDataQueue)
            {
                if (_userDataQueue.Count > 0)
            {
                return true;
            }
            else
            {
                return false;
            }
        }
        }

        private BufferFrame GetUserData()
        {
            if (IsUserDataAvailable())
        {
            return DequeueUserData();
        }

        return null;
        }

        private bool HandleApplicationLayer(int address, byte[] msg, int userDataStart, int userDataLength)
        {
            ASDU asdu;

            try
            {
                // 解析 msg（链路层接收缓冲）而非 this._buffer（发送缓冲），两者已分离
                asdu = new ASDU(_appLayerParameters, msg, userDataStart, userDataStart + userDataLength);
                asdu.TypeHandlers = TypeHandlers;
            }
            catch (ASDUParsingException e)
            {
                DebugLog("ASDU parsing failed: " + e.Message);
                return false;
            }

            var messageHandled = false;

            if (_fileClient != null)
        {
            messageHandled = _fileClient.HandleFileAsdu(asdu);
        }

        if (messageHandled == false)
            {
                if (_asduReceivedHandler != null)
            {
                messageHandled = _asduReceivedHandler(_asduReceivedHandlerParameter, address, asdu);
            }

            AsduReceived?.Invoke(_asduReceivedHandlerParameter, address, asdu);
            }

            return messageHandled;
        }

        public void SendLinkLayerTestFunction()
        {
            _linkLayer.SendTestFunction();
        }

        public void SendInterrogationCommand(CauseOfTransmission cot, int ca, byte qoi)
        {
            EnqueueUserData(CommandBuilder.Interrogation(_appLayerParameters, cot, ca, qoi));
        }

        public void SendCounterInterrogationCommand(CauseOfTransmission cot, int ca, byte qcc)
        {
            EnqueueUserData(CommandBuilder.CounterInterrogation(_appLayerParameters, cot, ca, qcc));
        }

        public void SendReadCommand(int ca, int ioa)
        {
            EnqueueUserData(CommandBuilder.Read(_appLayerParameters, ca, ioa));
        }

        public void SendClockSyncCommand(int ca, CP56Time2a time)
        {
            EnqueueUserData(CommandBuilder.ClockSync(_appLayerParameters, ca, time));
        }

        public void SendTestCommand(int ca)
        {
            EnqueueUserData(CommandBuilder.Test(_appLayerParameters, ca));
        }

        public void SendTestCommandWithCP56Time2a(int ca, ushort tsc, CP56Time2a time)
        {
            EnqueueUserData(CommandBuilder.TestWithCP56Time2a(_appLayerParameters, ca, tsc, time));
        }

        public void SendResetProcessCommand(CauseOfTransmission cot, int ca, byte qrp)
        {
            EnqueueUserData(CommandBuilder.ResetProcess(_appLayerParameters, cot, ca, qrp));
        }

        public void SendDelayAcquisitionCommand(CauseOfTransmission cot, int ca, CP16Time2a delay)
        {
            EnqueueUserData(CommandBuilder.DelayAcquisition(_appLayerParameters, cot, ca, delay));
        }

        public void SendControlCommand(CauseOfTransmission cot, int ca, InformationObject sc)
        {
            EnqueueUserData(CommandBuilder.Control(_appLayerParameters, cot, ca, sc));
        }

        public void SendASDU(ASDU asdu)
        {
            EnqueueUserData(asdu);
        }

        public ApplicationLayerParameters GetApplicationLayerParameters()
        {
            return _appLayerParameters;
        }

        public void GetFile(int ca, int ioa, NameOfFile nof, IFileReceiver receiver)
        {
            if (_fileClient == null)
        {
            _fileClient = new FileClient(this, DebugLog);
        }

        _fileClient.RequestFile(ca, ioa, nof, receiver);
        }

        public void SendFile(int ca, int ioa, NameOfFile nof, IFileProvider fileProvider)
        {
            if (_fileClient == null)
        {
            _fileClient = new FileClient(this, DebugLog);
        }

        _fileClient.SendFile(ca, ioa, nof, fileProvider);
        }
    }
