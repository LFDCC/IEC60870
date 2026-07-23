/*
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 *
 *  See COPYING file for the complete license text.
 */

using System;
using IEC60870.Core;


namespace IEC60870.CS101.LinkLayer
{
    internal class SecondaryLinkLayerUnbalanced : SecondaryLinkLayer
    {
        // expected value of next frame count bit (FCB)
        private bool expectedFcb = true;

        private Action<string> DebugLog;
        private LinkLayer linkLayer;
        private IServerApplicationLayer applicationLayer;

        private int linkLayerAddress = 0;

        public SecondaryLinkLayerUnbalanced(LinkLayer linkLayer, int address, IServerApplicationLayer applicationLayer, Action<string> debugLog)
        {
            this.linkLayer = linkLayer;
            linkLayerAddress = address;
            DebugLog = debugLog;
            this.applicationLayer = applicationLayer;
        }

        public override int Address
        {
            get { return linkLayerAddress; }
            set { linkLayerAddress = value; }
        }

        private bool CheckFCB(bool fcb)
        {
            if (fcb != expectedFcb)
            {
                DebugLog("SLL - ERROR: Frame count bit (FCB) invalid!");
                //TODO change link status
                return false;
            }
            else
            {
                expectedFcb = !expectedFcb;
                return true;
            }
        }

        public override void HandleMessage(FunctionCodePrimary fcp, bool isBroadcast, int address, bool fcb, bool fcv, byte[] msg, int userDataStart, int userDataLength)
        {

            switch (fcp)
            {

                case FunctionCodePrimary.REQUEST_LINK_STATUS:
                    DebugLog("SLL - REQUEST LINK STATUS");
                    {
                        /* check that FCV=0 */
                        if (fcv)
                        {
                            DebugLog("SLL - REQUEST LINK STATUS failed - invalid FCV\n");
                            return;
                        }

                        bool accessDemand = applicationLayer.IsClass1DataAvailable();

                        linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.STATUS_OF_LINK_OR_ACCESS_DEMAND, linkLayerAddress, accessDemand, false);
                    }
                    break;

                case FunctionCodePrimary.RESET_REMOTE_LINK:
                    DebugLog("SLL - RESET REMOTE LINK");
                    {
                        /* check that FCB=0 and FCV=0 */
                        if ((fcv) || (fcb))
                        {
                            DebugLog("SLL - RESET REMOTE LINK failed - invalid FCV/FCB\n");
                            return;
                        }

                        expectedFcb = true;

                        if (linkLayer.linkLayerParameters.UseSingleCharACK)
                            linkLayer.SendSingleCharACK();
                        else
                            linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.ACK, linkLayerAddress, false, false);

                        applicationLayer.ResetCUReceived(false);
                    }

                    break;

                case FunctionCodePrimary.RESET_FCB:
                    DebugLog("SLL - RESET FCB");
                    {
                        /* used by CS103 */

                        /* check that FCV=0 */
                        if ((fcv) || (fcb))
                        {
                            DebugLog("SLL - RESET FCB failed - invalid FCV/FCB");
                            return;
                        }

                        expectedFcb = true;

                        if (linkLayer.linkLayerParameters.UseSingleCharACK)
                            linkLayer.SendSingleCharACK();
                        else
                            linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.ACK, linkLayerAddress, false, false);

                        applicationLayer.ResetCUReceived(true);
                    }
                    break;

                case FunctionCodePrimary.REQUEST_USER_DATA_CLASS_2:
                    DebugLog("SLL - REQUEST USER DATA CLASS 2");
                    {
                        if (fcv)
                        {
                            if (CheckFCB(fcb) == false)
                            {
                                DebugLog("SLL - REQ UD2 - unexpected FCB\n");
                            }
                        }

                        BufferFrame asdu = applicationLayer.GetCLass2Data();

                        bool accessDemand = applicationLayer.IsClass1DataAvailable();

                        if (asdu != null)
                            linkLayer.SendVariableLengthFrameSecondary(FunctionCodeSecondary.RESP_USER_DATA, linkLayerAddress, accessDemand, false, asdu);
                        else
                        {
                            if (linkLayer.linkLayerParameters.UseSingleCharACK && (accessDemand == false))
                                linkLayer.SendSingleCharACK();
                            else
                                linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.RESP_NACK_NO_DATA, linkLayerAddress, accessDemand, false);
                        }

                    }
                    break;

                case FunctionCodePrimary.REQUEST_USER_DATA_CLASS_1:
                    DebugLog("SLL - REQUEST USER DATA CLASS 1");
                    {
                        if (fcv)
                        {
                            if (CheckFCB(fcb) == false)
                            {
                                DebugLog("SLL - REQ UD1 - unexpected FCB\n");
                            }
                        }

                        BufferFrame asdu = applicationLayer.GetClass1Data();
                        bool accessDemand = applicationLayer.IsClass1DataAvailable();

                        if (asdu != null)
                            linkLayer.SendVariableLengthFrameSecondary(FunctionCodeSecondary.RESP_USER_DATA, linkLayerAddress, accessDemand, false, asdu);
                        else
                        {
                            if (linkLayer.linkLayerParameters.UseSingleCharACK && (accessDemand == false))
                                linkLayer.SendSingleCharACK();
                            else
                                linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.RESP_NACK_NO_DATA, linkLayerAddress, accessDemand, false);
                        }

                    }
                    break;

                case FunctionCodePrimary.USER_DATA_CONFIRMED:
                    DebugLog("SLL - USER DATA CONFIRMED");
                    {
                        bool indicateUserData = true;

                        if (fcv)
                        {
                            if (CheckFCB(fcb) == false)
                            {
                                DebugLog("SLL - FCB check failed -> ignore UD confirmed\n");
                                indicateUserData = false;
                            }
                        }

                        if ((indicateUserData == true) && (userDataLength) > 0)
                        {
                            applicationLayer.HandleReceivedData(msg, isBroadcast, userDataStart, userDataLength);
                        }

                        bool accessDemand = applicationLayer.IsClass1DataAvailable();

                        if (linkLayer.linkLayerParameters.UseSingleCharACK && !accessDemand)
                            linkLayer.SendSingleCharACK();
                        else
                            linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.ACK, linkLayerAddress, false, false);

                    }
                    break;

                case FunctionCodePrimary.USER_DATA_NO_REPLY:
                    DebugLog("SLL - USER DATA NO REPLY");
                    {
                        if (fcv)
                        {
                            DebugLog("SLL - USER DATA NO REPL - invalid FCV");
                            return;
                        }
                        if (userDataLength > 0)
                        {
                            applicationLayer.HandleReceivedData(msg, isBroadcast, userDataStart, userDataLength);
                        }
                    }
                    break;

                default:
                    DebugLog("SLL - UNEXPECTED LINK LAYER MESSAGE");
                    linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.LINK_SERVICE_NOT_IMPLEMENTED, linkLayerAddress, false, false);
                    break;
            }
        }

        public override void RunStateMachine()
        {

        }
    }
}

