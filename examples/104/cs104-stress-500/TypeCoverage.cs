/*
 *  TypeCoverage.cs
 *
 *  Authoritative list of the IEC 60870-5-104 TypeIDs this stress test exercises,
 *  plus a factory that builds a sample InformationObject for each one and a
 *  factory that builds a "server response" ASDU for the commands the master
 *  fleet pushes at the simulated devices.
 *
 *  The factory mirrors tests-cs104/EncodeRoundtripTests.cs (which already covers
 *  the full built-in set) so that every value object here is known to round-trip
 *  through the library's own encoder/decoder.
 */

using System;
using IEC60870.Core;

namespace cs104_stress_500
{
    public static class TypeCoverage
    {
        // Monitored (M_*) + control (C_*) + parameter (P_*) + file (F_*) types that
        // are meaningful for a station-automation workload. F_SC_NB_1 (127) is an
        // optional variant the library does not model, so it is intentionally excluded.
        public static readonly TypeID[] All =
        {
            // ── Monitor direction: no timestamp ──
            TypeID.M_SP_NA_1, TypeID.M_SP_TA_1,
            TypeID.M_DP_NA_1, TypeID.M_DP_TA_1,
            TypeID.M_ST_NA_1, TypeID.M_ST_TA_1,
            TypeID.M_BO_NA_1, TypeID.M_BO_TA_1,
            TypeID.M_ME_NA_1, TypeID.M_ME_TA_1,
            TypeID.M_ME_NB_1, TypeID.M_ME_TB_1,
            TypeID.M_ME_NC_1, TypeID.M_ME_TC_1,
            TypeID.M_IT_NA_1, TypeID.M_IT_TA_1,
            TypeID.M_EP_TA_1, TypeID.M_EP_TB_1, TypeID.M_EP_TC_1,
            TypeID.M_PS_NA_1, TypeID.M_ME_ND_1,

            // ── Monitor direction: CP56Time2a ──
            TypeID.M_SP_TB_1, TypeID.M_DP_TB_1, TypeID.M_ST_TB_1, TypeID.M_BO_TB_1,
            TypeID.M_ME_TD_1, TypeID.M_ME_TE_1, TypeID.M_ME_TF_1, TypeID.M_IT_TB_1,
            TypeID.M_EP_TD_1, TypeID.M_EP_TE_1, TypeID.M_EP_TF_1,

            // ── Control direction ──
            TypeID.C_SC_NA_1, TypeID.C_DC_NA_1, TypeID.C_RC_NA_1,
            TypeID.C_SE_NA_1, TypeID.C_SE_NB_1, TypeID.C_SE_NC_1, TypeID.C_BO_NA_1,
            TypeID.C_SC_TA_1, TypeID.C_DC_TA_1, TypeID.C_RC_TA_1,
            TypeID.C_SE_TA_1, TypeID.C_SE_TB_1, TypeID.C_SE_TC_1, TypeID.C_BO_TA_1,

            // ── System ──
            TypeID.M_EI_NA_1,
            TypeID.C_IC_NA_1, TypeID.C_CI_NA_1, TypeID.C_RD_NA_1, TypeID.C_CS_NA_1,
            TypeID.C_TS_NA_1, TypeID.C_TS_TA_1, TypeID.C_RP_NA_1, TypeID.C_CD_NA_1,

            // ── Parameters ──
            TypeID.P_ME_NA_1, TypeID.P_ME_NB_1, TypeID.P_ME_NC_1, TypeID.P_AC_NA_1,

            // ── File transfer ──
            TypeID.F_FR_NA_1, TypeID.F_SR_NA_1, TypeID.F_SC_NA_1,
            TypeID.F_LS_NA_1, TypeID.F_AF_NA_1, TypeID.F_SG_NA_1, TypeID.F_DR_TA_1
        };

        // Command types that a master can drive the device with. For each, the
        // device simply echoes back an ACTIVATION_CON carrying the same command.
        public static readonly TypeID[] CommandTypes =
        {
            TypeID.C_SC_NA_1, TypeID.C_DC_NA_1, TypeID.C_RC_NA_1,
            TypeID.C_SE_NA_1, TypeID.C_SE_NB_1, TypeID.C_SE_NC_1, TypeID.C_BO_NA_1,
            TypeID.C_SC_TA_1, TypeID.C_DC_TA_1, TypeID.C_RC_TA_1,
            TypeID.C_SE_TA_1, TypeID.C_SE_TB_1, TypeID.C_SE_TC_1, TypeID.C_BO_TA_1
        };

        /// <summary>Whether the control command carries an explicit Select (预发/执行) bit (i.e. not C_BO_*).</summary>
        public static bool HasSelectBit(TypeID t)
            => t != TypeID.C_BO_NA_1 && t != TypeID.C_BO_TA_1;

        // Monitored types the device streams spontaneously and in response to a
        // general interrogation (all of the M_* types above).
        public static readonly TypeID[] MonitorTypes;

        static TypeCoverage()
        {
            var monitors = new System.Collections.Generic.List<TypeID>();
            foreach (var t in All)
            {
                if (t.ToString().StartsWith("M_"))
                {
                    monitors.Add(t);
                }
            }

            MonitorTypes = monitors.ToArray();
        }

        /// <summary>Build a sample information object of the requested type (IOA = baseIoa + slot).</summary>
        public static InformationObject MakeMonitorSample(TypeID typeId, int ioa, int seed)
        {
            var now = DateTime.Now;
            var ts56 = new CP56Time2a(now);
            var ts24 = new CP24Time2a(now.Minute, now.Second, now.Millisecond);
            var ts16 = new CP16Time2a(seed % 60000);
            var qd = QualityDescriptor.VALID();

            switch (typeId)
            {
                case TypeID.M_SP_NA_1: return new SinglePointInformation(ioa, (seed & 1) == 0, qd);
                case TypeID.M_SP_TA_1: return new SinglePointWithCP24Time2a(ioa, true, qd, ts24);
                case TypeID.M_SP_TB_1: return new SinglePointWithCP56Time2a(ioa, false, qd, ts56);
                case TypeID.M_DP_NA_1: return new DoublePointInformation(ioa, DoublePointValue.ON, qd);
                case TypeID.M_DP_TA_1: return new DoublePointWithCP24Time2a(ioa, DoublePointValue.OFF, qd, ts24);
                case TypeID.M_DP_TB_1: return new DoublePointWithCP56Time2a(ioa, DoublePointValue.INDETERMINATE, qd, ts56);
                case TypeID.M_ST_NA_1: return new StepPositionInformation(ioa, seed % 64 - 32, false, qd);
                case TypeID.M_ST_TA_1: return new StepPositionWithCP24Time2a(ioa, 7, true, qd, ts24);
                case TypeID.M_ST_TB_1: return new StepPositionWithCP56Time2a(ioa, -3, false, qd, ts56);
                case TypeID.M_BO_NA_1: return new Bitstring32(ioa, 0xA55A5AA5u, qd);
                case TypeID.M_BO_TA_1: return new Bitstring32WithCP24Time2a(ioa, 0x12345678u, qd, ts24);
                case TypeID.M_BO_TB_1: return new Bitstring32WithCP56Time2a(ioa, 0x87654321u, qd, ts56);
                case TypeID.M_ME_NA_1: return new MeasuredValueNormalized(ioa, -0.25f, qd);
                case TypeID.M_ME_TA_1: return new MeasuredValueNormalizedWithCP24Time2a(ioa, 0.5f, qd, ts24);
                case TypeID.M_ME_TD_1: return new MeasuredValueNormalizedWithCP56Time2a(ioa, 0.75f, qd, ts56);
                case TypeID.M_ME_ND_1: return new MeasuredValueNormalizedWithoutQuality(ioa, -1.0f);
                case TypeID.M_ME_NB_1: return new MeasuredValueScaled(ioa, seed * 7 % 32000 - 16000, qd);
                case TypeID.M_ME_TB_1: return new MeasuredValueScaledWithCP24Time2a(ioa, 12345, qd, ts24);
                case TypeID.M_ME_TE_1: return new MeasuredValueScaledWithCP56Time2a(ioa, -1, qd, ts56);
                case TypeID.M_ME_NC_1: return new MeasuredValueShort(ioa, seed * 0.5f, qd);
                case TypeID.M_ME_TC_1: return new MeasuredValueShortWithCP24Time2a(ioa, -2.5f, qd, ts24);
                case TypeID.M_ME_TF_1: return new MeasuredValueShortWithCP56Time2a(ioa, 99.5f, qd, ts56);
                case TypeID.M_IT_NA_1: return new IntegratedTotals(ioa, MakeBcr(seed));
                case TypeID.M_IT_TA_1: return new IntegratedTotalsWithCP24Time2a(ioa, MakeBcr(seed), ts24);
                case TypeID.M_IT_TB_1: return new IntegratedTotalsWithCP56Time2a(ioa, MakeBcr(seed), ts56);
                case TypeID.M_EP_TA_1: return new EventOfProtectionEquipment(ioa, new SingleEvent(0x01), ts16, ts24);
                case TypeID.M_EP_TD_1: return new EventOfProtectionEquipmentWithCP56Time2a(ioa, new SingleEvent(0x02), ts16, ts56);
                case TypeID.M_EP_TB_1: return new PackedStartEventsOfProtectionEquipment(ioa, new StartEvent(0x03), new QualityDescriptorP(), ts16, ts24);
                case TypeID.M_EP_TE_1: return new PackedStartEventsOfProtectionEquipmentWithCP56Time2a(ioa, new StartEvent(0x04), new QualityDescriptorP(), ts16, ts56);
                case TypeID.M_EP_TC_1: return new PackedOutputCircuitInfo(ioa, new OutputCircuitInfo(), new QualityDescriptorP(), ts16, ts24);
                case TypeID.M_EP_TF_1: return new PackedOutputCircuitInfoWithCP56Time2a(ioa, new OutputCircuitInfo(), new QualityDescriptorP(), ts16, ts56);
                case TypeID.M_PS_NA_1: return new PackedSinglePointWithSCD(ioa, new StatusAndStatusChangeDetection(), qd);
                case TypeID.M_EI_NA_1: return new EndOfInitialization(0x04);
                default:
                    throw new ArgumentOutOfRangeException(nameof(typeId), typeId, "no monitor sample");
            }
        }

        /// <summary>Build a sample control command of the requested type and select phase.</summary>
        public static InformationObject MakeCommandSample(TypeID typeId, int ioa, bool select)
        {
            var ts56 = new CP56Time2a(DateTime.Now);
            switch (typeId)
            {
                case TypeID.C_SC_NA_1: return new SingleCommand(ioa, true, select, 0);
                case TypeID.C_SC_TA_1: return new SingleCommandWithCP56Time2a(ioa, false, select, 0, ts56);
                case TypeID.C_DC_NA_1: return new DoubleCommand(ioa, 1, select, 0);
                case TypeID.C_DC_TA_1: return new DoubleCommandWithCP56Time2a(ioa, 0, select, 0, ts56);
                case TypeID.C_RC_NA_1: return new StepCommand(ioa, StepCommandValue.LOWER, select, 0);
                case TypeID.C_RC_TA_1: return new StepCommandWithCP56Time2a(ioa, StepCommandValue.HIGHER, select, 0, ts56);
                case TypeID.C_SE_NA_1: return new SetpointCommandNormalized(ioa, 0.5f, new SetpointCommandQualifier(select, 0));
                case TypeID.C_SE_TA_1: return new SetpointCommandNormalizedWithCP56Time2a(ioa, -0.5f, new SetpointCommandQualifier(select, 0), ts56);
                case TypeID.C_SE_NB_1: return new SetpointCommandScaled(ioa, new ScaledValue(-32000), new SetpointCommandQualifier(select, 0));
                case TypeID.C_SE_TB_1: return new SetpointCommandScaledWithCP56Time2a(ioa, new ScaledValue(32000), new SetpointCommandQualifier(select, 0), ts56);
                case TypeID.C_SE_NC_1: return new SetpointCommandShort(ioa, 123.456f, new SetpointCommandQualifier(select, 0));
                case TypeID.C_SE_TC_1: return new SetpointCommandShortWithCP56Time2a(ioa, -0.001f, new SetpointCommandQualifier(select, 0), ts56);
                case TypeID.C_BO_NA_1: return new Bitstring32Command(ioa, 0xDEADBEEFu);
                case TypeID.C_BO_TA_1: return new Bitstring32CommandWithCP56Time2a(ioa, 0x0F0F0F0Fu, ts56);
                default:
                    throw new ArgumentOutOfRangeException(nameof(typeId), typeId, "no command sample");
            }
        }

        private static BinaryCounterReading MakeBcr(int seed)
        {
            return new BinaryCounterReading
            {
                Value = seed * 1000,
                SequenceNumber = seed & 0x1F
            };
        }
    }
}
