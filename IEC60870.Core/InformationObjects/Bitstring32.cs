/*
 *  BitString32.cs
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

    public class Bitstring32 : InformationObject
    {

        override public TypeID Type
        {
            get
            {
                return TypeID.M_BO_NA_1;
            }
        }

        override public bool SupportsSequence
        {
            get
            {
                return true;
            }
        }

        private UInt32 value;

        public UInt32 Value
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

        public Bitstring32(int ioa, UInt32 value, QualityDescriptor quality)
            : base(ioa)
        {
            this.value = value;
            this.quality = quality;
        }

        public Bitstring32(Bitstring32 original)
            : base(original.ObjectAddress)
        {
            value = original.value;
            quality = new QualityDescriptor(original.quality);
        }

        internal Bitstring32(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            value = msg[startIndex++];
            value += ((uint)msg[startIndex++] * 0x100);
            value += ((uint)msg[startIndex++] * 0x10000);
            value += ((uint)msg[startIndex++] * 0x1000000);

            quality = new QualityDescriptor(msg[startIndex++]);

        }

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);

            frame.SetNextByte((byte)(value % 0x100));
            frame.SetNextByte((byte)((value / 0x100) % 0x100));
            frame.SetNextByte((byte)((value / 0x10000) % 0x100));
            frame.SetNextByte((byte)(value / 0x1000000));

            frame.SetNextByte(quality.EncodedValue);
        }
    }

    public class Bitstring32WithCP24Time2a : Bitstring32
    {

        override public TypeID Type
        {
            get
            {
                return TypeID.M_BO_TA_1;
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

        public Bitstring32WithCP24Time2a(int ioa, UInt32 value, QualityDescriptor quality, CP24Time2a timestamp)
            : base(ioa, value, quality)
        {
            this.timestamp = timestamp;
        }

        public Bitstring32WithCP24Time2a(Bitstring32WithCP24Time2a original)
            : base(original)
        {
            timestamp = new CP24Time2a(original.timestamp);
        }

        internal Bitstring32WithCP24Time2a(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            startIndex += 5; /* value + quality */

            /* parse CP24Time2a (time stamp) */
            timestamp = new CP24Time2a(msg, startIndex);
        }

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);

            frame.AppendBytes(timestamp.AsSpan());
        }
    }

    public class Bitstring32WithCP56Time2a : Bitstring32
    {

        override public TypeID Type
        {
            get
            {
                return TypeID.M_BO_TB_1;
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

        public Bitstring32WithCP56Time2a(int ioa, UInt32 value, QualityDescriptor quality, CP56Time2a timestamp)
            : base(ioa, value, quality)
        {
            this.timestamp = timestamp;
        }

        public Bitstring32WithCP56Time2a(Bitstring32WithCP56Time2a original)
            : base(original)
        {
            timestamp = new CP56Time2a(original.timestamp);
        }

        internal Bitstring32WithCP56Time2a(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            startIndex += 5; /* value + quality */

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

