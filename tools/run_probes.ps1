# =============================================================================
# tools/run_probes.ps1 - run every AIPet probe (one-shot regression runner)
#
# PowerShell twin of tools/run_probes.sh, for machines without bash/git-bash.
# This file is intentionally PURE ASCII: Windows PowerShell 5.1 parses *.ps1
# without a BOM as ANSI, so non-ASCII comments would break on some hosts.
#
# Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\run_probes.ps1
#   powershell ... -File tools\run_probes.ps1 -List              # list only, no run
#   powershell ... -File tools\run_probes.ps1 -All               # include real-window probes
#   powershell ... -File tools\run_probes.ps1 -Only PanelProbe
#   powershell ... -File tools\run_probes.ps1 -Only PanelProbe,StateProbe
#   powershell ... -File tools\run_probes.ps1 -Agent            # add the Agent/LLM probes (skipped by default)
#   powershell ... -File tools\run_probes.ps1 -Timeout 300
#   powershell ... -File tools\run_probes.ps1 -Godot <exe> -Logs <dir>
#   powershell ... -File tools\run_probes.ps1 -Only SessionProbe -ProbeArgs read
#     (PS 5.1 cannot use the bare '--' separator the bash script accepts; the
#      '--' Godot needs is added for you - write -ProbeArgs read, not -- read)
#
# Verdict rules (same as the bash script / tests\README.md):
#   * exit code 0 = pass, non-zero = fail; 124 is this script's timeout sentinel
#   * a log line containing 'FAIL  ' (FAIL + two spaces) counts as one failed assert
#   * PASS = exit code 0 AND zero failed asserts
#
# Probes share user:// save data, so they are always run SEQUENTIALLY.
# Non-headless probes open real windows and move the real cursor.
# =============================================================================

[CmdletBinding()]
param(
    [switch]$All,
    [switch]$List,
    [switch]$NoAgent,   # 兼容保留（跳过现在是默认行为）
    [switch]$Agent,     # 要跑需真实 Agent 的探针时显式加
    [string[]]$Only = @(),
    [string[]]$Skip = @(),
    [int]$Timeout = 0,
    [string]$Godot = '',
    [string]$Logs = '',
    [string]$Project = '',
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$ProbeArgs = @()
)

# ---------------------------------------------------------------- config -----
# Godot executable (default machine path; override with -Godot or $env:GODOT_EXE).
$DefaultGodot = 'D:/Games/godot/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe'

# NON-HEADLESS probes: they need a real window / real cursor, so they are skipped
# by default and only run with -All (or when named via -Only).
#   DragProbe, SettingsProbe, StatsWindowProbe, EdgeHideBehaviorProbe, EnterProbe
#   WalkProbe: tests/README.md marks it "must NOT be headless" - the walking code
#     gives up when screen size is 0, so a headless run is a false green.
#   ChatBoxShot: chat panel command bar visuals (red quit button, game-mode entry) - no rendering headless.
$NonHeadless = @('DragProbe', 'SettingsProbe', 'StatsWindowProbe', 'EdgeHideBehaviorProbe', 'EnterProbe', 'WalkProbe', 'BubbleShot', 'AnimShot', 'ChatBoxShot')

# Probes that need a live Agent (LLM). Skipped by default (flaky / env-dependent); run with -Agent.
$AgentProbes = @('AcpTest', 'ChatFlowTest', 'CommandE2E', 'HistoryProbe', 'SessionProbe')

$DefaultTimeout = 180
# -----------------------------------------------------------------------------

function Fail-Usage([string]$msg) {
    Write-Host "ERROR: $msg"
    Write-Host 'Try: powershell -NoProfile -File tools\run_probes.ps1 -List'
    exit 2
}

function Fail-Env([string]$msg) {
    Write-Host "ERROR: $msg"
    exit 3
}

# MSYS-style paths (/d/games/...) and backslashes -> native forward-slash path.
function ConvertTo-Native([string]$p) {
    if ([string]::IsNullOrEmpty($p)) { return $p }
    $p = $p -replace '\\', '/'
    if ($p -match '^/([A-Za-z])/(.*)$') { $p = $Matches[1].ToUpper() + ':/' + $Matches[2] }
    return $p
}

function Test-In([string]$needle, [string[]]$hay) {
    foreach ($h in $hay) { if ($h -eq $needle) { return $true } }
    return $false
}

function Get-ProbeNote([string]$name) {
    switch ($name) {
        'AcpTest'      { 'Agent: spawns real backend process' }
        'ChatFlowTest' { 'Agent: lazy connect + real LLM call' }
        'CommandE2E'   { 'Agent: real LLM call' }
        'HistoryProbe' { 'Agent: real session history' }
        'SessionProbe' { 'two passes: set token, then -- read' }
        'DegradeProbe' { 'Agent-unavailable downgrade path' }
        'AudioProbe'   { 'audio device diagnostics (silent)' }
        'TtsProbe'     { 'TTS: synthesizes to WAV, stays silent' }
        'EnvProbe'     { 'reads real Win32 state (fullscreen/idle)' }
        'WindowProbe'  { 'author note prefers a real window' }
        'WalkProbe'    { 'must be non-headless (gives up headless)' }
        default        { '' }
    }
}

# Kill a whole process tree: Godot_*_console.exe is a launcher, so killing it
# alone would leave an orphaned real window behind; /T takes the children too.
function Stop-Tree([int]$ProcId) {
    try { & taskkill.exe /F /T /PID $ProcId 2>$null | Out-Null } catch { }
    try { Stop-Process -Id $ProcId -Force -ErrorAction SilentlyContinue } catch { }
}

# Extra safety net for timeouts: kill any Godot process whose own command line
# mentions this probe scene. Only that scene is touched - never the editor and
# never another project.
function Stop-SceneProcs([string]$Marker) {
    if ([string]::IsNullOrEmpty($Marker)) { return }
    try {
        Get-CimInstance Win32_Process -Filter "Name like '%godot%'" |
            Where-Object { $_.CommandLine -like ('*' + $Marker + '*') } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    } catch { }
}

# Run one probe. Returns a hashtable: rc (124 = timeout) and timedOut.
# Note: Start-Process -PassThru cannot report ExitCode on PS 5.1 (it comes back
# empty), so the process is driven through .NET directly; that also lets us
# stream stdout/stderr into files without a pipe deadlock.
function Invoke-Probe([string]$Name, [bool]$Headless, [string]$LogBase, [string[]]$ExtraArgs) {
    $outFile = "$LogBase.out.log"
    $errFile = "$LogBase.err.log"
    Remove-Item -LiteralPath $outFile, $errFile -ErrorAction SilentlyContinue

    $argList = @()
    if ($Headless) { $argList += '--headless' }
    $argList += @('--path', $ProjectWin, ("res://tests/{0}.tscn" -f $Name))
    if ($ExtraArgs.Count -gt 0) { $argList += $ExtraArgs }

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $Godot
    $psi.Arguments = (($argList | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' ')
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true

    $pr = New-Object System.Diagnostics.Process
    $pr.StartInfo = $psi
    $fsOut = [System.IO.File]::Create($outFile)
    $fsErr = [System.IO.File]::Create($errFile)

    $rc = -1
    $timedOut = $false
    try {
        $null = $pr.Start()
    } catch {
        Write-Host ''
        Write-Host ("           | cannot start: {0}" -f $_.Exception.Message)
        $fsOut.Dispose(); $fsErr.Dispose()
        return @{ rc = -1; timedOut = $false; secs = 0 }
    }

    $tOut = $pr.StandardOutput.BaseStream.CopyToAsync($fsOut)
    $tErr = $pr.StandardError.BaseStream.CopyToAsync($fsErr)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while (-not $pr.HasExited) {
        if ($sw.Elapsed.TotalSeconds -ge $Timeout) { $timedOut = $true; break }
        Start-Sleep -Milliseconds 100
    }
    if ($timedOut) {
        Stop-Tree $pr.Id
        Stop-SceneProcs ("res://tests/{0}.tscn" -f $Name)
        try { $pr.WaitForExit(10000) | Out-Null } catch { }
        $rc = 124
    } else {
        try { if ($pr.HasExited) { $rc = $pr.ExitCode } } catch { $rc = -1 }
    }
    try { $pr.StandardOutput.Close(); $pr.StandardError.Close() } catch { }
    try { $tOut.Wait(5000) | Out-Null; $tErr.Wait(5000) | Out-Null } catch { }
    $fsOut.Dispose(); $fsErr.Dispose()
    $sw.Stop()
    return @{ rc = $rc; timedOut = $timedOut; secs = [int]$sw.Elapsed.TotalSeconds }
}

if ($env:AIPET_PROJECT) { $DefaultProject = $env:AIPET_PROJECT } else { $DefaultProject = '' }
if ([string]::IsNullOrEmpty($Project)) {
    if ([string]::IsNullOrEmpty($DefaultProject)) {
        # default: the parent folder of this script's folder
        $scriptDir = $PSScriptRoot
        if ([string]::IsNullOrEmpty($scriptDir)) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
        if ([string]::IsNullOrEmpty($scriptDir)) { $scriptDir = (Get-Location).Path }
        $Project = Split-Path -Parent $scriptDir
    } else {
        $Project = $DefaultProject
    }
}
if ([string]::IsNullOrEmpty($Godot)) {
    if ($env:GODOT_EXE) { $Godot = $env:GODOT_EXE } else { $Godot = $DefaultGodot }
}
if ($Timeout -le 0) {
    if ($env:PROBE_TIMEOUT) { $Timeout = [int]$env:PROBE_TIMEOUT } else { $Timeout = $DefaultTimeout }
}

$ProjectWin = ConvertTo-Native $Project
$TestsDir = Join-Path $ProjectWin 'tests'

if (-not (Test-Path -LiteralPath $TestsDir)) {
    Fail-Env "probe folder not found: $TestsDir (pass -Project <AIPet root>)"
}

# ---------------------------------------------------------------- probes -----
$AllProbes = @(Get-ChildItem -LiteralPath $TestsDir -Filter '*.tscn' -File |
    Sort-Object Name | ForEach-Object { $_.BaseName })
if ($AllProbes.Count -eq 0) { Fail-Env "no *.tscn probe scenes under $TestsDir" }

$WinCount = 0
foreach ($p in $AllProbes) { if (Test-In $p $NonHeadless) { $WinCount++ } }

foreach ($s in $Skip) { $NonHeadless += $s }

# --- -List: print the inventory, run nothing ---------------------------------
if ($List) {
    Write-Host ("Probe list ({0}, {1} probes)" -f (ConvertTo-Native $TestsDir), $AllProbes.Count)
    Write-Host ''
    Write-Host ('  {0,-22} {1,-13} {2}' -f 'NAME', 'MODE', 'NOTE')
    Write-Host ('  {0,-22} {1,-13} {2}' -f '----', '----', '----')
    foreach ($p in $AllProbes) {
        $mode = 'headless'
        if (Test-In $p $NonHeadless) { $mode = 'non-headless' }
        $note = Get-ProbeNote $p
        if (Test-In $p $AgentProbes) {
            if ($note -ne '') { $note = $note + ' ' } else { $note = '' }
            $note = $note + '[needs Agent]'
        }
        Write-Host ('  {0,-22} {1,-13} {2}' -f $p, $mode, $note)
    }
    Write-Host ''
    Write-Host ("headless {0} / non-headless {1}" -f ($AllProbes.Count - $WinCount), $WinCount)
    Write-Host ("default run (no -All) covers the headless {0}; non-headless skipped: {1}" -f ($AllProbes.Count - $WinCount), ($NonHeadless -join ' '))
    Write-Host ("timeout: {0}s per probe (-Timeout)" -f $Timeout)
    exit 0
}

# --- pick the probes for this run -------------------------------------------
$SelNames = @()
$SelModes = @()
$Skipped = @()
$SkippedNh = @()
$SkippedAgent = @()

if ($Only.Count -gt 0) {
    foreach ($raw in $Only) {
        if ([string]::IsNullOrEmpty($raw)) { continue }
        $want = $raw -replace '\.tscn$', ''
        $name = $null
        foreach ($p in $AllProbes) { if ($p.ToLower() -eq $want.ToLower()) { $name = $p; break } }
        if ($null -eq $name) { Fail-Env "unknown probe '$raw' (use -List to see all names)" }
        if (Test-In $name $SelNames) { continue }
        if (Test-In $name $NonHeadless) { $SelModes += 'window' } else { $SelModes += 'headless' }
        $SelNames += $name
    }
} else {
    foreach ($p in $AllProbes) {
        if ((-not $All) -and (Test-In $p $NonHeadless)) { $Skipped += $p; $SkippedNh += $p; continue }
        if ((-not $Agent) -and (Test-In $p $AgentProbes)) { $Skipped += $p; $SkippedAgent += $p; continue }
        if (Test-In $p $NonHeadless) { $SelModes += 'window' } else { $SelModes += 'headless' }
        $SelNames += $p
    }
}
if ($SelNames.Count -eq 0) { Fail-Env 'nothing to run (all probes were filtered out)' }

if (-not (Test-Path -LiteralPath $Godot)) {
    Fail-Env ("Godot executable not found: {0}`n   -> edit `$DefaultGodot in tools\run_probes.ps1, or pass -Godot <exe> / `$env:GODOT_EXE" -f $Godot)
}

if ([string]::IsNullOrEmpty($Logs)) {
    $Logs = Join-Path ([System.IO.Path]::GetTempPath()) ('aipet_probes\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
try {
    New-Item -ItemType Directory -Force -Path $Logs | Out-Null
} catch {
    Fail-Env "cannot create log folder: $Logs"
}

# Passthrough args: Godot wants the literal '--' before user args (they arrive in
# OS.GetCmdlineUserArgs()). PS 5.1 rejects the bare '--' separator on script files,
# so the separator is added here unless the caller already supplied it.
$Extra = @()
if ($ProbeArgs.Count -gt 0) {
    if ($ProbeArgs[0] -eq '--') { $Extra = $ProbeArgs } else { $Extra = @('--') + $ProbeArgs }
}

Write-Host '=== AIPet probe regression ==='
Write-Host ("project : {0}" -f $ProjectWin)
Write-Host ("godot   : {0}" -f (ConvertTo-Native $Godot))
if ($All) {
    Write-Host ("scope   : all {0} probes (non-headless ones open real windows / move the cursor)" -f $SelNames.Count)
} else {
    Write-Host ("scope   : headless only, {0} probes" -f $SelNames.Count)
}
Write-Host ("timeout : {0}s per probe (timeout counts as failure, whole process tree is killed)" -f $Timeout)
Write-Host ("logs    : {0}" -f (ConvertTo-Native $Logs))
if ($Extra.Count -gt 0) { Write-Host ("passthru: {0}" -f ($Extra -join ' ')) }
if ($SkippedNh.Count -gt 0) {
    Write-Host ("skipped : {0}" -f ($SkippedNh -join ' '))
    Write-Host '          (non-headless: run with -All; a single one with -Only <name>)'
}
if ($SkippedAgent.Count -gt 0) {
    Write-Host ("skipped : {0}" -f ($SkippedAgent -join ' '))
    Write-Host '          (skipped by default: live-LLM probes are flaky; use -Agent to run)'
}
Write-Host ''

# ---------------------------------------------------------------- run --------
$ResNames = @(); $ResModes = @(); $ResResults = @(); $ResFails = @(); $ResExits = @(); $ResSecs = @()
$NPass = 0; $NFail = 0; $NTimeout = 0; $TotalFails = 0; $FailedLogs = @()

for ($i = 0; $i -lt $SelNames.Count; $i++) {
    $name = $SelNames[$i]
    $kind = $SelModes[$i]
    $idx = $i + 1

    if ($kind -eq 'window') { $modeLabel = 'window'; $headless = $false } else { $modeLabel = 'headless'; $headless = $true }
    $logBase = Join-Path $Logs $name

    Write-Host ('[{0,2}/{1,2}] {2,-22} {3,-10} ' -f $idx, $SelNames.Count, $name, "($modeLabel)") -NoNewline
    $r = Invoke-Probe -Name $name -Headless $headless -LogBase $logBase -ExtraArgs $Extra
    $rc = $r.rc
    $secs = $r.secs

    # Failed asserts: 'FAIL  ' (FAIL + two spaces) in stdout or stderr.
    $fails = 0
    foreach ($f in @("$logBase.out.log", "$logBase.err.log")) {
        if (Test-Path -LiteralPath $f) {
            $txt = Get-Content -LiteralPath $f -Raw -ErrorAction SilentlyContinue
            if ($txt) { $fails += ([regex]::Matches($txt, 'FAIL  ')).Count }
        }
    }

    if ($rc -eq 124) {
        $result = 'TIMEOUT'; $NTimeout++; $NFail++; $FailedLogs += $logBase
    } elseif (($rc -eq 0) -and ($fails -eq 0)) {
        $result = 'PASS'; $NPass++
    } else {
        $result = 'FAIL'; $NFail++; $FailedLogs += $logBase
    }
    $TotalFails += $fails

    Write-Host ('{0}  {1,4}s  exit={2,-3} FAIL={3}' -f $result, $secs, $rc, $fails)

    if ($result -ne 'PASS') {
        $failLines = @()
        foreach ($f in @("$logBase.out.log", "$logBase.err.log")) {
            if (Test-Path -LiteralPath $f) {
                $failLines += @(Select-String -LiteralPath $f -Pattern 'FAIL  ' -ErrorAction SilentlyContinue |
                    Select-Object -First 12 | ForEach-Object { $_.Line })
            }
        }
        if ($failLines.Count -gt 0) {
            foreach ($l in ($failLines | Select-Object -First 12)) { Write-Host ("           | {0}" -f $l) }
            if ($fails -gt 12) { Write-Host ("           | ... {0} more, see the log" -f ($fails - 12)) }
        } else {
            Write-Host '           | tail of log (no FAIL line, look here):'
            if (Test-Path -LiteralPath "$logBase.out.log") {
                @(Get-Content -LiteralPath "$logBase.out.log" -ErrorAction SilentlyContinue | Select-Object -Last 8) |
                    ForEach-Object { Write-Host ("           | {0}" -f $_) }
            }
        }
    }

    $ResNames += $name; $ResModes += $modeLabel; $ResResults += $result
    $ResFails += $fails; $ResExits += $rc; $ResSecs += $secs
}

# ---------------------------------------------------------------- summary ----
Write-Host ''
Write-Host '=== summary ==='
Write-Host ('{0,-22} {1,-10} {2,-8} {3,5} {4,5} {5,6}' -f 'PROBE', 'MODE', 'RESULT', 'FAIL', 'EXIT', 'TIME')
Write-Host ('{0,-22} {1,-10} {2,-8} {3,5} {4,5} {5,6}' -f '----------------------', '----------', '--------', '-----', '-----', '------')
for ($i = 0; $i -lt $ResNames.Count; $i++) {
    Write-Host ('{0,-22} {1,-10} {2,-8} {3,5} {4,5} {5,5}s' -f `
        $ResNames[$i], $ResModes[$i], $ResResults[$i], $ResFails[$i], $ResExits[$i], $ResSecs[$i])
}

Write-Host ''
Write-Host ("probes {0}: pass {1} / fail {2} (timeouts {3}); failed asserts total {4}" -f `
    $ResNames.Count, $NPass, $NFail, $NTimeout, $TotalFails)
if ($Skipped.Count -gt 0) {
    Write-Host ("not run (skipped) {0}: {1}" -f $Skipped.Count, ($Skipped -join ' '))
}
if ($FailedLogs.Count -gt 0) {
    Write-Host 'failed logs:'
    foreach ($l in $FailedLogs) { Write-Host ("  - {0}.out.log" -f (ConvertTo-Native $l)) }
}
if ($NFail -eq 0) {
    Write-Host 'result: ALL PASS'
    exit 0
} else {
    Write-Host 'result: FAILURES PRESENT'
    exit 1
}
