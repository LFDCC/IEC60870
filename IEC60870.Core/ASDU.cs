/*
 *  ASDU.cs
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
using System.Collections.Generic;
using IEC60870.Core.InformationObjects;
using IEC60870.Core.File;



namespace IEC60870.Core
{

    /// <summary>
    /// This class represents an application layer message. It contains some generic message information and
    /// one or more InformationObject instances of the same type. It is used to send and receive messages.
    /// </summary>
    public class ASDU
    {
        /* ---- internal fields (accessed by AsduEncoder / AsduDecoder in same assembly) ---- */
        internal ApplicationLayerParameters parameters;

        internal TypeID typeId;
        internal bool hasTypeId;

        /* variable structure qualifier */
        internal byte vsq;

        internal CauseOfTransmission cot;

        /* originator address */
        internal byte oa;

        /* is message a test message */
        internal bool isTest;

        /* is message a negative confirmation */
        internal bool isNegative;

        /* Common address of ASDU */
        internal int ca;

        internal int spaceLeft = 0;

        internal byte[] payload = null;
        internal List<InformationObject> informationObjects = null;

        internal PrivateInformationObjectTypes privateObjectTypes = null;

        /// <summary>
        /// Initializes a new instance of the <see cref="IEC60870.Core.ASDU"/> class.
        /// </summary>
        /// <param name="parameters">application layer parameters to be used for encoding/decoding</param>
        /// <param name="cot">Cause of transmission (COT)</param>
        /// <param name="isTest">If set to <c>true</c> ASDU is a test ASDU.</param>
        /// <param name="isNegative">If set to <c>true</c> is negative confirmation.</param>
        /// <param name="oa">originator address (OA)</param>
        /// <param name="ca">common address of the ASDU (CA)</param>
        /// <param name="isSequence">If set to <c>true</c> is a sequence of information objects.</param>
        public ASDU(ApplicationLayerParameters parameters, CauseOfTransmission cot, bool isTest, bool isNegative, byte oa, int ca, bool isSequence)
            : this(parameters, TypeID.M_SP_NA_1, cot, isTest, isNegative, oa, ca, isSequence)
        {
            hasTypeId = false;
        }

        internal ASDU(ApplicationLayerParameters parameters, TypeID typeId, CauseOfTransmission cot, bool isTest, bool isNegative, byte oa, int ca, bool isSequence)
        {
            this.parameters = parameters;
            this.typeId = typeId;
            this.cot = cot;
            this.isTest = isTest;
            this.isNegative = isNegative;
            this.oa = oa;
            this.ca = ca;
            spaceLeft = parameters.MaxAsduLength -
            parameters.SizeOfTypeId - parameters.SizeOfVSQ - parameters.SizeOfCA - parameters.SizeOfCOT;

            if (isSequence)
                vsq = 0x80;
            else
                vsq = 0;

            hasTypeId = true;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="IEC60870.Core.ASDU"/> class from a byte buffer.
        /// </summary>
        public ASDU(ApplicationLayerParameters parameters, byte[] msg, int bufPos, int msgLength)
        {
            this.parameters = parameters;

            int asduHeaderSize = 2 + parameters.SizeOfCOT + parameters.SizeOfCA;

            if ((msgLength - bufPos) < asduHeaderSize)
                throw new ASDUParsingException("Message header too small");

            typeId = (TypeID)msg[bufPos++];
            vsq = msg[bufPos++];

            hasTypeId = true;

            byte cotByte = msg[bufPos++];

            if ((cotByte & 0x80) != 0)
                isTest = true;
            else
                isTest = false;

            if ((cotByte & 0x40) != 0)
                isNegative = true;
            else
                isNegative = false;

            cot = (CauseOfTransmission)(cotByte & 0x3f);

            if (parameters.SizeOfCOT == 2)
                oa = msg[bufPos++];

            ca = msg[bufPos++];

            if (parameters.SizeOfCA > 1)
                ca += (msg[bufPos++] * 0x100);

            int payloadSize = msgLength - bufPos;

            // 校验 payload 长度是否足以容纳 VSQ 声明的信息对象数（代码评审 #15 / 原 TODO）。
            // 短 payload + 过大 VSQ 会让 GetElement(index) 算出越界偏移，导致部分类型 IndexOutOfRange。
            int expected = AsduDecoder.ComputeExpectedPayloadSize(this);
            if (expected >= 0 && payloadSize < expected)
                throw new ASDUParsingException("Payload too small for declared VSQ/TypeID (need " + expected + ", got " + payloadSize + ")");

            payload = new byte[payloadSize];

            /* save payload */
            Buffer.BlockCopy(msg, bufPos, payload, 0, payloadSize);
        }

        /// <summary>
        /// Adds an information object to the ASDU.
        /// </summary>
        /// <returns><c>true</c>, if information object was added, <c>false</c> otherwise.</returns>
        /// <param name="io">The information object to add</param>
        public bool AddInformationObject(InformationObject io)
        {
            return AsduEncoder.AddInformationObject(io, this);
        }

        public void Encode(Frame frame, ApplicationLayerParameters parameters)
        {
            AsduEncoder.Encode(frame, parameters, this);
        }

        /// <summary>
        /// 将 ASDU 编码为字节数组。
        /// </summary>
        /// <returns>
        /// 编码后的字节；若实际编码长度与预期缓冲尺寸不符（理论上仅在 <see cref="AddInformationObject"/>
        /// 与 <see cref="Encode"/> 之间参数被改动时才可能发生），返回 <c>null</c>。
        /// 调用方必须判空（代码评审 #19）。如希望永不返回 null，可改用 <see cref="Encode"/> 配合可调长缓冲。
        /// </returns>
        public byte[] AsByteArray()
        {
            return AsduEncoder.AsByteArray(this);
        }

        /// <summary>
        /// Gets the type identifier (TI).
        /// </summary>
        /// <value>The type identifier.</value>
        public TypeID TypeId
        {
            get
            {
                return typeId;
            }
        }

        /// <summary>
        /// Gets or sets the cause of transmission (COT)
        /// </summary>
        /// <value>The COT value</value>
        public CauseOfTransmission Cot
        {
            get
            {
                return cot;
            }
            set
            {
                cot = value;
            }
        }

        /// <summary>
        /// Gets the originator address (OA)
        /// </summary>
        /// <value>The OA</value>
        public byte Oa
        {
            get
            {
                return oa;
            }
        }

        /// <summary>
        /// Gets a value indicating whether this instance is a test message.
        /// </summary>
        /// <value><c>true</c> if this instance is a test message; otherwise, <c>false</c>.</value>
        public bool IsTest
        {
            get
            {
                return isTest;
            }
        }

        /// <summary>
        /// Gets or sets a value indicating whether this instance is a negative confirmation.
        /// </summary>
        /// <value><c>true</c> if this instance is a negative confirmation; otherwise, <c>false</c>.</value>
        public bool IsNegative
        {
            get
            {
                return isNegative;
            }
            set
            {
                isNegative = value;
            }
        }

        /// <summary>
        /// Gets the common address of the ASDU (CA)
        /// </summary>
        /// <value>The CA value</value>
        public int Ca
        {
            get
            {
                return ca;
            }
        }

        /// <summary>
        /// Gets a value indicating whether this instance is a sequence of information objects
        /// </summary>
        /// A sequence of information objects contains multiple information objects with successive
        /// information object addresses (IOA).
        /// <value><c>true</c> if this instance is a sequence; otherwise, <c>false</c>.</value>
        public bool IsSequence
        {
            get
            {
                if ((vsq & 0x80) != 0)
                    return true;
                else
                    return false;
            }
        }

        /// <summary>
        /// Gets the number of elements (information objects) of the ASDU
        /// </summary>
        /// <value>The number of information objects.</value>
        public int NumberOfElements
        {
            get
            {
                return (vsq & 0x7f);
            }
        }

        public InformationObject GetElement(int index, PrivateInformationObjectTypes privateObjectTypes)
        {
            return AsduDecoder.GetElement(index, privateObjectTypes, this);
        }

        public InformationObject GetElement(int index, IPrivateIOFactory ioFactory)
        {
            return AsduDecoder.GetElement(index, ioFactory, this);
        }

        /// <summary>
        /// 类型安全版 <see cref="GetElement(int)"/>：按报文 typeId 解析后，断言实际类型为 <typeparamref name="T"/>。
        /// </summary>
        /// <typeparam name="T">期望的信息对象具体类型，须为 <see cref="InformationObject"/> 的子类</typeparam>
        /// <param name="index">元素索引（从 0 开始）</param>
        /// <returns>类型为 <typeparamref name="T"/> 的信息对象</returns>
        /// <exception cref="IEC60870.Core.ASDUParsingException">
        /// 当 index 越界、解析结果为 <c>null</c>，或解析出的实际类型不是 <typeparamref name="T"/> 时抛出
        /// </exception>
        public T GetElement<T>(int index) where T : InformationObject
        {
            return AsduDecoder.GetElement<T>(index, this);
        }

        /// <summary>
        /// Gets the element (information object) with the specified index
        /// </summary>
        /// <returns>the information object at index</returns>
        /// <param name="index">index of the element (starting with 0)</param>
        /// <exception cref="IEC60870.Core.ASDUParsingException">Thrown when there is a problem parsing the ASDU</exception>
        public InformationObject GetElement(int index)
        {
            return AsduDecoder.GetElement(index, this);
        }

        public override string ToString()
        {
            string ret;

            ret = "TypeID: " + typeId.ToString() + " COT: " + cot.ToString();

            if (parameters.SizeOfCOT == 2)
                ret += " OA: " + oa;

            if (isTest)
                ret += " [TEST]";

            if (isNegative)
                ret += " [NEG]";

            if (IsSequence)
                ret += " [SEQ]";

            ret += " elements: " + NumberOfElements;

            ret += " CA: " + ca;

            return ret;
        }
    }
}