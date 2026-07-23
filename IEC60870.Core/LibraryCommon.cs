

/*
 *  Copyright 2016-2025 LFDCC
 *
 *  This file is part of IEC60870.Core.NET
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 *
 *  See COPYING file for the complete license text.
 */

namespace IEC60870.Core
{

    /// <summary>
    /// Common information functions about the library
    /// </summary>
    public class LibraryCommon
    {
        /// <summary>
        /// Library major version number
        /// </summary>
        public const int VERSION_MAJOR = 2;

        /// <summary>
        /// Library minor version number
        /// </summary>
        public const int VERSION_MINOR = 3;

        /// <summary>
        /// Library patch number
        /// </summary>
        public const int VERSION_PATCH = 0;

        /// <summary>
        /// Gets the library version as string {major}.{minor}.{patch}.
        /// </summary>
        /// <returns>The library version as string.</returns>
        public static string GetLibraryVersionString()
        {
            return "" + VERSION_MAJOR + "." + VERSION_MINOR + "." + VERSION_PATCH;
        }
    }

    /// <summary>
    /// Raw message handler. Can be used to access the raw message.
    /// Returns true when message should be handled by the protocol stack, false, otherwise.
    /// </summary>
    public delegate bool RawMessageHandler(object parameter, byte[] message, int messageSize);
}

