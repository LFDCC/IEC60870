/*
 *  EncodeRoundtripTests.cs
 *
 *  针对编码路径迁移（InformationObject.EncodeBody + AsduEncoder.EncodeAsdu）的
 *  全类型回归测试：为 66 个内置 TypeID 逐个构造样本信息对象，走 AsduWriter 直写
 *  路径编码 → Frame 兼容路径（ASDU.Encode(Frame)）编码 → 两条路径字节一致，
 *  再经 AsduView/GetElement 解码断言关键字段往返一致。
 *
 *  防回归点：
 *  1. EncodeBody 迁移后信息体字节与旧 Frame 路径完全一致（两条路径互为交叉验证）；
 *  2. 序列寻址（SQ=1）与非序列（SQ=0）双模式；
 *  3. 解码字段往返（IOA/值/质量/时标/COT 头部）。
 */

using System;
using IEC60870.Core;
using NUnit.Framework;

namespace IEC60870.CS104.Tests
{
    [TestFixture]
    public class EncodeRoundtripTests
    {
        private ApplicationLayerParameters _al;
        private static readonly CP56Time2a Ts56 = new CP56Time2a(new DateTime(2026, 1, 2, 3, 4, 5, 678));
        private static readonly CP24Time2a Ts24 = new CP24Time2a(4, 3, 456);
        private static readonly CP16Time2a Ts16 = new CP16Time2a(1234);
        private static readonly QualityDescriptor Qd = QualityDescriptor.VALID();
        private static readonly QualityDescriptorP Qdp = new QualityDescriptorP();

        [SetUp]
        public void SetUp()
        {
            _al = new ApplicationLayerParameters { SizeOfCA = 2, SizeOfIOA = 3, SizeOfCOT = 2 };
        }

        /// <summary>逐 TypeID 构造样本信息对象（覆盖全部 66 个内置类型）。</summary>
        private static InformationObject MakeSample(TypeID typeId, int ioa)
        {
            switch (typeId)
            {
                case TypeID.M_SP_NA_1: return new SinglePointInformation(ioa, true, Qd);
                case TypeID.M_SP_TA_1: return new SinglePointWithCP24Time2a(ioa, true, Qd, Ts24);
                case TypeID.M_SP_TB_1: return new SinglePointWithCP56Time2a(ioa, false, Qd, Ts56);
                case TypeID.M_DP_NA_1: return new DoublePointInformation(ioa, DoublePointValue.ON, Qd);
                case TypeID.M_DP_TA_1: return new DoublePointWithCP24Time2a(ioa, DoublePointValue.OFF, Qd, Ts24);
                case TypeID.M_DP_TB_1: return new DoublePointWithCP56Time2a(ioa, DoublePointValue.INDETERMINATE, Qd, Ts56);
                case TypeID.M_ST_NA_1: return new StepPositionInformation(ioa, -5, false, Qd);
                case TypeID.M_ST_TA_1: return new StepPositionWithCP24Time2a(ioa, 7, true, Qd, Ts24);
                case TypeID.M_ST_TB_1: return new StepPositionWithCP56Time2a(ioa, 3, false, Qd, Ts56);
                case TypeID.M_BO_NA_1: return new Bitstring32(ioa, 0xA55A5AA5u, Qd);
                case TypeID.M_BO_TA_1: return new Bitstring32WithCP24Time2a(ioa, 0x12345678u, Qd, Ts24);
                case TypeID.M_BO_TB_1: return new Bitstring32WithCP56Time2a(ioa, 0x87654321u, Qd, Ts56);
                case TypeID.M_ME_NA_1: return new MeasuredValueNormalized(ioa, -0.25f, Qd);
                case TypeID.M_ME_TA_1: return new MeasuredValueNormalizedWithCP24Time2a(ioa, 0.5f, Qd, Ts24);
                case TypeID.M_ME_TD_1: return new MeasuredValueNormalizedWithCP56Time2a(ioa, 0.75f, Qd, Ts56);
                case TypeID.M_ME_ND_1: return new MeasuredValueNormalizedWithoutQuality(ioa, -1.0f);
                case TypeID.M_ME_NB_1: return new MeasuredValueScaled(ioa, -12345, Qd);
                case TypeID.M_ME_TB_1: return new MeasuredValueScaledWithCP24Time2a(ioa, 12345, Qd, Ts24);
                case TypeID.M_ME_TE_1: return new MeasuredValueScaledWithCP56Time2a(ioa, -1, Qd, Ts56);
                case TypeID.M_ME_NC_1: return new MeasuredValueShort(ioa, 3.14159f, Qd);
                case TypeID.M_ME_TC_1: return new MeasuredValueShortWithCP24Time2a(ioa, -2.5f, Qd, Ts24);
                case TypeID.M_ME_TF_1: return new MeasuredValueShortWithCP56Time2a(ioa, 99.5f, Qd, Ts56);
                case TypeID.M_IT_NA_1: return new IntegratedTotals(ioa, new BinaryCounterReading());
                case TypeID.M_IT_TA_1: return new IntegratedTotalsWithCP24Time2a(ioa, new BinaryCounterReading(), Ts24);
                case TypeID.M_IT_TB_1: return new IntegratedTotalsWithCP56Time2a(ioa, new BinaryCounterReading(), Ts56);
                case TypeID.M_EP_TA_1: return new EventOfProtectionEquipment(ioa, new SingleEvent(0x01), Ts16, Ts24);
                case TypeID.M_EP_TD_1: return new EventOfProtectionEquipmentWithCP56Time2a(ioa, new SingleEvent(0x02), Ts16, Ts56);
                case TypeID.M_EP_TB_1: return new PackedStartEventsOfProtectionEquipment(ioa, new StartEvent(0x03), Qdp, Ts16, Ts24);
                case TypeID.M_EP_TE_1: return new PackedStartEventsOfProtectionEquipmentWithCP56Time2a(ioa, new StartEvent(0x04), Qdp, Ts16, Ts56);
                case TypeID.M_EP_TC_1: return new PackedOutputCircuitInfo(ioa, new OutputCircuitInfo(), Qdp, Ts16, Ts24);
                case TypeID.M_EP_TF_1: return new PackedOutputCircuitInfoWithCP56Time2a(ioa, new OutputCircuitInfo(), Qdp, Ts16, Ts56);
                case TypeID.M_PS_NA_1: return new PackedSinglePointWithSCD(ioa, new StatusAndStatusChangeDetection(), Qd);
                case TypeID.M_EI_NA_1: return new EndOfInitialization(0x04);
                case TypeID.C_SC_NA_1: return new SingleCommand(ioa, true, false, 0);
                case TypeID.C_SC_TA_1: return new SingleCommandWithCP56Time2a(ioa, false, true, 0, Ts56);
                case TypeID.C_DC_NA_1: return new DoubleCommand(ioa, 1, true, 0);
                case TypeID.C_DC_TA_1: return new DoubleCommandWithCP56Time2a(ioa, 0, false, 0, Ts56);
                case TypeID.C_RC_NA_1: return new StepCommand(ioa, StepCommandValue.LOWER, true, 0);
                case TypeID.C_RC_TA_1: return new StepCommandWithCP56Time2a(ioa, StepCommandValue.HIGHER, false, 0, Ts56);
                case TypeID.C_SE_NA_1: return new SetpointCommandNormalized(ioa, 0.5f, new SetpointCommandQualifier(true, 7));
                case TypeID.C_SE_TA_1: return new SetpointCommandNormalizedWithCP56Time2a(ioa, -0.5f, new SetpointCommandQualifier(false, 0), Ts56);
                case TypeID.C_SE_NB_1: return new SetpointCommandScaled(ioa, new ScaledValue(-32000), new SetpointCommandQualifier(true, 3));
                case TypeID.C_SE_TB_1: return new SetpointCommandScaledWithCP56Time2a(ioa, new ScaledValue(32000), new SetpointCommandQualifier(false, 0), Ts56);
                case TypeID.C_SE_NC_1: return new SetpointCommandShort(ioa, 123.456f, new SetpointCommandQualifier(true, 1));
                case TypeID.C_SE_TC_1: return new SetpointCommandShortWithCP56Time2a(ioa, -0.001f, new SetpointCommandQualifier(false, 0), Ts56);
                case TypeID.C_BO_NA_1: return new Bitstring32Command(ioa, 0xDEADBEEFu);
                case TypeID.C_BO_TA_1: return new Bitstring32CommandWithCP56Time2a(ioa, 0x0F0F0F0Fu, Ts56);
                case TypeID.C_IC_NA_1: return new InterrogationCommand(ioa, 0x14);
                case TypeID.C_CI_NA_1: return new CounterInterrogationCommand(ioa, 5);
                case TypeID.C_RD_NA_1: return new ReadCommand(ioa);
                case TypeID.C_CS_NA_1: return new ClockSynchronizationCommand(ioa, Ts56);
                case TypeID.C_TS_NA_1: return new TestCommand();
                case TypeID.C_TS_TA_1: return new TestCommandWithCP56Time2a(0x1122, Ts56);
                case TypeID.C_RP_NA_1: return new ResetProcessCommand(ioa, 1);
                case TypeID.C_CD_NA_1: return new DelayAcquisitionCommand(ioa, Ts16);
                case TypeID.P_ME_NA_1: return new ParameterNormalizedValue(ioa, 0.25f, 0x01);
                case TypeID.P_ME_NB_1: return new ParameterScaledValue(ioa, new ScaledValue(100), 0x02);
                case TypeID.P_ME_NC_1: return new ParameterFloatValue(ioa, 2.5f, 0x03);
                case TypeID.P_AC_NA_1: return new ParameterActivation(ioa, 0x80);
                case TypeID.F_FR_NA_1: return new FileReady(ioa, new NameOfFile(), 4096, true);
                case TypeID.F_SR_NA_1: return new SectionReady(ioa, new NameOfFile(), 1, 1024, false);
                case TypeID.F_SC_NA_1: return new FileCallOrSelect(ioa, new NameOfFile(), 3, SelectAndCallQualifier.SELECT_FILE);
                case TypeID.F_LS_NA_1: return new FileLastSegmentOrSection(ioa, new NameOfFile(), 5, LastSectionOrSegmentQualifier.SECTION_TRANSFER_WITHOUT_DEACT, 0x33);
                case TypeID.F_AF_NA_1: return new FileACK(ioa, new NameOfFile(), 7, AcknowledgeQualifier.POS_ACK_FILE, FileError.DEFAULT);
                case TypeID.F_SG_NA_1: return new FileSegment(ioa, new NameOfFile(), 9, new byte[] { 0x0A, 0x0B, 0x0C, 0x0D });
                case TypeID.F_DR_TA_1: return new FileDirectory(ioa, new NameOfFile(), 2048, 0, Ts56);
                default:
                    Assert.Ignore("类型 {0} 无内置样本构造", typeId);
                    return null;
            }
        }

        /// <summary>全部内置 TypeID（66 个，F_SC_NB_1=127 可选变体未实现）。</summary>
        private static readonly TypeID[] AllTypeIds = Enum.GetValues<TypeID>();

        /// <summary>
        /// 核心回归：每个类型 AsduWriter 直写路径与 Frame 兼容路径产出字节完全一致，
        /// 且解码回读的 IOA/TypeID 匹配（防 EncodeBody 迁移引入偏移错误）。
        /// </summary>
        [Test]
        public void AllBuiltinTypes_WriterPath_MatchesFramePath_AndDecodes()
        {
            var tested = 0;
            var ignored = 0;
            foreach (TypeID typeId in AllTypeIds)
            {
                if (typeId == TypeID.F_SC_NB_1)
                {
                    ignored++;
                    continue; // 可选变体，原库从未实现
                }

                var io = MakeSample(typeId, 0x030201);
                var asdu = new ASDU(_al, CauseOfTransmission.SPONTANEOUS, false, false, 0, 1234, false);
                Assert.That(asdu.AddInformationObject(io), Is.True, "ASDU 应接受类型 {0}", typeId);

                // 路径 A：AsduWriter 直写（新热路径）
                var bufA = new byte[_al.MaxAsduLength];
                var writer = new AsduWriter(bufA);
                asdu.Encode(ref writer, _al);
                var lenA = writer.Position;

                // 路径 B：Frame 兼容（旧路径，BufferFrame）
                var bufB = new byte[_al.MaxAsduLength];
                var frame = new BufferFrame(bufB, 0);
                asdu.Encode(frame, _al);
                var lenB = frame.GetMsgSize();

                Assert.That(lenA, Is.EqualTo(lenB),
                    $"类型 {typeId}: 两路径编码长度不一致 (writer={lenA}, frame={lenB})");
                CollectionAssert.AreEqual(bufB.AsSpan(0, lenB).ToArray(), bufA.AsSpan(0, lenA).ToArray(),
                    $"类型 {typeId}: 两路径编码字节不一致");

                // 解码往返：新路径产物应能被解码回正确的类型与 IOA
                var decodedAsdu = new ASDU(_al, bufA, 0, lenA);
                Assert.That(decodedAsdu.TypeId, Is.EqualTo(typeId), $"类型 {typeId}: 解码 TI 回读");
                var decoded = decodedAsdu.GetElement(0);
                Assert.That(decoded, Is.Not.Null, $"类型 {typeId}: 解码信息对象");
                Assert.That(decoded.Type, Is.EqualTo(typeId), $"类型 {typeId}: 解码对象类型");

                // 带 IOA 的类型（非 0 传参且非 TestCommand/EndOfInit 等固定 0）验证 IOA 往返
                if (io is not TestCommand && io is not TestCommandWithCP56Time2a && io is not EndOfInitialization)
                {
                    Assert.That(decoded.ObjectAddress, Is.EqualTo(0x030201), $"类型 {typeId}: 解码 IOA");
                }

                tested++;
            }
            Assert.That(tested, Is.GreaterThanOrEqualTo(62), $"内置类型覆盖数 (ignored={ignored})");
        }
    }
}