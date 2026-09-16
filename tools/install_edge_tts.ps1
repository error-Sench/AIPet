# 给桌宠装 Edge 在线语音（edge-tts）
#
# 为什么不用系统语音：Windows 自带的 SAPI5 声音很机械（主人原话「听得我要窒息了」）。
# edge-tts 是纯 Python 小工具（约几十 KB，不下载任何模型），调用 Edge 的在线语音，
# 声音自然；默认用 zh-CN-XiaoxiaoNeural（晓晓）。
#
# 用法：powershell -ExecutionPolicy Bypass -File tools\install_edge_tts.ps1
# 装完重启桌宠即可（settings/tts.json 的 引擎 保持 "edge"）。
# 没装 / 断网时桌宠会自动回退系统语音，不会报错。

$ErrorActionPreference = "Stop"
$venv = Join-Path $env:LOCALAPPDATA "AIPet\tts-venv"
$py = Get-Command python -ErrorAction SilentlyContinue
if (-not $py) { Write-Host "没找到 python，请先装 Python 3.9+ 并加入 PATH" -ForegroundColor Red; exit 1 }

if (-not (Test-Path (Join-Path $venv "Scripts\python.exe"))) {
    Write-Host "创建专用 venv: $venv"
    & python -m venv $venv
}
Write-Host "安装 edge-tts ..."
& (Join-Path $venv "Scripts\python.exe") -m pip install --quiet --upgrade pip edge-tts

$exe = Join-Path $venv "Scripts\edge-tts.exe"
if (Test-Path $exe) {
    Write-Host "OK: $exe" -ForegroundColor Green
    Write-Host "试一句（会用晓晓念出来）..."
    & $exe --voice zh-CN-XiaoxiaoNeural --text "语音装好了，我是小萝。" --write-media (Join-Path $env:TEMP "aipet_tts_check.mp3")
    Write-Host "已生成测试音频: $env:TEMP\aipet_tts_check.mp3"
    Write-Host "可用中文语音：& $exe --list-voices | Select-String zh-CN"
} else {
    Write-Host "安装失败：没找到 $exe" -ForegroundColor Red; exit 1
}
