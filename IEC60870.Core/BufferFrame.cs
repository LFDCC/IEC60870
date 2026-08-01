

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

namespace IEC60870.Core
{
    /// <summary>
    /// Implementation of Frame to encode into a given byte array
    /// </summary>
    public class BufferFrame : Frame
    {
        private byte[] buffer;
        private int startPos;
        private int bufPos;

        public BufferFrame(byte[] buffer, int startPos)
        {
            this.buffer = buffer;
            this.startPos = startPos;
            bufPos = startPos;
        }

        public BufferFrame Clone()
        {
            byte[] newBuffer = new byte[GetMsgSize()];

            int newBufPos = 0;

            for (int i = startPos; i < bufPos; i++)
            {
                newBuffer[newBufPos++] = buffer[i];
            }

            BufferFrame clone = new BufferFrame(newBuffer, 0);
            clone.bufPos = newBufPos;

            return clone;
        }

        public override void ResetFrame()
        {
            bufPos = startPos;
        }

        public override void SetNextByte(byte value)
        {
            buffer[bufPos++] = value;
        }

        public override void AppendBytes(byte[] bytes)
        {
            AppendBytes(bytes.AsSpan());
        }

        public override void AppendBytes(ReadOnlySpan<byte> bytes)
        {
            bytes.CopyTo(buffer.AsSpan(bufPos));
            bufPos += bytes.Length;
        }

        public override int GetMsgSize()
        {
            return bufPos;
        }

        public override byte[] GetBuffer()
        {
            return buffer;
        }
    }
}
