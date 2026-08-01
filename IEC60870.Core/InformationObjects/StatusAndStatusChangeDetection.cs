/*
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 *
 *  See COPYING file for the complete license text.
 */

using System;
using System.Text;



namespace IEC60870.Core.InformationObjects
{

    public class StatusAndStatusChangeDetection
    {
        public UInt16 STn
        {
            get
            {
                return (ushort)(encodedValue[0] + (256 * encodedValue[1]));
            }

            set
            {
                encodedValue[0] = (byte)(value % 256);
                encodedValue[1] = (byte)(value / 256);
            }
        }

        public UInt16 CDn
        {
            get
            {
                return (ushort)(encodedValue[2] + (256 * encodedValue[3]));
            }

            set
            {
                encodedValue[2] = (byte)(value % 256);
                encodedValue[3] = (byte)(value / 256);
            }
        }

        public bool ST(int i)
        {
            if ((i >= 0) && (i < 16))
                return ((STn & (1 << i)) != 0);
            else
                return false;
        }

        public void ST(int i, bool value)
        {
            if ((i >= 0) && (i < 16))
            {
                if (value)
                    STn = (UInt16)(STn | (1 << i));
                else
                    STn = (UInt16)(STn & ~(1 << i));
            }
        }

        public bool CD(int i)
        {
            if ((i >= 0) && (i < 16))
                return ((CDn & (1 << i)) != 0);
            else
                return false;
        }

        public void CD(int i, bool value)
        {
            if ((i >= 0) && (i < 16))
            {
                if (value)
                    CDn = (UInt16)(CDn | (1 << i));
                else
                    CDn = (UInt16)(CDn & ~(1 << i));
            }
        }

        public StatusAndStatusChangeDetection()
        {
        }

        public StatusAndStatusChangeDetection(StatusAndStatusChangeDetection original)
        {
            STn = original.STn;
            CDn = original.CDn;
        }

        public StatusAndStatusChangeDetection(byte[] msg, int startIndex)
        {
            if (msg.Length < startIndex + 4)
                throw new ASDUParsingException("Message too small for parsing StatusAndStatusChangeDetection");

            for (int i = 0; i < 4; i++)
                encodedValue[i] = msg[startIndex + i];
        }

        private byte[] encodedValue = new byte[4];

        public byte[] GetEncodedValue()
        {
            return encodedValue;
        }

        /// <summary>
        /// Returns the encoded value as a ReadOnlySpan for zero-allocation encoding.
        /// </summary>
        public ReadOnlySpan<byte> AsSpan() => encodedValue.AsSpan();

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder(50);

            sb.Append("ST:");

            for (int i = 0; i < 16; i++)
                sb.Append(ST(i) ? "1" : "0");

            sb.Append(" CD:");

            for (int i = 0; i < 16; i++)
                sb.Append(CD(i) ? "1" : "0");

            return sb.ToString();
        }
    }
}
