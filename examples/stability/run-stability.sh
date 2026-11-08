#!/usr/bin/env bash
#
# 并行长跑稳定性测试（101 / 103 / 104）
#
# 三个协议各自起一个独立 dotnet test 进程，互不共享资源计数器 ——
# 这样每份日志里的 句柄/线程/托管堆 漂移断言才是有意义的（单进程并发会互相污染）。
#
# 用法：
#   ./run-stability.sh            # 默认每个用例 120 秒
#   ./run-stability.sh 600        # 每个用例 10 分钟
#
# 结果：
#   tests-cs104/StabilityEnduranceTests.cs 的三条用例
#   logs/stability-{101,103,104}.log
#   脚本最后打印三份日志的结论行，任一失败则退出码非 0。

set -u

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DURATION="${1:-120}"
LOG_DIR="$ROOT/examples/stability/logs"
PROJECT="$ROOT/tests-cs104/tests-cs104.csproj"

mkdir -p "$LOG_DIR"

# testhost 残留会锁住输出目录 —— 必须在构建之前清理
tasklist //FI "IMAGENAME eq testhost.exe" 2>/dev/null \
    | grep -i testhost | awk '{print $2}' \
    | xargs -r -n1 taskkill //F //PID >/dev/null 2>&1

echo "== 构建测试项目 =="
if ! dotnet build "$PROJECT" -v q --nologo; then
    echo "构建失败，终止" >&2
    exit 1
fi

run_one() {
    local name="$1"
    local filter="$2"
    local log="$LOG_DIR/stability-$name.log"

    echo "== 启动 $name（${DURATION}s）→ $log =="
    IEC_STABILITY_SECONDS="$DURATION" \
        dotnet test "$PROJECT" --no-build --filter "$filter" \
        --logger "console;verbosity=normal" >"$log" 2>&1

    local rc=$?
    echo "$name exit=$rc"
    return $rc
}

echo "== 并行启动 101 / 103 / 104 =="
run_one 101 "FullyQualifiedName~稳定性101" &
PID101=$!
run_one 103 "FullyQualifiedName~稳定性103" &
PID103=$!
run_one 104 "FullyQualifiedName~稳定性104" &
PID104=$!

wait $PID101; RC101=$?
wait $PID103; RC103=$?
wait $PID104; RC104=$?

echo
echo "══════════════ 汇总 ══════════════"
for name in 101 103 104; do
    log="$LOG_DIR/stability-$name.log"
    echo "── $name ──"
    grep -E "^\[$name\]|已通过|失败 |错误消息|Test Run|测试总数|通过数|失败数" "$log" | tail -12
    echo
done

if [ "$RC101" -ne 0 ] || [ "$RC103" -ne 0 ] || [ "$RC104" -ne 0 ]; then
    echo "结果：存在失败（101=$RC101 103=$RC103 104=$RC104）" >&2
    exit 1
fi

echo "结果：三协议长跑全部通过"
