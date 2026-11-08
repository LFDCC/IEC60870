//------------------------------------------------------------------------------
//  版权（除特别声明外）归 LFDCC 所有
//  源代码使用协议遵循本仓库的开源协议（MIT License）
//------------------------------------------------------------------------------

namespace IEC60870.CS103;

/// <summary>
/// 厂商私有类型处理器契约（103 侧）。
/// </summary>
/// <remarks>
/// <para>
/// <b>职责边界</b>：库负责「标准 TypeID 怎么解」和「这个 TI / FUN+INF 我认识」；
/// 私有类型的<b>解析归业务代码</b>——103 没有 IOA，私有类型布局完全是厂商的，
/// 用 <c>switch (asdu.TypeId)</c> 直接分派比绕一层注册表更直白。
/// 本契约只承担两件事：把 TI / FUN / INF 登记进注册表（未登记才会上报未知），
/// 以及把厂商对象编码成信息体字节供 <c>SendPrivateAsync</c> 使用。
/// </para>
/// <para>
/// 契约要点：
/// <list type="bullet">
///   <item>103 无 IOA 寻址，用 <b>FUN/INF 两级寻址</b>；处理器可用 <see cref="Fun"/> / <see cref="Inf"/>
///         声明自己绑定的寻址键。</item>
///   <item>私有类型常同时改 FUN/INF 含义，同一 TI 在不同 FUN/INF 下可以是不同语义。</item>
///   <item><see cref="Encode"/> 与业务侧的解码函数配对，保证收发对称。</item>
/// </list>
/// </para>
/// </remarks>
public interface IIec103PrivateTypeHandler
{
    /// <summary>类型标识（TI）。仅作元数据，寻址匹配以注册时提供的键为准。</summary>
    byte TypeId { get; }

    /// <summary>厂商标识（如 <c>"Xuji"</c> / <c>"NARI"</c> / <c>"SAC"</c>），用于诊断与日志。</summary>
    string VendorTag { get; }

    /// <summary>本处理器绑定的 FUN；<c>null</c> 表示不限定（仅按 TI 匹配）。</summary>
    byte? Fun { get; }

    /// <summary>本处理器绑定的 INF；<c>null</c> 表示不限定。</summary>
    byte? Inf { get; }

    /// <summary>
    /// 编码厂商自定义对象为 ASDU 数据区字节（FUN/INF 之后的部分）。
    /// </summary>
    /// <param name="value">厂商对象。</param>
    /// <returns>信息体字节。</returns>
    byte[] Encode(Iec103DecodedAsdu value);
}

/// <summary>
/// 通用分类数据处理器契约（GIN / KOD / GDD 轴）。
/// </summary>
/// <remarks>
/// 103 的厂商私有扩展大量落在通用分类：私有 GIN 段承载定值/软压板，
/// 私有 KOD 扩展语义，私有 GDD 数据类型改变 GID 的解释。
/// 三者任一未命中都会保留原始 GID 字节并通过 <see cref="Iec103GenericUnknownEntry"/> 上报。
/// </remarks>
public interface IIec103GenericDataHandler
{
    /// <summary>厂商标识。</summary>
    string VendorTag { get; }

    /// <summary>描述该数据集的语义文本。</summary>
    string Describe(in Gin gin, byte kod, in Gdd gdd, ReadOnlySpan<byte> gid);

    /// <summary>解码 GID 为厂商对象。</summary>
    object Decode(in Gin gin, byte kod, in Gdd gdd, ReadOnlySpan<byte> gid);
}

/// <summary>未识别的通用分类条目（保留原始 GID，绝不丢信息）。</summary>
public sealed class Iec103GenericUnknownEntry
{
    /// <summary>构造未识别通用分类条目。</summary>
    public Iec103GenericUnknownEntry(in Gin gin, byte kod, in Gdd gdd, byte[] gid, string? reason = null)
    {
        Gin = gin;
        Kod = kod;
        Gdd = gdd;
        Gid = gid ?? Array.Empty<byte>();
        Reason = reason;
    }

    /// <summary>通用分类标识号。</summary>
    public Gin Gin { get; }

    /// <summary>描述类型。</summary>
    public byte Kod { get; }

    /// <summary>数据描述。</summary>
    public Gdd Gdd { get; }

    /// <summary>原始 GID 字节。</summary>
    public byte[] Gid { get; }

    /// <summary>未识别原因。</summary>
    public string? Reason { get; }

    /// <inheritdoc />
    public override string ToString()
        => $"GIN={Gin} KOD={Kod}({KodCodes.Describe(Kod)}) GDD={GddTypes.Describe(Gdd.DataType)}"
            + $" 宽度={Gdd.DataWidth} 数量={Gdd.Number} GID={Convert.ToHexString(Gid)}"
            + (string.IsNullOrEmpty(Reason) ? string.Empty : $" 原因={Reason}");
}

/// <summary>
/// IEC 60870-5-103 私有类型注册表（5 条扩展轴）。
/// </summary>
/// <remarks>
/// <para><b>为什么需要 5 条轴</b>：
/// 103 作为「保护设备信息接口配套标准」，标准本身就把 FUN(160-255)/INF、
/// 通用分类 GIN/KOD/GDD 划为厂商专用范围，现场装置的私有规约因此是常态而非例外。</para>
/// <para><b>分派顺序</b>（越具体越优先）：
/// <list type="number">
///   <item>FUN+INF 精确匹配</item>
///   <item>FUN 区间匹配</item>
///   <item>FUN+INF+TI 匹配（注册时同时给了 TI 与 FUN/INF）</item>
///   <item>TI 匹配</item>
///   <item>标准 TI 内置处理</item>
///   <item>兜底：<see cref="Iec103UnknownAsdu"/>（不丢帧）</item>
/// </list>
/// </para>
/// <para><b>线程安全</b>：读路径无锁（256 槽 <c>Volatile.Read</c> 直址数组 + 不可变字典快照），
/// 写路径写时复制。</para>
/// <para><b>实例隔离</b>：每个 Client/Server 各持一份（默认预置标准表），
/// 不同厂商装置可挂不同注册表互不影响。</para>
/// </remarks>
public sealed class Iec103PrivateTypeRegistry
{
    private const int TypeSlotCount = 256;
    private const int FunSlotCount = 256;

    private static readonly Iec103PrivateTypeRegistry _default = new();

    // 轴 1：TI → 处理器（写时复制快照）
    private IIec103PrivateTypeHandler?[] _byType = new IIec103PrivateTypeHandler?[TypeSlotCount];

    // 轴 2：FUN → (INF → 处理器) 与 FUN → 区间处理器（写时复制整体替换）
    private Dictionary<(byte Fun, byte Inf), IIec103PrivateTypeHandler> _byFunInf = new();
    private List<(byte From, byte To, IIec103PrivateTypeHandler Handler)> _byFunRange = new();

    // 轴 3：通用分类 GIN / KOD / GDD 类型（写时复制）
    private Dictionary<(ushort Group, byte Entry), IIec103GenericDataHandler> _byGin = new();
    private List<(ushort From, ushort To, IIec103GenericDataHandler Handler)> _byGinRange = new();
    private Dictionary<byte, string> _kodNames = new();
    private Dictionary<byte, string> _gddTypeNames = new();

    // 轴 4：COT 描述（仅语义，不参与分派）
    private Dictionary<byte, string> _cotNames = new();

    /// <summary>全局默认注册表（预置标准 COT/KOD/GDD 名称，无私有处理器）。</summary>
    public static Iec103PrivateTypeRegistry Default => _default;

    /// <summary>创建空的私有注册表实例。</summary>
    public Iec103PrivateTypeRegistry()
    {
    }

    /// <summary>已登记的私有 TI 数量（诊断用）。</summary>
    public int RegisteredTypeCount
    {
        get
        {
            var count = 0;
            var snapshot = Volatile.Read(ref _byType);
            for (var i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i] is not null)
                {
                    count++;
                }
            }

            return count;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  轴 1：TypeID
    // ═══════════════════════════════════════════════════════════════

    /// <summary>按 TI 登记私有处理器（同一 TI 重复登记按覆盖处理）。</summary>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> 为 null。</exception>
    public void RegisterType(IIec103PrivateTypeHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var snapshot = Volatile.Read(ref _byType);
        var updated = (IIec103PrivateTypeHandler?[])snapshot.Clone();
        updated[handler.TypeId] = handler;
        Volatile.Write(ref _byType, updated);
    }

    /// <summary>按显式 TI 登记私有处理器（<paramref name="typeId"/> 与处理器的 <c>TypeId</c> 可不同）。</summary>
    public void RegisterType(byte typeId, IIec103PrivateTypeHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var snapshot = Volatile.Read(ref _byType);
        var updated = (IIec103PrivateTypeHandler?[])snapshot.Clone();
        updated[typeId] = handler;
        Volatile.Write(ref _byType, updated);
    }

    /// <summary>注销某 TI 的私有处理器；本就未登记返回 false。</summary>
    public bool UnregisterType(byte typeId)
    {
        var snapshot = Volatile.Read(ref _byType);
        if (snapshot[typeId] is null)
        {
            return false;
        }

        var updated = (IIec103PrivateTypeHandler?[])snapshot.Clone();
        updated[typeId] = null;
        Volatile.Write(ref _byType, updated);
        return true;
    }

    /// <summary>按 TI 查询私有处理器。</summary>
    public bool TryGetType(byte typeId, out IIec103PrivateTypeHandler? handler)
    {
        handler = Volatile.Read(ref _byType)[typeId];
        return handler is not null;
    }

    // ═══════════════════════════════════════════════════════════════
    //  轴 2：FUN / INF（103 私有扩展的主战场）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>按 FUN+INF 精确登记私有处理器（优先级最高）。</summary>
    public void RegisterFunInf(byte fun, byte inf, IIec103PrivateTypeHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var current = Volatile.Read(ref _byFunInf);
        var updated = new Dictionary<(byte, byte), IIec103PrivateTypeHandler>(current)
        {
            [(fun, inf)] = handler,
        };
        Volatile.Write(ref _byFunInf, updated);
    }

    /// <summary>按 FUN 区间登记私有处理器（闭区间，如专用范围 160-255）。</summary>
    /// <param name="funFrom">区间下界（含）。</param>
    /// <param name="funTo">区间上界（含）。</param>
    /// <param name="handler">处理器。</param>
    public void RegisterFunRange(byte funFrom, byte funTo, IIec103PrivateTypeHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (funFrom > funTo)
        {
            (funFrom, funTo) = (funTo, funFrom);
        }

        var current = Volatile.Read(ref _byFunRange);
        var updated = new List<(byte, byte, IIec103PrivateTypeHandler)>(current)
        {
            (funFrom, funTo, handler),
        };
        Volatile.Write(ref _byFunRange, updated);
    }

    /// <summary>注销 FUN+INF 精确登记。</summary>
    public bool UnregisterFunInf(byte fun, byte inf)
    {
        var current = Volatile.Read(ref _byFunInf);
        if (!current.ContainsKey((fun, inf)))
        {
            return false;
        }

        var updated = new Dictionary<(byte, byte), IIec103PrivateTypeHandler>(current);
        updated.Remove((fun, inf));
        Volatile.Write(ref _byFunInf, updated);
        return true;
    }

    /// <summary>
    /// 按完整头部解析私有处理器（分派顺序：FUN+INF → FUN 区间 → TI）。
    /// </summary>
    /// <param name="typeId">类型标识。</param>
    /// <param name="fun">功能类型。</param>
    /// <param name="inf">信息序号。</param>
    /// <param name="cot">传送原因（预留给需要按 COT 细分的处理器）。</param>
    /// <param name="handler">命中的处理器。</param>
    public bool TryResolve(byte typeId, byte fun, byte inf, byte cot, out IIec103PrivateTypeHandler? handler)
    {
        _ = cot;

        if (Volatile.Read(ref _byFunInf).TryGetValue((fun, inf), out handler))
        {
            return true;
        }

        var ranges = Volatile.Read(ref _byFunRange);
        for (var i = ranges.Count - 1; i >= 0; i--)
        {
            var (from, to, candidate) = ranges[i];
            if (fun >= from && fun <= to)
            {
                handler = candidate;
                return true;
            }
        }

        handler = Volatile.Read(ref _byType)[typeId];
        return handler is not null;
    }

    // ═══════════════════════════════════════════════════════════════
    //  轴 3：通用分类 GIN / KOD / GDD
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 判定通用分类条目是否「三个轴都在标准范围内」：GIN 已登记（或为 0000 全目录）、
    /// KOD 与 GDD 数据类型落在标准值域内。
    /// </summary>
    /// <remarks>
    /// 主站接收侧与从站接收侧共用同一判定，避免两边各写一份而逐渐走偏
    /// （任一侧漏掉某个轴，私有条目就会被静默吞掉，与「未登记必须上报」的目标矛盾）。
    /// </remarks>
    /// <param name="set">已解码的通用分类数据集。</param>
    public bool IsStandardEntry(GenericDataSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        var ginKnown = (set.Gin.Group == 0 && set.Gin.Entry == 0)
            || TryResolveGin(set.Gin, out _);

        return ginKnown
            && set.Kod <= KodCodes.RelatedEntries
            && set.Gdd.DataType is >= GddTypes.None and <= GddTypes.Index;
    }

    /// <summary>按 GIN（组+条目）登记通用分类处理器。</summary>
    public void RegisterGin(byte group, byte entry, IIec103GenericDataHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        var current = Volatile.Read(ref _byGin);
        var updated = new Dictionary<(ushort, byte), IIec103GenericDataHandler>(current)
        {
            [((ushort)group, entry)] = handler,
        };
        Volatile.Write(ref _byGin, updated);
    }

    /// <summary>按 GIN 组区间登记通用分类处理器（闭区间）。</summary>
    public void RegisterGinRange(ushort groupFrom, ushort groupTo, IIec103GenericDataHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (groupFrom > groupTo)
        {
            (groupFrom, groupTo) = (groupTo, groupFrom);
        }

        var current = Volatile.Read(ref _byGinRange);
        var updated = new List<(ushort, ushort, IIec103GenericDataHandler)>(current)
        {
            (groupFrom, groupTo, handler),
        };
        Volatile.Write(ref _byGinRange, updated);
    }

    /// <summary>登记私有 KOD 语义名称（仅影响描述，不改变分派）。</summary>
    public void RegisterKod(byte kod, string name)
    {
        var current = Volatile.Read(ref _kodNames);
        var updated = new Dictionary<byte, string>(current)
        {
            [kod] = name ?? $"KOD {kod}",
        };
        Volatile.Write(ref _kodNames, updated);
    }

    /// <summary>登记私有 GDD 数据类型名称（仅影响描述）。</summary>
    public void RegisterGddDataType(byte dataType, string name)
    {
        var current = Volatile.Read(ref _gddTypeNames);
        var updated = new Dictionary<byte, string>(current)
        {
            [dataType] = name ?? $"类型 {dataType}",
        };
        Volatile.Write(ref _gddTypeNames, updated);
    }

    /// <summary>按 GIN 解析通用分类处理器（精确优先，其次组区间）。</summary>
    public bool TryResolveGin(in Gin gin, out IIec103GenericDataHandler? handler)
    {
        if (Volatile.Read(ref _byGin).TryGetValue(((ushort)gin.Group, gin.Entry), out handler))
        {
            return true;
        }

        var ranges = Volatile.Read(ref _byGinRange);
        for (var i = ranges.Count - 1; i >= 0; i--)
        {
            var (from, to, candidate) = ranges[i];
            if (gin.Group >= from && gin.Group <= to)
            {
                handler = candidate;
                return true;
            }
        }

        handler = null;
        return false;
    }

    /// <summary>KOD 描述（私有名称优先，回退标准表）。</summary>
    public string DescribeKod(byte kod)
        => Volatile.Read(ref _kodNames).TryGetValue(kod, out var name) ? name : KodCodes.Describe(kod);

    /// <summary>GDD 数据类型描述（私有名称优先，回退标准表）。</summary>
    public string DescribeGddDataType(byte dataType)
        => Volatile.Read(ref _gddTypeNames).TryGetValue(dataType, out var name) ? name : GddTypes.Describe(dataType);

    // ═══════════════════════════════════════════════════════════════
    //  轴 4：COT 语义
    // ═══════════════════════════════════════════════════════════════

    /// <summary>登记私有 COT 语义名称。</summary>
    public void RegisterCot(byte cot, string name)
    {
        var current = Volatile.Read(ref _cotNames);
        var updated = new Dictionary<byte, string>(current)
        {
            [cot] = name ?? $"COT {cot}",
        };
        Volatile.Write(ref _cotNames, updated);
    }

    /// <summary>COT 描述（私有名称优先，回退标准表；自动带 P/N 标注）。</summary>
    public string DescribeCot(byte cot)
    {
        var name = Volatile.Read(ref _cotNames).TryGetValue(CotCodes.Value(cot), out var custom)
            ? custom
            : CotCodes.Describe(cot);
        return name;
    }

    // ═══════════════════════════════════════════════════════════════
    //  轴 5：TI 语义
    // ═══════════════════════════════════════════════════════════════

    /// <summary>TI 描述（有私有处理器时用其厂商标识，回退标准表）。</summary>
    public string DescribeType(byte typeId)
    {
        var handler = Volatile.Read(ref _byType)[typeId];
        return handler is null
            ? TypeIds.Describe(typeId)
            : $"{TypeIds.Describe(typeId)} [{handler.VendorTag}]";
    }

    /// <summary>便利方法：一次性登记一个厂商的全部私有 FUN/INF 键。</summary>
    /// <param name="handler">处理器。</param>
    /// <param name="keys">(FUN, INF) 组合。</param>
    public void RegisterFunInfMany(IIec103PrivateTypeHandler handler, params (byte Fun, byte Inf)[] keys)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (keys is null || keys.Length == 0)
        {
            return;
        }

        var current = Volatile.Read(ref _byFunInf);
        var updated = new Dictionary<(byte, byte), IIec103PrivateTypeHandler>(current);
        foreach (var (fun, inf) in keys)
        {
            updated[(fun, inf)] = handler;
        }

        Volatile.Write(ref _byFunInf, updated);
    }
}
