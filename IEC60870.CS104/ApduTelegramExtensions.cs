/*
 *  Copyright 2026 LFDCC
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 */

// 调试辅助：CS104 APDU / ASDU → 报文(telegram)文本
//
// 作为 IEC60870.CS104 的内置扩展方法，所有引用本库的项目均可直接使用，便于开发调试：
//
//   // 完整 APDU（含 APCI）：链路捕获的整帧、或从站上送前预览
//   byte[] apdu = view.Raw.ToArray();
//   Console.WriteLine(apdu.ToTelegram("主站接收"));
//   Console.WriteLine(asdu.ToTelegram(server.Parameters, "从站上送"));
//
// 说明：本方法依赖 ApduCodec（位于 IEC60870.CS104），因此放在 CS104 程序集而非 Core，
// 以避免 Core → CS104 的循环依赖（CS104 已引用 Core）。CS101 走 FT1.2 帧，无 APCI，
// 故不需要此格式化器。

using System;
using System.Text;
using IEC60870.Core;

namespace IEC60870.CS104
{
    /// <summary>
    /// 把 CS104 APDU / ASDU 渲染为便于开发调试的报文文本（IEC60870.CS104 内置，全局可用）。
    /// </summary>
    public static class ApduTelegramExtensions
    {
    #region 报文(APDU)格式化

    /// <summary>
    /// 把一条完整的 CS104 APDU（APCI + ASDU 字节）渲染为便于调试的报文文本。
    /// 自动借助 <see cref="ApduCodec"/> 解析帧型、序列号、ASDU 长度，并分段列出
    /// APCI / ASDU / 整帧 的 16 进制。
    /// </summary>
    /// <param name="apdu">完整 APDU 字节（含 0x68 起始字节与 APCI），或裸 ASDU 载荷</param>
    /// <param name="direction">方向标签（如 "主站接收"/"从站上送"），可为 null</param>
    /// <returns>多行报文文本</returns>
    public static string ToTelegram(this byte[] apdu, string direction = null)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(direction))
            sb.AppendLine($"──── {direction} ────");

        if (apdu == null || apdu.Length == 0)
        {
            sb.AppendLine("(空报文)");
            return sb.ToString();
        }

        // 完整 APDU（以 0x68 起始）才做 APCI 解析；裸 ASDU（如主站回调只给 ASDU 载荷）
        // 则包装成「展示用 I 帧」便于与发送侧对照。
        if (apdu[0] == ApduCodec.StartByte
            && ApduCodec.TryParseApdu(
                apdu, out var kind, out var sendSeq, out var recvSeq,
                out var payloadOffset, out var payloadLength, out var uFunction) > 0
            && payloadOffset >= ApduCodec.ApciLength)
        {
            sb.AppendLine($"帧型={kind}  APDU={apdu.Length}字节  APCI={ApduCodec.ApciLength}  ASDU={payloadLength}");
            if (kind == ApduKind.Information)
                sb.AppendLine($"N(S)={sendSeq}  N(R)={recvSeq}");
            else if (kind == ApduKind.Supervisory)
                sb.AppendLine($"N(R)={recvSeq}");
            else if (kind == ApduKind.Unnumbered)
                sb.AppendLine($"U功能码=0x{uFunction:X2}");

            sb.AppendLine($"APCI : {apdu.AsSpan(0, ApduCodec.ApciLength).ToArray().ToHex()}");
            if (payloadLength > 0)
                sb.AppendLine($"ASDU : {apdu.AsSpan(payloadOffset, payloadLength).ToArray().ToHex()}");

            sb.AppendLine($"整帧 : {apdu.ToHex()}");
            return sb.ToString();
        }

        // 裸 ASDU：包装成展示用 I 帧（N(S)/N(R) 用 0，仅用于结构对照）
        var disp = new byte[ApduCodec.ApciLength + apdu.Length];
        ApduCodec.WriteIFormatHeader(disp, 0, 0, apdu.Length);
        apdu.CopyTo(disp, ApduCodec.ApciLength);

        sb.AppendLine("(输入为裸 ASDU，已按展示用 I 帧包装)");
        sb.AppendLine($"APCI : {disp.AsSpan(0, ApduCodec.ApciLength).ToArray().ToHex()}");
        sb.AppendLine($"ASDU : {apdu.ToHex()}");
        sb.AppendLine($"整帧 : {disp.ToHex()}");
        return sb.ToString();
    }

    /// <summary>
    /// 把一条 ASDU（不含 APCI）包装成 I 帧 APDU，并渲染为便于上送前预览的报文文本。
    /// 注意：此处的 N(S)/N(R) 仅用于展示，默认 0；真实链路的序号由连接状态机维护。
    /// </summary>
    /// <param name="asdu">应用服务数据单元</param>
    /// <param name="parameters">应用层参数（用于编码 ASDU 字节）</param>
    /// <param name="direction">方向标签，可为 null</param>
    /// <param name="sendSeq">展示用发送序号 N(S)</param>
    /// <param name="recvSeq">展示用接收序号 N(R)</param>
    /// <returns>多行报文文本</returns>
    public static string ToTelegram(this ASDU asdu, ApplicationLayerParameters parameters,
        string direction = null, int sendSeq = 0, int recvSeq = 0)
    {
        if (asdu == null) return "(空 ASDU)";

        byte[] asduBytes = asdu.AsByteArray();
        if (asduBytes == null)
        {
            // 兜底：用 BufferFrame 直接编码，避免 AsByteArray 因空间计算差异返回 null
            var buf = new byte[parameters.MaxAsduLength];
            var frame = new BufferFrame(buf, 0);
            asdu.Encode(frame, parameters);
            asduBytes = new byte[frame.GetMsgSize()];
            Array.Copy(buf, asduBytes, asduBytes.Length);
        }

        var apdu = new byte[ApduCodec.ApciLength + asduBytes.Length];
        ApduCodec.WriteIFormatHeader(apdu, sendSeq, recvSeq, asduBytes.Length);
        asduBytes.CopyTo(apdu, ApduCodec.ApciLength);

        return apdu.ToTelegram(direction);
    }

        #endregion
    }
}
