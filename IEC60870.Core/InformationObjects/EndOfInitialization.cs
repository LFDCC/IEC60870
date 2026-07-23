

/*
 *  EndOfInitialization.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 *
 *  See COPYING file for the complete license text.
 */

namespace IEC60870.Core.InformationObjects
{
    /// <summary>
    /// End of initialization information object (M_EI_NA_1)
    /// </summary>
    public class EndOfInitialization : InformationObject
    {
        private byte coi;

        /// <summary>
        /// Cause of Initialization (COI)
        /// </summary>
        public byte COI
        {
            get
            {
                return coi;
            }
            set
            {
                coi = value;
            }
        }

        override public int GetEncodedSize()
        {
            return 1;
        }

        override public TypeID Type
        {
            get
            {
                return TypeID.M_EI_NA_1;
            }
        }

        override public bool SupportsSequence
        {
            get
            {
                return false;
            }
        }

        public EndOfInitialization(byte coi)
            : base(0)
        {
            this.coi = coi;
        }

        public EndOfInitialization(EndOfInitialization original)
            : base(original.ObjectAddress)
        {
            coi = original.coi;
        }

        internal EndOfInitialization(ApplicationLayerParameters parameters, byte[] msg, int startIndex)
            : base(parameters, msg, startIndex, false)
        {
            startIndex += parameters.SizeOfIOA; /* skip IOA */

            if ((msg.Length - startIndex) < GetEncodedSize())
                throw new ASDUParsingException("Message too small");

            coi = msg[startIndex];
        }

        public override void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            base.Encode(frame, parameters, isSequence);

            frame.SetNextByte(coi);
        }
    }

}
