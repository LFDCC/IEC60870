//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

/*
 *  CS101 从站侧文件传输服务：可用文件注册表 + 状态机驱动的文件服务
 *  （自 FileServices.cs 拆分）。
 */

using System;
using System.Collections.Generic;
using IEC60870.Core;

namespace IEC60870.CS101;

    public enum FileServerState
    {
        UNSELECTED_IDLE,
        WAITING_FOR_FILE_CALL,
        WAITING_FOR_SECTION_CALL,
        TRANSMIT_SECTION,
        WAITING_FOR_SECTION_ACK,
        WAITING_FOR_FILE_ACK,
        SEND_ABORT,

        TRANSFER_COMPLETED,

        WAITING_FOR_SECTION_READY,
        RECEIVE_SECTION,
    }

    /// <summary>
    /// Encapsulates a IFileProvider object to add some state information
    /// </summary>
    internal class FileServerEntry
    {

        public FileServerEntry(IFileProvider file)
        {
            provider = file;
        }

        public IFileProvider provider = null;
        public object selectedBy = null;

    }

    /// <summary>
    /// Represents the available files in a file client or file server
    /// </summary>
    public class FilesAvailable
    {

        private List<FileServerEntry> _availableFiles = new List<FileServerEntry>();

        internal FileServerEntry GetFile(int ca, int ioa, NameOfFile nof)
        {
            lock (_availableFiles)
            {

                foreach (var file in _availableFiles)
                {
                    if ((file.provider.GetCA() == ca) && (file.provider.GetIOA() == ioa))
                    {

                        if (nof == NameOfFile.DEFAULT)
                    {
                        return file;
                    }
                    else
                        {

                            if (nof == file.provider.GetNameOfFile())
                        {
                            return file;
                        }
                    }
                    }
                }
            }

            return null;
        }

        internal void SendDirectoy(IClientConnection masterConnection, bool spontaneous)
        {
            CauseOfTransmission cot;

            if (spontaneous)
        {
            cot = CauseOfTransmission.SPONTANEOUS;
        }
        else
        {
            cot = CauseOfTransmission.REQUEST;
        }

        lock (_availableFiles)
            {

                var size = _availableFiles.Count;
                var i = 0;

                var currentCa = -1;
                var currentIOA = -1;

                ASDU directoryAsdu = null;

                foreach (var file in _availableFiles)
                {

                    var newAsdu = false;

                    if (file.provider.GetCA() != currentCa)
                    {
                        currentCa = file.provider.GetCA();
                        newAsdu = true;
                    }

                    if (currentIOA != (file.provider.GetIOA() - 1))
                    {
                        newAsdu = true;
                    }

                    if (newAsdu)
                    {
                        if (directoryAsdu != null)
                        {
                            masterConnection.SendASDU(directoryAsdu);
                            directoryAsdu = null;
                        }
                    }

                    currentIOA = file.provider.GetIOA();

                    i++;

                    if (directoryAsdu == null)
                    {
                        directoryAsdu = new ASDU(masterConnection.GetApplicationLayerParameters(), cot, false, false, 0, currentCa, true);
                    }

                    var lastFile = (i == size);

                    byte sof = 0;

                    if (lastFile)
                {
                    sof = 0x20;
                }

                InformationObject io = new FileDirectory(currentIOA, file.provider.GetNameOfFile(), file.provider.GetFileSize(), sof, new CP56Time2a(file.provider.GetFileDate()));

                    if (directoryAsdu.AddInformationObject(io) == false)
                    {
                        masterConnection.SendASDU(directoryAsdu);

                        directoryAsdu = new ASDU(masterConnection.GetApplicationLayerParameters(), cot, false, false, 0, currentCa, true);
                        directoryAsdu.AddInformationObject(io);
                    }
                }

                if (directoryAsdu != null)
                {
                    masterConnection.SendASDU(directoryAsdu);
                }

            }
        }

        /// <summary>
        /// Adds a file to the list of available files
        /// </summary>
        /// <param name="file">file to add</param>
        public void AddFile(IFileProvider file)
        {
            lock (_availableFiles)
            {

                _availableFiles.Add(new FileServerEntry(file));
            }

        }

        /// <summary>
        /// Removes a file from the list of available files
        /// </summary>
        /// <param name="file">file to remove</param>
        public void RemoveFile(IFileProvider file)
        {
            lock (_availableFiles)
            {

                foreach (var availableFile in _availableFiles)
                {

                    if (availableFile.provider == file)
                    {
                        _availableFiles.Remove(availableFile);
                        return;
                    }

                }
            }
        }

        /// <summary>
        /// Gets the list of available files
        /// </summary>
        /// <returns>the list of available files</returns>
        public List<IFileProvider> GetFiles()
        {
            // 与 AddFile/RemoveFile/GetFile/SendDirectoy 一致，在锁内取快照，
            // 避免并发增删时 InvalidOperationException 或读到中间状态。
            lock (_availableFiles)
            {
                var files = new List<IFileProvider>(_availableFiles.Count);
                foreach (var file in _availableFiles)
                {
                    files.Add(file.provider);
                }

                return files;
            }
        }

    }

    public class FileServer
    {

        public FileServer(IClientConnection masterConnection, FilesAvailable availableFiles, DebugLogger logger)
        {
            _transferState = FileServerState.UNSELECTED_IDLE;
            _alParameters = masterConnection.GetApplicationLayerParameters();
            _maxSegmentSize = FileSegment.GetMaxDataSize(_alParameters);
            _availableFiles = availableFiles;
            _logger = logger;
            _connection = masterConnection;
        }

        private FilesAvailable _availableFiles;

        private FileServerEntry _selectedFile;

        private DebugLogger _logger;

        private ApplicationLayerParameters _alParameters;

        private IClientConnection _connection;
        private int _maxSegmentSize;

        private byte _currentSectionNumber;
        private int _currentSectionSize;
        private int _currentSectionOffset;
        private byte _sectionChecksum = 0;
        private byte _fileChecksum = 0;

        private long _timeout = 3000;
        private long _lastSentTime = 0;

        private int _ca;
        private int _ioa;
        private NameOfFile _nof;


        private FileServerState _transferState;

        private FileReadyHandler _fileReadyHandler = null;
        private object _fileReadyHandlerParameter = null;

        private IFileReceiver _fileReceiver = null;

        // 诊断计数：只在排障时使用（长跑失败信息里带上，用于区分
        // "SELECT 没到从站" / "从站拒绝了" / "从站回了但主站没处理"）。
        private long _selectFileReceived;
        private long _fileReadySent;
        private long _unknownFileSelects;
        private long _callFileReceived;
        private long _unexpectedCallFile;
        private long _resetCount;

        internal string Diagnostics =>
            $"state={_transferState}, selected={_selectedFile != null}, receiver={_fileReceiver != null}, "
            + $"selects={Interlocked.Read(ref _selectFileReceived)}, fileReady={Interlocked.Read(ref _fileReadySent)}, "
            + $"unknown={Interlocked.Read(ref _unknownFileSelects)}, calls={Interlocked.Read(ref _callFileReceived)}, "
            + $"unexpectedCalls={Interlocked.Read(ref _unexpectedCallFile)}, resets={Interlocked.Read(ref _resetCount)}";

        public void SetFileReadyHandler(FileReadyHandler handler, object parameter)
        {
            _fileReadyHandler = handler;
            _fileReadyHandlerParameter = parameter;
        }

        /// <summary>
        /// 链路重建时丢弃上一次会话残留的文件传输状态。
        /// </summary>
        /// <remarks>
        /// 必须在"新链路建立"时调用。从站的 <see cref="FileServer"/> 是随
        /// <c>Iec101Server</c> 长期存在的单实例，主站在传输中途断线（例如收完
        /// 最后一段就断开、DEACTIVATE/最终 ACK 没发出去）时，状态机会停在
        /// <c>WAITING_FOR_FILE_ACK</c> 一类非空闲态；这个状态没有任何"对端已换人"
        /// 的自愈路径，新主站连上来发 SELECT FILE 会被当成"Unexpected SELECT FILE"
        /// 静默忽略，直到 3 秒超时才恢复 —— 表现为重连后第一次文件传输必然超时。
        ///
        /// 超时路径只覆盖 <c>_selectedFile</c>/<c>_fileReceiver</c> 非空的组合；
        /// 一旦两者都为空但状态仍非 IDLE，<see cref="HandleFileTransmission"/>
        /// 的门槛会让超时检查整段跳过，状态就永久悬住。这里无条件复位到 IDLE。
        /// </remarks>
        internal void Reset()
        {
            Interlocked.Increment(ref _resetCount);
            _logger?.Invoke("Link rebuilt - reset file transfer state");

            if (_selectedFile != null)
            {
                var provider = _selectedFile.provider;
                _selectedFile.selectedBy = null;
                _selectedFile = null;

                try
                {
                    provider.TransferComplete(false);
                }
                catch (Exception ex)
                {
                    _logger("File provider TransferComplete threw: " + ex.Message);
                }
            }

            if (_fileReceiver != null)
            {
                var receiver = _fileReceiver;
                _fileReceiver = null;

                try
                {
                    receiver.Finished(FileErrorCode.ABORTED_BY_REMOTE);
                }
                catch (Exception ex)
                {
                    _logger("File receiver Finished threw: " + ex.Message);
                }
            }

            ResetTransferStats();
            _transferState = FileServerState.UNSELECTED_IDLE;
        }

        /// <summary>只复位一次传输内的编号/偏移/校验和，不动状态机。</summary>
        private void ResetTransferStats()
        {
            _currentSectionNumber = 0;
            _currentSectionSize = 0;
            _currentSectionOffset = 0;
            _sectionChecksum = 0;
            _fileChecksum = 0;
            _lastSentTime = 0;
        }

        /// <summary>
        /// Gets or sets the timeout for file transfers
        /// </summary>
        /// <value>timeout in ms</value>
        public long Timeout
        {
            get
            {
                return _timeout;
            }
            set
            {
                _timeout = value;
            }
        }

        public bool HandleFileAsdu(ASDU asdu)
        {
            var handled = true;

            switch (asdu.TypeId)
            {
                case TypeID.F_FR_NA_1: /* File Ready */

                    _logger("Received file ready F_FR_NA_1");

                    if (asdu.Cot == CauseOfTransmission.FILE_TRANSFER)
                    {
                        if (_fileReadyHandler != null)
                        {

                            FileReady fileReady = (FileReady)asdu.GetElement(0);

                            _fileReceiver = _fileReadyHandler(_fileReadyHandlerParameter, asdu.Ca, fileReady.ObjectAddress, fileReady.NOF, fileReady.LengthOfFile);

                            if (_fileReceiver == null)
                            {
                                asdu.IsNegative = true;
                                asdu.Cot = CauseOfTransmission.UNKNOWN_INFORMATION_OBJECT_ADDRESS;
                                _connection.SendASDU(asdu);
                            }
                            else
                            {

                                _ca = asdu.Ca;
                                _ioa = fileReady.ObjectAddress;
                                _nof = fileReady.NOF;

                                /* send call file */

                                ASDU callFile = new ASDU(_alParameters, CauseOfTransmission.FILE_TRANSFER, false, false, 0, asdu.Ca, false);

                                callFile.AddInformationObject(new FileCallOrSelect(fileReady.ObjectAddress, fileReady.NOF, 0, SelectAndCallQualifier.REQUEST_FILE));

                                _connection.SendASDU(callFile);

                                _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                                _transferState = FileServerState.WAITING_FOR_SECTION_READY;
                            }

                        }
                        else
                        {
                            asdu.IsNegative = true;
                            asdu.Cot = CauseOfTransmission.UNKNOWN_INFORMATION_OBJECT_ADDRESS;
                            _connection.SendASDU(asdu);
                        }
                    }
                    else
                    {
                        asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                        _connection.SendASDU(asdu);
                    }

                    break;

                case TypeID.F_SR_NA_1: /* Section Ready */

                    if (asdu.Cot == CauseOfTransmission.FILE_TRANSFER)
                    {
                        if (_transferState == FileServerState.WAITING_FOR_SECTION_READY)
                        {

                            SectionReady sectionReady = (SectionReady)asdu.GetElement(0);

                            _currentSectionNumber = sectionReady.NameOfSection;
                            _currentSectionOffset = 0;
                            _currentSectionSize = sectionReady.LengthOfSection;

                            /* send call section */

                            ASDU callSection = new ASDU(_alParameters, CauseOfTransmission.FILE_TRANSFER, false, false, 0, _ca, false);

                            callSection.AddInformationObject(new FileCallOrSelect(_ioa, _nof, _currentSectionNumber, SelectAndCallQualifier.REQUEST_SECTION));

                            _connection.SendASDU(callSection);
                            _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                            _transferState = FileServerState.RECEIVE_SECTION;
                        }
                    }
                    else
                    {
                        asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                        _connection.SendASDU(asdu);
                    }

                    break;

                case TypeID.F_SG_NA_1: /* Segment */

                    if (asdu.Cot == CauseOfTransmission.FILE_TRANSFER)
                    {
                        if (_transferState == FileServerState.RECEIVE_SECTION)
                        {

                            FileSegment segment = (FileSegment)asdu.GetElement(0);

                            _logger("Received F_SG_NA_1(segment) (NoS=" + segment.NameOfSection + ", LoS=" + segment.LengthOfSegment + ")");

                            if (_fileReceiver != null)
                            {
                                _fileReceiver.SegmentReceived(segment.NameOfSection, _currentSectionOffset, segment.LengthOfSegment, segment.SegmentData);
                            }

                            // 与 FileClient 接收路径对称：累积段校验和，供 LAST SEGMENT 比对。
                            // 原实现从不算，下载文件损坏无从发现。
                            foreach (var octet in segment.SegmentData)
                            {
                                _sectionChecksum += octet;
                            }

                            _currentSectionOffset += segment.LengthOfSegment;
                        }
                        else
                        {
                            _logger("Unexpected F_SG_NA_1(file segment)");
                        }
                    }
                    else
                    {
                        asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                        _connection.SendASDU(asdu);
                    }

                    break;

                case TypeID.F_LS_NA_1: /* Last Segment/Section */

                    _logger("Received F_LS_NA_1 (last segment/section)");

                    if (asdu.Cot == CauseOfTransmission.FILE_TRANSFER)
                    {
                        if (_transferState == FileServerState.RECEIVE_SECTION)
                        {

                            FileLastSegmentOrSection lastSection = (FileLastSegmentOrSection)asdu.GetElement(0);

                            if (lastSection.LSQ == LastSectionOrSegmentQualifier.SECTION_TRANSFER_WITHOUT_DEACT)
                            {

                                // 校验段校验和：与 FileClient 接收路径对称，不符回否定确认
                                // 并让主站重发该段，而不是无条件 POS_ACK 收下损坏数据。
                                var checksumOk = lastSection.CHS == _sectionChecksum;
                                var ackQualifier = checksumOk
                                    ? AcknowledgeQualifier.POS_ACK_SECTION
                                    : AcknowledgeQualifier.NEG_ACK_SECTION;
                                var ackError = checksumOk ? FileError.DEFAULT : FileError.CHECKSUM_FAILED;

                                ASDU sectionAck = new ASDU(_alParameters, CauseOfTransmission.FILE_TRANSFER, false, false, 0, asdu.Ca, false);

                                sectionAck.AddInformationObject(new FileACK(_ioa, _nof, lastSection.NameOfSection, ackQualifier, ackError));

                                _connection.SendASDU(sectionAck);
                                _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                                _logger(checksumOk ? "Send section ACK" : "checksum failed - send section NACK");

                                _sectionChecksum = 0;
                                _transferState = FileServerState.WAITING_FOR_SECTION_READY;

                            }
                            else if (lastSection.LSQ == LastSectionOrSegmentQualifier.FILE_TRANSFER_WITH_DEACT)
                            {
                                /* master aborted transfer */

                                if (_fileReceiver != null)
                            {
                                _fileReceiver.Finished(FileErrorCode.ABORTED_BY_REMOTE);
                            }

                            _transferState = FileServerState.UNSELECTED_IDLE;
                            }
                            else
                            {

                            }


                        }
                        else if (_transferState == FileServerState.WAITING_FOR_SECTION_READY)
                        {

                            FileLastSegmentOrSection lastSection = (FileLastSegmentOrSection)asdu.GetElement(0);

                            if (lastSection.LSQ == LastSectionOrSegmentQualifier.FILE_TRANSFER_WITHOUT_DEACT)
                            {

                                ASDU fileAck = new ASDU(_alParameters, CauseOfTransmission.FILE_TRANSFER, false, false, 0, asdu.Ca, false);

                                fileAck.AddInformationObject(new FileACK(_ioa, _nof, lastSection.NameOfSection, AcknowledgeQualifier.POS_ACK_FILE, FileError.DEFAULT));

                                _connection.SendASDU(fileAck);
                                _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                                _logger("Send file ACK");

                                // 下载结束：通知接收者后立即释放引用（原实现保留引用直到下一次
                                // FILE_READY 或超时 —— receiver 可能持有 FileStream/大缓冲，
                                // 长跑下表现为句柄与内存不随传输结束释放），并复位分段状态。
                                var receiver = _fileReceiver;
                                _fileReceiver = null;

                                if (receiver != null)
                                {
                                    receiver.Finished(FileErrorCode.SUCCESS);
                                }

                                _transferState = FileServerState.UNSELECTED_IDLE;
                                ResetTransferStats();

                                _logger("Received file success");
                            }
                            else if (lastSection.LSQ == LastSectionOrSegmentQualifier.FILE_TRANSFER_WITH_DEACT)
                            {
                                /* master aborted transfer */

                                var receiver = _fileReceiver;
                                _fileReceiver = null;

                                if (receiver != null)
                                {
                                    receiver.Finished(FileErrorCode.ABORTED_BY_REMOTE);
                                }

                                _transferState = FileServerState.UNSELECTED_IDLE;
                                ResetTransferStats();
                            }
                            else
                            {
                                _logger("F_LS_NA_1 with unexpected LSQ: " + lastSection.LSQ.ToString());
                            }
                        }

                    }
                    else
                    {
                        asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                        _connection.SendASDU(asdu);
                    }

                    break;

                case TypeID.F_AF_NA_1: /*  124 - ACK file, ACK section */

                    _logger("Received file/section ACK F_AF_NA_1");

                    if (asdu.Cot == CauseOfTransmission.FILE_TRANSFER)
                    {

                        if (_transferState != FileServerState.UNSELECTED_IDLE && _selectedFile != null)
                        {

                            var file = _selectedFile.provider;

                            FileACK ack = (FileACK)asdu.GetElement(0);

                            if (ack.AckQualifier == AcknowledgeQualifier.POS_ACK_FILE)
                            {

                                _logger("Received positive file ACK");

                                if (_transferState == FileServerState.WAITING_FOR_FILE_ACK)
                                {

                                    _selectedFile.provider.TransferComplete(true);
                                    _selectedFile.selectedBy = null;

                                    _availableFiles.RemoveFile(_selectedFile.provider);

                                    _selectedFile = null;

                                    _transferState = FileServerState.UNSELECTED_IDLE;
                                    ResetTransferStats();
                                }
                                else
                                {
                                    _logger("Unexpected file transfer state --> abort file transfer");

                                    _transferState = FileServerState.SEND_ABORT;
                                }


                            }
                            else if (ack.AckQualifier == AcknowledgeQualifier.NEG_ACK_FILE)
                            {

                                _logger("Received negative file ACK - stop transfer");

                                if (_transferState == FileServerState.WAITING_FOR_FILE_ACK)
                                {

                                    _selectedFile.provider.TransferComplete(false);

                                    _selectedFile.selectedBy = null;
                                    _selectedFile = null;

                                    _transferState = FileServerState.UNSELECTED_IDLE;
                                    ResetTransferStats();
                                }
                                else
                                {
                                    _logger("Unexpected file transfer state --> abort file transfer");

                                    _transferState = FileServerState.SEND_ABORT;
                                }

                            }
                            else if (ack.AckQualifier == AcknowledgeQualifier.NEG_ACK_SECTION)
                            {

                                _logger("Received negative file section ACK - repeat section");

                                if (_transferState == FileServerState.WAITING_FOR_SECTION_ACK)
                                {
                                    _currentSectionOffset = 0;
                                    _sectionChecksum = 0;

                                    ASDU sectionReady = new ASDU(_alParameters, CauseOfTransmission.FILE_TRANSFER, false, false, 0, file.GetCA(), false);

                                    sectionReady.AddInformationObject(
                                        new SectionReady(_selectedFile.provider.GetIOA(), _selectedFile.provider.GetNameOfFile(), _currentSectionNumber, _currentSectionSize, false));

                                    _connection.SendASDU(sectionReady);


                                    _transferState = FileServerState.TRANSMIT_SECTION;
                                }
                                else
                                {
                                    _logger("Unexpected file transfer state --> abort file transfer");

                                    _transferState = FileServerState.SEND_ABORT;
                                }

                            }
                            else if (ack.AckQualifier == AcknowledgeQualifier.POS_ACK_SECTION)
                            {

                                if (_transferState == FileServerState.WAITING_FOR_SECTION_ACK)
                                {
                                    _currentSectionNumber++;

                                    var nextSectionSize =
                                        _selectedFile.provider.GetSectionSize(_currentSectionNumber - 1);

                                    _currentSectionOffset = 0;

                                    ASDU responseAsdu = new ASDU(_alParameters, CauseOfTransmission.FILE_TRANSFER, false, false, 0, file.GetCA(), false);

                                    if (nextSectionSize == -1)
                                    {
                                        _logger("Received positive file section ACK - send last section indication");

                                        responseAsdu.AddInformationObject(
                                            new FileLastSegmentOrSection(file.GetIOA(), file.GetNameOfFile(),
                                                _currentSectionNumber,
                                                LastSectionOrSegmentQualifier.FILE_TRANSFER_WITHOUT_DEACT,
                                                _fileChecksum));

                                        _transferState = FileServerState.WAITING_FOR_FILE_ACK;
                                    }
                                    else
                                    {
                                        _logger("Received positive file section ACK - send next section ready indication");

                                        _currentSectionSize = nextSectionSize;

                                        responseAsdu.AddInformationObject(
                                            new SectionReady(_selectedFile.provider.GetIOA(), _selectedFile.provider.GetNameOfFile(), _currentSectionNumber, _currentSectionSize, false));

                                        _transferState = FileServerState.WAITING_FOR_SECTION_CALL;
                                    }

                                    _connection.SendASDU(responseAsdu);

                                    _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                                    _sectionChecksum = 0;
                                }
                                else
                                {
                                    _logger("Unexpected file transfer state --> abort file transfer");

                                    _transferState = FileServerState.SEND_ABORT;
                                }
                            }
                        }
                        else
                        {
                            _logger("Unexpected File ACK message -> ignore");
                        }

                    }
                    else
                    {
                        asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                        _connection.SendASDU(asdu);
                    }
                    break;

                case TypeID.F_SC_NA_1: /* 122 - Call/Select directory/file/section */

                    _logger("Received call/select F_SC_NA_1");

                    if (asdu.Cot == CauseOfTransmission.FILE_TRANSFER)
                    {

                        FileCallOrSelect sc = (FileCallOrSelect)asdu.GetElement(0);


                        if (sc.SCQ == SelectAndCallQualifier.SELECT_FILE)
                        {

                            Interlocked.Increment(ref _selectFileReceived);
                            // 非空闲态又收到 SELECT：说明上一笔传输已被对端放弃
                            // （典型场景：主站收完最后一段就断线/重启，最终 ACK 没发出去，
                            // 从站停在 WAITING_FOR_FILE_ACK；新主站连上来第一件事就是 SELECT）。
                            // 合规主站不会在传输进行中再发 SELECT，所以这里必须"先收尾再服务"，
                            // 而不是静默忽略让新主站等到 3 秒超时 —— 那样表现为
                            // "重连后第一次文件传输偶发必失败"（长跑约 1/180 轮复现）。
                            if (_transferState != FileServerState.UNSELECTED_IDLE)
                            {
                                _logger("SELECT FILE while busy (" + _transferState + ") - abort previous transfer");

                                if (_selectedFile != null)
                                {
                                    var staleProvider = _selectedFile.provider;
                                    _selectedFile.selectedBy = null;
                                    _selectedFile = null;

                                    try
                                    {
                                        staleProvider.TransferComplete(false);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger("File provider TransferComplete threw: " + ex.Message);
                                    }
                                }

                                if (_fileReceiver != null)
                                {
                                    var staleReceiver = _fileReceiver;
                                    _fileReceiver = null;

                                    try
                                    {
                                        staleReceiver.Finished(FileErrorCode.ABORTED_BY_REMOTE);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger("File receiver Finished threw: " + ex.Message);
                                    }
                                }

                                ResetTransferStats();
                                _transferState = FileServerState.UNSELECTED_IDLE;
                            }

                            if (_transferState == FileServerState.UNSELECTED_IDLE)
                            {

                                _logger("Received SELECT FILE");

                                var file = _availableFiles.GetFile(asdu.Ca, sc.ObjectAddress, sc.NOF);

                                if (file == null)
                                {
                                    Interlocked.Increment(ref _unknownFileSelects);
                                    asdu.Cot = CauseOfTransmission.UNKNOWN_INFORMATION_OBJECT_ADDRESS;
                                    _connection.SendASDU(asdu);
                                }
                                else
                                {

                                    ASDU fileReady = new ASDU(_alParameters, CauseOfTransmission.FILE_TRANSFER, false, false, 0, asdu.Ca, false);

                                    /* check if already selected */
                                    if (file.selectedBy == null)
                                    {
                                        file.selectedBy = this;

                                        // 新传输从干净的分段/校验和状态开始。
                                        // 原实现只在超时路径清零 _fileChecksum，成功结束后残留，
                                        // 导致同一从站的第二次上传携带"上一文件 + 本文件"的校验和 → 必失败。
                                        ResetTransferStats();

                                        fileReady.AddInformationObject(new FileReady(sc.ObjectAddress, sc.NOF, file.provider.GetFileSize(), true));

                                        _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                                        _selectedFile = file;

                                        _transferState = FileServerState.WAITING_FOR_FILE_CALL;

                                    }
                                    else
                                    {
                                        fileReady.AddInformationObject(new FileReady(sc.ObjectAddress, sc.NOF, 0, false));

                                        _transferState = FileServerState.UNSELECTED_IDLE;
                                    }

                                    Interlocked.Increment(ref _fileReadySent);
                                    _connection.SendASDU(fileReady);


                                }

                            }
                            else
                            {
                                _logger("Unexpected SELECT FILE message");
                            }

                        }
                        else if (sc.SCQ == SelectAndCallQualifier.DEACTIVATE_FILE)
                        {

                            _logger("Received DEACTIVATE FILE");

                            // 主动复位，不区分方向：上传（_selectedFile）与下载（_fileReceiver）
                            // 都可能收到主站的 DEACTIVATE。原实现只认 _selectedFile，
                            // 下载方向会落入"Unexpected DEACTIVATE"分支 —— 状态与接收者都不清理，
                            // 只能等 3 秒超时，期间新请求全部被拒。
                            if (_transferState != FileServerState.UNSELECTED_IDLE
                                || _selectedFile != null
                                || _fileReceiver != null)
                            {
                                if (_selectedFile != null)
                                {
                                    var provider = _selectedFile.provider;
                                    _selectedFile.selectedBy = null;
                                    _selectedFile = null;

                                    try
                                    {
                                        provider.TransferComplete(false);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger("File provider TransferComplete threw: " + ex.Message);
                                    }
                                }

                                if (_fileReceiver != null)
                                {
                                    var receiver = _fileReceiver;
                                    _fileReceiver = null;

                                    try
                                    {
                                        receiver.Finished(FileErrorCode.ABORTED_BY_REMOTE);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger("File receiver Finished threw: " + ex.Message);
                                    }
                                }

                                _transferState = FileServerState.UNSELECTED_IDLE;
                                ResetTransferStats();
                            }
                            else
                            {
                                _logger("Unexpected DEACTIVATE FILE message");
                            }

                        }
                        else if (sc.SCQ == SelectAndCallQualifier.REQUEST_FILE)
                        {

                            _logger("Received CALL FILE");
                            Interlocked.Increment(ref _callFileReceived);

                            if (_transferState == FileServerState.WAITING_FOR_FILE_CALL)
                            {

                                if (_selectedFile.provider.GetIOA() != sc.ObjectAddress)
                                {
                                    _logger("Unkown IOA");
                                    asdu.IsNegative = true;
                                    asdu.Cot = CauseOfTransmission.UNKNOWN_INFORMATION_OBJECT_ADDRESS;
                                    _connection.SendASDU(asdu);
                                }
                                else
                                {

                                    ASDU sectionReady = new ASDU(_alParameters, CauseOfTransmission.FILE_TRANSFER, false, false, 0, asdu.Ca, false);

                                    _currentSectionNumber = 1;
                                    _currentSectionOffset = 0;
                                    // 首段的长度必须在 CALL FILE 时就填对（0 基索引 = 段号 - 1）。
                                    // 原实现只写了注释没有赋值，发出的 SECTION READY 长度恒为 0 或上一次传输的残留值。
                                    _currentSectionSize = _selectedFile.provider.GetSectionSize(_currentSectionNumber - 1);

                                    sectionReady.AddInformationObject(new SectionReady(sc.ObjectAddress, _selectedFile.provider.GetNameOfFile(), _currentSectionNumber, _currentSectionSize, false));

                                    _connection.SendASDU(sectionReady);

                                    _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                                    _logger("Send SECTION READY");

                                    _transferState = FileServerState.WAITING_FOR_SECTION_CALL;
                                }

                            }
                            else
                            {
                                _logger("Unexpected FILE CALL message, state=" + _transferState);
                                Interlocked.Increment(ref _unexpectedCallFile);
                            }


                        }
                        else if (sc.SCQ == SelectAndCallQualifier.REQUEST_SECTION)
                        {

                            _logger("Received CALL SECTION (NoS=" + sc.NameOfSection + ") current section: " + _currentSectionNumber);

                            if (_transferState == FileServerState.WAITING_FOR_SECTION_CALL)
                            {

                                if (_selectedFile.provider.GetIOA() != sc.ObjectAddress)
                                {
                                    _logger("Unkown IOA");
                                    asdu.IsNegative = true;
                                    asdu.Cot = CauseOfTransmission.UNKNOWN_INFORMATION_OBJECT_ADDRESS;
                                    _connection.SendASDU(asdu);
                                }
                                else
                                {
                                    if (asdu.IsNegative)
                                    {

                                        _currentSectionNumber++;
                                        _currentSectionOffset = 0;

                                        _currentSectionSize = _selectedFile.provider.GetSectionSize(_currentSectionNumber - 1);

                                        if (_currentSectionSize > 0)
                                        {

                                            /* send section ready with new section number */

                                            ASDU sectionReady = new ASDU(_alParameters, CauseOfTransmission.FILE_TRANSFER, false, false, 0, asdu.Ca, false);

                                            // 使用上面按 _currentSectionNumber - 1 算出的段长度；原实现写死索引 0 会使第 2 段起重发长度错误

                                            sectionReady.AddInformationObject(new SectionReady(sc.ObjectAddress, _selectedFile.provider.GetNameOfFile(), _currentSectionNumber, _currentSectionSize, false));

                                            _connection.SendASDU(sectionReady);

                                            _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                                            _logger("Send F_SR_NA_1 (section ready) (NoS = " + _currentSectionNumber + ")");

                                            _transferState = FileServerState.WAITING_FOR_SECTION_CALL;

                                        }
                                        else
                                        {
                                            /* send last section PDU */

                                            ASDU lastSection = new ASDU(_alParameters, CauseOfTransmission.FILE_TRANSFER, false, false, 0, asdu.Ca, false);


                                            lastSection.AddInformationObject(
                                                   new FileLastSegmentOrSection(_selectedFile.provider.GetIOA(), _selectedFile.provider.GetNameOfFile(),
                                                    _currentSectionNumber,
                                                    LastSectionOrSegmentQualifier.FILE_TRANSFER_WITHOUT_DEACT,
                                                    _fileChecksum));

                                            _connection.SendASDU(lastSection);

                                            _logger("Send F_LS_NA_1 (last section))");

                                            _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                                            _transferState = FileServerState.WAITING_FOR_FILE_ACK;
                                        }

                                    }
                                    else
                                    {
                                        _currentSectionSize = _selectedFile.provider.GetSectionSize(sc.NameOfSection - 1);

                                        if (_currentSectionSize > 0)
                                        {
                                            _currentSectionNumber = sc.NameOfSection;
                                            _currentSectionOffset = 0;

                                            _transferState = FileServerState.TRANSMIT_SECTION;
                                        }
                                        else
                                        {
                                            _logger("Unexpected number of section");
                                            _logger("Send negative confirm");
                                            asdu.IsNegative = true;

                                            _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                                            _connection.SendASDU(asdu);
                                        }
                                    }


                                }
                            }
                            else
                            {
                                _logger("Unexpected SECTION CALL message");
                            }
                        }

                    }
                    else if (asdu.Cot == CauseOfTransmission.REQUEST)
                    {
                        _logger("Call directory received");

                        _availableFiles.SendDirectoy(_connection, false);

                    }
                    else
                    {
                        asdu.IsNegative = true;
                        asdu.Cot = CauseOfTransmission.UNKNOWN_CAUSE_OF_TRANSMISSION;
                        _connection.SendASDU(asdu);
                    }
                    break;

                default:
                    handled = false;
                    break;
            }

            return handled;
        }

        public void HandleFileTransmission()
        {
            // 上传（从站→主站，_selectedFile 驱动）与下载（主站→从站，_fileReceiver 驱动）
            // 是两条对称路径。原实现的门槛只认 _selectedFile，把下载状态
            // WAITING_FOR_SECTION_READY / RECEIVE_SECTION 完全排除在超时处理之外；
            // 超时时也不通知 _fileReceiver、不清状态 —— 主站中途停发即永久卡在
            // RECEIVE_SECTION（SetFileReadyHandler 转发修好后这条路径才可用，
            // 但只接通了「启动/收段」这一半）。
            // SEND_ABORT 原先是死状态：4 处赋值却没有处理分支，DEACTIVATE_FILE
            // 从不发出，唯一出口是 3 秒超时。与 FileClient.AbortFileTransfer 对称，
            // 从站侧也必须主动发 DEACTIVATE_FILE 并复位本端状态。
            // 必须在 inUpload/inDownload 门槛【之前】处理：中止时 _selectedFile 与
            // _fileReceiver 都可能已经为空（例如收到否定文件确认后），否则又会漏掉。
            if (_transferState == FileServerState.SEND_ABORT)
            {
                SendDeactivateAndReset();
                return;
            }

            var inUpload = _selectedFile != null;
            var inDownload = _fileReceiver != null
                && (_transferState == FileServerState.WAITING_FOR_SECTION_READY
                    || _transferState == FileServerState.RECEIVE_SECTION);

            if (_transferState != FileServerState.UNSELECTED_IDLE && (inUpload || inDownload))
            {
                if (_transferState == FileServerState.TRANSMIT_SECTION && inUpload)
                {

                    if (_selectedFile != null)
                    {

                        var file = _selectedFile.provider;

                        ASDU fileAsdu = new ASDU(_alParameters, CauseOfTransmission.FILE_TRANSFER, false, false, 0, file.GetCA(), false);


                        if (_currentSectionOffset == _currentSectionSize)
                        {

                            /* send last segment */

                            fileAsdu.AddInformationObject(
                                new FileLastSegmentOrSection(file.GetIOA(), file.GetNameOfFile(),
                                    _currentSectionNumber,
                                    LastSectionOrSegmentQualifier.SECTION_TRANSFER_WITHOUT_DEACT,
                                    _sectionChecksum));

                            _fileChecksum += _sectionChecksum;
                            _sectionChecksum = 0;


                            _logger("Send LAST SEGMENT (NoS=" + _currentSectionNumber + ")");

                            _connection.SendASDU(fileAsdu);

                            _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                            _transferState = FileServerState.WAITING_FOR_SECTION_ACK;

                        }
                        else
                        {

                            var currentSegmentSize = _currentSectionSize - _currentSectionOffset;

                            if (currentSegmentSize > _maxSegmentSize)
                        {
                            currentSegmentSize = _maxSegmentSize;
                        }

                        var segmentData = new byte[currentSegmentSize];

                            file.GetSegmentData(_currentSectionNumber - 1,
                                _currentSectionOffset,
                                currentSegmentSize,
                                segmentData);

                            fileAsdu.AddInformationObject(
                                new FileSegment(file.GetIOA(), file.GetNameOfFile(), _currentSectionNumber,
                                    segmentData));

                            byte checksum = 0;

                            foreach (var octet in segmentData)
                            {
                                checksum += octet;
                            }

                            _connection.SendASDU(fileAsdu);

                            _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                            _sectionChecksum += checksum;

                            _logger("Send SEGMENT (NoS=" + _currentSectionNumber + ", CHS=" + _sectionChecksum + ")");
                            _currentSectionOffset += currentSegmentSize;

                        }
                    }
                }

                /* check for timeout */
                if (System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > _lastSentTime + _timeout)
                {
                    _logger("Abort file transfer due to timeout");

                    if (_selectedFile != null)
                    {
                        _selectedFile.selectedBy = null;
                        _selectedFile = null;
                    }

                    // 下载方向对称：必须通知接收者超时并释放，否则主站中途停发后
                    // _fileReceiver 永远悬挂、状态永久停在 RECEIVE_SECTION。
                    if (_fileReceiver != null)
                    {
                        _fileReceiver.Finished(FileErrorCode.TIMEOUT);
                        _fileReceiver = null;
                    }

                    _sectionChecksum = 0;
                    _fileChecksum = 0;
                    _transferState = FileServerState.UNSELECTED_IDLE;
                }

            }
        }

        /// <summary>
        /// 中止：主动发 DEACTIVATE_FILE（与 FileClient.AbortFileTransfer 对称），
        /// 并复位上传/下载两侧的全部状态。
        /// </summary>
        private void SendDeactivateAndReset()
        {
            _logger("Abort file transfer - send DEACTIVATE_FILE");

            try
            {
                var ioa = _selectedFile != null ? _selectedFile.provider.GetIOA() : 0;
                var nof = _selectedFile != null
                    ? _selectedFile.provider.GetNameOfFile()
                    : NameOfFile.TRANSPARENT_FILE;
                var ca = _selectedFile != null ? _selectedFile.provider.GetCA() : 1;

                var deactivate = new ASDU(_alParameters, CauseOfTransmission.FILE_TRANSFER,
                    false, false, 0, ca, false);
                deactivate.AddInformationObject(new FileCallOrSelect(ioa, nof, 0,
                    SelectAndCallQualifier.DEACTIVATE_FILE));
                _connection.SendASDU(deactivate);
            }
            catch (Exception ex)
            {
                // 发送失败也要复位本地状态，不能让中止态悬住
                _logger("Abort file transfer - send failed: " + ex.Message);
            }

            if (_selectedFile != null)
            {
                _selectedFile.selectedBy = null;
                _selectedFile = null;
            }

            if (_fileReceiver != null)
            {
                _fileReceiver.Finished(FileErrorCode.ABORTED_BY_REMOTE);
                _fileReceiver = null;
            }

            _sectionChecksum = 0;
            _fileChecksum = 0;
            _transferState = FileServerState.UNSELECTED_IDLE;
        }
    }
