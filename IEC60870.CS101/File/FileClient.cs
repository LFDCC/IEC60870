//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

/*
 *  CS101 主站侧文件传输客户端（上传=监控方向读取 / 下载=控制方向发送），
 *  状态机驱动，由 Iec101Client 在收到文件相关 ASDU 时喂入
 *  （自 FileServices.cs 拆分）。
 */

using System;
using IEC60870.Core;

namespace IEC60870.CS101;

    public enum FileClientState
    {
        IDLE,

        /* states for file upload (monitor direction) */

        WAITING_FOR_FILE_READY,

        WAITING_FOR_SECTION_READY, /* or for LAST_SECTION */

        RECEIVING_SECTION, /* waiting for SEGMENT or LAST SEGMENT */

        /* states for file download (control direction) */

        WAITING_FOR_REQUEST_FILE,

        SECTION_READY,

        SEND_SECTION,

        WAITING_FOR_SECTION_ACK,

        WAITING_FOR_FILE_ACK

    }

    public class FileClient
    {
        private FileClientState _state = FileClientState.IDLE;
        private Iec101Client _master;

        private int _ca;
        private int _ioa;
        private int _numberOfSection;
        private NameOfFile _nof;
        private IFileReceiver _fileReceiver = null;
        private IFileProvider _fileProvider = null;

        private DebugLogger _debugLog;

        private int _maxSegmentSize = 0;

        private int _currentSectionSize = 0;
        private int _currentSectionOffset = 0;
        private byte _sectionChecksum = 0;
        private byte _fileChecksum = 0;

        private long _timeout = 3000;
        private long _lastSentTime = 0;

        public FileClient(Iec101Client master, DebugLogger debugLog)
        {
            _master = master;
            _debugLog = debugLog;
            _maxSegmentSize = FileSegment.GetMaxDataSize(master.GetApplicationLayerParameters());
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

        private ASDU NewAsdu(InformationObject io)
        {
            ASDU asdu = new ASDU(_master.GetApplicationLayerParameters(), CauseOfTransmission.FILE_TRANSFER, false, false, 0, _ca, false);

            asdu.AddInformationObject(io);

            return asdu;
        }

        private void SendLastSegment()
        {
            var fileAsdu = NewAsdu(new FileLastSegmentOrSection(_ioa, _nof, (byte)_numberOfSection,
                    LastSectionOrSegmentQualifier.SECTION_TRANSFER_WITHOUT_DEACT,
                    _sectionChecksum));

            _fileChecksum += _sectionChecksum;
            _sectionChecksum = 0;

            _debugLog("Send LAST SEGMENT (NoS=" + _numberOfSection + ")");

            _master.SendASDU(fileAsdu);
        }

        private byte CalculateChecksum(byte[] data)
        {
            byte checksum = 0;

            foreach (var octet in data)
            {
                checksum += octet;
            }

            return checksum;
        }

        private bool SendSegment()
        {
            var currentSegmentSize = _currentSectionSize - _currentSectionOffset;

            if (currentSegmentSize > 0)
            {
                if (currentSegmentSize > _maxSegmentSize)
            {
                currentSegmentSize = _maxSegmentSize;
            }

            var segmentData = new byte[currentSegmentSize];

                _fileProvider.GetSegmentData(_numberOfSection - 1,
                    _currentSectionOffset,
                    currentSegmentSize,
                    segmentData);

                var fileAsdu = NewAsdu(new FileSegment(_ioa, _nof, (byte)_numberOfSection, segmentData));

                _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                _master.SendASDU(fileAsdu);

                _sectionChecksum += CalculateChecksum(segmentData);

                _debugLog("Send SEGMENT (NoS=" + _numberOfSection + ", CHS=" + _sectionChecksum + ")");
                _currentSectionOffset += currentSegmentSize;

                return true;
            }
            else
        {
            return false;
        }
    }

        private void ResetStateToIdle()
        {
            _fileReceiver = null;
            _fileProvider = null;
            _fileChecksum = 0;
            _state = FileClientState.IDLE;
        }

        private void AbortFileTransfer(FileErrorCode errorCode)
        {
            var deactivateFile = NewAsdu(new FileCallOrSelect(_ioa, _nof, 0, SelectAndCallQualifier.DEACTIVATE_FILE));

            _master.SendASDU(deactivateFile);

            _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (_fileReceiver != null)
        {
            _fileReceiver.Finished(errorCode);
        }

        ResetStateToIdle();
        }

        private void FileUploadFailed()
        {
            if (_fileProvider != null)
        {
            _fileProvider.TransferComplete(false);
        }

        ResetStateToIdle();
        }

        public bool HandleFileAsdu(ASDU asdu)
        {
            var asduHandled = true;

            switch (asdu.TypeId)
            {

                case TypeID.F_SC_NA_1: /* File/Section/Directory Call/Select */

                    _debugLog("Received F_SC_NA_1 (select/call)");

                    if (_state == FileClientState.WAITING_FOR_FILE_READY) /* file download */
                    {
                        var errCode = FileErrorCode.PROTOCOL_ERROR;

                        if (asdu.Cot == CauseOfTransmission.UNKNOWN_TYPE_ID)
                    {
                        errCode = FileErrorCode.UNKNOWN_SERVICE;
                    }
                    else if (asdu.Cot == CauseOfTransmission.UNKNOWN_COMMON_ADDRESS_OF_ASDU)
                    {
                        errCode = FileErrorCode.UNKNOWN_CA;
                    }
                    else if (asdu.Cot == CauseOfTransmission.UNKNOWN_INFORMATION_OBJECT_ADDRESS)
                    {
                        errCode = FileErrorCode.UNKNOWN_IOA;
                    }

                    if (_fileReceiver != null)
                    {
                        _fileReceiver.Finished(errCode);
                    }

                    ResetStateToIdle();
                    }
                    else if (_state == FileClientState.WAITING_FOR_REQUEST_FILE) /* file upload */
                    {
                        if ((asdu.Ca == _ca))
                        {

                            _numberOfSection = 1;
                            _currentSectionSize = _fileProvider.GetSectionSize(0);

                            var sectionReady = NewAsdu(new SectionReady(_ioa, _nof, 1, _currentSectionSize, false));
                            _master.SendASDU(sectionReady);

                            _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                            _state = FileClientState.SECTION_READY;
                        }
                        else
                        {
                            _fileProvider.TransferComplete(false);
                            ResetStateToIdle();
                        }
                    }
                    else if (_state == FileClientState.SECTION_READY)
                    {

                        if ((asdu.Ca == _ca))
                        {

                            /* send first segment */

                            _currentSectionOffset = 0;

                            SendSegment();

                            _state = FileClientState.SEND_SECTION;
                        }

                    }
                    else
                    {
                        if (_fileReceiver != null)
                    {
                        _fileReceiver.Finished(FileErrorCode.PROTOCOL_ERROR);
                    }

                    ResetStateToIdle();
                    }

                    break;

                case TypeID.F_FR_NA_1: /* File ready */

                    _debugLog("Received F_FR_NA_1 (file ready)");

                    if (_state == FileClientState.WAITING_FOR_FILE_READY)
                    {

                        FileReady fileReady = (FileReady)asdu.GetElement(0);

                        if ((asdu.Ca == _ca) && (fileReady.ObjectAddress == _ioa) && (fileReady.NOF == _nof))
                        {

                            if (fileReady.Positive)
                            {

                                /* send call file */

                                var callFile = NewAsdu(new FileCallOrSelect(_ioa, _nof, 0, SelectAndCallQualifier.REQUEST_FILE));
                                _master.SendASDU(callFile);

                                _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                                _debugLog("Send CALL FILE");

                                _state = FileClientState.WAITING_FOR_SECTION_READY;

                            }
                            else
                            {
                                if (_fileReceiver != null)
                            {
                                _fileReceiver.Finished(FileErrorCode.FILE_NOT_READY);
                            }

                            ResetStateToIdle();
                            }

                        }
                        else
                        {
                            _debugLog("Unexpected CA, IOA, or NOF");

                            if (_fileReceiver != null)
                        {
                            _fileReceiver.Finished(FileErrorCode.PROTOCOL_ERROR);
                        }

                        ResetStateToIdle();
                        }


                    }
                    else if (_state == FileClientState.IDLE)
                    {

                        _state = FileClientState.WAITING_FOR_SECTION_READY;

                    }
                    else if (_state == FileClientState.WAITING_FOR_REQUEST_FILE)
                    {

                        if (asdu.IsNegative)
                        {
                            _debugLog("Slave rejected file download: " + asdu.Cot.ToString());
                        }
                        else
                        {
                            _debugLog("Unexpected file ready while trying to start file download");
                        }

                        if (_fileProvider != null)
                    {
                        _fileProvider.TransferComplete(false);
                    }
                }
                    else
                    {
                        AbortFileTransfer(FileErrorCode.PROTOCOL_ERROR);
                    }

                    break;

                case TypeID.F_SR_NA_1: /* Section ready */

                    _debugLog("Received F_SR_NA_1 (section ready)");

                    if (_state == FileClientState.WAITING_FOR_SECTION_READY)
                    {

                        SectionReady sc = (SectionReady)asdu.GetElement(0);

                        if (sc.NotReady == false)
                        {
                            _debugLog("Received SECTION READY(NoF=" + sc.NOF + ", NoS=" + sc.NameOfSection + ")");

                            var callSection = NewAsdu(new FileCallOrSelect(_ioa, _nof, sc.NameOfSection, SelectAndCallQualifier.REQUEST_SECTION));
                            _master.SendASDU(callSection);

                            _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                            _debugLog("Send CALL SECTION(NoF=" + sc.NOF + ", NoS=" + sc.NameOfSection + ")");

                            _currentSectionOffset = 0;
                            _sectionChecksum = 0;
                            _state = FileClientState.RECEIVING_SECTION;

                        }
                        else
                        {
                            AbortFileTransfer(FileErrorCode.SECTION_NOT_READY);
                        }

                    }
                    else if (_state == FileClientState.IDLE)
                    {
                    }
                    else
                    {
                        if (_fileReceiver != null)
                    {
                        _fileReceiver.Finished(FileErrorCode.PROTOCOL_ERROR);
                    }

                    ResetStateToIdle();
                    }

                    break;

                case TypeID.F_SG_NA_1: /* Segment */

                    _debugLog("Received F_SG_NA_1 (segment)");

                    if (_state == FileClientState.RECEIVING_SECTION)
                    {

                        FileSegment segment = (FileSegment)asdu.GetElement(0);

                        _debugLog("Received segment (NoS=" + segment.NameOfSection + ", LoS=" + segment.LengthOfSegment + ")");

                        _sectionChecksum += CalculateChecksum(segment.SegmentData);

                        if (_fileReceiver != null)
                        {
                            _fileReceiver.SegmentReceived(segment.NameOfSection, _currentSectionOffset, segment.LengthOfSegment, segment.SegmentData);
                        }

                        _currentSectionOffset += segment.LengthOfSegment;

                    }
                    else if (_state == FileClientState.IDLE)
                    {
                    }
                    else
                    {
                        AbortFileTransfer(FileErrorCode.PROTOCOL_ERROR);
                    }

                    break;


                case TypeID.F_LS_NA_1: /* Last segment or section */

                    _debugLog("Received F_LS_NA_1 (last segment/section)");

                    if (_state != FileClientState.IDLE)
                    {

                        FileLastSegmentOrSection lastSection = (FileLastSegmentOrSection)asdu.GetElement(0);

                        if (lastSection.LSQ == LastSectionOrSegmentQualifier.SECTION_TRANSFER_WITHOUT_DEACT)
                        {

                            if (_state == FileClientState.RECEIVING_SECTION)
                            {

                                ASDU segmentAck;

                                if (lastSection.CHS == _sectionChecksum)
                                {
                                    segmentAck = NewAsdu(new FileACK(_ioa, _nof, lastSection.NameOfSection, AcknowledgeQualifier.POS_ACK_SECTION, FileError.DEFAULT));
                                    _debugLog("Send SEGMENT ACK");
                                }
                                else
                                {
                                    segmentAck = NewAsdu(new FileACK(_ioa, _nof, lastSection.NameOfSection, AcknowledgeQualifier.NEG_ACK_SECTION, FileError.CHECKSUM_FAILED));
                                    _debugLog("checksum check failed! Send SEGMENT NACK");
                                }

                                _master.SendASDU(segmentAck);

                                _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                                _state = FileClientState.WAITING_FOR_SECTION_READY;
                            }
                            else
                            {
                                AbortFileTransfer(FileErrorCode.PROTOCOL_ERROR);
                            }
                        }
                        else if (lastSection.LSQ == LastSectionOrSegmentQualifier.FILE_TRANSFER_WITH_DEACT)
                        {
                            /* slave aborted transfer */

                            if (_fileReceiver != null)
                        {
                            _fileReceiver.Finished(FileErrorCode.ABORTED_BY_REMOTE);
                        }

                        ResetStateToIdle();
                        }
                        else if (lastSection.LSQ == LastSectionOrSegmentQualifier.FILE_TRANSFER_WITHOUT_DEACT)
                        {

                            if (_state == FileClientState.WAITING_FOR_SECTION_READY)
                            {
                                var fileAck = NewAsdu(new FileACK(_ioa, _nof, lastSection.NameOfSection, AcknowledgeQualifier.POS_ACK_FILE, FileError.DEFAULT));

                                _master.SendASDU(fileAck);

                                _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                                _debugLog("Send FILE ACK");

                                if (_fileReceiver != null)
                            {
                                _fileReceiver.Finished(FileErrorCode.SUCCESS);
                            }

                            ResetStateToIdle();
                            }
                            else
                            {

                                _debugLog("Illegal state: " + _state.ToString());

                                AbortFileTransfer(FileErrorCode.PROTOCOL_ERROR);
                            }
                        }
                    }

                    break;

                case TypeID.F_AF_NA_1: /* Section or File ACK */

                    _debugLog("Received F_AF_NA_1 (section/file ACK)");

                    FileACK ack = (FileACK)asdu.GetElement(0);

                    if (_state == FileClientState.WAITING_FOR_SECTION_ACK)
                    {
                        if ((asdu.Ca == _ca) && (asdu.Cot == CauseOfTransmission.FILE_TRANSFER))
                        {

                            if (ack.AckQualifier == AcknowledgeQualifier.POS_ACK_SECTION)
                            {

                                _numberOfSection++;

                                var nextSectionSize = _fileProvider.GetSectionSize(_numberOfSection - 1);

                                if (nextSectionSize > 0)
                                {
                                    _currentSectionSize = nextSectionSize;
                                    _currentSectionOffset = 0;

                                    var sectionReady = NewAsdu(new SectionReady(_ioa, _nof, (byte)_numberOfSection, _currentSectionSize, false));
                                    _master.SendASDU(sectionReady);

                                    _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                                    _state = FileClientState.SECTION_READY;
                                }
                                else
                                {

                                    var lastSection = NewAsdu(new FileLastSegmentOrSection(_ioa, _nof, (byte)_numberOfSection, LastSectionOrSegmentQualifier.FILE_TRANSFER_WITHOUT_DEACT, _fileChecksum));
                                    _master.SendASDU(lastSection);

                                    _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                                    _state = FileClientState.WAITING_FOR_FILE_ACK;
                                }

                            }
                            else
                            {
                                FileUploadFailed();
                            }

                        }
                        else
                        {
                            FileUploadFailed();
                        }
                    }
                    else if (_state == FileClientState.WAITING_FOR_FILE_ACK)
                    {
                        if ((asdu.Ca == _ca) && (asdu.Cot == CauseOfTransmission.FILE_TRANSFER))
                        {

                            if (ack.AckQualifier == AcknowledgeQualifier.POS_ACK_FILE)
                            {
                                if (_fileProvider != null)
                            {
                                _fileProvider.TransferComplete(true);
                            }

                            ResetStateToIdle();

                            }
                            else
                            {
                                FileUploadFailed();
                            }

                        }
                        else
                        {
                            FileUploadFailed();
                        }
                    }

                    break;

                default:

                    asduHandled = false;
                    break;
            }


            return asduHandled;
        }

        public void HandleFileService()
        {

            if (_state == FileClientState.SEND_SECTION)
            {
                if (SendSegment() == false)
                {
                    SendLastSegment();
                    _state = FileClientState.WAITING_FOR_SECTION_ACK;
                }
            }

            /* Check for timeout */
            if (_state != FileClientState.IDLE)
            {
                if (System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > _lastSentTime + _timeout)
                {
                    _debugLog("Abort file transfer due to timeout");

                    if (_fileProvider != null)
                {
                    _fileProvider.TransferComplete(false);
                }

                if (_fileReceiver != null)
                {
                    _fileReceiver.Finished(FileErrorCode.TIMEOUT);
                }

                ResetStateToIdle();
                }
            }

        }

        public void RequestFile(int ca, int ioa, NameOfFile nof, IFileReceiver fileReceiver)
        {
            _ca = ca;
            _ioa = ioa;
            _nof = nof;
            _fileReceiver = fileReceiver;

            var selectFile = NewAsdu(new FileCallOrSelect(ioa, nof, 0, SelectAndCallQualifier.SELECT_FILE));

            _master.SendASDU(selectFile);

            _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            _state = FileClientState.WAITING_FOR_FILE_READY;
        }

        public void SendFile(int ca, int ioa, NameOfFile nof, IFileProvider fileProvider)
        {
            _ca = ca;
            _ioa = ioa;
            _nof = nof;
            _fileProvider = fileProvider;

            var fileReady = NewAsdu(new FileReady(ioa, nof, fileProvider.GetFileSize(), true));

            _master.SendASDU(fileReady);

            _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            _state = FileClientState.WAITING_FOR_REQUEST_FILE;
        }
    }
