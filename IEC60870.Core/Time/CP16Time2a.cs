//------------------------------------------------------------------------------
//  IEC60870.Core.NET — CP16Time2a 二进制时间间隔（2 字节毫秒计数）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

namespace IEC60870.Core;

/// <summary>
/// CP16Time2a：2 字节小端无符号毫秒计数（IEC 60870-5-4 §5.13），
/// 表示经过的时间，范围 0…65535 ms。
/// </summary>
public class CP16Time2a
{
    private readonly byte[] _encodedValue = new byte[2];

    /// <summary>从报文切片解析 2 字节编码。</summary>
    /// <exception cref="ASDUParsingException">剩余长度不足 2 字节时抛出。</exception>
    public CP16Time2a(ReadOnlySpan<byte> msg, int startIndex)
    {
        if (msg.Length < startIndex + 2)
        {
            throw new ASDUParsingException("报文长度不足以解析 CP16Time2a");
        }

        msg.Slice(startIndex, 2).CopyTo(_encodedValue);
    }

    /// <summary>以经过毫秒数构造（超出 16 位范围按低 16 位截断）。</summary>
    public CP16Time2a(int elapsedTimeInMs) => ElapsedTimeInMs = elapsedTimeInMs;

    /// <summary>构造 0 ms 时间间隔。</summary>
    public CP16Time2a()
    {
    }

    /// <summary>复制构造。</summary>
    public CP16Time2a(CP16Time2a original)
    {
        original._encodedValue.CopyTo(_encodedValue, 0);
    }

    /// <inheritdoc/>
    public override bool Equals(object obj) => obj is CP16Time2a other && GetHashCode() == other.GetHashCode();

    /// <inheritdoc/>
    public override int GetHashCode() => new System.Numerics.BigInteger(_encodedValue).GetHashCode();

    /// <summary>经过时间（毫秒，0…65535），小端 2 字节。</summary>
    public int ElapsedTimeInMs
    {
        get => _encodedValue[0] | (_encodedValue[1] << 8);
        set
        {
            _encodedValue[0] = (byte)value;
            _encodedValue[1] = (byte)(value >> 8);
        }
    }

    /// <summary>内部编码数组引用（勿修改）。</summary>
    public byte[] GetEncodedValue() => _encodedValue;

    /// <summary>零分配编码切片。</summary>
    public ReadOnlySpan<byte> AsSpan() => _encodedValue.AsSpan();

    /// <summary>将 2 字节编码写入目标缓冲。</summary>
    public void WriteTo(Span<byte> destination) => _encodedValue.AsSpan().CopyTo(destination);

    /// <inheritdoc/>
    public override string ToString() => ElapsedTimeInMs.ToString();
}
