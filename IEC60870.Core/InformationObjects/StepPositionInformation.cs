/*
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
using IEC60870.Core.Quality;



namespace IEC60870.Core.InformationObjects
{
    /// <summary>
    /// Step position information object (M_ST_NA_1)
    /// </summary>
    public class StepPositionInformation : InformationObject
    {

        override public TypeID Type
        {
            get
            {
                return TypeID.M_ST_NA_1;
            }
        }

        override public bool SupportsSequence
        {
            get
            {
                return true;
            }
        }

        private int value;

        /// <summary>
        /// Step position (range -64 ... +63)
        /// </summary>
        /// <value>The value.</value>
        public int Value
        {
            get
            {
                return value;
            }
            set
            {
                if (value > 63)
                    this.value = 63;
                else if (value < -64)
                    this.value = -64;
                else
                    this.value = value;
            }
        }

        private bool isTransient;

        /// <summary>
        /// Gets a value indicating whether this <see cref="IEC60870.Core.InformationObjects.StepPositionInformation"/> is in transient state.
        /// </summary>
        /// <value><c>true</c> if transient; otherwise, <c>false</c>.</value>
        public bool Transient
        {
            get
            {
                return isTransient;
            }
            set
            {
                isTransient = value;
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

        public StepPositionInformation(int ioa, int value, bool isTransient, QualityDescriptor quality)
            : base(ioa)
        {
            if ((value < -64) || (value > 63))
                throw new ArgumentOutOfRangeException("value has to be in range -64 .. 63");

            Value = value;
            Transient = isTransient;
            this.quality = quality;
        }

        public StepPositionInformation(StepPositionInformation original)
            : base(original.ObjectAddress)
        {
            Value = original.Value;
            Transient = original.Transient;
            quality = new QualityDescriptor(original.quality);
        }

        internal StepPositionInformation(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            /* parse VTI (value with transient state indication) */
            byte vti = msg[startIndex++];

            isTransient = ((vti & 0x80) == 0x80);

            value = (vti & 0x7f);

            if (value > 63)
                value = value - 128;

            quality = new QualityDescriptor(msg[startIndex++]);
        }

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);

            byte vti;

            if (value < 0)
                vti = (byte)(value + 128);
            else
                vti = (byte)value;

            if (isTransient)
                vti += 0x80;

            frame.SetNextByte(vti);

            frame.SetNextByte(quality.EncodedValue);
        }

    }

    /// <summary>
    /// Step position information object with CP24Time2a time tag (M_ST_TA_1)
    /// </summary>
    public class StepPositionWithCP24Time2a : StepPositionInformation
    {

        override public TypeID Type
        {
            get
            {
                return TypeID.M_ST_TA_1;
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
            set
            {
                timestamp = value;
            }
        }

        public StepPositionWithCP24Time2a(int ioa, int value, bool isTransient, QualityDescriptor quality, CP24Time2a timestamp)
            : base(ioa, value, isTransient, quality)
        {
            Timestamp = timestamp;
        }

        public StepPositionWithCP24Time2a(StepPositionWithCP24Time2a original)
            : base(original)
        {
            timestamp = new CP24Time2a(original.timestamp);
        }

        internal StepPositionWithCP24Time2a(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            startIndex += 2; /* VTI + quality*/

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
    /// Step position information object with CP56Time2a time tag (M_ST_TB_1)
    /// </summary>
    public class StepPositionWithCP56Time2a : StepPositionInformation
    {

        override public TypeID Type
        {
            get
            {
                return TypeID.M_ST_TB_1;
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
            set
            {
                timestamp = value;
            }
        }

        public StepPositionWithCP56Time2a(int ioa, int value, bool isTransient, QualityDescriptor quality, CP56Time2a timestamp)
            : base(ioa, value, isTransient, quality)
        {
            Timestamp = timestamp;
        }

        public StepPositionWithCP56Time2a(StepPositionWithCP56Time2a original)
            : base(original)
        {
            timestamp = new CP56Time2a(original.timestamp);
        }

        internal StepPositionWithCP56Time2a(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            startIndex += 2; /* skip VTI + quality*/

            /* parse CP24Time2a (time stamp) */
            timestamp = new CP56Time2a(msg, startIndex);
        }

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);

            frame.AppendBytes(timestamp.AsSpan());
        }
    }

}

