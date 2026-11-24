// 通用分类数据（TI=21 读 / TI=10 数据）的从站应答。
//
// 规约 3.7.1~3.7.8。信息体结构：
//   请求 TI=21：RII(1) + NOG(1) + [GIN(2) KOD(1)] × NOG
//   应答 TI=10：RII(1) + NGD(1) + [GIN(2) KOD(1) GDD(3) GID(n)] × NGD
//
// INF 说明：规约正文明文只定义了 INF=0（FUN=254 通用分类），但许继工具实际还会发
// 私有 INF=240/241/244/245 读、INF=249/250/251 写。实测这些帧的信息体结构与
// INF=0 完全相同，只是 INF 字节不同，因此按 INF 原样回显即可（不按 INF 分支解析）。
//
// 从站只在内存里维护一张 (组号 → 组标题 + 条目表)，真实装置换成自己的参数区。

using IEC60870.CS103;

namespace cs103_private_ioa.Xuji;

/// <summary>通用分类从站：应答读目录、读组描述、读条目值、写条目。</summary>
public sealed class GenericDevice
{
    /// <summary>INF=240：读所有被定义的组的标题（私有）。</summary>
    public const byte ReadAllTitles = 240;

    /// <summary>INF=241：读一个组的全部条目（私有）。</summary>
    public const byte ReadGroupEntries = 241;

    /// <summary>INF=244：读条目的属性（私有）。</summary>
    public const byte ReadEntryAttribute = 244;

    /// <summary>INF=245：读所有组的标题（私有，与 240 同义）。</summary>
    public const byte ReadAllTitlesAlt = 245;

    /// <summary>INF=249：带确认的写条目（预发/返校）。</summary>
    public const byte WriteWithAck = 249;

    /// <summary>INF=250：带执行的写条目。</summary>
    public const byte WriteExecute = 250;

    /// <summary>INF=251：带中止的写条目。</summary>
    public const byte WriteAbort = 251;

    private readonly Iec103Server _server;

    /// <summary>CA → 该装置的组表（组号 → 组标题 + 条目）。</summary>
    private readonly Dictionary<int, Dictionary<byte, GenericGroup>> _devices = new();

    /// <summary>CA → 预发但未执行的写（INF=249 暂存，等待 INF=250 执行 / 251 中止）。</summary>
    private readonly Dictionary<int, (Gin Gin, byte Kod, byte[] Gid)> _pending = new();

    /// <summary>构造通用分类从站。</summary>
    /// <param name="server">已构建的 103 从站。</param>
    public GenericDevice(Iec103Server server)
    {
        _server = server;
    }

    /// <summary>登记某装置的一个通用分类组及其条目。</summary>
    /// <param name="ca">公共地址（装置地址 &lt;&lt; 8 | 扇区）。</param>
    public void AddGroup(int ca, byte group, string title, IReadOnlyList<GenericEntry> entries)
    {
        if (!_devices.TryGetValue(ca, out var groups))
        {
            groups = new Dictionary<byte, GenericGroup>();
            _devices[ca] = groups;
        }

        groups[group] = new GenericGroup(title, entries);
    }

    /// <summary>取某 CA 的组表；未登记时返回空表。</summary>
    private Dictionary<byte, GenericGroup> GroupsOf(int ca)
        => _devices.TryGetValue(ca, out var g) ? g : Empty;

    private static readonly Dictionary<byte, GenericGroup> Empty = new();

    /// <summary>挂接到从站的 ASDU 回调上。</summary>
    public void Attach() => _server.AsduReceived += OnAsdu;

    private void OnAsdu(IIec103Session session, in Iec103AsduView asdu)
    {
        // 通用分类固定 FUN=254；TI=21 是读命令，TI=10 是数据（主站写）
        if (asdu.Fun != Asdu.GenericFun)
        {
            return;
        }

        if (asdu.TypeId == TypeIds.GenericCommand)
        {
            HandleRead(session, in asdu);
        }
        else if (asdu.TypeId == TypeIds.GenericData)
        {
            HandleWrite(session, in asdu);
        }
    }

    // ── TI=21 读命令 ────────────────────────────────────────────────

    private void HandleRead(IIec103Session session, in Iec103AsduView asdu)
    {
        var data = asdu.Data;
        if (data.Length < 2)
        {
            return;
        }

        // 应答原样回显请求的 CA 与 INF（INF 有标准 0 和私有 240/241/244/245）
        var ca = asdu.Ca;
        var inf = asdu.Inf;

        var rii = data[0];
        var nog = data[1];

        // NOG=0 或 GIN=0000 → 读全目录，只回各组标题（3.7.1）
        if (nog == 0)
        {
            ReplyAllTitles(session, ca, inf, rii);
            return;
        }

        if (data.Length < 2 + nog * 3)
        {
            return;
        }

        var sets = new List<GenericDataSet>();
        for (var i = 0; i < nog; i++)
        {
            var offset = 2 + i * 3;
            var gin = new Gin(data[offset], data[offset + 1]);
            var kod = data[offset + 2];

            if (gin.Group == 0 && gin.Entry == 0)
            {
                ReplyAllTitles(session, ca, inf, rii);
                return;
            }

            AppendGroupEntries(sets, ca, gin, kod);
        }

        if (sets.Count == 0)
        {
            // 组未定义 → COT=43 读响应（无效）
            session.SendAsduAsync(BuildData(ca, inf, rii, continuation: false, CotCodes.GenericReadInvalid,
                Array.Empty<GenericDataSet>()));
            return;
        }

        SendSets(session, ca, inf, rii, sets);
    }

    /// <summary>3.7.1 读全目录：每个已定义的组回一条 GIN(组,0) KOD=描述。</summary>
    private void ReplyAllTitles(IIec103Session session, int ca, byte inf, byte rii)
    {
        var sets = new List<GenericDataSet>();
        foreach (var (group, info) in GroupsOf(ca).OrderBy(g => g.Key))
        {
            sets.Add(GenericDataSet.Ascii(new Gin(group, 0), KodCodes.Description, info.Title));
        }

        SendSets(session, ca, inf, rii, sets);
    }

    /// <summary>把某个组（或单条）的条目追加到待发集合。</summary>
    private void AppendGroupEntries(List<GenericDataSet> sets, int ca, Gin gin, byte kod)
    {
        if (!GroupsOf(ca).TryGetValue(gin.Group, out var info))
        {
            return;
        }

        // 条目号为 0 = 整组；否则定位单条
        if (gin.Entry == 0)
        {
            if (kod == KodCodes.Description)
            {
                // 组标题 + 各条目名称（3.7.2 读描述）
                sets.Add(GenericDataSet.Ascii(new Gin(gin.Group, 0), KodCodes.Description, info.Title));
                foreach (var entry in info.Entries)
                {
                    sets.Add(GenericDataSet.Ascii(new Gin(gin.Group, entry.Entry),
                        KodCodes.Description, entry.Name));
                }

                return;
            }

            foreach (var entry in info.Entries)
            {
                sets.Add(BuildSet(gin.Group, entry, kod));
            }

            return;
        }

        var target = info.Entries.FirstOrDefault(e => e.Entry == gin.Entry);
        if (target is not null)
        {
            sets.Add(BuildSet(gin.Group, target, kod));
        }
    }

    private static GenericDataSet BuildSet(byte group, GenericEntry entry, byte kod)
        => kod == KodCodes.Description
            ? GenericDataSet.Ascii(new Gin(group, entry.Entry), KodCodes.Description, entry.Name)
            : new GenericDataSet(new Gin(group, entry.Entry), KodCodes.ActualValue,
                new Gdd(entry.DataType, entry.Width, 1, false), entry.Gid);

    // ── TI=10 数据（主站写）──────────────────────────────────────────

    /// <summary>
    /// 写条目三步（规约 3.7.6~3.7.8）：
    /// INF=249 预发（暂存 + 回显），INF=250 执行（落盘 + 确认），INF=251 中止（丢弃）。
    /// 无 INF（=0）时按单步写处理，直接落盘并确认。
    /// </summary>
    private void HandleWrite(IIec103Session session, in Iec103AsduView asdu)
    {
        var data = asdu.Data;
        if (data.Length < 2)
        {
            return;
        }

        var ca = asdu.Ca;
        var inf = asdu.Inf;
        var rii = data[0];

        // 中止：不带数据集，直接清暂存并回确认
        if (inf == WriteAbort)
        {
            _pending.Remove(ca);
            session.SendAsduAsync(GenericAck(ca, inf, rii, Gin.Zero, KodCodes.ActualValue,
                GrcCodes.Acknowledge, negative: false));
            return;
        }

        var (count, _) = NgdCodes.Decode(data[1]);
        var offset = 2;
        var lastGin = Gin.Zero;
        var lastKod = KodCodes.ActualValue;
        byte[]? lastGid = null;

        for (var i = 0; i < count; i++)
        {
            if (!GenericDataSet.TryDecode(data[offset..], out var set, out var consumed) || set is null)
            {
                return;
            }

            lastGin = set.Gin;
            lastKod = set.Kod;
            lastGid = set.Gid;
            offset += consumed;
        }

        // INF=249 预发：暂存，等 250 执行；不落盘
        if (inf == WriteWithAck)
        {
            _pending[ca] = (lastGin, lastKod, lastGid ?? Array.Empty<byte>());
            session.SendAsduAsync(GenericAck(ca, inf, rii, lastGin, lastKod,
                GrcCodes.Acknowledge, negative: false));
            return;
        }

        // INF=250 执行：把暂存值落盘；无暂存时按单步写落盘当前帧
        var hasPending = _pending.TryGetValue(ca, out var pending);
        var targetGin = hasPending ? pending.Gin : lastGin;
        var targetKod = hasPending ? pending.Kod : lastKod;
        var targetGid = hasPending ? pending.Gid : (lastGid ?? Array.Empty<byte>());

        var ok = ApplyWrite(ca, targetGin, targetKod, targetGid);
        _pending.Remove(ca);

        session.SendAsduAsync(GenericAck(ca, inf, rii, targetGin, targetKod,
            GrcCodes.Acknowledge, negative: !ok));
    }

    /// <summary>写入落地。</summary>
    private bool ApplyWrite(int ca, Gin gin, byte kod, byte[] gid)
    {
        if (kod != KodCodes.ActualValue || !GroupsOf(ca).TryGetValue(gin.Group, out var info))
        {
            return false;
        }

        var index = info.Entries.FindIndex(e => e.Entry == gin.Entry);
        if (index < 0)
        {
            return false;
        }

        info.Entries[index] = info.Entries[index] with { Gid = gid };
        return true;
    }

    /// <summary>构造写确认（TI=10，COT=40/41，带 GRC 回复码，INF 原样回显）。</summary>
    private static Asdu GenericAck(
        int ca, byte inf, byte rii, Gin gin, byte kod, byte grc, bool negative)
    {
        var set = GenericDataSet.ReplyCode(gin, kod, grc);
        var data = new byte[2 + set.EncodedSize];
        data[0] = rii;
        data[1] = NgdCodes.Encode(1, false);
        set.Encode().CopyTo(data, 2);

        return new Asdu(
            TypeIds.GenericData,
            Sq: true,
            Count: 1,
            Cot: negative ? CotCodes.GenericWriteNegative : CotCodes.GenericWrite,
            Negative: false,
            Ca: ca,
            Fun: Asdu.GenericFun,
            Inf: inf,
            Data: data);
    }

    /// <summary>构造 TI=10 数据帧（信息体 = RII + NGD + 数据集序列），INF 原样写入。</summary>
    private static Asdu BuildData(
        int ca, byte inf, byte rii, bool continuation, byte cot, IReadOnlyList<GenericDataSet> sets)
    {
        var total = 2;
        foreach (var set in sets)
        {
            total += set.EncodedSize;
        }

        var data = new byte[total];
        data[0] = rii;
        data[1] = NgdCodes.Encode(sets.Count, continuation);
        var offset = 2;
        foreach (var set in sets)
        {
            set.Encode().CopyTo(data, offset);
            offset += set.EncodedSize;
        }

        return new Asdu(
            TypeIds.GenericData,
            Sq: true,
            Count: 1,
            Cot: cot,
            Negative: false,
            Ca: ca,
            Fun: Asdu.GenericFun,
            Inf: inf,
            Data: data);
    }

    // ── 分帧发送 ────────────────────────────────────────────────────

    /// <summary>按 ASDU 上限分帧发送；非末帧置 NGD 后续标志。</summary>
    private void SendSets(IIec103Session session, int ca, byte inf, byte rii, List<GenericDataSet> sets)
    {
        if (sets.Count == 0)
        {
            session.SendAsduAsync(BuildData(ca, inf, rii, continuation: false, CotCodes.GenericRead,
                Array.Empty<GenericDataSet>()));
            return;
        }

        const int asduLimit = 253 - Asdu.HeaderLength - 2;

        var offset = 0;
        while (offset < sets.Count)
        {
            var chunk = new List<GenericDataSet>();
            var used = 0;
            while (offset < sets.Count && used + sets[offset].EncodedSize <= asduLimit)
            {
                used += sets[offset].EncodedSize;
                chunk.Add(sets[offset]);
                offset++;
            }

            // 单条超长也要发出去，否则死循环
            if (chunk.Count == 0)
            {
                chunk.Add(sets[offset]);
                offset++;
            }

            var more = offset < sets.Count;
            session.SendAsduAsync(BuildData(ca, inf, rii, more, CotCodes.GenericRead, chunk));
        }
    }
}

/// <summary>一个通用分类组。</summary>
/// <param name="Title">组标题（KOD=描述时回送）。</param>
/// <param name="Entries">条目表。</param>
public sealed record GenericGroup(string Title, List<GenericEntry> Entries)
{
    /// <summary>以只读列表构造。</summary>
    public GenericGroup(string title, IReadOnlyList<GenericEntry> entries) : this(title, [.. entries])
    {
    }
}

/// <summary>通用分类条目。</summary>
/// <param name="Entry">条目号（组内序号，从 1 开始）。</param>
/// <param name="Name">条目名称（KOD=描述时回送）。</param>
/// <param name="DataType">GDD 数据类型（<see cref="GddTypes"/>）。</param>
/// <param name="Width">数据宽度（字节）。</param>
/// <param name="Gid">实际值（KOD=实际值）。</param>
public sealed record GenericEntry(byte Entry, string Name, byte DataType, byte Width, byte[] Gid);
