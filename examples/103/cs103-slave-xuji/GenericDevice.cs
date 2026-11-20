// 通用分类数据（TI=21 读 / TI=10 数据）的从站应答。
//
// 规约 3.7.1~3.7.8。信息体结构：
//   请求 TI=21：RII(1) + NOG(1) + [GIN(2) KOD(1)] × NOG
//   应答 TI=10：RII(1) + NGD(1) + [GIN(2) KOD(1) GDD(3) GID(n)] × NGD
//
// 从站只在内存里维护一张 (组号 → 组标题 + 条目表)，真实装置换成自己的参数区。

using IEC60870.CS103;

namespace cs103_private_ioa.Xuji;

/// <summary>通用分类从站：应答读目录、读组描述、读条目值、写条目。</summary>
public sealed class GenericDevice
{
    private readonly Iec103Server _server;
    private readonly int _ca;

    /// <summary>组号 → (组标题, 条目表)。</summary>
    private readonly Dictionary<byte, GenericGroup> _groups = new();

    /// <summary>构造通用分类从站。</summary>
    /// <param name="server">已构建的 103 从站。</param>
    /// <param name="ca">公共地址（2 字节承载下含扇区与装置地址）。</param>
    public GenericDevice(Iec103Server server, int ca)
    {
        _server = server;
        _ca = ca;
    }

    /// <summary>登记一个组及其条目。</summary>
    public void AddGroup(byte group, string title, IReadOnlyList<GenericEntry> entries)
        => _groups[group] = new GenericGroup(title, entries);

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

        var rii = data[0];
        var nog = data[1];
        if (data.Length < 2 + nog * 3)
        {
            return;
        }

        // GIN 0000 视为读全目录（3.7.1），否则按请求的组逐条应答
        if (nog == 0)
        {
            ReplyAllTitles(session, rii);
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
                // 读全目录：只回各组标题（3.7.1）
                ReplyAllTitles(session, rii);
                return;
            }

            AppendGroupEntries(sets, gin, kod);
        }

        if (sets.Count == 0)
        {
            // 没有任何可用条目 → COT=43 读响应（无效）
            session.SendAsduAsync(Asdu.GenericDataAsdu(
                _ca, rii, continuation: false, CotCodes.GenericReadInvalid,
                Array.Empty<GenericDataSet>()));
            return;
        }

        SendSets(session, rii, sets);
    }

    /// <summary>3.7.1 读全目录：每个已定义的组回一条 GIN(组,0) KOD=描述。</summary>
    private void ReplyAllTitles(IIec103Session session, byte rii)
    {
        var sets = new List<GenericDataSet>();
        foreach (var (group, info) in _groups.OrderBy(g => g.Key))
        {
            sets.Add(GenericDataSet.Ascii(new Gin(group, 0), KodCodes.Description, info.Title));
        }

        SendSets(session, rii, sets);
    }

    /// <summary>把某个组（或单条）的条目追加到待发集合。</summary>
    private void AppendGroupEntries(List<GenericDataSet> sets, Gin gin, byte kod)
    {
        if (!_groups.TryGetValue(gin.Group, out var info))
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

    private void HandleWrite(IIec103Session session, in Iec103AsduView asdu)
    {
        var data = asdu.Data;
        if (data.Length < 2)
        {
            return;
        }

        var rii = data[0];
        var (count, _) = NgdCodes.Decode(data[1]);
        if (count == 0)
        {
            return;
        }

        var offset = 2;
        var gin = Gin.Zero;
        var kod = KodCodes.ActualValue;

        for (var i = 0; i < count; i++)
        {
            if (!GenericDataSet.TryDecode(data[offset..], out var set, out var consumed) || set is null)
            {
                return;
            }

            gin = set.Gin;
            kod = set.Kod;
            ApplyWrite(set);
            offset += consumed;
        }

        // 写确认回同一条目、COT=40（3.7.6/3.7.7 响应）
        session.SendAsduAsync(Asdu.GenericWriteAck(_ca, rii, gin, kod, GrcCodes.Acknowledge, negative: false));
    }

    /// <summary>写入落地：把值存进条目表。</summary>
    private void ApplyWrite(GenericDataSet set)
    {
        if (!_groups.TryGetValue(set.Gin.Group, out var info))
        {
            return;
        }

        var index = info.Entries.FindIndex(e => e.Entry == set.Gin.Entry);
        if (index >= 0)
        {
            info.Entries[index] = info.Entries[index] with { Gid = set.Gid };
        }
    }

    // ── 分帧发送 ────────────────────────────────────────────────────

    /// <summary>按 ASDU 上限分帧发送；非末帧置 NGD 后续标志。</summary>
    private void SendSets(IIec103Session session, byte rii, List<GenericDataSet> sets)
    {
        if (sets.Count == 0)
        {
            session.SendAsduAsync(Asdu.GenericDataAsdu(
                _ca, rii, continuation: false, CotCodes.GenericRead, Array.Empty<GenericDataSet>()));
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
            session.SendAsduAsync(Asdu.GenericDataAsdu(_ca, rii, more, CotCodes.GenericRead, chunk));
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
