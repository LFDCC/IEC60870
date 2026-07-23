

/*
 *  Frame.cs
 *
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  IEC60870.Core.NET is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  IEC60870.Core.NET is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with IEC60870.Core.NET.  If not, see <http://www.gnu.org/licenses/>.
 *
 *  See COPYING file for the complete license text.
 */

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

        public abstract int GetMsgSize();

        public abstract byte[] GetBuffer();
    }
}
