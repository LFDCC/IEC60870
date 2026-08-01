/*
 *  Iec101Client.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
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
using IEC60870.Core;
using IEC60870.Core.Time;
using IEC60870.Core.InformationObjects;
using IEC60870.Core.File;
using IEC60870.CS101.File;



namespace IEC60870.CS101
{
    /// <summary>
    /// IEC 60870-5-101 异步主站（ClientBase）。全异步：无工作线程阻塞，收发循环由
    /// <see cref="Task"/> 驱动，底层可接串口（<see cref="SerialPort"/>）或 TouchSocket TCP 隧道。
    /// 应用层 <c>Send*</c> 维持非阻塞入队语义（与原版一致），由链路层状态机负责确认与重发。
    /// </summary>
    public class Iec101Client : ClientBase, IClientLinkLayerCallbacks
    {
        private CancellationTokenSource _cts = null;

        internal LinkLayerEngine linkLayer = null;

        internal FileClient fileClient = null;

        private SerialPort _port = null;
        private ISerialLinkTransport _transport;
        private bool _fatalError = false;

        public bool DIR
        {
            get { return linkLayer.DIR; }
            set { linkLayer.DIR = value; }
        }

        /// <summary>
        /// 运行一次协议状态机（用于不使用后台循环的场景）。
        /// </summary>
        public async Task RunAsync(CancellationToken ct = default)
        {
            if (_fatalError == false)
            {
                await linkLayer.RunAsync(ct).ConfigureAwait(false);

                if (fileClient != null)
                    fileClient.HandleFileService();
            }
        }

        private void FatalErrorHandler(object sender, EventArgs eventArgs)
        {
            _fatalError = true;
        }

        public void AddPortDeniedHandler(EventHandler eventHandler)
        {
            linkLayer.AddPortDeniedHandler(eventHandler);
        }

        /// <summary>
        /// 启动后台异步收发循环。
        /// </summary>
        public Task StartAsync(CancellationToken ct = default)
        {
            // 重复调用时先释放上一次创建的 CTS，避免泄漏（代码评审 #16）。无可取消外部 token 时不分配链接源。
            _cts?.Dispose();
            _cts = ct.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : new CancellationTokenSource();

            if (_port != null)
            {
                if (_port.IsOpen == false)
                    _port.Open();

                _port.DiscardInBuffer();
            }

            linkLayer.AddPortDeniedHandler(FatalErrorHandler);

            if (_transport is TcpClientLinkTransport tcp)
                return Task.Run(async () =>
                {
                    await tcp.ConnectAsync(_cts.Token).ConfigureAwait(false);
                    await RunLoopAsync(_cts.Token).ConfigureAwait(false);
                }, _cts.Token);

            return RunLoopAsync(_cts.Token);
        }

        /// <summary>
        /// 停止后台异步收发循环（同步取消，不阻塞）。
        /// </summary>
        public void Stop()
        {
            _cts?.Cancel();
        }

        private async Task RunLoopAsync(CancellationToken ct)
        {
            try
            {
                while (ct.IsCancellationRequested == false)
                {
                    await linkLayer.RunAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _fatalError = true;
                Console.WriteLine("Iec101Client loop error: " + ex.Message);
            }
        }

        public int OwnAddress
        {
            get { return linkLayer.OwnAddress; }
            set { linkLayer.OwnAddress = value; }
        }

        public LinkLayerState GetLinkLayerState()
        {
            if (linkLayer.LinkLayerMode == LinkLayerMode.BALANCED)
                return primaryLinkLayer.GetLinkLayerState();
            else
                return linkLayerUnbalanced.GetStateOfSlave(slaveAddress);
        }

        public override void SetReceivedRawMessageHandler(RawMessageHandler handler, object parameter)
        {
            linkLayer.SetReceivedRawMessageHandler(handler, parameter);
        }

        public override void SetSentRawMessageHandler(RawMessageHandler handler, object parameter)
        {
            linkLayer.SetSentRawMessageHandler(handler, parameter);
        }

        private PrimaryLinkLayerUnbalanced linkLayerUnbalanced = null;
        private PrimaryLinkLayerBalanced primaryLinkLayer = null;

        private SecondaryLinkLayer secondaryLinkLayer = null;

        private int slaveAddress = 0;

        private byte[] buffer = new byte[300];

        private LinkLayerParameters linkLayerParameters;
        private ApplicationLayerParameters appLayerParameters;

        private ASDUReceivedHandler asduReceivedHandler = null;
        private object asduReceivedHandlerParameter = null;

        /// <summary>收到 ASDU 的事件（多播，与 <see cref="SetASDUReceivedHandler"/> 并存；消费语义仍由 SetASDUReceivedHandler 的返回值驱动）。</summary>
        public event ASDUReceivedHandler AsduReceived;

        private Queue<BufferFrame> userDataQueue;

        private void DebugLog(string msg)
        {
            if (debugOutput)
            {
                Console.Write("CS101 MASTER: ");
                Console.WriteLine(msg);
            }
        }

        public Iec101Client(SerialPort port, LinkLayerMode mode, LinkLayerParameters llParams = null, ApplicationLayerParameters alParams = null)
        {
            if (llParams == null)
                linkLayerParameters = new LinkLayerParameters();
            else
                linkLayerParameters = llParams;

            if (alParams == null)
                appLayerParameters = new ApplicationLayerParameters();
            else
                appLayerParameters = alParams;

            _transport = new SerialTransceiverFT12(port, linkLayerParameters, DebugLog);

            InitializeLinkLayer(mode);

            this._port = port;
            fileClient = null;
        }

        public Iec101Client(Stream serialStream, LinkLayerMode mode, LinkLayerParameters llParams = null, ApplicationLayerParameters alParams = null)
        {
            if (llParams == null)
                linkLayerParameters = new LinkLayerParameters();
            else
                linkLayerParameters = llParams;

            if (alParams == null)
                appLayerParameters = new ApplicationLayerParameters();
            else
                appLayerParameters = alParams;

            _transport = new SerialTransceiverFT12(serialStream, linkLayerParameters, DebugLog);

            InitializeLinkLayer(mode);

            fileClient = null;
        }

        /// <summary>
        /// 通过 TouchSocket TCP 隧道构造主站。
        /// </summary>
        public Iec101Client(string hostname, int tcpPort, LinkLayerMode mode, LinkLayerParameters llParams = null, ApplicationLayerParameters alParams = null)
        {
            if (llParams == null)
                linkLayerParameters = new LinkLayerParameters();
            else
                linkLayerParameters = llParams;

            if (alParams == null)
                appLayerParameters = new ApplicationLayerParameters();
            else
                appLayerParameters = alParams;

            _transport = new TcpClientLinkTransport(hostname, tcpPort, linkLayerParameters, DebugLog);

            InitializeLinkLayer(mode);

            fileClient = null;
        }

        private void InitializeLinkLayer(LinkLayerMode mode)
        {
            linkLayer = new LinkLayerEngine(buffer, linkLayerParameters, _transport, DebugLog);
            linkLayer.LinkLayerMode = mode;

            if (mode == LinkLayerMode.BALANCED)
            {
                linkLayer.DIR = true;

                primaryLinkLayer = new PrimaryLinkLayerBalanced(linkLayer, GetUserData, DebugLog);

                linkLayer.SetPrimaryLinkLayer(primaryLinkLayer);
                secondaryLinkLayer = new SecondaryLinkLayerBalanced(linkLayer, 0, HandleApplicationLayer, DebugLog);
                linkLayer.SetSecondaryLinkLayer(secondaryLinkLayer);

                userDataQueue = new Queue<BufferFrame>();
            }
            else
            {
                linkLayerUnbalanced = new PrimaryLinkLayerUnbalanced(linkLayer, this, DebugLog);
                linkLayer.SetPrimaryLinkLayer(linkLayerUnbalanced);
            }
        }

        public void SetTimeouts(int messageTimeout, int characterTimeout)
        {
            _transport.SetTimeouts(messageTimeout, characterTimeout);
        }

        public void SetASDUReceivedHandler(ASDUReceivedHandler handler, object parameter)
        {
            asduReceivedHandler = handler;
            asduReceivedHandlerParameter = parameter;
        }

        public void AddSlave(int slaveAddress)
        {
            if (linkLayerUnbalanced != null)
                linkLayerUnbalanced.AddSlaveConnection(slaveAddress);
        }

        public LinkLayerState GetLinkLayerState(int slaveAddress)
        {
            if (linkLayerUnbalanced != null)
                return linkLayerUnbalanced.GetStateOfSlave(slaveAddress);
            else
                return primaryLinkLayer.GetLinkLayerState();
        }

        public void SetLinkLayerStateChangedHandler(LinkLayerStateChanged handler, object parameter)
        {
            if (linkLayerUnbalanced != null)
                linkLayerUnbalanced.SetLinkLayerStateChanged(handler, parameter);
            else
                primaryLinkLayer.SetLinkLayerStateChanged(handler, parameter);
        }

        public int SlaveAddress
        {
            set
            {
                UseSlaveAddress(value);

                if (secondaryLinkLayer != null)
                    secondaryLinkLayer.Address = slaveAddress;
            }

            get
            {
                if (primaryLinkLayer == null)
                    return slaveAddress;
                else
                    return primaryLinkLayer.LinkLayerAddressOtherStation;
            }
        }

        public void UseSlaveAddress(int slaveAddress)
        {
            if (primaryLinkLayer != null)
                primaryLinkLayer.LinkLayerAddressOtherStation = slaveAddress;

            this.slaveAddress = slaveAddress;
        }

        void IClientLinkLayerCallbacks.AccessDemand(int slaveAddress)
        {
            DebugLog("Access demand slave " + slaveAddress);
            linkLayerUnbalanced.RequestClass1Data(slaveAddress);
        }

        void IClientLinkLayerCallbacks.UserData(int slaveAddress, byte[] message, int start, int length)
        {
            DebugLog("User data slave " + slaveAddress);

            ASDU asdu;

            try
            {
                asdu = new ASDU(appLayerParameters, message, start, start + length);
            }
            catch (ASDUParsingException e)
            {
                DebugLog("ASDU parsing failed: " + e.Message);
                return;
            }

            bool messageHandled = false;

            if (fileClient != null)
                messageHandled = fileClient.HandleFileAsdu(asdu);

            if (messageHandled == false)
            {
                if (asduReceivedHandler != null)
                    asduReceivedHandler(asduReceivedHandlerParameter, slaveAddress, asdu);
                AsduReceived?.Invoke(asduReceivedHandlerParameter, slaveAddress, asdu);
            }
        }

        void IClientLinkLayerCallbacks.Timeout(int slaveAddress)
        {
            DebugLog("Timeout accessing slave " + slaveAddress);
        }

        public void PollSingleSlave(int address)
        {
            try
            {
                if (linkLayerUnbalanced != null)
                    linkLayerUnbalanced.RequestClass2Data(address);
            }
            catch (LinkLayerBusyException)
            {
                DebugLog("Link layer busy");
            }
        }

        public void RequestClass1Data(int address)
        {
            try
            {
                if (linkLayerUnbalanced != null)
                    linkLayerUnbalanced.RequestClass1Data(address);
            }
            catch (LinkLayerBusyException)
            {
                DebugLog("Link layer busy");
            }
        }

        private void EnqueueUserData(ASDU asdu)
        {
            if (linkLayerUnbalanced != null)
            {
                /* 用户线程编码，使用独立缓冲区，避免与后台接收循环共享 buffer 产生数据竞争
                   （平衡分支本就使用 new byte[256]）。SendConfirmed 仅保存帧引用，稍后由
                   链路层线程发送，故此处缓冲区不会被并发访问。 */
                BufferFrame frame = new BufferFrame(new byte[256], 0);

                asdu.Encode(frame, appLayerParameters);

                linkLayerUnbalanced.SendConfirmed(slaveAddress, frame);
            }
            else
            {
                lock (userDataQueue)
                {
                    BufferFrame frame = new BufferFrame(new byte[256], 0);

                    asdu.Encode(frame, appLayerParameters);

                    userDataQueue.Enqueue(frame);
                }
            }
        }

        private BufferFrame DequeueUserData()
        {
            lock (userDataQueue)
            {
                if (userDataQueue.Count > 0)
                    return userDataQueue.Dequeue();
                else
                    return null;
            }
        }

        private bool IsUserDataAvailable()
        {
            lock (userDataQueue)
            {
                if (userDataQueue.Count > 0)
                    return true;
                else
                    return false;
            }
        }

        private BufferFrame GetUserData()
        {
            if (IsUserDataAvailable())
                return DequeueUserData();

            return null;
        }

        private bool HandleApplicationLayer(int address, byte[] msg, int userDataStart, int userDataLength)
        {
            ASDU asdu;

            try
            {
                asdu = new ASDU(appLayerParameters, buffer, userDataStart, userDataStart + userDataLength);
            }
            catch (ASDUParsingException e)
            {
                DebugLog("ASDU parsing failed: " + e.Message);
                return false;
            }

            bool messageHandled = false;

            if (fileClient != null)
                messageHandled = fileClient.HandleFileAsdu(asdu);

            if (messageHandled == false)
            {
                if (asduReceivedHandler != null)
                    messageHandled = asduReceivedHandler(asduReceivedHandlerParameter, address, asdu);
                AsduReceived?.Invoke(asduReceivedHandlerParameter, address, asdu);
            }

            return messageHandled;
        }

        public void SendLinkLayerTestFunction()
        {
            linkLayer.SendTestFunction();
        }

        public override void SendInterrogationCommand(CauseOfTransmission cot, int ca, byte qoi)
        {
            EnqueueUserData(CommandBuilder.Interrogation(appLayerParameters, cot, ca, qoi));
        }

        public override void SendCounterInterrogationCommand(CauseOfTransmission cot, int ca, byte qcc)
        {
            EnqueueUserData(CommandBuilder.CounterInterrogation(appLayerParameters, cot, ca, qcc));
        }

        public override void SendReadCommand(int ca, int ioa)
        {
            EnqueueUserData(CommandBuilder.Read(appLayerParameters, ca, ioa));
        }

        public override void SendClockSyncCommand(int ca, CP56Time2a time)
        {
            EnqueueUserData(CommandBuilder.ClockSync(appLayerParameters, ca, time));
        }

        public override void SendTestCommand(int ca)
        {
            EnqueueUserData(CommandBuilder.Test(appLayerParameters, ca));
        }

        public override void SendTestCommandWithCP56Time2a(int ca, ushort tsc, CP56Time2a time)
        {
            EnqueueUserData(CommandBuilder.TestWithCP56Time2a(appLayerParameters, ca, tsc, time));
        }

        public override void SendResetProcessCommand(CauseOfTransmission cot, int ca, byte qrp)
        {
            EnqueueUserData(CommandBuilder.ResetProcess(appLayerParameters, cot, ca, qrp));
        }

        public override void SendDelayAcquisitionCommand(CauseOfTransmission cot, int ca, CP16Time2a delay)
        {
            EnqueueUserData(CommandBuilder.DelayAcquisition(appLayerParameters, cot, ca, delay));
        }

        public override void SendControlCommand(CauseOfTransmission cot, int ca, InformationObject sc)
        {
            EnqueueUserData(CommandBuilder.Control(appLayerParameters, cot, ca, sc));
        }

        public override void SendASDU(ASDU asdu)
        {
            EnqueueUserData(asdu);
        }

        public override ApplicationLayerParameters GetApplicationLayerParameters()
        {
            return appLayerParameters;
        }

        public override void GetFile(int ca, int ioa, NameOfFile nof, IFileReceiver receiver)
        {
            if (fileClient == null)
                fileClient = new FileClient(this, DebugLog);

            fileClient.RequestFile(ca, ioa, nof, receiver);
        }

        public override void SendFile(int ca, int ioa, NameOfFile nof, IFileProvider fileProvider)
        {
            if (fileClient == null)
                fileClient = new FileClient(this, DebugLog);

            fileClient.SendFile(ca, ioa, nof, fileProvider);
        }
    }
}
