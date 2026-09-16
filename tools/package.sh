#!/usr/bin/env bash
# AIPet 打包脚本：构建 → Godot 导出 → 组装分发包 → 打 zip
#
# 用法： bash tools/package.sh [版本号]        （版本号缺省读仓库根 VERSION 文件）
# 依赖： dotnet SDK、Godot 4.7.2 mono 可执行文件（可用环境变量 GODOT= 指定）、导出模板（首次需在 Godot
#        编辑器里 Editor → Manage Export Templates 下载 4.7.2 模板）
#
# 注意：shell 变量名必须用 ASCII（bash 不认识中文标识符，会把它当命令执行）。注释与输出用中文没问题。
#
# 为什么这样组装（见 document/打包审计.md）：
#   * 导出产物读不到 res:// 里的 config/*.json → 必须把 config/ 放在 exe 旁边（ConfigFile 的查找顺序）
#   * mods/ 与 config/ 是运行必需资产，必须随包（桌宠动画、命令、工具路径都在这）
#   * 分发包里的 agent.json 要清掉作者机器路径（默认「本地桌宠模式」，用户自己接 Agent）
#   * 不要装进 Program Files：程序要在自己旁边读写 mods/ config/

set -uo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
GODOT="${GODOT:-D:/Games/godot/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe}"
VER=$(cat "$ROOT/VERSION" 2>/dev/null | tr -d '[:space:]')
[ -z "$VER" ] && VER="0.0.0"
[ $# -ge 1 ] && VER="$1"
OUT_DIR="$ROOT/dist/AIPet-$VER"
EXPORT_DIR="$ROOT/dist/export"

echo "=== [1/5] 构建（Release；必须 0 个 error CS）==="
BUILD_OUT=$(dotnet build "$ROOT/desktop.csproj" -c Release 2>&1)
ERR_COUNT=$(printf '%s' "$BUILD_OUT" | grep -c "error CS")
if [ "$ERR_COUNT" != "0" ]; then
    printf '%s\n' "$BUILD_OUT" | grep "error CS" | head -20
    echo "✗ 构建失败：$ERR_COUNT 个错误 —— 先修好再打包"
    exit 1
fi
echo "✓ 构建 OK"

echo "=== [2/5] Godot 导出 ==="
if [ ! -f "$GODOT" ]; then
    echo "✗ 找不到 Godot 可执行文件：$GODOT"
    echo "  用环境变量指定，例如： GODOT=/path/to/Godot_console.exe bash tools/package.sh"
    exit 1
fi
TEMPLATE_DIR="$USERPROFILE/AppData/Roaming/Godot/export_templates"
if ! ls "$TEMPLATE_DIR" 2>/dev/null | grep -q "4\.7"; then
    echo "✗ 没找到 4.7.x 导出模板（$TEMPLATE_DIR）"
    echo "  首次打包：打开 Godot 编辑器 → 编辑器菜单 → 管理导出模板 → 下载并安装与 4.7.2 匹配的模板"
    exit 2
fi
mkdir -p "$EXPORT_DIR"
"$GODOT" --headless --path "$ROOT" --export-release "MagicPet" "$EXPORT_DIR/MagicPet.exe" || {
    echo "✗ 导出失败（看上面 Godot 输出）"; exit 1;
}
echo "✓ 导出完成"

echo "=== [3/5] 组装分发包 ==="
rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR/licenses"
cp "$EXPORT_DIR/MagicPet.exe" "$OUT_DIR/" 2>/dev/null
cp "$EXPORT_DIR/MagicPet.pck" "$OUT_DIR/" 2>/dev/null || echo "  （提示：embed_pck=false 时 exe 与 pck 必须一起发）"
cp -r "$EXPORT_DIR"/data_* "$OUT_DIR/" 2>/dev/null   # .NET 导出产物目录
cp -r "$ROOT/mods" "$OUT_DIR/mods"                   # 运行必需资产
cp -r "$ROOT/config" "$OUT_DIR/config"

# 清掉作者机器专属配置：默认「本地桌宠模式」，用户自己接 Agent
python - "$OUT_DIR/config/agent.json" <<'PYEOF'
import json, sys, os
p = sys.argv[1]
if os.path.exists(p):
    d = json.load(open(p, encoding="utf-8"))
    d["backend"] = "none"
    d["executable"] = ""
    d["workingDirectory"] = ""
    d["_comment_dist"] = ("分发包默认「本地桌宠模式」，不连任何 Agent。想接上你自己的 Agent："
                          "把 backend 改成 \"hermes-acp\"、executable 指向它（如 hermes.exe），"
                          "arguments 保持 \"acp\"；详情见包内「新用户上手.md」。")
    json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    print("✓ 已清空分发包里的 Agent 路径（默认本地模式）")
PYEOF

cp "$ROOT/LICENSE" "$OUT_DIR/licenses/GPL-3.0.txt" 2>/dev/null
cp "$ROOT/licenses/"* "$OUT_DIR/licenses/" 2>/dev/null
cp "$ROOT/document/新用户上手.md" "$OUT_DIR/" 2>/dev/null
cp "$ROOT/VERSION" "$OUT_DIR/"

cat > "$OUT_DIR/README.txt" <<TXTEOF
AIPet $VER —— 解压即用
重要：请放在**可写目录**（例如 D:\\AIPet），不要放进 Program Files —— 它要在自己旁边读写 mods/ config/。
1) 双击 MagicPet.exe 启动桌宠（首次启动会生成人格与数据文件）。
2) 数据位置：%APPDATA%\\Godot\\app_userdata\\AIPet\\（人格 soul/soul.md、数值 stats.json、
   上下文接口 context.md、事件 events.jsonl、画像 soul/profile.md、记忆 soul/memory.jsonl）。
3) 退出：右键桌宠 → 退出。
4) 想让它连上你自己的 Agent（推荐）：见 config/agent.json 与「新用户上手.md」。
5) 语音：默认 Edge 在线语音（晓晓）；不想联网可在 config/tts.json 切「sapi」（Windows 自带）。
TXTEOF

echo "=== [4/5] 打 zip ==="
ZIP_OK=0
if command -v 7z >/dev/null 2>&1; then
    ( cd "$ROOT/dist" && 7z a -tzip "AIPet-$VER.zip" "AIPet-$VER" >/dev/null ) && ZIP_OK=1
elif command -v zip >/dev/null 2>&1; then
    ( cd "$ROOT/dist" && zip -qr "AIPet-$VER.zip" "AIPet-$VER" ) && ZIP_OK=1
else
    powershell -NoProfile -Command "Compress-Archive -Path '$OUT_DIR' -DestinationPath '$ROOT/dist/AIPet-$VER.zip' -Force" >/dev/null 2>&1 && ZIP_OK=1
fi
if [ "$ZIP_OK" = "1" ]; then ls -lh "$ROOT/dist/AIPet-$VER.zip"; else echo "  （没打 zip：装个 zip / 7z，或手动右键压缩）"; fi

echo "=== [5/5] 完成 ==="
echo "产物目录：$OUT_DIR"
echo "验收清单：见包内「新用户上手.md」—— 最好在**没装 SDK 的机器/干净账户**上解压跑一遍。"
