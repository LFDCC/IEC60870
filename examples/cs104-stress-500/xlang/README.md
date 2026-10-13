# 跨语言同口径微基准（vs lib60870-C）

`bench-lib60870-c.c` 是 mz-automation lib60870-C 的协议核心微基准，与
`../MicroBench.cs` 同工作负载（完整路径：建 ASDU → 建对象 → Add → 编码 /
从字节建 ASDU → getElement×N → 销毁），用于本库与 C 参考实现的跨语言对比。

复现（Windows / MSVC）：

1. `git clone --depth 1 https://github.com/mz-automation/lib60870.git`
2. `cmake -S lib60870/lib60870-C -B cb -DBUILD_EXAMPLES=OFF -DBUILD_TESTS=OFF`
3. `cmake --build cb --config Release`（得到 `cb/src/lib60870.dir/Release/*.obj`）
4. 用 VS CMake 生成器把 `bench-lib60870-c.c` 与全部 `lib60870.dir` 静态对象一起编成
   exe（`/O2`），运行 `bench.exe 1000000`（100 万次 × 7 轮取最优）。
5. .NET 侧同机跑 `dotnet run -c Release --project .. -- --bench --bench-iterations 1000000`。

结果见 `STRESS-TEST-REPORT.md` §4.5。基准机：Win11 16 逻辑核，MSVC 14.51，.NET 8.0.28。
