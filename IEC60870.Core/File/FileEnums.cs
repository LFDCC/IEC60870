//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 文件传输限定符枚举（NOF / SCQ / LSQ / AFQ / ERR）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

namespace IEC60870.Core;

/// <summary>
/// 文件名（NOF）：标识被传输文件的类别。取值按 IEC 60870-5-101 文件传输附录排列。
/// </summary>
public enum NameOfFile : ushort
{
    /// <summary>未指定</summary>
    DEFAULT,
    /// <summary>透明文件（无内部结构）</summary>
    TRANSPARENT_FILE,
    /// <summary>故障录波数据</summary>
    DISTURBANCE_DATA,
    /// <summary>事件顺序记录（SOE）</summary>
    SEQUENCES_OF_EVENTS,
    /// <summary>模拟量顺序记录</summary>
    SEQUENCES_OF_ANALOGUE_VALUES
}

/// <summary>
/// 选择与召唤限定词（SCQ）：用于 F_SC_NA_1，指明对文件或段执行的操作。
/// </summary>
public enum SelectAndCallQualifier : byte
{
    /// <summary>未使用</summary>
    DEFAULT,
    /// <summary>选择文件</summary>
    SELECT_FILE,
    /// <summary>请求（召唤）已选文件</summary>
    REQUEST_FILE,
    /// <summary>取消选择文件</summary>
    DEACTIVATE_FILE,
    /// <summary>删除文件</summary>
    DELETE_FILE,
    /// <summary>选择段</summary>
    SELECT_SECTION,
    /// <summary>请求（召唤）已选段</summary>
    REQUEST_SECTION,
    /// <summary>取消选择段</summary>
    DEACTIVATE_SECTION
}

/// <summary>
/// 末段/末节限定词（LSQ）：用于 F_LS_NA_1，指明传输是否以停止激活（DEACT）收尾。
/// </summary>
public enum LastSectionOrSegmentQualifier : byte
{
    /// <summary>未使用</summary>
    NOT_USED,
    /// <summary>文件传输结束，不发停止激活</summary>
    FILE_TRANSFER_WITHOUT_DEACT,
    /// <summary>文件传输结束，随后停止激活</summary>
    FILE_TRANSFER_WITH_DEACT,
    /// <summary>段传输结束，不发停止激活</summary>
    SECTION_TRANSFER_WITHOUT_DEACT,
    /// <summary>段传输结束，随后停止激活</summary>
    SECTION_TRANSFER_WITH_DEACT
}

/// <summary>
/// 确认限定词（AFQ）：用于 F_AF_NA_1，对文件或段作肯定/否定应答。
/// </summary>
public enum AcknowledgeQualifier
{
    /// <summary>未使用</summary>
    NOT_USED = 0,
    /// <summary>肯定确认：文件</summary>
    POS_ACK_FILE = 1,
    /// <summary>否定确认：文件</summary>
    NEG_ACK_FILE = 2,
    /// <summary>肯定确认：段</summary>
    POS_ACK_SECTION = 3,
    /// <summary>否定确认：段</summary>
    NEG_ACK_SECTION = 4
}

/// <summary>
/// 文件传输差错原因（ERR）：出现在就绪类与应答类消息中，0 表示无差错。
/// </summary>
public enum FileError
{
    /// <summary>无差错</summary>
    DEFAULT = 0,
    /// <summary>请求的内存不可用</summary>
    REQ_MEMORY_NOT_AVAILABLE = 1,
    /// <summary>校验和错误</summary>
    CHECKSUM_FAILED = 2,
    /// <summary>通信服务不符合协议</summary>
    UNEXPECTED_COMM_SERVICE = 3,
    /// <summary>文件名与请求不符</summary>
    UNEXPECTED_NAME_OF_FILE = 4,
    /// <summary>段名与请求不符</summary>
    UNEXPECTED_NAME_OF_SECTION = 5
}
