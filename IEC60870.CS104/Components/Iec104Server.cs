//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS104;

/// <summary>
/// IEC 60870-5-104 异步服务端（从站）。基于 TouchSocket <see cref="TcpService{TClient}"/>，
/// 每个连接由 <see cref="Iec104Session"/> 承载独立的异步状态机。
/// </summary>
/// <remarks>
/// 用法：
/// <code>
/// var server = new Iec104Server();
/// server.AsduReceived += (Iec104Session s, in AsduView a) => { /* 处理 */ };
/// await server.StartAsync(2404);
/// </code>
/// </remarks>
public sealed class Iec104Server : TcpService<Iec104Session>, IIec104Slave
{
    private readonly APCIParameters _apci;
    private readonly ApplicationLayerParameters _al;
    private readonly ServiceSslOption _sslOption;
    private readonly ServerMode _mode;
    private readonly int _maxQueueSize;
    private readonly EnqueueMode _enqueueMode;
    private readonly ConcurrentDictionary<string, Iec104Session> _sessions
        = new ConcurrentDictionary<string, Iec104Session>();

    // 冗余组：SINGLE 一个共享组；MULTIPLE 用户组 + catch-all；CONNECTION 每会话一个组。
    // _singleGroup/_catchAllGroup 在 InitGroups（构造函数调用）中赋值，故不能声明为 readonly。
    private RedundancyGroup _singleGroup;
    private RedundancyGroup _catchAllGroup;
    private readonly List<RedundancyGroup> _userGroups = new();
    private readonly ConcurrentDictionary<string, RedundancyGroup> _sessionGroups = new();
    private readonly ConcurrentDictionary<string, RedundancyGroup> _sessionToGroup = new();

    /// <summary>
    /// 已启动标志（0=停止，1=运行）。用于拒绝重复 <see cref="StartAsync(int, Action{TouchSocketConfig})"/>：
    /// 重复进入会先 <see cref="ReinitGroups"/> 丢弃仍在排空的旧组，却没有 Cancel，
    /// 旧排空循环 Task 永久悬挂（每次误调用泄漏一组循环与队列引用）。
    /// </summary>
    private int _started;

    /// <summary>
    /// 停止中标志：<see cref="StopAsync"/> 开始即置位，阻止停止过程中迟到的连接
    /// 再次 <see cref="RegisterSession"/> 进入会话表（否则 CleanupAllSessions 清表后
    /// 又被重新填回，Stop 后 SessionCount 不为 0 且资源泄漏）。
    /// </summary>
    private volatile bool _stopping;

    #region 属性

    /// <summary>APCI 参数（k/w/T1/T2/T3）。</summary>
    public APCIParameters ApciParameters => _apci;

    /// <summary>应用层参数（COT/CA/IOA 宽度）。</summary>
    public ApplicationLayerParameters Parameters => _al;

    /// <summary>服务端事件分发模式（冗余组支持）。</summary>
    public ServerMode Mode => _mode;

    /// <summary>ASDU 类型处理器注册表（实例级可替换，会话接收时戳记）。</summary>
    public AsduTypeHandlerRegistry TypeHandlers { get; set; } = AsduTypeHandlerRegistry.Default;

    /// <summary>当前活动会话数。</summary>
    public int SessionCount => _sessions.Count;

    /// <summary>协议标识（约定同 TouchSocket 组件体系），固定为 <see cref="Iec60870Utility.Iec104"/>（"iec104"）。</summary>
    public Protocol Protocol { get; } = new Protocol(Iec60870Utility.Iec104);

    #endregion 属性

    #region 事件

    /// <summary>收到 ASDU 的零拷贝事件（带来源会话，支持多订阅者）。</summary>
    public event ServerAsduHandler AsduReceived;

    /// <summary>连接层事件（带来源会话，支持多订阅者）。</summary>
    public event Action<Iec104Session, ApduConnectionEvent> ConnectionEvent;

    #endregion 事件

    #region 构造

    /// <summary>用独立参数构造服务端。</summary>
    /// <param name="apciParameters">APCI 参数；null 时使用库默认。</param>
    /// <param name="alParameters">应用层参数；null 时使用库默认。</param>
    /// <param name="sslOption">服务端 TLS 配置；null 表示明文 TCP。</param>
    /// <param name="mode">事件分发模式（冗余组支持）。</param>
    /// <param name="maxQueueSize">每个事件队列的最大容量。</param>
    /// <param name="enqueueMode">队列满时的入队策略。</param>
    public Iec104Server(APCIParameters apciParameters = null,
        ApplicationLayerParameters alParameters = null,
        ServiceSslOption sslOption = null,
        ServerMode mode = ServerMode.SINGLE_REDUNDANCY_GROUP,
        int maxQueueSize = 1000,
        EnqueueMode enqueueMode = EnqueueMode.REMOVE_OLDEST)
    {
        _apci = apciParameters ?? new APCIParameters();
        _al = alParameters ?? new ApplicationLayerParameters();
        _sslOption = sslOption;
        _mode = mode;
        _maxQueueSize = maxQueueSize;
        _enqueueMode = enqueueMode;
        InitGroups();
    }

    /// <summary>用 <see cref="Iec104Options"/> 构造服务端（Options 对象模式）。</summary>
    /// <param name="options">配置项；构造时快照，后续修改原对象不影响本服务端。</param>
    public Iec104Server(Iec104Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var snapshot = options.Clone();
        _apci = snapshot.ApciParameters;
        _al = snapshot.AlParameters;
        _sslOption = snapshot.ServiceSslOption;
        _mode = snapshot.ServerMode;
        _maxQueueSize = snapshot.MaxQueueSize;
        _enqueueMode = snapshot.EnqueueMode;
        InitGroups();
    }

    private void InitGroups()
    {
        switch (_mode)
        {
            case ServerMode.SINGLE_REDUNDANCY_GROUP:
                _singleGroup = new RedundancyGroup("single");
                _singleGroup.Init(_maxQueueSize, _enqueueMode, _al);
                break;
            case ServerMode.MULTIPLE_REDUNDANCY_GROUPS:
                _catchAllGroup = new RedundancyGroup("catch-all");
                _catchAllGroup.Init(_maxQueueSize, _enqueueMode, _al);
                break;
        }
    }

    /// <summary>
    /// 重建全部冗余组（StopAsync 之后重新 StartAsync 时调用）。
    /// 旧组的排空循环已由 StopAsync 停掉，这里用全新实例取代它们，
    /// 避免"重启后能收不能发"的假在线。
    /// </summary>
    private void ReinitGroups()
    {
        // 旧组（含用户组）必须先 Cancel 再丢弃：仅置空引用不会唤醒它们各自的
        // 排空循环（DrainAsync 仍挂在 _activeSignal / _queue 上），Stop→Start 每轮
        // 泄漏一条永不退出的 Task 与整组队列引用。Cancel 幂等，可重复调用。
        // 服务端自有的组再 Dispose 释放 CTS/信号量（幂等）；用户组所有权在用户，只 Cancel。
        _singleGroup?.Cancel();
        _singleGroup?.Dispose();
        _catchAllGroup?.Cancel();
        _catchAllGroup?.Dispose();

        _singleGroup = null;
        _catchAllGroup = null;

        lock (_userGroups)
        {
            foreach (var g in _userGroups)
            {
                g.Cancel();
            }

            _userGroups.Clear();
        }

        InitGroups();
    }

    #endregion 构造

    #region 启停

    /// <summary>工厂方法：每个新连接创建一个会话实例。</summary>
    protected override Iec104Session NewClient() => new Iec104Session();

    /// <summary>在指定端口启动监听。</summary>
    /// <param name="port">监听端口。</param>
    /// <param name="configureConfig">可选的 TouchSocket 配置回调，在库默认配置（监听地址/SSL）之后执行，
    /// 可追加插件、日志、缓冲等配置（用户回调追加式配置合并）。</param>
    /// <summary>
    /// 启动监听并（重新）拉起冗余组排空循环。
    /// </summary>
    /// <remarks>
    /// <see cref="StopAsync"/> 会停掉所有组的排空循环且不可恢复（<see cref="RedundancyGroup.Init"/>
    /// 幂等、早退），而 <see cref="InitGroups"/> 只在构造时调用一次。
    /// 于是"StopAsync → StartAsync"之后：服务端能建连、能收报文，但
    /// <see cref="EnqueueAsync"/> 的自发事件**永不发出**，且无异常无日志 ——
    /// 最危险的「假在线」。滚动升级 / 配置热重启必踩。
    /// 这里在每次 StartAsync 时重建组（StopAsync 已确保旧组排空循环已退出）。
    /// </remarks>
    public async Task StartAsync(int port, Action<TouchSocketConfig> configureConfig = null)
    {
        // 已启动时拒绝重入：继续往下会 ReinitGroups 丢弃仍在排空的旧组，
        // 使旧排空循环永久悬挂（每次误调用泄漏一组）。要求先显式 StopAsync。
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            throw new InvalidOperationException("服务端已启动：请先调用 StopAsync 再重新启动");
        }

        try
        {
            _stopping = false;
            ReinitGroups();

            var config = new TouchSocketConfig();
            config.SetListenIPHosts(new IPHost(port));
            if (_sslOption != null)
            {
                config.SetServiceSslOption(o =>
                {
                    o.Certificate = _sslOption.Certificate;
                    o.ClientCertificateRequired = _sslOption.ClientCertificateRequired;
                    o.SslProtocols = _sslOption.SslProtocols;
                    o.CheckCertificateRevocation = _sslOption.CheckCertificateRevocation;
                    o.CertificateValidationCallback = _sslOption.CertificateValidationCallback;
                });
            }

            // 用户自定义配置在库默认项之后应用，可追加插件、日志等
            configureConfig?.Invoke(config);
            await SetupAsync(config).ConfigureAwait(false);
            await StartAsync().ConfigureAwait(false);

            // SetupAsync 之后 Logger 已由容器解析：转发给冗余组，排空循环异常才能被记录。
            ApplyLoggerToGroups();
        }
        catch
        {
            // 启动失败必须复位标志，允许调用方修复问题后重试。
            Interlocked.Exchange(ref _started, 0);
            throw;
        }
    }

    /// <summary>
    /// 停止监听并关闭所有会话。服务端主动停止不会为每个会话派发 ConnectionClosed
    /// （每个会话已在关闭前标记 <see cref="Iec104Session.MarkIntentionalClose"/>，避免刷屏）；
    /// 客户端侧断开、超时或协议错误仍会正常派发，使订阅者感知“哪个会话”断开。
    /// </summary>
    public async Task StopAsync()
    {
        // 先关闸：停止过程中即便还有连接被 accept / 握手完成，也拒绝其登记会话，
        // 避免 CleanupAllSessions 清表后被迟到的 RegisterSession 重新填回。
        _stopping = true;

        foreach (var s in _sessions.Values)
        {
            s.MarkIntentionalClose();
        }

        try
        {
            await base.StopAsync().ConfigureAwait(false);

            // 停止全部冗余组排空循环（含每会话专属组）。
            // 必须等排空循环退出后再 Dispose 组内 CTS/信号量，否则排空循环
            // 可能在信号量释放后仍在 WaitAsync（ODE 噪声，虽有兜底也不应制造）。
            var stopTasks = new List<Task>();

            if (_singleGroup != null)
            {
                stopTasks.Add(_singleGroup.StopAsync());
            }

            if (_catchAllGroup != null)
            {
                stopTasks.Add(_catchAllGroup.StopAsync());
            }

            lock (_userGroups)
            {
                foreach (var g in _userGroups)
                {
                    stopTasks.Add(g.StopAsync());
                }
            }

            foreach (var g in _sessionGroups.Values)
            {
                stopTasks.Add(g.StopAsync());
            }

            await Task.WhenAll(stopTasks).ConfigureAwait(false);
        }
        finally
        {
            // 无论 base.StopAsync 是否异常，都必须清空会话/组映射并释放服务端自有的组，
            // 否则 ConnectionClosed 未派发时每次 Stop 泄漏 N 个组 + N 条映射（500 装置 = 500 组/次）。
            CleanupAllSessions();
            Interlocked.Exchange(ref _started, 0);
        }
    }

    /// <summary>
    /// 强制清理全部会话与冗余组（<see cref="StopAsync"/> 与 <see cref="SafetyDispose"/> 共用）。
    /// 幂等，可重复调用：
    /// <list type="bullet">
    /// <item>每个会话 <see cref="Iec104Session.ForceCleanup"/>（FinalizeSession 幂等）；</item>
    /// <item>清空会话表与「会话→组」映射；</item>
    /// <item>Cancel 全部组（唤醒排空循环）并释放服务端自有组（CTS/信号量）。</item>
    /// </list>
    /// </summary>
    private void CleanupAllSessions()
    {
        // 先停会话：排空循环可能正卡在向某会话 SendEncodedAsync，先让会话失效，
        // 循环才能因发送失败而回到 Cancel/退出路径。
        foreach (var kv in _sessions)
        {
            try
            {
                kv.Value.ForceCleanup();
            }
            catch
            {
                // 单个会话清理失败不得阻断其余会话；Dispose/Stop 路径不得抛出。
            }
        }

        _sessions.Clear();
        _sessionToGroup.Clear();

        // Cancel 全部组：唤醒挂起的排空循环（_activeSignal.WaitAsync / DequeueAsync）。
        // Cancel 幂等；对已在 StopAsync 中取消过的组重复调用无副作用。
        _singleGroup?.Cancel();
        _catchAllGroup?.Cancel();

        lock (_userGroups)
        {
            foreach (var g in _userGroups)
            {
                try
                {
                    g.Cancel();
                }
                catch
                {
                }
            }
        }

        foreach (var g in _sessionGroups.Values)
        {
            try
            {
                g.Cancel();
                g.Dispose();
            }
            catch
            {
            }
        }

        _sessionGroups.Clear();

        // 释放服务端自有组持有的 CTS / SemaphoreSlim / 引用（IDisposable 幂等，
        // 重复 Stop/Dispose 不会抛异常）。用户通过 AddRedundancyGroup 传入的组只 Cancel、
        // 不 Dispose —— 所有权在用户，且 Stop→Start 后用户可能重新加入同一实例。
        _singleGroup?.Dispose();
        _catchAllGroup?.Dispose();
    }

    /// <summary>把已解析的 <see cref="ILog"/> 转发给全部冗余组（排空循环异常记录用；Logger 为 null 时静默）。</summary>
    private void ApplyLoggerToGroups()
    {
        var log = Logger;
        if (log == null)
        {
            return;
        }

        // 不能写成 `_singleGroup?.Logger = log`：对 null 条件访问表达式赋值属于
        // Roslyn 的 preview 特性（CS8652），常规 LangVersion 下直接编译失败。
        if (_singleGroup != null)
        {
            _singleGroup.Logger = log;
        }

        if (_catchAllGroup != null)
        {
            _catchAllGroup.Logger = log;
        }

        lock (_userGroups)
        {
            foreach (var g in _userGroups)
            {
                g.Logger = log;
            }
        }

        foreach (var g in _sessionGroups.Values)
        {
            g.Logger = log;
        }
    }

    #endregion 启停

    #region 发送

    /// <summary>向所有已激活会话广播一个 ASDU。</summary>
    public async Task BroadcastAsync(ASDU asdu, CancellationToken cancellationToken = default)
    {
        // 并发向各会话发送，避免某个会话 k 窗口满时串行阻塞其余会话（代码评审 #9）。
        // 每个会话独立编码（参数可能不同），并行安全。
        var tasks = new List<Task>(_sessions.Count);
        foreach (var kv in _sessions)
        {
            var s = kv.Value;
            if (s.IsActivated)
            {
                tasks.Add(SendOneAsync(s, asdu, cancellationToken));
            }
        }

        // 并发等待：单会话失败不影响其余会话的发送结果，但汇总所有失败以便调用方排查。
        // SendOneAsync 只透传 OperationCanceledException，其余异常原样抛到此处被 catch。
        List<Exception> failures = null;
        foreach (var t in tasks)
        {
            try
            {
                await t.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            } // 取消必须上抛
            catch (Exception ex)
            {
                (failures ??= new List<Exception>()).Add(ex);
            }
        }

        if (failures != null)
        {
            throw new AggregateException("部分会话广播失败", failures);
        }
    }

    private async Task SendOneAsync(Iec104Session s, ASDU asdu, CancellationToken ct)
    {
        // 不吞 Exception：让异常传到 BroadcastAsync 的 await 处被 catch 汇总到 failures。
        // 仅 OperationCanceledException 透传（取消语义必须上抛）。
        await s.SendAsync(asdu, ct).ConfigureAwait(false);
    }

    #endregion 发送

    #region 内部：会话与回调桥接

    /// <summary>注册会话（由会话在连接建立时调用）。停止中拒绝登记，防止 Stop 后会话表被重新填回。</summary>
    internal void RegisterSession(Iec104Session session)
    {
        if (_stopping)
        {
            // 服务端正在停止：迟到的连接不再纳入管理，直接终结其端点资源。
            try
            {
                session.ForceCleanup();
            }
            catch
            {
            }

            return;
        }

        _sessions[session.Id] = session;
    }

    /// <summary>注销会话（由会话在连接关闭时调用）。</summary>
    internal void UnregisterSession(Iec104Session session) => _sessions.TryRemove(session.Id, out _);

    internal AsduViewHandler RaiseAsduReceived(Iec104Session session)
        => (in AsduView a) => AsduReceived?.Invoke(session, in a);

    internal void RaiseConnectionEvent(Iec104Session session, ApduConnectionEvent ev)
    {
        // 先维护冗余组成员关系，再向订阅者派发，保证订阅者看到事件时组状态已一致。
        switch (ev)
        {
            case ApduConnectionEvent.Activated:
                OnSessionActivated(session);
                break;
            case ApduConnectionEvent.Deactivated:
                // 与 Activated / ConnectionClosed 对称：STOPDT 去激活后会话不得继续
                // 担任冗余组主用，否则排空循环仍向已停止的链路发 I 帧（违反 104 规约）。
                // 保留 _sessionToGroup 映射：连接还在，后续可再次 STARTDT 重新激活。
                OnSessionDeactivated(session);
                break;
            case ApduConnectionEvent.ConnectionClosed:
                OnSessionClosed(session);
                break;
        }

        ConnectionEvent?.Invoke(session, ev);
    }

    #region 事件队列 / 冗余组

    /// <summary>
    /// 添加一个冗余组（仅 <see cref="ServerMode.MULTIPLE_REDUNDANCY_GROUPS"/> 有效）。
    /// 组的允许客户端列表为空时作为 catch-all 组。
    /// </summary>
    public void AddRedundancyGroup(RedundancyGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        if (_mode != ServerMode.MULTIPLE_REDUNDANCY_GROUPS)
        {
            throw new InvalidOperationException("仅 MULTIPLE_REDUNDANCY_GROUPS 模式支持显式添加冗余组");
        }

        group.Init(_maxQueueSize, _enqueueMode, _al);
        group.Logger = Logger;

        lock (_userGroups)
        {
            _userGroups.Add(group);
        }
    }

    /// <summary>
    /// 把一个自发 ASDU 编码入队，由对应冗余组的排空循环在连接可用时按序发送。
    /// 与 <see cref="BroadcastAsync"/> 的直接发送不同，本方法不阻塞、且事件在连接
    /// 未激活/窗口满时缓冲，冗余切换时不丢失。
    /// </summary>
    public Task EnqueueAsync(ASDU asdu, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asdu);
        cancellationToken.ThrowIfCancellationRequested();

        switch (_mode)
        {
            case ServerMode.SINGLE_REDUNDANCY_GROUP:
                _singleGroup.Enqueue(asdu);
                break;

            case ServerMode.CONNECTION_IS_REDUNDANCY_GROUP:
                foreach (var g in _sessionGroups.Values)
                {
                    g.Enqueue(asdu);
                }
                break;

            case ServerMode.MULTIPLE_REDUNDANCY_GROUPS:
                lock (_userGroups)
                {
                    foreach (var g in _userGroups)
                    {
                        g.Enqueue(asdu);
                    }
                }
                _catchAllGroup?.Enqueue(asdu);
                break;
        }

        return Task.CompletedTask;
    }

    /// <summary>把一个自发 ASDU 入队到指定冗余组（仅 MULTIPLE 模式）。</summary>
    public Task EnqueueAsync(ASDU asdu, RedundancyGroup group, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asdu);
        ArgumentNullException.ThrowIfNull(group);
        cancellationToken.ThrowIfCancellationRequested();

        if (_mode != ServerMode.MULTIPLE_REDUNDANCY_GROUPS)
        {
            throw new InvalidOperationException("仅 MULTIPLE_REDUNDANCY_GROUPS 模式支持定向入队");
        }

        group.Enqueue(asdu);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 返回事件队列中的待发送 ASDU 数。
    /// SINGLE 模式返回共享组队列长度；MULTIPLE 模式返回指定组（null 返回 0）；
    /// CONNECTION 模式返回首个非空会话队列长度。
    /// </summary>
    public int GetNumberOfQueueEntries(RedundancyGroup redundancyGroup = null)
    {
        switch (_mode)
        {
            case ServerMode.CONNECTION_IS_REDUNDANCY_GROUP:
                foreach (var g in _sessionGroups.Values)
                {
                    if (g.QueueLength > 0)
                    {
                        return g.QueueLength;
                    }
                }
                return 0;

            case ServerMode.MULTIPLE_REDUNDANCY_GROUPS:
                return redundancyGroup?.QueueLength ?? 0;

            default:
                return _singleGroup?.QueueLength ?? 0;
        }
    }

    private void OnSessionActivated(Iec104Session session)
    {
        if (_stopping)
        {
            // 停止中不再建组/记录主用：此时 CleanupAllSessions 可能已清空映射，
            // 迟到的 Activated 会重新造出新组（排空循环）从而绕过 Stop 的清理。
            return;
        }

        var group = ResolveGroupForSession(session);

        if (group != null)
        {
            _sessionToGroup[session.Id] = group;
            group.OnActivated(session);
        }
    }

    /// <summary>STOPDT 去激活：仅让会话退出冗余组主用角色（连接仍在，不删组映射/不停止专属组）。</summary>
    private void OnSessionDeactivated(Iec104Session session)
    {
        if (_sessionToGroup.TryGetValue(session.Id, out var group))
        {
            group.OnDeactivated(session);
        }
    }

    private void OnSessionClosed(Iec104Session session)
    {
        if (_sessionToGroup.TryRemove(session.Id, out var group))
        {
            group.OnClosed(session);
        }

        if (_mode == ServerMode.CONNECTION_IS_REDUNDANCY_GROUP
            && _sessionGroups.TryRemove(session.Id, out var sg))
        {
            _ = StopAndDisposeGroupAsync(sg); // 会话结束，其专属组随之停止并释放
        }
    }

    /// <summary>停止并释放一个会话专属冗余组（排空循环退出后再 Dispose 其 CTS/信号量）。</summary>
    private static async Task StopAndDisposeGroupAsync(RedundancyGroup group)
    {
        try
        {
            await group.StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // 停止路径异常不上抛（会话关闭回调内不得抛出）
        }

        group.Dispose();
    }

    private RedundancyGroup ResolveGroupForSession(Iec104Session session)
    {
        switch (_mode)
        {
            case ServerMode.SINGLE_REDUNDANCY_GROUP:
                return _singleGroup;

            case ServerMode.CONNECTION_IS_REDUNDANCY_GROUP:
                return _sessionGroups.GetOrAdd(session.Id, _ =>
                {
                    var g = new RedundancyGroup($"conn:{session.Id}");
                    g.Init(_maxQueueSize, _enqueueMode, _al);
                    g.Logger = Logger;
                    return g;
                });

            case ServerMode.MULTIPLE_REDUNDANCY_GROUPS:
                var ip = session.ClientIpAddress;

                lock (_userGroups)
                {
                    foreach (var g in _userGroups)
                    {
                        if (g.Matches(ip))
                        {
                            return g;
                        }
                    }
                }

                return _catchAllGroup;

            default:
                return null;
        }
    }

    #endregion 事件队列 / 冗余组

    /// <summary>
    /// Dispose 兜底：正常路径下各会话的 OnTcpClosed 已清理自身资源，但服务端直接
    /// Dispose 时 TouchSocket 内部关闭客户端连接不等待异步回调，存在会话资源未及
    /// 清理的竞态——此处同步兜底释放全部会话的 ApduConnection 与 CTS，
    /// 并取消/释放全部冗余组。与 <see cref="StopAsync"/> 共用幂等清理。
    /// </summary>
    protected override void SafetyDispose(bool disposing)
    {
        if (disposing)
        {
            _stopping = true; // 拒绝释放过程中迟到的会话登记/激活建组
            CleanupAllSessions();
            Interlocked.Exchange(ref _started, 0);
        }
        base.SafetyDispose(disposing);
    }

    #endregion 内部：会话与回调桥接
}
