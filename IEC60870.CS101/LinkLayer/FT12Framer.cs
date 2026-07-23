/*
 *  FT12Framer.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 *
 *  See COPYING file for the complete license text.
 */

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;


namespace IEC60870.CS101.LinkLayer
{
    /// <summary>
    /// 字节源抽象：FT1.2 帧解析器从它读取字节（带超时）。串口与 TCP 隧道各自实现。
    /// </summary>
    internal interface IByteSource
    {
        /// <summary>
        /// 读取最多 <paramref name="buffer"/>.Length 个字节，超时或关闭时返回 0。
        /// </summary>
        ValueTask<int> ReadAsync(Memory<byte> buffer, int timeoutMs, CancellationToken ct);
    }

    /// <summary>
    /// 基于 <see cref="Stream"/> 的字节源（串口 BaseStream 或任意 Stream）。
    /// </summary>
    internal sealed class StreamByteSource : IByteSource
    {
        private readonly Stream _stream;
        private readonly byte[] _one = new byte[1];

        public StreamByteSource(Stream stream) => _stream = stream;

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, int timeoutMs, CancellationToken ct)
        {
            if (buffer.Length == 1)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeoutMs);
                try
                {
                    int n = await _stream.ReadAsync(_one, 0, 1, cts.Token).ConfigureAwait(false);
                    if (n == 1)
                    {
                        buffer.Span[0] = _one[0];
                        return 1;
                    }
                    return 0;
                }
                catch (OperationCanceledException)
                {
                    return 0;
                }
                catch (IOException)
                {
                    return 0;
                }
                catch (ObjectDisposedException)
                {
                    return 0;
                }
            }

            using var cts2 = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts2.CancelAfter(timeoutMs);
            int total = 0;
            try
            {
                while (total < buffer.Length)
                {
                    int n = await _stream.ReadAsync(buffer.Slice(total), cts2.Token).ConfigureAwait(false);
                    if (n <= 0)
                        break;
                    total += n;
                }
            }
            catch (OperationCanceledException)
            {
                return total;
            }
            catch (IOException)
            {
                return total;
            }
            catch (ObjectDisposedException)
            {
                return total;
            }
            return total;
        }
    }

    /// <summary>
    /// 自管理的异步字节队列（TCP 隧道用）。TouchSocket 收到字节后通过 <see cref="Write"/> 推入，
    /// FT1.2 帧解析器通过 <see cref="ReadAsync"/> 取走，支持超时与关闭。
    /// </summary>
    internal sealed class AsyncByteQueue : IByteSource
    {
        private readonly byte[] _buf = new byte[16384];
        private int _head;
        private int _tail;
        private int _count;
        private readonly object _lock = new object();
        private readonly SemaphoreSlim _signal = new SemaphoreSlim(0, int.MaxValue);
        private readonly CancellationTokenSource _closeCts = new CancellationTokenSource();
        private bool _closed;

        public void Write(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
                return;

            int accepted;
            lock (_lock)
            {
                if (_closed)
                    return;
                accepted = 0;
                foreach (byte b in data)
                {
                    if (_count == _buf.Length)
                    {
                        // 缓冲已满：丢弃最旧字节以接纳新字节（有界环形，避免静默覆盖未读数据导致帧损坏，代码评审 #11）。
                        _head = (_head + 1) % _buf.Length;
                        _count--;
                    }
                    _buf[_tail] = b;
                    _tail = (_tail + 1) % _buf.Length;
                    _count++;
                    accepted++;
                }
            }
            // 仅对实际写入的字节放行信号量，避免溢出丢弃时信号量计数虚高。
            if (accepted > 0)
            {
                try { _signal.Release(accepted); } catch { /* disposed */ }
            }
        }

        public void Close()
        {
            _closed = true;
            _closeCts.Cancel();
            try { _signal.Release(); } catch { /* disposed */ }
        }

        /// <summary>清空已缓冲但未消费的字节（连接切换/重连时丢弃上一会话的残留帧，避免帧损坏）。</summary>
        /// <remarks>
        /// 注意：本方法不会回收已通过 <see cref="Write"/> 释放到信号量的许可。已派发的许可会让
        /// <see cref="ReadAsync"/> 在 <c>_count == 0</c> 时仍通过等待，并返回 0 字节——调用方会把它
        /// 当作「读超时/对端关闭」并重试。这与 <see cref="StreamByteSource"/> 在超时时返回 0 的语义
        /// 一致，是可接受的（清空通常发生在连接切换时，下一次 <see cref="ReadAsync"/> 会重新等待
        /// 起始字节）。但若调用方对 0 采取「连接已关闭」的终止动作，则需在 Clear 后让读循环重试。
        /// </remarks>
        public void Clear()
        {
            lock (_lock)
            {
                _head = 0;
                _tail = 0;
                _count = 0;
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
                int toCopy = Math.Min(buffer.Length, _count);
                for (int i = 0; i < toCopy; i++)
                {
                    buffer.Span[i] = _buf[_head];
                    _head = (_head + 1) % _buf.Length;
                    _count--;
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
        public static async ValueTask<int> ReadFrameAsync(IByteSource src, Memory<byte> frame,
            LinkLayerParameters ll, int messageTimeout, int characterTimeout, Action<string> log, CancellationToken ct)
        {
            // 等待帧起始字符
            if (await src.ReadAsync(frame.Slice(0, 1), messageTimeout, ct).ConfigureAwait(false) != 1)
                return 0;

            byte start = frame.Span[0];

            if (start == 0x68)
            {
                if (await src.ReadAsync(frame.Slice(1, 1), characterTimeout, ct).ConfigureAwait(false) != 1)
                {
                    log("RECV: SYNC ERROR reading length byte");
                    return 0;
                }

                int l = frame.Span[1];
                int rest = l + 4; // 从索引 2 起还需读取的字节数

                if (await src.ReadAsync(frame.Slice(2, rest), characterTimeout, ct).ConfigureAwait(false) != rest)
                {
                    log("RECV: Timeout reading variable length frame (l=" + l + ")");
                    return 0;
                }

                return rest + 2; // 含已读的起始(0x68)与长度字节
            }
            else if (start == 0x10)
            {
                int msgSize = 3 + ll.AddressLength;

                if (await src.ReadAsync(frame.Slice(1, msgSize), characterTimeout, ct).ConfigureAwait(false) != msgSize)
                {
                    log("RECV: Timeout reading fixed length frame");
                    return 0;
                }

                return msgSize + 1;
            }
            else if (start == 0xE5)
            {
                return 1;
            }
            else
            {
                log("RECV: SYNC ERROR unexpected start byte " + start);
                return 0;
            }
        }
    }
}
