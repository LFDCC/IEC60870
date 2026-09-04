//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 基于调用方字节数组的 Frame 实现（顺序写入、克隆与可写切片）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

namespace IEC60870.Core;

/// <summary>
/// 把帧内容顺序写入外部提供的字节数组的 <see cref="Frame"/> 实现。
/// 不负责数组的生命周期，也不做越界检查——容量由调用方按 <c>MaxAsduLength</c> 预留。
/// </summary>
public class BufferFrame : Frame
{
    private readonly byte[] _buffer;
    private readonly int _startPos;
    private int _bufPos;

    public BufferFrame(byte[] buffer, int startPos)
    {
        _buffer = buffer;
        _startPos = startPos;
        _bufPos = startPos;
    }

    /// <summary>把已写入区间复制为一个独立的新 <see cref="BufferFrame"/>。</summary>
    public BufferFrame Clone()
    {
        var written = _bufPos - _startPos;
        var copy = new byte[GetMsgSize()];
        Array.Copy(_buffer, _startPos, copy, 0, written);

        var clone = new BufferFrame(copy, 0);
        clone._bufPos = written;
        return clone;
    }

    public override void ResetFrame() => _bufPos = _startPos;

    public override void SetNextByte(byte value) => _buffer[_bufPos++] = value;

    public override void AppendBytes(byte[] bytes) => AppendBytes(bytes.AsSpan());

    public override void AppendBytes(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(_buffer.AsSpan(_bufPos));
        _bufPos += bytes.Length;
    }

    /// <summary>当前写入位置（等价于已用字节数，因起始位置通常为 0）。</summary>
    public override int GetMsgSize() => _bufPos;

    public override byte[] GetBuffer() => _buffer;

    /// <summary>从当前写入位置到缓冲末尾的可写切片（AsduWriter 直写适配）。</summary>
    public override Span<byte> GetRemainingSpan() => _buffer.AsSpan(_bufPos);

    /// <summary>推进写入位置（与 <see cref="GetRemainingSpan"/> 配对）。</summary>
    public override void Advance(int count) => _bufPos += count;
}
