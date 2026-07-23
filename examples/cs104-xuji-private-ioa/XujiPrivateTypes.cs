// 许继(Xuji)私有 ASDU 类型示例 —— TypeID 166 / 168 + 私有 IOA
//
// 说明：
//   IEC 60870-5-104 的标准 TypeID 为 1..127；128..170 为制造厂私有区。
//   许继(许继电气)保护装置在私有区定义了若干私有 ASDU 类型，并用「私有 IOA」
//   （非标准连续序号，而是带语义的 24 位地址）来寻址。
//
//   本示例演示如何：
//     1) 用 24 位私有 IOA 打包「区号/组号/条目序号」(XujiPrivateIoa)；
//     2) 实现私有 TypeID 166 / 168 的 InformationObject + IPrivateIOFactory；
//     3) 通过 PrivateInformationObjectTypes 注册，使其在收发双向可被编解码；
//     4) 在客户端/服务端通过 AsduReceived 回调拿到原始字节后重建 ASDU 并用
//        注册的工厂解析出强类型对象。
//
// ============================================================================
// 166 / 168 载荷布局（许继厂家私有定义）
// ============================================================================
// TypeID 166 —— 带故障量、无选相结果 的保护动作信号
//   qual1       1B  IV NT SB BL EI 0 ES        → 用现成 SingleEvent 表示
//   保护动作时间 2B  uint16 LE, 单位 ms          → 用现成 CP16Time2a 表示
//   时标        7B  ms_lo ms_hi min hour day month year → 用现成 CP56Time2a 表示
//   故障量数目 N 1B  N ≤ 48
//   故障量 1..N N*4B  每个故障量 4 字节, 小端, R32.23 标准 IEEE 754 单精度浮点
//
// TypeID 168 —— 带故障量、含选相结果 的保护动作信号
//   qual1       1B  0  0  0  0  CL3 CL2 CL1 GC  → 用现成 StartEvent 表示(GS/SL1/SL2/SL3)
//   qual2       1B  IV NT SB BL EI 0  0  0      → 用现成 QualityDescriptorP 表示
//   保护动作时间 2B  uint16 LE, 单位 ms          → 用现成 CP16Time2a 表示
//   时标        7B  ms_lo ms_hi min hour day month year → 用现成 CP56Time2a 表示
//   故障量数目 N 1B  N ≤ 48
//   故障量 1..N N*4B  4 字节, 小端, R32.23
//
// 元素信息（时间/事件/质量）全部复用 IEC60870.Core 内置对象，避免自行维护原始字节：
//   · IEC60870.Core.Time.CP56Time2a   (7 字节时标)
//   · IEC60870.Core.Time.CP16Time2a   (2 字节经过时间, ms)
//   · IEC60870.Core.InformationObjects.SingleEvent  (1 字节: QualityDescriptorP + 2bit 事件状态 ES)
//   · IEC60870.Core.InformationObjects.StartEvent   (1 字节: 选相/启动相位 GS/SL1/SL2/SL3)
//   · IEC60870.Core.Quality.QualityDescriptorP       (1 字节: 保护设备质量位 IV/NT/SB/BL/EI…)
//
// 用法（典型）：
//   // 发送：把 float[] 打包成 FaultData
//   var faultBytes = XujiPrivateTypes.EncodeFaultValues(new[] { 12.5f, -3.14f, 0.0f, 100.25f });
//   var io = new XujiType166Object(
//       new XujiPrivateIoa(1,2,3).ToObjectAddress(),
//       new SingleEvent(qual1: 0x02),                 // ES=<2>动作 (也可用 .State 属性)
//       new CP16Time2a(protectionTimeMs: 3590),       // 保护动作时间
//       new CP56Time2a(ts7, 0),                       // 7 字节时标(也可 new CP56Time2a(DateTime))
//       faultBytes);
//   var asdu = new ASDU(params, CauseOfTransmission.SPONTANEOUS, false, false, 0, 1, false);
//   asdu.AddInformationObject(io);
//   await server.BroadcastAsync(asdu);
//
//   // 接收：
//   var io = (XujiType166Object)asdu.GetElement(0, privateTypes);
//   byte[] faultData = io.FaultData;          // 4 字节/故障量, R32.23 小端
//   float[] values   = io.GetFaultValues();   // 直接得到 float[]
//   int count        = io.FaultCount;         // N
//   // 时间/事件/质量直接用现成对象：
//   CP56Time2a ts    = io.Timestamp;
//   CP16Time2a el    = io.ElapsedTime;
//   SingleEvent ev   = io.Event;
//   bool invalid     = io.Event.QDP.Invalid;  // 质量位
//   var when         = io.Timestamp.GetDateTime(2026); // 还原为 DateTime

using System;
using IEC60870.Core;
using IEC60870.Core.InformationObjects;
using IEC60870.Core.Quality;
using IEC60870.Core.Time;

namespace cs104_xuji_private_ioa
{
    /// <summary>
    /// 许继私有 IOA：24 位地址拆分为 区号(1B) | 组号(1B) | 条目序号(1B)。
    /// 框架原生用 3 字节(SizeOfIOA=3)小端写入 objectAddress，因此只要把语义地址
    /// 打包成 int 交给 InformationObject，Encode/Decode 会自动按 3 字节 IOA 处理。
    /// </summary>
    public readonly struct XujiPrivateIoa
    {
        public byte Zone { get; }   // 区号 / 装置区 (bits 16..23)
        public byte Group { get; }  // 组号 / 功能组 (bits 8..15)
        public byte Index { get; }  // 条目序号 (bits 0..7)

        public XujiPrivateIoa(byte zone, byte group, byte index)
        {
            Zone = zone;
            Group = group;
            Index = index;
        }

        /// <summary>打包为 24 位整数，可直接作为 InformationObject 的 objectAddress。</summary>
        public int ToObjectAddress() => (Zone << 16) | (Group << 8) | Index;

        /// <summary>从 24 位整数解析出语义地址。</summary>
        public static XujiPrivateIoa Parse(int address)
            => new XujiPrivateIoa(
                (byte)((address >> 16) & 0xff),
                (byte)((address >> 8) & 0xff),
                (byte)(address & 0xff));

        public override string ToString() => $"Zone={Zone} Group={Group} Index={Index} (0x{ToObjectAddress():X6})";
    }

    // ------------------------------------------------------------------------
    // TypeID 166 —— 带故障量、无选相结果
    // ------------------------------------------------------------------------
    /// <summary>
    /// 许继私有 TypeID 166（带故障量、无选相结果 的保护动作信号）。
    /// 载荷：SingleEvent(1) + CP16Time2a(2) + CP56Time2a(7) + 故障量数目 N(1) + 故障量(N*4)，
    /// 其中每个故障量是 4 字节 R32.23(IEEE 754 单精度浮点) 小端。
    /// 时间/事件/质量全部复用 IEC60870.Core 内置对象，不再自行维护原始字节。
    /// </summary>
    public class XujiType166Object : InformationObject, IPrivateIOFactory
    {
        public const int MaxFaultCount = 48;
        private const int HeaderSize = 1 + 2 + 7 + 1;  // SingleEvent + CP16Time2a + CP56Time2a + N

        /// <summary>qual1: IV NT SB BL EI 0 ES —— 用现成 SingleEvent 表示
        /// (QDP 承载 IV/NT/SB/BL/EI, State 承载 2 位事件状态 ES)。</summary>
        public SingleEvent Event { get; }

        /// <summary>保护动作时间, 单位 ms, 范围 0..65535。用现成 CP16Time2a 表示。</summary>
        public CP16Time2a ElapsedTime { get; }

        /// <summary>7 字节时标。用现成 CP56Time2a 表示, 可直接 GetDateTime() 还原为 DateTime。</summary>
        public CP56Time2a Timestamp { get; }

        /// <summary>故障量数目 N (≤ 48)。</summary>
        public byte FaultCount { get; }

        /// <summary>
        /// 故障量原始字节数组，长度 = FaultCount * 4。
        /// 每个故障量是 4 字节 R32.23 (IEEE 754 单精度浮点) 小端。
        /// 要得到 float[], 调用 <see cref="GetFaultValues"/>；要 hex 查看, 用
        /// <c>BitConverter.ToString(FaultData)</c> 或 Convert.ToHexString(FaultData)。
        /// </summary>
        public byte[] FaultData { get; }

        public XujiPrivateIoa PrivateIoa => XujiPrivateIoa.Parse(ObjectAddress);

        // ---- 构造 ----

        public XujiType166Object() : base(0)
        {
            Event = new SingleEvent();
            ElapsedTime = new CP16Time2a();
            Timestamp = new CP56Time2a();
            FaultData = Array.Empty<byte>();
        }

        /// <summary>发送用构造器。</summary>
        /// <param name="ioa">私有 IOA, 可用 <see cref="XujiPrivateIoa.ToObjectAddress"/> 打包。</param>
        /// <param name="singleEvent">qual1, 用 <c>new SingleEvent(0x02)</c> 或设置其 QDP/State。</param>
        /// <param name="elapsedTime">保护动作时间, 用 <c>new CP16Time2a(ms)</c>。</param>
        /// <param name="timestamp">7 字节时标, 用 <c>new CP56Time2a(bytes, 0)</c> 或 <c>new CP56Time2a(DateTime)</c>。</param>
        /// <param name="faultData">故障量原始字节, 长度 = 4 * N, N ≤ 48。</param>
        public XujiType166Object(int ioa, SingleEvent singleEvent, CP16Time2a elapsedTime, CP56Time2a timestamp, byte[] faultData)
            : base(ioa)
        {
            if (faultData == null || faultData.Length % 4 != 0)
                throw new ArgumentException("faultData length must be a multiple of 4", nameof(faultData));
            if (faultData.Length / 4 > MaxFaultCount)
                throw new ArgumentException($"max {MaxFaultCount} fault values (got {faultData.Length / 4})", nameof(faultData));

            Event = singleEvent ?? new SingleEvent();
            ElapsedTime = elapsedTime ?? new CP16Time2a();
            Timestamp = timestamp ?? new CP56Time2a();
            FaultCount = (byte)(faultData.Length / 4);
            FaultData = (byte[])faultData.Clone();
        }

        // ---- 接收解析 ----

        private XujiType166Object(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence) startIndex += parameters.SizeOfIOA; /* 跳过 IOA */

            Event = new SingleEvent(msg[startIndex++]);
            ElapsedTime = new CP16Time2a(msg, startIndex);
            startIndex += 2;
            Timestamp = new CP56Time2a(msg, startIndex);
            startIndex += 7;
            FaultCount = msg[startIndex++];
            if (FaultCount > MaxFaultCount) FaultCount = MaxFaultCount;   // 防御性截断
            FaultData = new byte[FaultCount * 4];
            Array.Copy(msg, startIndex, FaultData, 0, FaultData.Length);
        }

        // ---- 便捷方法 ----

        /// <summary>把 <see cref="FaultData"/> 按每 4 字节一个 IEEE 754 单精度浮点解码为 float[]。</summary>
        public float[] GetFaultValues()
        {
            var arr = new float[FaultCount];
            for (int i = 0; i < FaultCount; i++)
                arr[i] = BitConverter.ToSingle(FaultData, i * 4);
            return arr;
        }

        // ---- 框架方法 ----

        public override bool SupportsSequence => false;
        public override TypeID Type => (TypeID)166;

        InformationObject IPrivateIOFactory.Decode(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            => new XujiType166Object(parameters, msg, startIndex, isSequence);

        public override int GetEncodedSize() => HeaderSize + FaultData.Length;

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);
            frame.SetNextByte(Event.EncodedValue);
            frame.AppendBytes(ElapsedTime.GetEncodedValue());
            frame.AppendBytes(Timestamp.GetEncodedValue());
            frame.SetNextByte(FaultCount);
            for (int i = 0; i < FaultData.Length; i++) frame.SetNextByte(FaultData[i]);
        }
    }

    // ------------------------------------------------------------------------
    // TypeID 168 —— 带故障量、含选相结果
    // ------------------------------------------------------------------------
    /// <summary>
    /// 许继私有 TypeID 168（带故障量、含选相结果 的保护动作信号）。
    /// 载荷：StartEvent(1) + QualityDescriptorP(1) + CP16Time2a(2) + CP56Time2a(7) +
    /// 故障量数目 N(1) + 故障量(N*4, R32.23 小端)。
    /// 选相结果用现成 StartEvent(GS/SL1/SL2/SL3) 表示；质量位用现成 QualityDescriptorP 表示。
    /// </summary>
    public class XujiType168Object : InformationObject, IPrivateIOFactory
    {
        public const int MaxFaultCount = 48;
        private const int HeaderSize = 1 + 1 + 2 + 7 + 1;  // SPE + QDP + CP16Time2a + CP56Time2a + N

        /// <summary>qual1 选相结果: GC CL1 CL2 CL3 —— 用现成 StartEvent 表示
        /// (GS=三相, SL1=A相, SL2=B相, SL3=C相)。</summary>
        public StartEvent SPE { get; }

        /// <summary>qual2 质量位: IV NT SB BL EI 0 0 0 —— 用现成 QualityDescriptorP 表示。</summary>
        public QualityDescriptorP QDP { get; }

        public CP16Time2a ElapsedTime { get; }
        public CP56Time2a Timestamp { get; }
        public byte FaultCount { get; }
        public byte[] FaultData { get; }

        // qual1 选相位方便属性 (映射 StartEvent 相应位)
        public bool Gc  => SPE.GS;    // 三相动作
        public bool Cl1 => SPE.SL1;   // A 相动作
        public bool Cl2 => SPE.SL2;   // B 相动作
        public bool Cl3 => SPE.SL3;   // C 相动作

        // qual2 质量位方便属性 (映射 QualityDescriptorP 相应位)
        public bool Iv => QDP.Invalid;
        public bool Nt => QDP.NonTopical;
        public bool Sb => QDP.Substituted;
        public bool Bl => QDP.Blocked;
        public bool Ei => QDP.ElapsedTimeInvalid;

        public XujiPrivateIoa PrivateIoa => XujiPrivateIoa.Parse(ObjectAddress);

        public XujiType168Object() : base(0)
        {
            SPE = new StartEvent();
            QDP = new QualityDescriptorP();
            ElapsedTime = new CP16Time2a();
            Timestamp = new CP56Time2a();
            FaultData = Array.Empty<byte>();
        }

        /// <summary>发送用构造器。</summary>
        /// <param name="ioa">私有 IOA, 可用 <see cref="XujiPrivateIoa.ToObjectAddress"/> 打包。</param>
        /// <param name="spe">qual1 选相结果, 用 <c>new StartEvent(0x0B)</c> 或设置其 GS/SL1..SL3。</param>
        /// <param name="qdp">qual2 质量位, 用 <c>new QualityDescriptorP(0x88)</c>。</param>
        /// <param name="elapsedTime">保护动作时间, 用 <c>new CP16Time2a(ms)</c>。</param>
        /// <param name="timestamp">7 字节时标, 用 <c>new CP56Time2a(bytes, 0)</c>。</param>
        /// <param name="faultData">故障量原始字节, 长度 = 4 * N, N ≤ 48。</param>
        public XujiType168Object(int ioa, StartEvent spe, QualityDescriptorP qdp, CP16Time2a elapsedTime, CP56Time2a timestamp, byte[] faultData)
            : base(ioa)
        {
            if (faultData == null || faultData.Length % 4 != 0)
                throw new ArgumentException("faultData length must be a multiple of 4", nameof(faultData));
            if (faultData.Length / 4 > MaxFaultCount)
                throw new ArgumentException($"max {MaxFaultCount} fault values (got {faultData.Length / 4})", nameof(faultData));

            SPE = spe ?? new StartEvent();
            QDP = qdp ?? new QualityDescriptorP();
            ElapsedTime = elapsedTime ?? new CP16Time2a();
            Timestamp = timestamp ?? new CP56Time2a();
            FaultCount = (byte)(faultData.Length / 4);
            FaultData = (byte[])faultData.Clone();
        }

        private XujiType168Object(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence) startIndex += parameters.SizeOfIOA;

            SPE = new StartEvent(msg[startIndex++]);
            QDP = new QualityDescriptorP(msg[startIndex++]);
            ElapsedTime = new CP16Time2a(msg, startIndex);
            startIndex += 2;
            Timestamp = new CP56Time2a(msg, startIndex);
            startIndex += 7;
            FaultCount = msg[startIndex++];
            if (FaultCount > MaxFaultCount) FaultCount = MaxFaultCount;
            FaultData = new byte[FaultCount * 4];
            Array.Copy(msg, startIndex, FaultData, 0, FaultData.Length);
        }

        public float[] GetFaultValues()
        {
            var arr = new float[FaultCount];
            for (int i = 0; i < FaultCount; i++)
                arr[i] = BitConverter.ToSingle(FaultData, i * 4);
            return arr;
        }

        public override bool SupportsSequence => false;
        public override TypeID Type => (TypeID)168;

        InformationObject IPrivateIOFactory.Decode(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            => new XujiType168Object(parameters, msg, startIndex, isSequence);

        public override int GetEncodedSize() => HeaderSize + FaultData.Length;

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);
            frame.SetNextByte(SPE.EncodedValue);
            frame.SetNextByte(QDP.EncodedValue);
            frame.AppendBytes(ElapsedTime.GetEncodedValue());
            frame.AppendBytes(Timestamp.GetEncodedValue());
            frame.SetNextByte(FaultCount);
            for (int i = 0; i < FaultData.Length; i++) frame.SetNextByte(FaultData[i]);
        }
    }

    // ------------------------------------------------------------------------
    // 工厂 + 工具方法
    // ------------------------------------------------------------------------
    /// <summary>
    /// 构造并注册许继私有 166 / 168 类型。收发两侧都要各自持有一份
    /// PrivateInformationObjectTypes 实例（或共享同一份）才能正确解析。
    /// </summary>
    public static class XujiPrivateTypes
    {
        public static PrivateInformationObjectTypes Build()
        {
            var types = new PrivateInformationObjectTypes();
            types.AddPrivateInformationObjectType((TypeID)166, new XujiType166Object());
            types.AddPrivateInformationObjectType((TypeID)168, new XujiType168Object());
            return types;
        }

        /// <summary>
        /// 把 float[] 编码为 R32.23 字节数组（4 字节/值, 小端）。
        /// 用法：<c>var bytes = XujiPrivateTypes.EncodeFaultValues(new[] { 1.0f, 2.5f });</c>
        /// </summary>
        public static byte[] EncodeFaultValues(float[] values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            var bytes = new byte[values.Length * 4];
            for (int i = 0; i < values.Length; i++)
            {
                var b = BitConverter.GetBytes(values[i]);
                Array.Copy(b, 0, bytes, i * 4, 4);
            }
            return bytes;
        }
    }
}
