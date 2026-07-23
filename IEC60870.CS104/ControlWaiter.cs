/*
 *  ControlWaiter.cs
 *
 *  Copyright 2016-2026 LFDCC
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.Core.InformationObjects;

namespace IEC60870.CS104
{
    /// <summary>一次控制命令确认的快照（不可变）。</summary>
    public readonly struct ControlConfirmation
    {
        public ControlConfirmation(TypeID typeId, int commonAddress, int ioa,
            bool isSelect, bool isNegative, bool isTest)
        {
            TypeId = typeId;
            CommonAddress = commonAddress;
            Ioa = ioa;
            IsSelect = isSelect;     // true = 预发确认；false = 执行确认
            IsNegative = isNegative; // true = 否定确认（被拒）
            IsTest = isTest;
        }

        public TypeID TypeId { get; }
        public int CommonAddress { get; }
        public int Ioa { get; }
        public bool IsSelect { get; }
        public bool IsNegative { get; }
        public bool IsTest { get; }

        /// <summary>阶段中文名：预发结束 / 执行完成。</summary>
        public string Phase => IsSelect ? "预发结束" : "执行完成";

        public override string ToString() =>
            $"[{Phase}] Type={TypeId} IOA={Ioa} CA={CommonAddress} " +
            $"{(IsNegative ? "NEGATIVE(拒绝)" : "OK")}{(IsTest ? " [TEST]" : "")}";
    }

    /// <summary>
    /// 为 <see cref="Iec104Client"/> 提供"发送控制命令并等待其 ACT-CON"的能力。
    /// 通过订阅客户端 <see cref="Iec104Client.AsduReceived"/> 事件实现（多播，绝不接管外部订阅者），
    /// 并在 <see cref="Dispose"/> 中解绑，避免客户端被本辅助器长期持有导致内存泄漏。
    /// </summary>
    /// <remarks>
    /// 关联键 = (TypeID, 公共地址 CA, 信息对象地址 IOA, Select 位)。预发与执行共用同一 IOA，
    /// 靠 Select 位区分，二者确认互不串台。外部可同时独立订阅 <see cref="Iec104Client.AsduReceived"/>
    /// 获取全部 ASDU（含控制确认），与本辅助器并存。
    /// </remarks>
    public sealed class ControlWaiter : IDisposable
    {
        private readonly Iec104Client _client;
        private readonly ApplicationLayerParameters _al;
        private readonly object _gate = new();
        private readonly Dictionary<long, TaskCompletionSource<ControlConfirmation>> _pending = new();
        private bool _subscribed;

        public ControlWaiter(Iec104Client client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _al = client.Parameters;
            // 多播订阅：不接管 client.AsduReceived，外部订阅者仍会直接收到全部 ASDU
            client.AsduReceived += OnAsduReceived;
            _subscribed = true;
        }

        /// <summary>
        /// 发送一条控制命令 ASDU，并异步等待其 ACTIVATION_CON（或否定确认）。
        /// 内部用 Select 位区分"预发"与"执行"的确认，二者可顺序串行调用。
        /// </summary>
        /// <param name="cot">发起控制用 ACTIVATION。</param>
        /// <param name="ca">公共地址。</param>
        /// <param name="io">控制命令信息对象（SingleCommand/DoubleCommand/Setpoint...），其 Select 位决定预发或执行。</param>
        /// <param name="cancellationToken">外部取消。</param>
        /// <returns>服务端回送的确认（含是否否定）。</returns>
        /// <exception cref="TimeoutException">超过 5 秒未收到匹配确认时抛出，避免调用方永久挂起。</exception>
        public async Task<ControlConfirmation> SendControlCommandAndWaitAsync(
            CauseOfTransmission cot, int ca, InformationObject io,
            CancellationToken cancellationToken = default)
        {
            // 关联键基于命令自身的 Select 位（预发 true / 执行 false），保证两步互不串台
            bool selectBit = GetSelectBit(io);
            long key = MakeKey(io.Type, ca, io.ObjectAddress, selectBit);

            var tcs = new TaskCompletionSource<ControlConfirmation>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
                _pending[key] = tcs;

            try
            {
                // 1) 发送命令（k 窗口满时内部异步背压，不阻塞线程）
                await _client.SendControlCommandAsync(cot, ca, io, cancellationToken)
                    .ConfigureAwait(false);

                // 2) 等待服务端回送的 ACT-CON（带超时保护）
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"等待控制命令确认超时（{(selectBit ? "预发" : "执行")}）：" +
                        $"Type={io.Type}, IOA={io.ObjectAddress}, CA={ca}");
                }
            }
            finally
            {
                lock (_gate)
                    _pending.Remove(key);
            }
        }

        private void OnAsduReceived(in AsduView view)
        {
            // 仅拦截"激活确认"（否定确认 COT 仍为 ACTIVATION_CON，仅 negative 位置 1）
            if (view.Cot == CauseOfTransmission.ACTIVATION_CON)
            {
                int ioa = ReadIoa(view);
                bool selectBit = ReadSelectBit(view);
                long key = MakeKey(view.TypeId, view.CommonAddress, ioa, selectBit);

                TaskCompletionSource<ControlConfirmation> tcs;
                lock (_gate)
                    _pending.TryGetValue(key, out tcs);

                if (tcs != null)
                {
                    tcs.TrySetResult(new ControlConfirmation(
                        view.TypeId, view.CommonAddress, ioa,
                        selectBit, view.IsNegative, view.IsTest));
                    return; // 已被本次请求消费，不再外传
                }
            }

            // 其它 ASDU（如自发上送 M_SP_NA_1 等）不在此处理；
            // 外部订阅者会经各自的 += 直接收到全部 ASDU（含控制确认）
        }

        // ── 关联键与字段解析（直接读字节，零分配）─────────────────

        private static long MakeKey(TypeID type, int ca, int ioa, bool select) =>
            ((long)type << 48) | ((long)(ca & 0xffff) << 32) | ((long)(ioa & 0xffffff) << 8) | (select ? 1L : 0L);

        /// <summary>从收到的 ACT-CON 原始字节读 IOA（小端，宽度由应用层参数决定）。</summary>
        private int ReadIoa(in AsduView view)
        {
            ReadOnlySpan<byte> raw = view.Raw;
            int off = view.HeaderLength;
            int ioa = 0;
            for (int i = 0; i < _al.SizeOfIOA && off + i < raw.Length; i++)
                ioa |= raw[off + i] << (8 * i);
            return ioa;
        }

        /// <summary>从收到的 ACT-CON 读 Select 位：IOA 之后第一个限定词字节的 bit7。</summary>
        /// <remarks>
        /// 直接读字节（零分配），与 <see cref="GetSelectBit(InformationObject)"/> 的位定义一致：
        /// C_SC/DC/RC 的 SCO/DCQ、C_SE 的 QOS 均为信息元素首字节 bit7 = Select；
        /// 无 Select 位的命令（如 C_BO_NA_1）按 false 处理。
        /// </remarks>
        private bool ReadSelectBit(in AsduView view)
        {
            if (view.TypeId == TypeID.C_BO_NA_1)
                return false; // 无 Select 位

            ReadOnlySpan<byte> raw = view.Raw;
            int off = view.HeaderLength + _al.SizeOfIOA; // 第一个信息元素的限定词字节
            if (off < raw.Length)
                return (raw[off] & 0x80) != 0;
            return false;
        }

        /// <summary>
        /// 读取控制命令的 Select 位（预发=true / 执行=false）。各命令类型 Select 位位置不同：
        /// C_SC/DC/RC 在 IOA 后第 1 字节（SCO/DCQ/RCO），C_SE 在 QOS 字节，直接读类型化属性最稳妥。
        /// 无 Select 位的命令（如 C_BO_NA_1）按执行（false）处理。
        /// </summary>
        /// <remarks>
        /// <see cref="StepCommand"/>（C_RC_NA_1 / C_RC_TA_1）虽继承自 <see cref="DoubleCommand"/>、
        /// 类型模式已能命中，此处仍显式列出：避免将来 <see cref="StepCommand"/> 不再继承
        /// <see cref="DoubleCommand"/> 时静默落入 <c>_ => false</c>，导致预发（select）确认的
        /// 关联键永远等于执行（false），预发确认匹配不上而超时。
        /// </remarks>
        internal static bool GetSelectBit(InformationObject io) => io switch
        {
            SingleCommand sc => sc.Select,
            StepCommand rc => rc.Select,
            DoubleCommand dc => dc.Select,
            SetpointCommandNormalized s => s.QOS.Select,
            SetpointCommandScaled s => s.QOS.Select,
            SetpointCommandShort s => s.QOS.Select,
            _ => false
        };

        /// <summary>
        /// 解绑 <see cref="Iec104Client.AsduReceived"/> 事件，避免客户端被本实例长期持有导致内存泄漏。
        /// 幂等：可安全多次调用。
        /// </summary>
        public void Dispose()
        {
            if (_subscribed)
            {
                _client.AsduReceived -= OnAsduReceived;
                _subscribed = false;
            }
        }
    }
}
