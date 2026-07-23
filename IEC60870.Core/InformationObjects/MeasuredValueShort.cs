/*
 *  MeasuredValueShortFloat.cs
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
using IEC60870.Core.Quality;
using IEC60870.Core.Time;



namespace IEC60870.Core.InformationObjects
{
    public class MeasuredValueShort : InformationObject
    {
        override public int GetEncodedSize()
        {
            return 5;
        }

        override public TypeID Type
        {
            get
            {
                return TypeID.M_ME_NC_1;
            }
        }

        override public bool SupportsSequence
        {
            get
            {
                return true;
            }
        }

        private float value;

        public float Value
        {
            get
            {
                return value;
            }
            set
            {
                this.value = value;
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

        public MeasuredValueShort(int objectAddress, float value, QualityDescriptor quality)
            : base(objectAddress)
        {
            this.value = value;
            this.quality = quality;
        }

        public MeasuredValueShort(MeasuredValueShort original)
            : base(original.ObjectAddress)
        {
            value = original.value;
            quality = new QualityDescriptor(original.Quality);
        }

        internal MeasuredValueShort(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            /* parse float value —— 与 Encode 对称：字节以大端平台存储为小端，故解码时
               在非小端平台上需反转后再交给 BitConverter（代码评审 #18）。 */
            byte[] floatBytes = new byte[4];
            Buffer.BlockCopy(msg, startIndex, floatBytes, 0, 4);
            if (System.BitConverter.IsLittleEndian == false)
                System.Array.Reverse(floatBytes);
            value = System.BitConverter.ToSingle(floatBytes, 0);
            startIndex += 4;

            /* parse QDS (quality) */
            quality = new QualityDescriptor(msg[startIndex++]);
        }

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);

            byte[] floatEncoded = BitConverter.GetBytes(value);

            if (BitConverter.IsLittleEndian == false)
                Array.Reverse(floatEncoded);

            frame.AppendBytes(floatEncoded);

            frame.SetNextByte(quality.EncodedValue);
        }
    }

    public class MeasuredValueShortWithCP24Time2a : MeasuredValueShort
    {
        override public int GetEncodedSize()
        {
            return 8;
        }

        override public TypeID Type
        {
            get
            {
                return TypeID.M_ME_TC_1;
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

        public MeasuredValueShortWithCP24Time2a(int objectAddress, float value, QualityDescriptor quality, CP24Time2a timestamp)
            : base(objectAddress, value, quality)
        {
            this.timestamp = timestamp;
        }

        public MeasuredValueShortWithCP24Time2a(MeasuredValueShortWithCP24Time2a original)
            : base(original)
        {
            timestamp = new CP24Time2a(original.timestamp);
        }

        internal MeasuredValueShortWithCP24Time2a(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            startIndex += 5; /* skip float */

            /* parse CP56Time2a (time stamp) */
            timestamp = new CP24Time2a(msg, startIndex);
        }

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);

            frame.AppendBytes(timestamp.GetEncodedValue());
        }

    }

    public class MeasuredValueShortWithCP56Time2a : MeasuredValueShort
    {
        override public int GetEncodedSize()
        {
            return 12;
        }

        override public TypeID Type
        {
            get
            {
                return TypeID.M_ME_TF_1;
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

        public MeasuredValueShortWithCP56Time2a(int objectAddress, float value, QualityDescriptor quality, CP56Time2a timestamp)
            : base(objectAddress, value, quality)
        {
            this.timestamp = timestamp;
        }

        public MeasuredValueShortWithCP56Time2a(MeasuredValueShortWithCP56Time2a original)
            : base(original)
        {
            timestamp = new CP56Time2a(original.timestamp);
        }

        internal MeasuredValueShortWithCP56Time2a(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            startIndex += 5; /* skip float */

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

