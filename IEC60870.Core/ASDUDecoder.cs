/*
 *  ASDUDecoder.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 */

using System;
using System.Collections.Generic;
using IEC60870.Core.InformationObjects;
using IEC60870.Core.File;



namespace IEC60870.Core
{
    /// <summary>
    /// Zero-allocation, span-based dispatch table for IEC 60870-5-104 ASDU
    /// type identification and per-element size accounting.
    /// </summary>
    /// <remarks>
    /// <para>This is the single source of truth for ASDU element sizes and
    /// decoder behaviour. The legacy <c>ASDU.GetElement(int)</c> switch
    /// is being phased out in favour of <see cref="TryGetElementSize"/>,
    /// which fixes several long-standing bugs (incorrect elementSize for
    /// M_EP_TA_1, M_EI_NA_1, and the timed-command variants).</para>
    /// <para>All methods are pure and side-effect free; they are intended
    /// to be called from the receive hot path.</para>
    /// </remarks>
    internal static class ASDUDecoder
    {
        /// <summary>
        /// Look up the encoded payload size (excluding the IOA prefix) for a
        /// given <see cref="TypeID"/>.
        /// </summary>
        /// <param name="typeId">ASDU type identifier.</param>
        /// <param name="parameters">Application layer parameters.</param>
        /// <param name="payloadSize">Number of payload bytes per element
        /// (excluding the IOA). <c>0</c> for <see cref="TypeID.C_RD_NA_1"/>
        /// and <c>-1</c> for variable-size <see cref="TypeID.F_SG_NA_1"/>
        /// (use <see cref="IsVariableSize"/> + the segment's own LOS byte).</param>
        /// <param name="supportsSequence">Whether this type legally encodes
        /// as a sequence of information objects.</param>
        /// <returns><c>true</c> when the type is recognised; <c>false</c>
        /// for unknown / reserved TypeIDs.</returns>
        public static bool TryGetElementSize(
            TypeID typeId,
            ApplicationLayerParameters parameters,
            out int payloadSize,
            out bool supportsSequence)
        {
            switch (typeId)
            {
                // ── payload 0 ────────────────────────────────────────────
                case TypeID.C_RD_NA_1: /* 102 */
                    payloadSize = 0;
                    supportsSequence = false;
                    return true;

                // ── payload 1 ────────────────────────────────────────────
                case TypeID.M_SP_NA_1:        /* 1  */
                case TypeID.M_DP_NA_1:        /* 3  */
                case TypeID.M_EI_NA_1:        /* 70 — end of init */
                case TypeID.C_SC_NA_1:        /* 45 */
                case TypeID.C_DC_NA_1:        /* 46 */
                case TypeID.C_RC_NA_1:        /* 47 */
                case TypeID.C_IC_NA_1:        /* 100 */
                case TypeID.C_CI_NA_1:        /* 101 */
                case TypeID.C_RP_NA_1:        /* 105 */
                case TypeID.P_AC_NA_1:        /* 113 — parameter activation */
                    payloadSize = 1;
                    supportsSequence = false;
                    return true;

                // ── payload 2 ────────────────────────────────────────────
                case TypeID.M_ME_ND_1:        /* 21 — normalized without quality */
                case TypeID.M_ST_NA_1:        /* 5  */
                case TypeID.C_CD_NA_1:        /* 106 — delay acquisition */
                case TypeID.C_TS_NA_1:        /* 104 — test command */
                    payloadSize = 2;
                    supportsSequence = false;
                    return true;

                // ── payload 3 ────────────────────────────────────────────
                case TypeID.M_ME_NA_1:        /* 9  */
                case TypeID.M_ME_NB_1:        /* 11 */
                case TypeID.C_SE_NA_1:        /* 48 */
                case TypeID.C_SE_NB_1:        /* 49 */
                case TypeID.P_ME_NA_1:        /* 110 */
                case TypeID.P_ME_NB_1:        /* 111 */
                    payloadSize = 3;
                    supportsSequence = true;
                    return true;

                case TypeID.M_EP_TA_1:        /* 17 — was elementSize=3 (bug, real size 6) */
                    payloadSize = 6;
                    supportsSequence = false; // event, not a sequence
                    return true;

                // ── payload 4 ────────────────────────────────────────────
                case TypeID.M_SP_TA_1:        /* 2  — single point + CP24 */
                case TypeID.M_DP_TA_1:        /* 4  — double point + CP24 */
                case TypeID.C_BO_NA_1:        /* 51 */
                case TypeID.F_SC_NA_1:        /* 122 */
                case TypeID.F_AF_NA_1:        /* 124 */
                    payloadSize = 4;
                    supportsSequence = false;
                    return true;

                // ── payload 5 ────────────────────────────────────────────
                case TypeID.M_ST_TA_1:        /* 6  — step + CP24 */
                case TypeID.M_BO_NA_1:        /* 7  */
                case TypeID.M_ME_NC_1:        /* 13 */
                case TypeID.M_IT_NA_1:        /* 15 */
                case TypeID.F_LS_NA_1:        /* 123 */
                case TypeID.M_PS_NA_1:        /* 20 */
                case TypeID.C_SE_NC_1:        /* 50 */
                case TypeID.P_ME_NC_1:        /* 112 */
                case TypeID.C_CS_NA_1:        /* 103 */
                    payloadSize = 5;
                    supportsSequence = false;
                    return true;

                // ── payload 6 ────────────────────────────────────────────
                case TypeID.M_ME_TA_1:        /* 10 — normalized + CP24 */
                case TypeID.M_ME_TB_1:        /* 12 — scaled + CP24 */
                case TypeID.F_FR_NA_1:        /* 120 */
                    payloadSize = 6;
                    supportsSequence = false;
                    return true;

                // ── payload 7 ────────────────────────────────────────────
                case TypeID.M_EP_TB_1:        /* 18 */
                case TypeID.M_EP_TC_1:        /* 19 */
                case TypeID.F_SR_NA_1:        /* 121 */
                    payloadSize = 7;
                    supportsSequence = false;
                    return true;

                // ── payload 8 ────────────────────────────────────────────
                case TypeID.M_BO_TA_1:        /* 8  — bitstring + CP24 */
                case TypeID.M_SP_TB_1:        /* 30 — single + CP56 */
                case TypeID.M_DP_TB_1:        /* 31 — double + CP56 */
                case TypeID.M_ME_TC_1:        /* 14 — short + CP24 */
                case TypeID.M_IT_TA_1:        /* 16 — totals + CP24 */
                case TypeID.C_SC_TA_1:        /* 58 — was inherited 1 (bug) */
                case TypeID.C_DC_TA_1:        /* 59 — was inherited 1 (bug) */
                case TypeID.C_RC_TA_1:        /* 60 — was inherited 1 (bug) */
                    payloadSize = 8;
                    supportsSequence = false;
                    return true;

                // ── payload 9 ────────────────────────────────────────────
                case TypeID.M_ST_TB_1:        /* 32 — step + CP56 */
                case TypeID.C_TS_TA_1:        /* 107 — test + CP56 */
                    payloadSize = 9;
                    supportsSequence = false;
                    return true;

                // ── payload 10 ───────────────────────────────────────────
                case TypeID.M_BO_TB_1:        /* 33 — bitstring + CP56 */
                case TypeID.M_ME_TD_1:        /* 34 — normalized + CP56 */
                case TypeID.M_ME_TE_1:        /* 35 — scaled + CP56 */
                case TypeID.M_EP_TD_1:        /* 38 — event + CP56 */
                case TypeID.C_SE_TA_1:        /* 61 */
                case TypeID.C_SE_TB_1:        /* 62 */
                    payloadSize = 10;
                    supportsSequence = false;
                    return true;

                // ── payload 11 ───────────────────────────────────────────
                case TypeID.M_EP_TE_1:        /* 39 */
                case TypeID.M_EP_TF_1:        /* 40 */
                case TypeID.C_BO_TA_1:        /* 64 */
                    payloadSize = 11;
                    supportsSequence = false;
                    return true;

                // ── payload 12 ───────────────────────────────────────────
                case TypeID.M_ME_TF_1:        /* 36 — short + CP56 */
                case TypeID.M_IT_TB_1:        /* 37 — totals + CP56 */
                case TypeID.C_SE_TC_1:        /* 63 */
                    payloadSize = 12;
                    supportsSequence = false;
                    return true;

                // ── payload 13 ───────────────────────────────────────────
                case TypeID.F_DR_TA_1:        /* 126 — directory */
                    payloadSize = 13;
                    supportsSequence = false;
                    return true;

                // ── variable (FileSegment: 4 + LOS) ──────────────────────
                case TypeID.F_SG_NA_1:        /* 125 — file segment */
                    payloadSize = -1;
                    supportsSequence = false;
                    return true;

                default:
                    payloadSize = 0;
                    supportsSequence = false;
                    return false;
            }
        }

        /// <summary>
        /// Convenience: total bytes per element on the wire for a non-sequence
        /// element, including its IOA prefix.
        /// </summary>
        public static int ElementWireSize(TypeID typeId, ApplicationLayerParameters parameters)
        {
            if (TryGetElementSize(typeId, parameters, out int payload, out _))
            {
                if (payload < 0)
                    return -1; // variable
                return parameters.SizeOfIOA + payload;
            }
            return -1;
        }

        /// <summary>
        /// Returns true for variable-size payloads (<see cref="TypeID.F_SG_NA_1"/>).
        /// </summary>
        public static bool IsVariableSize(TypeID typeId)
        {
            return typeId == TypeID.F_SG_NA_1;
        }

        /// <summary>
        /// Decodes the ASDU header in-place (no payload copy) from a span
        /// describing the post-APCI portion of an APDU.
        /// </summary>
        /// <param name="payload">APDU payload span (TypeID + VSQ + COT [+ OA] + CA + IO bytes).</param>
        /// <param name="parameters">Application layer parameters.</param>
        /// <param name="typeId">Decoded TypeID.</param>
        /// <param name="vsq">Decoded VSQ byte.</param>
        /// <param name="cot">Cause of transmission.</param>
        /// <param name="isTest">Test bit from COT byte.</param>
        /// <param name="isNegative">Negative flag from COT byte.</param>
        /// <param name="oa">Originator address (only when <see cref="ApplicationLayerParameters.SizeOfCOT"/> == 2).</param>
        /// <param name="ca">Common address.</param>
        /// <param name="isSequence">Sequence bit from VSQ.</param>
        /// <param name="numElements">Number of elements encoded in this ASDU.</param>
        /// <param name="payloadBody">Span that points past the ASDU header into the IO payload.</param>
        /// <returns><c>true</c> when the header parsed cleanly and <paramref name="payloadBody"/> is valid.</returns>
        /// <exception cref="ASDUParsingException">Thrown when the header is too short.</exception>
        public static bool TryParseHeader(
            ReadOnlySpan<byte> payload,
            ApplicationLayerParameters parameters,
            out TypeID typeId,
            out byte vsq,
            out CauseOfTransmission cot,
            out bool isTest,
            out bool isNegative,
            out byte oa,
            out int ca,
            out bool isSequence,
            out int numElements,
            out ReadOnlySpan<byte> payloadBody)
        {
            typeId = default;
            vsq = 0;
            cot = default;
            isTest = false;
            isNegative = false;
            oa = 0;
            ca = 0;
            isSequence = false;
            numElements = 0;
            payloadBody = default;

            int headerSize = 2 + parameters.SizeOfCOT + parameters.SizeOfCA;
            if (payload.Length < headerSize)
                return false;

            int p = 0;
            typeId = (TypeID)payload[p++];
            vsq = payload[p++];

            isSequence = (vsq & 0x80) != 0;
            numElements = vsq & 0x7f;

            byte cotByte = payload[p++];
            isTest = (cotByte & 0x80) != 0;
            isNegative = (cotByte & 0x40) != 0;
            cot = (CauseOfTransmission)(cotByte & 0x3f);

            if (parameters.SizeOfCOT == 2)
                oa = payload[p++];

            ca = payload[p++];
            if (parameters.SizeOfCA > 1)
                ca += payload[p++] * 0x100;

            payloadBody = payload.Slice(p);
            return true;
        }

        /// <summary>
        /// Sanity-check that the payload body is large enough to hold
        /// <paramref name="numElements"/> of the given type. Returns false
        /// if the payload is too small.
        /// </summary>
        public static bool ValidatePayloadSize(
            int payloadBodyLength,
            int numElements,
            TypeID typeId,
            ApplicationLayerParameters parameters,
            PrivateInformationObjectTypes privateTypes)
        {
            if (numElements < 0 || numElements > 127)
                return false;

            int elementSize = ElementWireSize(typeId, parameters);
            if (elementSize > 0)
            {
                return payloadBodyLength >= numElements * elementSize;
            }

            if (elementSize < 0)
            {
                // Variable size: best-effort — caller must validate per-element LOS.
                return payloadBodyLength >= numElements * (parameters.SizeOfIOA + 4);
            }

            // Unknown type: fall back to private type factory.
            if (privateTypes != null)
            {
                var factory = privateTypes.GetFactory(typeId);
                if (factory != null)
                {
                    int privateSize = parameters.SizeOfIOA + factory.GetEncodedSize();
                    return payloadBodyLength >= numElements * privateSize;
                }
            }

            return false;
        }
    }
}