//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS104;

/// <summary>
/// IEC 60870-5-104 T1/T2/T3 定时驱动器（每连接的调度句柄）。
/// </summary>
/// <remarks>
/// <para>
/// 本类自身不再持有物理 Timer：底层改用进程级共享调度器
/// <see cref="SharedTimeoutScheduler"/>（单物理 Timer + 按绝对 deadline 排序的分片最小堆）。
/// 每个连接的 T1/T2/T3 仍各自独立（<see cref="APCIParameters"/> 按连接持有），
/// 由 <see cref="ApduConnection.GetSuggestedDelayMs"/> 算出「下一个检查时刻」后注册进去；
/// 共享的是「谁下一个到点」，不是「多久查一次」。
/// </para>
/// <para>
/// 语义与旧实现保持一致：
/// <list type="bullet">
/// <item>致命检查（返回 false）触发一次 <c>onFatal</c> 后不再重排；</item>
/// <item><see cref="OperationCanceledException"/>（连接终止令牌取消）静默停止，不触发 onFatal；</item>
/// <item>其它异常按瞬态处理：不崩溃、继续重排；</item>
/// <item><see cref="Dispose"/> 后不再触发检查（从共享堆中注销）。</item>
/// </list>
/// </para>
/// </remarks>
internal sealed class Iec104TimerScheduler : IDisposable
{
    private readonly Func<CancellationToken, ValueTask<bool>> _check;
    private readonly Func<int> _delayProvider;

    /// <summary>在共享调度器中的注册句柄；null 表示已注销/未启用。</summary>
    private SharedTimeoutScheduler.Entry _entry;

    private bool _disposed;

    /// <summary>
    /// 创建调度器并注册到共享调度器，首次检查延迟取 <paramref name="delayProvider"/> 的首个值。
    /// </summary>
    /// <param name="check">超时检查（即 <see cref="ApduConnection.CheckTimeoutsAsync"/>）；返回 false 表示致命超时。</param>
    /// <param name="delayProvider">下一次检查的延迟提供（即 <see cref="ApduConnection.GetSuggestedDelayMs"/>）。</param>
    /// <param name="onFatal">致命超时回调（宿主：标记 CloseReason + 关闭连接）。</param>
    /// <param name="cancellationToken">检查使用的取消令牌（连接生命周期令牌）。</param>
    public Iec104TimerScheduler(
        Func<CancellationToken, ValueTask<bool>> check,
        Func<int> delayProvider,
        Action onFatal,
        CancellationToken cancellationToken)
    {
        _check = check ?? throw new ArgumentNullException(nameof(check));
        _delayProvider = delayProvider ?? throw new ArgumentNullException(nameof(delayProvider));
        if (onFatal == null) throw new ArgumentNullException(nameof(onFatal));

        _entry = SharedTimeoutScheduler.Instance.Register(
            RunCheckAsync, _delayProvider, onFatal, cancellationToken, _delayProvider());
    }

    /// <summary>从共享调度器注销并停止后续检查。幂等。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var entry = _entry;
        _entry = null;
        if (entry != null)
        {
            SharedTimeoutScheduler.Instance.Unregister(entry);
        }
    }

    /// <summary>
    /// 共享调度器到期时调用：执行一次超时检查，并把结果映射为调度决策。
    /// </summary>
    /// <remarks>
    /// 返回值约定（三态，避免用 bool 重载语义）：
    /// <list type="bullet">
    /// <item><see cref="SchedulerAction.Stop"/> — 不再调度本连接、不触发 onFatal（正常终止）；</item>
    /// <item><see cref="SchedulerAction.Fatal"/> — 触发 onFatal 且不再调度（协议致命超时）；</item>
    /// <item><see cref="SchedulerAction.Continue"/> — 按 delayProvider 继续调度。</item>
    /// </list>
    /// </remarks>
    private async ValueTask<SchedulerAction> RunCheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            // ApduConnection.CheckTimeoutsAsync: true=健康/非致命, false=致命超时
            var healthy = await _check(cancellationToken).ConfigureAwait(false);
            return healthy ? SchedulerAction.Continue : SchedulerAction.Fatal;
        }
        catch (OperationCanceledException)
        {
            // 连接终止令牌取消：正常收尾，静默停止调度，不得触发 onFatal。
            return SchedulerAction.Stop;
        }
        catch (Exception)
        {
            // 检查中的瞬态异常（如 sink 写入噪声）不应让调度器崩溃：
            // 视为非致命，继续调度，由连接自身的关闭路径处理致命情况。
            return SchedulerAction.Continue;
        }
    }

    /// <summary>调度决策（共享调度器到期后对单个连接的动作）。</summary>
    internal enum SchedulerAction
    {
        /// <summary>停止调度本连接，不触发 onFatal。</summary>
        Stop,

        /// <summary>触发 onFatal 并停止调度。</summary>
        Fatal,

        /// <summary>按连接的 delayProvider 继续调度。</summary>
        Continue
    }
}
