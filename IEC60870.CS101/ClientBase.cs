

/*
 *  ClientBase.cs
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
using IEC60870.Core.Time;
using IEC60870.Core.InformationObjects;
using IEC60870.Core.File;
using IEC60870.CS101.File;
namespace IEC60870.CS101
{
    /// <summary>
    /// Handler that is called when a new ASDU is received
    /// </summary>
    public delegate bool ASDUReceivedHandler(object parameter, int slaveAddress, ASDU asdu);

    /// <summary>
    /// Common interface for CS104 and CS101 balanced and unbalanced master
    /// </summary>
    public abstract class ClientBase
    {

        protected bool debugOutput;

        public bool DebugOutput
        {
            get
            {
                return debugOutput;
            }
            set
            {
                debugOutput = value;
            }
        }

        /// <summary>
        /// Sends the interrogation command.
        /// </summary>
        /// <param name="cot">Cause of transmission</param>
        /// <param name="ca">Common address</param>
        /// <param name="qoi">Qualifier of interrogation (20 = station interrogation)</param>

        public abstract void SendInterrogationCommand(CauseOfTransmission cot, int ca, byte qoi);

        /// <summary>
        /// Sends the counter interrogation command (C_CI_NA_1 typeID: 101)
        /// </summary>
        /// <param name="cot">Cause of transmission</param>
        /// <param name="ca">Common address</param>
        /// <param name="qcc">Qualifier of counter interrogation command</param>

        public abstract void SendCounterInterrogationCommand(CauseOfTransmission cot, int ca, byte qcc);

        /// <summary>
        /// Sends a read command (C_RD_NA_1 typeID: 102).
        /// </summary>
        /// 
        /// This will send a read command C_RC_NA_1 (102) to the slave/outstation. The COT is always REQUEST (5).
        /// It is used to implement the cyclical polling of data application function.
        /// 
        /// <param name="ca">Common address</param>
        /// <param name="ioa">Information object address</param>

        public abstract void SendReadCommand(int ca, int ioa);

        /// <summary>
        /// Sends a clock synchronization command (C_CS_NA_1 typeID: 103).
        /// </summary>
        /// <param name="ca">Common address</param>
        /// <param name="time">the new time to set</param>

        public abstract void SendClockSyncCommand(int ca, CP56Time2a time);

        /// <summary>
        /// Sends a test command (C_TS_NA_1 typeID: 104).
        /// </summary>
        /// 
        /// Not required and supported by IEC 60870-5-104. 
        /// 
        /// <param name="ca">Common address</param>

        public abstract void SendTestCommand(int ca);

        /// <summary>
        /// Sends a test command with CP56Time2a time (C_TS_TA_1 typeID: 107).
        /// </summary>
        /// <param name="ca">Common address</param>
        /// <param name="tsc">test sequence number</param>
        /// <param name="time">test timestamp</param>

        public abstract void SendTestCommandWithCP56Time2a(int ca, ushort tsc, CP56Time2a time);

        /// <summary>
        /// Sends a reset process command (C_RP_NA_1 typeID: 105).
        /// </summary>
        /// <param name="cot">Cause of transmission</param>
        /// <param name="ca">Common address</param>
        /// <param name="qrp">Qualifier of reset process command</param>

        public abstract void SendResetProcessCommand(CauseOfTransmission cot, int ca, byte qrp);

        /// <summary>
        /// Sends a delay acquisition command (C_CD_NA_1 typeID: 106).
        /// </summary>
        /// <param name="cot">Cause of transmission</param>
        /// <param name="ca">Common address</param>
        /// <param name="delay">delay for acquisition</param>

        public abstract void SendDelayAcquisitionCommand(CauseOfTransmission cot, int ca, CP16Time2a delay);

        /// <summary>
        /// Sends the control command.
        /// </summary>
        /// 
        /// The type ID has to match the type of the InformationObject!
        /// 
        /// C_SC_NA_1 -> SingleCommand
        /// C_DC_NA_1 -> DoubleCommand
        /// C_RC_NA_1 -> StepCommand
        /// C_SC_TA_1 -> SingleCommandWithCP56Time2a
        /// C_SE_NA_1 -> SetpointCommandNormalized
        /// C_SE_NB_1 -> SetpointCommandScaled
        /// C_SE_NC_1 -> SetpointCommandShort
        /// C_BO_NA_1 -> Bitstring32Command
        /// 
        /// <param name="cot">Cause of transmission (use ACTIVATION to start a control sequence)</param>
        /// <param name="ca">Common address</param>
        /// <param name="sc">Information object of the command</param>

        public abstract void SendControlCommand(CauseOfTransmission cot, int ca, InformationObject sc);

        /// <summary>
        /// Sends an arbitrary ASDU to the connected slave
        /// </summary>
        /// <param name="asdu">The ASDU to send</param>
        public abstract void SendASDU(ASDU asdu);


        /// <summary>
        /// Read the file from slave (upload file)
        /// </summary>
        /// <param name="ca">CA</param>
        /// <param name="ioa">IOA</param>
        /// <param name="nof">Name of file (file type)</param>
        /// <param name="receiver">file receiver instance</param>
        public abstract void GetFile(int ca, int ioa, NameOfFile nof, IFileReceiver receiver);

        /// <summary>
        /// Sends the file to slave (download file)
        /// </summary>
        /// <param name="ca">CA</param>
        /// <param name="ioa">IOA</param>
        /// <param name="nof">Name of file (file type)</param>
        /// <param name="fileProvider">File provider instance</param>
        public abstract void SendFile(int ca, int ioa, NameOfFile nof, IFileProvider fileProvider);

        /// <summary>
        /// Get the application layer parameters used by this master instance
        /// </summary>
        /// <returns>used application layer parameters</returns>
        public abstract ApplicationLayerParameters GetApplicationLayerParameters();

        /// <summary>
        /// Sets the raw message handler for received messages
        /// </summary>
        /// <param name="handler">Handler/delegate that will be invoked when a message is received</param>
        /// <param name="parameter">will be passed to the delegate</param>
        public abstract void SetReceivedRawMessageHandler(RawMessageHandler handler, object parameter);

        /// <summary>
        /// Sets the sent message handler for sent messages.
        /// </summary>
        /// <param name="handler">Handler/delegate that will be invoked when a message is sent<</param>
        /// <param name="parameter">will be passed to the delegate</param>
        public abstract void SetSentRawMessageHandler(RawMessageHandler handler, object parameter);
    }

}

