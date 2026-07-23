/*
 *  Iec101Server.cs
 *
 *  Copyright 2016-2024 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  IEC60870.Core.NET is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  This program is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with IEC60870.Core.NET.  If not, see <http://www.gnu.org/licenses/>.
 *
 *  See COPYING file for the complete license text.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.CS101.LinkLayer;
using LinkLayerType = IEC60870.CS101.LinkLayer.LinkLayer;
using IEC60870.Core;
using IEC60870.CS101.File;
using IEC60870.Core.InformationObjects;



namespace IEC60870.CS101
{
    /// <summary>
    /// IEC 60870-5-101 异步从站（ServerBase）。全异步：无工作线程阻塞，收发循环由 <see cref="Task"/> 驱动，
    /// 底层可接串口或 TouchSocket TCP 隧道。实现 <see cref="ServerBase"/>、<see cref="IServerApplicationLayer"/>、
    /// <see cref="IClientConnection"/>。
    /// </summary>
    public class Iec101Server : ServerBase, IServerApplicationLayer, IClientConnection
    {
        private CancellationTokenSource _cts = null;

        private void DebugLog(string msg)
        {
            if (debugOutput)
            {
                Console.Write("CS101 SLAVE: ");
                Console.WriteLine(msg);
            }
        }

        void IClientConnection.SendASDU(ASDU asdu)
        {
            SendASDU(asdu);
        }

        void IClientConnection.SendACT_CON(ASDU asdu, bool negative)
        {
            asdu.Cot = CauseOfTransmission.ACTIVATION_CON;
            asdu.IsNegative = negative;

            SendASDU(asdu);
        }

        void IClientConnection.SendACT_TERM(ASDU asdu)
        {
            asdu.Cot = CauseOfTransmission.ACTIVATION_TERMINATION;
            asdu.IsNegative = false;

            SendASDU(asdu);
        }

        ApplicationLayerParameters IClientConnection.GetApplicationLayerParameters()
        {
            return parameters;
        }

        bool IServerApplicationLayer.IsClass1DataAvailable()
        {
            return IsUserDataClass1Available();
        }

        BufferFrame IServerApplicationLayer.GetClass1Data()
        {
            return DequeueUserDataClass1();
        }

        BufferFrame IServerApplicationLayer.GetCLass2Data()
        {
            BufferFrame asdu = DequeueUserDataClass2();

            if (asdu == null)
                asdu = DequeueUserDataClass1();

            return asdu;
        }

        bool IServerApplicationLayer.HandleReceivedData(byte[] msg, bool isBroadcast, int userDataStart, int userDataLength)
        {
            return HandleApplicationLayer(0, msg, userDataStart, userDataLength);
        }

        void IServerApplicationLayer.ResetCUReceived(bool onlyFcb)
        {
            lock (userDataClass1Queue)
            {
                userDataClass1Queue.Clear();
            }
            lock (userDataClass2Queue)
            {
                userDataClass2Queue.Clear();
            }
        }

        private LinkLayerType linkLayer = null;

        private byte[] buffer = new byte[300];
        private SerialPort _port = null;
        private ISerialLinkTransport _transport;
        private LinkLayerParameters linkLayerParameters;
        private LinkLayerMode linkLayerMode = LinkLayerMode.UNBALANCED;

        private int _listenPort = 2404;

        PrimaryLinkLayerBalanced primaryLinkLayerBalanced = null;

        private int linkLayerAddress = 0;
        private int linkLayerAddressOtherStation;
        /* link layer address of other station in balanced mode */

        private Queue<BufferFrame> userDataClass1Queue = new Queue<BufferFrame>();
        private int userDataClass1QueueMaxSize = 100;

        private Queue<BufferFrame> userDataClass2Queue = new Queue<BufferFrame>();
        private int userDataClass2QueueMaxSize = 100;

        private FileServer fileServer;

        private bool initialized;

        private ApplicationLayerParameters parameters = new ApplicationLayerParameters();

        public ApplicationLayerParameters Parameters
        {
            get { return parameters; }
            set { parameters = value; }
        }

        public bool DIR
        {
            get { return linkLayer.DIR; }
            set { linkLayer.DIR = value; }
        }

        public LinkLayerMode LinkLayerMode
        {
            get { return linkLayerMode; }
            set { if (initialized == false) linkLayerMode = value; }
        }

        public void Stop()
        {
            _cts?.Cancel();
        }

        internal bool IsUserDataClass1Available()
        {
            lock (userDataClass1Queue)
            {
                if (userDataClass1Queue.Count > 0)
                    return true;
                else
                    return false;
            }
        }

        public void SetUserDataQueueSizes(int class1QueueSize, int class2QueueSize)
        {
            userDataClass1QueueMaxSize = class1QueueSize;
            userDataClass2QueueMaxSize = class2QueueSize;
        }

        public bool IsUserDataClass1QueueFull()
        {
            return (userDataClass1Queue.Count == userDataClass1QueueMaxSize);
        }

        public void EnqueueUserDataClass1(ASDU asdu)
        {
            lock (userDataClass1Queue)
            {
                BufferFrame frame = new BufferFrame(new byte[256], 0);

                asdu.Encode(frame, parameters);

                userDataClass1Queue.Enqueue(frame);

                while (userDataClass1Queue.Count > userDataClass1QueueMaxSize)
                    userDataClass1Queue.Dequeue();
            }
        }

        internal BufferFrame DequeueUserDataClass1()
        {
            lock (userDataClass1Queue)
            {
                if (userDataClass1Queue.Count > 0)
                    return userDataClass1Queue.Dequeue();
                else
                    return null;
            }
        }

        internal bool IsUserDataClass2Available()
        {
            lock (userDataClass2Queue)
            {
                if (userDataClass2Queue.Count > 0)
                    return true;
                else
                    return false;
            }
        }

        public bool IsUserDataClass2QueueFull()
        {
            return (userDataClass2Queue.Count == userDataClass2QueueMaxSize);
        }

        public void EnqueueUserDataClass2(ASDU asdu)
        {
            lock (userDataClass2Queue)
            {
                BufferFrame frame = new BufferFrame(new byte[256], 0);

                asdu.Encode(frame, parameters);

                userDataClass2Queue.Enqueue(frame);

                while (userDataClass2Queue.Count > userDataClass2QueueMaxSize)
                    userDataClass2Queue.Dequeue();
            }
        }

        internal BufferFrame DequeueUserDataClass2()
        {
            lock (userDataClass2Queue)
            {
                if (userDataClass2Queue.Count > 0)
                    return userDataClass2Queue.Dequeue();
                else
                    return null;
            }
        }

        public int LinkLayerAddress
        {
            get { return linkLayerAddress; }
            set { linkLayerAddress = value; }
        }

        public int LinkLayerAddressOtherStation
        {
            get { return linkLayerAddressOtherStation; }
            set
            {
                linkLayerAddressOtherStation = value;
                if (primaryLinkLayerBalanced != null)
                    primaryLinkLayerBalanced.LinkLayerAddressOtherStation = value;
            }
        }

        public Iec101Server(SerialPort port, LinkLayerParameters parameters = null)
        {
            this._port = port;
            linkLayerParameters = parameters;
            if (linkLayerParameters == null)
                linkLayerParameters = new LinkLayerParameters();
            _transport = new SerialTransceiverFT12(port, linkLayerParameters, DebugLog);
            initialized = false;
            fileServer = new FileServer(this, GetAvailableFiles(), DebugLog);
        }

        public Iec101Server(Stream serialStream, LinkLayerParameters parameters = null)
        {
            linkLayerParameters = parameters;
            if (linkLayerParameters == null)
                linkLayerParameters = new LinkLayerParameters();
            _transport = new SerialTransceiverFT12(serialStream, linkLayerParameters, DebugLog);
            initialized = false;
            fileServer = new FileServer(this, GetAvailableFiles(), DebugLog);
        }

        /// <summary>
        /// 通过 TouchSocket TCP 隧道构造从站（监听指定端口）。
        /// </summary>
        public Iec101Server(int listenPort, LinkLayerParameters parameters = null)
        {
            _listenPort = listenPort;
            linkLayerParameters = parameters;
            if (linkLayerParameters == null)
                linkLayerParameters = new LinkLayerParameters();
            _transport = new TcpServerLinkTransport(linkLayerParameters, DebugLog);
            initialized = false;
            fileServer = new FileServer(this, GetAvailableFiles(), DebugLog);
        }

        internal void SendASDU(ASDU asdu)
        {
            EnqueueUserDataClass1(asdu);
        }

        private bool HandleApplicationLayer(int address, byte[] msg, int userDataStart, int userDataLength)
        {
            ASDU asdu;

            try
            {
                asdu = new ASDU(parameters, buffer, userDataStart, userDataStart + userDataLength);
            }
            catch (ASDUParsingException e)
            {
                DebugLog("ASDU parsing failed: " + e.Message);
                return false;
            }

            bool messageHandled = false;

            switch (asdu.TypeId)
            {
                case TypeID.C_IC_NA_1: /* 100 - interrogation command */

                    DebugLog("Rcvd interrogation command C_IC_NA_1");

                    if ((asdu.Cot == CauseOfTransmission.ACTIVATION) || (asdu.Cot == CauseOfTransmission.DEACTIVATION))
                    {
                        if (interrogationHandler != null)
                        {
                            InterrogationCommand irc = (InterrogationCommand)asdu.GetElement(0);

                            if (interrogationHandler(InterrogationHandlerParameter, this, asdu, irc.QOI))
                                messageHandled = true;
                        }
                    }
                    else
                    {
                        asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                        asdu.IsNegative = true;
                        SendASDU(asdu);
                    }

                    break;

                case TypeID.C_CI_NA_1: /* 101 - counter interrogation command */

                    DebugLog("Rcvd counter interrogation command C_CI_NA_1");

                    if ((asdu.Cot == CauseOfTransmission.ACTIVATION) || (asdu.Cot == CauseOfTransmission.DEACTIVATION))
                    {
                        if (counterInterrogationHandler != null)
                        {
                            CounterInterrogationCommand cic = (CounterInterrogationCommand)asdu.GetElement(0);

                            if (counterInterrogationHandler(counterInterrogationHandlerParameter, this, asdu, cic.QCC))
                                messageHandled = true;
                        }
                    }
                    else
                    {
                        asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                        asdu.IsNegative = true;
                        SendASDU(asdu);
                    }

                    break;

                case TypeID.C_RD_NA_1: /* 102 - read command */

                    DebugLog("Rcvd read command C_RD_NA_1");

                    if (asdu.Cot == CauseOfTransmission.REQUEST)
                    {
                        DebugLog("Read request for object: " + asdu.Ca);

                        if (readHandler != null)
                        {
                            ReadCommand rc = (ReadCommand)asdu.GetElement(0);

                            if (readHandler(readHandlerParameter, this, asdu, rc.ObjectAddress))
                                messageHandled = true;
                        }
                    }
                    else
                    {
                        asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                        asdu.IsNegative = true;
                        SendASDU(asdu);
                    }

                    break;

                case TypeID.C_CS_NA_1: /* 103 - Clock synchronization command */

                    DebugLog("Rcvd clock sync command C_CS_NA_1");

                    if (asdu.Cot == CauseOfTransmission.ACTIVATION)
                    {
                        if (clockSynchronizationHandler != null)
                        {
                            ClockSynchronizationCommand csc = (ClockSynchronizationCommand)asdu.GetElement(0);

                            if (clockSynchronizationHandler(clockSynchronizationHandlerParameter,
                                this, asdu, csc.NewTime))
                                messageHandled = true;
                        }
                    }
                    else
                    {
                        asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                        asdu.IsNegative = true;
                        SendASDU(asdu);
                    }

                    break;

                case TypeID.C_TS_NA_1: /* 104 - test command */

                    DebugLog("Rcvd test command C_TS_NA_1");

                    if (asdu.Cot != CauseOfTransmission.ACTIVATION)
                    {
                        asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                        asdu.IsNegative = true;
                    }
                    else
                        asdu.Cot = CauseOfTransmission.ACTIVATION_CON;

                    SendASDU(asdu);

                    messageHandled = true;

                    break;

                case TypeID.C_RP_NA_1: /* 105 - Reset process command */

                    DebugLog("Rcvd reset process command C_RP_NA_1");

                    if (asdu.Cot == CauseOfTransmission.ACTIVATION)
                    {
                        if (resetProcessHandler != null)
                        {
                            ResetProcessCommand rpc = (ResetProcessCommand)asdu.GetElement(0);

                            if (resetProcessHandler(resetProcessHandlerParameter,
                                this, asdu, rpc.QRP))
                                messageHandled = true;
                        }
                    }
                    else
                    {
                        asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                        asdu.IsNegative = true;
                        SendASDU(asdu);
                    }

                    break;

                case TypeID.C_CD_NA_1: /* 106 - Delay acquisition command */

                    DebugLog("Rcvd delay acquisition command C_CD_NA_1");

                    if ((asdu.Cot == CauseOfTransmission.ACTIVATION) || (asdu.Cot == CauseOfTransmission.SPONTANEOUS))
                    {
                        if (delayAcquisitionHandler != null)
                        {
                            DelayAcquisitionCommand dac = (DelayAcquisitionCommand)asdu.GetElement(0);

                            if (delayAcquisitionHandler(delayAcquisitionHandlerParameter,
                                this, asdu, dac.Delay))
                                messageHandled = true;
                        }
                    }
                    else
                    {
                        asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                        asdu.IsNegative = true;
                        SendASDU(asdu);
                    }

                    break;
            }

            if (messageHandled == false)
                messageHandled = fileServer.HandleFileAsdu(asdu);

            if ((messageHandled == false) && (asduHandler != null))
                if (asduHandler(asduHandlerParameter, this, asdu))
                    messageHandled = true;

            if (messageHandled == false)
            {
                asdu.Cot = CauseOfTransmission.UNKNOWN_TYPE_ID;
                asdu.IsNegative = true;
                SendASDU(asdu);
            }

            return true;
        }

        private BufferFrame GetUserData()
        {
            if (IsUserDataClass1Available())
                return DequeueUserDataClass1();
            else if (IsUserDataClass2Available())
                return DequeueUserDataClass2();
            else
                return null;
        }

        public void SendLinkLayerTestFunction()
        {
            linkLayer.SendTestFunction();
        }

        /// <summary>
        /// 运行一次消息接收与状态机。可不使用后台循环而手动驱动。
        /// </summary>
        public async Task RunAsync(CancellationToken ct = default)
        {
            if (initialized == false)
            {
                linkLayer = new LinkLayerType(buffer, linkLayerParameters, _transport, DebugLog);
                linkLayer.LinkLayerMode = linkLayerMode;

                if (linkLayerMode == LinkLayerMode.BALANCED)
                {
                    PrimaryLinkLayerBalanced primaryLinkLayerBalanced = new PrimaryLinkLayerBalanced(linkLayer, GetUserData, DebugLog);
                    primaryLinkLayerBalanced.LinkLayerAddressOtherStation = linkLayerAddressOtherStation;

                    linkLayer.SetPrimaryLinkLayer(primaryLinkLayerBalanced);

                    linkLayer.SetSecondaryLinkLayer(new SecondaryLinkLayerBalanced(linkLayer, linkLayerAddressOtherStation, HandleApplicationLayer, DebugLog));
                }
                else
                {
                    linkLayer.SetSecondaryLinkLayer(new SecondaryLinkLayerUnbalanced(linkLayer, linkLayerAddress, this, DebugLog));
                }

                initialized = true;
            }

            if (fileServer != null)
                fileServer.HandleFileTransmission();

            await linkLayer.RunAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 启动后台异步收发循环。
        /// </summary>
        public async Task StartAsync(CancellationToken ct = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            if (_port != null)
            {
                if (_port.IsOpen == false)
                    _port.Open();

                _port.DiscardInBuffer();
            }

            if (_transport is TcpServerLinkTransport srv)
                await srv.StartAsync(_listenPort, _cts.Token).ConfigureAwait(false);

            await RunLoopAsync(_cts.Token).ConfigureAwait(false);
        }

        private async Task RunLoopAsync(CancellationToken ct)
        {
            try
            {
                while (ct.IsCancellationRequested == false)
                {
                    await RunAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Console.WriteLine("Iec101Server loop error: " + ex.Message);
            }
        }

        public override int FileTimeout
        {
            get
            {
                if (fileServer != null)
                    return (int)fileServer.Timeout;
                else
                    return 0;
            }

            set
            {
                if (fileServer != null)
                    fileServer.Timeout = value;
            }
        }

        public void SetReceivedRawMessageHandler(RawMessageHandler handler, object parameter)
        {
            linkLayer.SetReceivedRawMessageHandler(handler, parameter);
        }

        public void SetSentRawMessageHandler(RawMessageHandler handler, object parameter)
        {
            linkLayer.SetSentRawMessageHandler(handler, parameter);
        }
    }
}
