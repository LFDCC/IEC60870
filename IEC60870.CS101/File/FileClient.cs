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

        /// <summary>当前正在请求的段号（来自从站的 SECTION READY）。</summary>
        private byte _currentSectionNumber;

        /// <summary>当前请求已重发次数；每次成功推进状态后清零。</summary>
        private int _retries;

        /// <summary>单个请求的最大重发次数（101 现场串口丢帧是常态，必须可重传）。</summary>
        private int _maxRetries = 2;

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

        /// <summary>
        /// 超时前重发当前请求（SELECT / CALL FILE / CALL SECTION / FILE READY）。
        /// </summary>
        /// <remarks>
        /// 101 在串口上丢帧是常态：链路层的 ACK 只保证"链路帧到达"，不保证应用
        /// 请求得到应答。原来一次丢帧就整笔文件传输失败（现场表现为"偶发文件取不到"）；
        /// 这里做有界重发，超过次数才真正判失败并通知应用层。
        /// 重发对从站是幂等的：忙状态收到 SELECT 会先收尾旧传输再服务新请求。
        /// </remarks>
        private bool TryRetryRequest()
        {
            if (_retries >= _maxRetries)
            {
                return false;
            }

            ASDU request;

            switch (_state)
            {
                case FileClientState.WAITING_FOR_FILE_READY:
                    request = NewAsdu(new FileCallOrSelect(_ioa, _nof, 0, SelectAndCallQualifier.SELECT_FILE));
                    break;

                case FileClientState.WAITING_FOR_SECTION_READY:
                    request = NewAsdu(new FileCallOrSelect(_ioa, _nof, 0, SelectAndCallQualifier.REQUEST_FILE));
                    break;

                case FileClientState.RECEIVING_SECTION:
                    // 已发出 CALL SECTION 但没等到段：重发一次请求
                    request = NewAsdu(new FileCallOrSelect(_ioa, _nof, _currentSectionNumber, SelectAndCallQualifier.REQUEST_SECTION));
                    break;

                case FileClientState.WAITING_FOR_REQUEST_FILE:
                    request = NewAsdu(new FileReady(_ioa, _nof,
                        _fileProvider?.GetFileSize() ?? 0, true));
                    break;

                default:
                    return false;
            }

            _retries++;

            try
            {
                _master.SendASDU(request);
                Trace($"Retry #{_retries} in {_state}");
                return true;
            }
            catch (Exception ex)
            {
                _debugLog("Retry send failed: " + ex.Message);
                return false;
            }
        }

        private void ResetStateToIdle()
        {
            Trace($"ResetToIdle from {_state} (caller={new System.Diagnostics.StackTrace(1, false).GetFrame(0)?.GetMethod()?.Name})");
            _retries = 0;
            _fileReceiver = null;
            _fileProvider = null;
            _fileChecksum = 0;
            _state = FileClientState.IDLE;
        }

        // 排障计数（长跑失败信息里带上）
        private long _fileReadyReceived;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _diagTrace =
            new System.Collections.Concurrent.ConcurrentQueue<string>();

        private void Trace(string message)
        {
            _diagTrace.Enqueue($"{DateTime.Now:HH:mm:ss.fff} {message}");

            while (_diagTrace.Count > 8 && _diagTrace.TryDequeue(out _))
            {
            }
        }

        internal string Diagnostics =>
            $"state={_state}, frReceived={Interlocked.Read(ref _fileReadyReceived)}, "
            + $"receiver={_fileReceiver != null}, provider={_fileProvider != null}, "
            + "trace=[" + string.Join(" | ", _diagTrace) + "]";

        /// <summary>
        /// 结束一次传输：**先复位内部状态，再通知应用层**。
        /// </summary>
        /// <remarks>
        /// 顺序不能反。旧实现在 Success 分支里先调 <c>Finished(SUCCESS)</c>、后调
        /// <c>ResetStateToIdle()</c>；而应用层在 <c>Finished</c> 回调里通常会立刻
        /// 发起下一笔传输（连续取文件是常规用法）。调用线程随即执行的
        /// <c>ResetStateToIdle()</c> 会把**新**传输的 state/receiver 清掉 ——
        /// 表现为"同一链路连续传输，第 N 次必然超时且收不到任何字节"。
        /// </remarks>
        private void FinishReceiver(FileErrorCode code)
        {
            var receiver = _fileReceiver;

            ResetStateToIdle();

            receiver?.Finished(code);
        }

        private void AbortFileTransfer(FileErrorCode errorCode)
        {
            var receiver = _fileReceiver;
            var provider = _fileProvider;

            var deactivateFile = NewAsdu(new FileCallOrSelect(_ioa, _nof, 0, SelectAndCallQualifier.DEACTIVATE_FILE));

            try
            {
                _master.SendASDU(deactivateFile);
                _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            catch (Exception ex)
            {
                // 发送失败（如链路层背压抛 LinkLayerBusyException）也必须走完清理，
                // 否则 Finished/TransferComplete/ResetStateToIdle 全被跳过，只剩超时兜底。
                _debugLog("Send DEACTIVATE failed: " + ex.Message);
            }

            // 失败清理必须与 HandleFileService 的超时块对称：接收者与提供者都要通知。
            // 原实现只通知 _fileReceiver，随后 ResetStateToIdle 把 _fileProvider 置 null，
            // 上传方永远拿不到 TransferComplete(false)。
            ResetStateToIdle();

            receiver?.Finished(errorCode);
            provider?.TransferComplete(false);
        }

        private void FileUploadFailed()
        {
            var provider = _fileProvider;

            ResetStateToIdle();

            provider?.TransferComplete(false);
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
                        FinishReceiver(FileErrorCode.PROTOCOL_ERROR);
                    }

                    break;

                case TypeID.F_FR_NA_1: /* File ready */

                    Interlocked.Increment(ref _fileReadyReceived);
                    Trace($"FR received, state={_state}, ca={asdu.Ca}, neg={asdu.IsNegative}");
                    _debugLog("Received F_FR_NA_1 (file ready)");

                    if (_state == FileClientState.WAITING_FOR_FILE_READY)
                    {

                        FileReady fileReady = (FileReady)asdu.GetElement(0);

                        if ((asdu.Ca == _ca) && (fileReady.ObjectAddress == _ioa) && (fileReady.NOF == _nof))
                        {

                            if (fileReady.Positive)
                            {

                                /* send call file */

                                _retries = 0;   // 状态推进：重试计数清零

                                var callFile = NewAsdu(new FileCallOrSelect(_ioa, _nof, 0, SelectAndCallQualifier.REQUEST_FILE));
                                _master.SendASDU(callFile);

                                _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                                _debugLog("Send CALL FILE");

                                _state = FileClientState.WAITING_FOR_SECTION_READY;

                            }
                            else
                            {
                                FinishReceiver(FileErrorCode.FILE_NOT_READY);
                            }

                        }
                        else
                        {
                            _debugLog("Unexpected CA, IOA, or NOF");

                            FinishReceiver(FileErrorCode.PROTOCOL_ERROR);
                        }


                    }
                    else if (_state == FileClientState.IDLE)
                    {
                        // 没有在等文件就收到 FILE_READY：这是从站的主动上送（或上一笔
                        // 传输的迟到应答）。绝不能只把状态改成 WAITING_FOR_SECTION_READY
                        // 而不发 CALL FILE —— 那样既不会推进传输，又会把随后
                        // RequestFile 设置的状态覆盖掉，主站只能空等超时。
                        // 正确做法是忽略；需要拉取时由应用显式 RequestFile。
                        _debugLog("Unexpected F_FR_NA_1 while idle - ignored");
                    }
                    else if (_state == FileClientState.WAITING_FOR_REQUEST_FILE)
                    {
                        // 与同一 switch 的所有兄弟失败分支对称：必须发 DEACTIVATE_FILE
                        // 并复位状态。原实现只调 TransferComplete(false) 就留在
                        // WAITING_FOR_REQUEST_FILE，稍后超时又对同一 provider 再调一次。
                        if (asdu.IsNegative)
                        {
                            _debugLog("Slave rejected file download: " + asdu.Cot.ToString());
                        }
                        else
                        {
                            _debugLog("Unexpected file ready while trying to start file download");
                        }

                        AbortFileTransfer(FileErrorCode.PROTOCOL_ERROR);
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

                            // 状态推进：重试计数清零，并记住段号（重发 CALL SECTION 时要用）
                            _retries = 0;
                            _currentSectionNumber = sc.NameOfSection;

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
                        FinishReceiver(FileErrorCode.PROTOCOL_ERROR);
                    }

                    break;

                case TypeID.F_SG_NA_1: /* Segment */

                    _debugLog("Received F_SG_NA_1 (segment)");

                    if (_state == FileClientState.RECEIVING_SECTION)
                    {

                        FileSegment segment = (FileSegment)asdu.GetElement(0);

                        _debugLog("Received segment (NoS=" + segment.NameOfSection + ", LoS=" + segment.LengthOfSegment + ")");

                        // 收到数据即视为请求已生效，重试计数清零
                        _retries = 0;

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

                            FinishReceiver(FileErrorCode.ABORTED_BY_REMOTE);
                        }
                        else if (lastSection.LSQ == LastSectionOrSegmentQualifier.FILE_TRANSFER_WITHOUT_DEACT)
                        {

                            if (_state == FileClientState.WAITING_FOR_SECTION_READY)
                            {
                                var fileAck = NewAsdu(new FileACK(_ioa, _nof, lastSection.NameOfSection, AcknowledgeQualifier.POS_ACK_FILE, FileError.DEFAULT));

                                _master.SendASDU(fileAck);

                                _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                                _debugLog("Send FILE ACK");

                                // 先复位内部状态再回调：回调里应用可能立刻发起下一笔传输
                                FinishReceiver(FileErrorCode.SUCCESS);
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
                try
                {
                    if (SendSegment() == false)
                    {
                        SendLastSegment();
                        _state = FileClientState.WAITING_FOR_SECTION_ACK;
                    }
                }
                catch (LinkLayerBusyException)
                {
                    // 链路层忙是暂时状态：保持 SEND_SECTION，下一轮重试，绝不把异常
                    // 抛给调用方（Iec101Client.RunLoopAsync 的兜底 catch 会退出整个循环）。
                    _debugLog("Link layer busy, retry segment next round");
                }
                catch (Exception ex)
                {
                    // 发送失败必须收敛状态：通知双方并回到 IDLE，否则文件状态永远
                    // 停在 SEND_SECTION 且后台循环已死，只能等超时。
                    _debugLog("Abort file transfer due to send failure: " + ex.Message);

                    var provider = _fileProvider;
                    var receiver = _fileReceiver;

                    ResetStateToIdle();

                    try
                    {
                        provider?.TransferComplete(false);
                    }
                    catch (Exception notifyEx)
                    {
                        _debugLog("TransferComplete(false) threw: " + notifyEx.Message);
                    }

                    try
                    {
                        receiver?.Finished(FileErrorCode.PROTOCOL_ERROR);
                    }
                    catch (Exception notifyEx)
                    {
                        _debugLog("Finished(PROTOCOL_ERROR) threw: " + notifyEx.Message);
                    }
                }
            }

            /* Check for timeout */
            if (_state != FileClientState.IDLE)
            {
                if (System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > _lastSentTime + _timeout)
                {
                    // 先尝试重发当前请求；重试次数用尽才真正判失败。
                    if (TryRetryRequest())
                    {
                        _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        return;
                    }

                    _debugLog("Abort file transfer due to timeout");

                    var provider = _fileProvider;
                    var receiver = _fileReceiver;

                    ResetStateToIdle();

                    try
                    {
                        provider?.TransferComplete(false);
                    }
                    catch (Exception notifyEx)
                    {
                        _debugLog("TransferComplete(false) threw: " + notifyEx.Message);
                    }

                    try
                    {
                        receiver?.Finished(FileErrorCode.TIMEOUT);
                    }
                    catch (Exception notifyEx)
                    {
                        _debugLog("Finished(TIMEOUT) threw: " + notifyEx.Message);
                    }
                }
            }

        }

        public void RequestFile(int ca, int ioa, NameOfFile nof, IFileReceiver fileReceiver)
        {
            // 上一笔传输还没结束就发起新请求：原实现直接覆盖 _ca/_ioa/_fileReceiver，
            // 旧接收者既收不到 Finished 也不会释放（持有流/大缓冲的应用会泄漏），
            // 链路两端状态也会失去同步。这里先明确中止旧传输并通知其接收者。
            if (_state != FileClientState.IDLE)
            {
                _debugLog("RequestFile called while busy - abort previous transfer first");
                AbortFileTransfer(FileErrorCode.ABORTED_BY_REMOTE);
            }

            _ca = ca;
            _ioa = ioa;
            _nof = nof;
            _fileReceiver = fileReceiver;

            var selectFile = NewAsdu(new FileCallOrSelect(ioa, nof, 0, SelectAndCallQualifier.SELECT_FILE));

            // 必须先发布状态、再发帧。SendASDU 只是入队，链路线程可能在下一微秒就
            // 发出请求并收到从站的 FILE_READY 并回调 HandleFileAsdu；若此时 _state
            // 仍是上一轮的 IDLE，F_FR_NA_1 会走 "state == IDLE" 的错误分支
            // （只改状态、不发 CALL FILE），随后这里再把状态覆盖成
            // WAITING_FOR_FILE_READY —— 主站永远等不到第二个 FILE_READY，只能超时。
            // 负载高时窗口被放大，长跑约 1/400 轮复现。
            _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _state = FileClientState.WAITING_FOR_FILE_READY;

            _retries = 0;
            Trace($"RequestFile ioa={ioa}, state now={_state}");

            _master.SendASDU(selectFile);
        }

        public void SendFile(int ca, int ioa, NameOfFile nof, IFileProvider fileProvider)
        {
            if (_state != FileClientState.IDLE)
            {
                _debugLog("SendFile called while busy - abort previous transfer first");
                AbortFileTransfer(FileErrorCode.ABORTED_BY_REMOTE);
            }

            _ca = ca;
            _ioa = ioa;
            _nof = nof;
            _fileProvider = fileProvider;

            var fileReady = NewAsdu(new FileReady(ioa, nof, fileProvider.GetFileSize(), true));

            // 同上：先发布状态再发送，避免从站的应答先于状态赋值到达。
            _lastSentTime = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _state = FileClientState.WAITING_FOR_REQUEST_FILE;
            _retries = 0;

            _master.SendASDU(fileReady);
        }
    }
