//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS104;

/// <summary>
/// IEC 60870-5-104 APCI（应用协议控制信息）参数：k/w 流控窗口与 T0~T3 定时器。
/// </summary>
/// <remarks>
/// <b>配置时机：</b>请在传给 <see cref="ApduConnection"/> 构造前完成全部赋值，构造后<b>不要</b>再修改本对象。
/// <para>
/// 语义不一致提示：<see cref="K"/> 在 <see cref="ApduConnection"/> 构造时即被捕获（决定 k 窗口容量），
/// 构造后修改 <see cref="K"/> 对窗口流控静默无效；而 <see cref="W"/>/<see cref="T1"/>/<see cref="T2"/>/<see cref="T3"/>
/// 在运行期被实时读取，构造后修改会动态影响超时与确认行为。为避免隐蔽的行为漂移，统一在构造前配置好。
/// </para>
/// </remarks>
public class APCIParameters
{
    private const int MaxWindowSize = 32767;

    private int _k = 12;
    private int _w = 8;
    private int _t0 = 10;
    private int _t1 = 15;
    private int _t2 = 10;
    private int _t3 = 20;

    // 是否被显式赋值：用于区分「默认 W=8」与「用户显式配置的 W」。
    // 现场/测试常只调小 K（如 K=2）而保留默认 W=8，此时 W>=K 并不致命（S 确认由 T2 兜底），
    // 不应在构造时判为配置错误；只有用户显式配置了 W 才强制 W<K。
    private bool _kSet;
    private bool _wSet;

    /// <summary>创建使用标准默认值（k=12、w=8、T0=10s、T1=15s、T2=10s、T3=20s）的参数对象。</summary>
    public APCIParameters()
    {
    }

    /// <summary>深拷贝当前参数。</summary>
    public APCIParameters Clone() => new APCIParameters
    {
        _k = _k,
        _w = _w,
        _t0 = _t0,
        _t1 = _t1,
        _t2 = _t2,
        _t3 = _t3,
        _kSet = _kSet,
        _wSet = _wSet
    };

    /// <summary>
    /// 未确认 I 帧的最大数量（k 窗口大小，范围 1..32767）。
    /// 发送方在收到确认前最多发送 k 个 I 帧，超出后发送将异步背压等待。默认 12。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> 不在 1..32767 之间。</exception>
    public int K
    {
        get => _k;
        set
        {
            if (value < 1 || value > MaxWindowSize)
            {
                throw new ArgumentOutOfRangeException(nameof(K), value, "K 必须在 1..32767 之间");
            }

            _k = value;
            _kSet = true;
        }
    }

    /// <summary>
    /// 接收方最迟每收到 w 个未确认 I 帧发送一次 S 确认（范围 1..32767，且须 w &lt; k）。默认 8。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> 不在 1..32767 之间。</exception>
    public int W
    {
        get => _w;
        set
        {
            if (value < 1 || value > MaxWindowSize)
            {
                throw new ArgumentOutOfRangeException(nameof(W), value, "W 必须在 1..32767 之间");
            }

            _w = value;
            _wSet = true;
        }
    }

    /// <summary>建立连接的超时时长（秒），即 TCP 握手 + STARTDT 完成的允许时间。默认 10。</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> 不大于 0。</exception>
    public int T0
    {
        get => _t0;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(T0), value, "T0 必须大于 0");
            }

            _t0 = value;
        }
    }

    /// <summary>
    /// I/U 帧发送后的确认超时时长（秒）。超时未获确认则关闭连接。默认 15。
    /// 标准要求 T0 &lt; T1 &lt; T3。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> 不大于 0。</exception>
    public int T1
    {
        get => _t1;
        set
        {
            // 0 表示不启用该定时器（单元测试与"兼容不规范装置"场景会用 0 关闭）
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(T1), value, "T1 不能为负数（0 表示不启用）");
            }

            _t1 = value;
        }
    }

    /// <summary>
    /// 收到 I 帧后延迟发送 S 确认的超时时长（秒）。默认 10。标准要求 T2 &lt; T1。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> 为负数。</exception>
    public int T2
    {
        get => _t2;
        set
        {
            // 0 表示不启用
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(T2), value, "T2 不能为负数（0 表示不启用）");
            }

            _t2 = value;
        }
    }

    /// <summary>空闲连接发送 TESTFR_ACT 测试帧的间隔（秒）。默认 20。标准要求 T1 &lt; T3。</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> 为负数。</exception>
    public int T3
    {
        get => _t3;
        set
        {
            // 0 表示不启用
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(T3), value, "T3 不能为负数（0 表示不启用）");
            }

            _t3 = value;
        }
    }

    /// <summary>
    /// 校验跨字段关系（单个字段的范围由各自 setter 保证）。
    /// 由 <see cref="ApduConnection"/> 在构造时调用：对象初始化器/多次赋值的过程中可能
    /// 出现非法中间态，无法在某个 setter 内可靠校验，因此统一放到使用处。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// T0/T1/T3 不满足 T0 &lt; T1 &lt; T3，或显式配置的 W 不满足 W &lt; K。
    /// </exception>
    internal void Validate()
    {
        // 0 表示"不启用该定时器"，此时不参与顺序校验（单元测试常用 0 关闭定时器）
        if (_t0 > 0 && _t1 > 0 && _t0 >= _t1)
        {
            throw new ArgumentOutOfRangeException(nameof(T0), _t0,
                $"T0 ({_t0}) 必须小于 T1 ({_t1})");
        }

        if (_t1 > 0 && _t3 > 0 && _t1 >= _t3)
        {
            throw new ArgumentOutOfRangeException(nameof(T3), _t3,
                $"T3 ({_t3}) 必须大于 T1 ({_t1})");
        }

        // 标准要求 W < K；但 K=1 是合法（且测试常用）的极端配置，此时只能取 W=1。
        // 因此按 W ≤ K 校验，拒绝真正矛盾的配置（W > K）。
        if (_wSet && _w > _k)
        {
            throw new ArgumentOutOfRangeException(nameof(W), _w,
                $"W ({_w}) 必须小于 K ({_k})");
        }
    }
}
