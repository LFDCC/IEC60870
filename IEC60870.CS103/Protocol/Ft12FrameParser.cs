//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS103;

/// <summary>
/// FT1.2 字节流分帧器：逐字节状态机切帧，处理粘包/半包。
/// </summary>
/// <remarks>
/// <list type="number">
///   <item>定长缓冲 + 索引，分帧路径零堆分配（帧本身除外）。</item>
///   <item><see cref="TryResetStale"/>：半帧超时由调用方复位状态机。
///         否则线路中断后解析器会永久停在 <c>VariableBody</c>，重连后的首帧被吞掉。</item>
///   <item>坏帧通过 <see cref="FrameRejected"/> 上报完整原始字节与原因后整体丢弃。</item>
/// </list>
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
    private const int MaxFrameLength = Ft12Frame.MaxAsduLength + 8;

    private readonly byte[] _buffer = new byte[MaxFrameLength];
    private int _count;
    private ParserState _state = ParserState.WaitStart;
    private int _variableNeeded;
    private long _stateEnteredTicks;

    /// <summary>坏帧被舍弃时触发：(原始十六进制, 原因)。</summary>
    public event Action<string, string>? FrameRejected;

    /// <summary>当前是否处于帧中间。</summary>
    public bool InFrame => _state != ParserState.WaitStart;

    /// <summary>缓冲中已累积但因半帧未成帧的字节数。</summary>
    public int PendingBytes => _count;

    /// <summary>
    /// 若已处于半帧状态超过 <paramref name="characterTimeoutMs"/>，复位状态机并上报一次坏帧。
    /// 建议在每次读取超时/空转时调用。
    /// </summary>
    /// <param name="characterTimeoutMs">帧内字符间隔超时（毫秒）。</param>
    /// <returns>确实复位了半帧返回 true。</returns>
    public bool TryResetStale(int characterTimeoutMs)
    {
        if (_state == ParserState.WaitStart)
        {
            return false;
        }

        if (characterTimeoutMs <= 0)
        {
            return false;
        }

        var elapsedMs = (Environment.TickCount64 - _stateEnteredTicks);
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

    /// <summary>
    /// 喂入字节流，返回本次切出的完整帧（可能 0 到多个）。
    /// </summary>
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
                        frames.Add(new Ft12Frame(Ft12FrameFormat.SingleChar, 0, 0, Ft12Frame.EmptyAsdu));
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
                    if (_count == Ft12Frame.FixedFrameLength)
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
                            if (_buffer[1] < 2 || _variableNeeded > MaxFrameLength)
                            {
                                Reject($"可变帧长度域 L={_buffer[1]} 非法");
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
        // 防御：帧长超出缓冲时立即判坏帧并复位，避免 _count 无界增长
        // 让后续 Flush 用 new ReadOnlySpan(buffer, 0, _count) 抛越界异常。
        // 当前 L≤255 使总长≤261=MaxFrameLength 恒不触发，但常量一旦调整即会击穿接收循环。
        if (_count >= _buffer.Length)
        {
            Reject($"帧超出缓冲（{_buffer.Length} 字节）");
            return;
        }

        _buffer[_count] = b;
        _count++;

        // 关键：超时基准必须是「距最后一个字节的间隔」，不是「从帧开始的总时长」。
        // 否则 9600 波特下 253 字节的合法长帧（约 264ms）会被默认 50ms 的
        // CharacterTimeoutMs 误判为半帧而整体丢弃。每收到一个字节都刷新。
        _stateEnteredTicks = Environment.TickCount64;
    }

    private void Flush(List<Ft12Frame> frames)
    {
        if (Ft12Frame.TryDecode(new ReadOnlySpan<byte>(_buffer, 0, _count), out var frame, out var error) && frame is not null)
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
