//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS104;

/// <summary>
/// IEC 60870-5-104 T1/T2/T3 定时驱动器：以单个 <see cref="System.Threading.Timer"/> 自调度
/// 驱动 <see cref="ApduConnection"/> 的超时检查，替代每连接一个常驻 <c>Task.Run</c> 轮询循环。
/// </summary>
/// <remarks>
/// <para>
/// 设计要点：
/// <list type="bullet">
/// <item><b>无常驻任务/线程</b>：仅一次性 Timer，回调跑完检查后按 <see cref="ApduConnection.GetSuggestedDelayMs"/>
/// 重排下一次；两次回调之间零占用，消除 <c>Task.Run</c> 常驻任务与每轮 <c>Task.Delay</c> 的 Timer+Task 分配。</item>
/// <item><b>重入保护</b>：<c>Interlocked.CompareExchange</c> 保证同一时刻最多一次检查在跑。</item>
/// <item><b>确定性释放</b>：<see cref="Dispose"/> 停止底层 Timer；一次性 Timer 语义下不会与在途回调竞争。</item>
/// </list>
/// </para>
/// <para>
/// 与连接状态机解耦：本类只负责"到点调用一次检查并重排"；协议判定（是否超时/发 TESTFR）
/// 全部在 <see cref="ApduConnection"/> 内。连接可在无定时器环境下被测试手动泵（现有测试即如此）。
/// </para>
/// </remarks>
internal sealed class Iec104TimerScheduler : IDisposable
{
    private readonly object _gate = new object();
    private Timer _timer;
    private readonly Func<CancellationToken, ValueTask<bool>> _check;
    private readonly Func<int> _delayProvider;
    private readonly Action _onFatal;
    private readonly CancellationToken _cancellationToken;
    private int _running;   // 1 = 一次检查正在执行（重入保护）
    private bool _disposed;

    /// <summary>
    /// 创建调度器并立即按连接当前截止时刻启动首轮调度。
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
        _onFatal = onFatal ?? throw new ArgumentNullException(nameof(onFatal));
        _cancellationToken = cancellationToken;

        _timer = new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);
        Change(Math.Max(1, _delayProvider()));
    }

    /// <summary>停止调度并释放底层 Timer。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var t = _timer;
            _timer = null;
            t?.Dispose();
        }
    }

    private void OnTimer(object state)
    {
        // 重入保护：同一时刻最多一次检查在跑。一次性 Timer 语义下，
        // 只有本回调重排后才会再次触发，正常不会并发；此处防御极端竞态。
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        _ = RunCheckAsync();
    }

    private async Task RunCheckAsync()
    {
        bool fatal;
        try
        {
            fatal = !await _check(_cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 连接终止：正常退出，不再调度。
            Interlocked.Exchange(ref _running, 0);
            return;
        }
        catch (Exception)
        {
            // 检查中的瞬态异常（如 sink 写入噪声）不应让调度器崩溃：
            // 视为非致命，继续按连接建议间隔调度，由连接自身的关闭路径处理致命情况。
            fatal = false;
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }

        if (fatal)
        {
            try
            {
                _onFatal();
            }
            catch { /* 关闭动作失败忽略，连接最终由上层清理 */ }
            return;
        }

        Change(Math.Max(1, _delayProvider()));
    }

    /// <summary>用 <paramref name="dueTimeMs"/> 重排底层 Timer；已释放则忽略。</summary>
    private void Change(int dueTimeMs)
    {
        Timer t;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            t = _timer;
        }
        t?.Change(dueTimeMs, Timeout.Infinite);
    }
}
