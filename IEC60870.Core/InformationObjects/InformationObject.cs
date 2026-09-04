//------------------------------------------------------------------------------
//  IEC60870.Core.NET — 信息对象基类与私有类型扩展契约
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

namespace IEC60870.Core;

/// <summary>
/// 所有信息对象（Information Object）的抽象基类。
/// 一个信息对象 = 信息对象地址（IOA）+ 一组信息元素；子类按 IEC 60870-5
/// 类型标识各自定义负载的编解码。
/// </summary>
/// <remarks>
/// 编解码契约：
/// <list type="bullet">
/// <item>解码：内部解析构造函数（parameters, msg, startIndex, isSequence）。</item>
/// <item>编码：<see cref="Encode"/> 写 IOA 前缀后，将剩余空间包装为
/// <see cref="AsduWriter"/> 交给 <see cref="EncodeBody"/> 直写（单一事实源）。</item>
/// </list>
/// 序列编码（SQ=1）时多个连续对象共享首对象的 IOA，负载逐个紧密排列。
/// </remarks>
public abstract class InformationObject
{
    private int _objectAddress;

    /// <summary>
    /// 从报文切片解析信息对象地址（IOA，按参数决定 1~3 字节小端宽度）。
    /// </summary>
    internal static int ParseInformationObjectAddress(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex)
    {
        var width = parameters.SizeOfIOA;
        if (msg.Length - startIndex < width)
        {
            throw new ASDUParsingException("信息对象地址不完整");
        }

        int ioa = msg[startIndex];
        if (width > 1)
        {
            ioa |= msg[startIndex + 1] << 8;
        }

        if (width > 2)
        {
            ioa |= msg[startIndex + 2] << 16;
        }

        return ioa;
    }

    /// <summary>
    /// 将信息对象地址按参数宽度写入帧（小端，1~3 字节）。
    /// </summary>
    private void WriteInformationObjectAddress(Frame frame, ApplicationLayerParameters parameters)
    {
        frame.SetNextByte((byte)_objectAddress);
        if (parameters.SizeOfIOA > 1)
        {
            frame.SetNextByte((byte)(_objectAddress >> 8));
        }

        if (parameters.SizeOfIOA > 2)
        {
            frame.SetNextByte((byte)(_objectAddress >> 16));
        }
    }

    /// <summary>
    /// 解码构造：仅在非序列（SQ=0）模式下读取 IOA；序列模式下 IOA 由 ASDU 层处理。
    /// </summary>
    protected InformationObject(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence)
    {
        if (!isSequence)
        {
            _objectAddress = ParseInformationObjectAddress(parameters, msg, startIndex);
        }
    }

    /// <summary>
    /// 编码构造：指定信息对象地址。
    /// </summary>
    public InformationObject(int objectAddress)
    {
        _objectAddress = objectAddress;
    }

    /// <summary>
    /// 编码负载尺寸（不含 IOA 的信息体字节数）。
    /// 默认从 <see cref="AsduTypeHandlerRegistry.Default"/> 类型处理器注册表派生
    /// （单一事实源）；变长类型（如 FileSegment）需 override。
    /// </summary>
    public virtual int GetEncodedSize()
    {
        if (AsduTypeHandlerRegistry.Default.TryGetSlot(Type, out var slot) && slot.PayloadSize >= 0)
        {
            return slot.PayloadSize;
        }

        return 0;
    }

    /// <summary>信息对象地址（IOA）。</summary>
    public int ObjectAddress
    {
        get => _objectAddress;
        internal set => _objectAddress = value;
    }

    /// <summary>
    /// 该类型是否支持序列编码（SQ=1，连续 IOA 的同类元素打包）。
    /// </summary>
    public abstract bool SupportsSequence { get; }

    /// <summary>对应的 IEC 60870-5 类型标识。</summary>
    public abstract TypeID Type { get; }

    /// <summary>
    /// 编码到帧：先写 IOA 前缀（SQ=0 时），再经 <see cref="AsduWriter"/> 直写负载。
    /// 用户自定义 IO 若完整 override 本方法（不调 base），则走自身逻辑不受影响。
    /// </summary>
    public virtual void Encode(Frame frame, ApplicationLayerParameters parameters, bool isSequence)
    {
        if (!isSequence)
        {
            WriteInformationObjectAddress(frame, parameters);
        }

        var w = new AsduWriter(frame.GetRemainingSpan());
        EncodeBody(ref w, parameters, isSequence);
        frame.Advance(w.Position);
    }

    /// <summary>
    /// 本类是否已迁移至 <see cref="AsduWriter"/> 直写编码（内置 IO 类均为 true）。
    /// <see cref="AsduEncoder.EncodeAsdu"/> 据此为未迁移的用户自定义 IO
    /// 选择 Frame 兼容回退路径。
    /// </summary>
    internal virtual bool HasAsduWriterBody => false;

    /// <summary>
    /// 编码信息体负载（不含 IOA 前缀），直写 <paramref name="w"/>。
    /// 子类在带载荷的中间类之上扩展时调用 <c>base.EncodeBody</c> 复用父类负载。
    /// </summary>
    protected internal virtual void EncodeBody(ref AsduWriter w, ApplicationLayerParameters parameters, bool isSequence)
    {
    }

    /// <inheritdoc/>
    public override string ToString() => $"IOA={ObjectAddress} Type={Type}";
}

/// <summary>
/// 私有信息对象类型工厂契约：为厂商自定义 TypeID 提供解码与尺寸信息。
/// </summary>
public interface IPrivateIOFactory
{
    /// <summary>解码信息对象，返回新实例。</summary>
    /// <param name="parameters">应用层参数（决定字段宽度）。</param>
    /// <param name="msg">完整接收报文。</param>
    /// <param name="startIndex">负载起始下标。</param>
    /// <param name="isSequence">是否序列（SQ=1）。</param>
    InformationObject Decode(ApplicationLayerParameters parameters, byte[] msg, int startIndex, bool isSequence);

    /// <summary>编码负载尺寸（不含 IOA 的信息体字节数）。</summary>
    int GetEncodedSize();
}

/// <summary>
/// 私有信息对象类型集合：按 TypeID 登记厂商自定义类型的解码工厂。
/// </summary>
public class PrivateInformationObjectTypes
{
    private readonly Dictionary<TypeID, IPrivateIOFactory> _factories = new();

    /// <summary>登记一个私有类型的解码工厂（重复登记同 TypeID 将抛异常）。</summary>
    public void AddPrivateInformationObjectType(TypeID typeId, IPrivateIOFactory factory)
    {
        _factories.Add(typeId, factory);
    }

    internal IPrivateIOFactory GetFactory(TypeID typeId)
    {
        _factories.TryGetValue(typeId, out var factory);
        return factory;
    }
}
