/*
 *  AsduEncoder.cs
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
using IEC60870.Core.InformationObjects;



namespace IEC60870.Core
{
    /// <summary>
    /// Internal encoder for ASDU messages. Provides encoding and space-accounting
    /// logic extracted from <see cref="ASDU"/>. All methods are internal static
    /// and operate on an <see cref="ASDU"/> instance passed by parameter.
    /// </summary>
    internal static class AsduEncoder
    {
        /// <summary>
        /// Adds an information object to the ASDU.
        /// </summary>
        /// <returns><c>true</c> if the information object was added, <c>false</c> otherwise.</returns>
        internal static bool AddInformationObject(InformationObject io, ASDU asdu)
        {
            if (asdu.informationObjects == null)
                asdu.informationObjects = new System.Collections.Generic.List<InformationObject>();

            if (asdu.hasTypeId)
            {
                if (io.Type != asdu.typeId)
                    throw new ArgumentException("Invalid information object type: expected " + asdu.typeId.ToString() + " was " + io.Type.ToString());
            }
            else
            {
                asdu.typeId = io.Type;
                asdu.hasTypeId = true;
            }

            if (asdu.informationObjects.Count >= 0x7f)
                return false;

            int objectSize = io.GetEncodedSize();

            if (asdu.IsSequence == false)
                objectSize += asdu.parameters.SizeOfIOA;
            else
            {
                if (asdu.informationObjects.Count == 0) // is first object?
                    objectSize += asdu.parameters.SizeOfIOA;
                else
                {
                    if (io.ObjectAddress != (asdu.informationObjects[0].ObjectAddress + asdu.informationObjects.Count))
                        return false;
                }
            }

            if (objectSize <= asdu.spaceLeft)
            {
                asdu.spaceLeft -= objectSize;
                asdu.informationObjects.Add(io);

                asdu.vsq = (byte)((asdu.vsq & 0x80) | asdu.informationObjects.Count);

                return true;
            }
            else
                return false;
        }

        /// <summary>
        /// Encodes the ASDU into a frame.
        /// </summary>
        internal static void Encode(Frame frame, ApplicationLayerParameters parameters, ASDU asdu)
        {
            frame.SetNextByte((byte)asdu.typeId);
            frame.SetNextByte(asdu.vsq);

            byte cotByte = (byte)asdu.cot;

            if (asdu.isTest)
                cotByte = (byte)(cotByte | 0x80);

            if (asdu.isNegative)
                cotByte = (byte)(cotByte | 0x40);

            frame.SetNextByte(cotByte);

            if (parameters.SizeOfCOT == 2)
                frame.SetNextByte(asdu.oa);

            frame.SetNextByte((byte)(asdu.ca % 256));

            if (parameters.SizeOfCA > 1)
                frame.SetNextByte((byte)(asdu.ca / 256));

            if (asdu.payload != null)
                frame.AppendBytes(asdu.payload);
            else
            {
                bool isFirst = true;

                foreach (InformationObject io in asdu.informationObjects)
                {
                    if (isFirst)
                    {
                        io.Encode(frame, parameters, false);
                        isFirst = false;
                    }
                    else
                    {
                        if (asdu.IsSequence)
                            io.Encode(frame, parameters, true);
                        else
                            io.Encode(frame, parameters, false);
                    }
                }
            }
        }

        /// <summary>
        /// Encodes the ASDU as a byte array.
        /// </summary>
        /// <returns>
        /// The encoded byte array, or <c>null</c> if the actual encoded size
        /// does not match the expected buffer size.
        /// </returns>
        internal static byte[] AsByteArray(ASDU asdu)
        {
            int expectedSize = asdu.parameters.MaxAsduLength - asdu.spaceLeft;

            BufferFrame frame = new BufferFrame(new byte[expectedSize], 0);

            Encode(frame, asdu.parameters, asdu);

            if (frame.GetMsgSize() == expectedSize)
                return frame.GetBuffer();
            else
                return null;
        }
    }
}