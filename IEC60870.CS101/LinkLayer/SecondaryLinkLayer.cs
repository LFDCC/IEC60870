

//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using IEC60870.Core;
namespace IEC60870.CS101;


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

