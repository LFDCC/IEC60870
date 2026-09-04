//------------------------------------------------------------------------------
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System;

namespace IEC60870.Core;

    /// <summary>
    /// ASDU 类型方向分类,决定 <see cref="AsduDecoder"/> 对信息对象区域的寻址策略。
    /// </summary>
    public enum AsduTypeKind
    {
        /// <summary>监视方向类型(M_*):支持序列寻址(首元素带 IOA 前缀,后续按 PayloadSize 步进)。</summary>
        Monitor = 0,

        /// <summary>控制方向类型(C_*/P_*):固定步进 SizeOfIOA + PayloadSize。</summary>
        Command = 1,

        /// <summary>文件传输类型(F_*):单元素、恒定偏移 0。</summary>
        File = 2,
    }

    /// <summary>
    /// ASDU 类型处理器策略接口。一个 TypeID 对应一个处理器,负责该类型信息对象的
    /// 解码构造、尺寸与方向元数据 —— 类型适配的单一事实源。
    /// </summary>
    /// <remarks>
    /// 标准类型由 <see cref="AsduTypeHandlerRegistry.Default"/> 预置;
    /// 私有/自定义类型通过 <see cref="AsduTypeHandlerRegistry.Register"/> 运行时注册,
    /// 各客户端/服务端实例可持有独立的注册表副本实现差异化扩展。
    /// <para>
    /// 职责边界:本接口只负责「解码与元数据」;编码仍由 <see cref="InformationObject"/>
    /// 继承体系自带的 <c>Encode(Frame,...)</c> 虚方法承担(数据模型即编码器)。
    /// </para>
    /// </remarks>
    public interface IAsduTypeHandler
    {
        /// <summary>此处理器支持的类型标识(TypeID)。</summary>
        TypeID TypeId { get; }

        /// <summary>类型方向(Monitor/Command/File),决定寻址策略。</summary>
        AsduTypeKind Kind { get; }

        /// <summary>
        /// 每个信息元素的固定负载字节数(不含 IOA 前缀)。
        /// 变长类型(如 F_SG_NA_1 FileSegment)返回 -1,此时构造期长度守卫与步进寻址
        /// 均跳过,由解码逻辑自行处理。
        /// </summary>
        int PayloadSize { get; }

        /// <summary>
        /// 解码单个信息对象。
        /// </summary>
        /// <param name="parameters">应用层参数(决定 IOA 宽度等)。</param>
        /// <param name="msg">ASDU 信息对象区域(零拷贝切片,仅在调用期间有效)。</param>
        /// <param name="startIndex">
        /// 该元素在 <paramref name="msg"/> 中的起始偏移:
        /// 非序列模式指向 IOA 前缀;序列模式除首元素(指向 IOA)外指向纯负载。
        /// </param>
        /// <param name="isSequence">该元素是否处于序列寻址模式(仅监视方向有效)。</param>
        /// <returns>解码出的信息对象。</returns>
        InformationObject Decode(ApplicationLayerParameters parameters, ReadOnlySpan<byte> msg, int startIndex, bool isSequence);
    }
