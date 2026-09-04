//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

/*
 *  CS101 文件服务公共契约：错误码、收/发两侧接口与回调委托
 *  （自 FileServices.cs 拆分）。
 */

using System;

namespace IEC60870.CS101;

    public enum FileErrorCode
    {
        SUCCESS,
        TIMEOUT,
        FILE_NOT_READY,
        SECTION_NOT_READY,
        UNKNOWN_CA,
        UNKNOWN_IOA,
        UNKNOWN_SERVICE,
        PROTOCOL_ERROR,
        ABORTED_BY_REMOTE
    }

    public interface IFileReceiver
    {
        void Finished(FileErrorCode result);

        void SegmentReceived(byte sectionName, int offset, int size, byte[] data);
    }

    public interface IFileProvider
    {

        /// <summary>
        /// Returns the CA (Comman address) of the file
        /// </summary>
        /// <returns>The CA</returns>
        int GetCA();

        /// <summary>
        /// Returns the IOA (information object address of the file)
        /// </summary>
        /// <returns>The IOA</returns>
        int GetIOA();

        /// <summary>
        /// Gets the type ("name") of the file
        /// </summary>
        /// <returns>The file type</returns>
        NameOfFile GetNameOfFile();

        DateTime GetFileDate();

        /// <summary>
        /// Gets the size of the file in bytes
        /// </summary>
        /// <returns>The file size in bytes</returns>
        int GetFileSize();

        /// <summary>
        /// Gets the size of a section in byzes
        /// </summary>
        /// <returns>The section size in bytes or -1 if the section does not exist</returns>
        /// <param name="sectionNumber">Number of section (starting with 0)</param>
        int GetSectionSize(int sectionNumber);

        /// <summary>
        /// Gets the segment data.
        /// </summary>
        /// <returns><c>true</c>, if segment data was gotten, <c>false</c> otherwise.</returns>
        /// <param name="sectionNumber">Section number.</param>
        /// <param name="offset">Offset.</param>
        /// <param name="segmentSize">Segment size.</param>
        /// <param name="segmentData">Segment data.</param>
        bool GetSegmentData(int sectionNumber, int offset, int segmentSize, byte[] segmentData);

        /// <summary>
        /// Indicates that the transfer is complete. When success equals true the file data can be deleted
        /// </summary>
        /// <param name="success">If set to <c>true</c> success.</param>
        void TransferComplete(bool success);
    }

    /// <summary>
    /// File ready handler. Will be called by the FileServer when a master sends a FILE READY (file download announcement) message to the slave.
    /// </summary>
    public delegate IFileReceiver FileReadyHandler(object parameter, int ca, int ioa, NameOfFile nof, int lengthOfFile);
