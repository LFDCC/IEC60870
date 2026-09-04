//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace IEC60870.CS101;

    /// <summary>
    /// Simple implementation of IFileProvider that can be used to provide transparent files. Derived classed should override the
    /// TransferComplete method.
    /// </summary>
    public class TransparentFile : IFileProvider
    {
        private List<byte[]> _sections = new List<byte[]>();

        private DateTime _time = DateTime.MinValue;

        private int _ca;
        private int _ioa;
        private NameOfFile _nof;

        public TransparentFile(int ca, int ioa, NameOfFile nof)
        {
            _ca = ca;
            _ioa = ioa;
            _nof = nof;
            _time = DateTime.Now;
        }

        public void AddSection(byte[] section)
        {
            _sections.Add(section);
        }

        public int GetCA()
        {
            return _ca;
        }

        public int GetIOA()
        {
            return _ioa;
        }

        public NameOfFile GetNameOfFile()
        {
            return _nof;
        }

        public DateTime GetFileDate()
        {
            return _time;
        }

        public int GetFileSize()
        {
            var fileSize = 0;

            foreach (var section in _sections)
        {
            fileSize += section.Length;
        }

        return fileSize;
        }

        public int GetSectionSize(int sectionNumber)
        {
            if (sectionNumber < _sections.Count)
        {
            return _sections[sectionNumber].Length;
        }
        else
        {
            return -1;
        }
    }

        public bool GetSegmentData(int sectionNumber, int offset, int segmentSize, byte[] segmentData)
        {
            if ((sectionNumber >= _sections.Count) || (sectionNumber < 0))
        {
            return false;
        }

        var section = _sections[sectionNumber];

            if (offset + segmentSize > section.Length)
        {
            return false;
        }

        for (var i = 0; i < segmentSize; i++)
        {
            segmentData[i] = section[i + offset];
        }

        return true;
        }

        public virtual void TransferComplete(bool success)
        {
        }
    }
