//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;


namespace IEC60870.CS101;

    /// <summary>
    /// 链路层连接状态变化时由协议栈回调
    /// </summary>
    /// <param name="address">Address of the slave (only used for unbalanced master mode)</param>
    public delegate void LinkLayerStateChanged(object parameter, int address, LinkLayerState newState);

    public enum LinkLayerState
    {
        IDLE,
        ERROR,
        BUSY,
        AVAILABLE
    }

    public enum LinkLayerMode
    {
        UNBALANCED,
        BALANCED
    }

    /* 非平衡传输功能码 */
    internal enum FunctionCodePrimary
    {
        RESET_REMOTE_LINK = 0,
        /* 复位通信单元（CU） */
        RESET_USER_PROCESS = 1,
        TEST_FUNCTION_FOR_LINK = 2,
        USER_DATA_CONFIRMED = 3,
        USER_DATA_NO_REPLY = 4,
        RESET_FCB = 7,
        REQUEST_LINK_STATUS = 9,
        REQUEST_USER_DATA_CLASS_1 = 10,
        REQUEST_USER_DATA_CLASS_2 = 11
    }

    /* 非平衡传输功能码 */
    internal enum FunctionCodeSecondary
    {
        ACK = 0,
        NACK = 1,
        RESP_USER_DATA = 8,
        RESP_NACK_NO_DATA = 9,
        STATUS_OF_LINK_OR_ACCESS_DEMAND = 11,
        LINK_SERVICE_NOT_FUNCTIONING = 14,
        LINK_SERVICE_NOT_IMPLEMENTED = 15
    }

    internal enum PrimaryLinkLayerState
    {
        IDLE,
        EXECUTE_REQUEST_STATUS_OF_LINK,
        EXECUTE_RESET_REMOTE_LINK,
        LINK_LAYERS_AVAILABLE,
        EXECUTE_SERVICE_SEND_CONFIRM,
        EXECUTE_SERVICE_REQUEST_RESPOND,
        SECONDARY_LINK_LAYER_BUSY,
        TIMEOUT
    }

    internal class LinkLayerEngine
    {
        protected Action<string> DebugLog;

        protected byte[] _buffer;
        /* byte buffer to receive and send frames */

        /// <summary>接收专用缓冲：与发送缓冲 <see cref="_buffer"/> 分离，消除共享缓冲的数据竞争隐患。</summary>
        private readonly byte[] _recvBuffer = new byte[300];

        public LinkLayerParameters linkLayerParameters;
        protected ISerialLinkTransport _transceiver;
        private LinkLayerMode _linkLayerMode = LinkLayerMode.BALANCED;

        /* 发送缓冲：SendXxx 先写入此处，RunAsync 每轮统一冲刷，避免状态机内部多次 await 写。 */
        private byte[] _sendBuffer = new byte[300];
        private int _sendLen = 0;

        private PrimaryLinkLayer _primaryLinkLayer = null;
        private SecondaryLinkLayer _secondaryLinkLayer = null;

        /// <summary>从站侧因 FCB 校验失败丢弃的用户数据帧数（排障用）。</summary>
        internal long SecondaryFcbMismatchDrops
            => (_secondaryLinkLayer as SecondaryLinkLayerBalanced)?.FcbMismatchDrops ?? 0;

        internal string SecondaryFcbDropInfo
            => (_secondaryLinkLayer as SecondaryLinkLayerBalanced)?.LastFcbDropInfo ?? "";

        private byte[] _singleCharAck = new byte[] { 0xe5 };

        private bool _dir;
        /* 仅用于平衡链路层 */

        private RawMessageHandler _receivedRawMessageHandler = null;
        private object _receivedRawMessageHandlerParameter = null;

        private RawMessageHandler _sentRawMessageHandler = null;
        private object _sentRawMessageHandlerParameter = null;

        /// <summary>
        /// 收到完整 FT1.2 帧时触发（原始报文，含起始符/控制域/校验）。参数已拷贝为 byte[]，
        /// 生命周期安全，可长期持有。仅在有订阅者时分配，无订阅者零开销。
        /// </summary>
        public event Action<byte[]> RawFrameReceived;

        /// <summary>
        /// 发送完整 FT1.2 帧时触发（原始报文，含起始符/控制域/校验）。参数已拷贝为 byte[]，
        /// 生命周期安全。覆盖固定帧(0x10)/变长帧(0x68)/单字符 ACK(0xE5)。
        /// 仅在有订阅者时分配，无订阅者零开销。
        /// </summary>
        public event Action<byte[]> RawFrameSent;

        public LinkLayerEngine(byte[] buffer, LinkLayerParameters parameters, ISerialLinkTransport transceiver, Action<string> debugLog)
        {
            _buffer = buffer;
            linkLayerParameters = parameters;
            _transceiver = transceiver;
            DebugLog = debugLog;
        }

        public void SetReceivedRawMessageHandler(RawMessageHandler handler, object parameter)
        {
            _receivedRawMessageHandler = handler;
            _receivedRawMessageHandlerParameter = parameter;
        }

        public void SetSentRawMessageHandler(RawMessageHandler handler, object parameter)
        {
            _sentRawMessageHandler = handler;
            _sentRawMessageHandlerParameter = parameter;
        }

        internal int GetBroadcastAddress()
        {
            if (linkLayerParameters.AddressLength == 1)
            {
                return 255;
            }
            else if (linkLayerParameters.AddressLength == 2)
            {
                return 65535;
            }

            return 0;
        }

        private int _ownAddress = 0;

        public int OwnAddress
        {
            get
            {
                if (_secondaryLinkLayer is SecondaryLinkLayerUnbalanced)
            {
                return _secondaryLinkLayer.Address;
            }
            else
            {
                return _ownAddress;
            }
        }
            set
            {
                if (_secondaryLinkLayer is SecondaryLinkLayerUnbalanced)
            {
                _secondaryLinkLayer.Address = value;
            }
            else
            {
                _ownAddress = value;
            }
        }
        }

        /// <summary>
        /// Gets or sets a value indicating whether this balanced <see cref="LinkLayerEngine"/> has DIR bit set
        /// </summary>
        /// <value><c>true</c> if DI; otherwise, <c>false</c>.</value>
        public bool DIR
        {
            get
            {
                return _dir;
            }
            set
            {
                _dir = value;
            }
        }

        public long TimeoutForACK
        {
            get { return linkLayerParameters.TimeoutForACK; }
        }

        public long TimeoutRepeat
        {
            get { return linkLayerParameters.TimeoutRepeat; }
        }

        public void SetPrimaryLinkLayer(PrimaryLinkLayer primaryLinkLayer)
        {
            _primaryLinkLayer = primaryLinkLayer;
        }

        /// <summary>当前主站（主动发送方）链路层；平衡模式下用于双端 FCB 同步。</summary>
        internal PrimaryLinkLayer PrimaryLinkLayer => _primaryLinkLayer;

        public void SetSecondaryLinkLayer(SecondaryLinkLayer secondaryLinkLayer)
        {
            _secondaryLinkLayer = secondaryLinkLayer;
        }

        public LinkLayerMode LinkLayerMode
        {
            get
            {
                return _linkLayerMode;
            }
            set
            {
                _linkLayerMode = value;
            }
        }

        public void SendTestFunction()
        {
            if (_primaryLinkLayer != null)
        {
            _primaryLinkLayer.SendLinkLayerTestFunction();
        }
    }

        public void SendSingleCharACK()
        {
            _buffer[0] = Ft12Frame.SingleAck;
            EmitFrame(1);
        }

        /// <summary>
        /// 统一出口：触发原始报文事件（有订阅者才拷贝）并追加到发送缓冲。
        /// 所有帧（固定/可变/单字符）都经过这里，保证事件与发送行为一致。
        /// </summary>
        private void EmitFrame(int frameLength)
        {
            if (_sentRawMessageHandler != null)
            {
                _sentRawMessageHandler(_sentRawMessageHandlerParameter, _buffer, frameLength);
            }

            // 原始报文事件（有订阅者才拷贝，无订阅者零开销）
            if (RawFrameSent != null)
            {
                var copy = new byte[frameLength];
                Array.Copy(_buffer, copy, frameLength);
                RawFrameSent(copy);
            }

            SendMessage(_buffer, frameLength);
        }

        /// <summary>
        /// 将完整 FT1.2 帧追加到发送缓冲（不立即写）。由 <see cref="FlushSendsAsync"/> 在每轮 RunAsync 末尾统一冲刷。
        /// </summary>
        private void SendMessage(byte[] msg, int msgSize)
        {
            if (_sendLen + msgSize > _sendBuffer.Length)
            {
                // 缓冲不足：扩展缓冲继续累积，避免同步阻塞冲刷线程池线程做异步 I/O
                // （原 FlushSendsAsync(...).GetAwaiter().GetResult() 在高负载下可能线程池饥饿，代码评审 #10）。
                // 每轮 RunAsync 末尾都会清空，实际增长有限。
                var need = _sendLen + msgSize;
                Array.Resize(ref _sendBuffer, Math.Max(_sendBuffer.Length * 2, need));
            }

            Array.Copy(msg, 0, _sendBuffer, _sendLen, msgSize);
            _sendLen += msgSize;
        }

        private async ValueTask FlushSendsAsync(CancellationToken ct)
        {
            if (_sendLen > 0)
            {
                await _transceiver.WriteAsync(_sendBuffer.AsMemory(0, _sendLen), ct).ConfigureAwait(false);
                _sendLen = 0;
            }
        }


        public void SendFixedFramePrimary(FunctionCodePrimary fc, int address, bool fcb, bool fcv)
        {
            SendFixedFrame((byte)fc, address, true, _dir, fcb, fcv);
        }

        public void SendFixedFrameSecondary(FunctionCodeSecondary fc, int address, bool acd, bool dfc)
        {
            SendFixedFrame((byte)fc, address, false, _dir, acd, dfc);
        }

        public void SendFixedFrame(byte fc, int address, bool prm, bool dir, bool acd, bool dfc)
        {
            var c = fc;

            if (prm)
            {
                c = (byte)(c + 0x40);
            }

            if (dir)
            {
                c = (byte)(c + 0x80);
            }

            if (acd)
            {
                c = (byte)(c + 0x20);
            }

            if (dfc)
            {
                c = (byte)(c + 0x10);
            }

            // 统一走 Ft12Frame 编码（与 103 共用同一套字节规则），
            // 地址域长度由链路参数决定（0/1/2）。
            var bufPos = Ft12Frame.EncodeFixedTo(_buffer, c, address, linkLayerParameters.AddressLength);
            EmitFrame(bufPos);
        }


        public void SendVariableLengthFramePrimary(FunctionCodePrimary fc, int address, bool fcb, bool fcv, BufferFrame frame)
        {
            var c = (byte)fc;

            if (_dir)
            {
                c = (byte)(c + 0x80);
            }

            c = (byte)(c + 0x40); // PRM = 1;

            if (fcv)
            {
                c = (byte)(c + 0x10);
            }

            if (fcb)
            {
                c = (byte)(c + 0x20);
            }

            // 统一走 Ft12Frame 编码；ASDU 超长时直接放弃本帧（与原实现一致）。
            try
            {
                var bufPos = Ft12Frame.EncodeVariableTo(
                    _buffer, c, address, linkLayerParameters.AddressLength,
                    new ReadOnlySpan<byte>(frame.GetBuffer(), 0, frame.GetMsgSize()));
                EmitFrame(bufPos);
            }
            catch (ArgumentOutOfRangeException)
            {
                // L 超过 255：原实现静默 return，保持同样行为
                DebugLog("WARN: variable frame too long, dropped");
            }
        }

        internal void SendVariableLengthFrameSecondary(FunctionCodeSecondary fc, int address, bool acd, bool dfc, BufferFrame frame)
        {
            var c = (byte)((int)fc & 0x1f);

            if (_linkLayerMode == LinkLayerMode.BALANCED)
            {
                if (_dir)
                {
                    c = (byte)(c + 0x80);
                }
            }

            if (acd)
            {
                c = (byte)(c + 0x20);
            }

            if (dfc)
            {
                c = (byte)(c + 0x10);
            }

            // 统一走 Ft12Frame 编码；ASDU 超长时放弃本帧（与原实现一致）。
            try
            {
                var bufPos = Ft12Frame.EncodeVariableTo(
                    _buffer, c, address, linkLayerParameters.AddressLength,
                    new ReadOnlySpan<byte>(frame.GetBuffer(), 0, frame.GetMsgSize()));
                EmitFrame(bufPos);
            }
            catch (ArgumentOutOfRangeException)
            {
                DebugLog("WARN: variable frame too long, dropped");
            }
        }


        private void ParseHeaderSecondaryUnbalanced(byte[] msg, int msgSize)
        {
            // 帧解析统一走 Ft12Frame.TryDecode（与 103 共用同一套校验规则：
            // 长度双写、结束符、校验和范围）。业务分支（地址判定、FC 分派）保留在本方法。
            if (!Ft12Frame.TryDecode(new ReadOnlySpan<byte>(msg, 0, msgSize),
                    linkLayerParameters.AddressLength, out var frame, out var decodeError))
            {
                if (msgSize > 0 && msg[0] == 0xE5)
                {
                    /* 来自其他从站的确认帧，直接忽略 */
                    return;
                }

                DebugLog("ERROR: " + (decodeError ?? "invalid frame"));
                return;
            }

            if (frame!.Format == Ft12FrameFormat.SingleChar)
            {
                // 来自其他从站的确认帧，直接忽略
                return;
            }

            var c = frame.Control;
            var address = frame.Address;
            var userDataStart = 0;
            var userDataLength = 0;

            if (frame.Format == Ft12FrameFormat.VariableLength)
            {
                userDataStart = 5 + linkLayerParameters.AddressLength;
                userDataLength = frame.Asdu.Length;
            }

            var isBroadcast = false;

            //check address
            if (linkLayerParameters.AddressLength > 0)
            {
                if (linkLayerParameters.AddressLength > 1)
                {
                    if (address == 65535)
                    {
                        isBroadcast = true;
                    }
                }
                else
                {
                    if (address == 255)
                    {
                        isBroadcast = true;
                    }
                }
            }

            var fc = c & 0x0f;
            FunctionCodePrimary fcp = (FunctionCodePrimary)fc;

            if (isBroadcast)
            {
                if (fcp != FunctionCodePrimary.USER_DATA_NO_REPLY)
                {
                    DebugLog("ERROR: Invalid function code for broadcast message!");
                    return;
                }

            }
            else
            {
                if (address != _secondaryLinkLayer.Address)
                {
                    DebugLog("INFO: unknown link layer address -> ignore message");
                    return;
                }
            }


            // parse C field bits
            var prm = ((c & 0x40) == 0x40);

            if (prm == false)
            {
                DebugLog("ERROR: Received secondary message in unbalanced slave mode!");
                return;
            }

            var fcb = ((c & 0x20) == 0x20);
            var fcv = ((c & 0x10) == 0x10);

            DebugLog("PRM=" + (prm == true ? "1" : "0") + " FCB=" + (fcb == true ? "1" : "0") + " FCV=" + (fcv == true ? "1" : "0")
                + " FC=" + fc + "(" + fcp.ToString() + ")");

            if (_secondaryLinkLayer != null)
        {
            _secondaryLinkLayer.HandleMessage(fcp, isBroadcast, address, fcb, fcv, msg, userDataStart, userDataLength);
        }
        else
        {
            DebugLog("No secondary link layer available!");
        }
    }


        public void HandleMessageBalancedAndPrimaryUnbalanced(byte[] msg, int msgSize)
        {
            // 帧解析统一走 Ft12Frame.TryDecode（与 103 共用同一套校验规则）。
            // 业务分支（平衡/非平衡、主/从、FC 分派）保留在本方法。
            if (!Ft12Frame.TryDecode(new ReadOnlySpan<byte>(msg, 0, msgSize),
                    linkLayerParameters.AddressLength, out var frame, out var decodeError))
            {
                DebugLog("ERROR: " + (decodeError ?? "invalid frame"));
                return;
            }

            var c = frame!.Control;
            var address = frame.Address;
            var userDataStart = 0;
            var userDataLength = 0;

            if (frame.Format == Ft12FrameFormat.VariableLength)
            {
                userDataStart = 5 + linkLayerParameters.AddressLength;
                userDataLength = frame.Asdu.Length;
            }

            var isAck = frame.Format == Ft12FrameFormat.SingleChar;

            if (isAck)
            {
                /* 单字节 ACK */
                DebugLog("Received single char ACK");
                if (_primaryLinkLayer != null)
                {
                    _primaryLinkLayer.HandleMessage(FunctionCodeSecondary.ACK, false, false, -1, null, 0, 0);
                }

                return;
            }

            // parse C field bits
            var fc = c & 0x0f;
            var prm = ((c & 0x40) == 0x40);

            if (prm)
            { /* 本端为副站链路层 */
                var fcb = ((c & 0x20) == 0x20);
                var fcv = ((c & 0x10) == 0x10);

                DebugLog("PRM=" + (prm == true ? "1" : "0") + " FCB=" + (fcb == true ? "1" : "0") + " FCV=" + (fcv == true ? "1" : "0")
                    + " FC=" + fc + "(" + ((FunctionCodePrimary)c).ToString() + ")");

                FunctionCodePrimary fcp = (FunctionCodePrimary)fc;

                if (_secondaryLinkLayer != null)
                {
                    _secondaryLinkLayer.HandleMessage(fcp, false, address, fcb, fcv, msg, userDataStart, userDataLength);
                }
                else
                {
                    DebugLog("No secondary link layer available!");
                }
            }
            else
            { /* 本端为主站链路层 */
                var dir = ((c & 0x80) == 0x80); /* DIR：平衡传输方向位 */
                var dfc = ((c & 0x10) == 0x10); /* DFC：数据流控制 */
                var acd = ((c & 0x20) == 0x20); /* ACD：1 类数据访问请求（非平衡传输） */

                DebugLog("PRM=" + (prm == true ? "1" : "0") + " DIR=" + (dir == true ? "1" : "0") + " DFC=" + (dfc == true ? "1" : "0")
                    + " FC=" + fc + "(" + ((FunctionCodeSecondary)c).ToString() + ")");

                FunctionCodeSecondary fcs = (FunctionCodeSecondary)fc;

                if (_primaryLinkLayer != null)
                {
                    if (_linkLayerMode == LinkLayerMode.BALANCED)
                    {
                        _primaryLinkLayer.HandleMessage(fcs, dir, dfc, address, msg, userDataStart, userDataLength);
                    }
                    else
                    {
                        _primaryLinkLayer.HandleMessage(fcs, acd, dfc, address, msg, userDataStart, userDataLength);
                    }
                }
                else
                {
                    DebugLog("No primary link layer available!");
                }
            }
        }

        void HandleMessageAction(byte[] msg, int msgSize)
        {
            DebugLog("RECV " + BitConverter.ToString(msg, 0, msgSize));

            // 上行原始报文（有订阅者才拷贝，无订阅者零开销）
            if (RawFrameReceived != null)
            {
                var copy = new byte[msgSize];
                Array.Copy(msg, copy, msgSize);
                RawFrameReceived(copy);
            }

            var handleMessage = true;

            if (_receivedRawMessageHandler != null)
        {
            handleMessage = _receivedRawMessageHandler(_receivedRawMessageHandlerParameter, msg, msgSize);
        }

        if (handleMessage)
            {

                if (_linkLayerMode == LinkLayerMode.BALANCED)
            {
                HandleMessageBalancedAndPrimaryUnbalanced(msg, msgSize);
            }
            else
                {
                    if (_secondaryLinkLayer != null)
                {
                    ParseHeaderSecondaryUnbalanced(msg, msgSize);
                }
                else if (_primaryLinkLayer != null)
                {
                    HandleMessageBalancedAndPrimaryUnbalanced(msg, msgSize);
                }
                else
                {
                    DebugLog("ERROR: Neither primary nor secondary link layer available!");
                }
            }
            }
            else
        {
            DebugLog("Message ignored because of raw message handler");
        }
    }

        public async ValueTask RunAsync(CancellationToken ct)
        {
            try
            {
                var n = await _transceiver.ReadFrameAsync(_recvBuffer, ct).ConfigureAwait(false);

                if (n > 0)
            {
                HandleMessageAction(_recvBuffer, n);
            }
                else
            {
                    // 底层在"无数据"时立即返回 0（串口流 EOF、已释放的 BaseStream、
                    // TCP 队列的残留许可）会让上层 RunLoop 无延迟重入 → 100% CPU 空转。
                    // 这里给一个最小退避，把"立即返回 0"的路径限制在 ≤1000 次/秒。
                    // 正常 TCP 超时本来就要等 50ms，1ms 退避对时延无可测影响。
                    try
                    {
                        await Task.Delay(1, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
            }
        }
            catch (OperationCanceledException)
            {
                // 外部取消：本轮直接结束
            }
            catch (Exception ex)
            {
                DebugLog?.Invoke("LinkLayerEngine RunAsync error: " + ex.GetType().Name + ": " + ex.Message);

                // 异常也不得让上层零延迟重入：退避一次再返回
                try
                {
                    await Task.Delay(5, ct).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            if (_linkLayerMode == LinkLayerMode.BALANCED)
            {
                _primaryLinkLayer.RunStateMachine();
                _secondaryLinkLayer.RunStateMachine();
            }
            else
            {
                if (_primaryLinkLayer != null)
            {
                _primaryLinkLayer.RunStateMachine();
            }
            else if (_secondaryLinkLayer != null)
            {
                _secondaryLinkLayer.RunStateMachine();
            }
        }

            await FlushSendsAsync(ct).ConfigureAwait(false);
        }

        public void AddPortDeniedHandler(EventHandler eventHandler)
        {
            _transceiver.PortDenied += eventHandler;
        }
    }
