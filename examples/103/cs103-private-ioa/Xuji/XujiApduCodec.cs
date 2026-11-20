//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//
//  本文件是**厂商私有实现**，不属于标准 IEC 60870-5-103，因此放在 examples 而非库里。
//  报文依据：许继 2005 版 103 规约 + 现场 2403 端口抓包（2026-11-19，7 帧）。
//------------------------------------------------------------------------------

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using IEC60870.CS103;

namespace cs103_private_ioa.Xuji;

/// <summary>
/// 许继网口 103 的 ASDU 头部布局（8 字节）。
/// </summary>
/// <remarks>
/// <code>
/// TI(1) | VSQ(1) | COT(2, 小端) | CA(2, 小端) | FUN(1) | INF(1) | 数据
///       偏移:  0       1          2-3            4-5         6       7
/// </code>
/// COT 低字节 <c>[T bit7][P/N bit6][原因 bit5-0]</c>，高字节是源发地址。
/// 现场报文中只有这个 8 字节读法能让 FUN=178 与 INF=100/107/108/109 命中规约表 26~30。
/// </remarks>
public static class XujiApduLayout
{
    /// <summary>许继网口 103 的 8 字节头部布局。</summary>
    /// <remarks>
    /// COT 字段宽 2 字节（104 的布局），但<b>首字节位序是 103 的</b>：
    /// <c>bit7=P/N、bit6-0=原因</c>。规约 COT 表里有 64（调版本号）、66（冻结否定）
    /// 等大于 63 的值，按 104 位序（bit6 作 P/N）会被抹成 0。
    /// </remarks>
    public static readonly Iec103AsduLayout Instance = new Iec103AsduLayout(
        HeaderLength: 8,
        CotOffset: 2,
        CotWidth: 2,
        CaOffset: 4,
        CaWidth: 2,
        FunOffset: 6,
        InfOffset: 7,
        CotNegativeMask: 0x80,
        CotValueMask: 0x7F).Validate();
}

/// <summary>
/// 许继 CA：2 字节，由「公共地址（扇区）」+「装置地址」两段拼成。
/// </summary>
/// <remarks>
/// 依据许继 104 规约 §3.14：<c>COMADDR 低字节 = 公共地址，高字节 = 装置地址</c>。
/// 「装置地址」是保护装置自身的编号，不是 103 串口里的链路地址。
/// CA 按小端写出，故 <c>(Device &lt;&lt; 8) | Sector</c> 上线顺序是「扇区在前」。
/// </remarks>
public readonly struct XujiCa
{
    /// <summary>公共地址（现场称「扇区」），占 CA 低字节。</summary>
    public byte Sector { get; }

    /// <summary>装置地址，占 CA 高字节。</summary>
    public byte Device { get; }

    /// <summary>构造许继 CA。</summary>
    public XujiCa(byte sector, byte device)
    {
        Sector = sector;
        Device = device;
    }

    /// <summary>打包为 ASDU 公共地址（2 字节整数）。</summary>
    public int ToCommonAddress() => (Device << 8) | Sector;

    /// <summary>从 ASDU 公共地址还原「扇区 / 装置地址」。</summary>
    public static XujiCa Parse(int commonAddress)
        => new((byte)(commonAddress & 0xFF), (byte)((commonAddress >> 8) & 0xFF));

    /// <inheritdoc />
    public override string ToString()
        => $"扇区(公共地址)={Sector} 装置地址={Device} (CA=0x{ToCommonAddress():X4})";
}

/// <summary>
/// 许继网口的 APCI 帧：<c>68 | L(1B) | 控制域4B | ASDU</c>。
/// </summary>
/// <remarks>
/// <c>L = 4 + ASDU</c>，总长 <c>2 + L</c>。I 帧承载 ASDU；
/// U 帧用于 STARTDT 握手（装置连上即发 <c>68 04 07 00 00 00</c>）。
/// 无 k-w 窗口、无 S 帧确认。
/// ASDU 里 104 的 <c>IOA(3)</c> 被换成 103 的 <c>FUN(1) INF(1)</c>。
/// </remarks>
public sealed record XujiApduFrame(int SendSequence, int ReceiveSequence, byte[] Asdu)
{
    /// <summary>该帧是否为 U/S 控制帧（无 ASDU）。</summary>
    public bool IsControlFrame { get; init; }

    /// <summary>
    /// 控制帧的功能码。U 帧取控制域首字节（<c>0x07</c> STARTDT_ACT / <c>0x0B</c> STARTDT_CON 等）；
    /// S 帧该字节恒为 <c>0x01</c>，其含义由 <see cref="IsSFrame"/> 区分。
    /// </summary>
    public byte ControlFunction { get; init; }

    /// <summary>是否为 S 帧（仅确认接收序号，按 104 无需应答）。</summary>
    public bool IsSFrame => IsControlFrame && ControlFunction == 0x01;

    /// <summary>起始字符 <c>68H</c>。</summary>
    public const byte Start = 0x68;

    /// <summary>控制域长度。</summary>
    public const int ControlLength = 4;

    /// <summary>APCI 总长度（起始 1 + 长度域 1 + 控制域 4）。</summary>
    public const int ApciLength = 6;

    /// <summary>ASDU 最大长度（L 为 1 字节：255 - 4 = 251）。</summary>
    public const int MaxAsduLength = 251;

    /// <summary>该帧编码后的线路字节数。</summary>
    public int GetEncodedLength() => ApciLength + Asdu.Length;

    /// <summary>编码为线路字节。</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[GetEncodedLength()];
        bytes[0] = Start;
        bytes[1] = (byte)(ControlLength + Asdu.Length);

        // I 帧：bit0=0，N(S)/N(R) 各 15 位、低字节在前
        bytes[2] = (byte)((SendSequence & 0x7F) << 1);
        bytes[3] = (byte)(SendSequence >> 7);
        bytes[4] = (byte)((ReceiveSequence & 0x7F) << 1);
        bytes[5] = (byte)(ReceiveSequence >> 7);

        Asdu.CopyTo(bytes, ApciLength);
        return bytes;
    }

    /// <summary>解码一个完整帧。</summary>
    public static bool TryDecode(ReadOnlySpan<byte> bytes, out XujiApduFrame? frame, out string? error)
    {
        frame = null;
        error = null;

        if (bytes.Length < ApciLength)
        {
            error = $"APDU 至少 {ApciLength} 字节，实际 {bytes.Length}";
            return false;
        }

        if (bytes[0] != Start)
        {
            error = $"APDU 起始字符应为 68H，实际 0x{bytes[0]:X2}";
            return false;
        }

        var length = bytes[1];
        if (length < ControlLength)
        {
            error = $"APDU 长度域 L={length} 小于控制域长度 {ControlLength}";
            return false;
        }

        var expectedTotal = 2 + length;
        if (bytes.Length != expectedTotal)
        {
            error = $"APDU 总长应为 {expectedTotal}（L={length}），实际 {bytes.Length}";
            return false;
        }

        var control = bytes.Slice(2, ControlLength);

        // S 帧 / U 帧：无 ASDU，不需要应用层处理
        if ((control[0] & 0x01) != 0)
        {
            frame = new XujiApduFrame(0, 0, Array.Empty<byte>())
            {
                IsControlFrame = true,
                ControlFunction = control[0],
            };
            return true;
        }

        var ns = ((control[0] & 0xFE) >> 1) | (control[1] << 7);
        var nr = ((control[2] & 0xFE) >> 1) | (control[3] << 7);

        var asduLength = length - ControlLength;
        var asdu = asduLength > 0 ? bytes.Slice(ApciLength, asduLength).ToArray() : Array.Empty<byte>();

        frame = new XujiApduFrame(ns, nr, asdu);
        return true;
    }

    /// <summary>构造 U 帧（STARTDT / STOPDT / TESTFR）。</summary>
    public static byte[] EncodeUFormat(byte function) => new byte[] { Start, 0x04, function, 0x00, 0x00, 0x00 };

    /// <summary>诊断用描述。</summary>
    public override string ToString() => IsSFrame
        ? $"S帧 NR={ReceiveSequence}"
        : IsControlFrame
            ? $"U帧 0x{ControlFunction:X2}"
            : $"I帧 NS={SendSequence} NR={ReceiveSequence} ASDU={Asdu.Length}B";
}

/// <summary>
/// 许继网口 103 的帧编解码器：<c>68 | L(1B) | 控制域4B | ASDU</c>。
/// </summary>
/// <remarks>
/// 作为 <see cref="IIec103FramingCodec"/> 的厂商实现放在 examples 里，
/// 库本身只保留标准 FT1.2。用法：
/// <code>
/// var options = new Iec103Options()
///     .UseLinkParameters(l => l.FramingCodec = new XujiApduCodec());
/// </code>
/// 收到 STARTDT_ACT 时自动回 STARTDT_CON（装置连上即握手）。
/// </remarks>
public sealed class XujiApduCodec : IIec103FramingCodec
{
    private readonly List<byte> _buffer = new();
    private long _stateEnteredTicks;

    /// <summary>STARTDT_ACT。</summary>
    public const byte StartDtAct = 0x07;

    /// <summary>STARTDT_CON。</summary>
    public const byte StartDtCon = 0x0B;

    /// <summary>最大帧长（L=255 时总长 257）。</summary>
    private const int MaxFrameLength = 2 + 255;

    /// <inheritdoc />
    public string Name => "XujiApdu";

    /// <inheritdoc />
    public Iec103AsduLayout Layout => XujiApduLayout.Instance;

    /// <inheritdoc />
    public event Action<string, string>? FrameRejected;

    /// <summary>需要回送的控制帧（如对 STARTDT_ACT 回 STARTDT_CON）；无则 null。</summary>
    public event Action<byte[]>? ControlFrameResponse;

    /// <summary>收到的控制帧（U 帧 / S 帧）原始字节。不产生 ASDU，仅用于诊断抓包。</summary>
    public event Action<byte[]>? ControlFrameReceived;

    /// <inheritdoc />
    public IReadOnlyList<Iec103IncomingFrame> Feed(ReadOnlySpan<byte> bytes)
    {
        var frames = new List<Iec103IncomingFrame>();

        foreach (var b in bytes)
        {
            // 等待起始字符：丢弃噪声
            if (_buffer.Count == 0)
            {
                if (b == XujiApduFrame.Start)
                {
                    _buffer.Add(b);
                    _stateEnteredTicks = Environment.TickCount64;
                }

                continue;
            }

            _buffer.Add(b);
            _stateEnteredTicks = Environment.TickCount64;

            // 第 2 字节是长度域
            if (_buffer.Count == 2 && b < XujiApduFrame.ControlLength)
            {
                Reject($"APDU 长度域 L={b} 小于最小值 {XujiApduFrame.ControlLength}");
                continue;
            }

            if (_buffer.Count >= 2)
            {
                var needed = 2 + _buffer[1];
                if (_buffer.Count == needed)
                {
                    Flush(frames);
                }
                else if (_buffer.Count > needed || _buffer.Count > MaxFrameLength)
                {
                    Reject($"APDU 帧超长（L={_buffer[1]}）");
                }
            }
        }

        return frames;
    }

    /// <inheritdoc />
    public byte[] Encode(ReadOnlySpan<byte> asdu)
    {
        if (asdu.Length > XujiApduFrame.MaxAsduLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(asdu), $"ASDU {asdu.Length} 字节超过 APDU 上限 {XujiApduFrame.MaxAsduLength}");
        }

        // 现场厂家不维护可依赖的序号（执行帧发 N(S)=0/N(R)=0），这里统一填 0 即可
        return new XujiApduFrame(0, 0, asdu.ToArray()).ToBytes();
    }

    /// <inheritdoc />
    public void Reset() => _buffer.Clear();

    /// <inheritdoc />
    public bool TryResetStale(int timeoutMilliseconds)
    {
        if (_buffer.Count == 0 || timeoutMilliseconds <= 0)
        {
            return false;
        }

        var elapsedMs = Environment.TickCount64 - _stateEnteredTicks;
        if (elapsedMs < timeoutMilliseconds)
        {
            return false;
        }

        Reject($"半帧超时（{elapsedMs}ms 未收齐，已累积 {_buffer.Count} 字节）");
        return true;
    }

    private void Flush(List<Iec103IncomingFrame> frames)
    {
        var raw = _buffer.ToArray();

        if (!XujiApduFrame.TryDecode(raw, out var frame, out var error) || frame is null)
        {
            Reject(error ?? "未知错误");
            return;
        }

        if (frame.IsControlFrame)
        {
            ControlFrameReceived?.Invoke(raw);

            // STARTDT_ACT → 回 STARTDT_CON；其余控制帧只记录
            var reply = frame.ControlFunction switch
            {
                StartDtAct => XujiApduFrame.EncodeUFormat(StartDtCon),
                _ => null,
            };

            if (reply is not null)
            {
                ControlFrameResponse?.Invoke(reply);
            }

            _buffer.Clear();
            return;
        }

        frames.Add(new Iec103IncomingFrame(raw, frame.Asdu));
        _buffer.Clear();
    }

    private void Reject(string reason)
    {
        var hex = Convert.ToHexString(_buffer.ToArray());
        _buffer.Clear();
        FrameRejected?.Invoke(hex, reason);
    }
}
