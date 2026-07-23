

/*
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 *
 *  See COPYING file for the complete license text.
 */

namespace IEC60870.Core.Quality
{

    /// <summary>
    /// QDP - Quality descriptor for events of protection equipment
    /// according to IEC 60870-5-101:2003 7.2.6.4
    /// </summary>
    public class QualityDescriptorP
    {
        private byte encodedValue;

        public QualityDescriptorP()
        {
            encodedValue = 0;
        }

        public QualityDescriptorP(byte encodedValue)
        {
            this.encodedValue = encodedValue;
        }

        public QualityDescriptorP(QualityDescriptorP original)
        {
            encodedValue = original.encodedValue;
        }

        public override bool Equals(object obj)
        {
            if (obj == null)
                return false;

            if (!(obj is QualityDescriptorP))
                return false;

            return (encodedValue == ((QualityDescriptorP)obj).encodedValue);
        }

        public override int GetHashCode()
        {
            return encodedValue.GetHashCode();
        }

        public bool Reserved
        {
            get
            {
                return ((encodedValue & 0x04) == 0x04);
            }

            set
            {
                if (value)
                    encodedValue |= 0x04;
                else
                    encodedValue &= 0xfb;
            }
        }

        public bool ElapsedTimeInvalid
        {
            get
            {
                return ((encodedValue & 0x08) == 0x08);
            }

            set
            {
                if (value)
                    encodedValue |= 0x08;
                else
                    encodedValue &= 0xf7;
            }
        }

        public bool Blocked
        {
            get
            {
                if ((encodedValue & 0x10) != 0)
                    return true;
                else
                    return false;
            }

            set
            {
                if (value)
                    encodedValue |= 0x10;
                else
                    encodedValue &= 0xef;
            }
        }

        public bool Substituted
        {
            get
            {
                if ((encodedValue & 0x20) != 0)
                    return true;
                else
                    return false;
            }

            set
            {
                if (value)
                    encodedValue |= 0x20;
                else
                    encodedValue &= 0xdf;
            }
        }

        public bool NonTopical
        {
            get
            {
                if ((encodedValue & 0x40) != 0)
                    return true;
                else
                    return false;
            }

            set
            {
                if (value)
                    encodedValue |= 0x40;
                else
                    encodedValue &= 0xbf;
            }
        }

        public bool Invalid
        {
            get
            {
                if ((encodedValue & 0x80) != 0)
                    return true;
                else
                    return false;
            }

            set
            {
                if (value)
                    encodedValue |= 0x80;
                else
                    encodedValue &= 0x7f;
            }
        }

        public byte EncodedValue
        {
            get
            {
                return encodedValue;
            }
            set
            {
                encodedValue = value;
            }
        }
    }


}
