//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS104;

/// <summary>
/// 「发送控制命令并等待其 ACT-CON」的兼容辅助器。
/// v5 起等待逻辑已内建于 <see cref="Iec104Client.SendControlCommandAndWaitAsync"/>
/// （内置匹配器,超时由 <see cref="Iec104Client.CommandConfirmationTimeout"/> 配置）,
/// 本类保留为薄包装以兼容既有用法;新代码建议直接使用客户端内建方法。
/// </summary>
public sealed class ControlWaiter : IDisposable
{
    private readonly Iec104Client _client;

    /// <summary>包装一个客户端。</summary>
    public ControlWaiter(Iec104Client client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>
    /// 发送一条控制命令 ASDU，并异步等待其 ACTIVATION_CON（或否定确认）。
    /// 内部用 Select 位区分"预发"与"执行"的确认，二者可顺序串行调用。
    /// </summary>
    /// <param name="cot">发起控制用 ACTIVATION。</param>
    /// <param name="ca">公共地址。</param>
    /// <param name="io">控制命令信息对象（SingleCommand/DoubleCommand/Setpoint...），其 Select 位决定预发或执行。</param>
    /// <param name="cancellationToken">外部取消。</param>
    /// <returns>服务端回送的确认（含是否否定）。</returns>
    /// <exception cref="TimeoutException">超过客户端配置的确认超时未收到匹配确认时抛出，避免调用方永久挂起。</exception>
    public Task<ControlConfirmation> SendControlCommandAndWaitAsync(
        CauseOfTransmission cot, int ca, InformationObject io,
        CancellationToken cancellationToken = default)
        => _client.SendControlCommandAndWaitAsync(cot, ca, io, cancellationToken);

    /// <summary>兼容占位:等待逻辑已内建于客户端,本对象无需解绑资源。幂等可重复调用。</summary>
    public void Dispose()
    {
    }
}
