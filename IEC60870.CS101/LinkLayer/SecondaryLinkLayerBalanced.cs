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


namespace IEC60870.CS101.LinkLayer
{
    internal class SecondaryLinkLayerBalanced : SecondaryLinkLayer
    {
        // expected value of next frame count bit (FCB)
        private bool expectedFcb = true;

        private Action<string> DebugLog;
        private LinkLayer linkLayer;
        private Func<int, byte[], int, int, bool> HandleApplicationLayer;

        private int linkLayerAddress = 0;

        public SecondaryLinkLayerBalanced(LinkLayer linkLayer, int address,
                                    Func<int, byte[], int, int, bool> handleApplicationLayer, Action<string> debugLog)
        {
            this.linkLayer = linkLayer;
            linkLayerAddress = address;
            DebugLog = debugLog;
            HandleApplicationLayer = handleApplicationLayer;
        }


        public override int Address
        {
            get { return linkLayerAddress; }
            set { linkLayerAddress = value; }
        }

        private void SendStatusOfLink(int address)
        {
            linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.STATUS_OF_LINK_OR_ACCESS_DEMAND, address, false, false);
        }

        private bool CheckFCB(bool fcb)
        {
            if (fcb != expectedFcb)
            {
                DebugLog("ERROR: Frame count bit (FCB) invalid!");
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

            if (fcv)
            {
                if (CheckFCB(fcb) == false)
                    return;
            }

            switch (fcp)
            {

                case FunctionCodePrimary.RESET_REMOTE_LINK:
                    expectedFcb = true;
                    DebugLog("SLL - RECV RESET REMOTE LINK");

                    if (linkLayer.linkLayerParameters.UseSingleCharACK)
                        linkLayer.SendSingleCharACK();
                    else
                        linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.ACK, linkLayerAddress, false, false);

                    break;

                case FunctionCodePrimary.TEST_FUNCTION_FOR_LINK:
                    DebugLog("SLL -TEST FUNCTION FOR LINK");
                    // TODO check if DCF has to be sent
                    if (linkLayer.linkLayerParameters.UseSingleCharACK)
                        linkLayer.SendSingleCharACK();
                    else
                        linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.ACK, linkLayerAddress, false, false);
                    break;

                case FunctionCodePrimary.USER_DATA_CONFIRMED:
                    DebugLog("SLL - USER DATA CONFIRMED");
                    if (userDataLength > 0)
                    {

                        if (HandleApplicationLayer(address, msg, userDataStart, userDataLength))
                            linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.ACK, linkLayerAddress, false, false);
                    }
                    break;

                case FunctionCodePrimary.USER_DATA_NO_REPLY:
                    DebugLog("SLL - USER DATA NO REPLY");
                    if (userDataLength > 0)
                    {
                        HandleApplicationLayer(address, msg, userDataStart, userDataLength);
                    }
                    break;

                case FunctionCodePrimary.REQUEST_LINK_STATUS:
                    DebugLog("SLL - RECV REQUEST LINK STATUS");
                    SendStatusOfLink(linkLayerAddress);
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

