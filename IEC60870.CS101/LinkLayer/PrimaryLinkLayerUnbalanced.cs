//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using IEC60870.Core;


namespace IEC60870.CS101;

    internal interface IPrimaryLinkLayerUnbalanced
    {
        void ResetCU(int slaveAddress);

        /// <summary>
        /// 判断该通道（从站连接）当前能否发送新的应用层报文
        /// </summary>
        /// <returns><c>true</c> if this instance is channel available; otherwise, <c>false</c>.</returns>
        /// <param name="slaveAddress">link layer address of the slave</param>
        bool IsChannelAvailable(int slaveAddress);

        void RequestClass1Data(int slaveAddress);

        void RequestClass2Data(int slaveAddress);

        void SendConfirmed(int slaveAddress, BufferFrame message);

        void SendNoReply(int slaveAddress, BufferFrame message);
    }


    internal class PrimaryLinkLayerUnbalanced : PrimaryLinkLayer, IPrimaryLinkLayerUnbalanced
    {
        private LinkLayerEngine _linkLayer;
        private Action<string> _debugLog;

        private List<SlaveConnection> _slaveConnections;

        /// <summary>
        /// 当前活动的从站连接。
        /// </summary>
        private SlaveConnection _currentSlave = null;

        private BufferFrame _nextBroadcastMessage = null;

        private IClientLinkLayerCallbacks _callbacks = null;

        private LinkLayerStateChanged _stateChanged = null;
        private object _stateChangedParameter = null;

        // can this class implement ClientBase interface?
        private class SlaveConnection
        {

            private Action<string> _debugLog = null;

            public int _address;
            public PrimaryLinkLayerState primaryState = PrimaryLinkLayerState.IDLE;
            public long lastSendTime = 0;
            public long originalSendTime = 0;
            public bool nextFcb = true;
            public bool waitingForResponse = false;
            public LinkLayerState linkLayerState = LinkLayerState.IDLE;

            PrimaryLinkLayerUnbalanced _linkLayerUnbalanced;

            private bool _sendLinkLayerTestFunction = false;

            // 暂停发送新的应用层报文，避免数据流拥塞
            private bool _dontSendMessages = false;

            public BufferFrame nextMessage = null;
            private BufferFrame _lastSentASDU = null;

            public bool requireConfirmation = false;

            public bool resetCu = false;
            public bool requestClass2Data = false;
            public bool requestClass1Data = false;

            private LinkLayerEngine _linkLayer;

            private void SetState(LinkLayerState newState)
            {
                if (linkLayerState != newState)
                {

                    linkLayerState = newState;

                    if (_linkLayerUnbalanced._stateChanged != null)
                {
                    _linkLayerUnbalanced._stateChanged(_linkLayerUnbalanced._stateChangedParameter,
                            _address, newState);
                }
            }
            }

            public SlaveConnection(int address, LinkLayerEngine linkLayer, Action<string> debugLog, PrimaryLinkLayerUnbalanced linkLayerUnbalanced)
            {
                _address = address;
                _linkLayer = linkLayer;
                _debugLog = debugLog;
                _linkLayerUnbalanced = linkLayerUnbalanced;
            }

            public bool IsMessageWaitingToSend()
            {
                if (requestClass1Data || requestClass2Data || (nextMessage != null))
            {
                return true;
            }
            else
            {
                return false;
            }
        }

            internal void HandleMessage(FunctionCodeSecondary fcs, bool acd, bool dfc,
                               int addr, byte[] msg, int userDataStart, int userDataLength)
            {
                var newState = primaryState;

                if (dfc)
                {

                    //stop sending ASDUs; only send Status of link requests
                    _dontSendMessages = true;

                    switch (primaryState)
                    {
                        case PrimaryLinkLayerState.EXECUTE_REQUEST_STATUS_OF_LINK:
                        case PrimaryLinkLayerState.EXECUTE_RESET_REMOTE_LINK:
                            newState = PrimaryLinkLayerState.EXECUTE_REQUEST_STATUS_OF_LINK;
                            break;
                        case PrimaryLinkLayerState.EXECUTE_SERVICE_SEND_CONFIRM:
                        //TODO message must be handled and switched to BUSY state later!
                        case PrimaryLinkLayerState.SECONDARY_LINK_LAYER_BUSY:
                            newState = PrimaryLinkLayerState.SECONDARY_LINK_LAYER_BUSY;
                            break;
                    }

                    SetState(LinkLayerState.BUSY);

                    primaryState = newState;
                    return;

                }
                else
                {
                    // 恢复应用层报文的发送
                    _dontSendMessages = false;
                }

                switch (fcs)
                {

                    case FunctionCodeSecondary.ACK:

                        _debugLog("[SLAVE " + _address + "] PLL - received ACK");

                        if (primaryState == PrimaryLinkLayerState.EXECUTE_RESET_REMOTE_LINK)
                        {
                            newState = PrimaryLinkLayerState.LINK_LAYERS_AVAILABLE;

                            SetState(LinkLayerState.AVAILABLE);
                        }
                        else if (primaryState == PrimaryLinkLayerState.EXECUTE_SERVICE_SEND_CONFIRM)
                        {

                            if (_sendLinkLayerTestFunction)
                        {
                            _sendLinkLayerTestFunction = false;
                        }

                        SetState(LinkLayerState.AVAILABLE);

                            newState = PrimaryLinkLayerState.LINK_LAYERS_AVAILABLE;
                        }
                        else if (primaryState == PrimaryLinkLayerState.EXECUTE_SERVICE_REQUEST_RESPOND)
                        {

                            /* 单字符 ACK 按 RESP_NO_DATA 处理 */
                            requestClass1Data = false;
                            requestClass2Data = false;

                            SetState(LinkLayerState.AVAILABLE);

                            newState = PrimaryLinkLayerState.LINK_LAYERS_AVAILABLE;
                        }

                        waitingForResponse = false;
                        break;

                    case FunctionCodeSecondary.NACK:

                        _debugLog("[SLAVE " + _address + "] PLL - received NACK");

                        if (primaryState == PrimaryLinkLayerState.EXECUTE_SERVICE_SEND_CONFIRM)
                        {

                            SetState(LinkLayerState.BUSY);

                            newState = PrimaryLinkLayerState.SECONDARY_LINK_LAYER_BUSY;
                        }

                        waitingForResponse = false;
                        break;

                    case FunctionCodeSecondary.STATUS_OF_LINK_OR_ACCESS_DEMAND:

                        _debugLog("[SLAVE " + _address + "] PLL - received STATUS OF LINK");

                        if (primaryState == PrimaryLinkLayerState.EXECUTE_REQUEST_STATUS_OF_LINK)
                        {

                            _debugLog("[SLAVE " + _address + "] PLL - SEND RESET REMOTE LINK");

                            _linkLayer.SendFixedFramePrimary(FunctionCodePrimary.RESET_REMOTE_LINK, _address, false, false);

                            nextFcb = true;
                            lastSendTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            waitingForResponse = true;
                            newState = PrimaryLinkLayerState.EXECUTE_RESET_REMOTE_LINK;

                            SetState(LinkLayerState.BUSY);
                        }
                        else
                        { /* 非法报文 */
                            newState = PrimaryLinkLayerState.IDLE;

                            SetState(LinkLayerState.ERROR);

                            waitingForResponse = false;
                        }

                        break;

                    case FunctionCodeSecondary.RESP_USER_DATA:

                        _debugLog("[SLAVE " + _address + "] PLL - received USER DATA");

                        if (primaryState == PrimaryLinkLayerState.EXECUTE_SERVICE_REQUEST_RESPOND)
                        {
                            _linkLayerUnbalanced._callbacks.UserData(_address, msg, userDataStart, userDataLength);

                            requestClass1Data = false;
                            requestClass2Data = false;

                            newState = PrimaryLinkLayerState.LINK_LAYERS_AVAILABLE;

                            SetState(LinkLayerState.AVAILABLE);
                        }
                        else
                        { /* 非法报文 */
                            newState = PrimaryLinkLayerState.IDLE;

                            SetState(LinkLayerState.ERROR);
                        }

                        waitingForResponse = false;

                        break;

                    case FunctionCodeSecondary.RESP_NACK_NO_DATA:

                        _debugLog("[SLAVE " + _address + "] PLL - received RESP NO DATA");

                        if (primaryState == PrimaryLinkLayerState.EXECUTE_SERVICE_REQUEST_RESPOND)
                        {
                            newState = PrimaryLinkLayerState.LINK_LAYERS_AVAILABLE;

                            requestClass1Data = false;
                            requestClass2Data = false;

                            SetState(LinkLayerState.AVAILABLE);
                        }
                        else
                        { /* 非法报文 */
                            newState = PrimaryLinkLayerState.IDLE;

                            SetState(LinkLayerState.ERROR);
                        }

                        waitingForResponse = false;

                        break;

                    case FunctionCodeSecondary.LINK_SERVICE_NOT_FUNCTIONING:
                    case FunctionCodeSecondary.LINK_SERVICE_NOT_IMPLEMENTED:

                        _debugLog("[SLAVE " + _address + "] PLL - link layer service not functioning/not implemented in secondary station ");

                        if (primaryState == PrimaryLinkLayerState.EXECUTE_SERVICE_SEND_CONFIRM)
                        {
                            newState = PrimaryLinkLayerState.LINK_LAYERS_AVAILABLE;

                            SetState(LinkLayerState.AVAILABLE);
                        }

                        waitingForResponse = false;

                        break;

                    default:
                        _debugLog("[SLAVE " + _address + "] UNEXPECTED SECONDARY LINK LAYER MESSAGE");
                        break;
                }

                if (acd)
                {
                    if (_linkLayerUnbalanced._callbacks != null)
                {
                    _linkLayerUnbalanced._callbacks.AccessDemand(_address);
                }
            }

                _debugLog("[SLAVE " + _address + "] PLL RECV - old state: " + primaryState.ToString() + " new state: " + newState.ToString());

                primaryState = newState;
            }

            public void RunStateMachine()
            {
                var newState = primaryState;

                var currentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                switch (primaryState)
                {
                    case PrimaryLinkLayerState.TIMEOUT:

                        if (lastSendTime > currentTime)
                    {

                        /* 上次发送时间不合理！ */
                        lastSendTime = currentTime;
                    }

                    if (currentTime > (lastSendTime + _linkLayer.linkLayerParameters.TimeoutLinkState))
                        {
                            newState = PrimaryLinkLayerState.IDLE;
                        }

                        break;

                    case PrimaryLinkLayerState.IDLE:

                        originalSendTime = 0;
                        _sendLinkLayerTestFunction = false;

                        _debugLog("[SLAVE " + _address + "] PLL - SEND FC 09 - REQUEST LINK STATUS\n");

                        _linkLayer.SendFixedFramePrimary(FunctionCodePrimary.REQUEST_LINK_STATUS, _address, false, false);

                        lastSendTime = currentTime;
                        waitingForResponse = true;
                        newState = PrimaryLinkLayerState.EXECUTE_REQUEST_STATUS_OF_LINK;

                        break;

                    case PrimaryLinkLayerState.EXECUTE_REQUEST_STATUS_OF_LINK:

                        if (waitingForResponse)
                        {
                            if (lastSendTime > currentTime)
                        {

                            /* 上次发送时间不合理！ */
                            lastSendTime = currentTime;
                        }

                        if (currentTime > (lastSendTime + _linkLayer.TimeoutForACK))
                            {
                                waitingForResponse = false;
                                lastSendTime = currentTime;
                                newState = PrimaryLinkLayerState.TIMEOUT;
                            }

                        }
                        else
                        {

                            _debugLog("[SLAVE " + _address + "] PLL - SEND RESET REMOTE LINK");

                            _linkLayer.SendFixedFramePrimary(FunctionCodePrimary.RESET_REMOTE_LINK, _address, false, false);

                            lastSendTime = currentTime;
                            waitingForResponse = true;
                            nextFcb = true;
                            newState = PrimaryLinkLayerState.EXECUTE_RESET_REMOTE_LINK;
                        }

                        break;

                    case PrimaryLinkLayerState.EXECUTE_RESET_REMOTE_LINK:

                        if (waitingForResponse)
                        {
                            if (lastSendTime > currentTime)
                        {

                            /* 上次发送时间不合理！ */
                            lastSendTime = currentTime;
                        }

                        if (currentTime > (lastSendTime + _linkLayer.TimeoutForACK))
                            {
                                waitingForResponse = false;
                                lastSendTime = currentTime;
                                newState = PrimaryLinkLayerState.TIMEOUT;

                                SetState(LinkLayerState.ERROR);
                            }
                        }
                        else
                        {
                            newState = PrimaryLinkLayerState.LINK_LAYERS_AVAILABLE;

                            SetState(LinkLayerState.AVAILABLE);
                        }

                        break;

                    case PrimaryLinkLayerState.LINK_LAYERS_AVAILABLE:

                        if (_sendLinkLayerTestFunction)
                        {
                            _debugLog("[SLAVE " + _address + "] PLL - SEND TEST LINK");

                            _linkLayer.SendFixedFramePrimary(FunctionCodePrimary.TEST_FUNCTION_FOR_LINK, _address, nextFcb, true);

                            nextFcb = !nextFcb;
                            lastSendTime = currentTime;
                            originalSendTime = currentTime;
                            waitingForResponse = true;

                            newState = PrimaryLinkLayerState.EXECUTE_SERVICE_SEND_CONFIRM;
                        }
                        else if (requestClass1Data || requestClass2Data)
                        {

                            if (requestClass1Data)
                            {
                                _debugLog("[SLAVE " + _address + "] PLL - SEND FC 10 - REQ UD 1");

                                _linkLayer.SendFixedFramePrimary(FunctionCodePrimary.REQUEST_USER_DATA_CLASS_1, _address, nextFcb, true);

                                requestClass1Data = false;
                            }
                            else
                            {
                                _debugLog("[SLAVE " + _address + "] PLL - SEND FC 11 - REQ UD 2");

                                _linkLayer.SendFixedFramePrimary(FunctionCodePrimary.REQUEST_USER_DATA_CLASS_2, _address, nextFcb, true);

                                requestClass2Data = false;
                            }

                            nextFcb = !nextFcb;
                            lastSendTime = currentTime;
                            originalSendTime = currentTime;
                            waitingForResponse = true;
                            newState = PrimaryLinkLayerState.EXECUTE_SERVICE_REQUEST_RESPOND;
                        }
                        else
                        {

                            if (_dontSendMessages == false)
                            {

                                var asdu = nextMessage;

                                if (asdu != null)
                                {

                                    _debugLog("[SLAVE " + _address + "] PLL - SEND FC 03 - USER DATA CONFIRMED");

                                    _linkLayer.SendVariableLengthFramePrimary(FunctionCodePrimary.USER_DATA_CONFIRMED, _address, nextFcb, true, asdu);

                                    _lastSentASDU = nextMessage;
                                    nextMessage = null;

                                    nextFcb = !nextFcb;
                                    lastSendTime = currentTime;
                                    originalSendTime = currentTime;
                                    waitingForResponse = true;

                                    newState = PrimaryLinkLayerState.EXECUTE_SERVICE_SEND_CONFIRM;
                                }
                            }
                        }

                        break;

                    case PrimaryLinkLayerState.EXECUTE_SERVICE_SEND_CONFIRM:

                        if (lastSendTime > currentTime)
                    {

                        /* 上次发送时间不合理！ */
                        lastSendTime = currentTime;
                    }

                    if (currentTime > (lastSendTime + _linkLayer.TimeoutForACK))
                        {

                            if (currentTime > (originalSendTime + _linkLayer.TimeoutRepeat))
                            {
                                _debugLog("[SLAVE " + _address + "] TIMEOUT SC: ASDU not confirmed after repeated transmission");

                                waitingForResponse = false;
                                lastSendTime = currentTime;
                                newState = PrimaryLinkLayerState.TIMEOUT;

                                SetState(LinkLayerState.ERROR);
                            }
                            else
                            {
                                _debugLog("[SLAVE " + _address + "] TIMEOUT SC: 1 ASDU not confirmed");

                                if (_sendLinkLayerTestFunction)
                                {

                                    _debugLog("[SLAVE " + _address + "] PLL - SEND FC 02 - RESET REMOTE LINK [REPEAT]");

                                    _linkLayer.SendFixedFramePrimary(FunctionCodePrimary.TEST_FUNCTION_FOR_LINK, _address, !nextFcb, true);

                                }
                                else
                                {

                                    _debugLog("[SLAVE " + _address + "] PLL - SEND FC 03 - USER DATA CONFIRMED [REPEAT]");

                                    _linkLayer.SendVariableLengthFramePrimary(FunctionCodePrimary.USER_DATA_CONFIRMED, _address, !nextFcb, true, _lastSentASDU);

                                }

                                lastSendTime = currentTime;
                            }
                        }

                        break;

                    case PrimaryLinkLayerState.EXECUTE_SERVICE_REQUEST_RESPOND:

                        if (lastSendTime > currentTime)
                    {

                        /* 上次发送时间不合理！ */
                        lastSendTime = currentTime;
                    }

                    if (currentTime > (lastSendTime + _linkLayer.TimeoutForACK))
                        {

                            if (currentTime > (originalSendTime + _linkLayer.TimeoutRepeat))
                            {
                                _debugLog("[SLAVE " + _address + "] TIMEOUT: ASDU not confirmed after repeated transmission");
                                newState = PrimaryLinkLayerState.IDLE;
                                requestClass1Data = false;
                                requestClass2Data = false;

                                SetState(LinkLayerState.ERROR);
                            }
                            else
                            {
                                _debugLog("[SLAVE " + _address + "] TIMEOUT: ASDU not confirmed");

                                if (requestClass1Data)
                                {
                                    _debugLog("[SLAVE " + _address + "] PLL - SEND FC 10 - REQ UD 1 [REPEAT]");

                                    _linkLayer.SendFixedFramePrimary(FunctionCodePrimary.REQUEST_USER_DATA_CLASS_1, _address, !nextFcb, true);
                                }
                                else if (requestClass2Data)
                                {

                                    _debugLog("[SLAVE " + _address + "] PLL - SEND FC 11 - REQ UD 2 [REPEAT]");

                                    _linkLayer.SendFixedFramePrimary(FunctionCodePrimary.REQUEST_USER_DATA_CLASS_2, _address, !nextFcb, true);
                                }

                                lastSendTime = currentTime;
                            }
                        }

                        break;

                    case PrimaryLinkLayerState.SECONDARY_LINK_LAYER_BUSY:
                        //TODO - reject new requests from application layer?
                        break;

                }

                if (primaryState != newState)
            {
                _debugLog("[SLAVE " + _address + "] PLL - old state: " + primaryState.ToString() + " new state: " + newState.ToString());
            }

            primaryState = newState;

            }
        }

        /********************************
         * IPrimaryLinkLayerUnbalanced
         ********************************/


        public void ResetCU(int slaveAddress)
        {
            var slave = GetSlaveConnection(slaveAddress);

            if (slave != null)
        {
            slave.resetCu = true;
        }
    }

        public bool IsChannelAvailable(int slaveAddress)
        {
            var slave = GetSlaveConnection(slaveAddress);

            if (slave != null)
            {
                if (slave.IsMessageWaitingToSend() == false)
            {
                return true;
            }
        }

            return false;
        }

        public void RequestClass1Data(int slaveAddress)
        {
            var slave = GetSlaveConnection(slaveAddress);

            if (slave != null)
            {
                slave.requestClass1Data = true;
            }
        }

        public void RequestClass2Data(int slaveAddress)
        {
            var slave = GetSlaveConnection(slaveAddress);

            if (slave != null)
            {
                if (slave.IsMessageWaitingToSend())
            {
                throw new LinkLayerBusyException("Message pending");
            }
            else
            {
                slave.requestClass2Data = true;
            }
        }
        }

        public void SendConfirmed(int slaveAddress, BufferFrame message)
        {
            var slave = GetSlaveConnection(slaveAddress);

            if (slave != null)
            {
                if (slave.nextMessage != null)
            {
                throw new LinkLayerBusyException("Message pending");
            }
            else
                {
                    slave.nextMessage = message.Clone();
                    slave.requireConfirmation = true;
                }
            }
        }

        public void SendNoReply(int slaveAddress, BufferFrame message)
        {
            if (slaveAddress == _linkLayer.GetBroadcastAddress())
            {
                if (_nextBroadcastMessage != null)
            {
                throw new LinkLayerBusyException("Broadcast message pending");
            }
            else
            {
                _nextBroadcastMessage = message;
            }
        }
            else
            {
                var slave = GetSlaveConnection(slaveAddress);

                if (slave != null)
                {
                    if (slave.IsMessageWaitingToSend())
                {
                    throw new LinkLayerBusyException("Message pending");
                }
                else
                    {
                        slave.nextMessage = message;
                        slave.requireConfirmation = false;
                    }
                }
            }
        }

        /********************************
         * END IPrimaryLinkLayerUnbalanced
         ********************************/

        public PrimaryLinkLayerUnbalanced(LinkLayerEngine linkLayer, IClientLinkLayerCallbacks callbacks, Action<string> debugLog)
        {
            _linkLayer = linkLayer;
            _callbacks = callbacks;
            _debugLog = debugLog;
            _slaveConnections = new List<SlaveConnection>();
        }

        private SlaveConnection GetSlaveConnection(int slaveAddres)
        {
            foreach (var connection in _slaveConnections)
            {
                if (connection._address == slaveAddres)
            {
                return connection;
            }
        }

            return null;
        }

        public void AddSlaveConnection(int slaveAddress)
        {
            var slave = GetSlaveConnection(slaveAddress);

            if (slave == null)
        {
            _slaveConnections.Add(new SlaveConnection(slaveAddress, _linkLayer, _debugLog, this));
        }
    }

        public LinkLayerState GetStateOfSlave(int slaveAddress)
        {
            var connection = GetSlaveConnection(slaveAddress);

            if (connection != null)
        {
            return connection.linkLayerState;
        }
        else
        {
            throw new ArgumentException("No slave with this address found");
        }
    }

        public override void HandleMessage(FunctionCodeSecondary fcs, bool acd, bool dfc,
                                     int address, byte[] msg, int userDataStart, int userDataLength)
        {
            SlaveConnection slave = null;

            if (address == -1)
        {
            slave = _currentSlave;
        }
        else
        {
            slave = GetSlaveConnection(address);
        }

        if (slave != null)
            {

                slave.HandleMessage(fcs, acd, dfc, address, msg, userDataStart, userDataLength);

            }
            else
            {
                _debugLog("PLL RECV - response from unknown slave " + address + " !");
            }
        }

        private int _currentSlaveIndex = 0;

        public override void RunStateMachine()
        {
            // 驱动所有已注册从站的链路层状态机

            if (_slaveConnections.Count > 0)
            {

                if (_currentSlave == null)
                {

                    /* 调度下一次从站连接 */
                    _currentSlave = _slaveConnections[_currentSlaveIndex];
                    _currentSlaveIndex = (_currentSlaveIndex + 1) % _slaveConnections.Count;

                }

                _currentSlave.RunStateMachine();

                if (_currentSlave.waitingForResponse == false)
            {
                _currentSlave = null;
            }
        }
        }

        public override void SendLinkLayerTestFunction()
        {
        }

        public void SetLinkLayerStateChanged(LinkLayerStateChanged callback, object parameter)
        {
            _stateChanged = callback;
            _stateChangedParameter = parameter;
        }
    }
