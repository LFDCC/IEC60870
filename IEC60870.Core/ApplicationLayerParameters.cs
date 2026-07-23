

/*
 *  ApplicationLayerParameters.cs
 *
 *  Copyright 2017-2022 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 *
 *  See COPYING file for the complete license text.
 */

namespace IEC60870.Core
{
    public class ApplicationLayerParameters
    {
        public static int IEC60870_5_104_MAX_ASDU_LENGTH = 249;

        private int sizeOfTypeId = 1;

        /* VSQ = variable sturcture qualifier */
        private int sizeOfVSQ = 1;

        /* (parameter b) COT = cause of transmission (1/2) */
        private int sizeOfCOT = 2;

        private int originatorAddress = 0;

        /* (parameter a) CA = common address of ASDUs (1/2) */
        private int sizeOfCA = 2;

        /* (parameter c) IOA = information object address (1/2/3) */
        private int sizeOfIOA = 3;

        /* maximum length of ASDU */
        private int maxAsduLength = IEC60870_5_104_MAX_ASDU_LENGTH;

        public ApplicationLayerParameters()
        {
        }

        public ApplicationLayerParameters Clone()
        {
            ApplicationLayerParameters copy = new ApplicationLayerParameters();

            copy.sizeOfTypeId = sizeOfTypeId;
            copy.sizeOfVSQ = sizeOfVSQ;
            copy.sizeOfCOT = sizeOfCOT;
            copy.originatorAddress = originatorAddress;
            copy.sizeOfCA = sizeOfCA;
            copy.sizeOfIOA = sizeOfIOA;
            copy.maxAsduLength = maxAsduLength;

            return copy;
        }

        public int SizeOfCOT
        {
            get
            {
                return sizeOfCOT;
            }
            set
            {
                sizeOfCOT = value;
            }
        }

        public int OA
        {
            get
            {
                return originatorAddress;
            }
            set
            {
                originatorAddress = value;
            }
        }

        public int SizeOfCA
        {
            get
            {
                return sizeOfCA;
            }
            set
            {
                sizeOfCA = value;
            }
        }

        public int SizeOfIOA
        {
            get
            {
                return sizeOfIOA;
            }
            set
            {
                sizeOfIOA = value;
            }
        }


        public int SizeOfTypeId
        {
            get
            {
                return sizeOfTypeId;
            }
        }

        public int SizeOfVSQ
        {
            get
            {
                return sizeOfVSQ;
            }
        }

        public int MaxAsduLength
        {
            get
            {
                return maxAsduLength;
            }
            set
            {
                maxAsduLength = value;
            }
        }
    }
}

