

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
    public class QualityDescriptor
    {
        private byte encodedValue;

        public static QualityDescriptor VALID()
        {
            return new QualityDescriptor();
        }

        public static QualityDescriptor INVALID()
        {
            var qd = new QualityDescriptor();
            qd.Invalid = true;
            return qd;
        }

        public QualityDescriptor()
        {
            encodedValue = 0;
        }

        public QualityDescriptor(QualityDescriptor original)
        {
            encodedValue = original.encodedValue;
        }

        public QualityDescriptor(byte encodedValue)
        {
            this.encodedValue = encodedValue;
        }

        public override bool Equals(object obj)
        {
            if (obj == null)
                return false;

            if (!(obj is QualityDescriptor))
                return false;

            return (encodedValue == ((QualityDescriptor)obj).encodedValue);
        }

        public override int GetHashCode()
        {
            return encodedValue.GetHashCode();
        }

        public bool Overflow
        {
            get
            {
                if ((encodedValue & 0x01) != 0)
                    return true;
                else
                    return false;
            }

            set
            {
                if (value)
                    encodedValue |= 0x01;
                else
                    encodedValue &= 0xfe;
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

        public override string ToString()
        {
            return string.Format("[QualityDescriptor: Overflow={0}, Blocked={1}, Substituted={2}, NonTopical={3}, Invalid={4}]", Overflow, Blocked, Substituted, NonTopical, Invalid);
        }
    }

}

