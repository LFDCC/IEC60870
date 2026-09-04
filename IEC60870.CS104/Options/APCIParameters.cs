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
    private int _k = 12;
    private int _w = 8;
    private int _t0 = 10;
    private int _t1 = 15;
    private int _t2 = 10;
    private int _t3 = 20;

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
        _t3 = _t3
    };

    /// <summary>
    /// 未确认 I 帧的最大数量（k 窗口大小，范围 1..32767）。
    /// 发送方在收到确认前最多发送 k 个 I 帧，超出后发送将异步背压等待。默认 12。
    /// </summary>
    public int K
    {
        get => _k;
        set => _k = value;
    }

    /// <summary>
    /// 接收方最迟每收到 w 个未确认 I 帧发送一次 S 确认（范围 1..32767，且须 w &lt; k）。默认 8。
    /// </summary>
    public int W
    {
        get => _w;
        set => _w = value;
    }

    /// <summary>建立连接的超时时长（秒），即 TCP 握手 + STARTDT 完成的允许时间。默认 10。</summary>
    public int T0
    {
        get => _t0;
        set => _t0 = value;
    }

    /// <summary>
    /// I/U 帧发送后的确认超时时长（秒）。超时未获确认则关闭连接。默认 15。
    /// 标准要求 T0 &lt; T1 &lt; T3。
    /// </summary>
    public int T1
    {
        get => _t1;
        set => _t1 = value;
    }

    /// <summary>
    /// 收到 I 帧后延迟发送 S 确认的超时时长（秒）。默认 10。标准要求 T2 &lt; T1。
    /// </summary>
    public int T2
    {
        get => _t2;
        set => _t2 = value;
    }

    /// <summary>空闲连接发送 TESTFR_ACT 测试帧的间隔（秒）。默认 20。标准要求 T1 &lt; T3。</summary>
    public int T3
    {
        get => _t3;
        set => _t3 = value;
    }
}
