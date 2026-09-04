//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;


namespace IEC60870.CS101;

    internal class SecondaryLinkLayerBalanced : SecondaryLinkLayer
    {
        // 下一帧计数位（FCB）的期望值
        private bool _expectedFcb = true;

        private Action<string> _debugLog;
        private LinkLayerEngine _linkLayer;
        private Func<int, byte[], int, int, bool> _handleApplicationLayer;

        private int _linkLayerAddress = 0;

        public SecondaryLinkLayerBalanced(LinkLayerEngine linkLayer, int address,
                                    Func<int, byte[], int, int, bool> handleApplicationLayer, Action<string> debugLog)
        {
            _linkLayer = linkLayer;
            _linkLayerAddress = address;
            _debugLog = debugLog;
            _handleApplicationLayer = handleApplicationLayer;
        }


        public override int Address
        {
            get { return _linkLayerAddress; }
            set { _linkLayerAddress = value; }
        }

        private void SendStatusOfLink(int address)
        {
            _linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.STATUS_OF_LINK_OR_ACCESS_DEMAND, address, false, false);
        }

        private bool CheckFCB(bool fcb)
        {
            if (fcb != _expectedFcb)
            {
                _debugLog("ERROR: Frame count bit (FCB) invalid!");
                //TODO change link status
                return false;
            }
            else
            {
                _expectedFcb = !_expectedFcb;
                return true;
            }
        }

        public override void HandleMessage(FunctionCodePrimary fcp, bool isBroadcast, int address, bool fcb, bool fcv, byte[] msg, int userDataStart, int userDataLength)
        {

            if (fcv)
            {
                if (CheckFCB(fcb) == false)
            {
                return;
            }
        }

            switch (fcp)
            {

                case FunctionCodePrimary.RESET_REMOTE_LINK:
                    _expectedFcb = true;
                    _debugLog("SLL - RECV RESET REMOTE LINK");

                    if (_linkLayer.linkLayerParameters.UseSingleCharACK)
                {
                    _linkLayer.SendSingleCharACK();
                }
                else
                {
                    _linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.ACK, _linkLayerAddress, false, false);
                }

                break;

                case FunctionCodePrimary.TEST_FUNCTION_FOR_LINK:
                    _debugLog("SLL -TEST FUNCTION FOR LINK");
                    // TODO：确认此处是否需要发送 DCF
                    if (_linkLayer.linkLayerParameters.UseSingleCharACK)
                {
                    _linkLayer.SendSingleCharACK();
                }
                else
                {
                    _linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.ACK, _linkLayerAddress, false, false);
                }

                break;

                case FunctionCodePrimary.USER_DATA_CONFIRMED:
                    _debugLog("SLL - USER DATA CONFIRMED");
                    if (userDataLength > 0)
                    {

                        if (_handleApplicationLayer(address, msg, userDataStart, userDataLength))
                    {
                        _linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.ACK, _linkLayerAddress, false, false);
                    }
                }
                    break;

                case FunctionCodePrimary.USER_DATA_NO_REPLY:
                    _debugLog("SLL - USER DATA NO REPLY");
                    if (userDataLength > 0)
                    {
                        _handleApplicationLayer(address, msg, userDataStart, userDataLength);
                    }
                    break;

                case FunctionCodePrimary.REQUEST_LINK_STATUS:
                    _debugLog("SLL - RECV REQUEST LINK STATUS");
                    SendStatusOfLink(_linkLayerAddress);
                    break;

                default:
                    _debugLog("SLL - UNEXPECTED LINK LAYER MESSAGE");
                    _linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.LINK_SERVICE_NOT_IMPLEMENTED, _linkLayerAddress, false, false);
                    break;

            }
        }

        public override void RunStateMachine()
        {

        }

    }
