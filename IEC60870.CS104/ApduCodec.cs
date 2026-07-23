/*
 *  ApduCodec.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  IEC60870.Core.NET is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  IEC60870.Core.NET is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with IEC60870.Core.NET.  If not, see <http://www.gnu.org/licenses/>.
 */

using System;


namespace IEC60870.CS104
{
    /// <summary>
    /// IEC 60870-5-104 APDU 帧类型（由控制域1的低2位决定）。
    /// </summary>
    public enum ApduKind : byte
    {
        /// <summary>I format - 信息传输帧，bit0 = 0</summary>
        Information = 0,

        /// <summary>S format - 监视帧，低2位 = 01</summary>
        Supervisory = 1,

        /// <summary>U format - 未编号控制帧，低2位 = 11</summary>
        Unnumbered = 3
    }

    /// <summary>
    /// IEC 60870-5-104 APCI（应用协议控制信息）编解码。
    /// 全部基于 <see cref="Span{T}"/>/<see cref="ReadOnlySpan{T}"/>，热路径零堆分配。
    /// </summary>
    /// <remarks>
    /// APDU 布局：0x68 | len(剩余字节数，不含起始和len自身) | 控制域1..4 | [ASDU]。
    /// APCI 固定 6 字节，ASDU 最长 253，APDU 最长 259 字节。
    /// 序列号 N(S)/N(R) 为 15 位 (0..32767)。
    /// </remarks>
    public static class ApduCodec
    {
        public const byte StartByte = 0x68;

        /// <summary>APCI 固定长度（字节数）。</summary>
        public const int ApciLength = 6;

        /// <summary>APDU 最大长度（APCI + 最大 ASDU）。</summary>
        public const int MaxApduLength = 259; // 6 + 253

        /// <summary>ASDU 最大长度。</summary>
        public const int MaxAsduLength = 253;

        // ── U-format 功能码（放在控制域1，低2位 = 11）──────────────
        public const byte StartDtAct = 0x07;
        public const byte StartDtCon = 0x0B;
        public const byte StopDtAct = 0x13;
        public const byte StopDtCon = 0x23;
        public const byte TestFrAct = 0x43;
        public const byte TestFrCon = 0x83;

        // ── 静态 U-frame 模板（6 字节），发送时直接引用，零分配 ──
        internal static readonly byte[] StartDtActMsg = { 0x68, 0x04, StartDtAct, 0x00, 0x00, 0x00 };
        internal static readonly byte[] StartDtConMsg = { 0x68, 0x04, StartDtCon, 0x00, 0x00, 0x00 };
        internal static readonly byte[] StopDtActMsg = { 0x68, 0x04, StopDtAct, 0x00, 0x00, 0x00 };
        internal static readonly byte[] StopDtConMsg = { 0x68, 0x04, StopDtCon, 0x00, 0x00, 0x00 };
        internal static readonly byte[] TestFrActMsg = { 0x68, 0x04, TestFrAct, 0x00, 0x00, 0x00 };
        internal static readonly byte[] TestFrConMsg = { 0x68, 0x04, TestFrCon, 0x00, 0x00, 0x00 };

        /// <summary>写入 I-format APCI 头（6 字节）。</summary>
        /// <param name="apci">至少 6 字节的目标 span</param>
        /// <param name="sendSeq">发送序列号 N(S) (0..32767)</param>
        /// <param name="recvSeq">接收序列号 N(R) (0..32767)</param>
        /// <param name="asduLength">紧随其后的 ASDU 字节数</param>
        public static void WriteIFormatHeader(Span<byte> apci, int sendSeq, int recvSeq, int asduLength)
        {
            apci[0] = StartByte;
            apci[1] = (byte)(asduLength + 4);          // len = 控制域4字节 + ASDU
            apci[2] = (byte)((sendSeq & 0x7f) << 1);   // bit0 = 0 → I format
            apci[3] = (byte)(sendSeq >> 7);
            apci[4] = (byte)((recvSeq & 0x7f) << 1);
            apci[5] = (byte)(recvSeq >> 7);
        }

        /// <summary>写入 S-format APCI 头（6 字节）。</summary>
        public static void WriteSFormatHeader(Span<byte> apci, int recvSeq)
        {
            apci[0] = StartByte;
            apci[1] = 0x04;
            apci[2] = 0x01;                            // 低2位 = 01 → S format
            apci[3] = 0x00;
            apci[4] = (byte)((recvSeq & 0x7f) << 1);
            apci[5] = (byte)(recvSeq >> 7);
        }

        /// <summary>写入 U-format APCI 头（6 字节）。</summary>
        public static void WriteUFormat(Span<byte> apci, byte uFunction)
        {
            apci[0] = StartByte;
            apci[1] = 0x04;
            apci[2] = uFunction;                       // 低2位须为 11
            apci[3] = 0x00;
            apci[4] = 0x00;
            apci[5] = 0x00;
        }

        /// <summary>
        /// 尝试从 <paramref name="buffer"/> 起始处解析一个完整 APDU。
        /// 全程只读、零分配。
        /// </summary>
        /// <returns>
        /// &gt;0：完整 APDU 总长度（含起始字节）；<br/>
        /// 0：数据不足，需继续读取；<br/>
        /// -1：格式错误（非法起始字节/长度）。
        /// </returns>
        public static int TryParseApdu(ReadOnlySpan<byte> buffer,
            out ApduKind kind, out int sendSeq, out int recvSeq,
            out int payloadOffset, out int payloadLength, out byte uFunction)
        {
            kind = ApduKind.Information;
            sendSeq = -1;
            recvSeq = -1;
            payloadOffset = 0;
            payloadLength = 0;
            uFunction = 0;

            if (buffer.Length < ApciLength)
                return 0;                              // 数据不足
            if (buffer[0] != StartByte)
                return -1;                             // 格式错误

            int len = buffer[1];
            int total = len + 2;
            if (total < ApciLength)
                return -1;                             // APCI 至少 6 字节
            if (total > MaxApduLength)
                return -1;                             // 超长
            if (total > buffer.Length)
                return 0;                              // 数据不足

            payloadOffset = ApciLength;
            payloadLength = total - ApciLength;

            byte ctrl1 = buffer[2];

            // 帧型判定（IEC 60870-5-104 §5.1）：
            //   I 帧: bit0 == 0（低2位为 00 或 10，取决于 N(S) 奇偶）
            //   S 帧: 低2位 == 01
            //   U 帧: 低2位 == 11
            // 注意：不能用 (ctrl1 & 0x03)==0 判 I 帧——当 N(S) 为奇数时
            //       ctrl1 = (N(S)<<1) 的 bit1 为 1，低2位为 10，会被漏判。
            if ((ctrl1 & 0x01) == 0)
            {
                // I format: N(S) = (ctrl1>>1) | (ctrl2<<7)，N(R) 同理取 ctrl3/ctrl4
                kind = ApduKind.Information;
                sendSeq = (ctrl1 >> 1) | (buffer[3] << 7);
                recvSeq = (buffer[4] >> 1) | (buffer[5] << 7);
            }
            else if ((ctrl1 & 0x03) == 0x01)
            {
                // S format: 只携带 N(R)
                kind = ApduKind.Supervisory;
                recvSeq = (buffer[4] >> 1) | (buffer[5] << 7);
            }
            else
            {
                // U format (低2位 == 11): 功能码在 ctrl1
                kind = ApduKind.Unnumbered;
                uFunction = ctrl1;
            }

            return total;
        }

        /// <summary>判断 <paramref name="uFunction"/> 是否为 STARTDT_ACT。</summary>
        public static bool IsStartDtAct(byte uFunction) => uFunction == StartDtAct;

        /// <summary>判断 <paramref name="uFunction"/> 是否为 STARTDT_CON。</summary>
        public static bool IsStartDtCon(byte uFunction) => uFunction == StartDtCon;

        /// <summary>判断 <paramref name="uFunction"/> 是否为 STOPDT_ACT。</summary>
        public static bool IsStopDtAct(byte uFunction) => uFunction == StopDtAct;

        /// <summary>判断 <paramref name="uFunction"/> 是否为 STOPDT_CON。</summary>
        public static bool IsStopDtCon(byte uFunction) => uFunction == StopDtCon;

        /// <summary>判断 <paramref name="uFunction"/> 是否为 TESTFR_ACT。</summary>
        public static bool IsTestFrAct(byte uFunction) => uFunction == TestFrAct;

        /// <summary>判断 <paramref name="uFunction"/> 是否为 TESTFR_CON。</summary>
        public static bool IsTestFrCon(byte uFunction) => uFunction == TestFrCon;
    }
}
