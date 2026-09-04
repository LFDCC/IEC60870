//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS104;

/// <summary>
/// IEC 60870-5-104 主站标准命令动词扩展（对齐 LFDCC.Modbus 的扩展方法 API 风格）。
/// 全部方法构造对应的标准 C_* 命令 ASDU 并经 I 帧发送；k 窗口满时内部异步背压，
/// 调用方无需阻塞；连接断开等异常由 <see cref="IIec104Master.SendAsync"/> 原样抛出。
/// </summary>
public static class Iec104MasterExtension
{
    #region 标准命令

    /// <summary>发送总召唤命令（C_IC_NA_1，typeID 100）。</summary>
    /// <param name="master">主站客户端。</param>
    /// <param name="cot">Cause of transmission</param>
    /// <param name="ca">Common address</param>
    /// <param name="qoi">Qualifier of interrogation（20 = 站召唤）</param>
    public static Task SendInterrogationCommandAsync(this IIec104Master master, CauseOfTransmission cot, int ca, byte qoi, CancellationToken cancellationToken = default)
        => master.SendAsync(CommandBuilder.Interrogation(master.Parameters, cot, ca, qoi), cancellationToken);

    /// <summary>发送计数量总召唤命令（C_CI_NA_1，typeID 101）。</summary>
    public static Task SendCounterInterrogationCommandAsync(this IIec104Master master, CauseOfTransmission cot, int ca, byte qcc, CancellationToken cancellationToken = default)
        => master.SendAsync(CommandBuilder.CounterInterrogation(master.Parameters, cot, ca, qcc), cancellationToken);

    /// <summary>发送读命令（C_RD_NA_1，typeID 102）。COT 固定 REQUEST，用于循环读取单个点。</summary>
    public static Task SendReadCommandAsync(this IIec104Master master, int ca, int ioa, CancellationToken cancellationToken = default)
        => master.SendAsync(CommandBuilder.Read(master.Parameters, ca, ioa), cancellationToken);

    /// <summary>发送时钟同步命令（C_CS_NA_1，typeID 103）。</summary>
    public static Task SendClockSyncCommandAsync(this IIec104Master master, int ca, CP56Time2a time, CancellationToken cancellationToken = default)
        => master.SendAsync(CommandBuilder.ClockSync(master.Parameters, ca, time), cancellationToken);

    /// <summary>发送测试命令（C_TS_NA_1，typeID 104）。</summary>
    public static Task SendTestCommandAsync(this IIec104Master master, int ca, CancellationToken cancellationToken = default)
        => master.SendAsync(CommandBuilder.Test(master.Parameters, ca), cancellationToken);

    /// <summary>发送带时标的测试命令（C_TS_TA_1，typeID 107）。</summary>
    public static Task SendTestCommandWithCP56Time2aAsync(this IIec104Master master, int ca, ushort tsc, CP56Time2a time, CancellationToken cancellationToken = default)
        => master.SendAsync(CommandBuilder.TestWithCP56Time2a(master.Parameters, ca, tsc, time), cancellationToken);

    /// <summary>发送复位进程命令（C_RP_NA_1，typeID 105）。</summary>
    public static Task SendResetProcessCommandAsync(this IIec104Master master, CauseOfTransmission cot, int ca, byte qrp, CancellationToken cancellationToken = default)
        => master.SendAsync(CommandBuilder.ResetProcess(master.Parameters, cot, ca, qrp), cancellationToken);

    /// <summary>发送延时获取命令（C_CD_NA_1，typeID 106）。</summary>
    public static Task SendDelayAcquisitionCommandAsync(this IIec104Master master, CauseOfTransmission cot, int ca, CP16Time2a delay, CancellationToken cancellationToken = default)
        => master.SendAsync(CommandBuilder.DelayAcquisition(master.Parameters, cot, ca, delay), cancellationToken);

    /// <summary>
    /// 发送通用控制命令。typeId 须与 sc 的类型匹配：
    /// C_SC_NA_1→SingleCommand、C_DC_NA_1→DoubleCommand、C_RC_NA_1→StepCommand、
    /// C_SC_TA_1→SingleCommandWithCP56Time2a、C_SE_NA_1→SetpointCommandNormalized、
    /// C_SE_NB_1→SetpointCommandScaled、C_SE_NC_1→SetpointCommandShort、C_BO_NA_1→Bitstring32Command。
    /// </summary>
    /// <param name="cot">Cause of transmission（发起控制序列用 ACTIVATION）</param>
    /// <param name="ca">Common address</param>
    /// <param name="sc">控制命令 InformationObject</param>
    public static Task SendControlCommandAsync(this IIec104Master master, CauseOfTransmission cot, int ca, InformationObject sc, CancellationToken cancellationToken = default)
        => master.SendAsync(CommandBuilder.Control(master.Parameters, cot, ca, sc), cancellationToken);

    #endregion 标准命令

    #region 发送并等待确认

    /// <summary>
    /// 发送一条控制命令 ASDU，并异步等待其 ACTIVATION_CON（或否定确认）——
    /// 「发送并等待确认」的一体内建入口。
    /// 内部用 Select 位区分"预发"与"执行"的确认，二者可顺序串行调用；
    /// 超时/取消不关闭连接，挂起槽位自动清理，迟到确认丢弃不串台。
    /// </summary>
    /// <param name="master">主站客户端。</param>
    /// <param name="cot">发起控制用 ACTIVATION。</param>
    /// <param name="ca">公共地址。</param>
    /// <param name="io">控制命令信息对象（SingleCommand/DoubleCommand/Setpoint...），其 Select 位决定预发或执行。</param>
    /// <param name="cancellationToken">外部取消。</param>
    /// <returns>服务端回送的确认（含是否否定）。</returns>
    /// <exception cref="TimeoutException">超过 <see cref="IIec104Master.CommandConfirmationTimeout"/> 未收到匹配确认时抛出，避免调用方永久挂起。</exception>
    public static async Task<ControlConfirmation> SendControlCommandAndWaitAsync(this IIec104Master master, CauseOfTransmission cot, int ca, InformationObject io, CancellationToken cancellationToken = default)
    {
        var tracker = ((Iec104Client)master).ConfirmationTracker;
        var selectBit = ControlConfirmMatcher.GetSelectBit(io);
        var key = ControlConfirmMatcher.MakeKey(io.Type, ca, io.ObjectAddress, selectBit);

        var tcs = new TaskCompletionSource<ControlConfirmation>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.Register(key, tcs);

        try
        {
            // 1) 发送命令（k 窗口满时内部异步背压，不阻塞线程）
            await master.SendControlCommandAsync(cot, ca, io, cancellationToken).ConfigureDefaultAwait();

            // 2) 等待服务端回送的 ACT-CON（带超时保护）
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(master.CommandConfirmationTimeout);
            try
            {
                return await tcs.Task.WaitAsync(cts.Token).ConfigureDefaultAwait();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"等待控制命令确认超时（{(selectBit ? "预发" : "执行")}）：" +
                    $"Type={io.Type}, IOA={io.ObjectAddress}, CA={ca}");
            }
        }
        finally
        {
            tracker.Unregister(key, tcs);
        }
    }

    #endregion 发送并等待确认
}
