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
    public class CS101n104File
    {

        public CS101n104File(IFileProvider file)
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

        private List<CS101n104File> _availableFiles = new List<CS101n104File>();

        internal CS101n104File GetFile(int ca, int ioa, NameOfFile nof)
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

                _availableFiles.Add(new CS101n104File(file));
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
            List<IFileProvider> files = new List<IFileProvider>();

            foreach (var file in _availableFiles)
            {
                files.Add(file.provider);
            }

            return files;
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

        private CS101n104File _selectedFile;

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

        public void SetFileReadyHandler(FileReadyHandler handler, object parameter)
        {
            _fileReadyHandler = handler;
            _fileReadyHandlerParameter = parameter;
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

                                ASDU sectionAck = new ASDU(_alParameters, CauseOfTransmission.FILE_TRANSFER, false, false, 0, asdu.Ca, false);

                                sectionAck.AddInformationObject(new FileACK(_ioa, _nof, lastSection.NameOfSection, AcknowledgeQualifier.POS_ACK_SECTION, FileError.DEFAULT));

                                _connection.SendASDU(sectionAck);
                                _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                                _logger("Send section ACK");

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

                                if (_fileReceiver != null)
                            {
                                _fileReceiver.Finished(FileErrorCode.SUCCESS);
                            }

                            _transferState = FileServerState.UNSELECTED_IDLE;

                                _logger("Received file success");
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

                        if (_transferState != FileServerState.UNSELECTED_IDLE)
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

                            if (_transferState == FileServerState.UNSELECTED_IDLE)
                            {

                                _logger("Received SELECT FILE");

                                var file = _availableFiles.GetFile(asdu.Ca, sc.ObjectAddress, sc.NOF);

                                if (file == null)
                                {
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

                            if (_transferState != FileServerState.UNSELECTED_IDLE)
                            {

                                if (_selectedFile != null)
                                {
                                    _selectedFile.selectedBy = null;
                                    _selectedFile = null;
                                }

                                _transferState = FileServerState.UNSELECTED_IDLE;

                            }
                            else
                            {
                                _logger("Unexpected DEACTIVATE FILE message");
                            }

                        }
                        else if (sc.SCQ == SelectAndCallQualifier.REQUEST_FILE)
                        {

                            _logger("Received CALL FILE");

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
                                    _currentSectionSize = _selectedFile.provider.GetSectionSize(0);

                                    sectionReady.AddInformationObject(new SectionReady(sc.ObjectAddress, _selectedFile.provider.GetNameOfFile(), _currentSectionNumber, _currentSectionSize, false));

                                    _connection.SendASDU(sectionReady);

                                    _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                                    _logger("Send SECTION READY");

                                    _transferState = FileServerState.WAITING_FOR_SECTION_CALL;
                                }

                            }
                            else
                            {
                                _logger("Unexpected FILE CALL message");
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

                                            _currentSectionSize = _selectedFile.provider.GetSectionSize(0);

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


            if (_transferState != FileServerState.UNSELECTED_IDLE)
            {

                if (_transferState == FileServerState.TRANSMIT_SECTION)
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

                    _transferState = FileServerState.UNSELECTED_IDLE;
                }

            }
        }
    }
