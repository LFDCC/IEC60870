//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;


namespace IEC60870.CS101;

    public class LinkLayerBusyException : Exception
    {
        public LinkLayerBusyException(string message)
            : base(message)
        {
        }

        public LinkLayerBusyException(string message, Exception e)
            : base(message, e)
        {
        }
    }

    internal interface IClientLinkLayerCallbacks
    {

        /// <summary>
        /// 表示客户端发起的访问请求（响应中置位 ACD 位）
        /// </summary>
        /// <param name="slaveAddress">address of the slave that requested the access demand</param>
        void AccessDemand(int slaveAddress);

        /// <summary>
        /// User data (application layer data) received from a slave
        /// </summary>
        /// <param name="slaveAddress">address of the slave that sent the data</param>
        /// <param name="message">buffer containing the received message</param>
        /// <param name="start">start of user data in the buffer</param>
        /// <param name="length">length of user data in the buffer</param>
        void UserData(int slaveAddress, byte[] message, int start, int length);

        /// <summary>
        /// A former request to the slave (UD Class 1, UD Class 2, confirmed...) resulted in a timeout
        /// 站点无响应指示
        /// </summary>
        /// <param name="slaveAddress">address of the slave that caused the timeout</param>
        void Timeout(int slaveAddress);
    }

    internal abstract class PrimaryLinkLayer
    {
        public abstract void HandleMessage(FunctionCodeSecondary fcs, bool dir, bool dfc,
                                     int address, byte[] msg, int userDataStart, int userDataLength);

        public abstract void RunStateMachine();

        public abstract void SendLinkLayerTestFunction();
    }

