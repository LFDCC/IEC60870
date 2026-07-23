/*
 *  CommandBuilder.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 *
 *  See COPYING file for the complete license text.
 */


using IEC60870.Core.InformationObjects;
using IEC60870.Core.Time;
namespace IEC60870.Core
{
    /// <summary>
    /// 构造标准 IEC 60870-5 命令 ASDU 的内部辅助。IEC 60870-5-104（异步）与 IEC 60870-5-101（同步）
    /// 两类客户端共用同一套构造逻辑，避免重复实现。每个方法返回已填好单个 InformationObject 的
    /// <see cref="ASDU"/>，调用方只需负责发送（I 帧）。
    /// </summary>
    /// <remarks>
    /// OA（源发地址）统一取自 <c>ApplicationLayerParameters.OA</c>，与原库语义一致。
    /// </remarks>
    public class CommandBuilder
    {
        /// <summary>构造一个携带单个命令 InformationObject 的 ASDU。</summary>
        internal static ASDU Build(ApplicationLayerParameters al, CauseOfTransmission cot, int ca, InformationObject io)
        {
            var asdu = new ASDU(al, cot, false, false, (byte)al.OA, ca, false);
            asdu.AddInformationObject(io);
            return asdu;
        }

        // ── C_IC_NA_1 (100) 总召唤 ──────────────────────────────────
        public static ASDU Interrogation(ApplicationLayerParameters al, CauseOfTransmission cot, int ca, byte qoi)
            => Build(al, cot, ca, new InterrogationCommand(0, qoi));

        // ── C_CI_NA_1 (101) 计数量总召唤 ────────────────────────────
        public static ASDU CounterInterrogation(ApplicationLayerParameters al, CauseOfTransmission cot, int ca, byte qcc)
            => Build(al, cot, ca, new CounterInterrogationCommand(0, qcc));

        // ── C_RD_NA_1 (102) 读命令（COT 固定 REQUEST）──────────────
        public static ASDU Read(ApplicationLayerParameters al, int ca, int ioa)
            => Build(al, CauseOfTransmission.REQUEST, ca, new ReadCommand(ioa));

        // ── C_CS_NA_1 (103) 时钟同步 ───────────────────────────────
        public static ASDU ClockSync(ApplicationLayerParameters al, int ca, CP56Time2a time)
            => Build(al, CauseOfTransmission.ACTIVATION, ca, new ClockSynchronizationCommand(0, time));

        // ── C_TS_NA_1 (104) 测试命令 ───────────────────────────────
        public static ASDU Test(ApplicationLayerParameters al, int ca)
            => Build(al, CauseOfTransmission.ACTIVATION, ca, new TestCommand());

        // ── C_TS_TA_1 (107) 带时标测试命令 ─────────────────────────
        public static ASDU TestWithCP56Time2a(ApplicationLayerParameters al, int ca, ushort tsc, CP56Time2a time)
            => Build(al, CauseOfTransmission.ACTIVATION, ca, new TestCommandWithCP56Time2a(tsc, time));

        // ── C_RP_NA_1 (105) 复位进程 ───────────────────────────────
        public static ASDU ResetProcess(ApplicationLayerParameters al, CauseOfTransmission cot, int ca, byte qrp)
            => Build(al, cot, ca, new ResetProcessCommand(0, qrp));

        // ── C_CD_NA_1 (106) 延时获取 ───────────────────────────────
        public static ASDU DelayAcquisition(ApplicationLayerParameters al, CauseOfTransmission cot, int ca, CP16Time2a delay)
            => Build(al, cot, ca, new DelayAcquisitionCommand(0, delay));

        // ── 通用控制命令（typeId 须与 sc 类型匹配）─────────────────
        public static ASDU Control(ApplicationLayerParameters al, CauseOfTransmission cot, int ca, InformationObject sc)
            => Build(al, cot, ca, sc);
    }
}
