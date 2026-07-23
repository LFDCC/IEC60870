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
    [Serializable]
    public class ASDUParsingException : Exception
    {
        public ASDUParsingException(string message)
            : base(message)
        {
        }
    }
}


