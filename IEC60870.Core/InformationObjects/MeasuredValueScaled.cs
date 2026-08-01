

/*
 *  MeasuredValueScaled.cs
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
    public class MeasuredValueScaled : InformationObject
    {

        override public TypeID Type
        {
            get
            {
                return TypeID.M_ME_NB_1;
            }
        }

        override public bool SupportsSequence
        {
            get
            {
                return true;
            }
        }

        private ScaledValue scaledValue;

        public ScaledValue ScaledValue
        {
            get
            {
                return scaledValue;
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

        /// <summary>
        /// Initializes a new instance of the <see cref="IEC60870.Core.InformationObjects.MeasuredValueScaled"/> class.
        /// </summary>
        /// <param name="objectAddress">Information object address</param>
        /// <param name="value">scaled value (range -32768 - 32767) </param>
        /// <param name="quality">quality descriptor (according to IEC 60870-5-101:2003 7.2.6.3)</param>
        public MeasuredValueScaled(int objectAddress, int value, QualityDescriptor quality)
            : base(objectAddress)
        {
            scaledValue = new ScaledValue(value);
            this.quality = quality;
        }

        internal MeasuredValueScaled(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSquence)
            : base(parameters, msg, startIndex, isSquence)
        {
            if (!isSquence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            scaledValue = new ScaledValue(msg, startIndex);
            startIndex += 2;

            /* parse QDS (quality) */
            quality = new QualityDescriptor(msg[startIndex++]);
        }

        public MeasuredValueScaled(MeasuredValueScaled original)
            : base(original.ObjectAddress)
        {
            scaledValue = new ScaledValue(original.ScaledValue);
            quality = new QualityDescriptor(original.quality);
        }

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);

            frame.AppendBytes(scaledValue.AsSpan());

            frame.SetNextByte(quality.EncodedValue);
        }

    }

    public class MeasuredValueScaledWithCP24Time2a : MeasuredValueScaled
    {

        override public TypeID Type
        {
            get
            {
                return TypeID.M_ME_TB_1;
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

        public MeasuredValueScaledWithCP24Time2a(int objectAddress, int value, QualityDescriptor quality, CP24Time2a timestamp)
            : base(objectAddress, value, quality)
        {
            this.timestamp = timestamp;
        }

        public MeasuredValueScaledWithCP24Time2a(MeasuredValueScaledWithCP24Time2a original)
            : base(original)
        {
            timestamp = new CP24Time2a(timestamp);
        }

        internal MeasuredValueScaledWithCP24Time2a(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            startIndex += 3; /* scaledValue + QDS */

            /* parse CP56Time2a (time stamp) */
            timestamp = new CP24Time2a(msg, startIndex);
        }

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);

            frame.AppendBytes(timestamp.AsSpan());
        }

    }

    public class MeasuredValueScaledWithCP56Time2a : MeasuredValueScaled
    {

        override public TypeID Type
        {
            get
            {
                return TypeID.M_ME_TE_1;
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

        public MeasuredValueScaledWithCP56Time2a(int objectAddress, int value, QualityDescriptor quality, CP56Time2a timestamp)
            : base(objectAddress, value, quality)
        {
            this.timestamp = timestamp;
        }

        public MeasuredValueScaledWithCP56Time2a(MeasuredValueScaledWithCP56Time2a original)
            : base(original)
        {
            timestamp = new CP56Time2a(original.timestamp);
        }

        internal MeasuredValueScaledWithCP56Time2a(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            startIndex += 3; /* scaledValue + QDS */

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

