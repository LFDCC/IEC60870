

/*
 *  MeasuredValueNormalized.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 *
 *  See COPYING file for the complete license text.
 */

using IEC60870.Core.Time;
using IEC60870.Core.Quality;
namespace IEC60870.Core.InformationObjects
{
    /// <summary>
    /// Measured value normalized without quality information object (M_ME_ND_1)
    /// </summary>
    public class MeasuredValueNormalizedWithoutQuality : InformationObject
    {
        override public int GetEncodedSize()
        {
            return 2;
        }

        override public TypeID Type
        {
            get
            {
                return TypeID.M_ME_ND_1;
            }
        }

        override public bool SupportsSequence
        {
            get
            {
                return false;
            }
        }

        private ScaledValue scaledValue;

        public short RawValue
        {
            get
            {
                return scaledValue.ShortValue;
            }
            set
            {
                scaledValue.ShortValue = value;
            }
        }

        public float NormalizedValue
        {
            get
            {
                return scaledValue.GetNormalizedValue();
            }
            set
            {
                scaledValue.SetScaledFromNormalizedValue(value);
            }
        }

        public MeasuredValueNormalizedWithoutQuality(int objectAddress, float normalizedValue)
            : base(objectAddress)
        {
            scaledValue = new ScaledValue();
            NormalizedValue = normalizedValue;
        }

        public MeasuredValueNormalizedWithoutQuality(MeasuredValueNormalizedWithoutQuality original)
            : base(original.ObjectAddress)
        {
            scaledValue = new ScaledValue(original.scaledValue);
        }

        public MeasuredValueNormalizedWithoutQuality(int objectAddress, short rawValue)
            : base(objectAddress)
        {
            scaledValue = new ScaledValue(rawValue);
        }

        internal MeasuredValueNormalizedWithoutQuality(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            scaledValue = new ScaledValue(msg, startIndex);
        }

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);

            frame.AppendBytes(scaledValue.GetEncodedValue());
        }
    }

    /// <summary>
    /// Measured value normalized information object (M_ME_NA_1)
    /// </summary>
    public class MeasuredValueNormalized : MeasuredValueNormalizedWithoutQuality
    {
        override public int GetEncodedSize()
        {
            return 3;
        }

        override public TypeID Type
        {
            get
            {
                return TypeID.M_ME_NA_1;
            }
        }

        override public bool SupportsSequence
        {
            get
            {
                return true;
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

        public MeasuredValueNormalized(int objectAddress, float value, QualityDescriptor quality)
            : base(objectAddress, value)
        {
            this.quality = quality;
        }

        public MeasuredValueNormalized(int objectAddress, short value, QualityDescriptor quality)
            : base(objectAddress, value)
        {
            this.quality = quality;
        }

        public MeasuredValueNormalized(MeasuredValueNormalized original)
            : base(original)
        {
            quality = new QualityDescriptor(original.quality);
        }

        internal MeasuredValueNormalized(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            startIndex += 2; /* normalized value */

            /* parse QDS (quality) */
            quality = new QualityDescriptor(msg[startIndex++]);
        }

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);

            frame.SetNextByte(quality.EncodedValue);
        }
    }

    /// <summary>
    /// Measured value normalized with CP24Time2a time tag (M_ME_TA_1)
    /// </summary>
    public class MeasuredValueNormalizedWithCP24Time2a : MeasuredValueNormalized
    {
        override public int GetEncodedSize()
        {
            return 6;
        }

        override public TypeID Type
        {
            get
            {
                return TypeID.M_ME_TA_1;
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


        public MeasuredValueNormalizedWithCP24Time2a(int objectAddress, float value, QualityDescriptor quality, CP24Time2a timestamp)
            : base(objectAddress, value, quality)
        {
            this.timestamp = timestamp;
        }

        public MeasuredValueNormalizedWithCP24Time2a(int objectAddress, short value, QualityDescriptor quality, CP24Time2a timestamp)
            : base(objectAddress, value, quality)
        {
            this.timestamp = timestamp;
        }

        public MeasuredValueNormalizedWithCP24Time2a(MeasuredValueNormalizedWithCP24Time2a original)
            : base(original)
        {
            timestamp = new CP24Time2a(original.timestamp);
        }

        internal MeasuredValueNormalizedWithCP24Time2a(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            startIndex += 3; /* normalized value + quality */

            /* parse CP24Time2a (time stamp) */
            timestamp = new CP24Time2a(msg, startIndex);
        }

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);

            frame.AppendBytes(timestamp.GetEncodedValue());
        }
    }

    /// <summary>
    /// Measured value normalized with CP56Time2a time tag (M_ME_TD_1)
    /// </summary>
    public class MeasuredValueNormalizedWithCP56Time2a : MeasuredValueNormalized
    {
        override public int GetEncodedSize()
        {
            return 10;
        }

        override public TypeID Type
        {
            get
            {
                return TypeID.M_ME_TD_1;
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

        public MeasuredValueNormalizedWithCP56Time2a(int objectAddress, float value, QualityDescriptor quality, CP56Time2a timestamp)
            : base(objectAddress, value, quality)
        {
            this.timestamp = timestamp;
        }

        public MeasuredValueNormalizedWithCP56Time2a(int objectAddress, short value, QualityDescriptor quality, CP56Time2a timestamp)
            : base(objectAddress, value, quality)
        {
            this.timestamp = timestamp;
        }

        public MeasuredValueNormalizedWithCP56Time2a(MeasuredValueNormalizedWithCP56Time2a original)
            : base(original)
        {
            timestamp = new CP56Time2a(original.timestamp);
        }

        internal MeasuredValueNormalizedWithCP56Time2a(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            startIndex += 3; /* normalized value + quality */

            /* parse CP56Time2a (time stamp) */
            timestamp = new CP56Time2a(msg, startIndex);
        }

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);

            frame.AppendBytes(timestamp.GetEncodedValue());
        }
    }

}

