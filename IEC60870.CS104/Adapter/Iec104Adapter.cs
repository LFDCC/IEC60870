//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS104;

/// <summary>
/// IEC 60870-5-104 数据处理适配器：基于 TouchSocket 定长头适配器
/// （<see cref="CustomFixedHeaderDataHandlingAdapter{T}"/>），2 字节定长头
/// （0x68 + 长度）+ 变长体（4 字节 APCI + ASDU）。
/// </summary>
/// <remarks>
/// <para>
/// 粘包/半包缓存与非法帧头的逐字节滑动重同步（GoOn → Advance(1) 重试）由框架
/// 内置实现，语义与旧版手写 <c>IecApduParser.Filter</c> 三态解析完全一致；
/// 帧头/负载的具体解析在 <see cref="IecApdu"/> 的
/// <see cref="IecApdu.OnParsingHeader"/> / <see cref="IecApdu.OnParsingBody"/> 中完成。
/// </para>
/// <para>
/// 装载本适配器后，接收回调中 <c>ByteBlock</c> 为 null、<c>RequestInfo</c> 为
/// <see cref="IecApdu"/>；发送侧由库手动构建字节（序列号在发送临界区回填），
/// 不经适配器按 requestInfo 序列化。
/// </para>
/// </remarks>
internal sealed class Iec104Adapter : CustomFixedHeaderDataHandlingAdapter<IecApdu>
{
    private readonly Func<bool> _captureRawFrames;

    /// <summary>IEC 104 APCI 定长头：0x68 + 长度，共 2 字节。</summary>
    public override int HeaderLength => 2;

    /// <summary>
    /// 创建适配器。
    /// </summary>
    /// <param name="captureRawFrames">
    /// 原始帧捕获开关（宿主端点在存在 <c>RawFrameReceived</c> 订阅者时返回 true）。
    /// 每帧解析时读取最新值：无订阅者时 S/U 帧零拷贝零分配、I 帧仅拷 ASDU 负载。
    /// </param>
    public Iec104Adapter(Func<bool> captureRawFrames)
    {
        _captureRawFrames = captureRawFrames;
    }

    /// <inheritdoc/>
    public override bool CanSendRequestInfo => false;

    /// <inheritdoc/>
    protected override IecApdu GetInstance() => new IecApdu
    {
        CaptureRawFrames = _captureRawFrames?.Invoke() ?? false,
    };
}
