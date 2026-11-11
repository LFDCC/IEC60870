// 许继(Xuji)私有 TypeID 166 / 168 + 单元公共地址(CA) 收发示例
//
// 真实场景方向（保护装置故障上送）：
//   从站(Server, 下位机/RTU/保护装置)  ── 主动上送 ──►  主站(Client, 上位机/SCADA)
//   · 从站在链路激活(STARTDT_CON)后, 检测到故障便自发(SPONTANEOUS)上送私有 166/168,
//     内含故障量 byte[] FaultData
//   · 主站在 AsduReceived 回调里用注册的私有工厂解码, 取出 byte[] FaultData / float[] 做处理
//
// ============================================================================
// 许继 104 的单元公共地址(CA) —— 本示例接入真实装置的关键
// ============================================================================
//   许继104规约 §3.14 表18 原文：
//       「COMADDR低字节 = 公共地址；COMADDR高字节 = 装置地址（下同）。」
//   许继现场把低字节那一段叫「扇区」。接入许继装置时，对方工程师提供的参数就是
//   「扇区 + 装置地址」两个数，缺一个都拼不出装置认可的 CA —— 只填其中一个，
//   装置对总召唤/时钟同步/遥控会回 COT=46「未知公共地址」或直接忽略。
//
//        ca = (装置地址 << 8) | 扇区           需 SizeOfCA = 2（库默认即 2）
//
//   自发上送的遥信/遥测，装置地址通常为 0；对某一具体装置下遥控时才填该装置的地址。
//
// 本示例用法：
//   dotnet run                                   # 默认 扇区=1 装置地址=0
//   dotnet run -- --sector 2 --device 5          # CA = 0x0502（现场参数照填）
//   启动时会先跑一遍自检（XujiCa 打包/拆包 + ASDU 线格式字节 + 解码往返），失败即退出码 1。
//
// 关键 API（时间/事件/质量全部复用 IEC60870.Core 内置对象）：
//   int ca = new XujiCa(sector, device).ToCommonAddress();
//   var asdu = new ASDU(server.Parameters, CauseOfTransmission.SPONTANEOUS, false, false, 0, ca, false);
//   await server.BroadcastAsync(asdu);                 // 上送给所有已激活主站
//   var parsed = XujiCa.Parse(asdu.Ca);                // 收到后还原 扇区/装置地址

using System;
using System.Linq;
using System.Threading.Tasks;
using IEC60870.Core;
using IEC60870.CS104;

namespace cs104_private_ioa.Xuji
{
    public static class Program
    {
        private const int Port = 2404;

        // 私有类型表：主站(接收方)用它解码；从站只负责上送(构造并广播)，无需在此持有
        private static readonly PrivateInformationObjectTypes ClientPriv = XujiPrivateTypes.Build();

        private static ApplicationLayerParameters? _clientParams;

        // 本站 CA（许继：扇区 + 装置地址），由命令行参数决定
        private static XujiCa _localCa = new(sector: 1, device: 0);
        private static int _expectedCa;

        private static readonly TaskCompletionSource<bool> _allReceived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static int _received;
        private static int _caMismatch;

        private static string Hex(byte[] data) => data.ToHex();

        // ────────────────────────────────────────────────────────────────
        //  自检：XujiCa 打包/拆包 + ASDU 线格式字节 + 解码往返
        // ────────────────────────────────────────────────────────────────
        private static void SelfCheck()
        {
            // 1) 打包 / 拆包往返（含边界值）
            (byte sector, byte device)[] cases =
            {
                (0, 0), (1, 0), (1, 1), (2, 5), (255, 255), (255, 0), (0, 255),
            };
            foreach (var (sector, device) in cases)
            {
                var ca = new XujiCa(sector, device);
                int packed = ca.ToCommonAddress();
                AssertEq(packed, (device << 8) | sector, $"XujiCa 打包 sector={sector} device={device}");

                var back = XujiCa.Parse(packed);
                AssertEq(back.Sector, sector, $"XujiCa 拆包 sector (ca=0x{packed:X4})");
                AssertEq(back.Device, device, $"XujiCa 拆包 device (ca=0x{packed:X4})");
            }

            // 2) 线格式：CA 为 2 字节小端 —— ASDU 头里应先出现「扇区」，再出现「装置地址」
            var al = new ApplicationLayerParameters();       // 默认 CA=2, COT=2, IOA=3
            var wireCa = new XujiCa(sector: 2, device: 5);   // CA = 0x0502
            var io = new XujiType166Object(
                ioa: 0x010203,
                new SingleEvent(0x02),
                new CP16Time2a(100),
                new CP56Time2a(new byte[7], 0),
                new byte[4]);
            var asdu = new ASDU(al, CauseOfTransmission.SPONTANEOUS, false, false, 0, wireCa.ToCommonAddress(), false);
            asdu.AddInformationObject(io);

            byte[] body = asdu.AsByteArray()
                ?? throw new InvalidOperationException("自检失败：ASDU.AsByteArray() 返回 null");

            // 布局: TI(1) | VSQ(1) | COT(SizeOfCOT, 含 Oa) | CA(SizeOfCA) | IOA(3) | 载荷
            int caOffset = 1 + 1 + al.SizeOfCOT;
            AssertEq(body[caOffset], 2, "ASDU 头 CA 首字节应为 扇区(=2)");
            AssertEq(body[caOffset + 1], 5, "ASDU 头 CA 次字节应为 装置地址(=5)");

            // 3) 解码往返：从字节重建 ASDU，CA 必须还原成同一个 扇区/装置地址
            var decoded = new ASDU(al, body, 0, body.Length);
            AssertEq(decoded.Ca, wireCa.ToCommonAddress(), "解码后 CA 还原");
            var parsed = XujiCa.Parse(decoded.Ca);
            AssertEq(parsed.Sector, 2, "解码后 扇区");
            AssertEq(parsed.Device, 5, "解码后 装置地址");

            Console.WriteLine("[自检] XujiCa 打包/拆包 + ASDU 线格式 + 解码往返：全部通过");
        }

        private static void AssertEq(int actual, int expected, string what)
        {
            if (actual != expected)
            {
                throw new InvalidOperationException($"自检失败：{what}，期望 {expected}，实际 {actual}");
            }
        }

        // ────────────────────────────────────────────────────────────────
        //  主站(Client / 上位机)：接收并处理从站上送的故障量
        // ────────────────────────────────────────────────────────────────
        private static void OnClientAsdu(in AsduView view)
        {
            var raw = view.Raw.ToArray();
            ASDU asdu = new ASDU(_clientParams!, raw, 0, raw.Length);

            if (asdu.TypeId != (TypeID)166 && asdu.TypeId != (TypeID)168)
            {
                return; // 忽略非许继私有类型（如 STARTDT/测试帧对应的系统 ASDU）
            }

            // 接入真实装置的关键校验：收到的 CA 必须等于「扇区 + 装置地址」拼出来的值
            var ca = XujiCa.Parse(asdu.Ca);
            bool caOk = asdu.Ca == _expectedCa;
            if (!caOk)
            {
                _caMismatch++;
            }

            Console.WriteLine($"\n[主站] 收到上送 TypeID={(int)asdu.TypeId} COT={asdu.Cot} CA={asdu.Ca} → {ca}" +
                              (caOk ? "  [CA 匹配]" : "  [CA 不匹配!]"));
            Console.WriteLine(raw.ToTelegram("主站接收"));

            if (asdu.TypeId == (TypeID)166)
            {
                var io = (XujiType166Object)asdu.GetElement(0, ClientPriv);
                Console.WriteLine($"       信息体地址 IOA = 0x{io.ObjectAddress:X6}");
                Console.WriteLine($"       Event(QDP.EncodedValue=0x{io.Event.EncodedValue:X2})  Invalid={io.Event.QDP.Invalid} NonTopical={io.Event.QDP.NonTopical} Blocked={io.Event.QDP.Blocked} Substituted={io.Event.QDP.Substituted} ElapsedTimeInvalid={io.Event.QDP.ElapsedTimeInvalid}  ES状态={io.Event.State}");
                Console.WriteLine($"       保护动作时间 ElapsedTime = {io.ElapsedTime.ElapsedTimeInMs} ms");
                Console.WriteLine($"       时标 Timestamp = {Hex(io.Timestamp.GetEncodedValue())}  → {io.Timestamp.GetDateTime(2026):yyyy-MM-dd HH:mm:ss.fff}");
                ProcessFaultData(io.FaultCount, io.FaultData, io.GetFaultValues());
            }
            else // 168
            {
                var io = (XujiType168Object)asdu.GetElement(0, ClientPriv);
                Console.WriteLine($"       信息体地址 IOA = 0x{io.ObjectAddress:X6}");
                Console.WriteLine($"       选相 SPE(EncodedValue=0x{io.SPE.EncodedValue:X2})  GC/三相={io.Gc} Cl1/A={io.Cl1} Cl2/B={io.Cl2} Cl3/C={io.Cl3}");
                Console.WriteLine($"       质量 QDP(EncodedValue=0x{io.QDP.EncodedValue:X2})  IV={io.Iv} NT={io.Nt} SB={io.Sb} BL={io.Bl} EI={io.Ei}");
                Console.WriteLine($"       保护动作时间 ElapsedTime = {io.ElapsedTime.ElapsedTimeInMs} ms");
                Console.WriteLine($"       时标 Timestamp = {Hex(io.Timestamp.GetEncodedValue())}  → {io.Timestamp.GetDateTime(2026):yyyy-MM-dd HH:mm:ss.fff}");
                ProcessFaultData(io.FaultCount, io.FaultData, io.GetFaultValues());
            }

            if (System.Threading.Interlocked.Increment(ref _received) >= 2)
            {
                _allReceived.TrySetResult(true);
            }
        }

        /// <summary>主站侧对故障量的实际处理：这里演示打印 byte[] 原始数据与解码后的 float[]。</summary>
        private static void ProcessFaultData(int faultCount, byte[] faultData, float[] values)
        {
            Console.WriteLine($"       FaultCount = {faultCount}");
            Console.WriteLine($"       FaultData(byte[]) = {Hex(faultData)}  (长度 {faultData.Length} = {faultData.Length / 4} × 4字节 R32.23)");
            if (faultData.Length / 4 != faultCount)
            {
                Console.WriteLine($"       (警告: 字节数 {faultData.Length} 与 FaultCount*4={faultCount * 4} 不一致)");
            }

            Console.WriteLine($"       float[] = [{string.Join(", ", values.Select(v => v.ToString("0.######")))}]");
        }

        // ────────────────────────────────────────────────────────────────
        //  从站(Server / 下位机)：链路激活后主动上送 166 / 168
        // ────────────────────────────────────────────────────────────────
        private static async Task PushFaultReportsAsync(Iec104Server server)
        {
            int ca = _localCa.ToCommonAddress();

            // 7 字节时标: ms_lo=0x10, ms_hi=0x0E (=0x0E10=3600ms), min=30, hour=14, day=23, month=7, year=26(=2026)
            byte[] ts = { 0x10, 0x0E, 30, 14, 23, 7, 26 };

            // ── 上送 1：TypeID 166（无选相结果），6 个故障量 ──
            float[] faultValues166 = { 12.5f, -3.14159f, 0.0f, 100.25f, 0.0001f, 99999.0f };
            var faultBytes166 = XujiPrivateTypes.EncodeFaultValues(faultValues166);
            // qual1 = 0x0A: ES=2(动作, 低2位), EI=1(bit3) → 直接构造 SingleEvent
            var evt166 = new SingleEvent(0x0A);
            var io166 = new XujiType166Object(
                ioa: 0x010203,                       // 信息体地址：分段含义查装置信息表
                singleEvent: evt166,
                elapsedTime: new CP16Time2a(3590),
                timestamp: new CP56Time2a(ts, 0),
                faultData: faultBytes166);

            var asdu166 = new ASDU(server.Parameters, CauseOfTransmission.SPONTANEOUS, false, false, 0, ca, false);
            asdu166.AddInformationObject(io166);

            Console.WriteLine($"[从站] 本站 CA = {_localCa}    (扇区/装置地址由 --sector/--device 决定)");
            Console.WriteLine("[从站] 上送 TypeID=166, 6 个故障量 float[] = " +
                              $"[{string.Join(", ", faultValues166.Select(v => v.ToString("0.######")))}]");
            Console.WriteLine($"       故障量 byte[] = {Hex(faultBytes166)}  ({faultBytes166.Length} 字节)  | Event=0x{evt166.EncodedValue:X2} 时标={Hex(io166.Timestamp.GetEncodedValue())}");
            Console.WriteLine(asdu166.ToTelegram(server.Parameters, "从站上送 TypeID=166"));
            await server.BroadcastAsync(asdu166);
            await Task.Delay(400);

            // ── 上送 2：TypeID 168（含选相结果），4 个故障量 ──
            float[] faultValues168 = { 220.0f, 110.5f, 50.0f, 12.345f };
            var faultBytes168 = XujiPrivateTypes.EncodeFaultValues(faultValues168);
            // qual1 = 0x0B: GC=1(bit0) CL1/A=1(bit1) CL3/C=1(bit3) → 直接构造 StartEvent
            var spe168 = new StartEvent(0x0B);
            // qual2 = 0x88: IV=1(bit7) EI=1(bit3) → 直接构造 QualityDescriptorP
            var qdp168 = new QualityDescriptorP(0x88);
            var io168 = new XujiType168Object(
                ioa: 0x010509,                       // 信息体地址：分段含义查装置信息表
                spe: spe168,
                qdp: qdp168,
                elapsedTime: new CP16Time2a(1234),
                timestamp: new CP56Time2a(ts, 0),
                faultData: faultBytes168);

            var asdu168 = new ASDU(server.Parameters, CauseOfTransmission.SPONTANEOUS, false, false, 0, ca, false);
            asdu168.AddInformationObject(io168);

            Console.WriteLine("\n[从站] 上送 TypeID=168, 4 个故障量 float[] = " +
                              $"[{string.Join(", ", faultValues168.Select(v => v.ToString("0.######")))}]");
            Console.WriteLine($"       故障量 byte[] = {Hex(faultBytes168)}  ({faultBytes168.Length} 字节)  | SPE=0x{spe168.EncodedValue:X2} QDP=0x{qdp168.EncodedValue:X2} 时标={Hex(io168.Timestamp.GetEncodedValue())}");
            Console.WriteLine(asdu168.ToTelegram(server.Parameters, "从站上送 TypeID=168"));
            await server.BroadcastAsync(asdu168);
        }

        public static async Task<int> Main(string[] args)
        {
            byte sector = 1, device = 0;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--sector" && byte.TryParse(args[i + 1], out var s)) sector = s;
                if (args[i] == "--device" && byte.TryParse(args[i + 1], out var d)) device = d;
            }

            _localCa = new XujiCa(sector, device);
            _expectedCa = _localCa.ToCommonAddress();
            Console.WriteLine($"许继 104 示例：扇区={sector} 装置地址={device} → CA=0x{_expectedCa:X4} (线上 2 字节小端)\n");

            try
            {
                SelfCheck();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[自检] 失败：" + ex.Message);
                return 1;
            }

            var apci = new APCIParameters();
            var al = new ApplicationLayerParameters();

            // ── 启动从站(Server) ──
            var server = new Iec104Server(apci, al);

            // 链路激活(收到 STARTDT_ACT 并回 CON, 进入 Activated)后, 从站开始主动上送故障报文
            server.ConnectionEvent += (session, ev) =>
            {
                if (ev == ApduConnectionEvent.Activated)
                {
                    Console.WriteLine("[从站] 链路已激活(STARTDT)，开始主动上送故障量\n");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await PushFaultReportsAsync(server);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("[从站] 上送异常: " + ex.Message);
                        }
                    });
                }
            };

            await server.StartAsync(Port);
            Console.WriteLine($"[从站] 已启动，监听端口 {Port}");

            // ── 启动主站(Client) ── autostart=true 时连接后自动发 STARTDT_ACT
            var client = new Iec104Client("127.0.0.1", Port, apci, al);
            client.AsduReceived += OnClientAsdu;
            await client.ConnectAsync();
            _clientParams = client.Parameters;
            Console.WriteLine("[主站] 已连接并激活数据传输");

            // 等两条上送都被主站处理完（最多等 5s）
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
            bool allOk = true;
            try
            {
                await _allReceived.Task.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("\n(超时: 未收全 2 条上送)");
                allOk = false;
            }

            if (allOk && _caMismatch == 0)
            {
                Console.WriteLine($"\n[结果] 2 条上送全部收到，CA 全部匹配（扇区={sector} 装置地址={device}）");
            }
            else
            {
                Console.WriteLine($"\n[结果] 失败：收到 {_received} 条，CA 不匹配 {_caMismatch} 条");
            }

            if (!Console.IsInputRedirected)
            {
                Console.ReadKey();
            }

            Console.WriteLine("\n完成。");
            await client.DisconnectAsync();
            await client.DisposeAsync();
            server.Dispose();

            return (allOk && _caMismatch == 0) ? 0 : 1;
        }
    }
}
