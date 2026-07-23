// ControlWaiter.cs
//
// 让 IEC 60870-5-104 控制命令（预发 / 执行）能够被"同步等待"的辅助器。
//
// 同步等待的原理
// --------------
// 客户端 Send 一条控制命令（I 帧）后，服务端会回一个 COT = ACTIVATION_CON
// （激活确认）的 ASDU。本辅助器在 Iec104Client.AsduReceived 回调里拦截这个确认，
// 通过 TaskCompletionSource<T> 唤醒当时正在 await 的发送调用，从而实现：
//
//     预发  ──发送──▶   (等待)  ◀──ACT-CON(select)──  预发结束
//     执行  ──发送──▶   (等待)  ◀──ACT-CON(execute)── 执行完成
//
// 关联键 = (TypeID, 公共地址 CA, 信息对象地址 IOA, Select 位)。
// 因为"预发"与"执行"共用同一个 IOA，靠 Select 位区分，二者确认互不串台。

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS104;
using IEC60870.Core.InformationObjects;

namespace cs104_control_select_execute
{
    /// <summary>一次控制命令确认的快照（不可变、零堆分配顾虑）。</summary>
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
    /// 一个客户端实例配一个 ControlWaiter 即可（构造时接管 AsduReceived）。
    /// </summary>
    public sealed class ControlWaiter : IDisposable
    {
        private readonly Iec104Client _client;
        private readonly ApplicationLayerParameters _al;
        private readonly object _gate = new();
        private readonly Dictionary<long, TaskCompletionSource<ControlConfirmation>> _pending = new();
        private readonly OtherAsduHandler? _onOtherAsdu;
        private readonly AsduViewHandler? _previousHandler;

        /// <summary>非控制命令类 ASDU（如自发上送）的透传回调。AsduView 是 ref struct，故用专用委托。</summary>
        public delegate void OtherAsduHandler(in AsduView view);

        public ControlWaiter(Iec104Client client, OtherAsduHandler? onOtherAsdu = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _al = client.Parameters;
            _onOtherAsdu = onOtherAsdu;
            _previousHandler = client.AsduReceived;
            client.AsduReceived = OnAsduReceived;
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

                TaskCompletionSource<ControlConfirmation>? tcs;
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

            // 其它 ASDU（如自发上送 M_SP_NA_1 等）转给外部日志 / 原回调
            _onOtherAsdu?.Invoke(in view);
            _previousHandler?.Invoke(in view);
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
        private bool ReadSelectBit(in AsduView view)
        {
            byte[] raw = view.Raw.ToArray();
            ASDU asdu = new ASDU(_al, raw, 0, raw.Length);
            return asdu.NumberOfElements > 0 && GetSelectBit(asdu.GetElement(0));
        }

        /// <summary>
        /// 读取控制命令的 Select 位（预发=true / 执行=false）。各命令类型 Select 位位置不同：
        /// C_SC/DC/RC 在 IOA 后第 1 字节（SCO/DCQ），C_SE 在 QOS 字节，直接读类型化属性最稳妥。
        /// 无 Select 位的命令（如 C_BO_NA_1）按执行（false）处理。
        /// </summary>
        private static bool GetSelectBit(InformationObject io) => io switch
        {
            SingleCommand sc => sc.Select,
            DoubleCommand dc => dc.Select,
            SetpointCommandNormalized s => s.QOS.Select,
            SetpointCommandScaled s => s.QOS.Select,
            SetpointCommandShort s => s.QOS.Select,
            _ => false
        };

        public void Dispose()
        {
            // 还原 AsduReceived（若有原回调则交还）
            _client.AsduReceived = _previousHandler;
        }
    }
}
