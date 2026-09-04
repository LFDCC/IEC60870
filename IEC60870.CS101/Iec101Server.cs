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
    /// IEC 60870-5-101 异步从站。全异步：无工作线程阻塞，收发循环由 <see cref="Task"/> 驱动，
    /// 底层可接串口或 TouchSocket TCP 隧道。实现 <see cref="IServerApplicationLayer"/>、<see cref="IClientConnection"/>，
    /// 公共契约见 <see cref="IIec101Slave"/>。
    /// </summary>
    /// <remarks>日志：默认静默，设置 <see cref="Logger"/> 可启用（<see cref="LoggerGroup"/> + <see cref="LoggerContainerExtension.AddConsoleLogger(LoggerGroup, LogLevel)"/>）。</remarks>
    public class Iec101Server : IIec101Slave, IServerApplicationLayer, IClientConnection, IDisposable
    {
        private CancellationTokenSource _cts = null;

        /// <summary>
        /// 日志记录器（TouchSocket <see cref="ILog"/>）。默认 null（静默）；
        /// 链路层与文件服务的调试日志经由此输出。
        /// </summary>
        /// <example>
        /// <code>
        /// var logger = new LoggerGroup();
        /// logger.AddConsoleLogger(LogLevel.Debug);
        /// server.Logger = logger;
        /// </code>
        /// </example>
        public ILog Logger { get; set; } = null;

        private void DebugLog(string msg) => Logger?.Debug("CS101 SLAVE: " + msg);

        #region 应用层回调（原 ServerBase 成员）

        private InterrogationHandler _interrogationHandler = null;
        private object _interrogationHandlerParameter = null;

        private CounterInterrogationHandler _counterInterrogationHandler = null;
        private object _counterInterrogationHandlerParameter = null;

        private ReadHandler _readHandler = null;
        private object _readHandlerParameter = null;

        private ClockSynchronizationHandler _clockSynchronizationHandler = null;
        private object _clockSynchronizationHandlerParameter = null;

        private ResetProcessHandler _resetProcessHandler = null;
        private object _resetProcessHandlerParameter = null;

        private DelayAcquisitionHandler _delayAcquisitionHandler = null;
        private object _delayAcquisitionHandlerParameter = null;

        private ASDUHandler _asduHandler = null;
        private object _asduHandlerParameter = null;

        /// <summary>收到 ASDU 的事件（多播，与 <see cref="SetASDUHandler"/> 并存；消费语义仍由 SetASDUHandler 的返回值驱动）。</summary>
        public event ASDUHandler AsduReceived;

        private void RaiseAsduReceived(object parameter, IClientConnection connection, ASDU asdu)
            => AsduReceived?.Invoke(parameter, connection, asdu);

        private FileReadyHandler _fileReadyHandler = null;
        private object _fileReadyHandlerParameter = null;

        /// <summary>Sets a callback for interrogation requests.</summary>
        public void SetInterrogationHandler(InterrogationHandler handler, object parameter)
        {
            _interrogationHandler = handler;
            _interrogationHandlerParameter = parameter;
        }

        /// <summary>Sets a callback for counter interrogation requests.</summary>
        public void SetCounterInterrogationHandler(CounterInterrogationHandler handler, object parameter)
        {
            _counterInterrogationHandler = handler;
            _counterInterrogationHandlerParameter = parameter;
        }

        /// <summary>Sets a callback for read requests.</summary>
        public void SetReadHandler(ReadHandler handler, object parameter)
        {
            _readHandler = handler;
            _readHandlerParameter = parameter;
        }

        /// <summary>Sets a callback for the clock synchronization request.</summary>
        public void SetClockSynchronizationHandler(ClockSynchronizationHandler handler, object parameter)
        {
            _clockSynchronizationHandler = handler;
            _clockSynchronizationHandlerParameter = parameter;
        }

        public void SetResetProcessHandler(ResetProcessHandler handler, object parameter)
        {
            _resetProcessHandler = handler;
            _resetProcessHandlerParameter = parameter;
        }

        public void SetDelayAcquisitionHandler(DelayAcquisitionHandler handler, object parameter)
        {
            _delayAcquisitionHandler = handler;
            _delayAcquisitionHandlerParameter = parameter;
        }

        /// <summary>
        /// Sets a callback to handle ASDUs (commands, requests) form clients. This callback can be used when
        /// 没有其他回调处理该主站报文。
        /// </summary>
        public void SetASDUHandler(ASDUHandler handler, object parameter)
        {
            _asduHandler = handler;
            _asduHandlerParameter = parameter;
        }

        /// <summary>Sets a callback handler that is called when a file ready message is received from a master.</summary>
        public void SetFileReadyHandler(FileReadyHandler handler, object parameter)
        {
            _fileReadyHandler = handler;
            _fileReadyHandlerParameter = parameter;
        }

        private FilesAvailable _filesAvailable = new FilesAvailable();

        /// <summary>Gets the available files that are registered with the file server.</summary>
        public FilesAvailable GetAvailableFiles()
        {
            return _filesAvailable;
        }

        #endregion

        void IClientConnection.SendASDU(ASDU asdu)
        {
            SendASDU(asdu);
        }

        void IClientConnection.SendACT_CON(ASDU asdu, bool negative)
        {
            asdu.Cot = CauseOfTransmission.ACTIVATION_CON;
            asdu.IsNegative = negative;

            SendASDU(asdu);
        }

        void IClientConnection.SendACT_TERM(ASDU asdu)
        {
            asdu.Cot = CauseOfTransmission.ACTIVATION_TERMINATION;
            asdu.IsNegative = false;

            SendASDU(asdu);
        }

        ApplicationLayerParameters IClientConnection.GetApplicationLayerParameters()
        {
            return _parameters;
        }

        bool IServerApplicationLayer.IsClass1DataAvailable()
        {
            return IsUserDataClass1Available();
        }

        BufferFrame IServerApplicationLayer.GetClass1Data()
        {
            return DequeueUserDataClass1();
        }

        BufferFrame IServerApplicationLayer.GetCLass2Data()
        {
            var asdu = DequeueUserDataClass2();

            if (asdu == null)
        {
            asdu = DequeueUserDataClass1();
        }

        return asdu;
        }

        bool IServerApplicationLayer.HandleReceivedData(byte[] msg, bool isBroadcast, int userDataStart, int userDataLength)
        {
            return HandleApplicationLayer(0, msg, userDataStart, userDataLength);
        }

        void IServerApplicationLayer.ResetCUReceived(bool onlyFcb)
        {
            lock (_userDataClass1Queue)
            {
                _userDataClass1Queue.Clear();
            }
            lock (_userDataClass2Queue)
            {
                _userDataClass2Queue.Clear();
            }
        }

        private LinkLayerEngine _linkLayer = null;

        private byte[] _buffer = new byte[300];
        private SerialPort _port = null;
        private ISerialLinkTransport _transport;
        private LinkLayerParameters _linkLayerParameters;
        private LinkLayerMode _linkLayerMode = LinkLayerMode.UNBALANCED;

        private int _listenPort = 2404;

        PrimaryLinkLayerBalanced primaryLinkLayerBalanced = null;

        private int _linkLayerAddress = 0;
        private int _linkLayerAddressOtherStation;
        /* 平衡模式下对端链路层地址 */

        private Queue<BufferFrame> _userDataClass1Queue = new Queue<BufferFrame>();
        private int _userDataClass1QueueMaxSize = 100;

        private Queue<BufferFrame> _userDataClass2Queue = new Queue<BufferFrame>();
        private int _userDataClass2QueueMaxSize = 100;

        private FileServer _fileServer;

        private bool _initialized;

        private ApplicationLayerParameters _parameters = new ApplicationLayerParameters();

        public ApplicationLayerParameters Parameters
        {
            get { return _parameters; }
            set { _parameters = value; }
        }

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

        public LinkLayerMode LinkLayerMode
        {
            get { return _linkLayerMode; }
            set
        {
            if (_initialized == false)
            {
                _linkLayerMode = value;
            }
        }
    }

        public void Stop()
        {
            _cts?.Cancel();
        }

        /// <summary>
        /// 释放全部资源：取消并释放运行循环 CTS、关闭链路传输（TCP 监听/连接/字节队列），
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

    internal bool IsUserDataClass1Available()
        {
            lock (_userDataClass1Queue)
            {
                if (_userDataClass1Queue.Count > 0)
            {
                return true;
            }
            else
            {
                return false;
            }
        }
        }

        public void SetUserDataQueueSizes(int class1QueueSize, int class2QueueSize)
        {
            _userDataClass1QueueMaxSize = class1QueueSize;
            _userDataClass2QueueMaxSize = class2QueueSize;
        }

        public bool IsUserDataClass1QueueFull()
        {
            return (_userDataClass1Queue.Count == _userDataClass1QueueMaxSize);
        }

        public void EnqueueUserDataClass1(ASDU asdu)
        {
            lock (_userDataClass1Queue)
            {
                BufferFrame frame = new BufferFrame(new byte[256], 0);

                asdu.Encode(frame, _parameters);

                _userDataClass1Queue.Enqueue(frame);

                while (_userDataClass1Queue.Count > _userDataClass1QueueMaxSize)
            {
                _userDataClass1Queue.Dequeue();
            }
        }
        }

        internal BufferFrame DequeueUserDataClass1()
        {
            lock (_userDataClass1Queue)
            {
                if (_userDataClass1Queue.Count > 0)
            {
                return _userDataClass1Queue.Dequeue();
            }
            else
            {
                return null;
            }
        }
        }

        internal bool IsUserDataClass2Available()
        {
            lock (_userDataClass2Queue)
            {
                if (_userDataClass2Queue.Count > 0)
            {
                return true;
            }
            else
            {
                return false;
            }
        }
        }

        public bool IsUserDataClass2QueueFull()
        {
            return (_userDataClass2Queue.Count == _userDataClass2QueueMaxSize);
        }

        public void EnqueueUserDataClass2(ASDU asdu)
        {
            lock (_userDataClass2Queue)
            {
                BufferFrame frame = new BufferFrame(new byte[256], 0);

                asdu.Encode(frame, _parameters);

                _userDataClass2Queue.Enqueue(frame);

                while (_userDataClass2Queue.Count > _userDataClass2QueueMaxSize)
            {
                _userDataClass2Queue.Dequeue();
            }
        }
        }

        internal BufferFrame DequeueUserDataClass2()
        {
            lock (_userDataClass2Queue)
            {
                if (_userDataClass2Queue.Count > 0)
            {
                return _userDataClass2Queue.Dequeue();
            }
            else
            {
                return null;
            }
        }
        }

        public int LinkLayerAddress
        {
            get { return _linkLayerAddress; }
            set { _linkLayerAddress = value; }
        }

        public int LinkLayerAddressOtherStation
        {
            get { return _linkLayerAddressOtherStation; }
            set
            {
                _linkLayerAddressOtherStation = value;
                if (primaryLinkLayerBalanced != null)
            {
                primaryLinkLayerBalanced.LinkLayerAddressOtherStation = value;
            }
        }
        }

        /// <summary>
        /// 协议标识（约定同 TouchSocket 组件体系）：串口构造为 <see cref="Iec60870Utility.Iec101"/>，
        /// TCP 隧道构造为 <see cref="Iec60870Utility.Iec101OverTcp"/>。
        /// </summary>
        public Protocol Protocol { get; protected set; }

        public Iec101Server(SerialPort port, LinkLayerParameters parameters = null)
        {
            Protocol = new Protocol(Iec60870Utility.Iec101);

            _port = port;
            _linkLayerParameters = parameters;
            if (_linkLayerParameters == null)
        {
            _linkLayerParameters = new LinkLayerParameters();
        }

        _transport = new SerialTransceiverFT12(port, _linkLayerParameters, DebugLog);
            _initialized = false;
            _fileServer = new FileServer(this, GetAvailableFiles(), DebugLog);
        }

        public Iec101Server(Stream serialStream, LinkLayerParameters parameters = null)
        {
            Protocol = new Protocol(Iec60870Utility.Iec101);

            _linkLayerParameters = parameters;
            if (_linkLayerParameters == null)
        {
            _linkLayerParameters = new LinkLayerParameters();
        }

        _transport = new SerialTransceiverFT12(serialStream, _linkLayerParameters, DebugLog);
            _initialized = false;
            _fileServer = new FileServer(this, GetAvailableFiles(), DebugLog);
        }

        /// <summary>
        /// 通过 TouchSocket TCP 隧道构造从站（监听指定端口）。
        /// </summary>
        public Iec101Server(int listenPort, LinkLayerParameters parameters = null)
        {
            Protocol = new Protocol(Iec60870Utility.Iec101OverTcp);

            _listenPort = listenPort;
            _linkLayerParameters = parameters;
            if (_linkLayerParameters == null)
        {
            _linkLayerParameters = new LinkLayerParameters();
        }

        _transport = new TcpServerLinkTransport(_linkLayerParameters, DebugLog);
            _initialized = false;
            _fileServer = new FileServer(this, GetAvailableFiles(), DebugLog);
        }

        internal void SendASDU(ASDU asdu)
        {
            EnqueueUserDataClass1(asdu);
        }

        private bool HandleApplicationLayer(int address, byte[] msg, int userDataStart, int userDataLength)
        {
            ASDU asdu;

            try
            {
                // 解析 msg（链路层接收缓冲）而非 this._buffer（发送缓冲），两者已分离
                asdu = new ASDU(_parameters, msg, userDataStart, userDataStart + userDataLength);
                asdu.TypeHandlers = TypeHandlers;
            }
            catch (ASDUParsingException e)
            {
                DebugLog("ASDU parsing failed: " + e.Message);
                return false;
            }

            var messageHandled = false;

            switch (asdu.TypeId)
            {
                case TypeID.C_IC_NA_1: /* 100 - interrogation command */

                    DebugLog("Rcvd interrogation command C_IC_NA_1");

                    if ((asdu.Cot == CauseOfTransmission.ACTIVATION) || (asdu.Cot == CauseOfTransmission.DEACTIVATION))
                    {
                        if (_interrogationHandler != null)
                        {
                            InterrogationCommand irc = (InterrogationCommand)asdu.GetElement(0);

                            if (irc.ObjectAddress != 0)
                            {
                                DebugLog("C_IC_NA_1: IOA != 0 (system command 期望 IOA=0)，拒收");
                                asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                                asdu.IsNegative = true;
                                SendASDU(asdu);
                                messageHandled = true;
                                break;
                            }

                            if (_interrogationHandler(_interrogationHandlerParameter, this, asdu, irc.QOI))
                        {
                            messageHandled = true;
                        }
                    }
                    }
                    else
                    {
                        asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                        asdu.IsNegative = true;
                        SendASDU(asdu);
                    }

                    break;

                case TypeID.C_CI_NA_1: /* 101 - counter interrogation command */

                    DebugLog("Rcvd counter interrogation command C_CI_NA_1");

                    if ((asdu.Cot == CauseOfTransmission.ACTIVATION) || (asdu.Cot == CauseOfTransmission.DEACTIVATION))
                    {
                        if (_counterInterrogationHandler != null)
                        {
                            CounterInterrogationCommand cic = (CounterInterrogationCommand)asdu.GetElement(0);

                            if (cic.ObjectAddress != 0)
                            {
                                DebugLog("C_CI_NA_1: IOA != 0 (system command 期望 IOA=0)，拒收");
                                asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                                asdu.IsNegative = true;
                                SendASDU(asdu);
                                messageHandled = true;
                                break;
                            }

                            if (_counterInterrogationHandler(_counterInterrogationHandlerParameter, this, asdu, cic.QCC))
                        {
                            messageHandled = true;
                        }
                    }
                    }
                    else
                    {
                        asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                        asdu.IsNegative = true;
                        SendASDU(asdu);
                    }

                    break;

                case TypeID.C_RD_NA_1: /* 102 - read command */

                    DebugLog("Rcvd read command C_RD_NA_1");

                    if (asdu.Cot == CauseOfTransmission.REQUEST)
                    {
                        DebugLog("Read request for object: " + asdu.Ca);

                        if (_readHandler != null)
                        {
                            ReadCommand rc = (ReadCommand)asdu.GetElement(0);

                            if (_readHandler(_readHandlerParameter, this, asdu, rc.ObjectAddress))
                        {
                            messageHandled = true;
                        }
                    }
                    }
                    else
                    {
                        asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                        asdu.IsNegative = true;
                        SendASDU(asdu);
                    }

                    break;

                case TypeID.C_CS_NA_1: /* 103 - Clock synchronization command */

                    DebugLog("Rcvd clock sync command C_CS_NA_1");

                    if (asdu.Cot == CauseOfTransmission.ACTIVATION)
                    {
                        if (_clockSynchronizationHandler != null)
                        {
                            ClockSynchronizationCommand csc = (ClockSynchronizationCommand)asdu.GetElement(0);

                            if (csc.ObjectAddress != 0)
                            {
                                DebugLog("C_CS_NA_1: IOA != 0 (system command 期望 IOA=0)，拒收");
                                asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                                asdu.IsNegative = true;
                                SendASDU(asdu);
                                messageHandled = true;
                                break;
                            }

                            if (_clockSynchronizationHandler(_clockSynchronizationHandlerParameter,
                                this, asdu, csc.NewTime))
                        {
                            messageHandled = true;
                        }
                    }
                    }
                    else
                    {
                        asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                        asdu.IsNegative = true;
                        SendASDU(asdu);
                    }

                    break;

                case TypeID.C_TS_NA_1: /* 104 - test command */

                    DebugLog("Rcvd test command C_TS_NA_1");

                    if (asdu.Cot != CauseOfTransmission.ACTIVATION)
                    {
                        asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                        asdu.IsNegative = true;
                    }
                    else
                {
                    asdu.Cot = CauseOfTransmission.ACTIVATION_CON;
                }

                SendASDU(asdu);

                    messageHandled = true;

                    break;

                case TypeID.C_RP_NA_1: /* 105 - Reset process command */

                    DebugLog("Rcvd reset process command C_RP_NA_1");

                    if (asdu.Cot == CauseOfTransmission.ACTIVATION)
                    {
                        if (_resetProcessHandler != null)
                        {
                            ResetProcessCommand rpc = (ResetProcessCommand)asdu.GetElement(0);

                            if (rpc.ObjectAddress != 0)
                            {
                                DebugLog("C_RP_NA_1: IOA != 0 (system command 期望 IOA=0)，拒收");
                                asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                                asdu.IsNegative = true;
                                SendASDU(asdu);
                                messageHandled = true;
                                break;
                            }

                            if (_resetProcessHandler(_resetProcessHandlerParameter,
                                this, asdu, rpc.QRP))
                        {
                            messageHandled = true;
                        }
                    }
                    }
                    else
                    {
                        asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                        asdu.IsNegative = true;
                        SendASDU(asdu);
                    }

                    break;

                case TypeID.C_CD_NA_1: /* 106 - Delay acquisition command */

                    DebugLog("Rcvd delay acquisition command C_CD_NA_1");

                    if ((asdu.Cot == CauseOfTransmission.ACTIVATION) || (asdu.Cot == CauseOfTransmission.SPONTANEOUS))
                    {
                        if (_delayAcquisitionHandler != null)
                        {
                            DelayAcquisitionCommand dac = (DelayAcquisitionCommand)asdu.GetElement(0);

                            if (dac.ObjectAddress != 0)
                            {
                                DebugLog("C_CD_NA_1: IOA != 0 (system command 期望 IOA=0)，拒收");
                                asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                                asdu.IsNegative = true;
                                SendASDU(asdu);
                                messageHandled = true;
                                break;
                            }

                            if (_delayAcquisitionHandler(_delayAcquisitionHandlerParameter,
                                this, asdu, dac.Delay))
                        {
                            messageHandled = true;
                        }
                    }
                    }
                    else
                    {
                        asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                        asdu.IsNegative = true;
                        SendASDU(asdu);
                    }

                    break;
            }

            if (messageHandled == false)
        {
            messageHandled = _fileServer.HandleFileAsdu(asdu);
        }

        if ((messageHandled == false) && (_asduHandler != null))
        {
            if (_asduHandler(_asduHandlerParameter, this, asdu))
            {
                messageHandled = true;
            }
        }

        RaiseAsduReceived(_asduHandlerParameter, this, asdu);

            if (messageHandled == false)
            {
                asdu.Cot = CauseOfTransmission.UNKNOWN_TYPE_ID;
                asdu.IsNegative = true;
                SendASDU(asdu);
            }

            return true;
        }

        private BufferFrame GetUserData()
        {
            if (IsUserDataClass1Available())
        {
            return DequeueUserDataClass1();
        }
        else if (IsUserDataClass2Available())
        {
            return DequeueUserDataClass2();
        }
        else
        {
            return null;
        }
    }

        public void SendLinkLayerTestFunction()
        {
            _linkLayer.SendTestFunction();
        }

        /// <summary>
        /// 运行一次消息接收与状态机。可不使用后台循环而手动驱动。
        /// </summary>
        public async Task RunAsync(CancellationToken ct = default)
        {
            if (_initialized == false)
            {
                _linkLayer = new LinkLayerEngine(_buffer, _linkLayerParameters, _transport, DebugLog);
                _linkLayer.LinkLayerMode = _linkLayerMode;

                // 桥接原始报文事件：linkLayer 首次运行时创建，此后任意时刻订阅均能收到（lambda 动态读取订阅者）
                _linkLayer.RawFrameReceived += f => RawFrameReceived?.Invoke(f);
                _linkLayer.RawFrameSent += f => RawFrameSent?.Invoke(f);

                if (_linkLayerMode == LinkLayerMode.BALANCED)
                {
                    PrimaryLinkLayerBalanced primaryLinkLayerBalanced = new PrimaryLinkLayerBalanced(_linkLayer, GetUserData, DebugLog);
                    primaryLinkLayerBalanced.LinkLayerAddressOtherStation = _linkLayerAddressOtherStation;

                    _linkLayer.SetPrimaryLinkLayer(primaryLinkLayerBalanced);

                    _linkLayer.SetSecondaryLinkLayer(new SecondaryLinkLayerBalanced(_linkLayer, _linkLayerAddressOtherStation, HandleApplicationLayer, DebugLog));
                }
                else
                {
                    _linkLayer.SetSecondaryLinkLayer(new SecondaryLinkLayerUnbalanced(_linkLayer, _linkLayerAddress, this, DebugLog));
                }

                _initialized = true;
            }

            if (_fileServer != null)
        {
            _fileServer.HandleFileTransmission();
        }

        await _linkLayer.RunAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 启动后台异步收发循环。
        /// </summary>
        /// <param name="ct">取消令牌。</param>
        /// <param name="configureConfig">TCP 传输时可选的 TouchSocket 配置回调，在库默认配置（监听地址）之后执行，
        /// 可追加插件、日志等配置。串口传输时忽略。</param>
        public async Task StartAsync(CancellationToken ct = default, Action<TouchSocketConfig> configureConfig = null)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            if (_port != null)
            {
                if (_port.IsOpen == false)
            {
                _port.Open();
            }

            _port.DiscardInBuffer();
            }

            if (_transport is TcpServerLinkTransport srv)
        {
            await srv.StartAsync(_listenPort, _cts.Token, configureConfig).ConfigureAwait(false);
        }

        await RunLoopAsync(_cts.Token).ConfigureAwait(false);
        }

        private async Task RunLoopAsync(CancellationToken ct)
        {
            try
            {
                while (ct.IsCancellationRequested == false)
                {
                    await RunAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                DebugLog("Loop error: " + ex.Message);
            }
        }

        /// <summary>Gets or sets the file service timeout in ms.</summary>
        public int FileTimeout
        {
            get
            {
                if (_fileServer != null)
            {
                return (int)_fileServer.Timeout;
            }
            else
            {
                return 0;
            }
        }

            set
            {
                if (_fileServer != null)
            {
                _fileServer.Timeout = value;
            }
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
        /// 上行原始报文（主站→本从站）。参数为完整 FT1.2 帧（含起始符/控制域/校验），
        /// 生命周期安全（已拷贝为 byte[]），无订阅者零开销。兼容"先订阅后启动"。
        /// </summary>
        public event Action<byte[]> RawFrameReceived;

        /// <summary>
        /// 下行原始报文（本从站→主站）。参数为完整 FT1.2 帧，覆盖固定/变长/单字符 ACK。
        /// 生命周期安全（已拷贝为 byte[]），无订阅者零开销。
        /// </summary>
        public event Action<byte[]> RawFrameSent;
    }
