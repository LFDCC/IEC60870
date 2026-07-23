/*
 *  Copyright 2026 LFDCC
 *
 *  Licensed under the MIT License. See the LICENSE file for details.
 */

/*
 *  DebugLogger.cs
 *
 *  Debug-logging delegate shared by the CS101 client/server and the file-transfer
 *  services. Carried over from the original single-assembly library, where it was
 *  declared on the master/slave base classes.
 */

namespace IEC60870.CS101
{
    /// <summary>Debug-logging callback (message only).</summary>
    public delegate void DebugLogger(string message);
}
