/*
 *  AsduDecoding.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 *
 *  See COPYING file for the complete license text.
 */

using System;
using IEC60870.Core.InformationObjects;
using IEC60870.Core.File;



namespace IEC60870.Core
{
    // ──────────────────────────────────────────────────────────────────────
    //  Type-level dispatch primitives (single source of truth)
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Categorises a TypeID for payload-size calculation in <see cref="AsduDecoder"/>.
    /// </summary>
    internal enum DecodeKind
    {
        /// <summary>Monitor-direction type (M_*).</summary>
        Monitor,
        /// <summary>Command/control type (C_*, P_*).</summary>
        Command,
        /// <summary>File-transfer type (F_*).</summary>
        File,
    }

    /// <summary>
    /// Describes how to decode a single InformationObject of a given TypeID.
    /// </summary>
    internal readonly struct TypeDescriptor
    {
        /// <summary>Encoded payload size per element (excluding IOA).</summary>
        public readonly int PayloadSize;

        /// <summary>Whether this type is a monitor, command, or file type.</summary>
        public readonly DecodeKind Kind;

        /// <summary>Decode function.</summary>
        public readonly IoDecode Decode;

        public TypeDescriptor(int payloadSize, DecodeKind kind, IoDecode decode)
        {
            PayloadSize = payloadSize;
            Kind = kind;
            Decode = decode;
        }
    }

    /// <summary>
    /// Decodes an InformationObject from raw bytes.
    /// </summary>
    /// <param name="parameters">Application layer parameters.</param>
    /// <param name="msg">Raw payload.</param>
    /// <param name="startIndex">Offset in <paramref name="msg"/> to start decoding.</param>
    /// <param name="isSequence">Whether the element is part of a sequence (monitor types only; command/file types ignore this).</param>
    /// <returns>Decoded information object.</returns>
    internal delegate InformationObject IoDecode(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence);

    // ──────────────────────────────────────────────────────────────────────
    //  Decoder logic
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Internal decoder for ASDU messages. Provides payload-size computation and
    /// element-decode logic extracted from <see cref="ASDU"/>. All methods are
    /// internal static and operate on an <see cref="ASDU"/> instance passed by parameter.
    /// </summary>
    internal static class AsduDecoder
    {
        // ── TypeID → TypeDescriptor registry (single source of truth) ──

        private static readonly TypeDescriptor[] _descriptors = BuildDescriptors();

        private static TypeDescriptor[] BuildDescriptors()
        {
            var t = new TypeDescriptor[256];

            // Helper: register a single type
            void Reg(TypeID id, int payloadSize, DecodeKind kind, IoDecode decode)
            {
                t[(int)id] = new TypeDescriptor(payloadSize, kind, decode);
            }

            // ── Monitor direction (M_*) — 4-param constructors (params, msg, startIndex, isSequence) ──
            Reg(TypeID.M_SP_NA_1, 1, DecodeKind.Monitor, (p, m, i, s) => new SinglePointInformation(p, m, i, s));
            Reg(TypeID.M_SP_TA_1, 4, DecodeKind.Monitor, (p, m, i, s) => new SinglePointWithCP24Time2a(p, m, i, s));
            Reg(TypeID.M_SP_TB_1, 8, DecodeKind.Monitor, (p, m, i, s) => new SinglePointWithCP56Time2a(p, m, i, s));
            Reg(TypeID.M_DP_NA_1, 1, DecodeKind.Monitor, (p, m, i, s) => new DoublePointInformation(p, m, i, s));
            Reg(TypeID.M_DP_TA_1, 4, DecodeKind.Monitor, (p, m, i, s) => new DoublePointWithCP24Time2a(p, m, i, s));
            Reg(TypeID.M_DP_TB_1, 8, DecodeKind.Monitor, (p, m, i, s) => new DoublePointWithCP56Time2a(p, m, i, s));
            Reg(TypeID.M_ST_NA_1, 2, DecodeKind.Monitor, (p, m, i, s) => new StepPositionInformation(p, m, i, s));
            Reg(TypeID.M_ST_TA_1, 5, DecodeKind.Monitor, (p, m, i, s) => new StepPositionWithCP24Time2a(p, m, i, s));
            Reg(TypeID.M_ST_TB_1, 9, DecodeKind.Monitor, (p, m, i, s) => new StepPositionWithCP56Time2a(p, m, i, s));
            Reg(TypeID.M_BO_NA_1, 5, DecodeKind.Monitor, (p, m, i, s) => new Bitstring32(p, m, i, s));
            Reg(TypeID.M_BO_TA_1, 8, DecodeKind.Monitor, (p, m, i, s) => new Bitstring32WithCP24Time2a(p, m, i, s));
            Reg(TypeID.M_BO_TB_1, 12, DecodeKind.Monitor, (p, m, i, s) => new Bitstring32WithCP56Time2a(p, m, i, s));
            Reg(TypeID.M_ME_ND_1, 2, DecodeKind.Monitor, (p, m, i, s) => new MeasuredValueNormalizedWithoutQuality(p, m, i, s));
            Reg(TypeID.M_ME_NA_1, 3, DecodeKind.Monitor, (p, m, i, s) => new MeasuredValueNormalized(p, m, i, s));
            Reg(TypeID.M_ME_TA_1, 6, DecodeKind.Monitor, (p, m, i, s) => new MeasuredValueNormalizedWithCP24Time2a(p, m, i, s));
            Reg(TypeID.M_ME_TD_1, 10, DecodeKind.Monitor, (p, m, i, s) => new MeasuredValueNormalizedWithCP56Time2a(p, m, i, s));
            Reg(TypeID.M_ME_NB_1, 3, DecodeKind.Monitor, (p, m, i, s) => new MeasuredValueScaled(p, m, i, s));
            Reg(TypeID.M_ME_TB_1, 6, DecodeKind.Monitor, (p, m, i, s) => new MeasuredValueScaledWithCP24Time2a(p, m, i, s));
            Reg(TypeID.M_ME_TE_1, 10, DecodeKind.Monitor, (p, m, i, s) => new MeasuredValueScaledWithCP56Time2a(p, m, i, s));
            Reg(TypeID.M_ME_NC_1, 5, DecodeKind.Monitor, (p, m, i, s) => new MeasuredValueShort(p, m, i, s));
            Reg(TypeID.M_ME_TC_1, 8, DecodeKind.Monitor, (p, m, i, s) => new MeasuredValueShortWithCP24Time2a(p, m, i, s));
            Reg(TypeID.M_ME_TF_1, 12, DecodeKind.Monitor, (p, m, i, s) => new MeasuredValueShortWithCP56Time2a(p, m, i, s));
            Reg(TypeID.M_IT_NA_1, 5, DecodeKind.Monitor, (p, m, i, s) => new IntegratedTotals(p, m, i, s));
            Reg(TypeID.M_IT_TA_1, 8, DecodeKind.Monitor, (p, m, i, s) => new IntegratedTotalsWithCP24Time2a(p, m, i, s));
            Reg(TypeID.M_IT_TB_1, 12, DecodeKind.Monitor, (p, m, i, s) => new IntegratedTotalsWithCP56Time2a(p, m, i, s));
            Reg(TypeID.M_EP_TA_1, 6, DecodeKind.Monitor, (p, m, i, s) => new EventOfProtectionEquipment(p, m, i, s));
            Reg(TypeID.M_EP_TB_1, 7, DecodeKind.Monitor, (p, m, i, s) => new PackedStartEventsOfProtectionEquipment(p, m, i, s));
            Reg(TypeID.M_EP_TC_1, 7, DecodeKind.Monitor, (p, m, i, s) => new PackedOutputCircuitInfo(p, m, i, s));
            Reg(TypeID.M_EP_TD_1, 10, DecodeKind.Monitor, (p, m, i, s) => new EventOfProtectionEquipmentWithCP56Time2a(p, m, i, s));
            Reg(TypeID.M_EP_TE_1, 11, DecodeKind.Monitor, (p, m, i, s) => new PackedStartEventsOfProtectionEquipmentWithCP56Time2a(p, m, i, s));
            Reg(TypeID.M_EP_TF_1, 11, DecodeKind.Monitor, (p, m, i, s) => new PackedOutputCircuitInfoWithCP56Time2a(p, m, i, s));
            Reg(TypeID.M_PS_NA_1, 5, DecodeKind.Monitor, (p, m, i, s) => new PackedSinglePointWithSCD(p, m, i, s));
            Reg(TypeID.M_EI_NA_1, 1, DecodeKind.Monitor, (p, m, i, s) => new EndOfInitialization(p, m, i));

            // ── Command direction (C_*) — 3-param constructors (params, msg, startIndex) ──
            Reg(TypeID.C_SC_NA_1, 1, DecodeKind.Command, (p, m, i, s) => new SingleCommand(p, m, i));
            Reg(TypeID.C_SC_TA_1, 8, DecodeKind.Command, (p, m, i, s) => new SingleCommandWithCP56Time2a(p, m, i));
            Reg(TypeID.C_DC_NA_1, 1, DecodeKind.Command, (p, m, i, s) => new DoubleCommand(p, m, i));
            Reg(TypeID.C_DC_TA_1, 8, DecodeKind.Command, (p, m, i, s) => new DoubleCommandWithCP56Time2a(p, m, i));
            Reg(TypeID.C_RC_NA_1, 1, DecodeKind.Command, (p, m, i, s) => new StepCommand(p, m, i));
            Reg(TypeID.C_RC_TA_1, 8, DecodeKind.Command, (p, m, i, s) => new StepCommandWithCP56Time2a(p, m, i));
            Reg(TypeID.C_SE_NA_1, 3, DecodeKind.Command, (p, m, i, s) => new SetpointCommandNormalized(p, m, i));
            Reg(TypeID.C_SE_TA_1, 10, DecodeKind.Command, (p, m, i, s) => new SetpointCommandNormalizedWithCP56Time2a(p, m, i));
            Reg(TypeID.C_SE_NB_1, 3, DecodeKind.Command, (p, m, i, s) => new SetpointCommandScaled(p, m, i));
            Reg(TypeID.C_SE_TB_1, 10, DecodeKind.Command, (p, m, i, s) => new SetpointCommandScaledWithCP56Time2a(p, m, i));
            Reg(TypeID.C_SE_NC_1, 5, DecodeKind.Command, (p, m, i, s) => new SetpointCommandShort(p, m, i));
            Reg(TypeID.C_SE_TC_1, 12, DecodeKind.Command, (p, m, i, s) => new SetpointCommandShortWithCP56Time2a(p, m, i));
            Reg(TypeID.C_BO_NA_1, 4, DecodeKind.Command, (p, m, i, s) => new Bitstring32Command(p, m, i));
            Reg(TypeID.C_BO_TA_1, 11, DecodeKind.Command, (p, m, i, s) => new Bitstring32CommandWithCP56Time2a(p, m, i));
            Reg(TypeID.C_IC_NA_1, 1, DecodeKind.Command, (p, m, i, s) => new InterrogationCommand(p, m, i));
            Reg(TypeID.C_CI_NA_1, 1, DecodeKind.Command, (p, m, i, s) => new CounterInterrogationCommand(p, m, i));
            Reg(TypeID.C_RD_NA_1, 0, DecodeKind.Command, (p, m, i, s) => new ReadCommand(p, m, i));
            Reg(TypeID.C_CS_NA_1, 7, DecodeKind.Command, (p, m, i, s) => new ClockSynchronizationCommand(p, m, i));
            Reg(TypeID.C_TS_NA_1, 2, DecodeKind.Command, (p, m, i, s) => new TestCommand(p, m, i));
            Reg(TypeID.C_TS_TA_1, 9, DecodeKind.Command, (p, m, i, s) => new TestCommandWithCP56Time2a(p, m, i));
            Reg(TypeID.C_RP_NA_1, 1, DecodeKind.Command, (p, m, i, s) => new ResetProcessCommand(p, m, i));
            Reg(TypeID.C_CD_NA_1, 2, DecodeKind.Command, (p, m, i, s) => new DelayAcquisitionCommand(p, m, i));

            // ── Parameter (P_*) — 3-param constructors ──
            Reg(TypeID.P_ME_NA_1, 3, DecodeKind.Command, (p, m, i, s) => new ParameterNormalizedValue(p, m, i));
            Reg(TypeID.P_ME_NB_1, 3, DecodeKind.Command, (p, m, i, s) => new ParameterScaledValue(p, m, i));
            Reg(TypeID.P_ME_NC_1, 5, DecodeKind.Command, (p, m, i, s) => new ParameterFloatValue(p, m, i));
            Reg(TypeID.P_AC_NA_1, 1, DecodeKind.Command, (p, m, i, s) => new ParameterActivation(p, m, i));

            // ── File transfer (F_*) — 4-param constructors (params, msg, startIndex, isSequence) ──
            Reg(TypeID.F_FR_NA_1, 6, DecodeKind.File, (p, m, i, s) => new FileReady(p, m, i, s));
            Reg(TypeID.F_SR_NA_1, 7, DecodeKind.File, (p, m, i, s) => new SectionReady(p, m, i, s));
            Reg(TypeID.F_SC_NA_1, 4, DecodeKind.File, (p, m, i, s) => new FileCallOrSelect(p, m, i, s));
            Reg(TypeID.F_LS_NA_1, 5, DecodeKind.File, (p, m, i, s) => new FileLastSegmentOrSection(p, m, i, s));
            Reg(TypeID.F_AF_NA_1, 4, DecodeKind.File, (p, m, i, s) => new FileACK(p, m, i, s));
            Reg(TypeID.F_SG_NA_1, -1, DecodeKind.File, (p, m, i, s) => new FileSegment(p, m, i, s));
            Reg(TypeID.F_DR_TA_1, 13, DecodeKind.File, (p, m, i, s) => new FileDirectory(p, m, i, s));

            return t;
        }

        /// <summary>
        /// Look up the type descriptor for a given TypeID.
        /// </summary>
        internal static bool TryGetDescriptor(TypeID typeId, out TypeDescriptor descriptor)
        {
            int idx = (int)typeId;
            if (idx >= 0 && idx < _descriptors.Length && _descriptors[idx].Decode != null)
            {
                descriptor = _descriptors[idx];
                return true;
            }
            descriptor = default;
            return false;
        }

        /// <summary>
        /// 计算声明元素数所需的 payload 字节数（用于构造期长度校验，防止 GetElement 越界，代码评审 #15）。
        /// 返回值：-1 表示跳过校验（私有类型/文件控制类型/变长类型由各解码器自行处理）；否则为期望的最小 payload 长度。
        /// 尺寸/模式全部查 <see cref="TryGetDescriptor"/> 单一事实源，消除与 GetElement 的重复尺寸表。
        /// </summary>
        internal static int ComputeExpectedPayloadSize(ASDU asdu)
        {
            int n = asdu.NumberOfElements;
            if (n == 0)
                return 0;

            if (!TryGetDescriptor(asdu.typeId, out TypeDescriptor d))
                return -1; // 未知/私有类型：跳过，交给各自解码器

            // 文件控制类型（F_FR_NA_1 等，单元素、偏移 0）与变长类型（F_SG_NA_1）：跳过构造期尺寸校验
            if (d.Kind == DecodeKind.File || d.PayloadSize < 0)
                return -1;

            // Monitor 序列：首元素带 IOA 前缀 + n*payload；非序列 / Command：n*(SizeOfIOA+payload)
            if (d.Kind == DecodeKind.Monitor && asdu.IsSequence)
                return asdu.parameters.SizeOfIOA + n * d.PayloadSize;

            return n * (asdu.parameters.SizeOfIOA + d.PayloadSize);
        }

        /// <summary>
        /// Gets the element (information object) with the specified index.
        /// </summary>
        internal static InformationObject GetElement(int index, ASDU asdu)
        {
            if (index >= asdu.NumberOfElements)
                throw new ASDUParsingException("Index out of range");

            InformationObject retVal = null;

            if (TryGetDescriptor(asdu.typeId, out TypeDescriptor d))
            {
                if (d.Kind == DecodeKind.File)
                {
                    // 文件控制类型：恒定偏移 0，单元素 ASDU
                    retVal = d.Decode(asdu.parameters, asdu.payload, 0, false);
                }
                else if (d.Kind == DecodeKind.Monitor && asdu.IsSequence)
                {
                    // 监视类型序列：首元素带 IOA 前缀，后续元素按 payloadSize 步进
                    int ioa = InformationObject.ParseInformationObjectAddress(asdu.parameters, asdu.payload, 0);

                    int offset = asdu.parameters.SizeOfIOA + (index * d.PayloadSize);

                    retVal = d.Decode(asdu.parameters, asdu.payload, offset, true);

                    retVal.ObjectAddress = ioa + index;
                }
                else
                {
                    // 监视类型非序列 / 命令类型：固定步进 SizeOfIOA + payload
                    int offset = index * (asdu.parameters.SizeOfIOA + d.PayloadSize);

                    retVal = d.Decode(asdu.parameters, asdu.payload, offset, false);
                }
            }
            else if (asdu.privateObjectTypes != null)
            {
                // 未知/私有类型：委托 IPrivateIOFactory
                IPrivateIOFactory ioFactory = asdu.privateObjectTypes.GetFactory(asdu.typeId);

                if (ioFactory != null)
                {
                    int elementSize = asdu.parameters.SizeOfIOA + ioFactory.GetEncodedSize();

                    if (asdu.IsSequence)
                    {
                        int ioa = InformationObject.ParseInformationObjectAddress(asdu.parameters, asdu.payload, 0);

                        retVal = ioFactory.Decode(asdu.parameters, asdu.payload, index * elementSize, true);

                        retVal.ObjectAddress = ioa + index;
                    }
                    else
                        retVal = ioFactory.Decode(asdu.parameters, asdu.payload, index * elementSize, false);
                }
            }

            if (retVal == null)
                throw new ASDUParsingException("Unknown ASDU type id:" + asdu.typeId);

            return retVal;
        }

        /// <summary>
        /// Gets the element (information object) with the specified index, using a private-object-types registry.
        /// </summary>
        internal static InformationObject GetElement(int index, PrivateInformationObjectTypes privateObjectTypes, ASDU asdu)
        {
            asdu.privateObjectTypes = privateObjectTypes;

            return GetElement(index, asdu);
        }

        /// <summary>
        /// Gets the element (information object) with the specified index, using a user-defined IO factory.
        /// </summary>
        internal static InformationObject GetElement(int index, IPrivateIOFactory ioFactory, ASDU asdu)
        {
            if (ioFactory == null)
                return null;

            // index 范围校验
            if (index < 0 || index >= asdu.NumberOfElements)
                throw new ASDUParsingException("Index out of range");

            int elementSize = ioFactory.GetEncodedSize();
            int offset;
            int needed;

            if (asdu.IsSequence)
            {
                if (asdu.payload.Length < asdu.parameters.SizeOfIOA)
                    throw new ASDUParsingException("Payload too small for sequence IOA prefix");
                offset = asdu.parameters.SizeOfIOA + (index * elementSize);
                needed = elementSize;
            }
            else
            {
                offset = index * (asdu.parameters.SizeOfIOA + elementSize);
                needed = asdu.parameters.SizeOfIOA + elementSize;
            }

            // offset+needed 越界校验
            if (offset < 0 || offset + needed > asdu.payload.Length)
                throw new ASDUParsingException("Payload too small for declared element size/VSQ (offset=" + offset + ", needed=" + needed + ", payload=" + asdu.payload.Length + ")");

            InformationObject retVal;

            if (asdu.IsSequence)
            {
                int ioa = InformationObject.ParseInformationObjectAddress(asdu.parameters, asdu.payload, 0);

                retVal = ioFactory.Decode(asdu.parameters, asdu.payload, offset, true);

                retVal.ObjectAddress = ioa + index;
            }
            else
                retVal = ioFactory.Decode(asdu.parameters, asdu.payload, offset, false);

            return retVal;
        }

        /// <summary>
        /// 类型安全版 <see cref="GetElement(int, ASDU)"/>。
        /// </summary>
        internal static T GetElement<T>(int index, ASDU asdu) where T : InformationObject
        {
            InformationObject io = GetElement(index, asdu);

            if (io == null)
                throw new ASDUParsingException("Element " + index + " is null");

            if (io is not T typed)
                throw new ASDUParsingException(
                    "Element " + index + " decoded as " + io.GetType().Name + ", not " + typeof(T).Name);

            return typed;
        }
    }
}