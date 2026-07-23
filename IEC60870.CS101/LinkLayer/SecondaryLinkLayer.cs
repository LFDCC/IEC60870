

/*
 *  SecondaryLinkLayer.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 *
 *  See COPYING file for the complete license text.
 */

using IEC60870.Core;
namespace IEC60870.CS101.LinkLayer
{

    internal interface IServerApplicationLayer
    {
        bool IsClass1DataAvailable();

        BufferFrame GetClass1Data();

        BufferFrame GetCLass2Data();

        bool HandleReceivedData(byte[] msg, bool isBroadcast, int userDataStart, int userDataLength);

        void ResetCUReceived(bool onlyFCB);
    }

    internal abstract class SecondaryLinkLayer
    {
        public abstract int Address
        {
            get;
            set;
        }

        public abstract void HandleMessage(FunctionCodePrimary fcp, bool isBroadcast, int address, bool fcb, bool fcv, byte[] msg, int userDataStart, int userDataLength);

        public abstract void RunStateMachine();
    }

}

