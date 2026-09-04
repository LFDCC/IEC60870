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
        /* CS103 专用 */
        REQUEST_FOR_ACCESS_DEMAND = 8,
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
            if (_sentRawMessageHandler != null)
        {
            _sentRawMessageHandler(_sentRawMessageHandlerParameter, _singleCharAck, 1);
        }

        RawFrameSent?.Invoke((byte[])_singleCharAck.Clone()); // 单字符 ACK 原始报文

            SendMessage(_singleCharAck, 1);
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
            var bufPos = 0;

            _buffer[bufPos++] = 0x10; /* START */

            var c = fc;

            if (prm)
        {
            c += 0x40;
        }

        if (dir)
        {
            c += 0x80;
        }

        if (acd)
        {
            c += 0x20;
        }

        if (dfc)
        {
            c += 0x10;
        }

        _buffer[bufPos++] = c;

            if (linkLayerParameters.AddressLength > 0)
            {
                _buffer[bufPos++] = (byte)(address % 0x100);

                if (linkLayerParameters.AddressLength > 1)
            {
                _buffer[bufPos++] = (byte)((address / 0x100) % 0x100);
            }
        }

            byte checksum = 0;

            for (var i = 1; i < bufPos; i++)
        {
            checksum += _buffer[i];
        }

        _buffer[bufPos++] = checksum;

            _buffer[bufPos++] = 0x16; /* END */

            if (_sentRawMessageHandler != null)
        {
            _sentRawMessageHandler(_sentRawMessageHandlerParameter, _buffer, bufPos);
        }

        // 固定帧(0x10) 原始报文（有订阅者才拷贝）
        if (RawFrameSent != null)
            {
                var copy = new byte[bufPos];
                Array.Copy(_buffer, copy, bufPos);
                RawFrameSent(copy);
            }

            SendMessage(_buffer, bufPos);
        }


        public void SendVariableLengthFramePrimary(FunctionCodePrimary fc, int address, bool fcb, bool fcv, BufferFrame frame)
        {
            _buffer[0] = 0x68; /* START */
            _buffer[3] = 0x68; /* START */

            var c = (byte)fc;

            if (_dir)
        {
            c += 0x80;
        }

        c += 0x40; // PRM = 1;

            if (fcv)
        {
            c += 0x10;
        }

        if (fcb)
        {
            c += 0x20;
        }

        _buffer[4] = c;

            var bufPos = 5;

            if (linkLayerParameters.AddressLength > 0)
            {
                _buffer[bufPos++] = (byte)(address % 0x100);

                if (linkLayerParameters.AddressLength > 1)
            {
                _buffer[bufPos++] = (byte)((address / 0x100) % 0x100);
            }
        }

            var userData = frame.GetBuffer();
            var userDataLength = frame.GetMsgSize();

            for (var i = 0; i < userDataLength; i++)
        {
            _buffer[bufPos++] = userData[i];
        }

        var l = 1 + linkLayerParameters.AddressLength + frame.GetMsgSize();

            if (l > 255)
        {
            return;
        }

        _buffer[1] = (byte)l;
            _buffer[2] = (byte)l;

            byte checksum = 0;

            for (var i = 4; i < bufPos; i++)
        {
            checksum += _buffer[i];
        }

        _buffer[bufPos++] = checksum;

            _buffer[bufPos++] = 0x16; /* END */

            if (_sentRawMessageHandler != null)
        {
            _sentRawMessageHandler(_sentRawMessageHandlerParameter, _buffer, bufPos);
        }

        // 变长帧(0x68) 原始报文（有订阅者才拷贝）
        if (RawFrameSent != null)
            {
                var copy = new byte[bufPos];
                Array.Copy(_buffer, copy, bufPos);
                RawFrameSent(copy);
            }

            SendMessage(_buffer, bufPos);
        }

        internal void SendVariableLengthFrameSecondary(FunctionCodeSecondary fc, int address, bool acd, bool dfc, BufferFrame frame)
        {
            _buffer[0] = 0x68; /* START */
            _buffer[3] = 0x68; /* START */

            var c = (byte)((int)fc & 0x1f);

            if (_linkLayerMode == LinkLayerMode.BALANCED)
            {
                if (_dir)
            {
                c += 0x80;
            }
        }

            if (acd)
        {
            c += 0x20;
        }

        if (dfc)
        {
            c += 0x10;
        }

        _buffer[4] = c;

            var bufPos = 5;

            if (linkLayerParameters.AddressLength > 0)
            {
                _buffer[bufPos++] = (byte)(address % 0x100);

                if (linkLayerParameters.AddressLength > 1)
            {
                _buffer[bufPos++] = (byte)((address / 0x100) % 0x100);
            }
        }

            var userData = frame.GetBuffer();
            var userDataLength = frame.GetMsgSize();

            var l = 1 + linkLayerParameters.AddressLength + userDataLength;

            if (l > 255)
        {
            return;
        }

        _buffer[1] = (byte)l;
            _buffer[2] = (byte)l;

            for (var i = 0; i < userDataLength; i++)
        {
            _buffer[bufPos++] = userData[i];
        }

        byte checksum = 0;

            for (var i = 4; i < bufPos; i++)
        {
            checksum += _buffer[i];
        }

        _buffer[bufPos++] = checksum;

            _buffer[bufPos++] = 0x16; /* END */

            if (_sentRawMessageHandler != null)
        {
            _sentRawMessageHandler(_sentRawMessageHandlerParameter, _buffer, bufPos);
        }

        // 变长帧(0x68) 原始报文（有订阅者才拷贝）
        if (RawFrameSent != null)
            {
                var copy = new byte[bufPos];
                Array.Copy(_buffer, copy, bufPos);
                RawFrameSent(copy);
            }

            SendMessage(_buffer, bufPos);
        }


        private void ParseHeaderSecondaryUnbalanced(byte[] msg, int msgSize)
        {
            var userDataLength = 0;
            var userDataStart = 0;
            byte c;
            int csStart;
            int csIndex;
            var address = 0;

            if (msg[0] == 0x68)
            {

                if (msg[1] != msg[2])
                {
                    DebugLog("ERROR: L fields differ!");
                    return;
                }

                userDataLength = msg[1] - linkLayerParameters.AddressLength - 1;
                userDataStart = 5 + linkLayerParameters.AddressLength;

                csStart = 4;
                csIndex = userDataStart + userDataLength;

                // 校验报文长度是否合理
                if (msgSize != (userDataStart + userDataLength + 2 /* CS + END */))
                {
                    DebugLog("ERROR: Invalid message length");
                    return;
                }

                c = msg[4];
            }
            else if (msg[0] == 0x10)
            {
                c = msg[1];
                csStart = 1;
                csIndex = 2 + linkLayerParameters.AddressLength;

            }
            else if (msg[0] == 0xE5)
            {
                /* 来自其他从站的确认帧，直接忽略 */
                return;
            }
            else
            {
                DebugLog("ERROR: Received unexpected message type in unbalanced slave mode!");
                return;
            }

            var isBroadcast = false;

            //check address
            if (linkLayerParameters.AddressLength > 0)
            {
                address = msg[csStart + 1];

                if (linkLayerParameters.AddressLength > 1)
                {
                    address += (msg[csStart + 2] * 0x100);

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

            //check checksum
            byte checksum = 0;

            for (var i = csStart; i < csIndex; i++)
        {
            checksum += msg[i];
        }

        if (checksum != msg[csIndex])
            {
                DebugLog("ERROR: checksum invalid!");
                return;
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
            var userDataLength = 0;
            var userDataStart = 0;
            byte c = 0;
            var csStart = 0;
            var csIndex = 0;
            var address = 0; /* 平衡模式下地址是否可忽略？ */
            var prm = true;
            var fc = 0;

            var isAck = false;

            if (msg[0] == 0x68)
            {

                if (msg[1] != msg[2])
                {
                    DebugLog("ERROR: L fields differ!");
                    return;
                }

                userDataLength = msg[1] - linkLayerParameters.AddressLength - 1;
                userDataStart = 5 + linkLayerParameters.AddressLength;

                csStart = 4;
                csIndex = userDataStart + userDataLength;

                // 校验报文长度是否合理
                if (msgSize != (userDataStart + userDataLength + 2 /* CS + END */))
                {
                    DebugLog("ERROR: Invalid message length");
                    return;
                }

                c = msg[4];

                if (linkLayerParameters.AddressLength > 0)
            {
                address += msg[5];
            }

            if (linkLayerParameters.AddressLength > 1)
            {
                address += msg[6] * 0x100;
            }
        }
            else if (msg[0] == 0x10)
            {
                c = msg[1];
                csStart = 1;
                csIndex = 2 + linkLayerParameters.AddressLength;

                if (linkLayerParameters.AddressLength > 0)
            {
                address += msg[2];
            }

            if (linkLayerParameters.AddressLength > 1)
            {
                address += msg[3] * 0x100;
            }
        }
            else if (msg[0] == 0xe5)
            {
                isAck = true;
                fc = (int)FunctionCodeSecondary.ACK;
                prm = false; /* 单字符 ACK 仅由副站发送 */
                DebugLog("Received single char ACK");
            }
            else
            {
                DebugLog("ERROR: Received unexpected message type!");
                return;
            }

            if (isAck == false)
            {

                //check checksum
                byte checksum = 0;

                for (var i = csStart; i < csIndex; i++)
            {
                checksum += msg[i];
            }

            if (checksum != msg[csIndex])
                {
                    DebugLog("ERROR: checksum invalid!");
                    return;
                }

                // parse C field bits
                fc = c & 0x0f;
                prm = ((c & 0x40) == 0x40);

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
            else
            { /* 单字节 ACK */
                if (_primaryLinkLayer != null)
            {
                _primaryLinkLayer.HandleMessage(FunctionCodeSecondary.ACK, false, false, -1, null, 0, 0);
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
        }
            catch (OperationCanceledException)
            {
                // 外部取消：本轮直接结束
            }
            catch (Exception ex)
            {
                DebugLog?.Invoke("LinkLayerEngine RunAsync error: " + ex.GetType().Name + ": " + ex.Message);
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
