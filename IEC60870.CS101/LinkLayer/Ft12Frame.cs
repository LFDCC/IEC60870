//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//  协议实现依据：IEC 60870-5-1 (FT1.2) / IEC 60870-5-2
//------------------------------------------------------------------------------

#nullable enable

namespace IEC60870.CS101;

/// <summary>FT1.2 帧格式（IEC 60870-5-1）。</summary>
public enum Ft12FrameFormat : byte
{
    /// <summary>固定帧长：<c>10 C [A] CS 16</c>。</summary>
    FixedLength = 0,

    /// <summary>可变帧长：<c>68 L L 68 C [A] ASDU CS 16</c>。</summary>
    VariableLength = 1,

    /// <summary>单字符确认 <c>E5</c>。</summary>
    SingleChar = 2,
}

/// <summary>
/// IEC 60870-5-1 FT1.2 帧（101/103 共用同一套字节格式）。
/// </summary>
/// <remarks>
/// <para>帧布局：</para>
/// <list type="bullet">
///   <item>固定帧：<c>10 C [A(0/1/2)] CS 16</c>，<c>CS</c> = C 与地址域各字节之和 mod 256。</item>
///   <item>可变帧：<c>68 L L 68 C [A(0/1/2)] ASDU CS 16</c>，
///         <c>L = 1 + AddressLength + ASDU.Length</c>（长度双写），
///         <c>CS</c> 为自 C 起至 ASDU 末的算术和 mod 256，总长 <c>L + 6</c>。</item>
///   <item>单字符：<c>E5</c>。</item>
/// </list>
/// <para>
/// <b>与 103 的差异</b>：本类型把地址域长度 <see cref="AddressLength"/> 参数化（0/1/2 字节），
/// 而 103 的 <c>IEC60870.CS103.Ft12Frame</c> 固定 1 字节。101 平衡模式允许 0/2 字节地址，
/// 因此不能共用同一份硬编码实现。
/// </para>
/// </remarks>
public sealed record Ft12Frame(
    Ft12FrameFormat Format,
    byte Control,
    int Address,
    int AddressLength,
    byte[] Asdu)
{
    /// <summary>固定帧起始字符 <c>10H</c>。</summary>
    public const byte StartFixed = 0x10;

    /// <summary>可变帧起始字符 <c>68H</c>。</summary>
    public const byte StartVariable = 0x68;

    /// <summary>帧结束字符 <c>16H</c>。</summary>
    public const byte End = 0x16;

    /// <summary>单字符确认 <c>E5H</c>。</summary>
    public const byte SingleAck = 0xE5;

    /// <summary>空 ASDU（固定帧与单字符帧共用）。</summary>
    public static readonly byte[] EmptyAsdu = Array.Empty<byte>();

    /// <summary>该帧编码后的线路字节数。</summary>
    public int GetEncodedLength() => Format switch
    {
        Ft12FrameFormat.FixedLength => 4 + AddressLength,
        Ft12FrameFormat.VariableLength => Asdu.Length + AddressLength + 7,
        Ft12FrameFormat.SingleChar => 1,
        _ => 1,
    };

    /// <summary>该地址长度下可变帧的 ASDU 上限（L ≤ 255）。</summary>
    public static int MaxAsduLength(int addressLength) => 253 - addressLength;

    /// <summary>编码为线路字节。</summary>
    public byte[] ToBytes()
    {
        var buffer = new byte[GetEncodedLength()];
        WriteTo(buffer);
        return buffer;
    }

    /// <summary>编码到目标 span，返回写入长度。</summary>
    public int WriteTo(Span<byte> destination) => Format switch
    {
        Ft12FrameFormat.FixedLength => EncodeFixedTo(destination, Control, Address, AddressLength),
        Ft12FrameFormat.VariableLength => EncodeVariableTo(destination, Control, Address, AddressLength, Asdu),
        Ft12FrameFormat.SingleChar => WriteSingleChar(destination),
        _ => throw new InvalidOperationException($"未知帧格式 {Format}"),
    };

    /// <summary>编码固定帧，返回写入长度。</summary>
    public static int EncodeFixedTo(Span<byte> destination, byte control, int address, int addressLength)
    {
        ValidateAddressLength(addressLength);
        var total = 4 + addressLength;
        if (destination.Length < total)
        {
            throw new ArgumentException($"目标缓冲至少需要 {total} 字节", nameof(destination));
        }

        var pos = 0;
        destination[pos++] = StartFixed;
        destination[pos++] = control;

        var checksum = control;
        for (var i = 0; i < addressLength; i++)
        {
            var b = (byte)((address >> (8 * i)) & 0xFF);
            destination[pos++] = b;
            checksum += b;
        }

        destination[pos++] = checksum;
        destination[pos++] = End;
        return pos;
    }

    /// <summary>编码可变帧，返回写入长度。</summary>
    public static int EncodeVariableTo(Span<byte> destination, byte control, int address, int addressLength, ReadOnlySpan<byte> asdu)
    {
        ValidateAddressLength(addressLength);

        var maxAsdu = MaxAsduLength(addressLength);
        if (asdu.Length > maxAsdu)
        {
            throw new ArgumentOutOfRangeException(nameof(asdu),
                $"{addressLength} 字节地址下用户数据上限为 {maxAsdu}，实际 {asdu.Length}");
        }

        // 68 L L 68 C [A..] ASDU CS 16
        // = 4(68 L L 68) + 1(C) + addressLength + asdu.Length + 2(CS 16)
        var total = asdu.Length + addressLength + 7;
        if (destination.Length < total)
        {
            throw new ArgumentException($"目标缓冲至少需要 {total} 字节", nameof(destination));
        }

        var length = (byte)(1 + addressLength + asdu.Length);

        destination[0] = StartVariable;
        destination[1] = length;
        destination[2] = length;
        destination[3] = StartVariable;
        destination[4] = control;

        var pos = 5;
        var checksum = control;
        for (var i = 0; i < addressLength; i++)
        {
            var b = (byte)((address >> (8 * i)) & 0xFF);
            destination[pos++] = b;
            checksum += b;
        }

        foreach (var b in asdu)
        {
            destination[pos++] = b;
            checksum += b;
        }

        destination[pos++] = checksum;
        destination[pos++] = End;
        return pos;
    }

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
    /// 解码完整帧。<paramref name="addressLength"/> 必须与发送方设置一致，
    /// 解码器无法从帧本身推断地址域长度。
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, int addressLength, out Ft12Frame? frame, out string? error)
    {
        frame = null;
        error = null;

        if (!IsValidAddressLength(addressLength))
        {
            error = $"地址域长度须为 0/1/2，实际 {addressLength}";
            return false;
        }

        if (bytes.IsEmpty)
        {
            error = "空帧";
            return false;
        }

        if (bytes.Length == 1)
        {
            if (bytes[0] == SingleAck)
            {
                frame = new Ft12Frame(Ft12FrameFormat.SingleChar, 0, 0, 0, EmptyAsdu);
                return true;
            }

            error = $"无法识别的单字节 0x{bytes[0]:X2}";
            return false;
        }

        if (bytes[0] == StartFixed)
        {
            return TryDecodeFixed(bytes, addressLength, out frame, out error);
        }

        if (bytes[0] == StartVariable)
        {
            return TryDecodeVariable(bytes, addressLength, out frame, out error);
        }

        error = $"未知启动字符 0x{bytes[0]:X2}";
        return false;
    }

    private static bool TryDecodeFixed(ReadOnlySpan<byte> bytes, int addressLength, out Ft12Frame? frame, out string? error)
    {
        frame = null;
        error = null;

        var expected = 4 + addressLength;
        if (bytes.Length != expected)
        {
            error = $"固定帧长应为 {expected} 字节（地址 {addressLength}），实际 {bytes.Length}";
            return false;
        }

        if (bytes[^1] != End)
        {
            error = $"固定帧结束符应为 16H，实际 0x{bytes[^1]:X2}";
            return false;
        }

        var control = bytes[1];
        var checksum = bytes[^2];
        var calculated = control;
        var address = 0;
        for (var i = 0; i < addressLength; i++)
        {
            var b = bytes[2 + i];
            address |= b << (8 * i);
            calculated += b;
        }

        if (checksum != (byte)calculated)
        {
            error = $"固定帧校验和错：期望 0x{(byte)calculated:X2}，实际 0x{checksum:X2}";
            return false;
        }

        frame = new Ft12Frame(Ft12FrameFormat.FixedLength, control, address, addressLength, EmptyAsdu);
        return true;
    }

    private static bool TryDecodeVariable(ReadOnlySpan<byte> bytes, int addressLength, out Ft12Frame? frame, out string? error)
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

        // L = C + A + ASDU，最小 1 + AddressLength
        var minLength = 1 + addressLength;
        if (length < minLength)
        {
            error = $"可变帧长度域 L={length} 小于最小值 {minLength}（地址 {addressLength} 字节）";
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

        var control = bytes[4];
        var checksum = bytes[^2];
        var calculated = control;
        var address = 0;
        var pos = 5;
        for (var i = 0; i < addressLength; i++)
        {
            var b = bytes[pos++];
            address |= b << (8 * i);
            calculated += b;
        }

        var asduEnd = 4 + length;
        for (var i = pos; i < asduEnd; i++)
        {
            calculated += bytes[i];
        }

        if (checksum != (byte)calculated)
        {
            error = $"可变帧校验和错：期望 0x{(byte)calculated:X2}，实际 0x{checksum:X2}";
            return false;
        }

        var asdu = bytes.Slice(pos, asduEnd - pos).ToArray();
        frame = new Ft12Frame(Ft12FrameFormat.VariableLength, control, address, addressLength, asdu);
        return true;
    }

    /// <summary>地址域长度是否合法（0/1/2）。</summary>
    public static bool IsValidAddressLength(int addressLength) => addressLength is >= 0 and <= 2;

    private static void ValidateAddressLength(int addressLength)
    {
        if (!IsValidAddressLength(addressLength))
        {
            throw new ArgumentOutOfRangeException(nameof(addressLength), $"地址域长度须为 0/1/2，实际 {addressLength}");
        }
    }
}
