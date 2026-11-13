//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS103;

/// <summary>
/// IEC 60870-5-103 链路层参数（FT1.2）。
/// </summary>
/// <remarks>
/// 103 使用 FT1.2 帧格式（IEC 60870-5-1/-2），现场固定为
/// <b>1 字节链路地址</b>、<b>仅非平衡传输</b>。本类刻意不暴露
/// 可变的地址长度与平衡模式开关，避免配出 103 装置无法识别的帧。
/// <para>
/// 构造后请勿再修改：组件在构造期取快照，运行期改动不会生效。
/// 需要差异化配置时用 <see cref="Clone"/> 复制一份再改。
/// </para>
/// </remarks>
public sealed class Iec103LinkParameters
{
    /// <summary>FT1.2 变长帧 ASDU 上限（1 字节链路地址下 L≤255，扣除 C+A 后为 253）。</summary>
    public const int MaxAsduLength = 253;

    /// <summary>链路地址长度。103 固定 1 字节，暴露为只读属性仅为文档完整性。</summary>
    public int AddressLength => 1;

    /// <summary>广播地址（链路层）。103 使用 255。</summary>
    public int BroadcastAddress => 255;

    /// <summary>
    /// 等待对端应答的超时（毫秒），用于 SEND/CONFIRM 与 REQUEST/RESPOND 服务。
    /// 默认 2000ms。
    /// </summary>
    public int MessageTimeoutMs { get; set; } = 2000;

    /// <summary>
    /// 帧内字符间隔超时（毫秒）：收到起始字符后，等待后续字节的最大间隔。
    /// 超时即判定为半帧并丢弃、复位分帧状态机。默认 50ms。
    /// </summary>
    /// <remarks>
    /// 该值必须显式生效——103 原实现依赖 <c>SerialPort.ReadTimeout</c> 抛
    /// <c>TimeoutException</c> 来"醒一次"，但 TCP 字节流不具该语义，
    /// 会导致断线后分帧器永久卡在半帧状态。此处改为分帧器自身计时。
    /// </remarks>
    public int CharacterTimeoutMs { get; set; } = 50;

    /// <summary>
    /// 单帧最大重发次数（链路层超时重发）。默认 3。
    /// </summary>
    public int MaxRepeat { get; set; } = 3;

    /// <summary>
    /// 主站收到 1 级数据后，是否在同一次 1 级召唤中继续取直到无数据。
    /// 默认 true（103 现场通行做法：一次召唤把 1 级队列排空）。
    /// </summary>
    public bool DrainClass1UntilEmpty { get; set; } = true;

    /// <summary>
    /// 本站链路地址（<b>仅串口/Stream 从站使用</b>）。
    /// </summary>
    /// <remarks>
    /// RS-485 总线上多个从站共享物理线路，从站必须只响应本站地址的请求帧，
    /// 其余静默忽略（否则会与其他从站抢答导致总线冲突）。
    /// <para>
    /// 一条通道下挂多台装置、由链路地址（即 CA）区分时，用 <see cref="AddLinkAddress"/>
    /// 把同一从站需要应答的其余地址也加入 <see cref="LinkAddresses"/>。
    /// </para>
    /// <para>
    /// TCP 模式下忽略此配置：一条连接对应一个主站，地址由主站首帧确定。
    /// </para>
    /// </remarks>
    public byte LocalLinkAddress
    {
        get => _localLinkAddress;
        set
        {
            _localLinkAddress = value;
            _linkAddresses.Clear();
            _linkAddresses.Add(value);
        }
    }

    /// <summary>
    /// 本站接受的全部链路地址（RS-485 地址过滤依据）。
    /// </summary>
    /// <remarks>
    /// 默认只含 <see cref="LocalLinkAddress"/>。一条通道下挂多台装置时，
    /// 每台装置应各自运行一个从站实例并配置自己的地址；只有「一台物理装置
    /// 占用多个逻辑地址」的场景才需要把多个地址加到同一个从站上。
    /// </remarks>
    public IReadOnlySet<byte> LinkAddresses => _linkAddresses;

    /// <summary>增加一个本站链路地址（多逻辑地址场景）。</summary>
    /// <param name="address">链路地址（1-254；255 为广播，不应作为本站地址）。</param>
    public void AddLinkAddress(byte address)
    {
        if (address == 0 || address == BroadcastAddress)
        {
            throw new ArgumentOutOfRangeException(nameof(address), "本站链路地址不能是 0 或广播地址 255");
        }

        _linkAddresses.Add(address);
    }

    private byte _localLinkAddress = 1;
    private readonly HashSet<byte> _linkAddresses = new() { 1 };

    /// <summary>
    /// 是否接受 255 广播帧（仅串口/Stream 从站使用）。默认 true。
    /// </summary>
    /// <remarks>
    /// 广播帧（SEND/NO REPLY）不需要应答，但可能携带需要执行的命令；
    /// 若不希望本从站处理广播，置为 false。
    /// </remarks>
    public bool AcceptBroadcast { get; set; } = true;

    /// <summary>复制当前参数（组件构造期取快照用）。</summary>
    public Iec103LinkParameters Clone()
    {
        var clone = new Iec103LinkParameters();
        clone.CopyFrom(this);
        return clone;
    }

    /// <summary>
    /// 从另一实例复制全部字段。
    /// </summary>
    /// <remarks>
    /// 集中在此维护字段清单：<see cref="Clone"/> 与 <see cref="Iec103Options.Clone"/>
    /// 都走这里，新增参数时只需改这一处，不会出现某个副本路径漏拷。
    /// </remarks>
    internal void CopyFrom(Iec103LinkParameters source)
    {
        if (ReferenceEquals(this, source))
        {
            return;
        }

        MessageTimeoutMs = source.MessageTimeoutMs;
        CharacterTimeoutMs = source.CharacterTimeoutMs;
        MaxRepeat = source.MaxRepeat;
        DrainClass1UntilEmpty = source.DrainClass1UntilEmpty;
        AcceptBroadcast = source.AcceptBroadcast;

        // 直接复制字段而非走 LocalLinkAddress setter：setter 会把集合重置为单地址，
        // 会丢掉 AddLinkAddress 添加的其余地址。
        _localLinkAddress = source._localLinkAddress;
        _linkAddresses.Clear();
        foreach (var address in source._linkAddresses)
        {
            _linkAddresses.Add(address);
        }
    }
}

/// <summary>
/// 未识别 ASDU 的处理策略。
/// </summary>
public enum Iec103UnknownTypePolicy
{
    /// <summary>记录日志后继续（默认）。报文仍会通过 <c>UnknownAsduReceived</c> 事件投递，绝不丢帧。</summary>
    Log = 0,

    /// <summary>静默继续：不记日志，但仍投递 <c>UnknownAsduReceived</c> 事件。</summary>
    Ignore = 1,

    /// <summary>抛出 <see cref="Iec103UnknownAsduException"/>。仅用于开发期严格校验，生产环境不建议。</summary>
    Throw = 2,
}

/// <summary>
/// IEC 60870-5-103 组件配置（主站/从站通用）。
/// </summary>
/// <remarks>
/// fluent <c>Use*</c> 配置 + <see cref="Clone"/> 构造期快照：组件在构造时取快照，运行期改配置不生效。
/// </remarks>
public sealed class Iec103Options
{
    /// <summary>创建默认配置。</summary>
    public Iec103Options()
    {
    }

    /// <summary>FT1.2 链路层参数。</summary>
    public Iec103LinkParameters Link { get; } = new();

    /// <summary>未识别 ASDU 处理策略，默认 <see cref="Iec103UnknownTypePolicy.Log"/>。</summary>
    public Iec103UnknownTypePolicy UnknownTypePolicy { get; private set; } = Iec103UnknownTypePolicy.Log;

    /// <summary>设置链路层参数。</summary>
    public Iec103Options UseLinkParameters(Action<Iec103LinkParameters> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        configure(Link);
        return this;
    }

    /// <summary>设置未识别 ASDU 处理策略。</summary>
    public Iec103Options UseUnknownTypePolicy(Iec103UnknownTypePolicy policy)
    {
        UnknownTypePolicy = policy;
        return this;
    }

    /// <summary>
    /// 复制当前配置（含 <see cref="Link"/> 的深拷贝）。
    /// </summary>
    /// <remarks>
    /// <see cref="Link"/> 的复制委托给 <see cref="Iec103LinkParameters.Clone"/>，
    /// 避免在此逐字段枚举——新增链路参数时只需改一处，不会静默漏拷。
    /// </remarks>
    public Iec103Options Clone()
    {
        var clone = new Iec103Options
        {
            UnknownTypePolicy = UnknownTypePolicy,
        };

        clone.Link.CopyFrom(Link);
        return clone;
    }
}
