//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;


namespace IEC60870.CS101;

    internal class SecondaryLinkLayerBalanced : SecondaryLinkLayer
    {
        // 下一帧计数位（FCB）的期望值
        private bool _expectedFcb = true;

        private Action<string> _debugLog;
        private LinkLayerEngine _linkLayer;
        private Func<int, byte[], int, int, bool> _handleApplicationLayer;

        private int _linkLayerAddress = 0;

        public SecondaryLinkLayerBalanced(LinkLayerEngine linkLayer, int address,
                                    Func<int, byte[], int, int, bool> handleApplicationLayer, Action<string> debugLog)
        {
            _linkLayer = linkLayer;
            _linkLayerAddress = address;
            _debugLog = debugLog;
            _handleApplicationLayer = handleApplicationLayer;
        }


        public override int Address
        {
            get { return _linkLayerAddress; }
            set { _linkLayerAddress = value; }
        }

        private void SendStatusOfLink(int address)
        {
            _linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.STATUS_OF_LINK_OR_ACCESS_DEMAND, address, false, false);
        }

        /// <summary>因 FCB 校验失败而丢弃的用户数据帧数（排障用）。</summary>
        internal long FcbMismatchDrops;

        /// <summary>最近一次 FCB 丢弃的详细信息（排障用）。</summary>
        internal volatile string LastFcbDropInfo;

        private bool CheckFCB(bool fcb)
        {
            if (fcb != _expectedFcb)
            {
                _debugLog("ERROR: Frame count bit (FCB) invalid!");
                //TODO change link status
                return false;
            }
            else
            {
                _expectedFcb = !_expectedFcb;
                return true;
            }
        }

        public override void HandleMessage(FunctionCodePrimary fcp, bool isBroadcast, int address, bool fcb, bool fcv, byte[] msg, int userDataStart, int userDataLength)
        {
            // FCV=1 时校验帧计数位。校验失败说明这是对端重发的重复帧（或链路失步）：
            // 必须应答让对端停止重复，但不能再次交给应用层（否则会重复执行命令）。
            var fcbValid = true;

            if (fcv)
            {
                fcbValid = CheckFCB(fcb);

                if (!fcbValid)
                {
                    System.Threading.Interlocked.Increment(ref FcbMismatchDrops);
                    LastFcbDropInfo = $"fcb={fcb} expected={_expectedFcb} fcp={fcp} len={userDataLength}";
                    _debugLog("SLL - FCB check failed -> ignore user data (" + LastFcbDropInfo + ")");
                }
            }

            switch (fcp)
            {

                case FunctionCodePrimary.RESET_REMOTE_LINK:
                    _expectedFcb = true;
                    _debugLog("SLL - RECV RESET REMOTE LINK");

                    // 本类既是「从站应答方」也是「主动发数据方」（平衡模式双向）。
                    // 对端复位链路会把它的期望 FCB 重置为 1，本端发送侧的 _nextFcb 也必须
                    // 同步复位，否则本端随后主动发的第一帧会带着上一会话的 FCB，
                    // 被对端判为重复帧而静默丢弃（实测：反复重连后总召唤的 ACT_CON 稳定丢失）。
                    (_linkLayer.PrimaryLinkLayer as PrimaryLinkLayerBalanced)?.ResetSequenceState();

                    if (_linkLayer.linkLayerParameters.UseSingleCharACK)
                {
                    _linkLayer.SendSingleCharACK();
                }
                else
                {
                    _linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.ACK, _linkLayerAddress, false, false);
                }

                break;

                case FunctionCodePrimary.TEST_FUNCTION_FOR_LINK:
                    _debugLog("SLL -TEST FUNCTION FOR LINK");
                    // TODO：确认此处是否需要发送 DCF
                    if (_linkLayer.linkLayerParameters.UseSingleCharACK)
                {
                    _linkLayer.SendSingleCharACK();
                }
                else
                {
                    _linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.ACK, _linkLayerAddress, false, false);
                }

                break;

                case FunctionCodePrimary.USER_DATA_CONFIRMED:
                    _debugLog("SLL - USER DATA CONFIRMED");
                    {
                        if (fcbValid && (userDataLength > 0))
                        {
                            _handleApplicationLayer(address, msg, userDataStart, userDataLength);
                        }

                        // 链路层必须始终应答确认型用户数据帧（ACK / 单字符 ACK），
                        // 与 FCV/FCB 校验是否通过、应用层是否"处理"了该报文无关：
                        // 应用层返回值只表示它是否自行消费了报文，不能用来决定链路是否应答，
                        // 否则未注册应用层处理的主站将永不回应，对端只能超时复位链路。
                        if (_linkLayer.linkLayerParameters.UseSingleCharACK)
                        {
                            _linkLayer.SendSingleCharACK();
                        }
                        else
                        {
                            _linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.ACK, _linkLayerAddress, false, false);
                        }
                    }
                    break;

                case FunctionCodePrimary.USER_DATA_NO_REPLY:
                    _debugLog("SLL - USER DATA NO REPLY");
                    if (fcbValid && (userDataLength > 0))
                    {
                        _handleApplicationLayer(address, msg, userDataStart, userDataLength);
                    }
                    break;

                case FunctionCodePrimary.REQUEST_LINK_STATUS:
                    _debugLog("SLL - RECV REQUEST LINK STATUS");
                    SendStatusOfLink(_linkLayerAddress);
                    break;

                default:
                    _debugLog("SLL - UNEXPECTED LINK LAYER MESSAGE");
                    _linkLayer.SendFixedFrameSecondary(FunctionCodeSecondary.LINK_SERVICE_NOT_IMPLEMENTED, _linkLayerAddress, false, false);
                    break;

            }
        }

        public override void RunStateMachine()
        {

        }

    }
