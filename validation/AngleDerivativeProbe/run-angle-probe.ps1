#requires -Version 5.1
<#
.SYNOPSIS
  Self-asserting ANGLE D3D11 derivative-shape probe (issue #136). Exits non-zero on failure.

.DESCRIPTION
  Renders probe.html's three fragment control-flow shapes in a headless Chromium browser
  forced onto ANGLE's Direct3D11 backend (what WebGL uses in every Windows browser) and
  asserts derivative liveness:

    A (control, top-level derivative)          -> red=255 required
    B (PRE-fix one-shot for-loop shape)        -> red=0 expected (the issue-#136 poisoning;
                                                  a non-zero B means this ANGLE build no
                                                  longer exhibits the bug - WARN, not fail)
    C (POST-fix unwrapped shape, what ShadowDusk emits) -> red=255 required

  The RENDERER line must name Direct3D11; a SwiftShader/WARP fallback makes the probe
  meaningless and FAILS the gate so it cannot go silently vacuous.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$probeDir  = $PSScriptRoot
$probeHtml = Join-Path $probeDir 'probe.html'
if (-not (Test-Path $probeHtml)) { Write-Host "probe.html not found at $probeHtml" -ForegroundColor Red; exit 1 }

$browsers = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $browsers) {
    Write-Host "No Edge/Chrome found - the ANGLE probe needs a Chromium browser." -ForegroundColor Red
    exit 1
}

$fileUrl = 'file:///' + ($probeHtml -replace '\\', '/')
Write-Host "[angle-probe] browser: $browsers"
# A dedicated --user-data-dir defeats the Chromium launcher handoff (with a shared profile
# an already-running interactive Edge would absorb the launch and emit nothing). The
# pipeline through ForEach-Object is load-bearing under Windows PowerShell 5.1: plain
# assignment capture stops reading stdout when the short-lived LAUNCHER process exits,
# before the real browser child writes the DOM, and yields an empty result; a pipeline
# reads to end-of-stream. (PowerShell 7 is unaffected, but the render-gate script invokes
# this file with powershell.exe.)
$udd = Join-Path $env:TEMP 'shadowdusk-angle-probe'
# Windows PowerShell 5.1 escalates ANY native-command stderr line into a terminating
# NativeCommandError under $ErrorActionPreference = 'Stop', even when redirected to $null
# (the escalation happens before the redirect is applied). Chromium can emit incidental,
# non-fatal stderr logging (e.g. a benign task-manager warning) on an otherwise-successful
# run, so this call must run under 'Continue' - the probe's own pass/fail assertions below
# (renderer + A/B/C values), not $ErrorActionPreference, are what decide the gate.
$previousEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $dom = & $browsers --headless=new --use-angle=d3d11 --no-sandbox --disable-gpu-sandbox `
        --no-first-run "--user-data-dir=$udd" `
        --virtual-time-budget=8000 --timeout=20000 --dump-dom $fileUrl 2>$null |
        ForEach-Object { $_ }
} finally {
    $ErrorActionPreference = $previousEap
}

function Get-ProbeValue([string[]]$Lines, [string]$Prefix) {
    $line = $Lines | Where-Object { $_ -match [regex]::Escape($Prefix) } | Select-Object -First 1
    if ($line -and $line -match "$([regex]::Escape($Prefix)):\s*red=(\d+)") { return [int]$Matches[1] }
    return $null
}

$lines = $dom -split "`n"
$rendererLine = $lines | Where-Object { $_ -match 'RENDERER:' } | Select-Object -First 1
$a = Get-ProbeValue $lines 'A-control-toplevel'
$b = Get-ProbeValue $lines 'B-prefix-oneshot-forloop'
$c = Get-ProbeValue $lines 'C-postfix-unwrapped'

Write-Host "[angle-probe] $($rendererLine -replace '<[^>]+>','' -replace '^\s+','')"
Write-Host "[angle-probe] A(control)=$a  B(pre-fix loop)=$b  C(post-fix unwrapped)=$c"

$failed = $false
if (-not $rendererLine -or $rendererLine -notmatch 'Direct3D11') {
    Write-Host "[angle-probe] FAIL: renderer is not ANGLE Direct3D11 (SwiftShader fallback?) - probe is not measuring the browser backend the bug lives on." -ForegroundColor Red
    $failed = $true
}
if ($a -ne 255) { Write-Host "[angle-probe] FAIL: control shape read $a (expected 255) - derivatives broken at top level, environment problem." -ForegroundColor Red; $failed = $true }
if ($c -ne 255) { Write-Host "[angle-probe] FAIL: ShadowDusk's unwrapped shape read $c (expected 255) - the emitted control-flow shape LOSES derivatives on ANGLE D3D11 (issue #136 class)." -ForegroundColor Red; $failed = $true }
if (-not $failed -and $b -ne 0) {
    Write-Host "[angle-probe] WARN: pre-fix loop shape read $b (expected 0) - this ANGLE build no longer exhibits the gradient poisoning. Gate still valid (C is the assertion)." -ForegroundColor Yellow
}

if ($failed) { exit 1 }
Write-Host "[angle-probe] PASS - ShadowDusk's fragment shapes keep derivatives alive on real ANGLE Direct3D11." -ForegroundColor Green
exit 0
