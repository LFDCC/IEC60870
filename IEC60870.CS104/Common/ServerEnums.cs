//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS104;

/// <summary>
/// 服务端事件分发模式（冗余组支持）。决定自发 ASDU 如何进入事件队列、
/// 以及同一队列在多个连接间如何选主。
/// </summary>
public enum ServerMode
{
    /// <summary>
    /// 单一冗余组：全部连接共享一个事件队列，同一时刻仅一个连接处于活动态，
    /// 其余为备用；活动连接断开时自动切换到下一个备用连接，未确认事件不丢失。
    /// </summary>
    SINGLE_REDUNDANCY_GROUP,

    /// <summary>
    /// 每连接独立冗余组：每个连接拥有自己的事件队列，互不影响，
    /// 适合简单的多客户端服务端（各客户端收到各自的事件流）。
    /// </summary>
    CONNECTION_IS_REDUNDANCY_GROUP,

    /// <summary>
    /// 多冗余组：显式注册若干 <see cref="RedundancyGroup"/>，每组按客户端 IP 归属，
    /// 组内共享一个事件队列且仅一个活动连接；未匹配到任何组的连接落入 catch-all 组。
    /// </summary>
    MULTIPLE_REDUNDANCY_GROUPS
}

/// <summary>
/// 事件队列满时的入队策略。
/// </summary>
public enum EnqueueMode
{
    /// <summary>丢弃最旧的一条 ASDU，为新 ASDU 腾出空间（保证最新事件可见）。</summary>
    REMOVE_OLDEST,

    /// <summary>队列满时直接忽略新 ASDU，不覆盖已有事件。</summary>
    IGNORE,

    /// <summary>队列满时抛出 <see cref="ASDUQueueException"/>。</summary>
    THROW_EXCEPTION
}

/// <summary>
/// 事件队列相关异常（当前仅队列满且 <see cref="EnqueueMode.THROW_EXCEPTION"/> 时抛出）。
/// </summary>
public class ASDUQueueException : Exception
{
    public ASDUQueueException()
    {
    }

    public ASDUQueueException(string message)
        : base(message)
    {
    }

    public ASDUQueueException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
