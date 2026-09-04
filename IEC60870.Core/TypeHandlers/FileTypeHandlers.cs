//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;

namespace IEC60870.Core;

    // ──────────────────────────────────────────────────────────────────────
    //  文件传输(F_*)类型处理器。文件类型按单元素、恒定偏移 0 寻址;
    //  F_SG_NA_1(FileSegment)为变长段数据,PayloadSize = -1。
    // ──────────────────────────────────────────────────────────────────────

    internal sealed class FileReadyHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.F_FR_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.File;
        public int PayloadSize => 6;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new FileReady(parameters, msg, startIndex, isSequence);
    }

    internal sealed class SectionReadyHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.F_SR_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.File;
        public int PayloadSize => 7;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new SectionReady(parameters, msg, startIndex, isSequence);
    }

    internal sealed class FileCallOrSelectHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.F_SC_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.File;
        public int PayloadSize => 4;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new FileCallOrSelect(parameters, msg, startIndex, isSequence);
    }

    internal sealed class FileLastSegmentOrSectionHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.F_LS_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.File;
        public int PayloadSize => 5;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new FileLastSegmentOrSection(parameters, msg, startIndex, isSequence);
    }

    internal sealed class FileACKHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.F_AF_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.File;
        public int PayloadSize => 4;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new FileACK(parameters, msg, startIndex, isSequence);
    }

    internal sealed class FileSegmentHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.F_SG_NA_1;
        public AsduTypeKind Kind => AsduTypeKind.File;
        /// <summary>变长段数据:构造期长度守卫与步进寻址跳过,由 FileSegment 自行处理。</summary>
        public int PayloadSize => -1;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new FileSegment(parameters, msg, startIndex, isSequence);
    }

    internal sealed class FileDirectoryHandler : IAsduTypeHandler
    {
        public TypeID TypeId => TypeID.F_DR_TA_1;
        public AsduTypeKind Kind => AsduTypeKind.File;
        public int PayloadSize => 13;
        public InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
            => new FileDirectory(parameters, msg, startIndex, isSequence);
    }
