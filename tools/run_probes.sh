#!/usr/bin/env bash
# =============================================================================
# tools/run_probes.sh —— 一键跑全部探针（回归脚本 · bash / git-bash 版）
#
# 用途：把 tests/*.tscn 里的探针逐个跑一遍，打印「名字 / 结果 / 耗时」，
#       最后给汇总表；任一失败 → 脚本退出码非 0（可直接喂给 CI / 别的脚本）。
#
# 用法：
#   bash tools/run_probes.sh                # 默认：只跑 headless 探针（安全，不弹窗）
#   bash tools/run_probes.sh --all          # 全部（含非 headless：会弹真实窗口、动光标）
#   bash tools/run_probes.sh --only PanelProbe
#   bash tools/run_probes.sh --only PanelProbe,StateProbe
#   bash tools/run_probes.sh --list         # 只列探针与分类，不跑（安全）
#   bash tools/run_probes.sh --no-agent     # 跳过需要真实 Agent（LLM）的探针
#   bash tools/run_probes.sh --skip Foo     # 额外把 Foo 当非 headless 跳过（临时白名单）
#   bash tools/run_probes.sh --timeout 300  # 单个探针超时秒数（默认 180）
#   bash tools/run_probes.sh --godot <exe>  # 指定 Godot 可执行文件
#   bash tools/run_probes.sh --logs <dir>   # 指定日志目录
#   bash tools/run_probes.sh --only SessionProbe -- -- read   # `--` 之后原样透传给探针
#
# 判定规则（与 tests/README.md 一致）：
#   * 退出码 0 = 通过；非 0 = 失败。124 是脚本自己的超时哨兵（超时也算失败）。
#   * 日志里 'FAIL  '（FAIL + 两个空格）= 一条失败断言，行数即失败断言数。
#   * 通过 = 退出码 0 且失败断言数 0。
#
# 环境变量（命令行参数优先）：GODOT_EXE / AIPET_PROJECT / PROBE_TIMEOUT
#
# 注意：探针会共用 user:// 存档（行为节律、数值等），所以**必须串行**跑，
#       本脚本不并行执行。非 headless 探针会真弹窗、真动光标。
# =============================================================================
set -uo pipefail

# ---------------------------------------------------------------- 可改配置 ---
# Godot 可执行文件：本机固定位置；换机器改这里，或用 --godot / $GODOT_EXE 覆盖。
GODOT_EXE="${GODOT_EXE:-D:/Games/godot/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe}"

# 项目根（默认 = 本脚本所在目录的上一级；可用 AIPET_PROJECT / --project 覆盖）。
PROJECT_DEFAULT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"

# ★ 非 headless 白名单：需要真实窗口 / 真实光标，默认跳过，只有 --all 或 --only 才跑。
#   DragProbe / SettingsProbe / StatsWindowProbe / EdgeHideBehaviorProbe / EnterProbe：
#     会真弹窗口、动光标，不能盲目乱跑。
#   WalkProbe：tests/README.md 标注「必须非 headless」——headless 下屏幕/窗口尺寸为 0，
#     `尝试走动` 直接放弃，headless 跑出来是假绿，所以也放白名单。
NON_HEADLESS=(DragProbe SettingsProbe StatsWindowProbe EdgeHideBehaviorProbe EnterProbe WalkProbe)

# 需要真实 Agent（LLM）的探针：没配 Agent 时会内部超时 → 失败。--no-agent 跳过。
AGENT_PROBES=(AcpTest ChatFlowTest CommandE2E HistoryProbe SessionProbe)

# 默认超时（秒）。单个探针超过就 taskkill 掉整棵进程树，并按失败计。
DEFAULT_TIMEOUT="${PROBE_TIMEOUT:-180}"
# -----------------------------------------------------------------------------

PROJECT_DIR="${AIPET_PROJECT:-$PROJECT_DEFAULT}"
TIMEOUT_SECS="$DEFAULT_TIMEOUT"
LOG_DIR=""
MODE="headless"        # headless | all
ONLY=()                # 显式指定的探针（跳过一切默认筛选）
LIST_ONLY=0
SKIP_AGENT=0
EXTRA_ARGS=()          # `--` 之后透传给探针的参数

# ---------------------------------------------------------------- 小工具 -----
die() { printf 'ERROR: %s\n' "$*" >&2; exit 3; }
usage_err() { printf 'ERROR: %s\n' "$*" >&2; printf '用法见 `bash tools/run_probes.sh --help`\n' >&2; exit 2; }

usage() { awk 'NR>1 && /^# =+$/ && ++c==2 {exit} NR>1 {sub(/^# ?/,""); print}' "${BASH_SOURCE[0]}"; }

# MSYS 路径 → Windows 原生路径（给 Godot 这类原生程序用；本机 MSYS 不做自动转换）
to_win() {
  if command -v cygpath >/dev/null 2>&1; then
    cygpath -m "$1"
  elif [[ "$1" =~ ^/([A-Za-z])/(.*)$ ]]; then
    printf '%s:/%s\n' "${BASH_REMATCH[1]^^}" "${BASH_REMATCH[2]}"
  else
    printf '%s\n' "$1"
  fi
}

now_ms() { date +%s%3N 2>/dev/null || printf '%s000\n' "$(date +%s)"; }

# 超时清场用的「场景标记」：清场时只杀命令行里带这个标记的 Godot 进程，
# 免得误伤编辑器 / 别的项目（每个探针开跑前设置）。
SCENE_MARKER=""

# 按命令行精确清场（超时兜底）。
# 为什么需要它：MSYS 下子进程会被「重新挂父」，taskkill /T 可能漏杀真正的 Godot 进程；
# 这里用 CIM 查进程自己的命令行，只杀 `res://tests/<本探针>.tscn` 的那个。
sweep_scene_procs() {
  local ps_exe='' marker="$SCENE_MARKER"
  [ -n "$marker" ] || return 0
  if command -v powershell.exe >/dev/null 2>&1; then ps_exe='powershell.exe'
  elif command -v pwsh >/dev/null 2>&1; then ps_exe='pwsh'; fi
  [ -n "$ps_exe" ] || return 0
  "$ps_exe" -NoProfile -NonInteractive -Command "Get-CimInstance Win32_Process -Filter \"Name like '%godot%'\" | Where-Object { \$_.CommandLine -like '*$marker*' } | ForEach-Object { Stop-Process -Id \$_.ProcessId -Force -ErrorAction SilentlyContinue }" >/dev/null 2>&1 || true
}

# 杀掉整棵进程树。
# 三个坑：
#   1) MSYS/Cygwin 的 PID ≠ Windows PID，必须用 /proc/<msyspid>/winpid 转成真 PID，
#      否则 taskkill 会打到一个完全无关的 Windows 进程上。
#   2) *_console.exe 是启动器（会另起真正的 Godot 进程），只杀它 = 屏幕上留个孤儿窗口，
#      所以必须 /T（连子进程一起杀）。
#   3) 斜杠写法跟「MSYS 路径转换」开关有关：转换开着要写 //F，关着要写 /F，两种都试，
#      再兜底走 cmd.exe。
kill_tree() {
  local pid="$1" winpid
  winpid="$(cat "/proc/$pid/winpid" 2>/dev/null || true)"
  if [ -n "$winpid" ]; then
    taskkill //F //T //PID "$winpid" >/dev/null 2>&1 \
      || taskkill /F /T /PID "$winpid" >/dev/null 2>&1 \
      || cmd.exe /c "taskkill /F /T /PID $winpid" >/dev/null 2>&1 \
      || true
  else
    printf '\n  [warn] 拿不到 WINPID（/proc/%s/winpid），只能杀直接子进程\n' "$pid" >&2
  fi
  sweep_scene_procs
  kill -9 "$pid" >/dev/null 2>&1 || true
}

RC=0
# run_with_timeout <秒> <日志文件> <命令...>；结束时 exit code 落在全局 RC
#   0..127 = 探针自己的退出码；124 = 超时杀掉；125 = 超时且没杀干净
run_with_timeout() {
  local secs="$1" log="$2"; shift 2
  local flag="$log.timeout" pid watchdog start_ms end_ms i=0
  rm -f "$flag"
  start_ms="$(now_ms)"
  "$@" >"$log" 2>&1 &
  pid=$!
  ( sleep "$secs"; : >"$flag"; kill_tree "$pid" ) >/dev/null &
  watchdog=$!
  # 等探针自己退出，或等超时哨兵（硬上限 secs+15s；MSYS 下每圈约 0.1-0.15s）。
  # ⚠ 必须允许等到 secs，不能只等十几秒——否则 20 秒以上的探针会被误判「杀不掉」。
  local cap=$((secs * 10 + 150))
  while kill -0 "$pid" 2>/dev/null; do
    [ -f "$flag" ] && break
    [ "$i" -ge "$cap" ] && break
    sleep 0.1; i=$((i + 1))
  done
  # 哨兵已触发 / 触到硬上限：有界等待杀进程生效（最多再等 ~15s），等不到才算杀不掉
  if kill -0 "$pid" 2>/dev/null; then
    i=0
    while kill -0 "$pid" 2>/dev/null && [ "$i" -lt 150 ]; do sleep 0.1; i=$((i + 1)); done
  fi
  if kill -0 "$pid" 2>/dev/null; then
    kill_tree "$pid" >/dev/null 2>&1 || true
    RC=125
    printf '\n  [warn] 进程 %s 杀不掉，屏幕上可能残留一个窗口；继续跑下一个\n' "$pid" >&2
  else
    wait "$pid" 2>/dev/null; RC=$?   # 2>/dev/null 屏蔽 bash 的 "Killed" 噪声
  fi
  end_ms="$(now_ms)"
  # 只有「跑满超时时间」且哨兵被写下才算真超时（防收尾边界误判）
  if [ -f "$flag" ] && [ $((end_ms - start_ms)) -ge $((secs * 1000)) ]; then
    [ "$RC" = 125 ] || RC=124
  fi
  kill "$watchdog" >/dev/null 2>&1 || true
  wait "$watchdog" 2>/dev/null || true
  rm -f "$flag"
  return 0
}

count_fails() { grep -c 'FAIL  ' "$1" 2>/dev/null || true; }

in_list() { # in_list <名字> [列表...]
  local needle="$1"; shift
  local item
  for item in "$@"; do [ "$item" = "$needle" ] && return 0; done
  return 1
}

# 探针 NOTE（只用于 --list 的说明列，纯文档性质，不影响跑法）
note_for() {
  case "$1" in
    AcpTest)      echo 'Agent：真起后端进程（需 hermes-acp）' ;;
    ChatFlowTest) echo 'Agent：惰性连接 + 真调 LLM' ;;
    CommandE2E)   echo 'Agent：真调一次 LLM' ;;
    HistoryProbe) echo 'Agent：真连一次会话历史' ;;
    SessionProbe) echo '两趟：先设暗号，再 `-- -- read` 验记忆' ;;
    DegradeProbe) echo 'Agent 不可用时的降级路径' ;;
    AudioProbe)   echo '音频设备诊断（不发声）' ;;
    TtsProbe)     echo '语音：真合成到 WAV，不打断主人' ;;
    EnvProbe)     echo '真读 Win32 环境（全屏/空闲）' ;;
    WindowProbe)  echo '作者注释说更想要真实窗口；headless 也能跑' ;;
    WalkProbe)    echo '必须非 headless（headless 下放弃走动）' ;;
    *)            echo '' ;;
  esac
}

# ---------------------------------------------------------------- 参数解析 ---
while [ $# -gt 0 ]; do
  case "$1" in
    --all)        MODE="all" ;;
    --headless)   MODE="headless" ;;
    --only)       [ $# -ge 2 ] || usage_err '--only 需要探针名'
                  IFS=',' read -r -a _p <<<"$2"; ONLY+=("${_p[@]}"); shift ;;
    --only=*)     IFS=',' read -r -a _p <<<"${1#--only=}"; ONLY+=("${_p[@]}") ;;
    --skip)       [ $# -ge 2 ] || usage_err '--skip 需要探针名'
                  IFS=',' read -r -a _p <<<"$2"; NON_HEADLESS+=("${_p[@]}"); shift ;;
    --skip=*)     IFS=',' read -r -a _p <<<"${1#--skip=}"; NON_HEADLESS+=("${_p[@]}") ;;
    --list)       LIST_ONLY=1 ;;
    --no-agent)   SKIP_AGENT=1 ;;
    --agent)      SKIP_AGENT=0 ;;
    --timeout)    [ $# -ge 2 ] || usage_err '--timeout 需要秒数'; TIMEOUT_SECS="$2"; shift ;;
    --timeout=*)  TIMEOUT_SECS="${1#--timeout=}" ;;
    --godot)      [ $# -ge 2 ] || usage_err '--godot 需要路径'; GODOT_EXE="$2"; shift ;;
    --godot=*)    GODOT_EXE="${1#--godot=}" ;;
    --logs)       [ $# -ge 2 ] || usage_err '--logs 需要目录'; LOG_DIR="$2"; shift ;;
    --logs=*)     LOG_DIR="${1#--logs=}" ;;
    --project)    [ $# -ge 2 ] || usage_err '--project 需要目录'; PROJECT_DIR="$2"; shift ;;
    --project=*)  PROJECT_DIR="${1#--project=}" ;;
    -h|--help)    usage; exit 0 ;;
    --)           shift; EXTRA_ARGS=("$@"); break ;;
    -*)           usage_err "未知参数 $1" ;;
    *)            usage_err "多余的位置参数 $1（指定探针请用 --only $1）" ;;
  esac
  shift
done

case "$TIMEOUT_SECS" in
  ''|*[!0-9]*) usage_err "--timeout 需要正整数秒数，得到 '$TIMEOUT_SECS'" ;;
esac

GODOT_WIN="$(to_win "$GODOT_EXE")"
PROJECT_WIN="$(to_win "$PROJECT_DIR")"
TESTS_DIR="$PROJECT_DIR/tests"

# ---------------------------------------------------------------- 收集探针 ---
[ -d "$TESTS_DIR" ] || die "找不到探针目录 $TESTS_DIR —— 项目路径对吗？（用 --project 或 \$AIPET_PROJECT 指定 AIPet 根目录）"

ALL_PROBES=()
while IFS= read -r line; do
  [ -n "$line" ] && ALL_PROBES+=("$line")
done < <(cd "$TESTS_DIR" && ls -1 *.tscn 2>/dev/null | sed 's/\.tscn$//' | LC_ALL=C sort)
[ "${#ALL_PROBES[@]}" -gt 0 ] || die "$TESTS_DIR 下没有 *.tscn 探针场景"

norm_name() { # 允许 `--only Foo` / `--only Foo.tscn`，大小写不敏感
  local n="${1%.tscn}" p
  for p in "${ALL_PROBES[@]}"; do
    [ "${p,,}" = "${n,,}" ] && { printf '%s\n' "$p"; return 0; }
  done
  return 1
}

is_non_headless() { in_list "$1" "${NON_HEADLESS[@]}"; }

win_count=0
for p in "${ALL_PROBES[@]}"; do is_non_headless "$p" && win_count=$((win_count + 1)); done

# --- --list：只列清单，不跑（安全） ---
if [ "$LIST_ONLY" = 1 ]; then
  printf '探针清单（%s，共 %d 个）\n\n' "$(to_win "$TESTS_DIR")" "${#ALL_PROBES[@]}"
  printf '  %-22s %-13s %s\n' 'NAME' 'MODE' 'NOTE'
  printf '  %-22s %-13s %s\n' '----' '----' '----'
  for p in "${ALL_PROBES[@]}"; do
    mode='headless'; is_non_headless "$p" && mode='non-headless'
    note="$(note_for "$p")"
    in_list "$p" "${AGENT_PROBES[@]}" && note="${note:+$note }[需 Agent]"
    printf '  %-22s %-13s %s\n' "$p" "$mode" "$note"
  done
  printf '\nheadless %d 个 / 非 headless %d 个。\n' "$(( ${#ALL_PROBES[@]} - win_count ))" "$win_count"
  printf '默认（不带 --all）只跑 headless 的 %d 个；非 headless 的会被跳过：%s\n' \
    "$(( ${#ALL_PROBES[@]} - win_count ))" "${NON_HEADLESS[*]}"
  printf '超时：单个探针 %ss（--timeout 改）。\n' "$TIMEOUT_SECS"
  exit 0
fi

# --- 选定本次要跑的探针 ---
SEL_NAMES=(); SEL_MODES=(); SKIPPED=(); SKIPPED_NH=(); SKIPPED_AGENT=()
if [ "${#ONLY[@]}" -gt 0 ]; then
  for raw in "${ONLY[@]}"; do
    [ -n "$raw" ] || continue
    name="$(norm_name "$raw")" || die "找不到探针 '$raw'（用 --list 看全部名字）"
    if [ "${#SEL_NAMES[@]}" -gt 0 ] && in_list "$name" "${SEL_NAMES[@]}"; then continue; fi
    if is_non_headless "$name"; then SEL_MODES+=("window"); else SEL_MODES+=("headless"); fi
    SEL_NAMES+=("$name")
  done
else
  for p in "${ALL_PROBES[@]}"; do
    if [ "$MODE" != "all" ] && is_non_headless "$p"; then
      SKIPPED+=("$p"); SKIPPED_NH+=("$p"); continue
    fi
    if [ "$SKIP_AGENT" = 1 ] && in_list "$p" "${AGENT_PROBES[@]}"; then
      SKIPPED+=("$p"); SKIPPED_AGENT+=("$p"); continue
    fi
    if is_non_headless "$p"; then SEL_MODES+=("window"); else SEL_MODES+=("headless"); fi
    SEL_NAMES+=("$p")
  done
fi
[ "${#SEL_NAMES[@]}" -gt 0 ] || die '没有可跑的探针（筛选条件全被跳过了？）'

# --- Godot 可执行文件检查：没有就优雅报错，而不是每个探针都炸一遍 ---
if [ ! -f "$GODOT_EXE" ]; then
  die "找不到 Godot 可执行文件：$GODOT_EXE
   → 改 tools/run_probes.sh 顶部的 GODOT_EXE，或用 --godot <路径> / \$GODOT_EXE 指定。
   → 本机默认：D:/Games/godot/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe"
fi

if [ -z "$LOG_DIR" ]; then
  LOG_DIR="${TMPDIR:-/tmp}/aipet_probes/$(date +%Y%m%d-%H%M%S)"
fi
mkdir -p "$LOG_DIR" || die "建不了日志目录 $LOG_DIR"

printf '=== AIPet 探针回归 ===\n'
printf '项目   : %s\n' "$PROJECT_WIN"
printf 'Godot  : %s\n' "$GODOT_WIN"
if [ "$MODE" = "all" ]; then
  printf '范围   : 全部 %d 个（含非 headless：会真弹窗 / 真动光标）\n' "${#SEL_NAMES[@]}"
else
  printf '范围   : 仅 headless %d 个\n' "${#SEL_NAMES[@]}"
fi
printf '超时   : 单个 %ss（超时按失败计，taskkill 整棵进程树）\n' "$TIMEOUT_SECS"
printf '日志   : %s\n' "$(to_win "$LOG_DIR")"
[ "${#EXTRA_ARGS[@]}" -gt 0 ] && printf '透传   : %s\n' "${EXTRA_ARGS[*]}"
if [ "${#SKIPPED[@]}" -gt 0 ]; then
  if [ "${#SKIPPED_NH[@]}" -gt 0 ]; then
    printf '跳过   : %s\n' "${SKIPPED_NH[*]}"
    printf '         （非 headless：要 --all 才会跑；单个可用 --only <名字>）\n'
  fi
  if [ "${#SKIPPED_AGENT[@]}" -gt 0 ]; then
    printf '跳过   : %s\n' "${SKIPPED_AGENT[*]}"
    printf '         （需要真实 Agent：去掉 --no-agent 就会跑）\n'
  fi
fi
printf '\n'

# ---------------------------------------------------------------- 逐个跑 -----
RES_NAMES=(); RES_MODES=(); RES_RESULTS=(); RES_FAILS=(); RES_EXITS=(); RES_SECS=()
N_PASS=0; N_FAIL=0; N_TIMEOUT=0; TOTAL_FAILS=0; FAILED_LOGS=()

idx=0; total="${#SEL_NAMES[@]}"
for i in "${!SEL_NAMES[@]}"; do
  name="${SEL_NAMES[$i]}"; kind="${SEL_MODES[$i]}"
  idx=$((idx + 1))

  CMD=("$GODOT_EXE")
  if [ "$kind" = "window" ]; then mode_label='window'; else mode_label='headless'; CMD+=(--headless); fi
  CMD+=(--path "$PROJECT_WIN" "res://tests/$name.tscn")
  [ "${#EXTRA_ARGS[@]}" -gt 0 ] && CMD+=("${EXTRA_ARGS[@]}")

  log="$LOG_DIR/$name.log"
  printf '[%2d/%2d] %-22s %-10s ' "$idx" "$total" "$name" "($mode_label)"
  start_ms="$(now_ms)"
  SCENE_MARKER="res://tests/$name.tscn"   # 超时清场只杀这个场景的 Godot 进程
  run_with_timeout "$TIMEOUT_SECS" "$log" "${CMD[@]}"
  SCENE_MARKER=""
  rc="$RC"
  end_ms="$(now_ms)"
  secs=$(( (end_ms - start_ms) / 1000 ))
  fails="$(count_fails "$log")"
  [ -n "$fails" ] || fails=0

  if [ "$rc" = 124 ] || [ "$rc" = 125 ]; then
    result='TIMEOUT'; N_TIMEOUT=$((N_TIMEOUT + 1)); N_FAIL=$((N_FAIL + 1)); FAILED_LOGS+=("$log")
  elif [ "$rc" = 0 ] && [ "$fails" = 0 ]; then
    result='PASS'; N_PASS=$((N_PASS + 1))
  else
    result='FAIL'; N_FAIL=$((N_FAIL + 1)); FAILED_LOGS+=("$log")
  fi
  TOTAL_FAILS=$((TOTAL_FAILS + fails))

  printf '%s  %4ds  exit=%-3s FAIL=%s\n' "$result" "$secs" "$rc" "$fails"
  if [ "$result" != 'PASS' ]; then
    if [ "$fails" -gt 0 ]; then
      grep 'FAIL  ' "$log" 2>/dev/null | head -12 | sed 's/^/           | /'
      [ "$fails" -gt 12 ] && printf '           | …（还有 %d 条，见日志）\n' "$((fails - 12))"
    else
      printf '           | 日志末尾（没有 FAIL 行，原因在这里找）：\n'
      tail -n 8 "$log" 2>/dev/null | sed 's/^/           | /'
    fi
  fi

  RES_NAMES+=("$name"); RES_MODES+=("$mode_label"); RES_RESULTS+=("$result")
  RES_FAILS+=("$fails"); RES_EXITS+=("$rc"); RES_SECS+=("$secs")
done

# ---------------------------------------------------------------- 汇总 -------
printf '\n=== 汇总 ===\n'
printf '%-22s %-10s %-8s %5s %5s %6s\n' 'PROBE' 'MODE' 'RESULT' 'FAIL' 'EXIT' 'TIME'
printf '%-22s %-10s %-8s %5s %5s %6s\n' '----------------------' '----------' '--------' '-----' '-----' '------'
for i in "${!RES_NAMES[@]}"; do
  printf '%-22s %-10s %-8s %5s %5s %5ss\n' \
    "${RES_NAMES[$i]}" "${RES_MODES[$i]}" "${RES_RESULTS[$i]}" \
    "${RES_FAILS[$i]}" "${RES_EXITS[$i]}" "${RES_SECS[$i]}"
done

printf '\n探针 %d 个：通过 %d / 失败 %d（其中超时 %d）；失败断言合计 %d\n' \
  "${#RES_NAMES[@]}" "$N_PASS" "$N_FAIL" "$N_TIMEOUT" "$TOTAL_FAILS"
if [ "${#SKIPPED[@]}" -gt 0 ]; then
  printf '未跑（跳过）%d 个：%s\n' "${#SKIPPED[@]}" "${SKIPPED[*]}"
fi
if [ "${#FAILED_LOGS[@]}" -gt 0 ]; then
  printf '失败日志：\n'
  for l in "${FAILED_LOGS[@]}"; do printf '  - %s\n' "$(to_win "$l")"; done
fi
if [ "$N_FAIL" -eq 0 ]; then
  printf '结果：全部通过 ✔\n'
  exit 0
else
  printf '结果：有失败 ✘\n'
  exit 1
fi
