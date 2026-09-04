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
                    //TODO - reject new requests from application layer?
                    break;

            }

            if (_primaryState != newState)
        {
            _debugLog("PLL - old state: " + _primaryState.ToString() + " new state: " + newState.ToString());
        }

        _primaryState = newState;
        }
    }
