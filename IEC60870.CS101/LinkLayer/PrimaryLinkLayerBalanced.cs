//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;
using IEC60870.Core;


namespace IEC60870.CS101;

    internal class PrimaryLinkLayerBalanced : PrimaryLinkLayer
    {
        private Action<string> _debugLog;

        private PrimaryLinkLayerState _primaryState = PrimaryLinkLayerState.IDLE;
        private LinkLayerState _state = LinkLayerState.IDLE;

        private bool _waitingForResponse = false;
        private long _lastSendTime;
        private long _originalSendTime;
        private bool _sendLinkLayerTestFunction = false;
        private bool _nextFcb = true;

        private BufferFrame _lastSendASDU = null; /* 超时后重发的上一帧 ASDU */

        private int _linkLayerAddressOtherStation = 0;

        private LinkLayerEngine _linkLayer;

        Func<BufferFrame> GetUserData;

        private LinkLayerStateChanged _stateChangedCallback = null;
        private object _stateChangedCallbackParameter = null;

        public PrimaryLinkLayerBalanced(LinkLayerEngine linkLayer, Func<BufferFrame> getUserData, Action<string> debugLog)
        {
            _debugLog = debugLog;
            GetUserData = getUserData;
            _linkLayer = linkLayer;
        }

        public void SetLinkLayerStateChanged(LinkLayerStateChanged handler, object parameter)
        {
            _stateChangedCallback = handler;
            _stateChangedCallbackParameter = parameter;
        }

        public LinkLayerState GetLinkLayerState()
        {
            return _state;
        }

        public int LinkLayerAddressOtherStation
        {
            set
            {
                _linkLayerAddressOtherStation = value;
            }

            get
            {
                return _linkLayerAddressOtherStation;
            }
        }

        private void SetNewState(LinkLayerState newState)
        {
            if (newState != _state)
            {
                _state = newState;

                if (_stateChangedCallback != null)
            {
                _stateChangedCallback(_stateChangedCallbackParameter, -1, newState);
            }
        }
        }

        public override void HandleMessage(FunctionCodeSecondary fcs, bool dir, bool dfc,
                                     int address, byte[] msg, int userDataStart, int userDataLength)
        {
            var newState = _primaryState;

            if (dfc)
            {
                switch (_primaryState)
                {
                    case PrimaryLinkLayerState.EXECUTE_REQUEST_STATUS_OF_LINK:
                    case PrimaryLinkLayerState.EXECUTE_RESET_REMOTE_LINK:
                        newState = PrimaryLinkLayerState.EXECUTE_REQUEST_STATUS_OF_LINK;
                        break;
                    case PrimaryLinkLayerState.EXECUTE_SERVICE_SEND_CONFIRM:
                    case PrimaryLinkLayerState.SECONDARY_LINK_LAYER_BUSY:
                        newState = PrimaryLinkLayerState.SECONDARY_LINK_LAYER_BUSY;
                        break;
                }

                SetNewState(LinkLayerState.BUSY);
                _primaryState = newState;
                return;
            }

            switch (fcs)
            {

                case FunctionCodeSecondary.ACK:

                    _debugLog("PLL - received ACK");

                    if (_primaryState == PrimaryLinkLayerState.EXECUTE_RESET_REMOTE_LINK)
                    {
                        newState = PrimaryLinkLayerState.LINK_LAYERS_AVAILABLE;
                        SetNewState(LinkLayerState.AVAILABLE);

                        _waitingForResponse = false;
                    }
                    else if (_primaryState == PrimaryLinkLayerState.EXECUTE_SERVICE_SEND_CONFIRM)
                    {

                        if (_sendLinkLayerTestFunction)
                    {
                        _sendLinkLayerTestFunction = false;
                    }

                    newState = PrimaryLinkLayerState.LINK_LAYERS_AVAILABLE;
                        SetNewState(LinkLayerState.AVAILABLE);

                        _waitingForResponse = false;
                    }
                    else if (_primaryState == PrimaryLinkLayerState.EXECUTE_REQUEST_STATUS_OF_LINK)
                    {
                        _debugLog("PLL - ACK (FC 0) unexpected -> expected status-of-link (FC 11)");
                    }
                    else
                    {
                        _waitingForResponse = false;
                    }

                    break;

                case FunctionCodeSecondary.NACK:
                    _debugLog("PLL - received NACK");
                    if (_primaryState == PrimaryLinkLayerState.EXECUTE_SERVICE_SEND_CONFIRM)
                    {
                        newState = PrimaryLinkLayerState.SECONDARY_LINK_LAYER_BUSY;
                        SetNewState(LinkLayerState.BUSY);
                    }
                    break;

                case FunctionCodeSecondary.RESP_USER_DATA:

                    _debugLog("PLL - RESV FC 08 - RESP USER DATA");

                    newState = PrimaryLinkLayerState.IDLE;
                    SetNewState(LinkLayerState.ERROR);

                    break;

                case FunctionCodeSecondary.RESP_NACK_NO_DATA:

                    _debugLog("PLL - RECV FC 09 - RESP NACK - NO DATA\n");

                    newState = PrimaryLinkLayerState.IDLE;
                    SetNewState(LinkLayerState.ERROR);

                    break;

                case FunctionCodeSecondary.STATUS_OF_LINK_OR_ACCESS_DEMAND:
                    _debugLog("PLL - RECV FC 11 - STATUS OF LINK");

                    if (_primaryState == PrimaryLinkLayerState.EXECUTE_REQUEST_STATUS_OF_LINK)
                    {
                        _debugLog("PLL - SEND RESET REMOTE LINK to address " + _linkLayerAddressOtherStation);

                        // 同上：本端发起链路复位即约定 FCB 归零，发送侧序号必须同步复位。
                        ResetSequenceState();

                        _linkLayer.SendFixedFramePrimary(FunctionCodePrimary.RESET_REMOTE_LINK, _linkLayerAddressOtherStation, false, false);
                        _lastSendTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        _waitingForResponse = true;
                        newState = PrimaryLinkLayerState.EXECUTE_RESET_REMOTE_LINK;
                        SetNewState(LinkLayerState.BUSY);
                    }
                    else
                    { /* 非法报文 */
                        newState = PrimaryLinkLayerState.IDLE;
                        SetNewState(LinkLayerState.ERROR);
                    }

                    break;

                case FunctionCodeSecondary.LINK_SERVICE_NOT_FUNCTIONING:
                case FunctionCodeSecondary.LINK_SERVICE_NOT_IMPLEMENTED:
                    _debugLog("PLL - link layer service not functioning/not implemented in secondary station");

                    if (_sendLinkLayerTestFunction)
                {
                    _sendLinkLayerTestFunction = false;
                }

                if (_primaryState == PrimaryLinkLayerState.EXECUTE_SERVICE_SEND_CONFIRM)
                    {
                        newState = PrimaryLinkLayerState.LINK_LAYERS_AVAILABLE;
                        SetNewState(LinkLayerState.AVAILABLE);
                    }
                    break;

                default:
                    _debugLog("UNEXPECTED SECONDARY LINK LAYER MESSAGE");
                    break;
            }

            _debugLog("PLL RECV - old state: " + _primaryState.ToString() + " new state: " + newState.ToString());

            _primaryState = newState;

        }

        public override void SendLinkLayerTestFunction()
        {
            _sendLinkLayerTestFunction = true;
        }

        /// <summary>
        /// 复位本端发送侧序号状态（FCB 与重发表）。
        /// </summary>
        /// <remarks>
        /// 必须在链路复位（RESET_REMOTE_LINK）/ 新连接建立时调用：对端在链路复位时把
        /// 「期望的 FCB」重置为 1，本端若继续沿用上一轮会话的 FCB，则本端发出的第一帧
        /// 会被对端判为 FCB 错误而**静默丢弃**（101 平衡模式靠 FCB 去重，宁可丢也不重复执行）。
        /// 实测症状：反复重连后，第 2 轮起总召唤的 ACT_CON（从站推的第一帧）稳定丢失，
        /// 数据帧与 ACT_TERM 正常 —— 因为只有跨会话的第一帧带着错误的 FCB。
        /// </remarks>
        internal void ResetSequenceState()
        {
            _nextFcb = true;
            _lastSendASDU = null;
            _waitingForResponse = false;
            _originalSendTime = 0;
            _lastSendTime = 0;
        }

        public override void RunStateMachine()
        {
            var currentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var newState = _primaryState;

            switch (_primaryState)
            {

                case PrimaryLinkLayerState.IDLE:

                    _originalSendTime = 0;
                    _sendLinkLayerTestFunction = false;

                    _linkLayer.SendFixedFramePrimary(FunctionCodePrimary.REQUEST_LINK_STATUS, _linkLayerAddressOtherStation, false, false);

                    _lastSendTime = currentTime;
                    _waitingForResponse = true;

                    newState = PrimaryLinkLayerState.EXECUTE_REQUEST_STATUS_OF_LINK;

                    break;

                case PrimaryLinkLayerState.EXECUTE_REQUEST_STATUS_OF_LINK:

                    if (_waitingForResponse)
                    {
                        if (_lastSendTime > currentTime)
                    {

                        /* 上次发送时间不合理！ */
                        _lastSendTime = currentTime;
                    }

                    if (currentTime > (_lastSendTime + _linkLayer.TimeoutForACK))
                        {
                            newState = PrimaryLinkLayerState.IDLE;
                        }
                    }
                    else
                    {
                        _debugLog("PLL - SEND RESET REMOTE LINK to address " + _linkLayerAddressOtherStation);

                        // 本端发起链路复位：双方约定 FCB 从头开始，发送侧序号也必须归位。
                        // 否则复位后发往对端的第一帧会带着上一会话的 FCB，被对端当作重复帧
                        // 静默丢弃（与「收到对端 RESET_REMOTE_LINK」那一侧是对称的两个半）。
                        ResetSequenceState();

                        _linkLayer.SendFixedFramePrimary(FunctionCodePrimary.RESET_REMOTE_LINK, _linkLayerAddressOtherStation, false, false);

                        _lastSendTime = currentTime;
                        _waitingForResponse = true;
                        newState = PrimaryLinkLayerState.EXECUTE_RESET_REMOTE_LINK;
                    }

                    break;

                case PrimaryLinkLayerState.EXECUTE_RESET_REMOTE_LINK:

                    if (_waitingForResponse)
                    {
                        if (_lastSendTime > currentTime)
                    {

                        /* 上次发送时间不合理！ */
                        _lastSendTime = currentTime;
                    }

                    if (currentTime > (_lastSendTime + _linkLayer.TimeoutForACK))
                        {
                            _waitingForResponse = false;
                            newState = PrimaryLinkLayerState.IDLE;
                            SetNewState(LinkLayerState.ERROR);
                        }
                    }
                    else
                    {
                        newState = PrimaryLinkLayerState.LINK_LAYERS_AVAILABLE;
                        SetNewState(LinkLayerState.AVAILABLE);
                    }

                    break;

                case PrimaryLinkLayerState.LINK_LAYERS_AVAILABLE:

                    if (_lastSendTime > currentTime)
                {

                    /* 上次发送时间不合理！ */
                    _lastSendTime = currentTime;
                }

                if (_sendLinkLayerTestFunction)
                    {
                        _debugLog("PLL - SEND TEST LINK");

                        _linkLayer.SendFixedFramePrimary(FunctionCodePrimary.TEST_FUNCTION_FOR_LINK, _linkLayerAddressOtherStation, _nextFcb, true);

                        _nextFcb = !_nextFcb;
                        _lastSendTime = currentTime;
                        _originalSendTime = _lastSendTime;
                        newState = PrimaryLinkLayerState.EXECUTE_SERVICE_SEND_CONFIRM;
                    }
                    else
                    {
                        var asdu = GetUserData();

                        if (asdu != null)
                        {
                            _linkLayer.SendVariableLengthFramePrimary(FunctionCodePrimary.USER_DATA_CONFIRMED, _linkLayerAddressOtherStation, _nextFcb, true, asdu);

                            _lastSendASDU = asdu; /* keep for message repetition after timeout */

                            _nextFcb = !_nextFcb;
                            _lastSendTime = currentTime;
                            _originalSendTime = _lastSendTime;
                            _waitingForResponse = true;

                            newState = PrimaryLinkLayerState.EXECUTE_SERVICE_SEND_CONFIRM;
                        }
                    }

                    break;

                case PrimaryLinkLayerState.EXECUTE_SERVICE_SEND_CONFIRM:

                    if (_lastSendTime > currentTime)
                {

                    /* 上次发送时间不合理！ */
                    _lastSendTime = currentTime;
                }

                if (currentTime > (_lastSendTime + _linkLayer.TimeoutForACK))
                    {

                        if (currentTime > (_originalSendTime + _linkLayer.TimeoutRepeat))
                        {
                            _debugLog("TIMEOUT: ASDU not confirmed after repeated transmission");
                            newState = PrimaryLinkLayerState.IDLE;
                            SetNewState(LinkLayerState.ERROR);
                        }
                        else
                        {
                            _debugLog("TIMEOUT: ASDU not confirmed");

                            if (_sendLinkLayerTestFunction)
                            {
                                _debugLog("PLL - REPEAT SEND RESET REMOTE LINK");
                                _linkLayer.SendFixedFramePrimary(FunctionCodePrimary.TEST_FUNCTION_FOR_LINK, _linkLayerAddressOtherStation, !_nextFcb, true);
                            }
                            else
                            {
                                _debugLog("PLL - repeat last ASDU");
                                if(_lastSendASDU != null)
                            {
                                _linkLayer.SendVariableLengthFramePrimary(FunctionCodePrimary.USER_DATA_CONFIRMED, _linkLayerAddressOtherStation, !_nextFcb, true, _lastSendASDU);
                            }
                        }

                            _lastSendTime = currentTime;
                        }
                    }

                    break;

                case PrimaryLinkLayerState.SECONDARY_LINK_LAYER_BUSY:
                    // 对端忙（DFC=1 / NACK）后必须有退出路径：原实现是空 break，
                    // 一次"装置启动瞬间回 NACK"就会让主站永久停在 BUSY，不再发送任何
                    // ASDU、也不复位链路 —— 现场表现为"链路显示在线但永远没有数据"。
                    // 超过 TimeoutRepeat 仍未转为空闲，就重新建立链路。
                    if (currentTime > _lastSendTime + _linkLayer.TimeoutRepeat)
                    {
                        _debugLog("PLL - secondary busy timeout -> re-establish link");
                        _waitingForResponse = false;
                        newState = PrimaryLinkLayerState.IDLE;
                        SetNewState(LinkLayerState.ERROR);
                    }
                    break;

            }

            if (_primaryState != newState)
        {
            _debugLog("PLL - old state: " + _primaryState.ToString() + " new state: " + newState.ToString());
        }

        _primaryState = newState;
        }
    }
