//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;

namespace IEC60870.Core;

    // ──────────────────────────────────────────────────────────────────────
    //  监视方向(M_*)类型处理器。每个处理器薄封装对应 InformationObject
    //  的内部解码构造,元数据(TypeId/Kind/PayloadSize)即类型适配单一事实源。
    // ──────────────────────────────────────────────────────────────────────

    internal sealed class SinglePointInformationHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_SP_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 1;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new SinglePointInformation(parameters, msg, startIndex, isSequence);
    }

    internal sealed class SinglePointWithCP24Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_SP_TA_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 4;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new SinglePointWithCP24Time2a(parameters, msg, startIndex, isSequence);
    }

    internal sealed class SinglePointWithCP56Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_SP_TB_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 8;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new SinglePointWithCP56Time2a(parameters, msg, startIndex, isSequence);
    }

    internal sealed class DoublePointInformationHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_DP_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 1;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new DoublePointInformation(parameters, msg, startIndex, isSequence);
    }

    internal sealed class DoublePointWithCP24Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_DP_TA_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 4;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new DoublePointWithCP24Time2a(parameters, msg, startIndex, isSequence);
    }

    internal sealed class DoublePointWithCP56Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_DP_TB_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 8;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new DoublePointWithCP56Time2a(parameters, msg, startIndex, isSequence);
    }

    internal sealed class StepPositionInformationHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_ST_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 2;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new StepPositionInformation(parameters, msg, startIndex, isSequence);
    }

    internal sealed class StepPositionWithCP24Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_ST_TA_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 5;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new StepPositionWithCP24Time2a(parameters, msg, startIndex, isSequence);
    }

    internal sealed class StepPositionWithCP56Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_ST_TB_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 9;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new StepPositionWithCP56Time2a(parameters, msg, startIndex, isSequence);
    }

    internal sealed class Bitstring32Handler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_BO_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 5;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new Bitstring32(parameters, msg, startIndex, isSequence);
    }

    internal sealed class Bitstring32WithCP24Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_BO_TA_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 8;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new Bitstring32WithCP24Time2a(parameters, msg, startIndex, isSequence);
    }

    internal sealed class Bitstring32WithCP56Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_BO_TB_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 12;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new Bitstring32WithCP56Time2a(parameters, msg, startIndex, isSequence);
    }

    internal sealed class MeasuredValueNormalizedWithoutQualityHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_ME_ND_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 2;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new MeasuredValueNormalizedWithoutQuality(parameters, msg, startIndex, isSequence);
    }

    internal sealed class MeasuredValueNormalizedHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_ME_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 3;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new MeasuredValueNormalized(parameters, msg, startIndex, isSequence);
    }

    internal sealed class MeasuredValueNormalizedWithCP24Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_ME_TA_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 6;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new MeasuredValueNormalizedWithCP24Time2a(parameters, msg, startIndex, isSequence);
    }

    internal sealed class MeasuredValueNormalizedWithCP56Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_ME_TD_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 10;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new MeasuredValueNormalizedWithCP56Time2a(parameters, msg, startIndex, isSequence);
    }

    internal sealed class MeasuredValueScaledHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_ME_NB_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 3;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new MeasuredValueScaled(parameters, msg, startIndex, isSequence);
    }

    internal sealed class MeasuredValueScaledWithCP24Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_ME_TB_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 6;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new MeasuredValueScaledWithCP24Time2a(parameters, msg, startIndex, isSequence);
    }

    internal sealed class MeasuredValueScaledWithCP56Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_ME_TE_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 10;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new MeasuredValueScaledWithCP56Time2a(parameters, msg, startIndex, isSequence);
    }

    internal sealed class MeasuredValueShortHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_ME_NC_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 5;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new MeasuredValueShort(parameters, msg, startIndex, isSequence);
    }

    internal sealed class MeasuredValueShortWithCP24Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_ME_TC_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 8;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new MeasuredValueShortWithCP24Time2a(parameters, msg, startIndex, isSequence);
    }

    internal sealed class MeasuredValueShortWithCP56Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_ME_TF_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 12;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new MeasuredValueShortWithCP56Time2a(parameters, msg, startIndex, isSequence);
    }

    internal sealed class IntegratedTotalsHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_IT_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 5;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new IntegratedTotals(parameters, msg, startIndex, isSequence);
    }

    internal sealed class IntegratedTotalsWithCP24Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_IT_TA_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 8;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new IntegratedTotalsWithCP24Time2a(parameters, msg, startIndex, isSequence);
    }

    internal sealed class IntegratedTotalsWithCP56Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_IT_TB_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 12;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new IntegratedTotalsWithCP56Time2a(parameters, msg, startIndex, isSequence);
    }

    internal sealed class EventOfProtectionEquipmentHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_EP_TA_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 6;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new EventOfProtectionEquipment(parameters, msg, startIndex, isSequence);
    }

    internal sealed class PackedStartEventsOfProtectionEquipmentHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_EP_TB_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 7;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new PackedStartEventsOfProtectionEquipment(parameters, msg, startIndex, isSequence);
    }

    internal sealed class PackedOutputCircuitInfoHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_EP_TC_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 7;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new PackedOutputCircuitInfo(parameters, msg, startIndex, isSequence);
    }

    internal sealed class EventOfProtectionEquipmentWithCP56Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_EP_TD_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 10;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new EventOfProtectionEquipmentWithCP56Time2a(parameters, msg, startIndex, isSequence);
    }

    internal sealed class PackedStartEventsOfProtectionEquipmentWithCP56Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_EP_TE_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 11;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new PackedStartEventsOfProtectionEquipmentWithCP56Time2a(parameters, msg, startIndex, isSequence);
    }

    internal sealed class PackedOutputCircuitInfoWithCP56Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_EP_TF_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 11;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new PackedOutputCircuitInfoWithCP56Time2a(parameters, msg, startIndex, isSequence);
    }

    internal sealed class PackedSinglePointWithSCDHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_PS_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 5;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new PackedSinglePointWithSCD(parameters, msg, startIndex, isSequence);
    }

    internal sealed class EndOfInitializationHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.M_EI_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.Monitor;
        public int PayloadSize => 1;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new EndOfInitialization(parameters, msg, startIndex);
    }
