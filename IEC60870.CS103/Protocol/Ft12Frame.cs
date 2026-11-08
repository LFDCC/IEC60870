//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//  协议实现依据：IEC 60870-5-1/-2 (FT1.2) 与 DL/T 667-1999 (IEC 60870-5-103)
//------------------------------------------------------------------------------

namespace IEC60870.CS103;

/// <summary>
/// FT1.2 帧格式（IEC 60870-5-1/-2）：
/// 固定帧、可变帧长、单字符三种。
/// </summary>
public enum Ft12FrameFormat : byte
{
    /// <summary>固定帧长：<c>10 C A CS 16</c>（复位链路、确认、无所请求数据等）。</summary>
    FixedLength = 0,

    /// <summary>可变帧长：<c>68 L L 68 C A ASDU CS 16</c>（L = C+A+ASDU 字节数）。</summary>
    VariableLength = 1,

    /// <summary>单字符确认 <c>E5</c>。</summary>
    SingleChar = 2,
}

/// <summary>
/// IEC 60870-5-103 链路层帧（FT1.2）。
/// </summary>
/// <remarks>
/// 帧布局（DL/T 667-1999 / IEC 60870-5-1）：
/// <list type="bullet">
///   <item>固定帧：<c>10 C A CS 16</c>，5 字节，<c>CS=(C+A) mod 256</c>。</item>
///   <item>可变帧：<c>68 L L 68 C A &lt;ASDU&gt; CS 16</c>，<c>L=C+A+ASDU</c>（长度双写），
///         <c>CS</c> 为自 C 起至 ASDU 末的算术和 mod 256，总长 <c>L+6</c>。</item>
///   <item>单字符：<c>E5</c>。</item>
/// </list>
/// 校验失败/长度不一致的帧整体无效并舍弃（标准规定）。
/// </remarks>
public sealed record Ft12Frame(Ft12FrameFormat Format, byte Control, byte Address, byte[] Asdu)
{
    /// <summary>固定帧起始字符 <c>10H</c>。</summary>
    public const byte StartFixed = 0x10;

    /// <summary>可变帧起始字符 <c>68H</c>。</summary>
    public const byte StartVariable = 0x68;

    /// <summary>帧结束字符 <c>16H</c>。</summary>
    public const byte End = 0x16;

    /// <summary>单字符确认 <c>E5H</c>。</summary>
    public const byte SingleAck = 0xE5;

    /// <summary>链路层最大用户数据长度（L ≤ 255，其中 C/A 各占 1 字节）。</summary>
    public const int MaxAsduLength = 253;

    /// <summary>固定帧总长。</summary>
    public const int FixedFrameLength = 5;

    /// <summary>空 ASDU（固定帧与单字符帧共用，避免重复分配）。</summary>
    public static readonly byte[] EmptyAsdu = Array.Empty<byte>();

    /// <summary>编码固定帧（5 字节）。</summary>
    public static byte[] EncodeFixed(byte control, byte address)
    {
        var frame = new byte[FixedFrameLength];
        EncodeFixedTo(frame, control, address);
        return frame;
    }

    /// <summary>编码单字符确认 <c>E5</c>。</summary>
    public static byte[] EncodeSingleChar() => new[] { SingleAck };

    /// <summary>编码可变帧。<paramref name="asdu"/> 超过 <see cref="MaxAsduLength"/> 时抛异常。</summary>
    public static byte[] EncodeVariable(byte control, byte address, ReadOnlySpan<byte> asdu)
    {
        var frame = new byte[asdu.Length + 8];
        EncodeVariableTo(frame, control, address, asdu);
        return frame;
    }

    /// <summary>把固定帧写入目标 span（至少 5 字节），返回写入长度。</summary>
    public static int EncodeFixedTo(Span<byte> destination, byte control, byte address)
    {
        if (destination.Length < FixedFrameLength)
        {
            throw new ArgumentException($"目标缓冲至少需要 {FixedFrameLength} 字节", nameof(destination));
        }

        destination[0] = StartFixed;
        destination[1] = control;
        destination[2] = address;
        destination[3] = (byte)(control + address);
        destination[4] = End;
        return FixedFrameLength;
    }

    /// <summary>把可变帧写入目标 span（至少 <c>asdu.Length + 8</c> 字节），返回写入长度。</summary>
    public static int EncodeVariableTo(Span<byte> destination, byte control, byte address, ReadOnlySpan<byte> asdu)
    {
        if (asdu.Length > MaxAsduLength)
        {
            throw new ArgumentOutOfRangeException(nameof(asdu), $"用户数据 {asdu.Length} 字节超过 FT1.2 上限 {MaxAsduLength}");
        }

        var total = asdu.Length + 8;
        if (destination.Length < total)
        {
            throw new ArgumentException($"目标缓冲至少需要 {total} 字节", nameof(destination));
        }

        var length = (byte)(asdu.Length + 2);
        var checksum = (byte)(control + address);
        foreach (var b in asdu)
        {
            checksum += b;
        }

        destination[0] = StartVariable;
        destination[1] = length;
        destination[2] = length;
        destination[3] = StartVariable;
        destination[4] = control;
        destination[5] = address;
        asdu.CopyTo(destination.Slice(6));
        destination[total - 2] = checksum;
        destination[total - 1] = End;
        return total;
    }

    /// <summary>该帧编码后的线路字节数。</summary>
    public int GetEncodedLength() => Format switch
    {
        Ft12FrameFormat.FixedLength => FixedFrameLength,
        Ft12FrameFormat.VariableLength => Asdu.Length + 8,
        Ft12FrameFormat.SingleChar => 1,
        _ => 1,
    };

    /// <summary>编码为线路字节。单字符帧不含控制域/地址。</summary>
    public byte[] ToBytes() => Format switch
    {
        Ft12FrameFormat.FixedLength => EncodeFixed(Control, Address),
        Ft12FrameFormat.VariableLength => EncodeVariable(Control, Address, Asdu),
        Ft12FrameFormat.SingleChar => EncodeSingleChar(),
        _ => throw new InvalidOperationException($"未知帧格式 {Format}"),
    };

    /// <summary>编码到目标 span，返回写入长度。</summary>
    public int WriteTo(Span<byte> destination) => Format switch
    {
        Ft12FrameFormat.FixedLength => EncodeFixedTo(destination, Control, Address),
        Ft12FrameFormat.VariableLength => EncodeVariableTo(destination, Control, Address, Asdu),
        Ft12FrameFormat.SingleChar => WriteSingleChar(destination),
        _ => throw new InvalidOperationException($"未知帧格式 {Format}"),
    };

    private static int WriteSingleChar(Span<byte> destination)
    {
        if (destination.Length < 1)
        {
            throw new ArgumentException("目标缓冲至少需要 1 字节", nameof(destination));
        }

        destination[0] = SingleAck;
        return 1;
    }

    /// <summary>
    /// 解码一个完整帧。返回 false 时 <paramref name="error"/> 说明原因
    /// （校验和错、长度不一致、结束符错等）。
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out Ft12Frame? frame, out string? error)
    {
        frame = null;
        error = null;

        if (bytes.IsEmpty)
        {
            error = "空帧";
            return false;
        }

        if (bytes.Length == 1)
        {
            if (bytes[0] == SingleAck)
            {
                frame = new Ft12Frame(Ft12FrameFormat.SingleChar, 0, 0, EmptyAsdu);
                return true;
            }

            error = $"无法识别的单字节 0x{bytes[0]:X2}";
            return false;
        }

        if (bytes[0] == StartFixed)
        {
            return TryDecodeFixed(bytes, out frame, out error);
        }

        if (bytes[0] == StartVariable)
        {
            return TryDecodeVariable(bytes, out frame, out error);
        }

        error = $"未知启动字符 0x{bytes[0]:X2}";
        return false;
    }

    private static bool TryDecodeFixed(ReadOnlySpan<byte> bytes, out Ft12Frame? frame, out string? error)
    {
        frame = null;
        error = null;

        if (bytes.Length != FixedFrameLength)
        {
            error = $"固定帧长应为 {FixedFrameLength} 字节，实际 {bytes.Length}";
            return false;
        }

        var control = bytes[1];
        var address = bytes[2];
        var checksum = bytes[3];

        if (bytes[4] != End)
        {
            error = $"固定帧结束符应为 16H，实际 0x{bytes[4]:X2}";
            return false;
        }

        var expected = (byte)(control + address);
        if (checksum != expected)
        {
            error = $"固定帧校验和错：期望 0x{expected:X2}，实际 0x{checksum:X2}";
            return false;
        }

        frame = new Ft12Frame(Ft12FrameFormat.FixedLength, control, address, EmptyAsdu);
        return true;
    }

    private static bool TryDecodeVariable(ReadOnlySpan<byte> bytes, out Ft12Frame? frame, out string? error)
    {
        frame = null;
        error = null;

        if (bytes.Length < 6)
        {
            error = $"可变帧过短：{bytes.Length} 字节";
            return false;
        }

        var length = bytes[1];
        if (length != bytes[2])
        {
            error = $"可变帧两个长度字节不一致：{length} != {bytes[2]}";
            return false;
        }

        // L = C + A + ASDU，最小 2（C/A 各 1，ASDU 空）；L<2 会令切片长度为负
        if (length < 2)
        {
            error = $"可变帧长度域 L={length} 小于最小值 2";
            return false;
        }

        if (bytes[3] != StartVariable)
        {
            error = $"可变帧第二启动字符应为 68H，实际 0x{bytes[3]:X2}";
            return false;
        }

        var expectedTotal = length + 6;
        if (bytes.Length != expectedTotal)
        {
            error = $"可变帧总长应为 {expectedTotal}（L={length}），实际 {bytes.Length}";
            return false;
        }

        if (bytes[^1] != End)
        {
            error = $"可变帧结束符应为 16H，实际 0x{bytes[^1]:X2}";
            return false;
        }

        var checksum = bytes[^2];
        var calculated = 0;
        for (var i = 4; i < 4 + length; i++)
        {
            calculated += bytes[i];
        }

        if (checksum != (byte)calculated)
        {
            error = $"可变帧校验和错：期望 0x{(byte)calculated:X2}，实际 0x{checksum:X2}";
            return false;
        }

        var asdu = bytes.Slice(6, length - 2).ToArray();
        frame = new Ft12Frame(Ft12FrameFormat.VariableLength, bytes[4], bytes[5], asdu);
        return true;
    }
}
