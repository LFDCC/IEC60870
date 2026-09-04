//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS104;

/// <summary>
/// IEC 60870-5-104 组件配置项（Options 对象模式，对齐 LFDCC.Modbus 风格）。
/// 构造后经 fluent 方法 <see cref="UseApciParameters"/> / <see cref="UseAlParameters"/> / <see cref="UseSsl"/>
/// 或直接设置属性完成配置，再传给 <see cref="Iec104Client"/> / <see cref="Iec104Server"/> 构造。
/// 各组件构造时会 <see cref="Clone"/> 快照，构造后修改本对象不影响已创建的组件。
/// </summary>
public class Iec104Options
{
    private APCIParameters _apciParameters;
    private ApplicationLayerParameters _alParameters;
    private ClientSslOption _sslOption;
    private ServiceSslOption _serviceSslOption;
    private bool _autostart;
    private TimeSpan _commandConfirmationTimeout;
    private ServerMode _serverMode;
    private int _maxQueueSize;
    private EnqueueMode _enqueueMode;

    /// <summary>创建一个使用库默认参数的配置。</summary>
    public Iec104Options()
    {
        _apciParameters = new APCIParameters();
        _alParameters = new ApplicationLayerParameters();
        _autostart = true;
        _commandConfirmationTimeout = TimeSpan.FromSeconds(5);
        _serverMode = ServerMode.SINGLE_REDUNDANCY_GROUP;
        _maxQueueSize = 1000;
        _enqueueMode = EnqueueMode.REMOVE_OLDEST;
    }

    /// <summary>APCI 参数（k/w/T1/T2/T3）。</summary>
    public APCIParameters ApciParameters
    {
        get => _apciParameters;
        set => _apciParameters = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>应用层参数（COT/CA/IOA 宽度）。</summary>
    public ApplicationLayerParameters AlParameters
    {
        get => _alParameters;
        set => _alParameters = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>客户端 TLS 配置（仅客户端使用；为 null 表示明文 TCP）。</summary>
    public ClientSslOption ClientSslOption
    {
        get => _sslOption;
        set => _sslOption = value;
    }

    /// <summary>服务端 TLS 配置（仅服务端使用；为 null 表示明文 TCP）。</summary>
    public ServiceSslOption ServiceSslOption
    {
        get => _serviceSslOption;
        set => _serviceSslOption = value;
    }

    /// <summary>连接成功后是否自动发送 STARTDT_ACT 激活数据传输（仅客户端）。默认 <c>true</c>。</summary>
    public bool Autostart
    {
        get => _autostart;
        set => _autostart = value;
    }

    /// <summary>等待控制命令 ACT-CON 确认的超时时长（仅客户端）。默认 5 秒。</summary>
    public TimeSpan CommandConfirmationTimeout
    {
        get => _commandConfirmationTimeout;
        set => _commandConfirmationTimeout = value;
    }

    /// <summary>服务端事件分发模式（冗余组支持，仅服务端）。默认 <see cref="ServerMode.SINGLE_REDUNDANCY_GROUP"/>。</summary>
    public ServerMode ServerMode
    {
        get => _serverMode;
        set => _serverMode = value;
    }

    /// <summary>每个事件队列的最大容量（仅服务端）。默认 1000。</summary>
    public int MaxQueueSize
    {
        get => _maxQueueSize;
        set => _maxQueueSize = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    /// <summary>队列满时的入队策略（仅服务端）。默认 <see cref="EnqueueMode.REMOVE_OLDEST"/>。</summary>
    public EnqueueMode EnqueueMode
    {
        get => _enqueueMode;
        set => _enqueueMode = value;
    }

    /// <summary>流式配置 APCI 参数。</summary>
    public Iec104Options UseApciParameters(Action<APCIParameters> configure)
    {
        configure?.Invoke(_apciParameters);
        return this;
    }

    /// <summary>流式配置应用层参数。</summary>
    public Iec104Options UseAlParameters(Action<ApplicationLayerParameters> configure)
    {
        configure?.Invoke(_alParameters);
        return this;
    }

    /// <summary>设置客户端 TLS 配置（客户端侧）。</summary>
    public Iec104Options UseSsl(ClientSslOption sslOption)
    {
        _sslOption = sslOption;
        return this;
    }

    /// <summary>设置服务端 TLS 配置（服务端侧）。</summary>
    public Iec104Options UseSsl(ServiceSslOption sslOption)
    {
        _serviceSslOption = sslOption;
        return this;
    }

    /// <summary>设置连接成功后是否自动激活数据传输（客户端侧）。</summary>
    public Iec104Options UseAutostart(bool autostart)
    {
        _autostart = autostart;
        return this;
    }

    /// <summary>设置控制命令确认超时（客户端侧）。</summary>
    public Iec104Options SetCommandConfirmationTimeout(TimeSpan timeout)
    {
        _commandConfirmationTimeout = timeout;
        return this;
    }

    /// <summary>
    /// 深拷贝快照。组件构造时调用，此后对原 Options 的修改不影响已创建组件。
    /// </summary>
    public Iec104Options Clone()
    {
        var copy = new Iec104Options
        {
            _apciParameters = _apciParameters.Clone(),
            _alParameters = _alParameters.Clone(),
            _autostart = _autostart,
            _commandConfirmationTimeout = _commandConfirmationTimeout,
            _serverMode = _serverMode,
            _maxQueueSize = _maxQueueSize,
            _enqueueMode = _enqueueMode,
            // TLS 配置仅在 Setup 时读取一次，按引用共享即可（TouchSocket SslOption 无 Clone）
            _sslOption = _sslOption,
            _serviceSslOption = _serviceSslOption
        };

        return copy;
    }
}
