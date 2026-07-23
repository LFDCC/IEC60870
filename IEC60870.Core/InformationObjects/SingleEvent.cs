

/*
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 *
 *  See COPYING file for the complete license text.
 */

using IEC60870.Core.Quality;
namespace IEC60870.Core.InformationObjects
{

    public enum EventState
    {
        INDETERMINATE_0 = 0,
        OFF = 1,
        ON = 2,
        INDETERMINATE_3 = 3
    }


    public class SingleEvent
    {
        private QualityDescriptorP qdp;

        private EventState eventState;

        public SingleEvent()
        {
            eventState = EventState.INDETERMINATE_0;
            qdp = new QualityDescriptorP();
        }

        public SingleEvent(SingleEvent orignal)
        {
            eventState = orignal.eventState;
            qdp = new QualityDescriptorP(orignal.qdp);
        }

        public override bool Equals(object obj)
        {
            if (obj == null)
                return false;

            if (!(obj is SingleEvent))
                return false;

            return (EncodedValue == ((SingleEvent)obj).EncodedValue);
        }

        public override int GetHashCode()
        {
            return EncodedValue.GetHashCode();
        }

        public SingleEvent(byte encodedValue)
        {
            eventState = (EventState)(encodedValue & 0x03);

            qdp = new QualityDescriptorP(encodedValue);
        }

        public EventState State
        {
            get
            {
                return eventState;
            }
            set
            {
                eventState = value;
            }
        }

        public QualityDescriptorP QDP
        {
            get
            {
                return qdp;
            }
            set
            {
                qdp = value;
            }
        }

        public byte EncodedValue
        {
            get
            {
                byte encodedValue = (byte)((qdp.EncodedValue & 0xfc) + (int)eventState);

                return encodedValue;
            }
        }

    }
}

