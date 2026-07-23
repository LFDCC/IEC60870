/*
 *  PooledApduWriter.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 */

using System;
using System.Buffers;
using IEC60870.Core;


namespace IEC60870.CS104
{
    /// <summary>
    /// 池化 APDU 编码器。继承 <see cref="Frame"/>，可直接复用现有全部
    /// <c>ASDU.Encode(Frame)</c> / <c>InformationObject.Encode(Frame)</c> 逻辑，
    /// 而底层缓冲区改为从 <see cref="ArrayPool{T}"/> 租借——热路径无 per-frame 堆分配。
    /// </summary>
    /// <remarks>
    /// 缓冲区布局：<c>[0..6)</c> 预留给 APCI 头，ASDU 从偏移 6 开始写入。
    /// 编码完成后调用 <see cref="FinishIFormat"/> 回填 APCI 头并取得完整 APDU 视图。
    /// 使用完毕务必 <see cref="Dispose"/> 归还缓冲区。
    /// </remarks>
    public sealed class PooledApduWriter : Frame, IDisposable
    {
        /// <summary>APCI 头占用的偏移（ASDU 起始位置）。</summary>
        public const int HeaderOffset = ApduCodec.ApciLength; // 6

        private byte[] _buffer;
        private int _pos;
        private bool _disposed;

        public PooledApduWriter(int capacity = ApduCodec.MaxApduLength)
        {
            if (capacity < ApduCodec.MaxApduLength)
                capacity = ApduCodec.MaxApduLength;

            _buffer = ArrayPool<byte>.Shared.Rent(capacity);
            _pos = HeaderOffset;
        }

        // ── Frame 抽象实现（语义与 BufferFrame(startPos=6) 一致）──────────

        /// <summary>重置写入位置到 ASDU 起点（保留 APCI 预留区）。</summary>
        public override void ResetFrame() => _pos = HeaderOffset;

        public override void SetNextByte(byte value) => _buffer[_pos++] = value;

        public override void AppendBytes(byte[] bytes)
        {
            Buffer.BlockCopy(bytes, 0, _buffer, _pos, bytes.Length);
            _pos += bytes.Length;
        }

        /// <summary>返回当前绝对写入位置（含 6 字节 APCI 预留区，与 BufferFrame 语义一致）。</summary>
        public override int GetMsgSize() => _pos;

        public override byte[] GetBuffer() => _buffer;

        // ── 0GC APDU 扩展 ────────────────────────────────────────────

        /// <summary>已写入的 ASDU 字节数（不含 APCI 头）。</summary>
        public int AsduLength => _pos - HeaderOffset;

        /// <summary>可继续写入的空闲区（用于 Span 直写）。</summary>
        public Span<byte> WritableSpan => _buffer.AsSpan(_pos);

        /// <summary>Span 直写后推进写入位置。</summary>
        public void Advance(int count) => _pos += count;

        /// <summary>
        /// 在缓冲区头部回填 I-format APCI，返回完整 APDU 的内存视图 <c>[0.._pos)</c>。
        /// 返回值在本对象 <see cref="Dispose"/> 前有效。
        /// </summary>
        public ReadOnlyMemory<byte> FinishIFormat(int sendSeq, int recvSeq)
        {
            ApduCodec.WriteIFormatHeader(_buffer.AsSpan(0, HeaderOffset), sendSeq, recvSeq, AsduLength);
            return new ReadOnlyMemory<byte>(_buffer, 0, _pos);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            byte[] buf = _buffer;
            _buffer = null;
            if (buf != null)
                ArrayPool<byte>.Shared.Return(buf);
        }
    }
}
