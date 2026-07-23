/*
 *  InformationObject.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 *
 *  See COPYING file for the complete license text.
 */

using System.Collections.Generic;



namespace IEC60870.Core.InformationObjects
{
    public abstract class InformationObject
    {
        private int objectAddress;

        public override string ToString()
        {
            string ret;

            ret = "ObjectAddress: " + ObjectAddress;

            ret += " Type: " + Type.ToString();


            return ret;
        }

        internal static int ParseInformationObjectAddress(ApplicationLayerParameters parameters, byte[] msg, int startIndex)
        {
            if (msg.Length - startIndex < parameters.SizeOfIOA)
                throw new ASDUParsingException("Message to short");

            int ioa = msg[startIndex];

            if (parameters.SizeOfIOA > 1)
                ioa += (msg[startIndex + 1] * 0x100);

            if (parameters.SizeOfIOA > 2)
                ioa += (msg[startIndex + 2] * 0x10000);

            return ioa;
        }

        protected InformationObject(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence)
        {
            if (!isSequence)
                objectAddress = ParseInformationObjectAddress(parameters, msg, startIndex);
        }

        public InformationObject(int objectAddress)
        {
            this.objectAddress = objectAddress;
        }

        /// <summary>
        /// Gets the encoded payload size of the object (information object size without the IOA)
        /// </summary>
        /// <returns>The encoded size in bytes</returns>
        public abstract int GetEncodedSize();

        public int ObjectAddress
        {
            get
            {
                return objectAddress;
            }
            internal set
            {
                objectAddress = value;
            }
        }

        /// <summary>
        /// Indicates if this information object type supports sequence of information objects encoding
        /// </summary>
        /// <value><c>true</c> if supports sequence encoding; otherwise, <c>false</c>.</value>
        public abstract bool SupportsSequence
        {
            get;
        }

        /// <summary>
        /// The type ID (message type) of the information object type
        /// </summary>
        /// <value>The type.</value>
        public abstract TypeID Type
        {
            get;
        }

        public virtual void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
        {
            if (!isSequence)
            {
                frame.SetNextByte((byte)(objectAddress & 0xff));

                if (parameters.SizeOfIOA > 1)
                    frame.SetNextByte((byte)((objectAddress / 0x100) & 0xff));

                if (parameters.SizeOfIOA > 2)
                    frame.SetNextByte((byte)((objectAddress / 0x10000) & 0xff));
            }
        }

    }

    public interface IPrivateIOFactory
    {
        /// <summary>
        /// Decode the information object and create a new InformationObject instance
        /// </summary>
        /// <param name="parameters">Application layer parameters required for decoding</param>
        /// <param name="msg">the received message</param>
        /// <param name="startIndex">start index of the payload in the message</param>
        /// <param name="isSequence">If set to <c>true</c> is sequence.</param>
        InformationObject Decode(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence);

        /// <summary>
        /// Gets the encoded payload size of the object (information object size without the IOA)
        /// </summary>
        /// <returns>The encoded size in bytes</returns>
        int GetEncodedSize();
    }

    /// <summary>
    /// Hold a list of private information object (IO) types to be used for parsing
    /// </summary>
    public class PrivateInformationObjectTypes
    {

        private Dictionary<TypeID, IPrivateIOFactory> privateTypes = new Dictionary<TypeID, IPrivateIOFactory>();

        public void AddPrivateInformationObjectType(TypeID typeId, IPrivateIOFactory iot)
        {
            privateTypes.Add(typeId, iot);
        }

        internal IPrivateIOFactory GetFactory(TypeID typeId)
        {
            IPrivateIOFactory factory = null;

            privateTypes.TryGetValue(typeId, out factory);

            return factory;
        }
    }

}

