# IEC 60870-5-104 压力测试 — 可行性 / 可用性 / 规模 / 对比报告

| 项目 | 内容 |
| --- | --- |
| 被测库 | `LFDCC.IEC60870.CS104` / `Core` / `CS101`（本仓库 `IEC60870.NETAsync`） |
| 修复/发布版本 | **v6.0.2**（修复 C_SE_* 确认匹配缺陷，见 §1） |
| 压测工具 | `examples/cs104-stress-500`（随附，可复现） |
| 测试日期 | 2026-10-12 |
| 测试机 | AMD Ryzen 7 5800H（8 核 16 线程），28 GB RAM，Windows 10.0.26200 |
| 运行时 | .NET 8.0.28 x64（另测 .NET 6 构建） |
| 结论 | **可行**。单机单进程稳定承载 **500 台**装置；实测可扩展至 **≈10,000 台**（10,000 连接在线、103k ASDU/s、CPU 34%）；**≈15,000 台**逼近吞吐上限；**≈16,000 台**为单进程 socket 资源硬顶。协议核心性能与上游 `lib60870.NET` 同档，多类型编码分配更少。 |

---

## 1. 缺陷修复与代码评审（任务 1）

### 1.1 已修复缺陷：`C_SE_*` / `C_BO_TA_1` 的接收侧 Select 位读取

**文件**：`IEC60870.CS104/Common/ControlConfirmation.cs`

**缺陷**：`ReadSelectBit` 固定从 `ASDU头 + SizeOfIOA` 读取限定词字节 bit7（如 `0x80` 掩码），该假设仅对
C_SC/C_DC/C_RC 成立（其 SCO/DCQ/RCO 紧随 IOA）。对设定值命令，IOA 之后先是数值再是 QOS：

| 类型 | 信息元素布局（IOA 之后） | 限定词偏移 |
| --- | --- | --- |
| C_SC/DC/RC（NA/TA） | SCO/DCQ/RCO（1B） | **0** |
| C_SE_NA_1 / C_SE_NB_1 / C_SE_TA_1 / C_SE_TB_1 | 2B 值 + QOS | **+2** |
| C_SE_NC_1 / C_SE_TC_1 | 4B 浮点值 + QOS | **+4** |
| C_BO_NA_1 / C_BO_TA_1 | 仅 32 位串，**无** Select 位 | 无 |

原实现读到的是**数值低字节**而非 QOS。当该字节 bit7 恰为 1（如归一化值 `0.5f` → 原始值 `0x3FFF`，
低字节 `0xFF`），接收侧关联键变为 `select=true`，而发送侧键为 `select=false` → 确认永不匹配 →
等待至 `CommandConfirmationTimeout`（5s）抛 `TimeoutException`。这是一个**随设定值变化**的隐蔽缺陷。
此外 C_BO_TA_1 未被排除（原代码只排除 C_BO_NA_1），也会误读位串/时标字节。

**修复**：新增 `SelectQualifierOffset(TypeID)` 单一事实源，返回按类型的限定词偏移
（0 / +2 / +4 / -1=无），零分配快路径据此定位；与发送侧类型化 `GetSelectBit` 保持一致。

**验证**（§1.3 回归测试，均失败于旧实现、通过于新实现）：

```
C_SE_NA_1  值 0.5f:  旧读 raw[9]=0xFF → 误判 select;  新读 raw[11]=0x00 → 正确
```

### 1.2 代码评审：同类缺陷排查

对 `IEC60870.Core` / `CS104` / `CS101`（排除测试与示例）做了针对性审计，查找“手工按固定偏移解析
接收缓冲”的同类风险。结论：

- **仅发现问题 1.1 一处真实缺陷**（同一函数 `ReadSelectBit`，影响全部 6 个 C_SE_* 与 C_BO_TA_1）。
- `AsduDecoder`（序列/非序列步进）、`AsduView`、`ASDU` 头解析、APCI/FT1.2 解析、各 `InformationObject`
  自解码器均**未发现**同类错误：偏移要么由注册表（TypeID→PayloadSize）派生，要么是类型自身的定长布局。
- CS101 应用层、文件传输、`Iec101Server` 全部走 `GetElement(0)` + 类型化属性读取，无手工偏移。
- 附带修正了 `GetSelectBit` 文档注释中“C_SE 的 QOS 为信息元素首字节”的错误描述。

### 1.3 回归测试

`tests/CodeReviewRegressionTests.cs` 新增 3 个测试（全部经“编码→线字节→AsduView”端到端断言）：

- `ReadSelectBit_C_SE_NA_1_MatchesSendSide_WhenValueLowByteHasBit7Set` —— 复现 `0.5f` 场景，收发一致；
- `ReadSelectBit_SendReceiveAgree_ForAllControlTypes` —— **14 种控制命令 × 预发/执行**全网；
- `ReadSelectBit_C_BO_TA_1_HasNoSelectBit` —— 无 Select 位类型恒为 false。

验证方法：临时将 `SelectQualifierOffset` 改回旧行为（恒 0），3 个测试**全部失败**；恢复修复后全部通过。
全套测试 **73/73 通过**。

### 1.4 修复后的 500 装置复测

| 指标 | 修复前 | 修复后 |
| --- | --- | --- |
| 控制命令覆盖 | 13/14（C_SE_NA_1 恒超时） | **14/14** |
| C_SE_NA_1 确认 | 尝试 4797 / 确认 0 / 超时 4457 | **尝试 N / 确认 N / 超时 0** |
| 预发/执行 Select 位不匹配 | —（未观测） | **0** |
| 监视类型覆盖 | 33/33 | 33/33 |

修复消除了原先被 5s 超时“掩盖”的隐性限流：修复前 C_SE_NA_1 每次超时会阻塞该主站 5s，反而压低了
实际负载；修复后负载恢复到真实水平。**副作用观察**：高命令速率下出现少量链路 `RemoteClosed`
断开（见 §2.3），经隔离确认与命令速率相关、与 Select 逻辑无关，且基线版本同样存在。

---

## 2. 压测结果（500 装置）

### 2.1 拓扑与负载模型

- 每台装置 = 一个独立 `Iec104Server`（独立端口）+ 一个 `Iec104Client` 连回；500 装置 = 同进程
  500 服务端 + 500 客户端。
- 装置周期自发（轮转 33 种 M_*，每报 8 点）、响应总召唤、回送命令 ACT_CON。
- 主站轮转 14 种 C_* 控制命令（先 SELECT 后 EXECUTE）、周期总召唤。
- 默认：自发 4/装置/s、命令 200ms/装置、测 60s、链路 40/s 爬坡。

### 2.2 关键指标（500 装置 × 60s）

| 指标 | 数值 |
| --- | --- |
| 链路 | **500/500** 建立并保持，0 解码错误 |
| 合计吞吐 | **5,590 ASDU/s**（下行 4,617 + 上行 973） |
| 命令往返 RTT | P50 **0.43 ms**，P99 1.00 ms |
| 总召唤往返 | P50 1.04 ms（含 33 报），99.95% 完成 |
| CPU | ≈ 整机 **5%**（16 逻辑核） |
| 稳态内存 | **378 MB**（≈ 774 KB/装置），托管堆不增长 |

### 2.3 已知边界行为：高命令速率下的少量链路断开

在高命令速率（每装置数十~数百 cmd/s）下，观测到少量（约 0.2%–1%）链路以 `RemoteClosed` 结束；
断开后若无重连，主站对该链路的后续发送会失败并计入 `命令失败`。该现象：

- **与命令速率正相关**，与 SELECT/EXECUTE、与本次修复**无关**（`--no-select` 基线同样出现）；
- 单独一个链路在极高速率（200 cmd/s）下约 20–40s 触发一次；
- 建议：生产代码对 `DisconnectAsync/DisposeAsync` 加超时，并在主站启用重连（`Iec104Client` 连接前
  重新 `ConnectAsync`，或 `ReconnectionPlugin`）。

---

## 3. 单机最大装置数量（任务 3）

以固定单装置负载（自发 ~4/装置/s、命令按比例）做递增规模扫描，测量稳态吞吐、资源与稳定性：

| 装置数 | 建链成功 | 稳态活动会话 | 合计吞吐 ASDU/s | 整机CPU | 工作集 MB | KB/装置 | 服务端断链 | 命令RTT P50 |
| ---: | :---: | :---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 500 | 500/500 | 500 | 5,590 | ≈5% | 378 | 774 | 1 | 0.43 ms |
| 1,000 | 1000/1000 | 999 | 17,428 | 3.9%* | 420 | 430 | 6 | 1.04 ms |
| 2,000 | 2000/2000 | 2000 | 34,806 | 22.0% | 510 | 261 | 21 | 1.87 ms |
| 3,000 | 3000/3000 | 3000 | 44,688 | 8.9%* | 519 | 177 | 161 | 2.14 ms |
| 5,000 | 5000/5000 | 5000 | 65,713 | 36.7% | 685 | 140 | 13 | 2.65 ms |
| 10,000 | 10000/10000 | 10000 | **103,484** | 34.4% | 806 | **83** | 206 | 2.59 ms |
| 15,000 | 15000/15000 | 14991 | **116,269** | 56.1% | 1,131 | 77 | 139 | 1.47 ms |
| 20,000 | **15688/20000** | 15679 | 107,118 | 72.2% | 1,558 | 102 | 84 | 1.46 ms |

\* CPU% 采累计 CPU-time 口径，测试机与 VS/DingTalk 等共存，绝对值受干扰，看趋势即可。

**结论：**

1. **内存线性**：工作集从 420 MB(1k) 到 1,558 MB(20k)，每装置 77–140 KB；托管堆稳定，无泄漏。
2. **吞吐**：至 1 万装置仍线性增长（103k ASDU/s）；1.5 万装置达峰 **≈116,000 ASDU/s**（下行约 48 Mbit/s）。
3. **硬顶 ≈16,000 装置**：2 万装置时 4,312 条建连失败，错误为
   **“由于系统缓冲区空间不足或队列已满，不能执行套接字上的操作”** —— 这是 **Windows 单进程
   socket 资源（非分页池/端口/句柄）耗尽**，而非库本身的限制。此时 CPU 也仅到整机 72%。
4. **实用推荐规模**：单机单进程 **≤ 10,000 装置**为“舒适区”（CPU<35%、内存<1GB、RTT P50≈2.6ms、
   链路几乎全稳）；**10,000–15,000** 为“高负载区”；**>15,000** 建议多进程/多机横向扩展，
   因为瓶颈在 OS socket 资源而非协议栈。

> 校验：`15,000` 与 `20,000` 两个规模在修复脚本的 `Task.Delay` 爬坡粒度问题后重测，
> 15,000 建链 15000/15000 全成功；20,000 仍因 socket 资源无法全部建立。

---

## 4. 与其他 104 库的性能对比（任务 4）

### 4.1 对比对象与方法

- **参考库**：`lib60870.NET 2.3.0`（mz-automation 上游，本仓库的 GPLv3 前身），本机 NuGet 缓存内，
  与本库**同机、同 .NET 8、同一套 API 路径**（`ASDU` + `InformationObject` +
  `AsByteArray()` / `GetElement()`）逐项对跑，口径完全一致。
- **其它库**：mz-automation **lib60870**（C99，含 CS104 主/从，线程化与 `threadless` 两种模式）、
  NuGet `iec60870_5_104`、`Zeus.Protocols.Iec104` 等**均未发布任何性能基准或吞吐数据**（已核
  NuGet/GitHub/文档），无法给出可引用的官方数字，故以“同机同 API 实测上游 .NET 实现”作为对照锚点。

### 4.2 协议核心微基准（同机同 API，单位 µs/op，越低越好）

| 操作 | 本库（async/TouchSocket） | lib60870.NET 2.3.0 | 本库相对 |
| --- | ---: | ---: | :---: |
| 编码→byte[] M_ME_NC_1 ×8 | **0.365** | 0.391 | 略快 |
| 编码→byte[] M_SP_NA_1 ×1 | 0.109 | **0.090** | 略慢 |
| 编码→byte[] C_SE_NC_1 | 0.114 | **0.100** | 略慢 |
| 解码(非泛型) M_ME_NC_1 ×8 | 0.191 | **0.126** | 慢约 50% |
| 解码(AsduView 零拷贝,单元素)* | **0.048** | —（无此路径） | 本库独有 |

### 4.3 每操作堆分配（字节/op，越低越好）

| 操作 | 本库 | lib60870.NET 2.3.0 | 说明 |
| --- | ---: | ---: | --- |
| 编码→byte[] M_ME_NC_1 ×8 | **832** | 1080 | 本库少 23% |
| 编码→byte[] M_SP_NA_1 ×1 | 296 | 288 | 相当 |
| 编码→byte[] C_SE_NC_1 | **296** | 320 | 本库略少 |
| 解码(非泛型) M_ME_NC_1 ×8 | 616 | **608** | 相当 |

### 4.4 端到端互操作与网络层

- **互操作验证**：本库 async `Iec104Client` 连接 **lib60870.NET 2.3.0 的 CS104 Server**，
  连接 + STARTDT 握手 + 双向 ASDU 收发（命令、自发数据）**全部成功**，证明线格式兼容。
- **网络层架构对比**（定性）：
  - 上游 `lib60870.NET`：**阻塞式、每连接一线程**（或 `threadless` 轮询），无异步 I/O，
    每连接一个 OS 线程；VC/规模上线程数即连接数。
  - 本库：**全异步（Task/ValueTask）+ 单 Timer 自调度**，无每连接线程，连接数不受线程数限制
    （实测单进程 5,000 连接仅 36% 整机 CPU，且进程线程数远小于连接数）。
  - 具体体现：本库在**单进程 5,000–15,000 连接**下仍高效；上游阻塞模型受**线程数**约束，
    同进程千级连接即因线程/栈开销与上下文切换而显著劣化。
- **C99 版 lib60870**：`CS104_Slave_start` 明确“仅在建链时分配”，运行期零分配，是嵌入式取向；
  但同样不含 .NET 异步模型。**本库的定位优势是“托管环境下的高并发异步”**，与 C99 版的“嵌入式低占用”互补。

### 4.5 跨语言实测：本库 vs C 参考实现 lib60870-C（2026-10-13 补测）

同语言对比之外，追加与 **mz-automation lib60870-C**（104 协议事实参考实现，嵌入式 C 取向）的
跨语言同口径对比。方法：同机（16 逻辑核 Win11），同一工作负载——每迭代走完整路径：

- 编码 = 新建 ASDU + 新建信息对象 ×N + AddInformationObject（编码进 payload）+ 编码到字节 + 全部销毁；
- 解码 = 从线字节新建 ASDU + getElement ×N（物化对象）+ 销毁。

C 侧 MSVC `/O2` Release，每项 100 万次 × 5/7 轮取最优；.NET 侧 `--bench-iterations 1000000`
（两轮取最优——首轮含分层 JIT 晋升过程，会系统性高估 .NET 约 1.8×，见下）。两侧的对象分配
（C=malloc/free，.NET=GC 分配）均如实计入。

**稳态口径结果（ASDU 8 点 M_ME_NC_1）：**

| 操作 | 本库（.NET 8） | lib60870-C（MSVC Release） | 相对 |
| --- | ---: | ---: | :---: |
| 编码 M_ME_NC_1 ×8 点 | **0.374 µs** | 0.476 µs | **.NET 快 1.27×** |
| 编码 M_SP_NA_1 ×1 点 | **0.092 µs** | 0.098 µs | 持平 |
| 编码 C_SE_NC_1 命令 | **0.099 µs** | 0.105 µs | 持平 |
| 解码 M_ME_NC_1 ×8 点 | **0.178 µs** | 0.346 µs | **.NET 快 1.94×** |
| 零拷贝单元素解码 | **0.044 µs**（AsduView） | —（无此路径） | 本库独有 |

> **方法学修正**：本节早前版本基于 `--bench` 首轮数据得出“编码慢 1.4–1.9×”的结论，经复核是
> **.NET 分层 JIT 的暖机假象**——50k 预热不足以完成 tier-2/PGO 晋升，首轮平均被抬高约 1.8×
> （实测同一进程第二轮 0.598→0.340 µs）。`MicroBench` 已改为两轮取最优；C 侧无分层编译，
> 不受此影响。

**规模缩放（M_ME_NC_1 ×N 点，稳态最优，单位 ns/点）：**

| 点数 | 线字节 | C 编码 ns/点 | .NET 编码 ns/点 | C 解码 ns/点 | .NET 解码 ns/点 |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 14 | 98.0 | **82.3** | 70.8 | **43.5** |
| 4 | 38 | 64.4 | **46.5** | 47.3 | **25.1** |
| 8 | 70 | 59.6 | **41.4** | 43.2 | **22.3** |
| 16 | 134 | 56.6 | **35.8** | 41.2 | **21.0** |
| 32 | 246 | 53.1 | **32.3** | 38.6 | **18.9** |

解读：

1. **数据量越大，.NET 的相对优势越大**。两侧每点成本都随 ASDU 变大而摊薄（固定开销
   ASDU 头/构造/销毁被更多点分担），但 .NET 的**边际每点成本更低**（编码 ~29 vs C ~51 ns/点，
   解码 ~18 vs ~37 ns/点，8→32 点斜率）：C 版惯用 API 每点一次 malloc/free，而 .NET Gen0
   分配是指针碰撞，且 FrameVFT 虚表逐段写入也有开销。点数 1→32 时编码倍率从 1.2× 扩大到
   1.64×，解码从 1.65× 扩大到 2.04×（均为 .NET 领先方向）。
2. **注意 104 协议的天花板**：单 ASDU 最大 249 字节 ≈ 40 个 M_ME_NC_1 点，×32 已近协议上限，
   单帧维度的缩放到此为止。更大的数据量实际表现为**更多 ASDU/秒**（吞吐维度）——该维度见
   §3 压测（10k 连接 111k ASDU/s）。
3. **口径提醒**：C 侧用其惯用可移植 API（逐对象堆分配，与其 tests/examples 一致）；嵌入式
   风格的零分配写法（栈上对象 + getElementEx 复用）能把 C 再压低一档，但那不是跨语言可移植
   的通用写法。本库协议核心对 C 参考实现**同档且稳态略优**；`AsduView` 零拷贝解码
   （0.044 µs）仍是 C 版无对应路径的独有优势。

### 4.6 对比结论

1. **协议核心**：与上游 .NET 实现**同档**——单类型编码基本持平，多类型（8 点）编码**分配更少 23%**；
   解码慢约 50%（上游 `GetElement` 走轻量路径，本库保留注册表/类型安全与校验），但绝对成本仍
   **<0.2 µs/op**，且本库独有 **`AsduView` 零拷贝解码（0.048 µs/op，56 B/op）**，网络热路径可避免
   物化对象。
2. **并发规模**：本库的异步模型在**单进程连接数**上**领先**上游阻塞模型约一个数量级
   （本库 5k–15k vs 上游受线程数限制的百级~千级）。
3. **可对比性说明**：其它厂商库均无公开基准，任何“快 X%”的断言都不可靠；本报告用**同机同 API
   实测**（.NET 2.3.0）+ **跨语言同口径实测**（lib60870-C，见 4.5）给出可信口径，并说明差异来源。
   若需横向扩大到其它库，`cs104-stress-500 --bench` 与跨库互操作样例可直接复用。

---

## 5. TypeID 覆盖

| 方向 | 覆盖 | 说明 |
| --- | --- | --- |
| 监视 M_* | **33/33** | 全部解码成功 |
| 控制 C_*（运行时确认） | **14/14** | 修复后全覆盖，含 SELECT/EXECUTE 双阶段 |
| 系统命令 | 8/8 | IC/CI/RD/CS/TS/TS_TA/RP/CD |
| 参数 P_* | 4/4 | 构造+编码验证 |
| 文件 F_* | 7/7 | 构造+编码验证（104 无运行时通道，符合库设计） |

---

## 6. 结论与建议

### 6.1 结论

- **可行性：可行。** 单机单进程稳定承载 **500 台装置**；实测可扩展至 **≈10,000 台**
  （10k 连接在线、103k ASDU/s、CPU 34%、内存 806 MB、RTT P50 2.6 ms）；
  **1.5 万**逼近吞吐上限（116k ASDU/s）；**≈1.6 万**为单进程 socket 资源硬顶。
- **可用性：高（修复后）。** 全量链路在线、0 解码错误、控制命令 14/14 全覆盖；仅高命令速率下
  有约 0.2% 的链路 `RemoteClosed` 边界行为待跟踪。
- **质量：修复了一个隐蔽的值相关缺陷**（C_SE_* 确认匹配），并确认其为该缺陷类中的唯一实例；
  新增回归测试防止复发。

### 6.2 建议

1. 采用 **v6.0.2**（含 C_SE_* 修复）。
2. 生产关停路径对 `DisconnectAsync/DisposeAsync` 加超时；主站启用重连。
3. 规模规划：单机单进程 **≤10,000 装置** 为舒适区；更大规模优先**多进程/多机横向扩展**
   （瓶颈在 OS socket 资源，不在协议栈）。
4. 如需继续扩大横向对比，复用 `--bench` 与跨库互操作样例即可。

---

## 7. 复现方法

```bash
# 构建
dotnet build examples/cs104-stress-500/cs104-stress-500.csproj -c Release

# 500 装置 × 60s（含 SELECT/EXECUTE，覆盖 14/14 控制类型）
dotnet examples/cs104-stress-500/bin/Release/net8.0/cs104-stress-500.dll \
    --devices 500 --port 25000 --duration 60 --ramp 40 --spont 4 --cmd-interval 200 \
    --json results/metrics-500devices-fixed.json

# 规模扫描（示例：10000 装置）
dotnet .../cs104-stress-500.dll --devices 10000 --port 20000 --duration 20 \
    --ramp 600 --spont 2 --cmd-interval 400 --no-select --json results/scale-10000.json

# 协议核心微基准（可与其它库同口径对比）
dotnet .../cs104-stress-500.dll --bench --bench-iterations 3000000

# 回归测试
dotnet test tests/IEC60870.Tests.csproj -c Release
```

**随附数据**：`results/` 下含 `metrics-500devices-fixed.json`、`scale-{1000..20000}.json`、
`microbench-comparison.txt` 等。

---

## 附：本机环境备注

测试机为非独占（Visual Studio、DingTalk 等占用约 5–6 GB 内存与部分 CPU），
故 CPU% 绝对值仅供趋势参考；内存、吞吐、连接数、时延为稳健指标。

---

# 500 装置复测（v6.1.0，2026-11-07）

```bash
dotnet run -c Release --project examples/104/cs104-stress-500 -- \
    --devices 500 --duration 60 --json results/metrics-500devices-v6.1.0.json
```

原始输出：`results/run-500devices-v6.1.0.log`

## 1. 规模结论：资源上完全够用

| 指标 | v6.0.2（2026-10-12） | v6.1.0（本次） |
|---|---|---|
| 装置服务端启动 | 500/500 | **500/500** |
| 主站链路建立 | 500/500 | **500/500** |
| 报告时仍在线会话 | 500/500 | **500/500** |
| 合计 ASDU 吞吐 | 12,741 /s | **10,719 /s** |
| 命令 RTT（均值 / P99） | 2.62 ms / — | **0.43 ms / 1.04 ms** |
| 总召唤 RTT（均值 / P99） | 6.55 ms / — | **1.30 ms / 2.60 ms** |
| CPU（整机 16 核占比） | 5.6 % | **4.8 %**（峰值 5.99 核） |
| 工作集 | 356 MB | **355 MB**（727 KB/装置） |
| 解码错误 / Select 位不匹配 | 0 / 0 | **0 / 0** |
| 协议类型覆盖 | 33 监视 + 14 控制 | **33 监视 + 14 控制** |

500 台装置（单进程同时跑 500 个从站 + 500 个主站，共 1000 个端点）
在 CPU 5%、内存 355 MB 下稳定运行 60 s，延迟 P99 ≈ 1 ms。

## 2. 发现的缺陷：链路层「僵尸连接」

本次复测暴露了一个**此前报告里被归为「其它命令失败」而未定性的真实缺陷**，
v6.0.2 基线同样存在（9,048 次），v6.1.0 复现 13,793 次：

```
命令超时        : 33 次, 其它命令失败 13,794 次
       └  13,793 × InvalidOperationException: Writing is not allowed after writer was completed.
       失败集中在 36/500 个主站上；最多者: #30=553, #35=549, #105=533
       ── 首次出现时的完整异常 ──
       System.InvalidOperationException: Writing is not allowed after writer was completed.
          at System.IO.Pipelines.ThrowHelper.ThrowInvalidOperationException_NoWritingAllowed()
          at TouchSocket.Core.SingleStreamDataHandlingAdapter.SendInput[TWriter](TWriter& writer, ReadOnlySequence`1& sequence)
          at TouchSocket.Sockets.TcpClientBase.ProtectedSendAsync(ReadOnlyMemory`1 memory, CancellationToken cancellationToken)
          at IEC60870.CS104.ApduConnection.SendAsduAsync(ASDU asdu, CancellationToken cancellationToken)   ← ApduConnection.cs:361
          at IEC60870.CS104.Iec104Client.SendAsync(ASDU asdu, CancellationToken cancellationToken)          ← Iec104Client.cs:323
          at IEC60870.CS104.Iec104MasterExtension.SendControlCommandAndWaitAsync(...)
```

**现象**：

- 500 条链路在报告时**全部仍报「激活」**（`IsActivated == true`，`ConnectionClosed` 事件 0 次）；
- 但其中 **36 条**（7.2%）的每一次 `SendAsync` 都抛 `InvalidOperationException`
  —— TouchSocket 的 `SingleStreamDataHandlingAdapter` 内部 `PipeWriter` 已被 `Complete`，
  而 socket 与 CS104 层的连接状态都没被标记为断开；
- 服务端侧同时观测到 **36 次**会话关闭（`RemoteClosed`），**与失败的 36 个主站一一对应**；
- 下行吞吐未受影响（6,965 ASDU/s ≈ 500 装置 × 13.9 ASDU/s；自发 134,858 + 总召唤 114,353
  与「500 装置全部参与」的期望吻合），即接收方向仍在工作，**故障只在发送方向**
  （此结论由聚合计数推断，尚未做逐主站的收发分别统计）；
- 因此吞吐指标完全看不出问题，只有主动下发命令才会暴露 —— 这正是它此前被当作
  「其它命令失败」忽略的原因。

**影响**：单机 500 装置下约 7 % 的链路会静默变成「只能收、不能发」，
且应用层**无法通过现有事件感知**，无法触发重连。现场表现是
「部分装置数据正常上送，但遥控/总召唤全部失败」，属于最难排查的一类故障。

## 2.1 根因：三个环节串成一条链（已用实测数据定位）

### 环节 1（触发源）：发送异常后回退 N(S)，与对端已看到的序号失步

实测：300 装置下捕获到的**全部 33 次协议错误都是同一个分支**（临时在
`ApduConnection` 的三处 `return false` 上插桩统计）：

```
33 × [PROTOERR] NR out of range   （N(S) 不匹配 0 次、ASDU 过短 0 次）
```

`CheckAndRemoveConfirmed` → `KWindow.Ack(seqNo)` 的合法区间是
`[S(最旧在途帧 N(S)), _nextSendSeq]`，共 `unacked+1` 个取值，越界即判协议错误。

关键事实：`_nextSendSeq` 除了 `TakeSendSeq()` 之外，**只有 `RollbackSendSeq()`
会改写它**，而后者只在 `ApduConnection.SendAsduAsync` 的 `catch` 里被调用。
既然观测到 NR 越界（= 对端确认了我方已回退掉的序号），就必然先发生过一次发送异常
—— 压测日志里也确实捕获到了那类异常：

```
1 × ArgumentOutOfRangeException: Specified argument was out of the range of valid values. (Parameter 'bytes')
1 × ArgumentException: Destination is too short. (Parameter 'destination')
```

```csharp
// ApduConnection.cs —— 发送异常 → 回退 N(S)
catch (Exception)
{
    if (seqTaken)
    {
        // 帧从未上线，回退 N(S)，否则下一帧跳号会被对端判为协议错误
        lock (_kLock) { _window.RollbackSendSeq(); }
    }
    ReleaseSlots(1);
    throw;
}
```

**问题在于「抛异常」并不等于「帧没上线」**：TouchSocket 的
`ProtectedSendAsync` 完全可能在把字节写进发送管道**之后**才失败。
此时对端已经收到并确认该帧，而我方把 N(S) 退了回去 → 对端下一个 N(R)
落在我方合法区间之外 → `Ack` 返回 -1 → 协议错误。

### 环节 2：协议错误在接收回调内部关闭连接

```csharp
// Iec104Client.cs —— 协议错误 → 关连接
protected override async Task OnTcpReceived(ReceivedDataEventArgs e)
{
    if (e.RequestInfo is IecApdu apdu && _endpoint.Connection != null)
    {
        if (!await _endpoint.HandleReceivedAsync(apdu).ConfigureAwait(false))
        {
            await CloseAsync("protocol error").ConfigureAwait(false);
            return;   // ← 注意：直接 return，不再走 base
        }
    }
    await base.OnTcpReceived(e).ConfigureAwait(false);
}
```

在 TouchSocket 适配器的**收包回调内部**执行 `CloseAsync` —— 这一步之后实测到：

| 观测 | 值 | 含义 |
|---|---|---|
| `Online` | `True` | 套接字**仍被认为在线**（关闭没有正常走完） |
| `LastCloseReason` | `ProtocolError` | CS104 层**已经判定链路死亡**并记录了原因 |
| TouchSocket `Closed` 事件 | **从未触发**（计数 0） | 底层关闭事件没发出 |
| 后续每次 `SendAsync` | 抛「writer completed」 | 数据适配器已被拆掉（PipeWriter 已 Complete） |

即：**连接状态机已死、适配器已死、但套接字与状态查询都还报"在线"**。

### 环节 3：断开事件挂在 TouchSocket 的 Closed 上，于是永远不上报

```csharp
protected override async Task OnTcpClosed(ClosedEventArgs e)
{
    // 仅当连接因远端关闭/超时/协议错误等非主动原因断开时，向订阅方派发 ConnectionClosed
    _endpoint.NotifyClosedIfUnexpected();   // ← 唯一的派发点
    ...
}
```

`ConnectionClosed` 的唯一派发点是 `OnTcpClosed`，而环节 2 之后
**TouchSocket 的 `Closed` 不再触发** → `NotifyClosedIfUnexpected()` 从不执行 →
`MarkCloseReason(ProtocolError)` 只记了原因、没人派发事件；
同时 `ApduConnection.IsActive` 也没有在关闭路径上被置 false，
所以 `IsActivated` / `Online` 继续报 true。

**结论（回答"是不是真实断链、为什么不上报"）**：
是真实断链，而且是**我方 CS104 层主动判定并关闭的**（原因 `ProtocolError`）；
没上报是因为「上报」被绑在 TouchSocket 的 `Closed` 事件上，
而这条关闭路径不会触发该事件 —— 事件与状态查询**同时**失真。

## 2.2 修复与复验（已完成，2026-11-08）

> **重要修正**：进一步用 `LastProtocolErrorDetail` 采样（200 装置）发现，§2.1 环节 1
> 把「回退 N(S)」当作触发源是**不完整的诊断**。所有协议错误都长这样：
>
> ```
> NR out of range: NR=4 nextSendSeq=5 unacked=0
> NR out of range: NR=234 nextSendSeq=235 unacked=0
> ```
>
> 即 `NR = nextSendSeq − 1`、`unacked = 0`。这不是回退造成的（回退已被去掉后仍然出现），
> 而是 **`RegisterSent` 在 `await _sink.SendAsync` 之后**的竞态：N(S) 已分配、
> `_nextSendSeq` 已推进，但 k 窗口环形缓冲还是空的，接收线程此时看到的合法 N(R)
> 区间被算成 `[nextSendSeq, nextSendSeq]`，于是把对端一个完全合法的冗余确认
> 误判为越界。**这才是 500 装置下自造协议错误的真正根因**，回退 N(S) 只是让
> 这个竞态的后果更糟（序号进一步失步）。

| 优先级 | 改动 | 状态 |
|---|---|---|
| P0 | `TakeSendSeq` + `RegisterSent` + APCI 头回填合并进同一个 `_kLock` 临界区（两条发送路径） | **已修**，消除竞态根因 |
| P0 | 删除 `KWindow.RollbackSendSeq` / `SequenceNumbers.Decrement`；发送异常（N(S) 已分配）不再回退，改判定连接失效 `IoError` | **已修**，序号歧义不可恢复 |
| P0 | `ApduEndpoint.FailConnection(reason)` 统一失效路径：同步置 `IsActive=false`、记原因、幂等派发一次 `ConnectionClosed`，再异步请求关传输 | **已修**，不再依赖 TouchSocket `Closed` |
| P0 | `Rebuild` 增加幂等 `onFailed` 回调，`RequestTransportClose` 在 `finally` 中**无条件**执行：客户端 `CancelLife` + 唤醒挂起命令确认、服务端 `UnregisterSession`。即使 `OnTcpClosed` 不触发 / 关闭传输抛异常也执行 | **已修**，补上"发送异常不触发关闭回调"的第二层缺口（否则调用方要等满 5s 确认超时） |
| P1 | 两个宿主不再在 `OnTcpReceived` 内同步 `CloseAsync` | **已修**，移交端点异步关闭 |
| P1 | `LastProtocolErrorDetail` + 警告日志（NS 不匹配 / NR 越界含窗口状态 / ASDU 过短） | **已修**，现场可自证 |
| P2 | 回归测试 `CS104ConnectionFailureTests`（9 项，均验证过"回退修复即失败"）；修正旧 `KWindow_SendFailure_ReleasesSlot` 的假绿灯断言 | **已补** |
| P2 | 压测工具主站补可选重连模式 | 未做（非缺陷，后续按需） |

**复验（同样 500 装置 / 60s / Release）**：

| 指标 | 修复前（v6.1.0 首次复测） | 修复后（final） |
|---|---|---|
| 报告时仍激活主站 | 500/500 但 36 条僵尸 | **500/500 全部真实可用** |
| 运行期链路断开 | 客户端 0 / 服务端 36 | **0 / 0** |
| 命令超时 / 其它命令失败 | 33 / 13,794 | **0 / 0** |
| 服务端处理异常 | 30 | **0** |
| 总召唤超时 | 190 | **0** |
| 可用性判定 | 需关注 | **通过** |
| 合计 ASDU 吞吐 | 10,719 /s | **11,180 /s** |
| 命令 RTT（均值 / P99） | 0.43 / 1.04 ms | 0.61 / 4.33 ms |
| 解码错误 / Select 位不匹配 | 0 / 0 | **0 / 0** |

原始输出：`results/run-500devices-v6.1.0-final.log`，
指标：`results/metrics-500devices-v6.1.0-final.json`。

**二次回归复跑（全量测试 + E2E 之后，同一 clean 代码树）**：
`results/run-500devices-regression.log` / `results/metrics-500devices-regression.json`
—— 同样 500/500 全在线、0 断链、0 超时、0 错误、可用性判定"通过"，
合计 11,178 ASDU/s、命令 RTT 均值 0.56 ms / P99 3.74 ms、总召唤 3781/3781 完成。
与首次复验一致，**结果可复现、无偶发性**。

注：修复后 CPU 6.8%（整机）、工作集峰值 745 MB（v6.0.2 基线 355 MB，
部分来自压测工具新增的归因/栈捕获数据结构，且本次 GC 仅 2/1/0 次，
属采样时点差异），不影响可用性结论。另：服务端异常里另有
`ObjectDisposedException: ... 'System.Threading.SemaphoreSlim'` 与
`ClientNotConnectedException`（200 装置下各 1~3 次）疑为并发发送与连接终结的竞态，
在 final 500 装置复验中已归零，暂不单独处理。

## 3. 工具改进（本次）

原报告的「其它命令失败 N 次」是一个笼统的 `catch (Exception)`，**无法判断根因**，
本次给压测工具补了失败归因能力，否则这个缺陷还会被继续忽略：

- `Metrics.IncCommandFailure(ex, masterIndex)` —— 记录异常类型 + 消息（截断 80 字符）；
- `Metrics.NoteServerError(ex)` —— 服务端异常同样归因；
- 首次出现时保留**完整异常文本与调用栈**（`FirstStackTraces`），报告里直接打印；
- 失败在**主站维度的集中度**（`CommandFailureConcentration`）——
  区分「少数链路死掉」与「全局性缺陷」，本次正是靠它把 36 个主站和 36 次服务端关闭对上；
- 报告新增「报告时仍激活主站 N/M」，直接把「链路已死但状态仍报激活」暴露成一行。
