//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;
using IEC60870.Core;


namespace IEC60870.CS101;

    internal class SecondaryLinkLayerUnbalanced : SecondaryLinkLayer
    {
        // 下一帧计数位（FCB）的期望值
        private bool _expectedFcb = true;

        private Action<string> _debugLog;
        private LinkLayerEngine _linkLayer;
        private IServerApplicationLayer _applicationLayer;

        private int _linkLayerAddress = 0;

        public SecondaryLinkLayerUnbalanced(LinkLayerEngine linkLayer, int address, IServerApplicationLayer applicationLayer, Action<string> debugLog)
        {
            _linkLayer = linkLayer;
            _linkLayerAddress = address;
            _debugLog = debugLog;
            _applicationLayer = applicationLayer;
        }

        public override int Address
        {
            get { return _linkLayerAddress; }
            set { _linkLayerAddress = value; }
        }

        private bool CheckFCB(bool fcb)
        {
            if (fcb != _expectedFcb)
            {
                _debugLog("SLL - ERROR: Frame count bit (FCB) invalid!");
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

            switch (fcp)
            {

                case FunctionCodePrimary.REQUEST_LINK_STATUS:
                    _debugLog("SLL - REQUEST LINK STATUS");
                    {
                        /* 校验 FCV=0 */
                        if (fcv)
                        {
                            _debugLog("SLL - REQUEST LINK STATUS failed - invalid FCV\n");
                            return;
                        }

                        var accessDemand = _applicationLayer.IsClass1DataAvailable();

                        _linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.STATUS_OF_LINK_OR_ACCESS_DEMAND, _linkLayerAddress, accessDemand, false);
                    }
                    break;

                case FunctionCodePrimary.RESET_REMOTE_LINK:
                    _debugLog("SLL - RESET REMOTE LINK");
                    {
                        /* 校验 FCB=0 且 FCV=0 */
                        if ((fcv) || (fcb))
                        {
                            _debugLog("SLL - RESET REMOTE LINK failed - invalid FCV/FCB\n");
                            return;
                        }

                        _expectedFcb = true;

                        if (_linkLayer.linkLayerParameters.UseSingleCharACK)
                    {
                        _linkLayer.SendSingleCharACK();
                    }
                    else
                    {
                        _linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.ACK, _linkLayerAddress, false, false);
                    }

                    _applicationLayer.ResetCUReceived(false);
                    }

                    break;

                case FunctionCodePrimary.RESET_FCB:
                    _debugLog("SLL - RESET FCB");
                    {
                        /* CS103 使用 */

                        /* 校验 FCV=0 */
                        if ((fcv) || (fcb))
                        {
                            _debugLog("SLL - RESET FCB failed - invalid FCV/FCB");
                            return;
                        }

                        _expectedFcb = true;

                        if (_linkLayer.linkLayerParameters.UseSingleCharACK)
                    {
                        _linkLayer.SendSingleCharACK();
                    }
                    else
                    {
                        _linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.ACK, _linkLayerAddress, false, false);
                    }

                    _applicationLayer.ResetCUReceived(true);
                    }
                    break;

                case FunctionCodePrimary.REQUEST_USER_DATA_CLASS_2:
                    _debugLog("SLL - REQUEST USER DATA CLASS 2");
                    {
                        if (fcv)
                        {
                            if (CheckFCB(fcb) == false)
                            {
                                _debugLog("SLL - REQ UD2 - unexpected FCB\n");
                            }
                        }

                        var asdu = _applicationLayer.GetCLass2Data();

                        var accessDemand = _applicationLayer.IsClass1DataAvailable();

                        if (asdu != null)
                    {
                        _linkLayer.SendVariableLengthFrameSecondary(FunctionCodeSecondary.RESP_USER_DATA, _linkLayerAddress, accessDemand, false, asdu);
                    }
                    else
                        {
                            if (_linkLayer.linkLayerParameters.UseSingleCharACK && (accessDemand == false))
                        {
                            _linkLayer.SendSingleCharACK();
                        }
                        else
                        {
                            _linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.RESP_NACK_NO_DATA, _linkLayerAddress, accessDemand, false);
                        }
                    }

                    }
                    break;

                case FunctionCodePrimary.REQUEST_USER_DATA_CLASS_1:
                    _debugLog("SLL - REQUEST USER DATA CLASS 1");
                    {
                        if (fcv)
                        {
                            if (CheckFCB(fcb) == false)
                            {
                                _debugLog("SLL - REQ UD1 - unexpected FCB\n");
                            }
                        }

                        var asdu = _applicationLayer.GetClass1Data();
                        var accessDemand = _applicationLayer.IsClass1DataAvailable();

                        if (asdu != null)
                    {
                        _linkLayer.SendVariableLengthFrameSecondary(FunctionCodeSecondary.RESP_USER_DATA, _linkLayerAddress, accessDemand, false, asdu);
                    }
                    else
                        {
                            if (_linkLayer.linkLayerParameters.UseSingleCharACK && (accessDemand == false))
                        {
                            _linkLayer.SendSingleCharACK();
                        }
                        else
                        {
                            _linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.RESP_NACK_NO_DATA, _linkLayerAddress, accessDemand, false);
                        }
                    }

                    }
                    break;

                case FunctionCodePrimary.USER_DATA_CONFIRMED:
                    _debugLog("SLL - USER DATA CONFIRMED");
                    {
                        var indicateUserData = true;

                        if (fcv)
                        {
                            if (CheckFCB(fcb) == false)
                            {
                                _debugLog("SLL - FCB check failed -> ignore UD confirmed\n");
                                indicateUserData = false;
                            }
                        }

                        if ((indicateUserData == true) && (userDataLength) > 0)
                        {
                            _applicationLayer.HandleReceivedData(msg, isBroadcast, userDataStart, userDataLength);
                        }

                        var accessDemand = _applicationLayer.IsClass1DataAvailable();

                        if (_linkLayer.linkLayerParameters.UseSingleCharACK && !accessDemand)
                    {
                        _linkLayer.SendSingleCharACK();
                    }
                    else
                    {
                        _linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.ACK, _linkLayerAddress, false, false);
                    }
                }
                    break;

                case FunctionCodePrimary.USER_DATA_NO_REPLY:
                    _debugLog("SLL - USER DATA NO REPLY");
                    {
                        if (fcv)
                        {
                            _debugLog("SLL - USER DATA NO REPL - invalid FCV");
                            return;
                        }
                        if (userDataLength > 0)
                        {
                            _applicationLayer.HandleReceivedData(msg, isBroadcast, userDataStart, userDataLength);
                        }
                    }
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
