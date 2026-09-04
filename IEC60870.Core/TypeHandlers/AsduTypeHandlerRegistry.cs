//------------------------------------------------------------------------------
//  IEC60870.Core.NET — ASDU 类型处理器注册表（无锁直址数组 + 全局默认单例）
//
//  Licensed under the MIT License. See the LICENSE file for details.
//------------------------------------------------------------------------------

using System.Threading;

namespace IEC60870.Core;

/// <summary>
/// 单个 TypeID 的解码元数据快照：处理器实例 + 方向 + 负载尺寸。
/// 以结构体形式与处理器一同存入注册表槽位，使热路径一次数组寻址即可取齐
/// 全部所需信息，避免逐字段虚调用（TypeId/Kind/PayloadSize 各一次）。
/// </summary>
/// <param name="Handler">类型处理器；未注册时为 null。</param>
/// <param name="Kind">类型方向，决定寻址策略。</param>
/// <param name="PayloadSize">单元素负载字节数（不含 IOA）；变长类型为 -1。</param>
internal readonly record struct HandlerSlot(IAsduTypeHandler Handler, AsduTypeKind Kind, int PayloadSize);

/// <summary>
/// 维护 TypeID → <see cref="IAsduTypeHandler"/> 映射的注册表。
/// 全局共享一份预置标准类型的 <see cref="Default"/> 单例，各连接也可另建实例做差异化扩展。
/// </summary>
/// <remarks>
/// 读路径（解码/编码热路径）为无锁直址数组：TypeID 的线上格式固定 1 字节（0…255），
/// 故用 256 槽定长数组，读侧仅一次 <see cref="Volatile.Read{T}(ref T)"/> 取快照，
/// 与 switch 跳转表同为 O(1) 且无锁、无字典哈希。写路径（注册/注销）罕见，
/// 采用写时复制：改副本后整体替换数组引用，读者要么见旧快照要么见新快照，绝不撕裂。
/// <para>
/// 私有类型只要实现 <see cref="IAsduTypeHandler"/> 并调用 <see cref="Register"/>，
/// 即可与标准类型共用同一条解码/寻址管线。同一 TypeID 重复注册按覆盖处理。
/// </para>
/// </remarks>
public sealed class AsduTypeHandlerRegistry
{
    // TypeID 线上格式为 1 字节，256 槽覆盖全部合法取值。
    private const int SlotCount = 256;

    private static readonly AsduTypeHandlerRegistry _default = CreateDefault();

    // 写时复制的不可变快照；读侧 Volatile.Read，写侧 Volatile.Write 整体替换。
    private HandlerSlot[] _slots = new HandlerSlot[SlotCount];

    /// <summary>预置全部标准 IEC 60870-5 类型（M_*/C_*/P_*/F_*）处理器的全局默认注册表。</summary>
    public static AsduTypeHandlerRegistry Default => _default;

    private static AsduTypeHandlerRegistry CreateDefault()
    {
        var registry = new AsduTypeHandlerRegistry();
        StandardAsduTypeHandlers.RegisterAll(registry);
        return registry;
    }

    /// <summary>注册（或覆盖）某 TypeID 的处理器。<exception cref="ArgumentNullException">handler 为 null 时抛出。</exception></summary>
    public void Register(IAsduTypeHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var typeId = handler.TypeId;

        if ((uint)(int)typeId >= SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(handler), "TypeID 超出 0…255 范围，无法注册");
        }

        var slot = new HandlerSlot(handler, handler.Kind, handler.PayloadSize);

        // 写时复制：克隆当前快照、改一个槽、整体替换。罕见路径，一次数组拷贝可接受。
        var updated = (HandlerSlot[])Volatile.Read(ref _slots).Clone();
        updated[(int)typeId] = slot;
        Volatile.Write(ref _slots, updated);
    }

    /// <summary>注销某 TypeID；该 TypeID 本就未注册时返回 false。</summary>
    public bool Unregister(TypeID typeId)
    {
        if ((uint)(int)typeId >= SlotCount)
        {
            return false;
        }

        var current = Volatile.Read(ref _slots);

        if (current[(int)typeId].Handler == null)
        {
            return false;
        }

        var updated = (HandlerSlot[])current.Clone();
        updated[(int)typeId] = default;
        Volatile.Write(ref _slots, updated);
        return true;
    }

    /// <summary>查询 TypeID 对应的处理器，未注册返回 null。</summary>
    public IAsduTypeHandler GetHandler(TypeID typeId)
        => TryGetSlot(typeId, out var slot) ? slot.Handler : null;

    /// <summary>查询 TypeID 对应的处理器，返回是否命中。</summary>
    public bool TryGetHandler(TypeID typeId, out IAsduTypeHandler handler)
    {
        if (TryGetSlot(typeId, out var slot))
        {
            handler = slot.Handler;
            return true;
        }

        handler = null;
        return false;
    }

    /// <summary>
    /// 热路径入口：一次数组寻址取回解码元数据快照（处理器 + 方向 + 负载尺寸）。
    /// 未注册或 TypeID 越界返回 false。
    /// </summary>
    internal bool TryGetSlot(TypeID typeId, out HandlerSlot slot)
    {
        if ((uint)(int)typeId < SlotCount)
        {
            slot = Volatile.Read(ref _slots)[(int)typeId];
            return slot.Handler != null;
        }

        slot = default;
        return false;
    }
}
