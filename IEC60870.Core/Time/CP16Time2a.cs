

/*
 *  CP16Time2a.cs
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
 *
 *  See COPYING file for the complete license text.
 */

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

        public override string ToString()
        {
            return ElapsedTimeInMs.ToString();
        }
    }
}

