

/*
 *  EventOfProtectionEquipment.cs
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
namespace IEC60870.Core.InformationObjects
{
    /// <summary>
    /// Event of protection equipment information object (M_EP_TA_1)
    /// </summary>
    public class EventOfProtectionEquipment : InformationObject
    {

        override public TypeID Type
        {
            get
            {
                return TypeID.M_EP_TA_1;
            }
        }

        override public bool SupportsSequence
        {
            get
            {
                return false;
            }
        }

        private SingleEvent singleEvent;

        public SingleEvent Event
        {
            get
            {
                return singleEvent;
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

        public EventOfProtectionEquipment(int ioa, SingleEvent singleEvent, CP16Time2a elapsedTime, CP24Time2a timestamp)
            : base(ioa)
        {
            this.singleEvent = singleEvent;
            this.elapsedTime = elapsedTime;
            this.timestamp = timestamp;
        }

        public EventOfProtectionEquipment(EventOfProtectionEquipment original)
            : base(original.ObjectAddress)
        {
            singleEvent = new SingleEvent(original.singleEvent);
            elapsedTime = new CP16Time2a(original.elapsedTime);
            timestamp = new CP24Time2a(original.timestamp);
        }

        internal EventOfProtectionEquipment(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            singleEvent = new SingleEvent(msg[startIndex++]);

            elapsedTime = new CP16Time2a(msg, startIndex);
            startIndex += 2;

            /* parse CP56Time2a (time stamp) */
            timestamp = new CP24Time2a(msg, startIndex);
        }

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);

            frame.SetNextByte(singleEvent.EncodedValue);

            frame.AppendBytes(elapsedTime.AsSpan());

            frame.AppendBytes(timestamp.AsSpan());
        }
    }

    /// <summary>
    /// Event of protection equipment information object with CP56Time2a time tag (M_EP_TD_1)
    /// </summary>
    public class EventOfProtectionEquipmentWithCP56Time2a : InformationObject
    {

        override public TypeID Type
        {
            get
            {
                return TypeID.M_EP_TD_1;
            }
        }

        override public bool SupportsSequence
        {
            get
            {
                return false;
            }
        }

        private SingleEvent singleEvent;

        public SingleEvent Event
        {
            get
            {
                return singleEvent;
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

        public EventOfProtectionEquipmentWithCP56Time2a(int ioa, SingleEvent singleEvent, CP16Time2a elapsedTime, CP56Time2a timestamp)
            : base(ioa)
        {
            this.singleEvent = singleEvent;
            this.elapsedTime = elapsedTime;
            this.timestamp = timestamp;
        }

        public EventOfProtectionEquipmentWithCP56Time2a(EventOfProtectionEquipmentWithCP56Time2a original)
            : base(original.ObjectAddress)
        {
            singleEvent = new SingleEvent(original.singleEvent);
            elapsedTime = new CP16Time2a(original.elapsedTime);
            timestamp = new CP56Time2a(original.timestamp);
        }

        internal EventOfProtectionEquipmentWithCP56Time2a(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
            : base(parameters, msg, startIndex, isSequence)
        {
            if (!isSequence)
                startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            singleEvent = new SingleEvent(msg[startIndex++]);

            elapsedTime = new CP16Time2a(msg, startIndex);
            startIndex += 2;

            /* parse CP56Time2a (time stamp) */
            timestamp = new CP56Time2a(msg, startIndex);
        }

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);

            frame.SetNextByte(singleEvent.EncodedValue);

            frame.AppendBytes(elapsedTime.AsSpan());

            frame.AppendBytes(timestamp.AsSpan());
        }
    }
}

