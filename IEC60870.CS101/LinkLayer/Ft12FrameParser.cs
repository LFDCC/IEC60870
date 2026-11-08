//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//  协议实现依据：IEC 60870-5-1 (FT1.2)
//------------------------------------------------------------------------------

#nullable enable

namespace IEC60870.CS101;

/// <summary>
/// FT1.2 字节流分帧器：逐字节状态机切帧，处理粘包/半包/线路噪声。
/// </summary>
/// <remarks>
/// <para>
/// 与 103 的 <c>IEC60870.CS103.Ft12FrameParser</c> 行为一致（同样四状态机、
/// 半帧超时复位、噪声字节静默丢弃、坏帧上报），唯一差异是地址域长度
/// 由构造参数给出（101 允许 0/1/2 字节）。
/// </para>
/// <para>
/// <b>为什么不复用 103 的实现</b>：两个协议包相互独立、不共享程序集；
/// 且地址长度语义不同（101 可配、103 固定 1 字节），强行合并需要把
/// 101 的 internal 类型公开化。这里保持"能力对齐、代码各自独立"。
/// </para>
/// </remarks>
public sealed class Ft12FrameParser
{
    private enum ParserState : byte
    {
        WaitStart,
        FixedBody,
        VariableHeader,
        VariableBody,
    }

    /// <summary>可变帧最大总长（L=255 时总长 261）。</summary>
    private const int MaxFrameLength = 261;

    private readonly byte[] _buffer = new byte[MaxFrameLength];
    private readonly int _addressLength;
    private int _count;
    private ParserState _state = ParserState.WaitStart;
    private int _variableNeeded;
    private long _stateEnteredTicks;

    /// <summary>创建分帧器。</summary>
    /// <param name="addressLength">链路地址域长度（0/1/2），须与对端一致。</param>
    public Ft12FrameParser(int addressLength = 1)
    {
        if (!Ft12Frame.IsValidAddressLength(addressLength))
        {
            throw new ArgumentOutOfRangeException(nameof(addressLength), $"地址域长度须为 0/1/2，实际 {addressLength}");
        }

        _addressLength = addressLength;
    }

    /// <summary>地址域长度（0/1/2）。</summary>
    public int AddressLength => _addressLength;

    /// <summary>坏帧被舍弃时触发：(原始十六进制, 原因)。</summary>
    public event Action<string, string>? FrameRejected;

    /// <summary>当前是否处于帧中间。</summary>
    public bool InFrame => _state != ParserState.WaitStart;

    /// <summary>缓冲中已累积但因半帧未成帧的字节数。</summary>
    public int PendingBytes => _count;

    /// <summary>
    /// 若已处于半帧状态超过 <paramref name="characterTimeoutMs"/>，复位状态机并上报一次坏帧。
    /// </summary>
    /// <remarks>
    /// 计时基准是「距最后一个字节的间隔」而非「从帧开始的总时长」——
    /// 否则 9600 波特下 255 字节的合法长帧（约 266ms）会被默认 50ms 的超时误判。
    /// </remarks>
    public bool TryResetStale(int characterTimeoutMs)
    {
        if (_state == ParserState.WaitStart || characterTimeoutMs <= 0)
        {
            return false;
        }

        var elapsedMs = Environment.TickCount64 - _stateEnteredTicks;
        if (elapsedMs < characterTimeoutMs)
        {
            return false;
        }

        Reject($"半帧超时（{elapsedMs}ms 未收齐，已累积 {_count} 字节）");
        return true;
    }

    /// <summary>无条件复位状态机（连接重建时调用，不产生坏帧事件）。</summary>
    public void Reset()
    {
        _count = 0;
        _state = ParserState.WaitStart;
        _variableNeeded = 0;
    }

    /// <summary>喂入字节流，返回本次切出的完整帧（可能 0 到多个）。</summary>
    public List<Ft12Frame> Feed(ReadOnlySpan<byte> bytes)
    {
        var frames = new List<Ft12Frame>();
        foreach (var b in bytes)
        {
            switch (_state)
            {
                case ParserState.WaitStart:
                    if (b == Ft12Frame.SingleAck)
                    {
                        frames.Add(new Ft12Frame(Ft12FrameFormat.SingleChar, 0, 0, 0, Ft12Frame.EmptyAsdu));
                    }
                    else if (b == Ft12Frame.StartFixed)
                    {
                        BeginFrame(b, ParserState.FixedBody);
                    }
                    else if (b == Ft12Frame.StartVariable)
                    {
                        BeginFrame(b, ParserState.VariableHeader);
                    }

                    // 其余字节视为线路噪声丢弃（串口/485 现场常见）
                    break;

                case ParserState.FixedBody:
                    Append(b);
                    // 固定帧：10 C [A..] CS 16 = 4 + AddressLength
                    if (_count == 4 + _addressLength)
                    {
                        Flush(frames);
                    }

                    break;

                case ParserState.VariableHeader:
                    Append(b);
                    if (_count == 4)
                    {
                        // 68 L L 68 —— 长度双写 + 第二启动字符校验
                        if (_buffer[1] == _buffer[2] && _buffer[3] == Ft12Frame.StartVariable)
                        {
                            _variableNeeded = _buffer[1] + 6;
                            var minLength = 1 + _addressLength;
                            if (_buffer[1] < minLength || _variableNeeded > MaxFrameLength)
                            {
                                Reject($"可变帧长度域 L={_buffer[1]} 非法（地址 {_addressLength} 字节）");
                            }
                            else
                            {
                                _state = ParserState.VariableBody;
                                _stateEnteredTicks = Environment.TickCount64;
                            }
                        }
                        else
                        {
                            Reject("可变帧头不一致（L1/L2 或第二启动字符错）");
                        }
                    }

                    break;

                case ParserState.VariableBody:
                    Append(b);
                    if (_count == _variableNeeded)
                    {
                        Flush(frames);
                    }

                    break;
            }
        }

        return frames;
    }

    private void BeginFrame(byte startByte, ParserState next)
    {
        _count = 0;
        _buffer[_count++] = startByte;
        _state = next;
        _stateEnteredTicks = Environment.TickCount64;
    }

    private void Append(byte b)
    {
        if (_count >= _buffer.Length)
        {
            Reject($"帧超出缓冲（{_buffer.Length} 字节）");
            return;
        }

        _buffer[_count++] = b;

        // 超时基准刷新为「距最后一个字节的间隔」
        _stateEnteredTicks = Environment.TickCount64;
    }

    private void Flush(List<Ft12Frame> frames)
    {
        if (Ft12Frame.TryDecode(new ReadOnlySpan<byte>(_buffer, 0, _count), _addressLength, out var frame, out var error)
            && frame is not null)
        {
            frames.Add(frame);
        }
        else
        {
            Reject(error ?? "未知错误");
            return;
        }

        _count = 0;
        _state = ParserState.WaitStart;
    }

    private void Reject(string reason)
    {
        var hex = Convert.ToHexString(new ReadOnlySpan<byte>(_buffer, 0, Math.Min(_count, _buffer.Length)));
        _count = 0;
        _state = ParserState.WaitStart;
        _variableNeeded = 0;
        FrameRejected?.Invoke(hex, reason);
    }
}
