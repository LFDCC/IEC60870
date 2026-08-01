

/*
 *  CP16Time2a.cs
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

namespace IEC60870.Core.Time
{
    public class CP16Time2a
    {
        private byte[] encodedValue = new byte[2];

        public CP16Time2a(byte[] msg, int startIndex)
        {
            if (msg.Length < startIndex + 2)
                throw new ASDUParsingException("Message too small for parsing CP16Time2a");

            for (int i = 0; i < 2; i++)
                encodedValue[i] = msg[startIndex + i];
        }

        public CP16Time2a(int elapsedTimeInMs)
        {
            ElapsedTimeInMs = elapsedTimeInMs;
        }

        public CP16Time2a()
        {
            for (int i = 0; i < 2; i++)
                encodedValue[i] = 0;
        }

        public CP16Time2a(CP16Time2a original)
        {
            for (int i = 0; i < 2; i++)
                encodedValue[i] = original.encodedValue[i];
        }

        public override bool Equals(object obj)
        {
            if (obj == null)
                return false;

            if (!(obj is CP16Time2a))
                return false;

            return (GetHashCode() == obj.GetHashCode());
        }

        public override int GetHashCode()
        {
            return new System.Numerics.BigInteger(encodedValue).GetHashCode();
        }

        public int ElapsedTimeInMs
        {
            get
            {
                return (encodedValue[0] + (encodedValue[1] * 0x100));
            }

            set
            {
                encodedValue[0] = (byte)(value % 0x100);
                encodedValue[1] = (byte)(value / 0x100);
            }
        }

        public byte[] GetEncodedValue()
        {
            return encodedValue;
        }

        /// <summary>
        /// Returns the encoded value as a ReadOnlySpan for zero-allocation encoding.
        /// </summary>
        public ReadOnlySpan<byte> AsSpan() => encodedValue.AsSpan();

        /// <summary>
        /// Writes the 2-byte CP16Time2a encoding into <paramref name="destination"/>
        /// without intermediate allocation. Throws if the destination is too small.
        /// </summary>
        public void WriteTo(Span<byte> destination)
        {
            encodedValue.AsSpan().CopyTo(destination);
        }

        public override string ToString()
        {
            return ElapsedTimeInMs.ToString();
        }
    }
}

