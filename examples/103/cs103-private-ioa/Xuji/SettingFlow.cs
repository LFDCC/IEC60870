//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using IEC60870.CS103;

namespace cs103_private_ioa.Xuji;

/// <summary>
/// 许继 103 定值读写（TI=61）的主站侧流程。
/// </summary>
/// <remarks>
/// <para>《许继103通信规约》3.3.18~3.3.19：</para>
/// <list type="number">
///   <item><b>读</b>：INF=100（定值序号=0 表示整区）→ 装置按表28 分帧回送，末帧 COT=10；
///         无定值时按表27 回 COT=21 否定。</item>
///   <item><b>写</b>：预发 INF=101 → 返校 102 → 执行 103 → 确认 104。可用 105 撤销。</item>
/// </list>
/// <para>
/// 取数方式随承载自动选择：标准 FT1.2 是非平衡链路，需召唤 1/2 级队列；
/// 自定义承载（如许继网口）装置收到即回送，靠 <c>AsduReceived</c> 事件接收。
/// </para>
/// </remarks>
public sealed class SettingClient : IAsyncDisposable
{
    /// <summary>单帧最多携带的定值个数（报文上限 253 字节，3 字节/项留余量）。</summary>
    public const int MaxValuesPerFrame = 64;

    private readonly Iec103Client _client;
    private readonly int _ca;
    private readonly byte _fun;

    /// <summary>true = 装置主动推送（自定义承载）；false = 召唤拉取（标准 FT1.2）。</summary>
    private readonly bool _pushMode;

    /// <summary>推送模式下装置上送的 TI=61 应答。</summary>
    private readonly Channel<Asdu> _inbox = Channel.CreateUnbounded<Asdu>();

    /// <summary>召唤模式下，一批召唤结果中尚未消费的 ASDU。</summary>
    private readonly Queue<Asdu> _polled = new();

    /// <summary>构造定值客户端。</summary>
    /// <param name="client">已连接的主站。</param>
    /// <param name="ca">公共地址。</param>
    /// <param name="fun">功能类型（由装置信息表定义）。</param>
    public SettingClient(Iec103Client client, int ca, byte fun)
    {
        _client = client;
        _ca = ca;
        _fun = fun;
        _pushMode = client.UsesCustomFraming;

        if (_pushMode)
        {
            _client.AsduReceived += OnAsduReceived;
        }
    }

    /// <summary>把收到的 TI=61 应答转入队列；其余报文不处理。</summary>
    private void OnAsduReceived(in Iec103AsduView asdu)
    {
        if (asdu.TypeId != XujiTypeIds.SettingCommand)
        {
            return;
        }

        _inbox.Writer.TryWrite(Asdu.Materialize(in asdu));
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _client.AsduReceived -= OnAsduReceived;
        _inbox.Writer.TryComplete();
        return default;
    }

    /// <summary>读一个定值区的全部定值。</summary>
    /// <param name="cpu">CPU 板地址（0-15，15 表示全部）。</param>
    /// <param name="zone">定值区号（0-15，15 表示当前运行区）。</param>
    /// <param name="timeoutMs">整体超时。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="TimeoutException">超时未收到结束帧。</exception>
    /// <exception cref="InvalidOperationException">装置否定应答（无此定值）。</exception>
    public async Task<XujiSettingValue[]> ReadAsync(byte cpu, byte zone, int timeoutMs = 5000, CancellationToken ct = default)
    {
        // INF=100 调定值，定值序号=0 表示整区
        await SendAsync(XujiSettingInf.Read, new XujiSettingCommand(cpu, zone, 0, Array.Empty<XujiSettingValue>()), ct);

        var values = new List<XujiSettingValue>();

        while (true)
        {
            var asdu = await ReadNextAsync(XujiSettingInf.Read, timeoutMs, ct).ConfigureAwait(false);

            if (asdu.Negative || asdu.Cot == XujiCot.Negative)
            {
                throw new InvalidOperationException($"装置否定应答：TI=61 INF={asdu.Inf} COT={asdu.Cot}");
            }

            // 结束帧：COT=10，定值序号=0，不带定值
            if (asdu.Cot == XujiCot.EndOfData)
            {
                return values.ToArray();
            }

            values.AddRange(Decode(asdu).Values);
        }
    }

    /// <summary>
    /// 写一个定值区的全部定值：预发 → 返校校验 → 执行 → 执行确认。
    /// </summary>
    /// <param name="cpu">CPU 板地址。</param>
    /// <param name="zone">定值区号。</param>
    /// <param name="values">定值数组（2 字节/项）。</param>
    /// <param name="timeoutMs">每一步的超时。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="InvalidOperationException">返校值与预发值不一致，或装置否定确认。</exception>
    public async Task WriteAsync(byte cpu, byte zone, XujiSettingValue[] values, int timeoutMs = 5000, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        // ── 1. 预发（INF=101）──
        await SendAsync(XujiSettingInf.PreSet, new XujiSettingCommand(cpu, zone, 1, values), ct);

        // ── 2. 收返校（INF=102），逐项比对 ──
        var echoed = await CollectAsync(XujiSettingInf.Verify, timeoutMs, ct);
        if (!echoed.SequenceEqual(values))
        {
            // 返校不符：撤销，避免装置停在「已预发」状态
            await TryCancelAsync(ct);
            throw new InvalidOperationException(
                $"返校值与预发值不一致（预发 {values.Length} 项，返校 {echoed.Length} 项）");
        }

        // ── 3. 执行（INF=103）──
        await SendAsync(XujiSettingInf.Execute, new XujiSettingCommand(cpu, zone, 1, Array.Empty<XujiSettingValue>()), ct);

        // ── 4. 收执行确认（INF=104）──
        await WaitAckAsync(XujiSettingInf.ExecuteAck, timeoutMs, ct);
    }

    /// <summary>撤销一次已预发但未执行的定值修改（INF=105 → 106）。</summary>
    /// <param name="timeoutMs">超时。</param>
    /// <param name="ct">取消令牌。</param>
    public async Task CancelAsync(int timeoutMs = 5000, CancellationToken ct = default)
    {
        await SendAsync(XujiSettingInf.Cancel, new XujiSettingCommand(0, 0, 0, Array.Empty<XujiSettingValue>()), ct);
        await WaitAckAsync(XujiSettingInf.CancelAck, timeoutMs, ct);
    }

    // ── 内部 ───────────────────────────────────────────────────────

    private Task SendAsync(byte inf, XujiSettingCommand command, CancellationToken ct)
        => _client.SendPrivateAsync(_ca, _fun, inf, XujiTypeIds.SettingCommand, command, ct);

    /// <summary>收集某个子命令（INF）回送的定值，直到 COT=10 结束帧。</summary>
    private async Task<XujiSettingValue[]> CollectAsync(byte expectInf, int timeoutMs, CancellationToken ct)
    {
        var values = new List<XujiSettingValue>();

        while (true)
        {
            var asdu = await ReadNextAsync(expectInf, timeoutMs, ct).ConfigureAwait(false);

            if (asdu.Negative || asdu.Cot == XujiCot.Negative)
            {
                throw new InvalidOperationException($"装置对 INF={expectInf} 否定应答（COT={asdu.Cot}）");
            }

            if (asdu.Cot == XujiCot.EndOfData)
            {
                return values.ToArray();
            }

            values.AddRange(Decode(asdu).Values);
        }
    }

    /// <summary>等待一个只带确认语义的应答帧（INF=104 / 106）。</summary>
    private async Task WaitAckAsync(byte expectInf, int timeoutMs, CancellationToken ct)
    {
        var asdu = await ReadNextAsync(expectInf, timeoutMs, ct).ConfigureAwait(false);

        if (asdu.Negative || asdu.Cot == XujiCot.Negative)
        {
            throw new InvalidOperationException($"装置对 INF={expectInf} 否定应答");
        }
    }

    /// <summary>取下一条 INF 匹配的 TI=61 应答。</summary>
    private async ValueTask<Asdu> ReadNextAsync(byte expectInf, int timeoutMs, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(timeoutMs);

        while (true)
        {
            Asdu? asdu;
            try
            {
                asdu = _pushMode
                    ? await _inbox.Reader.ReadAsync(timeout.Token).ConfigureAwait(false)
                    : await PollClass1Async(expectInf, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"{timeoutMs}ms 内未收到 INF={expectInf} 的应答");
            }

            if (asdu is not null && asdu.Inf == expectInf)
            {
                return asdu;
            }
        }
    }

    /// <summary>
    /// 标准 FT1.2：从本地缓存或新一次召唤中取一条 TI=61 应答。
    /// </summary>
    /// <remarks>
    /// 一次召唤可能同时带回数据帧与结束帧，整批先入本地队列再逐条消费，
    /// 只取第一条会丢掉后面的。
    /// </remarks>
    private async Task<Asdu?> PollClass1Async(byte expectInf, CancellationToken ct)
    {
        while (_polled.Count > 0)
        {
            var pending = _polled.Dequeue();
            if (pending.TypeId == XujiTypeIds.SettingCommand && pending.Inf == expectInf)
            {
                return pending;
            }
        }

        var batch = await _client.RequestClass1Async(_ca, ct: ct).ConfigureAwait(false);

        foreach (var asdu in batch)
        {
            if (asdu.TypeId == XujiTypeIds.SettingCommand && asdu.Inf == expectInf)
            {
                _polled.Enqueue(asdu);
            }
        }

        return _polled.Count > 0 ? _polled.Dequeue() : null;
    }

    private async Task TryCancelAsync(CancellationToken ct)
    {
        try
        {
            await CancelAsync(ct: ct);
        }
        catch (Exception)
        {
            // 撤销失败不掩盖原始错误
        }
    }

    /// <summary>把已物化的 ASDU 交给许继解码器解析成定值命令。</summary>
    /// <remarks>必须带上该 ASDU 自己的头部布局，否则 FUN/INF 偏移会错 2 字节。</remarks>
    private static XujiSettingCommand Decode(Asdu asdu)
        => (XujiSettingCommand)XujiSettingHandler.Parse(
            new Iec103AsduView(asdu.Encode(), asdu.EffectiveLayout));
}


/// <summary>
/// 许继 103 定值读写的从站（装置）侧模拟。
/// </summary>
/// <remarks>
/// 内存里维护 (CPU 板, 定值区) → 定值数组，按规约响应主站的 TI=61 各子命令。
/// 真实装置把这张表换成自己的参数区即可。
/// </remarks>
public sealed class SettingDevice
{
    private readonly Iec103Server _server;
    private readonly int _ca;
    private readonly byte _fun;
    private readonly Dictionary<(byte Cpu, byte Zone), XujiSettingValue[]> _table = new();

    /// <summary>已预发但未执行的值（预发-执行两阶段）。</summary>
    private (byte Cpu, byte Zone, XujiSettingValue[] Values)? _pending;

    /// <summary>构造装置侧模拟。</summary>
    /// <param name="server">已启动的 103 从站。</param>
    /// <param name="ca">公共地址。</param>
    /// <param name="fun">功能类型（须与主站一致）。</param>
    public SettingDevice(Iec103Server server, int ca, byte fun)
    {
        _server = server;
        _ca = ca;
        _fun = fun;
    }

    /// <summary>登记某个定值区的当前值（模拟装置参数区）。</summary>
    public void SetTable(byte cpu, byte zone, XujiSettingValue[] values)
        => _table[(cpu, zone)] = values;

    /// <summary>挂接到从站的 ASDU 回调上。</summary>
    public void Attach()
        => _server.AsduReceived += OnAsdu;

    private void OnAsdu(IIec103Session session, in Iec103AsduView asdu)
    {
        if (asdu.TypeId != XujiTypeIds.SettingCommand)
        {
            return;
        }

        if (XujiSettingHandler.Parse(in asdu) is not XujiSettingCommand cmd)
        {
            return;
        }

        switch (asdu.Inf)
        {
            case XujiSettingInf.Read:
                RespondRead(session, cmd);
                break;

            case XujiSettingInf.PreSet:
                _pending = (cmd.CpuBoard, cmd.Zone, cmd.Values);
                RespondValues(session, XujiSettingInf.Verify, cmd);
                break;

            case XujiSettingInf.Execute:
                RespondExecute(session, cmd);
                break;

            case XujiSettingInf.Cancel:
                _pending = null;
                session.SendAsduAsync(XujiPrivateTypes.CreateAsdu(
                    XujiTypeIds.SettingCommand, _ca, _fun, XujiSettingInf.CancelAck,
                    new XujiSettingCommand(cmd.CpuBoard, cmd.Zone, 0, Array.Empty<XujiSettingValue>()),
                    XujiCot.Positive));
                break;
        }
    }

    /// <summary>INF=100：有定值按表28 分帧上送 + 结束帧；无定值回表27 否定。</summary>
    private void RespondRead(IIec103Session session, XujiSettingCommand request)
    {
        if (!_table.TryGetValue((request.CpuBoard, request.Zone), out var values))
        {
            // 表27：无定值时回答，定值序号=0，COT=21
            session.SendAsduAsync(XujiPrivateTypes.CreateAsdu(
                XujiTypeIds.SettingCommand, _ca, _fun, XujiSettingInf.Read,
                new XujiSettingCommand(request.CpuBoard, request.Zone, 0, Array.Empty<XujiSettingValue>()),
                XujiCot.Negative));
            return;
        }

        // 表28：按每帧 MaxValuesPerFrame 项分片，定值序号为该项在区内的 1 基序号
        for (var offset = 0; offset < values.Length; offset += SettingClient.MaxValuesPerFrame)
        {
            var count = Math.Min(SettingClient.MaxValuesPerFrame, values.Length - offset);
            var chunk = new XujiSettingValue[count];
            Array.Copy(values, offset, chunk, 0, count);

            session.SendAsduAsync(XujiPrivateTypes.CreateAsdu(
                XujiTypeIds.SettingCommand, _ca, _fun, XujiSettingInf.Read,
                new XujiSettingCommand(request.CpuBoard, request.Zone, (byte)(offset + 1), chunk),
                XujiCot.Positive));
        }

        SendEndFrame(session, request, XujiSettingInf.Read);
    }

    /// <summary>回送一组定值（返校用），最后一帧 COT=10。</summary>
    private void RespondValues(IIec103Session session, byte inf, XujiSettingCommand cmd)
    {
        var values = cmd.Values;
        for (var offset = 0; offset < values.Length; offset += SettingClient.MaxValuesPerFrame)
        {
            var count = Math.Min(SettingClient.MaxValuesPerFrame, values.Length - offset);
            var chunk = new XujiSettingValue[count];
            Array.Copy(values, offset, chunk, 0, count);

            session.SendAsduAsync(XujiPrivateTypes.CreateAsdu(
                XujiTypeIds.SettingCommand, _ca, _fun, inf,
                new XujiSettingCommand(cmd.CpuBoard, cmd.Zone, (byte)(offset + 1), chunk),
                XujiCot.Positive));
        }

        SendEndFrame(session, cmd, inf);
    }

    /// <summary>INF=103 执行：落盘并回 104 确认；没有预发内容回否定。</summary>
    private void RespondExecute(IIec103Session session, XujiSettingCommand cmd)
    {
        if (_pending is not { } pending)
        {
            session.SendAsduAsync(XujiPrivateTypes.CreateAsdu(
                XujiTypeIds.SettingCommand, _ca, _fun, XujiSettingInf.ExecuteAck,
                new XujiSettingCommand(cmd.CpuBoard, cmd.Zone, 0, Array.Empty<XujiSettingValue>()),
                XujiCot.Negative));
            return;
        }

        _table[(pending.Cpu, pending.Zone)] = pending.Values;
        _pending = null;

        // 表27：执行确认，定值序号=0，COT=20
        session.SendAsduAsync(XujiPrivateTypes.CreateAsdu(
            XujiTypeIds.SettingCommand, _ca, _fun, XujiSettingInf.ExecuteAck,
            new XujiSettingCommand(pending.Cpu, pending.Zone, 0, Array.Empty<XujiSettingValue>()),
            XujiCot.Positive));
    }

    private void SendEndFrame(IIec103Session session, XujiSettingCommand cmd, byte inf)
    {
        // 表28 末帧：COT=10，定值序号=0，不带定值
        session.SendAsduAsync(XujiPrivateTypes.CreateAsdu(
            XujiTypeIds.SettingCommand, _ca, _fun, inf,
            new XujiSettingCommand(cmd.CpuBoard, cmd.Zone, 0, Array.Empty<XujiSettingValue>()),
            XujiCot.EndOfData));
    }
}
