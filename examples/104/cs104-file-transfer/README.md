# CS104 录波文件召唤示例

演示 IEC 60870-5-104 的**标准文件传输**（对应 IEC 60870-5-101 文件传输附录），
用于从装置召唤故障录波文件。

> **与许继 103 的区别**：许继 103 的录波走 `TI=24/26/30/31`（私有）；
> 104 的录波走这里的 `TI=120~127` 文件传输。两者机制完全不同。

## 用法

```bash
dotnet run                # 自环：从站 + 主站同进程，端口 2410
dotnet run -- 2411        # 指定端口
```

运行后会看到完整流程，并在 `output/` 下生成：

```
output/
├── source.cfg / source.dat              ← 从站侧源文件（COMTRADE）
└── received-<时间戳>.cfg / .dat          ← 主站收到的文件
```

最后几行为：

```
✔ 内容与从站源文件逐字节一致
✔ cfg 与源一致: True
✔ dat 与源一致: True
```

## 报文流程

```
主站                                从站
  │                                  │
  ├─ F_SC_NA_1 选择文件(SCQ=1) ─────→│
  │                                  │
  ├─ F_SC_NA_1 请求文件(SCQ=2) ─────→│
  │←───── F_FR_NA_1 文件就绪 ────────┤  NOF / 文件长度 / FRQ(0=肯定)
  │←───── F_SR_NA_1 段就绪 ──────────┤  段号 / 段长度 / SRQ
  │←───── F_SG_NA_1 段数据 ──────────┤  × N 段
  │←───── F_LS_NA_1 最后段 ──────────┤  LSQ=1(无需停止激活) / 2(需停止激活)
  │                                  │
  ├─ F_SC_NA_1 停止激活(SCQ=3) ────→│  （LSQ=2 时才需要）
  │←───── F_LS_NA_1 (COT=ACTIVATION_TERMINATION) ┤
```

## 类型标识

| TI | 名称 | 方向 | 说明 |
|---|---|---|---|
| 120 | `F_FR_NA_1` | 从→主 | 文件就绪，含文件名 NOF、文件长度、限定词 FRQ |
| 121 | `F_SR_NA_1` | 从→主 | 段就绪，含段号、段长度、限定词 SRQ |
| 122 | `F_SC_NA_1` | 主→从 | 文件召唤/选择，含 NOF、段号、SCQ |
| 123 | `F_LS_NA_1` | 从→主 | 最后段/段结束，含 LSQ、校验和 |
| 125 | `F_SG_NA_1` | 从→主 | 段数据（变长） |
| 126 | `F_DR_TA_1` | 从→主 | 目录 |
| 127 | `F_SC_NB_1` | 主→从 | 段召唤（无 NOF） |

## 限定词

**SCQ（选择与召唤限定词，用于 F_SC_NA_1）**

| 值 | 名称 | 含义 |
|---|---|---|
| 0 | DEFAULT | 未使用 |
| 1 | SELECT_FILE | 选择文件 |
| 2 | REQUEST_FILE | 请求（召唤）已选文件 |
| 3 | DEACTIVATE_FILE | 取消选择文件 |
| 4 | DELETE_FILE | 删除文件 |
| 5 | SELECT_SECTION | 选择段 |
| 6 | REQUEST_SECTION | 请求已选段 |
| 7 | DEACTIVATE_SECTION | 取消选择段 |

**LSQ（最后段/段限定词，用于 F_LS_NA_1）**

| 值 | 含义 |
|---|---|
| 0 | NOT_USED |
| 1 | 文件传输结束，**不发**停止激活 |
| 2 | 文件传输结束，**随后**停止激活 |
| 3 | 段传输结束，不发停止激活 |
| 4 | 段传输结束，随后停止激活 |

**NOF（文件名）**：`DISTURBANCE_DATA` = 故障录波数据。另有 `TRANSPARENT_FILE`、
`SEQUENCES_OF_EVENTS`、`SEQUENCES_OF_ANALOGUE_VALUES`。

## 录波文件格式：COMTRADE（.cfg + .dat）

示例生成标准 COMTRADE 录波，不是自拟的二进制：

**source.cfg**（通道定义，ASCII 文本）
```
XUJI,PROT-01,1999              站名, 设备ID, 版本年
4,4A,0D                        4 个通道, 4 个模拟量, 0 个开关量
1,IA,A,A,0.01,0,0,0,0          通道号, 名称, 相别, 单位, 一次系数, 二次系数, 偏移, 有效值, 编码
2,IB,B,A,0.01,0,0,0,0
3,IC,C,A,0.01,0,0,0,0
4,3I0,N,A,0.01,0,0,0,0
1000,0                         采样率 1000 Hz
200                            采样点数
27/11/2026,10:30:00.000000     第一个采样点时间
27/11/2026,10:30:00.050000     触发时间
ASCII                          数据文件格式
1                              时间倍率（1 = 微秒）
```

**source.dat**（采样数据，CSV）
```
采样序号,时间戳(μs),IA,IB,IC,3I0
1,0,0,6816,9974,7780
2,1000,499,7173,9997,7457
...
```

物理值 = 一次系数 × 原始值 + 偏移，即 `0.01 × 9974 = 99.74 A`。

### 两个文件怎么通过 104 传

104 文件传输一次只传**一个流**，而 COMTRADE 需要两个文件。示例在应用层打包：

```
[4B "CTP1"][4B cfg 长度 LE][cfg 文本][dat 文本]
```

主站收到完整流后按此头拆回两个文件。真实装置若支持一次传多文件，
可对每个文件单独走一轮文件传输（NOF 相同、文件名不同）。

## 分段策略

单帧段数据上限 = `ASDU 249` − `ASDU 头 6` − `IO 固定头 7`（IOA 3 + NOF 2 + 段号 1 + 段长 1）
= 236 字节。示例取 **200 字节/段**留余量。

示例打包文件 6716 字节（cfg 200 + dat 6508 + 包头 8）→ 34 段。

## 实现要点

**从站侧**（`HandleFileCommand`）

- `SELECT_FILE` → 回 `ACTIVATION_CON`
- `REQUEST_FILE` → 依次发 `F_FR` → (`F_SR` + `F_SG`)×N → `F_LS`
- `DEACTIVATE_FILE` → 回 `F_LS`，COT 为 `ACTIVATION_TERMINATION`

**主站侧**（`RunMasterAsync`）

- 收到 `F_LS` 且 `LSQ` 为 `WITH_DEACT`/`WITHOUT_DEACT` 时**立即交付数据**，
  不等停止激活的确认（那只是流程收尾）

> **踩坑**：最初在 `COT == ACTIVATION_TERMINATION` 时才交付，导致超时——
> 因为 `FILE_TRANSFER_WITH_DEACT` 那帧来的时候数据其实已经齐了，
> 而停止激活的确认永远是最后一帧。

**发送 ASDU 不用指定 TypeId**：`AddInformationObject(io)` 会自动把 ASDU 的
`TypeId` 设为 `io.Type`（见 `IEC60870.Core/Encoding/AsduEncoder.cs:33`）。

## 相关文件

- `IEC60870.Core/File/` — 文件传输类型实现（`FileReady`、`SectionReady`、
  `FileCallOrSelect`、`FileSegment`、`FileLastSegmentOrSection`、`FileDirectory`）
- `IEC60870.Core/TypeHandlers/FileTypeHandlers.cs` — 类型编解码器
