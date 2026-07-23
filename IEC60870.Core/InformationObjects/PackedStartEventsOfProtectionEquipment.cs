

/*
 *  PackedStartEventsOfProtectionEquipment.cs
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

    public class PackedStartEventsOfProtectionEquipment : InformationObject
    {
        override public int GetEncodedSize()
        {
            return 7;
        }

        override public TypeID Type
        {
            get
            {
                return TypeID.M_EP_TB_1;
            }
        }

        override public bool SupportsSequence
        {
            get
            {
                return true;
            }
        }

        private StartEvent spe;

        public StartEvent SPE
        {
            get
            {
                return spe;
            }
        }

        private QualityDescriptorP qdp;

        public QualityDescriptorP QDP
        {
            get
            {
                return qdp;
            }
        }

        private CP16Time2a elapsedTime;

        public CP16Time2a ElapsedTime
        {
            get
            {
                return elapsedTime;
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

        public PackedStartEventsOfProtectionEquipment(int objectAddress, StartEvent spe, QualityDescriptorP qdp, CP16Time2a elapsedTime, CP24Time2a timestamp)
            : base(objectAddress)
        {
            this.spe = spe;
            this.qdp = qdp;
            this.elapsedTime = elapsedTime;
            this.timestamp = timestamp;
        }

        public PackedStartEventsOfProtectionEquipment(PackedStartEventsOfProtectionEquipment original)
            : base(original.ObjectAddress)
        {
            spe = new StartEvent(original.spe);
            qdp = new QualityDescriptorP(original.qdp);
            elapsedTime = new CP16Time2a(original.elapsedTime);
            timestamp = new CP24Time2a(original.timestamp);
        }

        internal PackedStartEventsOfProtectionEquipment(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            spe = new StartEvent(msg[startIndex++]);
            qdp = new QualityDescriptorP(msg[startIndex++]);

            elapsedTime = new CP16Time2a(msg, startIndex);
            startIndex += 2;

            /* parse CP56Time2a (time stamp) */
            timestamp = new CP24Time2a(msg, startIndex);
        }

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);

            frame.SetNextByte(spe.EncodedValue);

            frame.SetNextByte(qdp.EncodedValue);

            frame.AppendBytes(elapsedTime.GetEncodedValue());

            frame.AppendBytes(timestamp.GetEncodedValue());
        }
    }

    public class PackedStartEventsOfProtectionEquipmentWithCP56Time2a : InformationObject
    {
        override public int GetEncodedSize()
        {
            return 11;
        }

        override public TypeID Type
        {
            get
            {
                return TypeID.M_EP_TE_1;
            }
        }

        override public bool SupportsSequence
        {
            get
            {
                return true;
            }
        }

        private StartEvent spe;

        public StartEvent SPE
        {
            get
            {
                return spe;
            }
        }

        private QualityDescriptorP qdp;

        public QualityDescriptorP QDP
        {
            get
            {
                return qdp;
            }
        }

        private CP16Time2a elapsedTime;

        public CP16Time2a ElapsedTime
        {
            get
            {
                return elapsedTime;
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

        public PackedStartEventsOfProtectionEquipmentWithCP56Time2a(int objectAddress, StartEvent spe, QualityDescriptorP qdp, CP16Time2a elapsedTime, CP56Time2a timestamp)
            : base(objectAddress)
        {
            this.spe = spe;
            this.qdp = qdp;
            this.elapsedTime = elapsedTime;
            this.timestamp = timestamp;
        }

        public PackedStartEventsOfProtectionEquipmentWithCP56Time2a(PackedStartEventsOfProtectionEquipmentWithCP56Time2a original)
            : base(original.ObjectAddress)
        {
            spe = new StartEvent(original.spe);
            qdp = new QualityDescriptorP(original.qdp);
            elapsedTime = new CP16Time2a(original.elapsedTime);
            timestamp = new CP56Time2a(original.timestamp);
        }

        internal PackedStartEventsOfProtectionEquipmentWithCP56Time2a(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            spe = new StartEvent(msg[startIndex++]);
            qdp = new QualityDescriptorP(msg[startIndex++]);

            elapsedTime = new CP16Time2a(msg, startIndex);
            startIndex += 2;

            /* parse CP56Time2a (time stamp) */
            timestamp = new CP56Time2a(msg, startIndex);
        }

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);

            frame.SetNextByte(spe.EncodedValue);

            frame.SetNextByte(qdp.EncodedValue);

            frame.AppendBytes(elapsedTime.GetEncodedValue());

            frame.AppendBytes(timestamp.GetEncodedValue());
        }
    }
}

