//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 应用层参数（ASDU 寻址尺寸配置）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

namespace IEC60870.Core;

/// <summary>
/// IEC 60870-5 应用层参数（ASDU 寻址尺寸等）。
/// 对应标准中的参数 a/b/c：公共地址 CA、传送原因 COT、信息体地址 IOA 的字节长度，
/// 以及 ASDU 最大长度。CS101 与 CS104 使用不同的默认组合，可通过 <see cref="Clone"/> 派生配置。
/// </summary>
public class ApplicationLayerParameters
{
    /// <summary>IEC 60870-5-104 规定的 ASDU 最大长度（字节）。</summary>
    public static int IEC60870_5_104_MAX_ASDU_LENGTH = 249;

    private int _sizeOfCOT = 2;
    private int _originatorAddress = 0;
    private int _sizeOfCA = 2;
    private int _sizeOfIOA = 3;
    private int _maxAsduLength = IEC60870_5_104_MAX_ASDU_LENGTH;

    /// <summary>
    /// 使用 IEC 60870-5-104 默认参数（CA=2、COT=2、IOA=3、最大 ASDU 长度 249）构造实例。
    /// </summary>
    public ApplicationLayerParameters()
    {
    }

    /// <summary>
    /// 创建当前参数对象的独立副本（深拷贝所有尺寸字段）。
    /// </summary>
    public ApplicationLayerParameters Clone()
    {
        return new ApplicationLayerParameters
        {
            _sizeOfCOT = _sizeOfCOT,
            _originatorAddress = _originatorAddress,
            _sizeOfCA = _sizeOfCA,
            _sizeOfIOA = _sizeOfIOA,
            _maxAsduLength = _maxAsduLength
        };
    }

    /// <summary>
    /// 获取或设置传送原因 COT 的字节长度（参数 b，取值 1 或 2；为 2 时包含发起者地址 OA）。
    /// </summary>
    public int SizeOfCOT
    {
        get => _sizeOfCOT;
        set => _sizeOfCOT = value;
    }

    /// <summary>
    /// 获取或设置默认发起者地址 OA（Originator Address）。
    /// 仅在 <see cref="SizeOfCOT"/> 为 2 时参与编码。
    /// </summary>
    public int OA
    {
        get => _originatorAddress;
        set => _originatorAddress = value;
    }

    /// <summary>
    /// 获取或设置公共地址 CA 的字节长度（参数 a，取值 1 或 2）。
    /// </summary>
    public int SizeOfCA
    {
        get => _sizeOfCA;
        set => _sizeOfCA = value;
    }

    /// <summary>
    /// 获取或设置信息体地址 IOA 的字节长度（参数 c，取值 1、2 或 3）。
    /// </summary>
    public int SizeOfIOA
    {
        get => _sizeOfIOA;
        set => _sizeOfIOA = value;
    }

    /// <summary>类型标识 TI 的字节长度（固定为 1）。</summary>
    public int SizeOfTypeId => 1;

    /// <summary>结构限定符 VSQ 的字节长度（固定为 1）。</summary>
    public int SizeOfVSQ => 1;

    /// <summary>
    /// 获取或设置 ASDU 的最大长度（字节），用于编码时的剩余空间计算。
    /// </summary>
    public int MaxAsduLength
    {
        get => _maxAsduLength;
        set => _maxAsduLength = value;
    }
}