

/*
 *  DoublePointInformation.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 *
 *  See COPYING file for the complete license text.
 */

using IEC60870.Core.Quality;
using IEC60870.Core.Time;
namespace IEC60870.Core.InformationObjects
{
    /// <summary>
    /// Double point value
    /// </summary>
    public enum DoublePointValue
    {
        INTERMEDIATE = 0,
        OFF = 1,
        ON = 2,
        INDETERMINATE = 3
    }

    /// <summary>
    /// Double point information object (M_DP_NA_1)
    /// </summary>
    public class DoublePointInformation : InformationObject
    {

        override public TypeID Type
        {
            get
            {
                return TypeID.M_DP_NA_1;
            }
        }

        override public bool SupportsSequence
        {
            get
            {
                return true;
            }
        }

        private DoublePointValue value;

        public DoublePointValue Value
        {
            get
            {
                return value;
            }
        }

        private QualityDescriptor quality;

        public QualityDescriptor Quality
        {
            get
            {
                return quality;
            }
        }

        public DoublePointInformation(int ioa, DoublePointValue value, QualityDescriptor quality)
            : base(ioa)
        {
            this.value = value;
            this.quality = quality;
        }

        public DoublePointInformation(DoublePointInformation original)
            : base(original.ObjectAddress)
        {
            value = original.value;
            quality = new QualityDescriptor(original.quality);
        }

        internal DoublePointInformation(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            /* parse DIQ (double point information with qualitiy) */
            byte diq = msg[startIndex++];

            value = (DoublePointValue)(diq & 0x03);

            quality = new QualityDescriptor((byte)(diq & 0xf0));
        }

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);

            byte val = quality.EncodedValue;

            val += (byte)value;

            frame.SetNextByte(val);
        }
    }

    /// <summary>
    /// Double point information object with CP24Time2a time tag (M_DP_TA_1)
    /// </summary>
    public class DoublePointWithCP24Time2a : DoublePointInformation
    {

        override public TypeID Type
        {
            get
            {
                return TypeID.M_DP_TA_1;
            }
        }

        override public bool SupportsSequence
        {
            get
            {
                return false;
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

        public DoublePointWithCP24Time2a(int ioa, DoublePointValue value, QualityDescriptor quality, CP24Time2a timestamp)
            : base(ioa, value, quality)
        {
            this.timestamp = timestamp;
        }

        public DoublePointWithCP24Time2a(DoublePointWithCP24Time2a original)
            : base(original)
        {
            timestamp = new CP24Time2a(original.timestamp);
        }

        internal DoublePointWithCP24Time2a(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            startIndex += 1; /* skip DIQ */

            /* parse CP24Time2a (time stamp) */
            timestamp = new CP24Time2a(msg, startIndex);
        }

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);

            frame.AppendBytes(timestamp.AsSpan());
        }
    }

    /// <summary>
    /// Double point information object with CP56Time2a time tag (M_DP_TB_1)
    /// </summary>
    public class DoublePointWithCP56Time2a : DoublePointInformation
    {

        override public TypeID Type
        {
            get
            {
                return TypeID.M_DP_TB_1;
            }
        }

        override public bool SupportsSequence
        {
            get
            {
                return false;
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

        public DoublePointWithCP56Time2a(int ioa, DoublePointValue value, QualityDescriptor quality, CP56Time2a timestamp)
            : base(ioa, value, quality)
        {
            this.timestamp = timestamp;
        }

        public DoublePointWithCP56Time2a(DoublePointWithCP56Time2a original)
            : base(original)
        {
            timestamp = new CP56Time2a(original.timestamp);
        }

        internal DoublePointWithCP56Time2a(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            startIndex += 1; /* skip DIQ */

            /* parse CP56Time2a (time stamp) */
            timestamp = new CP56Time2a(msg, startIndex);
        }

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);

            frame.AppendBytes(timestamp.AsSpan());
        }
    }

}

