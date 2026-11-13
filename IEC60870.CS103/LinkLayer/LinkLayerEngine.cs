//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//  协议实现依据：DL/T 667-1999 第 2 章（链路层）
//------------------------------------------------------------------------------

namespace IEC60870.CS103;

/// <summary>
/// 103 非平衡链路层引擎（主站/从站共用）。
/// </summary>
/// <remarks>
/// <para><b>职责边界</b>：只处理 FT1.2 链路层事务——帧收发、FCB 翻转与校验、
/// 超时重发、1/2 级队列调度、ACD/DFC 维护。ASDU 语义由上层
/// （<see cref="Iec103Client"/> / <see cref="Iec103Server"/>）解释。</para>
/// <para><b>非平衡语义</b>：主站（PRM=1）发起服务，从站（PRM=0）被动应答；
/// 从站从不主动发起链路服务，突发数据靠 1 级队列 + ACD 位提示。103 没有平衡模式。</para>
/// <para><b>FCB 规则</b>：主站对同一从站的新一轮 SEND/CONFIRM 或 REQUEST 服务
/// 必须翻转 FCB；从站校验 FCB 重复帧并重发上次应答（消除重复报文）。
/// 本类把 FCB 收敛为单一状态，避免每个服务方法各自维护。</para>
/// <para><b>FCB 按地址独立</b>：IEC 60870-5-1 规定帧计数位是<b>每个从站独立</b>的。
/// 一条通道下挂多台装置（各自有链路地址）时，若共用一个 FCB，主站交替访问
/// 不同装置会让某台看到的 FCB 序列与它自己上一轮不符；严格校验的装置会把
/// 正常请求判成重复帧并回陈旧应答。故主站/从站两侧的 FCB 状态均按链路地址分组。</para>
/// </remarks>
internal sealed class LinkLayerEngine
{
    private readonly IIec103LinkTransport _transport;
    private readonly Iec103LinkParameters _parameters;
    private Action<string>? _log;

    // FCB 状态按链路地址分组。主站侧受调用方的链路事务锁保护、从站侧由单读循环
    // 串行访问；仍用一把锁兜底，避免未来接入并发调用时字典被并发读写破坏。
    private readonly object _fcbGate = new();

    // 主站侧 FCB 状态（各地址下一次 SEND/CONFIRM 或 REQUEST 使用的值）
    private readonly Dictionary<byte, bool> _nextFcb = new();

    // 从站侧：各地址上一次收到的 FCB 与其应答帧（用于重复帧检测）
    private readonly Dictionary<byte, SecondaryState> _secondaryStates = new();

    /// <summary>从站侧单地址的重复帧检测状态。</summary>
    private sealed class SecondaryState
    {
        /// <summary>是否已收到过一帧（对应原实现的 <c>bool?</c> 有值）。</summary>
        public bool HasFcb;

        /// <summary>上一次收到的 FCB 值。</summary>
        public bool Fcb;

        /// <summary>上一次应答帧的线路字节（重复帧原样回送）。</summary>
        public byte[]? Response;
    }

    private long _frameCount;
    private long _repeatCount;
    private long _rejectedCount;

    public LinkLayerEngine(IIec103LinkTransport transport, Iec103LinkParameters parameters, Action<string>? log = null)
    {
        _transport = transport;
        _parameters = parameters;
        _log = log;
    }

    /// <summary>链路地址（主站在连接后由对端确定，从站由配置指定）。</summary>
    public byte Address { get; set; }

    /// <summary>已发送/接收的帧总数（诊断）。</summary>
    public long FrameCount => Interlocked.Read(ref _frameCount);

    /// <summary>超时重发次数（诊断）。</summary>
    public long RepeatCount => Interlocked.Read(ref _repeatCount);

    /// <summary>被舍弃的坏帧数（诊断）。</summary>
    public long RejectedCount => Interlocked.Read(ref _rejectedCount);

    /// <summary>取得指定链路地址下一次服务应使用的 FCB 值。</summary>
    public bool GetNextFcb(byte address)
    {
        lock (_fcbGate)
        {
            return _nextFcb.TryGetValue(address, out var fcb) && fcb;
        }
    }

    /// <summary>翻转指定链路地址的 FCB（每完成一轮 SEND/CONFIRM 或 REQUEST/RESPOND 后调用）。</summary>
    public void FlipFcb(byte address)
    {
        lock (_fcbGate)
        {
            _nextFcb[address] = !(_nextFcb.TryGetValue(address, out var fcb) && fcb);
        }
    }

    /// <summary>复位指定地址的 FCB 与重复帧状态（该地址的 RESET CU / RESET FCB 后）。</summary>
    public void ResetFcbState(byte address)
    {
        lock (_fcbGate)
        {
            _nextFcb.Remove(address);
            _secondaryStates.Remove(address);
        }
    }

    /// <summary>
    /// 发送帧。返回本次使用的原始字节（便于 <c>RawFrameSent</c> 事件）。
    /// </summary>
    /// <summary>链路层诊断日志回调（帧收发、重发、重复帧、非法帧）。</summary>
    public Action<string>? Log
    {
        get => _log;
        set => _log = value;
    }

    public async ValueTask<byte[]> SendAsync(Ft12Frame frame, CancellationToken ct)
    {
        var bytes = frame.ToBytes();
        _log?.Invoke($"[TX] {Convert.ToHexString(bytes)}");
        await _transport.WriteAsync(bytes, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _frameCount);
        return bytes;
    }

    /// <summary>
    /// 主站：发送控制帧并等待从站的应答。
    /// </summary>
    /// <param name="control">控制域。</param>
    /// <param name="transaction">本轮事务（由 <see cref="LinkTransactionDispatcher103.Begin"/> 建立）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>从站应答帧；超时抛 <see cref="TimeoutException"/>。</returns>
    /// <remarks>
    /// 应答的相关性校验在 <see cref="LinkTransaction103.IsMatch"/> 完成：
    /// 只接受 PRM=0、地址匹配、且功能码在期望集合内的帧。
    /// 迟到帧/重复帧不会被本事务消费，从根源上消除了「全局队列串答」。
    /// </remarks>
    public async ValueTask<Ft12Frame> SendControlAndWaitAsync(
        LinkControl control,
        LinkTransaction103 transaction,
        CancellationToken ct)
    {
        var frame = new Ft12Frame(Ft12FrameFormat.FixedLength, control.ToByte(), Address, Ft12Frame.EmptyAsdu);

        for (var attempt = 0; attempt <= _parameters.MaxRepeat; attempt++)
        {
            await SendAsync(frame, ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_parameters.MessageTimeoutMs);

            try
            {
                return await transaction.ReadAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Iec103TransactionAbortedException)
            {
                // 事务已被结束（不是超时）：不得重发，直接让调用方收束
                throw;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 本帧超时，进入重发分支
            }

            if (attempt < _parameters.MaxRepeat)
            {
                Interlocked.Increment(ref _repeatCount);
                _log?.Invoke($"链路超时重发 FC={control.Function}({control.DescribeFunction()}) 第 {attempt + 1} 次");
            }
        }

        throw new TimeoutException($"链路服务 {control.DescribeFunction()} 在 {_parameters.MaxRepeat + 1} 次尝试后仍无应答");
    }

    /// <summary>
    /// 主站：发送可变帧（用户数据）并等待从站确认。
    /// </summary>
    /// <param name="asdu">ASDU 字节。</param>
    /// <param name="transaction">本轮事务。</param>
    /// <param name="ct">取消令牌。</param>
    public async ValueTask<Ft12Frame> SendConfirmedAndWaitAsync(
        ReadOnlyMemory<byte> asdu,
        LinkTransaction103 transaction,
        CancellationToken ct)
    {
        // 整轮重发必须使用同一个 FCB：重发的是「同一帧」，从站才能识别为重复帧。
        var fcb = GetNextFcb(Address);
        var control = LinkControl.SendConfirmed(fcb);
        var frame = new Ft12Frame(Ft12FrameFormat.VariableLength, control.ToByte(), Address, asdu.ToArray());

        for (var attempt = 0; attempt <= _parameters.MaxRepeat; attempt++)
        {
            await SendAsync(frame, ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_parameters.MessageTimeoutMs);

            try
            {
                return await transaction.ReadAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Iec103TransactionAbortedException)
            {
                // 事务已被结束（不是超时）：不得重发
                throw;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 重发（注意：不翻转 FCB，重发的是同一帧，从站才能识别为重复帧）
            }

            if (attempt < _parameters.MaxRepeat)
            {
                Interlocked.Increment(ref _repeatCount);
                _log?.Invoke($"用户数据超时重发（A={Address} FCB={(fcb ? 1 : 0)}）第 {attempt + 1} 次");
            }
        }

        throw new TimeoutException($"发送用户数据在 {_parameters.MaxRepeat + 1} 次尝试后仍无确认");
    }

    /// <summary>
    /// 从站：生成对 PRM=1 请求帧的应答。
    /// </summary>
    /// <param name="request">主站请求帧。</param>
    /// <param name="nextClass1">取下一条 1 级数据（若无返回 null）。</param>
    /// <param name="nextClass2">取下一条 2 级数据（若无返回 null）。</param>
    /// <param name="hasMoreClass1">取数据<b>之后</b> 1 级队列是否仍有余量（决定 ACD 位）。</param>
    /// <returns>应答帧；无需应答时返回 null（如广播帧）。</returns>
    public Ft12Frame? BuildSecondaryResponse(
        Ft12Frame request,
        Func<byte[]?> nextClass1,
        Func<byte[]?> nextClass2,
        Func<bool> hasMoreClass1)
    {
        var control = LinkControl.FromByte(request.Control);
        if (!control.IsPrimary)
        {
            _log?.Invoke($"收到 PRM=0 帧（FC={control.Function}），非平衡模式下非法，已忽略");
            return null;
        }

        var function = (PrimaryFunction)control.Function;
        var isBroadcast = request.Address == 255;

        // RS-485 广播：只有 SEND/NO REPLY 不需要应答；其余广播请求
        // 若每站都回帧会必然造成总线冲突，因此一律不回应答
        // （ASDU 已由 SessionCore.DispatchAsdu 派发执行）。
        if (isBroadcast && function != PrimaryFunction.SendNoReply)
        {
            return null;
        }

        switch (function)
        {
            case PrimaryFunction.ResetCu:
                ResetFcbState(request.Address);
                return SecondaryAck(request.Address, acd: hasMoreClass1(), dfc: false);

            case PrimaryFunction.ResetFcb:
                ResetFcbState(request.Address);
                return SecondaryAck(request.Address, acd: hasMoreClass1(), dfc: false);

            case PrimaryFunction.RequestLinkStatus:
                return new Ft12Frame(
                    Ft12FrameFormat.FixedLength,
                    LinkControl.Secondary(SecondaryFunction.LinkStatus, hasMoreClass1(), false).ToByte(),
                    request.Address,
                    Ft12Frame.EmptyAsdu);

            case PrimaryFunction.SendNoReply:
                return null;

            case PrimaryFunction.SendConfirmed:
                // SEND/CONFIRM 标准应答是固定帧 ACK/NACK。
                // 数据必须由后续 REQUEST/RESPOND 取走，不能把 1/2 级队列
                // 的数据塞进确认帧（那会让 CLASS1/CLASS2 语义失效）。
                if (!control.FcvOrDfc)
                {
                    _log?.Invoke("收到 FCV=0 的 SEND/CONFIRM，按无 FCB 处理");
                }

                if (!isBroadcast)
                {
                    var state = GetSecondaryState(request.Address);
                    if (state.HasFcb && state.Fcb == control.FcbOrAcd && state.Response is not null)
                    {
                        _log?.Invoke($"检测到重复帧（A={request.Address} FCB={(state.Fcb ? 1 : 0)}），重发上次应答");
                        return Ft12Frame.TryDecode(state.Response, out var cached, out _) ? cached : null;
                    }

                    state.HasFcb = true;
                    state.Fcb = control.FcbOrAcd;
                }

                var ack = SecondaryAck(request.Address, acd: hasMoreClass1(), dfc: false);
                if (!isBroadcast)
                {
                    GetSecondaryState(request.Address).Response = ack.ToBytes();
                }

                return ack;

            case PrimaryFunction.RequestClass1:
            case PrimaryFunction.RequestClass2:
                if (!control.FcvOrDfc)
                {
                    _log?.Invoke($"收到 FCV=0 的 {control.DescribeFunction()}，按无 FCB 处理");
                }

                if (!isBroadcast)
                {
                    var state = GetSecondaryState(request.Address);
                    if (state.HasFcb && state.Fcb == control.FcbOrAcd && state.Response is not null)
                    {
                        _log?.Invoke($"检测到重复帧（A={request.Address} FCB={(state.Fcb ? 1 : 0)}），重发上次应答");
                        return Ft12Frame.TryDecode(state.Response, out var cached, out _) ? cached : null;
                    }

                    state.HasFcb = true;
                    state.Fcb = control.FcbOrAcd;
                }

                // CLASS1 召唤只取 1 级队列，CLASS2 召唤只取 2 级队列。
                // 原实现无条件 nextClass1() ?? nextClass2()，使 2 级召唤
                // 在 1 级非空时收到突发事件，队列分类形同虚设。
                var payload = function == PrimaryFunction.RequestClass1
                    ? nextClass1()
                    : nextClass2();

                Ft12Frame response;
                var acd = hasMoreClass1(); // 取完后剩余，才是正确的 ACD 提示

                if (payload is not null)
                {
                    response = new Ft12Frame(
                        Ft12FrameFormat.VariableLength,
                        LinkControl.Secondary(SecondaryFunction.RespondData, acd, false).ToByte(),
                        request.Address,
                        payload);
                }
                else
                {
                    response = new Ft12Frame(
                        Ft12FrameFormat.FixedLength,
                        LinkControl.Secondary(SecondaryFunction.RespondNoData, acd, false).ToByte(),
                        request.Address,
                        Ft12Frame.EmptyAsdu);
                }

                if (!isBroadcast)
                {
                    GetSecondaryState(request.Address).Response = response.ToBytes();
                }

                return response;

            default:
                _log?.Invoke($"未处理的主站功能码 FC={control.Function}，回 ACK");
                return SecondaryAck(request.Address, acd: hasMoreClass1(), dfc: false);
        }
    }

    private static Ft12Frame SecondaryAck(byte address, bool acd, bool dfc) => new(
        Ft12FrameFormat.FixedLength,
        LinkControl.Secondary(SecondaryFunction.Ack, acd, dfc).ToByte(),
        address,
        Ft12Frame.EmptyAsdu);

    /// <summary>
    /// 取得指定地址的从站重复帧状态（不存在则创建）。
    /// </summary>
    /// <remarks>
    /// 只在字典增删上持锁；返回对象的字段由调用方（从站单读循环）串行读写，
    /// 与 <see cref="ResetFcbState(byte)"/> 的竞争最坏结果是丢失一次去重缓存，
    /// 不会破坏链路状态。
    /// </remarks>
    private SecondaryState GetSecondaryState(byte address)
    {
        lock (_fcbGate)
        {
            if (!_secondaryStates.TryGetValue(address, out var state))
            {
                state = new SecondaryState();
                _secondaryStates[address] = state;
            }

            return state;
        }
    }
}
