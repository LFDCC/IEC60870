

/*
 *  Frame.cs
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

namespace IEC60870.Core
{
    /// <summary>
    /// Abstract class to encode an application layer frame
    /// </summary>
    public abstract class Frame
    {
        public abstract void ResetFrame();

        public abstract void SetNextByte(byte value);

        public abstract void AppendBytes(byte[] bytes);

        /// <summary>
        /// Appends a span of bytes to the frame. Default implementation writes
        /// byte-by-byte via <see cref="SetNextByte"/>; subclasses with a direct
        /// buffer should override with a bulk copy for better performance.
        /// </summary>
        public virtual void AppendBytes(ReadOnlySpan<byte> bytes)
        {
            foreach (byte b in bytes)
                SetNextByte(b);
        }

        public abstract int GetMsgSize();

        public abstract byte[] GetBuffer();
    }
}
