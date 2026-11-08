//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TouchSocket.Core;


namespace IEC60870.CS101;

    /// <summary>
    /// 字节源抽象：FT1.2 帧解析器从它读取字节（带超时）。串口与 TCP 隧道各自实现。
    /// </summary>
    internal interface IByteSource
    {
        /// <summary>
        /// 读取最多 <paramref name="buffer"/>.Length 个字节，超时/取消时返回<b>已读到的字节数</b>
        /// （可能小于 <paramref name="buffer"/>.Length，调用方据此判定半帧）。
        /// </summary>
        /// <remarks>
        /// <paramref name="timeoutMs"/> 是<b>字符间超时</b>（character inter-byte timeout），
        /// 不是整次读取的总窗口：首字节最多等 <paramref name="timeoutMs"/>，之后每两个相邻
        /// 字节之间的空隙也各自最多等 <paramref name="timeoutMs"/>。因此一个传输耗时超过
        /// <paramref name="timeoutMs"/> 的长帧，只要字节之间不断流就不会被误判为超时。
        /// </remarks>
        ValueTask<int> ReadAsync(Memory<byte> buffer, int timeoutMs, CancellationToken ct);

        /// <summary>
        /// 底层流已不可用（对端关闭 / 串口被拔出 / BaseStream 已释放）。
        /// 返回 0 是"超时"还是"已故障"必须可区分，否则调用方只能空转。
        /// </summary>
        bool IsFaulted { get; }
    }

    /// <summary>
    /// 基于 <see cref="Stream"/> 的字节源（串口 BaseStream 或任意 Stream）。
    /// </summary>
    internal sealed class StreamByteSource : IByteSource
    {
        private readonly Stream _stream;
        private volatile bool _faulted;

        public StreamByteSource(Stream stream) => _stream = stream;

        /// <inheritdoc />
        public bool IsFaulted => _faulted;

        /// <summary>
        /// 读满 <paramref name="buffer"/>（或超时 / 出错），返回实际读到的字节数。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b><paramref name="timeoutMs"/> 的语义是"相邻字节之间的最大间隔"（字符间超时），
        /// 不是整次读取的总窗口。</b> 每读到 n&gt;0 个字节就为下一段重新开始计时：
        /// 首字节仍受完整的 <paramref name="timeoutMs"/> 约束，之后只要字节之间没有超过
        /// <paramref name="timeoutMs"/> 的空隙，读取就一直继续直到填满缓冲。
        /// </para>
        /// <para>
        /// 这对 FT1.2 变长帧是硬要求：帧体是一次性整段读取的（L 最大 255），9600 波特下
        /// 每字节约 1.04ms，100 字节的合法帧光传输就需要约 104ms。若把超时当成"整帧总窗口"，
        /// 默认 50ms 的字符超时会把所有 &gt;48 字节的帧判成半帧丢弃 —— 101 现场的总召唤响应、
        /// 多 IOA 帧几乎都在这个尺寸以上。
        /// </para>
        /// <para>
        /// 实现上每轮读取都用独立的新 CTS + <see cref="CancellationTokenSource.CancelAfter"/>，
        /// 而不是复用一个 CTS 反复 CancelAfter：后者在"计时器刚好触发"与"本轮读刚好完成"
        /// 重叠时无法复位（令牌已取消），下一段读会被立即取消，长帧会随机缩水。
        /// 独立 CTS 与对应的读操作同生命周期，既无该竞态，也不会残留计时器。
        /// </para>
        /// <para>
        /// 必须统一走 <see cref="Stream.ReadAsync(Memory{byte}, CancellationToken)"/>：
        /// 旧式 <c>ReadAsync(byte[], int, int, CancellationToken)</c> 重载在没有重写它的
        /// Stream 实现上会落到同步 <c>Read</c> 路径，而那条路径拿不到取消令牌
        /// —— 超时形同虚设，读会永久阻塞（自实现 Stream 或串口包装流上尤其明显）。
        /// </para>
        /// </remarks>
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, int timeoutMs, CancellationToken ct)
        {
            if (buffer.IsEmpty)
            {
                return 0;
            }

            // 与 AsyncByteQueue 一致：<=0 会变成 CancelAfter(0)/无限等待，至少给 1ms
            var interval = Math.Max(timeoutMs, 1);

            var total = 0;

            try
            {
                while (total < buffer.Length)
                {
                    int n;

                    // 每段字节独立计时：读到 n>0 后，下一段重新获得完整的 interval 窗口。
                    // 必须在 using 内 await：CTS 与读操作同生命周期，读完成（含异常）后才释放。
                    using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        cts.CancelAfter(interval);
                        n = await _stream.ReadAsync(buffer.Slice(total), cts.Token).ConfigureAwait(false);
                    }

                    if (n <= 0)
                    {
                        // EOF / 流已关闭：标记故障，让上层区分"本轮没数据"与"链路已死"。
                        // 原实现静默 break 返回 0，上层无延迟重入 → 串口拔线后满核空转。
                        _faulted = true;
                        break;
                    }

                    total += n;
                }
            }
            catch (OperationCanceledException)
            {
                // 字符间超时或外部取消：这是正常的"本轮无数据"，不是故障；
                // 返回已读字节数，调用方据此判断是不是半帧（契约不变）。
                return total;
            }
            catch (IOException)
            {
                _faulted = true;
                return total;
            }
            catch (ObjectDisposedException)
            {
                _faulted = true;
                return total;
            }

            return total;
        }
    }

    /// <summary>
    /// 自管理的异步字节队列（TCP 隧道用）。TouchSocket 收到字节后通过 <see cref="Write"/> 推入，
    /// FT1.2 帧解析器通过 <see cref="ReadAsync"/> 取走，支持超时与关闭。
    /// </summary>
    internal sealed class AsyncByteQueue : IByteSource, IDisposable
    {
        private readonly byte[] _buf = new byte[16384];
        private int _head;
        private int _tail;
        private int _count;
        private readonly object _lock = new object();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0, int.MaxValue);
        private readonly CancellationTokenSource _closeCts = new CancellationTokenSource();
        private volatile bool _closed;

        /// <inheritdoc />
        public bool IsFaulted => _closed;

        public void Write(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
            {
                return;
            }

            lock (_lock)
            {
                if (_closed)
                {
                    return;
                }

                var len = data.Length;
                if (len > _buf.Length)
                {
                    // 数据块超过环形缓冲总容量：只保留最新部分（丢弃最旧字节）
                    data = data.Slice(len - _buf.Length);
                    len = _buf.Length;
                }

                var wasEmpty = _count == 0;

                if (_count + len > _buf.Length)
                {
                    // 缓冲不足：丢弃最旧字节以腾出空间
                    var drop = _count + len - _buf.Length;
                    _head = (_head + drop) % _buf.Length;
                    _count -= drop;
                }

                // 整块写入环形缓冲（处理回绕）
                var first = Math.Min(len, _buf.Length - _tail);
                data.Slice(0, first).CopyTo(new Span<byte>(_buf, _tail, first));
                if (first < len)
                {
                    data.Slice(first).CopyTo(new Span<byte>(_buf, 0, len - first));
                }
                _tail = (_tail + len) % _buf.Length;
                _count += len;

                // 许可语义是「队列里有数据」而不是「发生了一次写入」：
                // FT1.2 分帧一帧可能分多次 ReadAsync 取（先取起始字节再取剩余），
                // 按写入次数放行会欠放行 → 第二次读拿不到许可，整帧被判超时。
                // 因此只在 0 → 非0 的跃变时放行一个；读走后若仍有数据再补一个（见 ReadAsync）。
                if (wasEmpty)
                {
                    try
                    {
                        _signal.Release();
                    }
                    catch { /* disposed */ }
                }
            }
        }

    public void Close()
        {
            _closed = true;
            _closeCts.Cancel();
            try
        {
            _signal.Release();
        }
        catch { /* disposed */ }
    }

    /// <summary>关闭队列并释放内部信号量与 CTS（幂等，SafeDispose 语义）。</summary>
    public void Dispose()
        {
            Close();
            _closeCts.SafeDispose();
            _signal.SafeDispose();
        }

        /// <summary>清空已缓冲但未消费的字节（连接切换/重连时丢弃上一会话的残留帧，避免帧损坏）。</summary>
        /// <remarks>
        /// 必须同时抽干已派发的许可：许可是"有一次写入"的凭据，与字节一起属于上一会话。
        /// 只清字节不清许可，残留许可会让重连后的读循环在无数据时连续返回 0，
        /// 调用方误判为"暂无数据"而空转。抽干放在同一把锁内，
        /// 与 <see cref="Write"/> 的"改缓冲 + 放行许可"原子化，避免清掉新会话的唤醒信号。
        /// </remarks>
        public void Clear()
        {
            lock (_lock)
            {
                _head = 0;
                _tail = 0;
                _count = 0;

                while (_signal.Wait(0))
                {
                }
            }
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, int timeoutMs, CancellationToken ct)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _closeCts.Token);
            linked.CancelAfter(Math.Max(timeoutMs, 1));
            try
            {
                await _signal.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }

            lock (_lock)
            {
                var toCopy = Math.Min(buffer.Length, _count);
                // 整块拷贝（处理回绕）
                var first = Math.Min(toCopy, _buf.Length - _head);
                new Span<byte>(_buf, _head, first).CopyTo(buffer.Span);
                if (first < toCopy)
                {
                    new Span<byte>(_buf, 0, toCopy - first).CopyTo(buffer.Span.Slice(first));
                }
                _head = (_head + toCopy) % _buf.Length;
                _count -= toCopy;

                // 数据还没取完（分帧器稍后还要读下一段）：把许可补回去，
                // 否则后续 ReadAsync 会一直等到超时 —— 一帧只取到一半就丢帧。
                if (_count > 0)
                {
                    try
                    {
                        _signal.Release();
                    }
                    catch { /* disposed */ }
                }

                return toCopy;
            }
        }
    }

    /// <summary>
    /// FT1.2 帧定界（0x68 可变长 / 0x10 固定长 / 0xE5 单字符 ACK），串口与 TCP 隧道共用。
    /// 逻辑等价于原 <c>SerialTransceiverFT12.ReadNextMessage</c>，但改为按需异步读取。
    /// </summary>
    internal static class FT12Framer
    {
        /// <summary>噪声重同步的最大尝试次数（防止对端持续发垃圾导致死循环）。</summary>
        private const int MaxResyncAttempts = 128;

        /// <summary>
        /// 读取一个完整 FT1.2 帧。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 与 103 的 <c>Ft12FrameParser</c> 对齐两项鲁棒性能力：
        /// </para>
        /// <list type="number">
        ///   <item><b>噪声重同步</b>：遇到非起始字节不再直接返回 0（那会让调用方以为"无数据"
        ///         而丢失同步），而是跳过该字节继续寻找起始符。</item>
        ///   <item><b>半帧超时复位</b>：读帧中途超时后丢弃已读部分、回到等待起始符状态，
        ///         避免残留半帧污染下一帧（103 的 TryResetStale 等价语义）。</item>
        /// </list>
        /// </remarks>
        public static async ValueTask<int> ReadFrameAsync(IByteSource src, Memory<byte> frame,
            LinkLayerParameters ll, int messageTimeout, int characterTimeout, Action<string> log, CancellationToken ct)
        {
            // 本轮是否从流里真的读到过字节（且全是噪声）。只有这种路径返回 0 才需要退避：
            // "无数据"路径已在 ReadAsync 内等满 messageTimeout，再加延迟只会白等。
            var sawNoise = false;

            for (var attempt = 0; attempt < MaxResyncAttempts; attempt++)
            {
                // 等待帧起始字符
                if (await src.ReadAsync(frame.Slice(0, 1), messageTimeout, ct).ConfigureAwait(false) != 1)
                {
                    return 0;
                }

                var start = frame.Span[0];

                if (start == 0x68)
                {
                    if (await src.ReadAsync(frame.Slice(1, 1), characterTimeout, ct).ConfigureAwait(false) != 1)
                    {
                        log("RECV: SYNC ERROR reading length byte (半帧超时，复位)");
                        continue;
                    }

                    int l = frame.Span[1];
                    var rest = l + 4; // 从索引 2 起还需读取的字节数

                    if (await src.ReadAsync(frame.Slice(2, rest), characterTimeout, ct).ConfigureAwait(false) != rest)
                    {
                        log("RECV: Timeout reading variable length frame (l=" + l + "，复位)");
                        continue;
                    }

                    return rest + 2; // 含已读的起始(0x68)与长度字节
                }

                if (start == 0x10)
                {
                    var msgSize = 3 + ll.AddressLength;

                    if (await src.ReadAsync(frame.Slice(1, msgSize), characterTimeout, ct).ConfigureAwait(false) != msgSize)
                    {
                        log("RECV: Timeout reading fixed length frame (复位)");
                        continue;
                    }

                    return msgSize + 1;
                }

                if (start == 0xE5)
                {
                    return 1;
                }

                // 噪声字节：跳过并继续寻找起始符（原实现直接 return 0 丢同步）
                sawNoise = true;
                log("RECV: SYNC ERROR unexpected start byte " + start + " (skip)");
            }

            log("RECV: too many resync attempts, giving up this read");

            // 持续噪声下的重同步是"读一个字节—判断—再读"的紧循环：本轮确实读到过字节，
            // 立即返回 0 会让调用方零延迟重入，噪声不停就满核空转。这里给一个最小退避
            // （正常"无数据"路径不受影响，见 sawNoise 的注释）。
            if (sawNoise)
            {
                try
                {
                    await Task.Delay(1, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            return 0;
        }
    }
