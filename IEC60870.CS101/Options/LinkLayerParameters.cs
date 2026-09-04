//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS101;

/// <summary>
/// IEC 60870-5-101 链路层（FT1.2）参数：地址域长度、确认/重发超时与单字符 ACK 开关。
/// </summary>
/// <remarks>
/// <b>配置时机：</b>请在传给 <see cref="Iec101Client"/> / <see cref="Iec101Server"/> 构造前完成全部赋值，
/// 构造后<b>不要</b>再修改本对象（链路层状态机直接持有引用实时读取）。
/// </remarks>
public class LinkLayerParameters
{
    /* 0/1/2 bytes address length */
    private int _addressLength = 1;

    /* timeout for ACKs in ms */
    private int _timeoutForACK = 1000;

    /* timeout for repeating messages when no ACK received in ms */
    private long _timeoutRepeat = 1000;

    /* use single char ACK for ACK (FC=0) or RESP_NO_USER_DATA (FC=9) */
    private bool _useSingleCharACK = true;

    /* interval to repeat request status of link (FC=9) after response timeout */
    private int _timeoutLinkState;

    /// <summary>
    /// Gets or sets the length of the link layer address field
    /// </summary>
    /// <para>The value can be either 0, 1, or 2 for balanced mode or 0, or 1 for unbalanced mode</para>
    /// <value>The length of the address in byte</value>
    public int AddressLength
    {
        get
        {
            return _addressLength;
        }
        set
        {
            _addressLength = value;
        }
    }

    /// <summary>
    /// Gets or sets the timeout for message ACK
    /// </summary>
    /// <value>The timeout to wait for message ACK in ms</value>
    public int TimeoutForACK
    {
        get
        {
            return _timeoutForACK;
        }
        set
        {
            _timeoutForACK = value;
        }
    }

    /// <summary>
    /// Gets or sets the timeout for message repetition in case of missing ACK messages
    /// </summary>
    /// <value>The timeout for message repetition in ms</value>
    public long TimeoutRepeat
    {
        get
        {
            return _timeoutRepeat;
        }
        set
        {
            _timeoutRepeat = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the secondary link layer uses single character ACK instead of FC 0 or FC 9
    /// </summary>
    /// <value><c>true</c> if use single char ACK; otherwise, <c>false</c>.</value>
    public bool UseSingleCharACK
    {
        get
        {
            return _useSingleCharACK;
        }
        set
        {
            _useSingleCharACK = value;
        }
    }

    /// <summary>
    /// Gets or sets the interval to repeat request status of link (FC=9) after response timeout
    /// </summary>
    /// <value>the timeout value in ms</value>
    public int TimeoutLinkState
    {
        get
        {
            return _timeoutLinkState;
        }
        set
        {
            _timeoutLinkState = value;
        }
    }
}
