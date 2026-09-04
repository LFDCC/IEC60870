//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 应用层帧写入抽象基类（逐字节/批量写入与可写切片适配）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

namespace IEC60870.Core;

/// <summary>
/// 应用层帧的抽象写出目标。派生类决定字节最终落到数组池、网络流还是调用方缓冲区。
/// 逐字节的虚方法保留以兼容既有实现；新代码优先使用 span 重载，
/// 其默认实现退化为逐字节循环，拥有连续缓冲的子类应覆盖为整段拷贝。
/// </summary>
public abstract class Frame
{
    /// <summary>把写入位置复位到帧起始处。</summary>
    public abstract void ResetFrame();

    /// <summary>写入单个字节并推进位置。</summary>
    public abstract void SetNextByte(byte value);

    /// <summary>整段追加字节数组。</summary>
    public abstract void AppendBytes(byte[] bytes);

    /// <summary>追加一段只读字节。默认逐个调用 <c>SetNextByte</c>；直接持有缓冲的子类应覆盖为批量拷贝。</summary>
    public virtual void AppendBytes(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            SetNextByte(value);
        }
    }

    /// <summary>已写入的字节数。</summary>
    public abstract int GetMsgSize();

    /// <summary>帧所依托的底层数组。</summary>
    public abstract byte[] GetBuffer();

    /// <summary>
    /// 从当前写入位置到缓冲末尾的可写切片（<see cref="AsduWriter"/> 直写适配用）。
    /// 默认抛 <see cref="NotSupportedException"/>，拥有连续缓冲的子类应覆盖。
    /// </summary>
    public virtual Span<byte> GetRemainingSpan() => throw new NotSupportedException(
        "此 Frame 实现没有连续可写缓冲,不支持 AsduWriter 直写适配");

    /// <summary>推进写入位置 <paramref name="count"/> 字节（与 <see cref="GetRemainingSpan"/> 配对）。</summary>
    public virtual void Advance(int count) => throw new NotSupportedException(
        "此 Frame 实现没有连续可写缓冲,不支持 AsduWriter 直写适配");
}
