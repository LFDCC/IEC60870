//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;

namespace IEC60870.Core;

    /// <summary>
    /// 直接叠加在 <see cref="Memory{T}"/> 上的编码帧,配合 <c>ValueByteBlock</c> 的
    /// 预留-回填构建模式:发送路径先从写入器预留整段内存,再以本帧为编码目标
    /// 复用全部既有 <c>InformationObject.Encode(Frame,...)</c> 逻辑,一次成型、无中间拷贝。
    /// </summary>
    /// <remarks>
    /// 帧头(如 IEC104 的 6 字节 APCI)通过 <paramref name="startPos"/> 预留,
    /// 编码完成后由调用方在预留区回填控制字段。
    /// </remarks>
    public sealed class MemoryFrame : Frame
    {
        private readonly Memory<byte> _memory;
        private readonly int _startPos;
        private int _pos;

        /// <summary>
        /// 在内存块上创建编码帧,从 <paramref name="startPos"/> 开始写入。
        /// </summary>
        /// <param name="memory">目标内存(须为可写,数组/池化缓冲底层)。</param>
        /// <param name="startPos">写入起始偏移(用于预留帧头区域)。</param>
        public MemoryFrame(Memory<byte> memory, int startPos = 0)
        {
            if (startPos < 0 || startPos > memory.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(startPos));
        }

        _memory = memory;
            _startPos = startPos;
            _pos = startPos;
        }

        /// <summary>重置写入位置到初始 startPos。</summary>
        public override void ResetFrame() => _pos = _startPos;

        public override void SetNextByte(byte value)
        {
            _memory.Span[_pos++] = value;
        }

        public override void AppendBytes(byte[] bytes)
            => AppendBytes(bytes.AsSpan());

        public override void AppendBytes(ReadOnlySpan<byte> bytes)
        {
            bytes.CopyTo(_memory.Span.Slice(_pos));
            _pos += bytes.Length;
        }

        /// <summary>当前绝对写入位置(含预留的帧头区)。</summary>
        public override int GetMsgSize() => _pos;

        /// <summary>
        /// 本帧不拥有缓冲,不支持取出数组(编码产物留在底层 Memory 中,
        /// 由调用方通过其 Memory/Span 视图发送)。
        /// </summary>
        public override byte[] GetBuffer() => throw new NotSupportedException(
            "MemoryFrame 编码产物保留在底层 Memory 中,请直接使用其 Span/ Memory 视图");

        /// <summary>从当前写入位置到缓冲末尾的可写切片(AsduWriter 直写适配)。</summary>
        public override Span<byte> GetRemainingSpan() => _memory.Span.Slice(_pos);

        /// <summary>推进写入位置(与 <see cref="GetRemainingSpan"/> 配对)。</summary>
        public override void Advance(int count) => _pos += count;
    }
