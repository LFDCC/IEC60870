//------------------------------------------------------------------------------
//  IEC60870.Core.NET — IEC 60870-5 类型标识（Type Identification）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

namespace IEC60870.Core;

/// <summary>
/// IEC 60870-5-101/104 类型标识（Type Identification, TI）。
/// ASDU 首字节，指示信息体容器的报文格式；数值由 IEC 60870-5 标准分配。
/// 命名惯例：M_* 监视方向、C_* 控制方向、P_* 参数、F_* 文件传输；
/// 后缀 _NA 无时标、_TA 带 CP24Time2a、_TB 带 CP56Time2a。
/// </summary>
public enum TypeID
{
    // ── 监视方向：无时标（1~21）─────────────────────────────────
    /// <summary>单点信息 [1]</summary>
    M_SP_NA_1 = 1,
    /// <summary>带 CP24Time2a 时标的单点信息 [2]</summary>
    M_SP_TA_1 = 2,
    /// <summary>双点信息 [3]</summary>
    M_DP_NA_1 = 3,
    /// <summary>带 CP24Time2a 时标的双点信息 [4]</summary>
    M_DP_TA_1 = 4,
    /// <summary>步位置信息 [5]</summary>
    M_ST_NA_1 = 5,
    /// <summary>带 CP24Time2a 时标的步位置信息 [6]</summary>
    M_ST_TA_1 = 6,
    /// <summary>32 位串信息 [7]</summary>
    M_BO_NA_1 = 7,
    /// <summary>带 CP24Time2a 时标的 32 位串信息 [8]</summary>
    M_BO_TA_1 = 8,
    /// <summary>归一化测量值 [9]</summary>
    M_ME_NA_1 = 9,
    /// <summary>带 CP24Time2a 时标的归一化测量值 [10]</summary>
    M_ME_TA_1 = 10,
    /// <summary>标度化测量值 [11]</summary>
    M_ME_NB_1 = 11,
    /// <summary>带 CP24Time2a 时标的标度化测量值 [12]</summary>
    M_ME_TB_1 = 12,
    /// <summary>短浮点测量值 [13]</summary>
    M_ME_NC_1 = 13,
    /// <summary>带 CP24Time2a 时标的短浮点测量值 [14]</summary>
    M_ME_TC_1 = 14,
    /// <summary>累计量 [15]</summary>
    M_IT_NA_1 = 15,
    /// <summary>带 CP24Time2a 时标的累计量 [16]</summary>
    M_IT_TA_1 = 16,
    /// <summary>带 CP24Time2a 时标的保护设备事件 [17]</summary>
    M_EP_TA_1 = 17,
    /// <summary>带 CP56Time2a 时标的保护设备事件（继电器输出电路）[18]</summary>
    M_EP_TB_1 = 18,
    /// <summary>带 CP24Time2a 时标的继电器输出电路事件 [19]</summary>
    M_EP_TC_1 = 19,
    /// <summary>带状态变位检测的成组单点信息 [20]</summary>
    M_PS_NA_1 = 20,
    /// <summary>无质量描述的归一化测量值 [21]</summary>
    M_ME_ND_1 = 21,

    // ── 监视方向：带 CP56Time2a 时标（30~40）────────────────────
    /// <summary>带 CP56Time2a 时标的单点信息 [30]</summary>
    M_SP_TB_1 = 30,
    /// <summary>带 CP56Time2a 时标的双点信息 [31]</summary>
    M_DP_TB_1 = 31,
    /// <summary>带 CP56Time2a 时标的步位置信息 [32]</summary>
    M_ST_TB_1 = 32,
    /// <summary>带 CP56Time2a 时标的 32 位串信息 [33]</summary>
    M_BO_TB_1 = 33,
    /// <summary>带 CP56Time2a 时标的归一化测量值 [34]</summary>
    M_ME_TD_1 = 34,
    /// <summary>带 CP56Time2a 时标的标度化测量值 [35]</summary>
    M_ME_TE_1 = 35,
    /// <summary>带 CP56Time2a 时标的短浮点测量值 [36]</summary>
    M_ME_TF_1 = 36,
    /// <summary>带 CP56Time2a 时标的累计量 [37]</summary>
    M_IT_TB_1 = 37,
    /// <summary>带 CP56Time2a 时标的保护设备事件 [38]</summary>
    M_EP_TD_1 = 38,
    /// <summary>带 CP56Time2a 时标的继电器输出电路事件 [39]</summary>
    M_EP_TE_1 = 39,
    /// <summary>带 CP56Time2a 时标的成组单点（带变位检测）[40]</summary>
    M_EP_TF_1 = 40,

    // ── 控制方向：无时标（45~51）────────────────────────────────
    /// <summary>单点命令 [45]</summary>
    C_SC_NA_1 = 45,
    /// <summary>双点命令 [46]</summary>
    C_DC_NA_1 = 46,
    /// <summary>步调节命令 [47]</summary>
    C_RC_NA_1 = 47,
    /// <summary>设定值命令（归一化值）[48]</summary>
    C_SE_NA_1 = 48,
    /// <summary>设定值命令（标度化值）[49]</summary>
    C_SE_NB_1 = 49,
    /// <summary>设定值命令（短浮点值）[50]</summary>
    C_SE_NC_1 = 50,
    /// <summary>32 位串命令 [51]</summary>
    C_BO_NA_1 = 51,

    // ── 控制方向：带 CP24Time2a 时标（58~64）────────────────────
    /// <summary>带 CP24Time2a 时标的单点命令 [58]</summary>
    C_SC_TA_1 = 58,
    /// <summary>带 CP24Time2a 时标的双点命令 [59]</summary>
    C_DC_TA_1 = 59,
    /// <summary>带 CP24Time2a 时标的步调节命令 [60]</summary>
    C_RC_TA_1 = 60,
    /// <summary>带 CP24Time2a 时标的设定值命令（归一化值）[61]</summary>
    C_SE_TA_1 = 61,
    /// <summary>带 CP24Time2a 时标的设定值命令（标度化值）[62]</summary>
    C_SE_TB_1 = 62,
    /// <summary>带 CP24Time2a 时标的设定值命令（短浮点值）[63]</summary>
    C_SE_TC_1 = 63,
    /// <summary>带 CP24Time2a 时标的 32 位串命令 [64]</summary>
    C_BO_TA_1 = 64,

    // ── 系统信息（70）──────────────────────────────────────────
    /// <summary>初始化结束 [70]</summary>
    M_EI_NA_1 = 70,

    // ── 系统命令（100~107）─────────────────────────────────────
    /// <summary>总召唤命令 [100]</summary>
    C_IC_NA_1 = 100,
    /// <summary>累计量召唤命令 [101]</summary>
    C_CI_NA_1 = 101,
    /// <summary>读命令 [102]</summary>
    C_RD_NA_1 = 102,
    /// <summary>时钟同步命令 [103]</summary>
    C_CS_NA_1 = 103,
    /// <summary>测试命令 [104]</summary>
    C_TS_NA_1 = 104,
    /// <summary>复位进程命令 [105]</summary>
    C_RP_NA_1 = 105,
    /// <summary>延迟采集命令 [106]</summary>
    C_CD_NA_1 = 106,
    /// <summary>带 CP56Time2a 时标的测试命令 [107]</summary>
    C_TS_TA_1 = 107,

    // ── 参数类型（110~113）─────────────────────────────────────
    /// <summary>归一化测量值参数 [110]</summary>
    P_ME_NA_1 = 110,
    /// <summary>标度化测量值参数 [111]</summary>
    P_ME_NB_1 = 111,
    /// <summary>短浮点测量值参数 [112]</summary>
    P_ME_NC_1 = 112,
    /// <summary>参数激活 [113]</summary>
    P_AC_NA_1 = 113,

    // ── 文件传输类型（120~127）─────────────────────────────────
    /// <summary>文件就绪 [120]</summary>
    F_FR_NA_1 = 120,
    /// <summary>节就绪 [121]</summary>
    F_SR_NA_1 = 121,
    /// <summary>召唤文件节 [122]</summary>
    F_SC_NA_1 = 122,
    /// <summary>最后节（带名称标识）[123]</summary>
    F_LS_NA_1 = 123,
    /// <summary>节确认 [124]</summary>
    F_AF_NA_1 = 124,
    /// <summary>段 [125]</summary>
    F_SG_NA_1 = 125,
    /// <summary>带 CP56Time2a 时标的目录 [126]</summary>
    F_DR_TA_1 = 126,
    /// <summary>带校验和的召唤文件节 [127]</summary>
    F_SC_NB_1 = 127
}
