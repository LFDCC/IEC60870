//------------------------------------------------------------------------------
//  IEC60870.Core.NET — IEC 60870-5 传送原因（Cause of Transmission）
//  
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

namespace IEC60870.Core;

/// <summary>
/// 传送原因（Cause of Transmission, COT）枚举。
/// 编码位置：ASDU 首字节后的第 2/3 字节，低 6 位为原因值，
/// bit7 = T（测试标志），bit6 = P/N（否定确认标志）。
/// 值定义遵循 IEC 60870-5-101/104 标准。
/// </summary>
public enum CauseOfTransmission
{
    /// <summary>周期 / 循环传送 [1]</summary>
    PERIODIC = 1,
    /// <summary>后台扫描 [2]</summary>
    BACKGROUND_SCAN = 2,
    /// <summary>突发 / 自发传送 [3]</summary>
    SPONTANEOUS = 3,
    /// <summary>初始化 [4]</summary>
    INITIALIZED = 4,
    /// <summary>请求或被请求 [5]</summary>
    REQUEST = 5,
    /// <summary>激活 [6]</summary>
    ACTIVATION = 6,
    /// <summary>激活确认 [7]</summary>
    ACTIVATION_CON = 7,
    /// <summary>停止激活 [8]</summary>
    DEACTIVATION = 8,
    /// <summary>停止激活确认 [9]</summary>
    DEACTIVATION_CON = 9,
    /// <summary>激活终止 [10]</summary>
    ACTIVATION_TERMINATION = 10,
    /// <summary>远方命令引起的返送信息 [11]</summary>
    RETURN_INFO_REMOTE = 11,
    /// <summary>当地命令引起的返送信息 [12]</summary>
    RETURN_INFO_LOCAL = 12,
    /// <summary>文件传输 [13]</summary>
    FILE_TRANSFER = 13,
    /// <summary>认证 [14]</summary>
    AUTHENTICATION = 14,
    /// <summary>维护认证会话密钥 [15]</summary>
    MAINTENANCE_OF_AUTH_SESSION_KEY = 15,
    /// <summary>维护用户角色与更新密钥 [16]</summary>
    MAINTENANCE_OF_USER_ROLE_AND_UPDATE_KEY = 16,

    /// <summary>总召唤响应 [20]</summary>
    INTERROGATED_BY_STATION = 20,
    /// <summary>第 1 组召唤响应 [21]</summary>
    INTERROGATED_BY_GROUP_1 = 21,
    /// <summary>第 2 组召唤响应 [22]</summary>
    INTERROGATED_BY_GROUP_2 = 22,
    /// <summary>第 3 组召唤响应 [23]</summary>
    INTERROGATED_BY_GROUP_3 = 23,
    /// <summary>第 4 组召唤响应 [24]</summary>
    INTERROGATED_BY_GROUP_4 = 24,
    /// <summary>第 5 组召唤响应 [25]</summary>
    INTERROGATED_BY_GROUP_5 = 25,
    /// <summary>第 6 组召唤响应 [26]</summary>
    INTERROGATED_BY_GROUP_6 = 26,
    /// <summary>第 7 组召唤响应 [27]</summary>
    INTERROGATED_BY_GROUP_7 = 27,
    /// <summary>第 8 组召唤响应 [28]</summary>
    INTERROGATED_BY_GROUP_8 = 28,
    /// <summary>第 9 组召唤响应 [29]</summary>
    INTERROGATED_BY_GROUP_9 = 29,
    /// <summary>第 10 组召唤响应 [30]</summary>
    INTERROGATED_BY_GROUP_10 = 30,
    /// <summary>第 11 组召唤响应 [31]</summary>
    INTERROGATED_BY_GROUP_11 = 31,
    /// <summary>第 12 组召唤响应 [32]</summary>
    INTERROGATED_BY_GROUP_12 = 32,
    /// <summary>第 13 组召唤响应 [33]</summary>
    INTERROGATED_BY_GROUP_13 = 33,
    /// <summary>第 14 组召唤响应 [34]</summary>
    INTERROGATED_BY_GROUP_14 = 34,
    /// <summary>第 15 组召唤响应 [35]</summary>
    INTERROGATED_BY_GROUP_15 = 35,
    /// <summary>第 16 组召唤响应 [36]</summary>
    INTERROGATED_BY_GROUP_16 = 36,

    /// <summary>总计数召唤响应 [37]</summary>
    REQUESTED_BY_GENERAL_COUNTER = 37,
    /// <summary>第 1 组计数召唤响应 [38]</summary>
    REQUESTED_BY_GROUP_1_COUNTER = 38,
    /// <summary>第 2 组计数召唤响应 [39]</summary>
    REQUESTED_BY_GROUP_2_COUNTER = 39,
    /// <summary>第 3 组计数召唤响应 [40]</summary>
    REQUESTED_BY_GROUP_3_COUNTER = 40,
    /// <summary>第 4 组计数召唤响应 [41]</summary>
    REQUESTED_BY_GROUP_4_COUNTER = 41,

    /// <summary>否定确认：未知类型标识 [44]</summary>
    UNKNOWN_TYPE_ID = 44,
    /// <summary>否定确认：未知传送原因 [45]</summary>
    UNKNOWN_CAUSE_OF_TRANSMISSION = 45,
    /// <summary>否定确认：未知公共地址 [46]</summary>
    UNKNOWN_COMMON_ADDRESS_OF_ASDU = 46,
    /// <summary>否定确认：未知信息体地址 [47]</summary>
    UNKNOWN_INFORMATION_OBJECT_ADDRESS = 47
}
