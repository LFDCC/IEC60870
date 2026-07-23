

/*
 *  SecondaryLinkLayer.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  IEC60870.Core.NET is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  IEC60870.Core.NET is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with IEC60870.Core.NET.  If not, see <http://www.gnu.org/licenses/>.
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

