/*
 *  IntegratedTotals.cs
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
using IEC60870.Core.Time;



namespace IEC60870.Core.InformationObjects
{
    /// <summary>
    /// Integrated totals information object (M_IT_NA_1)
    /// </summary>
    public class IntegratedTotals : InformationObject
    {
        override public int GetEncodedSize()
        {
            return 5;
        }

        override public TypeID Type
        {
            get
            {
                return TypeID.M_IT_NA_1;
            }
        }

        override public bool SupportsSequence
        {
            get
            {
                return true;
            }
        }

        private BinaryCounterReading bcr;

        public BinaryCounterReading BCR
        {
            get
            {
                return bcr;
            }
        }

        public IntegratedTotals(int ioa, BinaryCounterReading bcr)
            : base(ioa)
        {
            this.bcr = bcr;
        }

        public IntegratedTotals(IntegratedTotals original)
            : base(original.ObjectAddress)
        {
            bcr = new BinaryCounterReading(original.bcr);
        }

        internal IntegratedTotals(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSquence)
            : base(parameters, msg, startIndex, isSquence)
        {
            if (!isSquence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            bcr = new BinaryCounterReading(msg, startIndex);
        }

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);

            frame.AppendBytes(bcr.GetEncodedValue());
        }
    }

    /// <summary>
    /// Integrated totals information object with CP24Time2a time tag (M_IT_TA_1)
    /// </summary>
    public class IntegratedTotalsWithCP24Time2a : IntegratedTotals
    {
        override public int GetEncodedSize()
        {
            return 8;
        }

        override public TypeID Type
        {
            get
            {
                return TypeID.M_IT_TA_1;
            }
        }

        override public bool SupportsSequence
        {
            get
            {
                return true;
            }
        }

        private CP24Time2a timestamp;

        public CP24Time2a Timestamp
        {
            get
            {
                return timestamp;
            }
        }

        public IntegratedTotalsWithCP24Time2a(int ioa, BinaryCounterReading bcr, CP24Time2a timestamp)
            : base(ioa, bcr)
        {
            this.timestamp = timestamp;
        }

        public IntegratedTotalsWithCP24Time2a(IntegratedTotalsWithCP24Time2a original)
            : base(original)
        {
            timestamp = new CP24Time2a(timestamp);
        }

        public IntegratedTotalsWithCP24Time2a(IntegratedTotals original)
            : base(original)
        {
            timestamp = new CP24Time2a();
        }

        internal IntegratedTotalsWithCP24Time2a(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            startIndex += 5; /* BCR */

            timestamp = new CP24Time2a(msg, startIndex);
        }

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);

            frame.AppendBytes(timestamp.GetEncodedValue());
        }
    }

    /// <summary>
    /// Integrated totals information object with CP56Time2a time tag (M_IT_TB_1)
    /// </summary>
    public class IntegratedTotalsWithCP56Time2a : IntegratedTotals
    {
        override public int GetEncodedSize()
        {
            return 12;
        }

        override public TypeID Type
        {
            get
            {
                return TypeID.M_IT_TB_1;
            }
        }

        override public bool SupportsSequence
        {
            get
            {
                return true;
            }
        }

        private CP56Time2a timestamp;

        public CP56Time2a Timestamp
        {
            get
            {
                return timestamp;
            }
        }

        public IntegratedTotalsWithCP56Time2a(int ioa, BinaryCounterReading bcr, CP56Time2a timestamp)
            : base(ioa, bcr)
        {
            this.timestamp = timestamp;
        }

        public IntegratedTotalsWithCP56Time2a(IntegratedTotalsWithCP56Time2a original)
            : base(original)
        {
            timestamp = new CP56Time2a(original.timestamp);
        }

        public IntegratedTotalsWithCP56Time2a(IntegratedTotals original)
            : base(original)
        {
            timestamp = new CP56Time2a(DateTime.Now);
        }

        public IntegratedTotalsWithCP56Time2a(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            startIndex += 5; /* BCR */

            timestamp = new CP56Time2a(msg, startIndex);
        }

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);

            frame.AppendBytes(timestamp.GetEncodedValue());
        }
    }
}

