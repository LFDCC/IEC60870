

/*
 *  ApplicationLayerParameters.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 *
 *  See COPYING file for the complete license text.
 */

namespace IEC60870.CS104
{
    /// <summary>
    /// Parameters for the CS 104 APCI (Application Protocol Control Information)
    /// </summary>
    public class APCIParameters
    {
        private int k = 12;

        private int w = 8;

        private int t0 = 10;

        private int t1 = 15;

        private int t2 = 10;

        private int t3 = 20;

        public APCIParameters()
        {
        }

        public APCIParameters Clone()
        {
            APCIParameters copy = new APCIParameters();

            copy.k = k;
            copy.w = w;
            copy.t0 = t0;
            copy.t1 = t1;
            copy.t2 = t2;
            copy.t3 = t3;

            return copy;
        }

        /// <summary>
        /// number of unconfirmed APDUs in I format
        /// (range: 1 .. 32767 (2^15 - 1) - sender will
        ///  stop transmission after k unconfirmed I messages
        /// </summary>
        public int K
        {
            get
            {
                return k;
            }
            set
            {
                k = value;
            }
        }

        /// <summary>
        /// number of unconfirmed APDUs in I format 
        /// (range: 1 .. 32767 (2^15 - 1) - receiver
        /// will confirm latest after w messages
        /// </summary>
        public int W
        {
            get
            {
                return w;
            }
            set
            {
                w = value;
            }
        }

        /// <summary>
        /// Timeout for connection establishment (in s)
        /// </summary>
        /// <value>timeout t0</value>
        public int T0
        {
            get
            {
                return t0;
            }
            set
            {
                t0 = value;
            }
        }

        /// <summary>
        /// timeout for transmitted APDUs in I/U format (in s)
        /// when timeout elapsed without confirmation the connection
        /// will be closed
        /// </summary>
        /// <value>timeout t1</value>
        public int T1
        {
            get
            {
                return t1;
            }
            set
            {
                t1 = value;
            }
        }

        /// <summary>
        /// timeout to confirm messages (in s)
        /// </summary>
        /// <value>timeout t2</value>
        public int T2
        {
            get
            {
                return t2;
            }
            set
            {
                t2 = value;
            }
        }

        /// <summary>
        /// time until sending test telegrams in case of idle connection
        /// </summary>
        /// <value>timeout t3</value>
        public int T3
        {
            get
            {
                return t3;
            }
            set
            {
                t3 = value;
            }
        }
    }
}