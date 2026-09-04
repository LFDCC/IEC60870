//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;

namespace IEC60870.Core;

    // ──────────────────────────────────────────────────────────────────────
    //  控制方向(C_*)与参数(P_*)类型处理器。命令类内部解码构造为 3 参数
    //  (parameters, msg, startIndex),不区分序列寻址。
    // ──────────────────────────────────────────────────────────────────────

    internal sealed class SingleCommandHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.C_SC_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 1;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new SingleCommand(parameters, msg, startIndex);
    }

    internal sealed class SingleCommandWithCP56Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.C_SC_TA_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 8;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new SingleCommandWithCP56Time2a(parameters, msg, startIndex);
    }

    internal sealed class DoubleCommandHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.C_DC_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 1;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new DoubleCommand(parameters, msg, startIndex);
    }

    internal sealed class DoubleCommandWithCP56Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.C_DC_TA_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 8;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new DoubleCommandWithCP56Time2a(parameters, msg, startIndex);
    }

    internal sealed class StepCommandHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.C_RC_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 1;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new StepCommand(parameters, msg, startIndex);
    }

    internal sealed class StepCommandWithCP56Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.C_RC_TA_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 8;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new StepCommandWithCP56Time2a(parameters, msg, startIndex);
    }

    internal sealed class SetpointCommandNormalizedHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.C_SE_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 3;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new SetpointCommandNormalized(parameters, msg, startIndex);
    }

    internal sealed class SetpointCommandNormalizedWithCP56Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.C_SE_TA_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 10;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new SetpointCommandNormalizedWithCP56Time2a(parameters, msg, startIndex);
    }

    internal sealed class SetpointCommandScaledHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.C_SE_NB_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 3;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new SetpointCommandScaled(parameters, msg, startIndex);
    }

    internal sealed class SetpointCommandScaledWithCP56Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.C_SE_TB_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 10;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new SetpointCommandScaledWithCP56Time2a(parameters, msg, startIndex);
    }

    internal sealed class SetpointCommandShortHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.C_SE_NC_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 5;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new SetpointCommandShort(parameters, msg, startIndex);
    }

    internal sealed class SetpointCommandShortWithCP56Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.C_SE_TC_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 12;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new SetpointCommandShortWithCP56Time2a(parameters, msg, startIndex);
    }

    internal sealed class Bitstring32CommandHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.C_BO_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 4;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new Bitstring32Command(parameters, msg, startIndex);
    }

    internal sealed class Bitstring32CommandWithCP56Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.C_BO_TA_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 11;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new Bitstring32CommandWithCP56Time2a(parameters, msg, startIndex);
    }

    internal sealed class InterrogationCommandHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.C_IC_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 1;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new InterrogationCommand(parameters, msg, startIndex);
    }

    internal sealed class CounterInterrogationCommandHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.C_CI_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 1;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new CounterInterrogationCommand(parameters, msg, startIndex);
    }

    internal sealed class ReadCommandHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.C_RD_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 0;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new ReadCommand(parameters, msg, startIndex);
    }

    internal sealed class ClockSynchronizationCommandHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.C_CS_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 7;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new ClockSynchronizationCommand(parameters, msg, startIndex);
    }

    internal sealed class TestCommandHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.C_TS_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 2;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new TestCommand(parameters, msg, startIndex);
    }

    internal sealed class TestCommandWithCP56Time2aHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.C_TS_TA_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 9;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new TestCommandWithCP56Time2a(parameters, msg, startIndex);
    }

    internal sealed class ResetProcessCommandHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.C_RP_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 1;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new ResetProcessCommand(parameters, msg, startIndex);
    }

    internal sealed class DelayAcquisitionCommandHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.C_CD_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 2;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new DelayAcquisitionCommand(parameters, msg, startIndex);
    }

    // ── 参数(P_*)类型 ──────────────────────────────────────────────

    internal sealed class ParameterNormalizedValueHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.P_ME_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 3;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new ParameterNormalizedValue(parameters, msg, startIndex);
    }

    internal sealed class ParameterScaledValueHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.P_ME_NB_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 3;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new ParameterScaledValue(parameters, msg, startIndex);
    }

    internal sealed class ParameterFloatValueHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.P_ME_NC_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 5;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new ParameterFloatValue(parameters, msg, startIndex);
    }

    internal sealed class ParameterActivationHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.P_AC_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.Command;
        public int PayloadSize => 1;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new ParameterActivation(parameters, msg, startIndex);
    }
