/*
 *  CP56Time2a.cs
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
    public class CP56Time2a
    {
        private byte[] encodedValue = new byte[7];

        public CP56Time2a(byte[] msg, int startIndex)
        {
            if (msg.Length < startIndex + 7)
                throw new ASDUParsingException("Message too small for parsing CP56Time2a");

            for (int i = 0; i < 7; i++)
                encodedValue[i] = msg[startIndex + i];
        }

        public CP56Time2a(DateTime time)
        {
            Millisecond = time.Millisecond;
            Second = time.Second;
            Year = time.Year % 100;
            Month = time.Month;
            DayOfMonth = time.Day;
            Hour = time.Hour;
            Minute = time.Minute;
        }

        public CP56Time2a()
        {
            for (int i = 0; i < 7; i++)
                encodedValue[i] = 0;
        }

        public CP56Time2a(CP56Time2a original)
        {
            for (int i = 0; i < 7; i++)
                encodedValue[i] = original.encodedValue[i];
        }

        public override bool Equals(object obj)
        {
            if (obj is CP56Time2a other)
                return Equals(other);

            return false;
        }

        /// <summary>按字节逐位比较（值相等），避免哈希碰撞被误判为相等。</summary>
        public bool Equals(CP56Time2a other)
        {
            if (other is null)
                return false;

            for (int i = 0; i < encodedValue.Length; i++)
            {
                if (encodedValue[i] != other.encodedValue[i])
                    return false;
            }
            return true;
        }

        public override int GetHashCode()
        {
            // 稳定散列，且无需每次分配 BigInteger（代码评审 #13）。
            int h = 17;
            for (int i = 0; i < encodedValue.Length; i++)
                h = h * 31 + encodedValue[i];
            return h;
        }

        /// <summary>
        /// Gets the date time.
        /// </summary>
        /// <returns>The date time.</returns>
        /// <param name="startYear">Start year.</param>
        public DateTime GetDateTime(int startYear)
        {
            int baseYear = (startYear / 100) * 100;

            if (Year < (startYear % 100))
                baseYear += 100;

            int month = Month;

            if (month == 0)
                month = 1;

            int dayOfMonth = DayOfMonth;

            if (dayOfMonth == 0)
                dayOfMonth = 1;

            DateTime value;

            try
            {
                value = new DateTime(baseYear + Year, month, dayOfMonth, Hour, Minute, Second, Millisecond);
            }
            catch (ArgumentOutOfRangeException)
            {
                value = new DateTime(0);
            }

            return value;
        }

        public DateTime GetDateTime()
        {
            return GetDateTime(1970);
        }


        /// <summary>
        /// Gets or sets the millisecond part of the time value
        /// </summary>
        /// <value>The millisecond.</value>
        public int Millisecond
        {
            get
            {
                return (encodedValue[0] + (encodedValue[1] * 0x100)) % 1000;
            }

            set
            {
                int millies = (Second * 1000) + value;

                encodedValue[0] = (byte)(millies & 0xff);
                encodedValue[1] = (byte)((millies / 0x100) & 0xff);
            }
        }

        /// <summary>
        /// Gets or sets the second (range 0 to 59)
        /// </summary>
        /// <value>The second.</value>
        public int Second
        {
            get
            {
                return (encodedValue[0] + (encodedValue[1] * 0x100)) / 1000;
            }

            set
            {
                int millies = encodedValue[0] + (encodedValue[1] * 0x100);

                int msPart = millies % 1000;

                millies = (value * 1000) + msPart;

                encodedValue[0] = (byte)(millies & 0xff);
                encodedValue[1] = (byte)((millies / 0x100) & 0xff);
            }
        }

        /// <summary>
        /// Gets or sets the minute (range 0 to 59)
        /// </summary>
        /// <value>The minute.</value>
        public int Minute
        {
            get
            {
                return (encodedValue[2] & 0x3f);
            }

            set
            {
                encodedValue[2] = (byte)((encodedValue[2] & 0xc0) | (value & 0x3f));
            }
        }

        /// <summary>
        /// Gets or sets the hour (range 0 to 23)
        /// </summary>
        /// <value>The hour.</value>
        public int Hour
        {
            get
            {
                return (encodedValue[3] & 0x1f);
            }

            set
            {
                encodedValue[3] = (byte)((encodedValue[3] & 0xe0) | (value & 0x1f));
            }
        }

        /// <summary>
        /// Gets or sets the day of week in range from 1 (Monday) until 7 (Sunday)
        /// </summary>
        /// <value>The day of week.</value>
        public int DayOfWeek
        {
            get
            {
                return ((encodedValue[4] & 0xe0) >> 5);
            }

            set
            {
                encodedValue[4] = (byte)((encodedValue[4] & 0x1f) | ((value & 0x07) << 5));
            }
        }

        /// <summary>
        /// Gets or sets the day of month in range 1 to 31.
        /// </summary>
        /// <value>The day of month.</value>
        public int DayOfMonth
        {
            get
            {
                return (encodedValue[4] & 0x1f);
            }

            set
            {
                encodedValue[4] = (byte)((encodedValue[4] & 0xe0) + (value & 0x1f));
            }
        }

        /// <summary>
        /// Gets the month in range from 1 (January) to 12 (December)
        /// </summary>
        /// <value>The month.</value>
        public int Month
        {
            get
            {
                return (encodedValue[5] & 0x0f);
            }

            set
            {
                encodedValue[5] = (byte)((encodedValue[5] & 0xf0) + (value & 0x0f));
            }
        }

        /// <summary>
        /// Gets the year in the range 0 to 99
        /// </summary>
        /// <value>The year.</value>
        public int Year
        {
            get
            {
                return (encodedValue[6] & 0x7f);
            }

            set
            {
                /* limit value to range 0 - 99 */
                value = value % 100;

                encodedValue[6] = (byte)((encodedValue[6] & 0x80) + (value & 0x7f));
            }
        }

        public bool SummerTime
        {
            get
            {
                return ((encodedValue[3] & 0x80) != 0);
            }

            set
            {
                if (value)
                    encodedValue[3] |= 0x80;
                else
                    encodedValue[3] &= 0x7f;
            }
        }

        /// <summary>
        /// Gets a value indicating whether this <see cref="IEC60870.Core.CP56Time2a"/> is invalid.
        /// </summary>
        /// <value><c>true</c> if invalid; otherwise, <c>false</c>.</value>
        public bool Invalid
        {
            get
            {
                return ((encodedValue[2] & 0x80) != 0);
            }

            set
            {
                if (value)
                    encodedValue[2] |= 0x80;
                else
                    encodedValue[2] &= 0x7f;
            }
        }

        /// <summary>
        /// Gets a value indicating whether this <see cref="IEC60870.Core.CP26Time2a"/> was substitued by an intermediate station
        /// </summary>
        /// <value><c>true</c> if substitued; otherwise, <c>false</c>.</value>
        public bool Substituted
        {
            get
            {
                return ((encodedValue[2] & 0x40) == 0x40);
            }

            set
            {
                if (value)
                    encodedValue[2] |= 0x40;
                else
                    encodedValue[2] &= 0xbf;
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
        /// Writes the 7-byte CP56Time2a encoding into <paramref name="destination"/>
        /// without intermediate allocation. Throws if the destination is too small.
        /// </summary>
        public void WriteTo(Span<byte> destination)
        {
            encodedValue.AsSpan().CopyTo(destination);
        }

        public override string ToString()
        {
            return string.Format("[CP56Time2a: Millisecond={0}, Second={1}, Minute={2}, Hour={3}, DayOfWeek={4}, DayOfMonth={5}, Month={6}, Year={7}, SummerTime={8}, Invalid={9} Substituted={10}]", Millisecond, Second, Minute, Hour, DayOfWeek, DayOfMonth, Month, Year, SummerTime, Invalid, Substituted);
        }

    }

}

