/*
 *  BinaryCounterReading.cs
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



namespace IEC60870.Core.InformationObjects
{

    /// <summary>
    /// Binary counter reading. Used for tranmission of integrated totals.
    /// </summary>
    public class BinaryCounterReading
    {

        private byte[] encodedValue = new byte[5];

        public byte[] GetEncodedValue()
        {
            return encodedValue;
        }

        /// <summary>
        /// Returns the encoded value as a ReadOnlySpan for zero-allocation encoding.
        /// </summary>
        public ReadOnlySpan<byte> AsSpan() => encodedValue.AsSpan();

        /// <summary>
        /// Gets or sets the counter value.
        /// </summary>
        /// <value>The value.</value>
        public Int32 Value
        {
            get
            {
                Int32 value = encodedValue[0];
                value += (encodedValue[1] * 0x100);
                value += (encodedValue[2] * 0x10000);
                value += (encodedValue[3] * 0x1000000);

                return value;
            }

            set
            {
                byte[] valueBytes = BitConverter.GetBytes(value);

                if (BitConverter.IsLittleEndian == false)
                    Array.Reverse(valueBytes);

                Array.Copy(valueBytes, encodedValue, 4);
            }
        }

        /// <summary>
        /// Gets or sets the sequence number.
        /// </summary>
        /// <value>The sequence number.</value>
        public int SequenceNumber
        {
            get
            {
                return (encodedValue[4] & 0x1f);
            }

            set
            {
                int seqNumber = value & 0x1f;
                int flags = encodedValue[4] & 0xe0;

                encodedValue[4] = (byte)(flags | seqNumber);
            }
        }

        /// <summary>
        /// Gets or sets the carry flag
        /// </summary>
        /// <value><c>true</c> if carry flag set; otherwise, <c>false</c>.</value>
        public bool Carry
        {
            get
            {
                return ((encodedValue[4] & 0x20) == 0x20);
            }

            set
            {
                if (value)
                    encodedValue[4] |= 0x20;
                else
                    encodedValue[4] &= 0xdf;
            }
        }

        /// <summary>
        /// Gets or sets the adjusted flag.
        /// </summary>
        /// <value><c>true</c> if adjusted flag is set; otherwise, <c>false</c>.</value>
        public bool Adjusted
        {
            get
            {
                return ((encodedValue[4] & 0x40) == 0x40);
            }

            set
            {
                if (value)
                    encodedValue[4] |= 0x40;
                else
                    encodedValue[4] &= 0xbf;
            }
        }

        /// <summary>
        /// Gets or sets the invalid flag
        /// </summary>
        /// <value><c>true</c> if invalid flag is set; otherwise, <c>false</c>.</value>
        public bool Invalid
        {
            get
            {
                return ((encodedValue[4] & 0x80) == 0x80);
            }

            set
            {
                if (value)
                    encodedValue[4] |= 0x80;
                else
                    encodedValue[4] &= 0x7f;
            }
        }

        public BinaryCounterReading(byte[] msg, int startIndex)
        {
            if (msg.Length < startIndex + 5)
                throw new ASDUParsingException("Message too small for parsing BinaryCounterReading");

            for (int i = 0; i < 5; i++)
                encodedValue[i] = msg[startIndex + i];
        }

        public BinaryCounterReading()
        {
        }

        public BinaryCounterReading(BinaryCounterReading original)
        {
            for (int i = 0; i < 5; i++)
            {
                encodedValue[i] = original.encodedValue[i];
            }
        }
    }
}

