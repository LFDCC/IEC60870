//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

namespace IEC60870.Core;

    /// <summary>
    /// 标准 IEC 60870-5 类型处理器集合。由 <see cref="AsduTypeHandlerRegistry"/> 构建默认单例时
    /// 一次性注册全部标准类型(M_*/C_*/P_*/F_*)。
    /// </summary>
    internal static class StandardAsduTypeHandlers
    {
        /// <summary>向注册表注册全部标准类型处理器。</summary>
        internal static void RegisterAll(AsduTypeHandlerRegistry registry)
        {
            // ── 监视方向(M_*) ──
            registry.Register(new SinglePointInformationHandler());
            registry.Register(new SinglePointWithCP24Time2aHandler());
            registry.Register(new SinglePointWithCP56Time2aHandler());
            registry.Register(new DoublePointInformationHandler());
            registry.Register(new DoublePointWithCP24Time2aHandler());
            registry.Register(new DoublePointWithCP56Time2aHandler());
            registry.Register(new StepPositionInformationHandler());
            registry.Register(new StepPositionWithCP24Time2aHandler());
            registry.Register(new StepPositionWithCP56Time2aHandler());
            registry.Register(new Bitstring32Handler());
            registry.Register(new Bitstring32WithCP24Time2aHandler());
            registry.Register(new Bitstring32WithCP56Time2aHandler());
            registry.Register(new MeasuredValueNormalizedWithoutQualityHandler());
            registry.Register(new MeasuredValueNormalizedHandler());
            registry.Register(new MeasuredValueNormalizedWithCP24Time2aHandler());
            registry.Register(new MeasuredValueNormalizedWithCP56Time2aHandler());
            registry.Register(new MeasuredValueScaledHandler());
            registry.Register(new MeasuredValueScaledWithCP24Time2aHandler());
            registry.Register(new MeasuredValueScaledWithCP56Time2aHandler());
            registry.Register(new MeasuredValueShortHandler());
            registry.Register(new MeasuredValueShortWithCP24Time2aHandler());
            registry.Register(new MeasuredValueShortWithCP56Time2aHandler());
            registry.Register(new IntegratedTotalsHandler());
            registry.Register(new IntegratedTotalsWithCP24Time2aHandler());
            registry.Register(new IntegratedTotalsWithCP56Time2aHandler());
            registry.Register(new EventOfProtectionEquipmentHandler());
            registry.Register(new PackedStartEventsOfProtectionEquipmentHandler());
            registry.Register(new PackedOutputCircuitInfoHandler());
            registry.Register(new EventOfProtectionEquipmentWithCP56Time2aHandler());
            registry.Register(new PackedStartEventsOfProtectionEquipmentWithCP56Time2aHandler());
            registry.Register(new PackedOutputCircuitInfoWithCP56Time2aHandler());
            registry.Register(new PackedSinglePointWithSCDHandler());
            registry.Register(new EndOfInitializationHandler());

            // ── 控制方向(C_*) ──
            registry.Register(new SingleCommandHandler());
            registry.Register(new SingleCommandWithCP56Time2aHandler());
            registry.Register(new DoubleCommandHandler());
            registry.Register(new DoubleCommandWithCP56Time2aHandler());
            registry.Register(new StepCommandHandler());
            registry.Register(new StepCommandWithCP56Time2aHandler());
            registry.Register(new SetpointCommandNormalizedHandler());
            registry.Register(new SetpointCommandNormalizedWithCP56Time2aHandler());
            registry.Register(new SetpointCommandScaledHandler());
            registry.Register(new SetpointCommandScaledWithCP56Time2aHandler());
            registry.Register(new SetpointCommandShortHandler());
            registry.Register(new SetpointCommandShortWithCP56Time2aHandler());
            registry.Register(new Bitstring32CommandHandler());
            registry.Register(new Bitstring32CommandWithCP56Time2aHandler());
            registry.Register(new InterrogationCommandHandler());
            registry.Register(new CounterInterrogationCommandHandler());
            registry.Register(new ReadCommandHandler());
            registry.Register(new ClockSynchronizationCommandHandler());
            registry.Register(new TestCommandHandler());
            registry.Register(new TestCommandWithCP56Time2aHandler());
            registry.Register(new ResetProcessCommandHandler());
            registry.Register(new DelayAcquisitionCommandHandler());

            // ── 参数(P_*) ──
            registry.Register(new ParameterNormalizedValueHandler());
            registry.Register(new ParameterScaledValueHandler());
            registry.Register(new ParameterFloatValueHandler());
            registry.Register(new ParameterActivationHandler());

            // ── 文件传输(F_*) ──
            registry.Register(new FileReadyHandler());
            registry.Register(new SectionReadyHandler());
            registry.Register(new FileCallOrSelectHandler());
            registry.Register(new FileLastSegmentOrSectionHandler());
            registry.Register(new FileACKHandler());
            registry.Register(new FileSegmentHandler());
            registry.Register(new FileDirectoryHandler());
        }
    }
