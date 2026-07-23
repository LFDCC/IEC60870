

/*
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 *
 *  See COPYING file for the complete license text.
 */

namespace IEC60870.Core.InformationObjects
{
    public class SingleCommandQualifier
    {
        private byte encodedValue;

        public SingleCommandQualifier(byte encodedValue)
        {
            this.encodedValue = encodedValue;
        }

        public SingleCommandQualifier(bool state, bool selectCommand, int qu)
        {
            encodedValue = (byte)((qu & 0x1f) * 4);

            if (state)
                encodedValue |= 0x01;

            if (selectCommand)
                encodedValue |= 0x80;
        }

        public int QU
        {
            get
            {
                return ((encodedValue & 0x7c) / 4);
            }
        }

        public bool State
        {
            get
            {
                return ((encodedValue & 0x01) == 0x01);
            }
        }

        public bool Select
        {
            get
            {
                return ((encodedValue & 0x80) == 0x80);
            }
        }

        public byte GetEncodedValue()
        {
            return encodedValue;
        }

    }
}

